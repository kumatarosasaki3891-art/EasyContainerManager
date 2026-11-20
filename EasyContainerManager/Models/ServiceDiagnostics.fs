namespace EasyContainerManager.Models

open System
open System.IO
open System.Text.RegularExpressions
open System.Text.Json
open Avalonia.Media
open EasyContainerManager

/// <summary>診断所見の重大度区分。</summary>
type FindingSeverity =
    | Danger
    | Warning
    | Info

module FindingSeverity =
    /// <summary>重大度に対応する絵文字アイコン。</summary>
    let badge = function
        | Danger -> "🔴"
        | Warning -> "🟡"
        | Info -> "🔵"

    /// <summary>重大度に対応する表示名。</summary>
    let label languageCode = function
        | Danger -> Localization.translate languageCode "危険" "Danger"
        | Warning -> Localization.translate languageCode "警告" "Warning"
        | Info -> Localization.translate languageCode "情報" "Info"

/// <summary>診断所見の 1 件を表すレコード。</summary>
type DiagnosticFinding = {
    Severity: FindingSeverity
    TitleKey: string
    SummaryKey: string
    EvidenceLines: string list
    Count: int
}

/// <summary>診断所見 1 件の表示用モデル。</summary>
type FindingDisplayItem(finding: DiagnosticFinding, languageCode: string) =
    member _.SeverityBadge = FindingSeverity.badge finding.Severity
    member _.SeverityText = FindingSeverity.label languageCode finding.Severity
    member _.Title = Localization.translateKey languageCode finding.TitleKey
    member _.Summary = Localization.translateKey languageCode finding.SummaryKey
    member _.CountText =
        let template = Localization.translate languageCode "該当: {0}件" "Occurrences: {0}"
        String.Format(template, finding.Count)
    member _.EvidenceText =
        if List.isEmpty finding.EvidenceLines then ""
        else finding.EvidenceLines |> String.concat Environment.NewLine
    member _.HasEvidence = not (List.isEmpty finding.EvidenceLines)
    member _.EvidenceLabel = Localization.translate languageCode "根拠:" "Evidence:"
    member _.SeverityBrush : IBrush =
        match finding.Severity with
        | FindingSeverity.Danger -> Brushes.Crimson
        | FindingSeverity.Warning -> Brushes.DarkOrange
        | FindingSeverity.Info -> Brushes.DodgerBlue

/// <summary>診断全体の集計結果を表すレコード。</summary>
type DiagnosticsResult = {
    TargetServiceNames: string list
    ScannedLines: int
    Findings: DiagnosticFinding list
}

/// <summary>コンテナ inspect から抽出された状態情報。</summary>
type ContainerInspectState = {
    Status: string
    Running: bool
    Restarting: bool
    OOMKilled: bool
    ExitCode: int
}

/// <summary>コンテナ inspect から抽出された診断用データ。</summary>
type ContainerInspectData = {
    Id: string
    Name: string
    RestartCount: int
    State: ContainerInspectState option
}

