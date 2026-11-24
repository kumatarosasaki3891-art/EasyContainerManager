namespace EasyContainerManager.Models

open System
open System.IO
open System.Net.Http
open System.Text.Json
open Microsoft.Data.Sqlite

/// <summary>定義データのメタ情報レコード（取得日時・鮮度表示用）。</summary>
type MasterMetaRecord = {
    Key: string
    SchemaVersion: int
    UpdatedAt: string
    FetchedAt: string
}

/// <summary>コンテナエンジンの定義レコード。</summary>
type MasterEngineRecord = {
    Id: string
    DisplayName: string
    Latest: string
    Releases: string list
}

/// <summary>ランタイムのブランチ別定義レコード。</summary>
type RuntimeBranchRecord = {
    Branch: string
    Latest: string
    Eol: string option
    Support: bool
}

/// <summary>影響バージョン範囲（from 以上・toExclusive 未満）。</summary>
type AffectedRange = {
    From: string
    ToExclusive: string
}

/// <summary>手作業で精選された緊急 advisory レコード。</summary>
type MasterAdvisoryRecord = {
    RuntimeId: string
    Id: string
    RelatedIds: string list
    Severity: string
    Summary: string
    AffectedRanges: AffectedRange list
    FixedIn: string list
    Requires: string option
    Source: string option
    PublishedAt: string option
}

/// <summary>ランタイム全体の定義レコード。</summary>
type MasterRuntimeRecord = {
    Id: string
    DisplayName: string
    Branches: RuntimeBranchRecord list
    Advisories: MasterAdvisoryRecord list
}

/// <summary>各アプリの最新バージョン定義レコード。</summary>
type MasterAppRecord = {
    Id: string
    DisplayName: string
    Latest: string
    StoreUrl: string option
}

/// <summary>SQLite から読み出したマスターデータ一式のスナップショット。</summary>
type MasterDataSnapshot = {
    Meta: Map<string, MasterMetaRecord>
    Engines: MasterEngineRecord list
    Runtimes: MasterRuntimeRecord list
    Apps: MasterAppRecord list
}

