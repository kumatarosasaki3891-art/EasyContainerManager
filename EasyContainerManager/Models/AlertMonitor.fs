namespace EasyContainerManager.Models

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Avalonia.Media
open Microsoft.Data.Sqlite
open EasyContainerManager

/// <summary>アラートの重大度区分。</summary>
type AlertSeverity =
    | Warning
    | Danger

/// <summary>異常検知アラートの種別。</summary>
type AlertCategory =
    | ContainerAbnormalExit
    | ContainerOomKilled
    | ContainerRestartSurge
    | DiagnosticsDanger
    | VolumeThresholdExceeded
    | HighResourceUsage
    | ComposeAnomaly

/// <summary>定期更新ごとのコンテナ異常差分判定に使用する軽量スナップショット。</summary>
type ContainerMonitorSnapshot = {
    Id: string
    Name: string
    IsRunning: bool
    IsRestarting: bool
    OomKilled: bool
    ExitCode: int
    RestartCount: int
}

/// <summary>SQLite に保存・通知されるアラート履歴 1 件分のレコード。</summary>
type AlertEventEntry = {
    Id: int64
    OccurredAt: string
    Severity: AlertSeverity
    Category: AlertCategory
    TargetId: string
    TargetName: string
    TitleKey: string
    Message: string
}