/// <summary>コンテナログと状態からサービスの健康状態を診断する純粋モジュール。</summary>
module ServiceDiagnostics =

    let private maxLineLength = 2048
    let private maxEvidencePerFinding = 3

    /// <summary>1 行あたりの上限文字数で切り捨てる。</summary>
    let truncateLine (line: string) =
        if isNull line then ""
        elif line.Length > maxLineLength then line.Substring(0, maxLineLength)
        else line

    /// <summary>inspect JSON 出力文字列から診断用データを解析する。</summary>
    let parseInspectJson (output: string) : ContainerInspectData option =
        if String.IsNullOrWhiteSpace(output) then None
        else
            try
                use doc = JsonDocument.Parse(output)
                let root =
                    if doc.RootElement.ValueKind = JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 then
                        doc.RootElement.[0]
                    else
                        doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then None
                else
                    let tryProp (name: string) (elem: JsonElement) =
                        elem.EnumerateObject()
                        |> Seq.tryFind (fun p -> String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun p -> p.Value)

                    let id =
                        tryProp "Id" root
                        |> Option.map (fun p -> p.GetString())
                        |> Option.defaultValue ""

                    let name =
                        tryProp "Name" root
                        |> Option.map (fun p -> p.GetString().TrimStart('/'))
                        |> Option.defaultValue ""

                    let restartCount =
                        tryProp "RestartCount" root
                        |> Option.bind (fun p ->
                            match p.ValueKind with
                            | JsonValueKind.Number -> Some (p.GetInt32())
                            | JsonValueKind.String ->
                                match Int32.TryParse(p.GetString()) with
                                | true, n -> Some n
                                | _ -> None
                            | _ -> None)
                        |> Option.defaultValue 0

                    let stateOpt =
                        tryProp "State" root
                        |> Option.bind (fun stateElem ->
                            if stateElem.ValueKind <> JsonValueKind.Object then None
                            else
                                let status =
                                    tryProp "Status" stateElem
                                    |> Option.map (fun p -> p.GetString())
                                    |> Option.defaultValue ""
                                let running =
                                    tryProp "Running" stateElem
                                    |> Option.map (fun p -> p.ValueKind = JsonValueKind.True)
                                    |> Option.defaultValue false
                                let restarting =
                                    tryProp "Restarting" stateElem
                                    |> Option.map (fun p -> p.ValueKind = JsonValueKind.True)
                                    |> Option.defaultValue false
                                let oomKilled =
                                    tryProp "OOMKilled" stateElem
                                    |> Option.map (fun p -> p.ValueKind = JsonValueKind.True)
                                    |> Option.defaultValue false
                                let exitCode =
                                    tryProp "ExitCode" stateElem
                                    |> Option.bind (fun p ->
                                        match p.ValueKind with
                                        | JsonValueKind.Number -> Some (p.GetInt32())
                                        | JsonValueKind.String ->
                                            match Int32.TryParse(p.GetString()) with
                                            | true, n -> Some n
                                            | _ -> None
                                        | _ -> None)
                                    |> Option.defaultValue 0
                                Some {
                                    Status = status
                                    Running = running
                                    Restarting = restarting
                                    OOMKilled = oomKilled
                                    ExitCode = exitCode
                                })

                    Some {
                        Id = id
                        Name = name
                        RestartCount = restartCount
                        State = stateOpt
                    }
            with _ -> None

    /// <summary>パターン照合定義。</summary>
    type private PatternRule = {
        Id: string
        Severity: FindingSeverity
        TitleKey: string
        SummaryKey: string
        Matches: string -> bool
    }

    /// <summary>パターンごとの集計アキュムレータ。</summary>
    type private PatternAcc = {
        Rule: PatternRule
        mutable Count: int
        Evidence: ResizeArray<string>
    }

    let private createAcc rule = {
        Rule = rule
        Count = 0
        Evidence = ResizeArray<string>()
    }

    // --- レベル2: サービス別パターン辞書 ---

    // WebServer (Nginx / Apache)
    let private nginxRules = [
        {
            Id = "nginx-upstream-error"
            Severity = FindingSeverity.Warning
            TitleKey = "Upstream接続エラー"
            SummaryKey = "バックエンドサーバーへの接続失敗、タイムアウト、または利用不可が記録されています。"
            Matches = fun line ->
                line.Contains("connect() failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Connection refused while connecting to upstream", StringComparison.OrdinalIgnoreCase)
                || line.Contains("no live upstreams while connecting to upstream", StringComparison.OrdinalIgnoreCase)
                || line.Contains("upstream timed out", StringComparison.OrdinalIgnoreCase)
                || line.Contains("502 Bad Gateway", StringComparison.OrdinalIgnoreCase)
                || line.Contains("504 Gateway Time-out", StringComparison.OrdinalIgnoreCase)
        }
        {
            Id = "nginx-5xx"
            Severity = FindingSeverity.Warning
            TitleKey = "サーバーエラー (5xx)"
            SummaryKey = "HTTP 5xx のサーバー内部エラーまたはゲートウェイエラーがログに記録されています。"
            Matches = fun line ->
                // HTTPステータス 500, 502, 503, 504 のアクセスログ行
                Regex.IsMatch(line, """HTTP/[0-9\.]+"\s+50[0-4]\b""")
                || Regex.IsMatch(line, """"\s+50[0-4]\s+\d+""")
        }
        {
            Id = "nginx-worker-died"
            Severity = FindingSeverity.Danger
            TitleKey = "ワーカープロセスの異常終了"
            SummaryKey = "サーバーのワーカープロセスがシグナル等により異常終了しました。"
            Matches = fun line ->
                Regex.IsMatch(line, """worker process \d+ exited on signal""", RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, """worker process \d+ exited with fatal code""", RegexOptions.IgnoreCase)
                || line.Contains("worker process exited", StringComparison.OrdinalIgnoreCase)
        }
    ]

    // MySQL / MariaDB
    let private mysqlRules = [
        {
            Id = "mysql-too-many-connections"
            Severity = FindingSeverity.Warning
            TitleKey = "接続数上限到達"
            SummaryKey = "クライアントの接続数が上限に達し、新しい接続が拒否されています。"
            Matches = fun line ->
                line.Contains("Too many connections", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ER_CON_COUNT_ERROR", StringComparison.OrdinalIgnoreCase)
        }
        {
            Id = "mysql-innodb-error"
            Severity = FindingSeverity.Danger
            TitleKey = "InnoDBストレージエンジンエラー"
            SummaryKey = "InnoDBのデータ破損、I/Oエラー、またはテーブル破損が記録されています。"
            Matches = fun line ->
                line.Contains("InnoDB: Database page corruption", StringComparison.OrdinalIgnoreCase)
                || line.Contains("InnoDB: Error", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(line, """Table '.*' is marked as crashed""", RegexOptions.IgnoreCase)
                || line.Contains("InnoDB: Operating system error", StringComparison.OrdinalIgnoreCase)
        }
        {
            Id = "mysql-startup-error"
            Severity = FindingSeverity.Danger
            TitleKey = "データベース起動・実行エラー"
            SummaryKey = "データベースの起動失敗または重大な実行時エラーが記録されています。"
            Matches = fun line ->
                line.Contains("[ERROR] [MY-", StringComparison.OrdinalIgnoreCase)
                || line.Contains("mysqld: Can't start server", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Can't open and lock privilege tables", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(line, """Aborted connection \d+ to db:""", RegexOptions.IgnoreCase)
                || line.Contains("[ERROR] Fatal error:", StringComparison.OrdinalIgnoreCase)
        }
    ]

    // PostgreSQL
    let private postgresRules = [
        {
            Id = "postgres-connection-limit"
            Severity = FindingSeverity.Warning
            TitleKey = "接続拒否・接続数上限"
            SummaryKey = "接続スロットの上限到達、または認証失敗が発生しています。"
            Matches = fun line ->
                line.Contains("remaining connection slots are reserved", StringComparison.OrdinalIgnoreCase)
                || line.Contains("sorry, too many clients already", StringComparison.OrdinalIgnoreCase)
                || (line.Contains("FATAL:", StringComparison.OrdinalIgnoreCase) && line.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase))
        }
        {
            Id = "postgres-disk-checkpoint"
            Severity = FindingSeverity.Danger
            TitleKey = "チェックポイント障害・容量不足"
            SummaryKey = "ディスク容量不足またはチェックポイントの破損等の重大障害が記録されています。"
            Matches = fun line ->
                (line.Contains("could not write to file", StringComparison.OrdinalIgnoreCase) && line.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
                || line.Contains("could not locate a valid checkpoint record", StringComparison.OrdinalIgnoreCase)
                || line.Contains("PANIC:", StringComparison.OrdinalIgnoreCase)
        }
        {
            Id = "postgres-process-terminated"
            Severity = FindingSeverity.Danger
            TitleKey = "サーバープロセスの異常終了"
            SummaryKey = "データベースのサーバープロセスがシグナル等により異常終了しました。"
            Matches = fun line ->
                Regex.IsMatch(line, """server process \(PID \d+\) was terminated by signal""", RegexOptions.IgnoreCase)
                || line.Contains("terminating any other active server processes", StringComparison.OrdinalIgnoreCase)
        }
    ]

    // Redis
    let private redisRules = [
        {
            Id = "redis-persistence-error"
            Severity = FindingSeverity.Danger
            TitleKey = "永続化保存エラー (RDB/AOF)"
            SummaryKey = "スナップショット (RDB) または AOF ファイルのディスク保存に失敗しています。"
            Matches = fun line ->
                line.Contains("Failed opening the RDB file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Background saving error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("MISCONF Errors writing to the AOF file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Can't save in background", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Failed to write inside the snapshot file", StringComparison.OrdinalIgnoreCase)
        }
        {
            Id = "redis-oom"
            Severity = FindingSeverity.Warning
            TitleKey = "メモリ上限到達 (OOM)"
            SummaryKey = "設定された最大メモリ上限 (maxmemory) に到達し、書き込みが制限されている可能性があります。"
            Matches = fun line ->
                line.Contains("OOM command not allowed when used memory > 'maxmemory'", StringComparison.OrdinalIgnoreCase)
                || line.Contains("maxmemory has been reached", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Reached used_memory limit", StringComparison.OrdinalIgnoreCase)
        }
    ]

    // --- レベル1: 汎用ログエラーチェック ---
    let private genericRules = [
        {
            Id = "generic-log-error"
            Severity = FindingSeverity.Warning
            TitleKey = "ログ内のエラー・例外"
            SummaryKey = "ログ内にエラーまたは例外を示すメッセージが記録されています。"
            Matches = fun line ->
                // 明確なエラーインジケーター（単なる "without error" や "0 errors" を除外）
                let isTaggedError =
                    line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("level=error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("\"level\":\"error\"", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Error:", StringComparison.OrdinalIgnoreCase)

                let isCritical =
                    Regex.IsMatch(line, """\b(?:FATAL|PANIC|CRITICAL|Exception|Segfault|Segmentation fault)\b""", RegexOptions.IgnoreCase)

                (isTaggedError || isCritical)
                && not (line.Contains("0 errors", StringComparison.OrdinalIgnoreCase))
                && not (line.Contains("no error", StringComparison.OrdinalIgnoreCase))
        }
    ]

    /// <summary>指定された ServiceId 一覧に応じたルール一覧を取得する。</summary>
    let private getRulesForServices (services: ServiceId list) =
        services
        |> List.collect (function
            | ServiceId.WebServer -> nginxRules
            | ServiceId.Mysql -> mysqlRules
            | ServiceId.Postgres -> postgresRules
            | ServiceId.Redis -> redisRules
            | _ -> [])

    /// <summary>inspect データからレベル1チェックを行う。</summary>
    let private checkInspect (inspectOpt: ContainerInspectData option) : DiagnosticFinding list =
        match inspectOpt with
        | None -> []
        | Some inspect ->
            let findings = ResizeArray<DiagnosticFinding>()

            // 1. OOMKilled チェック (Danger)
            match inspect.State with
            | Some st when st.OOMKilled ->
                findings.Add({
                    Severity = FindingSeverity.Danger
                    TitleKey = "メモリ不足による強制終了 (OOMKilled)"
                    SummaryKey = "コンテナがメモリ上限を超過したため、OSにより強制終了されました。"
                    EvidenceLines = [ sprintf "State.OOMKilled: true (ExitCode: %d)" st.ExitCode ]
                    Count = 1
                })
            | _ -> ()

            // 2. 再起動の繰り返しチェック
            let isRestarting =
                inspect.State
                |> Option.exists (fun s -> s.Restarting || s.Status.Equals("restarting", StringComparison.OrdinalIgnoreCase))

            if inspect.RestartCount >= 3 || isRestarting then
                let status = inspect.State |> Option.map (fun s -> s.Status) |> Option.defaultValue "unknown"
                findings.Add({
                    Severity = FindingSeverity.Danger
                    TitleKey = "コンテナ再起動の繰り返し"
                    SummaryKey = "短時間に複数回の再起動が検知されました。起動直後に異常終了している可能性があります。"
                    EvidenceLines = [ sprintf "RestartCount: %d, State.Status: %s" inspect.RestartCount status ]
                    Count = max inspect.RestartCount 1
                })
            elif inspect.RestartCount > 0 then
                let status = inspect.State |> Option.map (fun s -> s.Status) |> Option.defaultValue "unknown"
                findings.Add({
                    Severity = FindingSeverity.Warning
                    TitleKey = "コンテナ再起動を検知"
                    SummaryKey = "コンテナの再起動が記録されています。"
                    EvidenceLines = [ sprintf "RestartCount: %d, State.Status: %s" inspect.RestartCount status ]
                    Count = inspect.RestartCount
                })

            findings |> Seq.toList

    /// <summary>ログ行をストリーミング走査し、所見を集計する（grep/rg 流儀: 1行ずつ読んで数えて捨てる）。</summary>
    let private scanLogs (serviceRules: PatternRule list) (logLines: seq<string>) : int * DiagnosticFinding list =
        let serviceAccs = serviceRules |> List.map createAcc
        let genericAccs = genericRules |> List.map createAcc

        let mutable scannedCount = 0

        for rawLine in logLines do
            scannedCount <- scannedCount + 1
            let line = truncateLine rawLine
            if not (String.IsNullOrWhiteSpace(line)) then
                let mutable matchedService = false

                // サービス固有ルールを優先照合
                for acc in serviceAccs do
                    if acc.Rule.Matches line then
                        matchedService <- true
                        acc.Count <- acc.Count + 1
                        if acc.Evidence.Count < maxEvidencePerFinding then
                            acc.Evidence.Add(line)

                // サービス固有ルールにマッチしなかった場合のみ、汎用エラーチェック
                if not matchedService then
                    for acc in genericAccs do
                        if acc.Rule.Matches line then
                            acc.Count <- acc.Count + 1
                            if acc.Evidence.Count < maxEvidencePerFinding then
                                acc.Evidence.Add(line)

        let logFindings = ResizeArray<DiagnosticFinding>()

        // サービス固有所見の集計
        for acc in serviceAccs do
            if acc.Count > 0 then
                logFindings.Add({
                    Severity = acc.Rule.Severity
                    TitleKey = acc.Rule.TitleKey
                    SummaryKey = acc.Rule.SummaryKey
                    EvidenceLines = acc.Evidence |> Seq.toList
                    Count = acc.Count
                })

        // 汎用所見の集計
        for acc in genericAccs do
            if acc.Count > 0 then
                // 10件以上の集中は Danger に昇格
                let severity =
                    if acc.Count >= 10 then FindingSeverity.Danger
                    else acc.Rule.Severity
                logFindings.Add({
                    Severity = severity
                    TitleKey = acc.Rule.TitleKey
                    SummaryKey = acc.Rule.SummaryKey
                    EvidenceLines = acc.Evidence |> Seq.toList
                    Count = acc.Count
                })

        scannedCount, (logFindings |> Seq.toList)

    /// <summary>ServiceId に対応するサービス表示名を取得する。</summary>
    let serviceDisplayName (serviceId: ServiceId) =
        match serviceId with
        | WebServer -> "Web (Nginx / Apache)"
        | Mysql -> "MySQL / MariaDB"
        | Postgres -> "PostgreSQL"
        | Redis -> "Redis"
        | Mongo -> "MongoDB"
        | Elasticsearch -> "Elasticsearch"
        | Kibana -> "Kibana"
        | Prometheus -> "Prometheus"
        | Ssh -> "SSH"
        | Mail -> "Mail"
        | Samba -> "Samba"
        | Rdp -> "RDP"
        | Ollama -> "Ollama"

    /// <summary>
    /// コンテナの状態（inspect）とログからサービス健全性を診断するメイン関数。
    /// 辞書にない所見は出さない（沈黙の原則）。
    /// </summary>
    let diagnose (inspectData: ContainerInspectData option) (targetServices: ServiceId list) (logLines: seq<string>) : DiagnosticsResult =
        let inspectFindings = checkInspect inspectData
        let serviceRules = getRulesForServices targetServices
        let scannedCount, logFindings = scanLogs serviceRules logLines

        // 重大度順（Danger → Warning → Info）にソート
        let allFindings =
            inspectFindings @ logFindings
            |> List.sortWith (fun a b ->
                let rank = function FindingSeverity.Danger -> 0 | FindingSeverity.Warning -> 1 | FindingSeverity.Info -> 2
                compare (rank a.Severity) (rank b.Severity))

        let targetNames =
            if List.isEmpty targetServices then []
            else targetServices |> List.map serviceDisplayName |> List.distinct

        {
            TargetServiceNames = targetNames
            ScannedLines = scannedCount
            Findings = allFindings
        }
