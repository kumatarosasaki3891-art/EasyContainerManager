namespace EasyContainerManager

open System
open System.IO
open System.Text.Json
open System.Collections.ObjectModel
open EasyContainerManager.Models

/// <summary>アプリのユーザー設定を保持し、JSON ファイルへの読み書きを行うクラス。</summary>
[<AllowNullLiteral>]
type AppSettings() =
    /// <summary>定期的な自動更新が有効かどうか。</summary>
    member val AutoRefresh = true with get, set

    /// <summary>自動更新の間隔秒数（既定値: 10秒）。</summary>
    member val RefreshIntervalSec = 10 with get, set

    /// <summary>表示言語コード（既定値: "ja-JP"）。</summary>
    member val Language = "ja-JP" with get, set

    /// <summary>アプリのテーマ（"System", "Light", "Dark"）。</summary>
    member val Theme = "System" with get, set

    /// <summary>登録されている Compose プロジェクト一覧。</summary>
    member val ComposeProjects = ObservableCollection<ComposeProject>() with get, set

    /// <summary>登録されている SSH 接続プロファイル一覧。</summary>
    member val SshConnections = ObservableCollection<SshConnection>() with get, set

    /// <summary>リソース使用率などのデスクトップ警告通知が有効かどうか。</summary>
    member val NotificationsEnabled = false with get, set

    /// <summary>OS ごとの設定ファイル保存パスを返す。</summary>
    static member private SettingsPath(applicationName: string) =
        let directory =
            if OperatingSystem.IsWindows() then
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), applicationName)
            else
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", applicationName)
        Path.Combine(directory, "settings.json")

    static member private FilePath = AppSettings.SettingsPath("EasyContainerManager")
    static member private LegacyFilePath = AppSettings.SettingsPath("EasyDockerManager")

    /// <summary>設定ファイルを読み込み、値を正規化して返す（未作成・失敗時は既定値）。</summary>
    static member Load() =
        try
            let path = if File.Exists(AppSettings.FilePath) then AppSettings.FilePath else AppSettings.LegacyFilePath
            if File.Exists(path) then
                let settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                if isNull settings then AppSettings()
                else
                    settings.RefreshIntervalSec <- Math.Clamp(settings.RefreshIntervalSec, 1, 3600)
                    settings.Language <- Localization.normalizeCode(settings.Language)
                    if settings.Theme <> "Light" && settings.Theme <> "Dark" then settings.Theme <- "System"
                    if isNull settings.ComposeProjects then settings.ComposeProjects <- ObservableCollection<ComposeProject>()
                    if isNull settings.SshConnections then settings.SshConnections <- ObservableCollection<SshConnection>()
                    settings
            else
                AppSettings()
        with _ -> AppSettings()

    /// <summary>現在の設定を JSON ファイルへ保存する。</summary>
    static member Save(settings: AppSettings) =
        try
            let path = AppSettings.FilePath
            Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
            let options = JsonSerializerOptions(WriteIndented = true)
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options))
        with _ -> ()