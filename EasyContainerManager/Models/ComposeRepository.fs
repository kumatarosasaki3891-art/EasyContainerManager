namespace EasyContainerManager.Models

open System
open System.IO
open Microsoft.Data.Sqlite

/// <summary>Compose プロジェクトの SQLite 永続化およびデータアクセスを提供するリポジトリ。</summary>
[<AbstractClass; Sealed>]
type ComposeRepository private () =

    static let mutable customDatabasePath: string option = None

    /// <summary>テスト用などのカスタムデータベースパスを取得または設定する。</summary>
    static member CustomDatabasePath
        with get () = customDatabasePath
        and set value = customDatabasePath <- value

    /// <summary>データベースファイルが配置されるディレクトリパスを取得する。</summary>
    static member DatabaseDirectory =
        if OperatingSystem.IsWindows() then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyContainerManager")
        else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "EasyContainerManager")

    /// <summary>Compose プロジェクト保存用 SQLite データベースの完全ファイルパスを取得する。</summary>
    static member DatabasePath =
        match customDatabasePath with
        | Some path -> path
        | None -> Path.Combine(ComposeRepository.DatabaseDirectory, "compose_projects.db")

    /// <summary>SQLite 接続文字列を取得する。</summary>
    static member ConnectionString =
        sprintf "Data Source=%s" ComposeRepository.DatabasePath

    /// <summary>データベースおよび必要なテーブルを初期化する。</summary>
    static member Initialize() =
        try
            let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
            if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            let sql = """
                CREATE TABLE IF NOT EXISTS ComposeProjects (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    FolderPath TEXT NOT NULL,
                    BrowserUrl TEXT,
                    ConnectionId TEXT NOT NULL DEFAULT 'local',
                    Engine TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ComposeProjects_Scope ON ComposeProjects(ConnectionId, Engine);
            """
            use command = new SqliteCommand(sql, connection)
            command.ExecuteNonQuery() |> ignore
        with _ -> ()

    /// <summary>データベースからすべての Compose プロジェクトを読み込む。</summary>
    static member GetAll() : ComposeProject list =
        try
            ComposeRepository.Initialize()
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            let sql = "SELECT Id, Name, FolderPath, BrowserUrl, ConnectionId, Engine FROM ComposeProjects ORDER BY CreatedAt ASC;"
            use command = new SqliteCommand(sql, connection)
            use reader = command.ExecuteReader()
            let projects = ResizeArray<ComposeProject>()
            while reader.Read() do
                let project = ComposeProject()
                if not (reader.IsDBNull(0)) then project.Id <- reader.GetString(0)
                if not (reader.IsDBNull(1)) then project.Name <- reader.GetString(1)
                if not (reader.IsDBNull(2)) then project.FolderPath <- reader.GetString(2)
                if not (reader.IsDBNull(3)) then project.BrowserUrl <- reader.GetString(3)
                if not (reader.IsDBNull(4)) then project.ConnectionId <- reader.GetString(4)
                if not (reader.IsDBNull(5)) then project.Engine <- reader.GetString(5)
                projects.Add(project)
            projects |> Seq.toList
        with _ -> []

    /// <summary>指定された接続先およびエンジンに合致する Compose プロジェクトを大文字小文字を無視して読み込む。</summary>
    static member GetByScope(connectionId: string, engine: string) : ComposeProject list =
        try
            let all = ComposeRepository.GetAll()
            all
            |> List.filter (fun project ->
                String.Equals(project.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)
                && (not (String.Equals(connectionId, "local", StringComparison.OrdinalIgnoreCase))
                    || String.IsNullOrWhiteSpace(project.Engine)
                    || String.Equals(project.Engine, engine, StringComparison.OrdinalIgnoreCase)))
        with _ -> []

    /// <summary>Compose プロジェクトを SQLite データベースに保存（新規挿入または更新）する。</summary>
    static member Save(project: ComposeProject) =
        try
            if isNull project then ()
            else
                ComposeRepository.Initialize()
                use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                connection.Open()

                if String.IsNullOrWhiteSpace(project.Id) then
                    project.Id <- Guid.NewGuid().ToString()

                // 同一の FolderPath と ConnectionId を持つ既存レコードがあるか確認
                let checkSql = "SELECT Id FROM ComposeProjects WHERE FolderPath = @FolderPath AND ConnectionId = @ConnectionId COLLATE NOCASE LIMIT 1;"
                use checkCmd = new SqliteCommand(checkSql, connection)
                checkCmd.Parameters.AddWithValue("@FolderPath", defaultArg (Option.ofObj project.FolderPath) "") |> ignore
                checkCmd.Parameters.AddWithValue("@ConnectionId", defaultArg (Option.ofObj project.ConnectionId) "local") |> ignore
                let existingIdObj = checkCmd.ExecuteScalar()

                let existingId = if isNull existingIdObj then null else existingIdObj.ToString()
                let targetId = if not (String.IsNullOrWhiteSpace(existingId)) then existingId else project.Id
                project.Id <- targetId

                let sql = """
                    INSERT INTO ComposeProjects (Id, Name, FolderPath, BrowserUrl, ConnectionId, Engine, CreatedAt)
                    VALUES (@Id, @Name, @FolderPath, @BrowserUrl, @ConnectionId, @Engine, @CreatedAt)
                    ON CONFLICT(Id) DO UPDATE SET
                        Name = excluded.Name,
                        FolderPath = excluded.FolderPath,
                        BrowserUrl = excluded.BrowserUrl,
                        ConnectionId = excluded.ConnectionId,
                        Engine = excluded.Engine;
                """
                use command = new SqliteCommand(sql, connection)
                command.Parameters.AddWithValue("@Id", targetId) |> ignore
                command.Parameters.AddWithValue("@Name", defaultArg (Option.ofObj project.Name) "") |> ignore
                command.Parameters.AddWithValue("@FolderPath", defaultArg (Option.ofObj project.FolderPath) "") |> ignore
                command.Parameters.AddWithValue("@BrowserUrl", defaultArg (Option.ofObj project.BrowserUrl) "") |> ignore
                command.Parameters.AddWithValue("@ConnectionId", defaultArg (Option.ofObj project.ConnectionId) "local") |> ignore
                command.Parameters.AddWithValue("@Engine", defaultArg (Option.ofObj project.Engine) "") |> ignore
                command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o")) |> ignore
                command.ExecuteNonQuery() |> ignore
        with _ -> ()

    /// <summary>指定された Compose プロジェクトを SQLite データベースから削除する。</summary>
    static member Delete(project: ComposeProject) =
        try
            if isNull project then ()
            else
                ComposeRepository.Initialize()
                use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                connection.Open()
                let sql = "DELETE FROM ComposeProjects WHERE Id = @Id OR (FolderPath = @FolderPath AND ConnectionId = @ConnectionId COLLATE NOCASE);"
                use command = new SqliteCommand(sql, connection)
                command.Parameters.AddWithValue("@Id", defaultArg (Option.ofObj project.Id) "") |> ignore
                command.Parameters.AddWithValue("@FolderPath", defaultArg (Option.ofObj project.FolderPath) "") |> ignore
                command.Parameters.AddWithValue("@ConnectionId", defaultArg (Option.ofObj project.ConnectionId) "local") |> ignore
                command.ExecuteNonQuery() |> ignore
        with _ -> ()

    /// <summary>指定された接続先IDに紐づくすべての Compose プロジェクトを SQLite データベースから削除する。</summary>
    static member DeleteByConnectionId(connectionId: string) =
        try
            if not (String.IsNullOrWhiteSpace(connectionId)) then
                ComposeRepository.Initialize()
                use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                connection.Open()
                let sql = "DELETE FROM ComposeProjects WHERE ConnectionId = @ConnectionId COLLATE NOCASE;"
                use command = new SqliteCommand(sql, connection)
                command.Parameters.AddWithValue("@ConnectionId", connectionId) |> ignore
                command.ExecuteNonQuery() |> ignore
        with _ -> ()

    /// <summary>古い設定ファイル等のコレクションから未保存のプロジェクトを SQLite に一括移行する。</summary>
    static member MigrateFrom(projects: seq<ComposeProject>) =
        try
            if not (isNull projects) then
                for p in projects do
                    ComposeRepository.Save(p)
        with _ -> ()