/// <summary>アラート差分検知・重大度整形・履歴上限管理を行う純粋モジュール。</summary>
module AlertMonitor =

    /// <summary>SQLite に保持する最新アラート件数の上限。</summary>
    let maxAlertHistoryRows = 200

    let private trimOrEmpty (s: string) =
        if isNull s then "" else s.Trim()

    /// <summary>コンテナの状態文字列（例: "Exited (137) 5 seconds ago"）から終了コードを抽出する。</summary>
    let parseExitCodeFromStatus (status: string) : int option =
        if String.IsNullOrWhiteSpace(status) then None
        else
            let m = Regex.Match(status, @"Exited\s*\((-?\d+)\)", RegexOptions.IgnoreCase)
            if m.Success then
                match Int32.TryParse(m.Groups.[1].Value) with
                | true, code -> Some code
                | _ -> None
            else
                None

    /// <summary>ContainerInfo から監視用の軽量スナップショットを生成する。</summary>
    let snapshotFromContainer (c: ContainerInfo) : ContainerMonitorSnapshot =
        let status = trimOrEmpty c.Status
        let inspStatus = trimOrEmpty c.InspectStatus
        let isRestarting =
            status.StartsWith("Restarting", StringComparison.OrdinalIgnoreCase)
            || inspStatus.Equals("restarting", StringComparison.OrdinalIgnoreCase)
        let isRunning =
            not isRestarting
            && (status.StartsWith("Up", StringComparison.OrdinalIgnoreCase)
                || inspStatus.Equals("running", StringComparison.OrdinalIgnoreCase))
        let effectiveExitCode =
            if c.ExitCode <> 0 then c.ExitCode
            else
                match parseExitCodeFromStatus status with
                | Some code -> code
                | None -> c.ExitCode
        {
            Id = trimOrEmpty c.Id
            Name = trimOrEmpty c.Name
            IsRunning = isRunning
            IsRestarting = isRestarting
            OomKilled = c.OomKilled
            ExitCode = effectiveExitCode
            RestartCount = c.RestartCount
        }

    /// <summary>前回スナップショットと今回スナップショットの差分から新規コンテナ異常イベントを抽出する。</summary>
    let detectContainerAnomalies
        (isBaseline: bool)
        (previous: Map<string, ContainerMonitorSnapshot>)
        (current: ContainerMonitorSnapshot list)
        (occurredAtUtc: DateTimeOffset)
        : AlertEventEntry list =
        if isBaseline then []
        else
            let ts = occurredAtUtc.ToUniversalTime().ToString("o")
            let events = ResizeArray<AlertEventEntry>()

            for curr in current do
                let prevOpt = Map.tryFind curr.Id previous
                let mutable emittedOom = false

                // 1. OOMKilled 検出（false -> true、または新規取得時点で OOMKilled=true）
                let prevOom = prevOpt |> Option.map (fun p -> p.OomKilled) |> Option.defaultValue false
                if curr.OomKilled && not prevOom then
                    emittedOom <- true
                    events.Add({
                        Id = 0L
                        OccurredAt = ts
                        Severity = AlertSeverity.Danger
                        Category = AlertCategory.ContainerOomKilled
                        TargetId = curr.Id
                        TargetName = curr.Name
                        TitleKey = "OOMKilled 検出"
                        Message = sprintf "OOMKilled (ExitCode: %d)" curr.ExitCode
                    })

                // 2. コンテナ異常終了（稼働/再起動中から非0 ExitCode で停止へ遷移）
                match prevOpt with
                | Some prev when (prev.IsRunning || prev.IsRestarting)
                                 && not curr.IsRunning
                                 && not curr.IsRestarting
                                 && curr.ExitCode <> 0
                                 && not emittedOom ->
                    events.Add({
                        Id = 0L
                        OccurredAt = ts
                        Severity = AlertSeverity.Danger
                        Category = AlertCategory.ContainerAbnormalExit
                        TargetId = curr.Id
                        TargetName = curr.Name
                        TitleKey = "コンテナ異常終了"
                        Message = sprintf "ExitCode: %d" curr.ExitCode
                    })
                | _ -> ()

                // 3. 再起動回数の急増（+2回以上、または <3 -> >=3 到達、または Restarting 状態へ突入）
                match prevOpt with
                | Some prev ->
                    let delta = curr.RestartCount - prev.RestartCount
                    let surged =
                        delta >= 2
                        || (prev.RestartCount < 3 && curr.RestartCount >= 3)
                        || (not prev.IsRestarting && curr.IsRestarting)
                    if surged then
                        events.Add({
                            Id = 0L
                            OccurredAt = ts
                            Severity = AlertSeverity.Danger
                            Category = AlertCategory.ContainerRestartSurge
                            TargetId = curr.Id
                            TargetName = curr.Name
                            TitleKey = "再起動回数の急増"
                            Message = sprintf "RestartCount: %d → %d" prev.RestartCount curr.RestartCount
                        })
                | None -> ()

            events |> Seq.toList

    /// <summary>ボリューム容量が警告・危険しきい値を超えた場合（レベル上昇時のみ）アラートを生成する。</summary>
    let detectVolumeThresholdAlerts
        (warningBytes: int64)
        (dangerBytes: int64)
        (previousLevels: Map<string, int>)
        (volumes: VolumeSnapshot list)
        (occurredAtUtc: DateTimeOffset)
        : AlertEventEntry list * Map<string, int> =
        let ts = occurredAtUtc.ToUniversalTime().ToString("o")
        let events = ResizeArray<AlertEventEntry>()
        let mutable nextLevels = Map.empty<string, int>

        for vol in volumes do
            let level =
                match vol.SizeBytes with
                | Some bytes when bytes >= dangerBytes -> 2
                | Some bytes when bytes >= warningBytes -> 1
                | _ -> 0
            nextLevels <- Map.add vol.Name level nextLevels
            let prevLevel = Map.tryFind vol.Name previousLevels |> Option.defaultValue 0
            if level > prevLevel && level >= 1 then
                let sev = if level >= 2 then AlertSeverity.Danger else AlertSeverity.Warning
                let sizeLabel =
                    if String.IsNullOrWhiteSpace(vol.SizeText) then
                        vol.SizeBytes |> Option.map VolumeParser.formatByteSize |> Option.defaultValue ""
                    else vol.SizeText
                let thresholdLabel =
                    if level >= 2 then VolumeParser.formatByteSize dangerBytes
                    else VolumeParser.formatByteSize warningBytes
                events.Add({
                    Id = 0L
                    OccurredAt = ts
                    Severity = sev
                    Category = AlertCategory.VolumeThresholdExceeded
                    TargetId = "volume:" + vol.Name
                    TargetName = VolumeParser.shortDisplayName vol.Name vol.IsAnonymous
                    TitleKey = "ボリューム容量超過"
                    Message = sprintf "%s (>= %s)" sizeLabel thresholdLabel
                })

        (events |> Seq.toList), nextLevels

    /// <summary>サービス診断結果に Danger 所見が含まれる場合のアラートを生成する。</summary>
    let createDiagnosticsDangerAlert
        (containerId: string)
        (containerName: string)
        (findings: DiagnosticFinding list)
        (occurredAtUtc: DateTimeOffset)
        : AlertEventEntry option =
        let dangerFindings = findings |> List.filter (fun f -> f.Severity = FindingSeverity.Danger)
        if List.isEmpty dangerFindings then None
        else
            let ts = occurredAtUtc.ToUniversalTime().ToString("o")
            let titles = dangerFindings |> List.truncate 2 |> List.map (fun f -> f.TitleKey) |> String.concat ", "
            Some {
                Id = 0L
                OccurredAt = ts
                Severity = AlertSeverity.Danger
                Category = AlertCategory.DiagnosticsDanger
                TargetId = trimOrEmpty containerId
                TargetName = trimOrEmpty containerName
                TitleKey = "サービス診断（危険）"
                Message = sprintf "%d critical finding(s): %s" dangerFindings.Length titles
            }

    let severityToDbString = function
        | AlertSeverity.Danger -> "danger"
        | AlertSeverity.Warning -> "warning"

    let severityFromDbString (raw: string) =
        if String.Equals(trimOrEmpty raw, "danger", StringComparison.OrdinalIgnoreCase) then AlertSeverity.Danger
        else AlertSeverity.Warning

    let categoryToDbString = function
        | ContainerAbnormalExit -> "abnormal_exit"
        | ContainerOomKilled -> "oom_killed"
        | ContainerRestartSurge -> "restart_surge"
        | DiagnosticsDanger -> "diagnostics_danger"
        | VolumeThresholdExceeded -> "volume_threshold"
        | HighResourceUsage -> "high_resource"
        | ComposeAnomaly -> "compose_anomaly"

    let categoryFromDbString (raw: string) =
        match (trimOrEmpty raw).ToLowerInvariant() with
        | "abnormal_exit" -> ContainerAbnormalExit
        | "oom_killed" -> ContainerOomKilled
        | "restart_surge" -> ContainerRestartSurge
        | "diagnostics_danger" -> DiagnosticsDanger
        | "volume_threshold" -> VolumeThresholdExceeded
        | "high_resource" -> HighResourceUsage
        | _ -> ComposeAnomaly

    let severityBadge = function
        | AlertSeverity.Danger -> "🔴"
        | AlertSeverity.Warning -> "🟡"

    let severityLabel (languageCode: string) = function
        | AlertSeverity.Danger -> Localization.translate languageCode "危険" "Danger"
        | AlertSeverity.Warning -> Localization.translate languageCode "警告" "Warning"

    let categoryLabel (languageCode: string) = function
        | ContainerAbnormalExit -> Localization.translate languageCode "コンテナ異常終了" "Container abnormal exit"
        | ContainerOomKilled -> Localization.translate languageCode "OOMKilled 検出" "OOMKilled detected"
        | ContainerRestartSurge -> Localization.translate languageCode "再起動回数の急増" "Restart surge"
        | DiagnosticsDanger -> Localization.translate languageCode "サービス診断（危険）" "Diagnostics danger"
        | VolumeThresholdExceeded -> Localization.translate languageCode "ボリューム容量超過" "Volume threshold exceeded"
        | HighResourceUsage -> Localization.translate languageCode "警告" "Warning"
        | ComposeAnomaly -> Localization.translate languageCode "Compose異常" "Compose warning"

    let formatTimestamp (isoTimestamp: string) : string =
        if String.IsNullOrWhiteSpace(isoTimestamp) then ""
        else
            match DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, dt -> dt.LocalDateTime.ToString("MM/dd HH:mm:ss")
            | _ -> isoTimestamp

    let trimNewest (maxCount: int) (entries: AlertEventEntry list) : AlertEventEntry list =
        let cap = max 0 maxCount
        entries
        |> List.sortWith (fun a b ->
            let tsCmp =
                match DateTimeOffset.TryParse(a.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                      DateTimeOffset.TryParse(b.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                | (true, dtA), (true, dtB) -> compare dtB dtA
                | _ -> String.CompareOrdinal(b.OccurredAt, a.OccurredAt)
            if tsCmp <> 0 then tsCmp else compare b.Id a.Id)
        |> List.truncate cap

/// <summary>UI 表示用アラート履歴モデル。</summary>
type AlertDisplayItem(entry: AlertEventEntry, languageCode: string) =
    member _.Entry = entry
    member _.OccurredAtText = AlertMonitor.formatTimestamp entry.OccurredAt
    member _.SeverityBadge = AlertMonitor.severityBadge entry.Severity
    member _.SeverityText = AlertMonitor.severityLabel languageCode entry.Severity
    member _.CategoryText = AlertMonitor.categoryLabel languageCode entry.Category
    member _.TitleText = Localization.translateKey languageCode entry.TitleKey
    member _.TargetName = entry.TargetName
    member _.Message = entry.Message
    member _.SummaryLine =
        if String.IsNullOrWhiteSpace(entry.TargetName) then entry.Message
        elif String.IsNullOrWhiteSpace(entry.Message) then entry.TargetName
        else sprintf "%s — %s" entry.TargetName entry.Message
    member _.SeverityBrush : IBrush =
        match entry.Severity with
        | AlertSeverity.Danger -> Brushes.Crimson
        | AlertSeverity.Warning -> Brushes.DarkGoldenrod

/// <summary>共有 SQLite データベース上で alert_history テーブルを管理するリポジトリ。</summary>
[<AbstractClass; Sealed>]
type AlertHistoryRepository private () =

    static let ensureTables (connection: SqliteConnection) =
        let sql = """
            CREATE TABLE IF NOT EXISTS alert_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                severity TEXT NOT NULL,
                category TEXT NOT NULL,
                target_id TEXT NOT NULL,
                target_name TEXT NOT NULL,
                title_key TEXT NOT NULL,
                message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_alert_history_time ON alert_history(occurred_at DESC, id DESC);
        """
        use cmd = new SqliteCommand(sql, connection)
        cmd.ExecuteNonQuery() |> ignore

    static let readEntry (reader: SqliteDataReader) : AlertEventEntry =
        {
            Id = reader.GetInt64(0)
            OccurredAt = if reader.IsDBNull(1) then "" else reader.GetString(1)
            Severity = AlertMonitor.severityFromDbString (if reader.IsDBNull(2) then "" else reader.GetString(2))
            Category = AlertMonitor.categoryFromDbString (if reader.IsDBNull(3) then "" else reader.GetString(3))
            TargetId = if reader.IsDBNull(4) then "" else reader.GetString(4)
            TargetName = if reader.IsDBNull(5) then "" else reader.GetString(5)
            TitleKey = if reader.IsDBNull(6) then "" else reader.GetString(6)
            Message = if reader.IsDBNull(7) then "" else reader.GetString(7)
        }

    /// <summary>alert_history テーブルおよびインデックスを初期化する。</summary>
    static member Initialize() =
        try
            let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
            if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            ensureTables connection
        with _ -> ()

    /// <summary>アラート 1 件を記録し、保持上限（既定 200 件）を超えた古い行を削除する。</summary>
    static member Record(entry: AlertEventEntry, ?maxEntries: int) : AlertEventEntry =
        try
            let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
            if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore
            let limit = max 1 (defaultArg maxEntries AlertMonitor.maxAlertHistoryRows)
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            ensureTables connection
            use tx = connection.BeginTransaction()
            try
                let insertSql = """
                    INSERT INTO alert_history (occurred_at, severity, category, target_id, target_name, title_key, message)
                    VALUES (@occurredAt, @severity, @category, @targetId, @targetName, @titleKey, @message);
                    SELECT last_insert_rowid();
                """
                use insCmd = new SqliteCommand(insertSql, connection, tx)
                insCmd.Parameters.AddWithValue("@occurredAt", entry.OccurredAt) |> ignore
                insCmd.Parameters.AddWithValue("@severity", AlertMonitor.severityToDbString entry.Severity) |> ignore
                insCmd.Parameters.AddWithValue("@category", AlertMonitor.categoryToDbString entry.Category) |> ignore
                insCmd.Parameters.AddWithValue("@targetId", entry.TargetId) |> ignore
                insCmd.Parameters.AddWithValue("@targetName", entry.TargetName) |> ignore
                insCmd.Parameters.AddWithValue("@titleKey", entry.TitleKey) |> ignore
                insCmd.Parameters.AddWithValue("@message", entry.Message) |> ignore
                let idObj = insCmd.ExecuteScalar()
                let newId =
                    match idObj with
                    | :? int64 as v -> v
                    | _ -> 0L

                let trimSql = """
                    DELETE FROM alert_history
                    WHERE id NOT IN (
                        SELECT id FROM alert_history
                        ORDER BY occurred_at DESC, id DESC
                        LIMIT @limit
                    );
                """
                use trimCmd = new SqliteCommand(trimSql, connection, tx)
                trimCmd.Parameters.AddWithValue("@limit", limit) |> ignore
                trimCmd.ExecuteNonQuery() |> ignore

                tx.Commit()
                { entry with Id = newId }
            with _ ->
                try tx.Rollback() with _ -> ()
                entry
        with _ -> entry

    /// <summary>最新のアラート履歴を新しい順で取得する。</summary>
    static member GetRecent(?limit: int) : AlertEventEntry list =
        try
            AlertHistoryRepository.Initialize()
            let maxRows = max 1 (defaultArg limit AlertMonitor.maxAlertHistoryRows)
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            let sql = """
                SELECT id, occurred_at, severity, category, target_id, target_name, title_key, message
                FROM alert_history
                ORDER BY occurred_at DESC, id DESC
                LIMIT @limit;
            """
            use cmd = new SqliteCommand(sql, connection)
            cmd.Parameters.AddWithValue("@limit", maxRows) |> ignore
            use reader = cmd.ExecuteReader()
            let entries = ResizeArray<AlertEventEntry>()
            while reader.Read() do
                entries.Add(readEntry reader)
            entries |> Seq.toList
        with _ -> []

/// <summary>OS デスクトップ通知（Linux notify-send / Windows Toast）を非同期で送信するヘルパー。</summary>
module OsNotifier =

    /// <summary>OS 通知をベストエフォートで非同期送信する（失敗しても例外を送出しない）。</summary>
    let sendDesktopNotification (title: string) (message: string) : unit =
        let safeTitle = if isNull title then "" else title.Trim()
        let safeMsg = if isNull message then "" else message.Trim()
        if not (String.IsNullOrWhiteSpace(safeTitle)) then
            Task.Run(fun () ->
                try
                    if OperatingSystem.IsLinux() then
                        let inFlatpak = not (OperatingSystem.IsWindows()) && File.Exists("/.flatpak-info")
                        let psi = ProcessStartInfo(if inFlatpak then "flatpak-spawn" else "notify-send")
                        psi.UseShellExecute <- false
                        psi.CreateNoWindow <- true
                        if inFlatpak then
                            psi.WorkingDirectory <- "/"
                            psi.ArgumentList.Add("--host")
                            psi.ArgumentList.Add("notify-send")
                        psi.ArgumentList.Add("--app-name=EasyContainerManager")
                        psi.ArgumentList.Add(safeTitle)
                        psi.ArgumentList.Add(safeMsg)
                        use proc = Process.Start(psi)
                        if not (isNull proc) then
                            proc.WaitForExit(3000) |> ignore
                    elif OperatingSystem.IsWindows() then
                        let script = """
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$title = [System.Security.SecurityElement]::Escape($env:ECM_NOTIFY_TITLE)
$msg = [System.Security.SecurityElement]::Escape($env:ECM_NOTIFY_MSG)
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$title</text><text>$msg</text></binding></visual></toast>")
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('EasyContainerManager').Show($toast)
"""
                        let psi = ProcessStartInfo("powershell.exe")
                        psi.UseShellExecute <- false
                        psi.CreateNoWindow <- true
                        psi.Environment.["ECM_NOTIFY_TITLE"] <- safeTitle
                        psi.Environment.["ECM_NOTIFY_MSG"] <- safeMsg
                        psi.ArgumentList.Add("-NoProfile")
                        psi.ArgumentList.Add("-NonInteractive")
                        psi.ArgumentList.Add("-WindowStyle")
                        psi.ArgumentList.Add("Hidden")
                        psi.ArgumentList.Add("-Command")
                        psi.ArgumentList.Add(script)
                        use proc = Process.Start(psi)
                        if not (isNull proc) then
                            proc.WaitForExit(4000) |> ignore
                with _ -> ())
            |> ignore
