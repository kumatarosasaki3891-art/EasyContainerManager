namespace EasyContainerManager.Models

open System
open System.Globalization
open System.IO
open Avalonia.Media
open Microsoft.Data.Sqlite
open EasyContainerManager

/// <summary>コンテナおよび Compose プロジェクトに対するユーザー操作履歴 1 件分のレコード。</summary>
type OperationLogEntry = {
    Id: int64
    ExecutedAt: string
    ContainerId: string
    ContainerName: string
    Operation: string
    Succeeded: bool
    Detail: string option
}

/// <summary>操作履歴の生成・整形・保持上限管理を行う純粋モジュール。</summary>
module OperationLog =
    /// <summary>コンテナごとの操作履歴保持上限件数。</summary>
    let maxEntriesPerContainer = 20

    /// <summary>失敗時詳細メッセージの最大文字数。</summary>
    let maxDetailLength = 200

    let private trimOrEmpty (value: string) =
        if isNull value then "" else value.Trim()

    /// <summary>失敗時詳細テキストの改行を除去してトリミング・長さ制限する。</summary>
    let sanitizeDetail (raw: string option) : string option =
        match raw with
        | None -> None
        | Some text when String.IsNullOrWhiteSpace(text) -> None
        | Some text ->
            let cleaned =
                text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim()
            if String.IsNullOrWhiteSpace(cleaned) then None
            elif cleaned.Length <= maxDetailLength then Some cleaned
            else Some(cleaned.Substring(0, maxDetailLength - 3).TrimEnd() + "...")

    /// <summary>UI コンテナ操作名を正規化された操作キーへ変換する。</summary>
    let mapContainerAction (action: string) : string =
        match action with
        | "起動" -> "start"
        | "停止" -> "stop"
        | "再起動" -> "restart"
        | "削除" -> "remove"
        | other -> trimOrEmpty other

    /// <summary>UI Compose 操作名を正規化された操作キーへ変換する。</summary>
    let mapComposeAction (action: string) : string =
        match action with
        | "起動" -> "compose_up"
        | "停止" -> "compose_down"
        | "再起動" -> "compose_restart"
        | other -> trimOrEmpty other

    /// <summary>Compose プロジェクト用の操作履歴ターゲット ID を生成する。</summary>
    let composeTargetId (projectId: string) : string =
        sprintf "compose:%s" (trimOrEmpty projectId)

    /// <summary>新規の操作履歴レコードを構築する。</summary>
    let createEntry (executedAtUtc: DateTimeOffset) (containerId: string) (containerName: string) (operation: string) (succeeded: bool) (detail: string option) : OperationLogEntry =
        {
            Id = 0L
            ExecutedAt = executedAtUtc.ToUniversalTime().ToString("o")
            ContainerId = trimOrEmpty containerId
            ContainerName = trimOrEmpty containerName
            Operation = trimOrEmpty operation
            Succeeded = succeeded
            Detail = sanitizeDetail detail
        }

    let private compareEntriesDesc (a: OperationLogEntry) (b: OperationLogEntry) =
        let tsCmp =
            match DateTimeOffset.TryParse(a.ExecutedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                  DateTimeOffset.TryParse(b.ExecutedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | (true, dtA), (true, dtB) -> compare dtB dtA
            | _ -> String.CompareOrdinal(b.ExecutedAt, a.ExecutedAt)
        if tsCmp <> 0 then tsCmp else compare b.Id a.Id

    /// <summary>操作履歴を新しい順（日時降順・ID降順）に並べ替える。</summary>
    let sortNewestFirst (entries: OperationLogEntry list) : OperationLogEntry list =
        entries |> List.sortWith compareEntriesDesc

    /// <summary>指定コンテナ ID に一致する操作履歴を新しい順で抽出する。</summary>
    let filterForContainer (containerId: string) (entries: OperationLogEntry list) : OperationLogEntry list =
        let targetId = trimOrEmpty containerId
        if String.IsNullOrWhiteSpace(targetId) then []
        else
            entries
            |> List.filter (fun entry -> String.Equals(entry.ContainerId, targetId, StringComparison.OrdinalIgnoreCase))
            |> sortNewestFirst

    /// <summary>コンテナごとの保持件数を上限までに切り詰める。</summary>
    let trimPerContainer (maxPerContainer: int) (entries: OperationLogEntry list) : OperationLogEntry list =
        let cap = max 0 maxPerContainer
        entries
        |> List.groupBy (fun entry -> (trimOrEmpty entry.ContainerId).ToLowerInvariant())
        |> List.collect (fun (_, group) ->
            group
            |> sortNewestFirst
            |> List.truncate cap)
        |> sortNewestFirst

    /// <summary>新しい履歴を追加し、コンテナごとの保持上限を適用する。</summary>
    let appendAndTrim (maxPerContainer: int) (newEntry: OperationLogEntry) (existing: OperationLogEntry list) : OperationLogEntry list =
        newEntry :: existing |> trimPerContainer maxPerContainer

    /// <summary>正規化された操作キーを現在の表示言語向けラベルへ翻訳する。</summary>
    let operationLabel (languageCode: string) (operation: string) : string =
        match operation with
        | "start" -> Localization.translate languageCode "コンテナ起動" "Start container"
        | "stop" -> Localization.translate languageCode "コンテナ停止" "Stop container"
        | "restart" -> Localization.translate languageCode "コンテナ再起動" "Restart container"
        | "remove" -> Localization.translate languageCode "コンテナ削除" "Delete container"
        | "exec" -> Localization.translate languageCode "コンテナ接続 (exec)" "Enter container (exec)"
        | "compose_up" -> Localization.translate languageCode "Compose 起動" "Compose up"
        | "compose_down" -> Localization.translate languageCode "Compose 停止" "Compose down"
        | "compose_restart" -> Localization.translate languageCode "Compose 再起動" "Compose restart"
        | other -> other

    /// <summary>ISO 8601 時刻をローカル時刻の短縮表示（MM/dd HH:mm:ss）に整形する。</summary>
    let formatTimestamp (isoTimestamp: string) : string =
        if String.IsNullOrWhiteSpace(isoTimestamp) then ""
        else
            match DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, dt -> dt.LocalDateTime.ToString("MM/dd HH:mm:ss")
            | _ -> isoTimestamp

    /// <summary>成否に応じた結果記号（✓ / ✗）を返す。</summary>
    let statusMark (succeeded: bool) : string =
        if succeeded then "✓" else "✗"

/// <summary>UI の操作履歴リスト表示用モデル。</summary>
type OperationLogDisplayItem(entry: OperationLogEntry, languageCode: string) =
    /// <summary>整形済みの実行日時文字列。</summary>
    member _.ExecutedAtText = OperationLog.formatTimestamp entry.ExecutedAt

    /// <summary>ローカライズ済みの操作名文字列。</summary>
    member _.OperationText = OperationLog.operationLabel languageCode entry.Operation

    /// <summary>操作が成功したかどうか。</summary>
    member _.Succeeded = entry.Succeeded

    /// <summary>成否記号（✓ または ✗）。</summary>
    member _.ResultMark = OperationLog.statusMark entry.Succeeded

    /// <summary>成否に応じた表示色ブラシ。</summary>
    member _.ResultBrush : IBrush =
        if entry.Succeeded then Brushes.SeaGreen else Brushes.Crimson

    /// <summary>失敗時の詳細理由文字列。</summary>
    member _.Detail = entry.Detail |> Option.defaultValue ""

    /// <summary>失敗詳細を表示するかどうか。</summary>
    member _.HasDetail = entry.Detail |> Option.exists (String.IsNullOrWhiteSpace >> not)

/// <summary>共有 SQLite データベース上で operation_log テーブルを管理するリポジトリ。</summary>
[<AbstractClass; Sealed>]
type OperationLogRepository private () =

    static let ensureTables (connection: SqliteConnection) =
        let sql = """
            CREATE TABLE IF NOT EXISTS operation_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                executed_at TEXT NOT NULL,
                container_id TEXT NOT NULL,
                container_name TEXT NOT NULL,
                operation TEXT NOT NULL,
                succeeded INTEGER NOT NULL,
                detail TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_operation_log_container ON operation_log(container_id, executed_at DESC, id DESC);
        """
        use cmd = new SqliteCommand(sql, connection)
        cmd.ExecuteNonQuery() |> ignore

    static let readEntry (reader: SqliteDataReader) : OperationLogEntry =
        let detail =
            if reader.IsDBNull(6) then None
            else
                let text = reader.GetString(6)
                if String.IsNullOrWhiteSpace(text) then None else Some text
        {
            Id = reader.GetInt64(0)
            ExecutedAt = if reader.IsDBNull(1) then "" else reader.GetString(1)
            ContainerId = if reader.IsDBNull(2) then "" else reader.GetString(2)
            ContainerName = if reader.IsDBNull(3) then "" else reader.GetString(3)
            Operation = if reader.IsDBNull(4) then "" else reader.GetString(4)
            Succeeded = not (reader.IsDBNull(5)) && reader.GetInt32(5) <> 0
            Detail = detail
        }

    /// <summary>operation_log テーブルおよびインデックスを初期化する。</summary>
    static member Initialize() =
        try
            let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
            if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            ensureTables connection
        with _ -> ()

    /// <summary>ユーザー操作を 1 件記録し、同一コンテナの履歴を直近上限件数以内にトリミングする。</summary>
    static member Record(containerId: string, containerName: string, operation: string, succeeded: bool, ?detail: string, ?executedAtUtc: DateTimeOffset, ?maxEntries: int) =
        try
            if not (String.IsNullOrWhiteSpace(containerId)) && not (String.IsNullOrWhiteSpace(operation)) then
                let dir = Path.GetDirectoryName(ComposeRepository.DatabasePath)
                if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                    Directory.CreateDirectory(dir) |> ignore
                let entry =
                    OperationLog.createEntry
                        (defaultArg executedAtUtc DateTimeOffset.UtcNow)
                        containerId
                        containerName
                        operation
                        succeeded
                        detail
                let limit = max 1 (defaultArg maxEntries OperationLog.maxEntriesPerContainer)
                use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                connection.Open()
                ensureTables connection
                use tx = connection.BeginTransaction()
                try
                    let insertSql = """
                        INSERT INTO operation_log (executed_at, container_id, container_name, operation, succeeded, detail)
                        VALUES (@executedAt, @containerId, @containerName, @operation, @succeeded, @detail);
                    """
                    use insCmd = new SqliteCommand(insertSql, connection, tx)
                    insCmd.Parameters.AddWithValue("@executedAt", entry.ExecutedAt) |> ignore
                    insCmd.Parameters.AddWithValue("@containerId", entry.ContainerId) |> ignore
                    insCmd.Parameters.AddWithValue("@containerName", entry.ContainerName) |> ignore
                    insCmd.Parameters.AddWithValue("@operation", entry.Operation) |> ignore
                    insCmd.Parameters.AddWithValue("@succeeded", if entry.Succeeded then 1 else 0) |> ignore
                    insCmd.Parameters.AddWithValue("@detail", match entry.Detail with Some d -> box d | None -> box DBNull.Value) |> ignore
                    insCmd.ExecuteNonQuery() |> ignore

                    let trimSql = """
                        DELETE FROM operation_log
                        WHERE container_id = @containerId COLLATE NOCASE
                          AND id NOT IN (
                              SELECT id FROM operation_log
                              WHERE container_id = @containerId COLLATE NOCASE
                              ORDER BY executed_at DESC, id DESC
                              LIMIT @limit
                          );
                    """
                    use trimCmd = new SqliteCommand(trimSql, connection, tx)
                    trimCmd.Parameters.AddWithValue("@containerId", entry.ContainerId) |> ignore
                    trimCmd.Parameters.AddWithValue("@limit", limit) |> ignore
                    trimCmd.ExecuteNonQuery() |> ignore

                    tx.Commit()
                with _ ->
                    try tx.Rollback() with _ -> ()
        with _ -> ()

    /// <summary>指定コンテナの操作履歴を新しい順で取得する。</summary>
    static member GetByContainerId(containerId: string, ?limit: int) : OperationLogEntry list =
        try
            if String.IsNullOrWhiteSpace(containerId) then []
            else
                OperationLogRepository.Initialize()
                let maxRows = max 1 (defaultArg limit OperationLog.maxEntriesPerContainer)
                use connection = new SqliteConnection(ComposeRepository.ConnectionString)
                connection.Open()
                let sql = """
                    SELECT id, executed_at, container_id, container_name, operation, succeeded, detail
                    FROM operation_log
                    WHERE container_id = @containerId COLLATE NOCASE
                    ORDER BY executed_at DESC, id DESC
                    LIMIT @limit;
                """
                use cmd = new SqliteCommand(sql, connection)
                cmd.Parameters.AddWithValue("@containerId", containerId.Trim()) |> ignore
                cmd.Parameters.AddWithValue("@limit", maxRows) |> ignore
                use reader = cmd.ExecuteReader()
                let entries = ResizeArray<OperationLogEntry>()
                while reader.Read() do
                    entries.Add(readEntry reader)
                entries |> Seq.toList
        with _ -> []

    /// <summary>記録されているすべての操作履歴を新しい順で取得する。</summary>
    static member GetAll() : OperationLogEntry list =
        try
            OperationLogRepository.Initialize()
            use connection = new SqliteConnection(ComposeRepository.ConnectionString)
            connection.Open()
            let sql = """
                SELECT id, executed_at, container_id, container_name, operation, succeeded, detail
                FROM operation_log
                ORDER BY executed_at DESC, id DESC;
            """
            use cmd = new SqliteCommand(sql, connection)
            use reader = cmd.ExecuteReader()
            let entries = ResizeArray<OperationLogEntry>()
            while reader.Read() do
                entries.Add(readEntry reader)
            entries |> Seq.toList
        with _ -> []