/// <summary>マスターデータ JSON の取得・検証・SQLite キャッシュ管理を行うリポジトリ。</summary>
[<AbstractClass; Sealed>]
type MasterDataRepository private () =

    static let supportedSchemaVersion = 1
    static let enginesMetaKey = "penguin_engines"
    static let runtimesMetaKey = "penguin_runtimes"
    static let appsMetaKey = "apps"

    static let enginesUrl = "https://raw.githubusercontent.com/kumatarosasaki3891-art/kuma-master-data/main/penguin/engines.json"
    static let runtimesUrl = "https://raw.githubusercontent.com/kumatarosasaki3891-art/kuma-master-data/main/penguin/runtimes.json"
    static let appsUrl = "https://raw.githubusercontent.com/kumatarosasaki3891-art/kuma-master-data/main/apps.json"

    static let httpClient =
        let client = new HttpClient(Timeout = TimeSpan.FromSeconds(10.0))
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyContainerManager/1.2")
        client

    // 初回起動・オフライン時用バンドルスナップショット（ビルド時デフォルト）
    static let bundledEnginesJson = """{
  "schemaVersion": 1,
  "updatedAt": "2026-11-19T00:00:00Z",
  "engines": [
    {
      "id": "docker",
      "displayName": "Docker Engine",
      "latest": "28.5.1",
      "releases": ["28.5.1", "28.5.0", "27.5.1"],
      "eol": null,
      "source": "https://api.github.com/repos/moby/moby/releases"
    },
    {
      "id": "podman",
      "displayName": "Podman",
      "latest": "5.4.2",
      "releases": ["5.4.2", "5.4.1", "5.3.2"],
      "eol": null,
      "source": "https://api.github.com/repos/containers/podman/releases"
    },
    {
      "id": "nerdctl",
      "displayName": "nerdctl",
      "latest": "2.1.0",
      "releases": ["2.1.0", "2.0.3"],
      "eol": null,
      "source": "https://api.github.com/repos/containerd/nerdctl/releases"
    }
  ]
}"""

    static let bundledRuntimesJson = """{
  "schemaVersion": 1,
  "updatedAt": "2026-11-19T00:00:00Z",
  "runtimes": [
    {
      "id": "nginx",
      "displayName": "nginx",
      "branches": [
        { "branch": "1.31", "latest": "1.31.9", "eol": null, "support": true },
        { "branch": "1.30", "latest": "1.30.7", "eol": null, "support": true },
        { "branch": "1.28", "latest": "1.28.0", "eol": "2026-04-08", "support": false },
        { "branch": "1.26", "latest": "1.26.3", "eol": "2025-04-23", "support": false }
      ],
      "advisories": [
        {
          "id": "CVE-2026-123007",
          "relatedIds": ["CVE-2026-123633"],
          "severity": "critical",
          "summary": "HTTP/2 有効時に、未認証でリモートコード実行されうる",
          "affectedRanges": [
            { "from": "1.9.5", "toExclusive": "1.30.7" },
            { "from": "1.31.0", "toExclusive": "1.31.9" }
          ],
          "fixedIn": ["1.30.7", "1.31.9"],
          "requires": "http2",
          "source": "https://nginx.org/en/security_advisories.html",
          "publishedAt": "2026-11-18"
        }
      ]
    },
    {
      "id": "php",
      "displayName": "PHP",
      "branches": [
        { "branch": "8.4", "latest": "8.4.5", "eol": "2028-12-31", "support": true },
        { "branch": "8.3", "latest": "8.3.19", "eol": "2027-12-31", "support": true },
        { "branch": "8.2", "latest": "8.2.28", "eol": "2026-12-31", "support": true },
        { "branch": "8.1", "latest": "8.1.31", "eol": "2025-12-31", "support": false },
        { "branch": "8.0", "latest": "8.0.30", "eol": "2023-11-26", "support": false }
      ]
    },
    {
      "id": "mysql",
      "displayName": "MySQL",
      "branches": [
        { "branch": "8.4", "latest": "8.4.4", "eol": "2032-04-30", "support": true },
        { "branch": "8.0", "latest": "8.0.41", "eol": "2026-04-30", "support": false },
        { "branch": "5.7", "latest": "5.7.44", "eol": "2023-10-31", "support": false }
      ]
    },
    {
      "id": "mariadb",
      "displayName": "MariaDB",
      "branches": [
        { "branch": "11.4", "latest": "11.4.5", "eol": "2029-05-29", "support": true },
        { "branch": "10.11", "latest": "10.11.11", "eol": "2028-02-16", "support": true },
        { "branch": "10.6", "latest": "10.6.21", "eol": "2026-07-06", "support": false },
        { "branch": "10.5", "latest": "10.5.28", "eol": "2025-06-24", "support": false }
      ]
    },
    {
      "id": "postgres",
      "displayName": "PostgreSQL",
      "branches": [
        { "branch": "17", "latest": "17.4", "eol": "2029-11-08", "support": true },
        { "branch": "16", "latest": "16.8", "eol": "2028-11-09", "support": true },
        { "branch": "15", "latest": "15.12", "eol": "2027-11-11", "support": true },
        { "branch": "14", "latest": "14.17", "eol": "2026-11-12", "support": false },
        { "branch": "13", "latest": "13.20", "eol": "2025-11-13", "support": false }
      ]
    },
    {
      "id": "redis",
      "displayName": "Redis",
      "branches": [
        { "branch": "7.4", "latest": "7.4.2", "eol": null, "support": true },
        { "branch": "7.2", "latest": "7.2.7", "eol": null, "support": true },
        { "branch": "6.2", "latest": "6.2.17", "eol": "2025-02-28", "support": false }
      ]
    },
    {
      "id": "httpd",
      "displayName": "Apache httpd",
      "branches": [
        { "branch": "2.4", "latest": "2.4.63", "eol": null, "support": true },
        { "branch": "2.2", "latest": "2.2.34", "eol": "2017-07-11", "support": false }
      ]
    }
  ]
}"""

    static let bundledAppsJson = """{
  "schemaVersion": 1,
  "updatedAt": "2026-11-24T00:00:00Z",
  "apps": [
    {
      "id": "simple-docker-manager",
      "displayName": "簡単コンテナ管理",
      "latest": "1.2.0",
      "storeUrl": "https://sites.google.com/view/kuma-applications/"
    },
    {
      "id": "simple-service-diagnosis",
      "displayName": "簡単サービス診断",
      "latest": "1.0.0",
      "storeUrl": "https://sites.google.com/view/kuma-applications/"
    }
  ]
}"""

    static let loadBundledAsset (fileName: string) (fallbackJson: string) =
        try
            let uri = Uri(sprintf "avares://EasyContainerManager/Assets/MasterData/%s" fileName)
            if Avalonia.Platform.AssetLoader.Exists(uri) then
                use stream = Avalonia.Platform.AssetLoader.Open(uri)
                use reader = new StreamReader(stream)
                let text = reader.ReadToEnd()
                if String.IsNullOrWhiteSpace(text) then fallbackJson else text
            else
                fallbackJson
        with _ -> fallbackJson

    static let tryGetProp (name: string) (elem: JsonElement) =
        if elem.ValueKind <> JsonValueKind.Object then None
        else
            let mutable value = Unchecked.defaultof<JsonElement>
            if elem.TryGetProperty(name, &value) then Some value
            else None

    static let tryGetString (name: string) (elem: JsonElement) =
        tryGetProp name elem
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then
                let s = v.GetString()
                if isNull s then None else Some s
            else None)

    static let tryGetBool (name: string) (elem: JsonElement) =
        tryGetProp name elem
        |> Option.bind (fun v ->
            match v.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | _ -> None)

    static let tryGetInt (name: string) (elem: JsonElement) =
        tryGetProp name elem
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.Number then
                let mutable i = 0
                if v.TryGetInt32(&i) then Some i else None
            else None)

    static let getStringList (name: string) (elem: JsonElement) =
        match tryGetProp name elem with
        | Some arr when arr.ValueKind = JsonValueKind.Array ->
            arr.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    let s = item.GetString()
                    if String.IsNullOrWhiteSpace(s) then None else Some s
                else None)
            |> Seq.toList
        | _ -> []

    static let parseStringListJson (json: string) =
        if String.IsNullOrWhiteSpace(json) then []
        else
            try
                use doc = JsonDocument.Parse(json)
                if doc.RootElement.ValueKind = JsonValueKind.Array then
                    doc.RootElement.EnumerateArray()
                    |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then Option.ofObj (x.GetString()) else None)
                    |> Seq.toList
                else []
            with _ -> []

    static let parseRangesJson (json: string) =
        if String.IsNullOrWhiteSpace(json) then []
        else
            try
                use doc = JsonDocument.Parse(json)
                if doc.RootElement.ValueKind = JsonValueKind.Array then
                    doc.RootElement.EnumerateArray()
                    |> Seq.choose (fun x ->
                        match tryGetString "from" x, tryGetString "toExclusive" x with
                        | Some f, Some t -> Some { From = f; ToExclusive = t }
                        | _ -> None)
                    |> Seq.toList
                else []
            with _ -> []

    static let runtimeDisplayNameForId = function
        | "nginx" -> "nginx"
        | "php" -> "PHP"
        | "mysql" -> "MySQL"
        | "mariadb" -> "MariaDB"
        | "postgres" -> "PostgreSQL"
        | "redis" -> "Redis"
        | "httpd" -> "Apache httpd"
        | "node" -> "Node.js"
        | "python" -> "Python"
        | other -> other

    static let ensureTables (connection: SqliteConnection) =
        let sql = """
            CREATE TABLE IF NOT EXISTS master_meta (
                key TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                fetched_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS master_engines (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                latest TEXT NOT NULL,
                releases TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS master_runtimes (
                id TEXT NOT NULL,
                branch TEXT NOT NULL,
                latest TEXT NOT NULL,
                eol TEXT,
                support INTEGER NOT NULL,
                PRIMARY KEY (id, branch)
            );
            CREATE TABLE IF NOT EXISTS master_advisories (
                runtime_id TEXT NOT NULL,
                id TEXT NOT NULL,
                related_ids TEXT,
                severity TEXT NOT NULL,
                summary TEXT NOT NULL,
                affected_ranges TEXT NOT NULL,
                fixed_in TEXT NOT NULL,
                requires TEXT,
                source TEXT,
                published_at TEXT,
                PRIMARY KEY (runtime_id, id)
            );
            CREATE TABLE IF NOT EXISTS master_apps (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                latest TEXT NOT NULL,
                store_url TEXT
            );
        """
        use cmd = new SqliteCommand(sql, connection)
        cmd.ExecuteNonQuery() |> ignore

    static let upsertMeta (connection: SqliteConnection) (tx: SqliteTransaction) (key: string) (schemaVersion: int) (updatedAt: string) (fetchedAt: string) =
        let sql = """
            INSERT INTO master_meta (key, schema_version, updated_at, fetched_at)
            VALUES (@key, @schemaVersion, @updatedAt, @fetchedAt)
            ON CONFLICT(key) DO UPDATE SET
                schema_version = excluded.schema_version,
                updated_at = excluded.updated_at,
                fetched_at = excluded.fetched_at;
        """
        use cmd = new SqliteCommand(sql, connection, tx)
        cmd.Parameters.AddWithValue("@key", key) |> ignore
        cmd.Parameters.AddWithValue("@schemaVersion", schemaVersion) |> ignore
        cmd.Parameters.AddWithValue("@updatedAt", updatedAt) |> ignore
        cmd.Parameters.AddWithValue("@fetchedAt", fetchedAt) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    static let hasMetaKey (connection: SqliteConnection) (key: string) =
        use cmd = new SqliteCommand("SELECT COUNT(*) FROM master_meta WHERE key = @key;", connection)
        cmd.Parameters.AddWithValue("@key", key) |> ignore
        let countObj = cmd.ExecuteScalar()
        match countObj with
        | :? int64 as n -> n > 0L
        | :? int32 as n -> n > 0
        | _ -> false

    /// <summary>定義データ用 GitHub Raw URL 定数。</summary>
    static member EnginesUrl = enginesUrl
    static member RuntimesUrl = runtimesUrl
    static member AppsUrl = appsUrl

    /// <summary>SQLite テーブルを作成し、未登録の定義があればバンドル済み初期スナップショットを投入する。</summary>
    static member Initialize() =
        try
            let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
            if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            ensureTables connection

            if not (hasMetaKey connection enginesMetaKey) then
                let json = loadBundledAsset "engines.json" bundledEnginesJson
                MasterDataRepository.ImportEnginesJson(json, "") |> ignore
            if not (hasMetaKey connection runtimesMetaKey) then
                let json = loadBundledAsset "runtimes.json" bundledRuntimesJson
                MasterDataRepository.ImportRuntimesJson(json, "") |> ignore
            if not (hasMetaKey connection appsMetaKey) then
                let json = loadBundledAsset "apps.json" bundledAppsJson
                MasterDataRepository.ImportAppsJson(json, "") |> ignore
        with _ -> ()

    /// <summary>engines.json を検証し、トランザクション内で全置き換えする。</summary>
    static member ImportEnginesJson(json: string, fetchedAt: string) : bool =
        if String.IsNullOrWhiteSpace(json) then false
        else
            try
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                match tryGetInt "schemaVersion" root, tryGetString "updatedAt" root, tryGetProp "engines" root with
                | Some sv, Some updatedAt, Some enginesElem when sv = supportedSchemaVersion && not (String.IsNullOrWhiteSpace(updatedAt)) && enginesElem.ValueKind = JsonValueKind.Array ->
                    let records =
                        enginesElem.EnumerateArray()
                        |> Seq.choose (fun item ->
                            match tryGetString "id" item, tryGetString "displayName" item, tryGetString "latest" item with
                            | Some id, Some displayName, Some latest when not (String.IsNullOrWhiteSpace(id)) && not (String.IsNullOrWhiteSpace(latest)) ->
                                let releases = getStringList "releases" item
                                Some {
                                    Id = id
                                    DisplayName = displayName
                                    Latest = latest
                                    Releases = releases
                                }
                            | _ -> None)
                        |> Seq.toList

                    let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
                    if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                        Directory.CreateDirectory(dir) |> ignore
                    use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                    connection.Open()
                    ensureTables connection
                    use tx = connection.BeginTransaction()
                    try
                        use delCmd = new SqliteCommand("DELETE FROM master_engines;", connection, tx)
                        delCmd.ExecuteNonQuery() |> ignore
                        for r in records do
                            let sql = "INSERT INTO master_engines (id, display_name, latest, releases) VALUES (@id, @displayName, @latest, @releases);"
                            use insCmd = new SqliteCommand(sql, connection, tx)
                            insCmd.Parameters.AddWithValue("@id", r.Id) |> ignore
                            insCmd.Parameters.AddWithValue("@displayName", r.DisplayName) |> ignore
                            insCmd.Parameters.AddWithValue("@latest", r.Latest) |> ignore
                            insCmd.Parameters.AddWithValue("@releases", JsonSerializer.Serialize(r.Releases)) |> ignore
                            insCmd.ExecuteNonQuery() |> ignore
                        upsertMeta connection tx enginesMetaKey sv updatedAt fetchedAt
                        tx.Commit()
                        true
                    with _ ->
                        try tx.Rollback() with _ -> ()
                        false
                | _ -> false
            with _ -> false

    /// <summary>runtimes.json を検証し、トランザクション内で全置き換えする。</summary>
    static member ImportRuntimesJson(json: string, fetchedAt: string) : bool =
        if String.IsNullOrWhiteSpace(json) then false
        else
            try
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                match tryGetInt "schemaVersion" root, tryGetString "updatedAt" root, tryGetProp "runtimes" root with
                | Some sv, Some updatedAt, Some runtimesElem when sv = supportedSchemaVersion && not (String.IsNullOrWhiteSpace(updatedAt)) && runtimesElem.ValueKind = JsonValueKind.Array ->
                    let branchRows = ResizeArray<string * RuntimeBranchRecord>()
                    let advisoryRows = ResizeArray<MasterAdvisoryRecord>()

                    for rt in runtimesElem.EnumerateArray() do
                        match tryGetString "id" rt with
                        | Some rtId when not (String.IsNullOrWhiteSpace(rtId)) ->
                            match tryGetProp "branches" rt with
                            | Some branchesElem when branchesElem.ValueKind = JsonValueKind.Array ->
                                for br in branchesElem.EnumerateArray() do
                                    match tryGetString "branch" br, tryGetString "latest" br with
                                    | Some branchName, Some latest when not (String.IsNullOrWhiteSpace(branchName)) && not (String.IsNullOrWhiteSpace(latest)) ->
                                        let eol = tryGetString "eol" br |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                        let support = tryGetBool "support" br |> Option.defaultValue true
                                        branchRows.Add(rtId, { Branch = branchName; Latest = latest; Eol = eol; Support = support })
                                    | _ -> ()
                            | _ -> ()

                            match tryGetProp "advisories" rt with
                            | Some advsElem when advsElem.ValueKind = JsonValueKind.Array ->
                                for adv in advsElem.EnumerateArray() do
                                    match tryGetString "id" adv, tryGetString "severity" adv, tryGetString "summary" adv with
                                    | Some advId, Some severity, Some summary when not (String.IsNullOrWhiteSpace(advId)) ->
                                        let relatedIds = getStringList "relatedIds" adv
                                        let fixedIn = getStringList "fixedIn" adv
                                        let requires = tryGetString "requires" adv |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                        let source = tryGetString "source" adv |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                        let publishedAt = tryGetString "publishedAt" adv |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                        let ranges =
                                            match tryGetProp "affectedRanges" adv with
                                            | Some rArr when rArr.ValueKind = JsonValueKind.Array ->
                                                rArr.EnumerateArray()
                                                |> Seq.choose (fun rng ->
                                                    match tryGetString "from" rng, tryGetString "toExclusive" rng with
                                                    | Some f, Some t -> Some { From = f; ToExclusive = t }
                                                    | _ -> None)
                                                |> Seq.toList
                                            | _ -> []
                                        advisoryRows.Add({
                                            RuntimeId = rtId
                                            Id = advId
                                            RelatedIds = relatedIds
                                            Severity = severity
                                            Summary = summary
                                            AffectedRanges = ranges
                                            FixedIn = fixedIn
                                            Requires = requires
                                            Source = source
                                            PublishedAt = publishedAt
                                        })
                                    | _ -> ()
                            | _ -> ()
                        | _ -> ()

                    let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
                    if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                        Directory.CreateDirectory(dir) |> ignore
                    use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                    connection.Open()
                    ensureTables connection
                    use tx = connection.BeginTransaction()
                    try
                        use delRt = new SqliteCommand("DELETE FROM master_runtimes;", connection, tx)
                        delRt.ExecuteNonQuery() |> ignore
                        use delAdv = new SqliteCommand("DELETE FROM master_advisories;", connection, tx)
                        delAdv.ExecuteNonQuery() |> ignore

                        for (rtId, br) in branchRows do
                            let sql = "INSERT INTO master_runtimes (id, branch, latest, eol, support) VALUES (@id, @branch, @latest, @eol, @support);"
                            use cmd = new SqliteCommand(sql, connection, tx)
                            cmd.Parameters.AddWithValue("@id", rtId) |> ignore
                            cmd.Parameters.AddWithValue("@branch", br.Branch) |> ignore
                            cmd.Parameters.AddWithValue("@latest", br.Latest) |> ignore
                            cmd.Parameters.AddWithValue("@eol", match br.Eol with Some e -> box e | None -> box DBNull.Value) |> ignore
                            cmd.Parameters.AddWithValue("@support", if br.Support then 1 else 0) |> ignore
                            cmd.ExecuteNonQuery() |> ignore

                        for adv in advisoryRows do
                            let sql = """
                                INSERT INTO master_advisories (runtime_id, id, related_ids, severity, summary, affected_ranges, fixed_in, requires, source, published_at)
                                VALUES (@runtimeId, @id, @relatedIds, @severity, @summary, @affectedRanges, @fixedIn, @requires, @source, @publishedAt);
                            """
                            use cmd = new SqliteCommand(sql, connection, tx)
                            let rangesJson =
                                JsonSerializer.Serialize(
                                    adv.AffectedRanges
                                    |> List.map (fun r -> {| from = r.From; toExclusive = r.ToExclusive |}))
                            cmd.Parameters.AddWithValue("@runtimeId", adv.RuntimeId) |> ignore
                            cmd.Parameters.AddWithValue("@id", adv.Id) |> ignore
                            cmd.Parameters.AddWithValue("@relatedIds", JsonSerializer.Serialize(adv.RelatedIds)) |> ignore
                            cmd.Parameters.AddWithValue("@severity", adv.Severity) |> ignore
                            cmd.Parameters.AddWithValue("@summary", adv.Summary) |> ignore
                            cmd.Parameters.AddWithValue("@affectedRanges", rangesJson) |> ignore
                            cmd.Parameters.AddWithValue("@fixedIn", JsonSerializer.Serialize(adv.FixedIn)) |> ignore
                            cmd.Parameters.AddWithValue("@requires", match adv.Requires with Some v -> box v | None -> box DBNull.Value) |> ignore
                            cmd.Parameters.AddWithValue("@source", match adv.Source with Some v -> box v | None -> box DBNull.Value) |> ignore
                            cmd.Parameters.AddWithValue("@publishedAt", match adv.PublishedAt with Some v -> box v | None -> box DBNull.Value) |> ignore
                            cmd.ExecuteNonQuery() |> ignore

                        upsertMeta connection tx runtimesMetaKey sv updatedAt fetchedAt
                        tx.Commit()
                        true
                    with _ ->
                        try tx.Rollback() with _ -> ()
                        false
                | _ -> false
            with _ -> false

    /// <summary>apps.json を検証し、トランザクション内で全置き換えする。</summary>
    static member ImportAppsJson(json: string, fetchedAt: string) : bool =
        if String.IsNullOrWhiteSpace(json) then false
        else
            try
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                match tryGetInt "schemaVersion" root, tryGetString "updatedAt" root, tryGetProp "apps" root with
                | Some sv, Some updatedAt, Some appsElem when sv = supportedSchemaVersion && not (String.IsNullOrWhiteSpace(updatedAt)) && appsElem.ValueKind = JsonValueKind.Array ->
                    let records =
                        appsElem.EnumerateArray()
                        |> Seq.choose (fun item ->
                            match tryGetString "id" item, tryGetString "displayName" item, tryGetString "latest" item with
                            | Some id, Some displayName, Some latest when not (String.IsNullOrWhiteSpace(id)) && not (String.IsNullOrWhiteSpace(latest)) ->
                                let storeUrl = tryGetString "storeUrl" item |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                Some {
                                    Id = id
                                    DisplayName = displayName
                                    Latest = latest
                                    StoreUrl = storeUrl
                                }
                            | _ -> None)
                        |> Seq.toList

                    let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
                    if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                        Directory.CreateDirectory(dir) |> ignore
                    use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                    connection.Open()
                    ensureTables connection
                    use tx = connection.BeginTransaction()
                    try
                        use delCmd = new SqliteCommand("DELETE FROM master_apps;", connection, tx)
                        delCmd.ExecuteNonQuery() |> ignore
                        for r in records do
                            let sql = "INSERT INTO master_apps (id, display_name, latest, store_url) VALUES (@id, @displayName, @latest, @storeUrl);"
                            use insCmd = new SqliteCommand(sql, connection, tx)
                            insCmd.Parameters.AddWithValue("@id", r.Id) |> ignore
                            insCmd.Parameters.AddWithValue("@displayName", r.DisplayName) |> ignore
                            insCmd.Parameters.AddWithValue("@latest", r.Latest) |> ignore
                            insCmd.Parameters.AddWithValue("@storeUrl", match r.StoreUrl with Some u -> box u | None -> box DBNull.Value) |> ignore
                            insCmd.ExecuteNonQuery() |> ignore
                        upsertMeta connection tx appsMetaKey sv updatedAt fetchedAt
                        tx.Commit()
                        true
                    with _ ->
                        try tx.Rollback() with _ -> ()
                        false
                | _ -> false
            with _ -> false

    /// <summary>SQLite キャッシュから現在のマスターデータスナップショットを取得する。</summary>
    static member GetSnapshot() : MasterDataSnapshot =
        try
            MasterDataRepository.Initialize()
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()

            let metaMap = System.Collections.Generic.Dictionary<string, MasterMetaRecord>()
            use metaCmd = new SqliteCommand("SELECT key, schema_version, updated_at, fetched_at FROM master_meta;", connection)
            use metaReader = metaCmd.ExecuteReader()
            while metaReader.Read() do
                let key = metaReader.GetString(0)
                metaMap.[key] <- {
                    Key = key
                    SchemaVersion = metaReader.GetInt32(1)
                    UpdatedAt = metaReader.GetString(2)
                    FetchedAt = metaReader.GetString(3)
                }

            let engines = ResizeArray<MasterEngineRecord>()
            use engCmd = new SqliteCommand("SELECT id, display_name, latest, releases FROM master_engines;", connection)
            use engReader = engCmd.ExecuteReader()
            while engReader.Read() do
                engines.Add({
                    Id = engReader.GetString(0)
                    DisplayName = engReader.GetString(1)
                    Latest = engReader.GetString(2)
                    Releases = parseStringListJson (engReader.GetString(3))
                })

            let branchesByRt = System.Collections.Generic.Dictionary<string, ResizeArray<RuntimeBranchRecord>>()
            use rtCmd = new SqliteCommand("SELECT id, branch, latest, eol, support FROM master_runtimes;", connection)
            use rtReader = rtCmd.ExecuteReader()
            while rtReader.Read() do
                let rtId = rtReader.GetString(0)
                let list =
                    match branchesByRt.TryGetValue(rtId) with
                    | true, existing -> existing
                    | _ ->
                        let created = ResizeArray<RuntimeBranchRecord>()
                        branchesByRt.[rtId] <- created
                        created
                list.Add({
                    Branch = rtReader.GetString(1)
                    Latest = rtReader.GetString(2)
                    Eol = if rtReader.IsDBNull(3) then None else Some(rtReader.GetString(3))
                    Support = rtReader.GetInt32(4) <> 0
                })

            let advsByRt = System.Collections.Generic.Dictionary<string, ResizeArray<MasterAdvisoryRecord>>()
            use advCmd = new SqliteCommand("SELECT runtime_id, id, related_ids, severity, summary, affected_ranges, fixed_in, requires, source, published_at FROM master_advisories;", connection)
            use advReader = advCmd.ExecuteReader()
            while advReader.Read() do
                let rtId = advReader.GetString(0)
                let list =
                    match advsByRt.TryGetValue(rtId) with
                    | true, existing -> existing
                    | _ ->
                        let created = ResizeArray<MasterAdvisoryRecord>()
                        advsByRt.[rtId] <- created
                        created
                list.Add({
                    RuntimeId = rtId
                    Id = advReader.GetString(1)
                    RelatedIds = if advReader.IsDBNull(2) then [] else parseStringListJson (advReader.GetString(2))
                    Severity = advReader.GetString(3)
                    Summary = advReader.GetString(4)
                    AffectedRanges = parseRangesJson (advReader.GetString(5))
                    FixedIn = parseStringListJson (advReader.GetString(6))
                    Requires = if advReader.IsDBNull(7) then None else Some(advReader.GetString(7))
                    Source = if advReader.IsDBNull(8) then None else Some(advReader.GetString(8))
                    PublishedAt = if advReader.IsDBNull(9) then None else Some(advReader.GetString(9))
                })

            let allRtIds =
                Seq.append branchesByRt.Keys advsByRt.Keys
                |> Seq.distinct
                |> Seq.toList

            let runtimes =
                allRtIds
                |> List.map (fun rtId ->
                    let branches =
                        match branchesByRt.TryGetValue(rtId) with
                        | true, b -> b |> Seq.toList
                        | _ -> []
                    let advs =
                        match advsByRt.TryGetValue(rtId) with
                        | true, a -> a |> Seq.toList
                        | _ -> []
                    {
                        Id = rtId
                        DisplayName = runtimeDisplayNameForId rtId
                        Branches = branches
                        Advisories = advs
                    })

            let apps = ResizeArray<MasterAppRecord>()
            use appCmd = new SqliteCommand("SELECT id, display_name, latest, store_url FROM master_apps;", connection)
            use appReader = appCmd.ExecuteReader()
            while appReader.Read() do
                apps.Add({
                    Id = appReader.GetString(0)
                    DisplayName = appReader.GetString(1)
                    Latest = appReader.GetString(2)
                    StoreUrl = if appReader.IsDBNull(3) then None else Some(appReader.GetString(3))
                })

            {
                Meta = metaMap |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                Engines = engines |> Seq.toList
                Runtimes = runtimes
                Apps = apps |> Seq.toList
            }
        with _ ->
            { Meta = Map.empty; Engines = []; Runtimes = []; Apps = [] }

    /// <summary>前回取得から 24 時間以上経過しているか（または未取得か）を判定する。</summary>
    static member ShouldAutoFetch(nowUtc: DateTimeOffset) : bool =
        let snapshot = MasterDataRepository.GetSnapshot()
        let requiredKeys = [ enginesMetaKey; runtimesMetaKey; appsMetaKey ]
        requiredKeys
        |> List.exists (fun key ->
            match Map.tryFind key snapshot.Meta with
            | None -> true
            | Some meta ->
                if String.IsNullOrWhiteSpace(meta.FetchedAt) then true
                else
                    match DateTimeOffset.TryParse(meta.FetchedAt) with
                    | true, fetchedAt -> (nowUtc - fetchedAt) >= TimeSpan.FromHours(24.0)
                    | _ -> true)

    /// <summary>GitHub Raw URL（またはテスト用フェッチャー）からマスターデータを取得して SQLite を更新する。</summary>
    static member FetchAndImportAsync(?customFetcher: string -> Async<Result<string, string>>, ?nowUtc: DateTimeOffset) : Async<bool> = async {
        MasterDataRepository.Initialize()
        let fetcher =
            match customFetcher with
            | Some fn -> fn
            | None ->
                fun (url: string) -> async {
                    try
                        let! content = httpClient.GetStringAsync(url) |> Async.AwaitTask
                        return Ok content
                    with ex ->
                        return Error ex.Message
                }

        let timestamp = (defaultArg nowUtc DateTimeOffset.UtcNow).ToString("o")
        let mutable updatedAny = false

        try
            let! enginesRes = fetcher enginesUrl
            match enginesRes with
            | Ok json ->
                if MasterDataRepository.ImportEnginesJson(json, timestamp) then
                    updatedAny <- true
            | Error _ -> ()
        with _ -> ()

        try
            let! runtimesRes = fetcher runtimesUrl
            match runtimesRes with
            | Ok json ->
                if MasterDataRepository.ImportRuntimesJson(json, timestamp) then
                    updatedAny <- true
            | Error _ -> ()
        with _ -> ()

        try
            let! appsRes = fetcher appsUrl
            match appsRes with
            | Ok json ->
                if MasterDataRepository.ImportAppsJson(json, timestamp) then
                    updatedAny <- true
            | Error _ -> ()
        with _ -> ()

        return updatedAny
    }

    /// <summary>スナップショットの定義データ更新日付（YYYY-MM-DD）を取得する。</summary>
    static member GetDefinitionDateText(snapshot: MasterDataSnapshot) : string option =
        let candidate =
            [ runtimesMetaKey; enginesMetaKey; appsMetaKey ]
            |> List.tryPick (fun key -> Map.tryFind key snapshot.Meta)
        candidate
        |> Option.bind (fun meta ->
            match DateTimeOffset.TryParse(meta.UpdatedAt) with
            | true, dt -> Some(dt.UtcDateTime.ToString("yyyy-MM-dd"))
            | _ ->
                if not (String.IsNullOrWhiteSpace(meta.UpdatedAt)) && meta.UpdatedAt.Length >= 10 then
                    Some(meta.UpdatedAt.Substring(0, 10))
                else
                    None)

    /// <summary>スナップショットから指定アプリIDのレコードを取得する。</summary>
    static member TryGetApp(appId: string, snapshot: MasterDataSnapshot) : MasterAppRecord option =
        snapshot.Apps
        |> List.tryFind (fun a -> String.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase))
