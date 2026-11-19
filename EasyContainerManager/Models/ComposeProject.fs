namespace EasyContainerManager.Models

open System
open CommunityToolkit.Mvvm.ComponentModel
open EasyContainerManager

/// <summary>Compose プロジェクトの稼働コンテナ数と異常サービス一覧のスナップショット。</summary>
type ComposeSnapshot =
    { Running: int; Total: int; Issues: string list }

/// <summary>登録された Compose プロジェクトの設定と稼働状態を保持するモデル。</summary>
[<AllowNullLiteral>]
type ComposeProject() =
    inherit ObservableObject()

    let mutable id = Guid.NewGuid().ToString()
    let mutable name = ""
    let mutable folderPath = ""
    let mutable browserUrl = ""
    let mutable statusKey = "unknown"
    let mutable runningContainerCount = 0
    let mutable totalContainerCount = 0
    let mutable issues: string list = []
    let mutable languageCode = "ja-JP"
    let mutable isOperating = false

    /// <summary>プロジェクトの一意識別子（SQLite 保存用）。</summary>
    member this.Id
        with get () = id
        and set value =
            if this.SetProperty(&id, value) then
                this.OnPropertyChanged(nameof this.Id)

    /// <summary>所属する接続先ID（"local" または SSH 接続プロファイルの ID）。</summary>
    member val ConnectionId = "local" with get, set

    /// <summary>このプロジェクトで使用するコンテナエンジン（"docker", "podman", "nerdctl"）。</summary>
    member val Engine = "" with get, set

    /// <summary>この Compose プロジェクトに対する操作（起動・停止・再起動等）が実行中かどうかを示す。</summary>
    member this.IsOperating
        with get () = isOperating
        and set value =
            if this.SetProperty(&isOperating, value) then
                this.OnPropertyChanged(nameof this.IsOperating)

    /// <summary>Compose プロジェクトの登録名。</summary>
    member this.Name
        with get () = name
        and set value = this.SetProperty(&name, value) |> ignore

    /// <summary>Compose ファイルが存在するフォルダの絶対パス。</summary>
    member this.FolderPath
        with get () = folderPath
        and set value = this.SetProperty(&folderPath, value) |> ignore

    /// <summary>ワンクリックで開くブラウザ URL（任意）。</summary>
    member this.BrowserUrl
        with get () = browserUrl
        and set value = this.SetProperty(&browserUrl, value) |> ignore

    /// <summary>現在の表示言語コード（ja-JP, en-US 等）。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then this.OnPropertyChanged(nameof this.Status)

    /// <summary>ローカライズされた稼働状態文字列（アイコン付き）。</summary>
    member this.Status
        with get () =
            match statusKey with
            | "running" -> Localization.translate languageCode "🟢 正常" "🟢 Normal"
            | "partial" -> Localization.translate languageCode "🟡 一部異常" "🟡 Partial failure"
            | "stopped" -> "🔴 " + Localization.translate languageCode "停止中" "Stopped"
            | _ -> "⚫ " + Localization.translate languageCode "確認できません" "Unavailable"
        and set value =
            let key = if value = "起動中" || value = "Running" then "running" elif value = "停止" || value = "Stopped" then "stopped" else "unknown"
            if this.SetProperty(&statusKey, key) then this.OnPropertyChanged(nameof this.Status)

    /// <summary>現在稼働中のコンテナ数。</summary>
    member _.RunningContainerCount = runningContainerCount

    /// <summary>プロジェクトに定義された総コンテナ数。</summary>
    member _.TotalContainerCount = totalContainerCount

    /// <summary>稼働数 / 総数の表示ラベル（例: 2 / 2）。</summary>
    member _.RunningCountLabel = sprintf "%d / %d" runningContainerCount totalContainerCount

    /// <summary>異常・警告が発生しているサービス名のカンマ区切り一覧。</summary>
    member _.Warning = String.concat ", " issues

    /// <summary>状態キー（running / partial / stopped / unknown）。</summary>
    member _.StatusKey = statusKey

    /// <summary>取得した Compose スナップショットから稼働数・状態・警告メッセージを更新する。</summary>
    member this.UpdateSnapshot(snapshot: ComposeSnapshot) =
        totalContainerCount <- snapshot.Total
        issues <- snapshot.Issues
        let key =
            if snapshot.Running = 0 then "stopped"
            elif snapshot.Running = snapshot.Total && List.isEmpty snapshot.Issues then "running"
            else "partial"
        this.UpdateStatus(key, snapshot.Running)
        this.OnPropertyChanged(nameof this.TotalContainerCount)
        this.OnPropertyChanged(nameof this.RunningCountLabel)
        this.OnPropertyChanged(nameof this.Warning)

    /// <summary>Compose 状態キーと稼働コンテナ数を更新し、関連プロパティの変更を通知する。</summary>
    member this.UpdateStatus(key: string, count: int) =
        if statusKey <> key then
            statusKey <- key
            this.OnPropertyChanged(nameof this.Status)
        if runningContainerCount <> count then
            runningContainerCount <- count
            this.OnPropertyChanged(nameof this.RunningContainerCount)
            this.OnPropertyChanged(nameof this.RunningCountLabel)
        if key = "unknown" then
            totalContainerCount <- 0
            issues <- []
            this.OnPropertyChanged(nameof this.RunningCountLabel)
            this.OnPropertyChanged(nameof this.Warning)