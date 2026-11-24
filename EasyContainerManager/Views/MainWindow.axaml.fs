namespace EasyContainerManager.Views

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Notifications
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open EasyContainerManager.ViewModels

/// <summary>メイン画面のウィンドウとボタン操作・ファイルピッカー・通知表示を仲介するクラス。</summary>
type MainWindow () as this = 
    inherit Window ()

    let mutable notificationSubscription: IDisposable option = None

    do
        this.InitializeComponent()

        // 指定ボタンの Click イベントに ViewModel のアクションを紐付けるヘルパー
        let onClick name action =
            this.FindControl<Button>(name).Click.Add(fun _ ->
                match this.DataContext with
                | :? MainViewModel as viewModel -> action viewModel
                | _ -> ())

        // トップバー操作
        onClick "RefreshButton" (fun viewModel -> viewModel.RefreshAsync() |> Async.StartImmediate)
        onClick "DismissAlertBannerButton" (fun viewModel -> viewModel.DismissAlertBanner())
        onClick "OpenMobileSessionDialogButton" (fun viewModel -> viewModel.OpenMobileSessionDialog())
        onClick "CloseMobileSessionDialogButton" (fun viewModel -> viewModel.CloseMobileSessionDialog())
        onClick "CloseMobileSessionDialogXButton" (fun viewModel -> viewModel.CloseMobileSessionDialog())
        onClick "StartMobileSessionButton" (fun viewModel -> viewModel.StartMobileSession())
        onClick "StopMobileSessionButton" (fun viewModel -> viewModel.StopMobileSession())
        onClick "ReissueMobileSessionButton" (fun viewModel -> viewModel.ReissueMobileSession())
        onClick "OpenFaqDialogButton" (fun viewModel -> viewModel.OpenFaqDialog())
        onClick "CloseFaqDialogButton" (fun viewModel -> viewModel.CloseFaqDialog())
        onClick "CloseFaqDialogXButton" (fun viewModel -> viewModel.CloseFaqDialog())
        onClick "OpenInfoDialogButton" (fun viewModel -> viewModel.OpenInfoDialog())
        onClick "CloseInfoDialogButton" (fun viewModel -> viewModel.CloseInfoDialog())
        onClick "CloseInfoDialogXButton" (fun viewModel -> viewModel.CloseInfoDialog())
        onClick "UpdateMasterDataButton" (fun viewModel -> viewModel.UpdateMasterDataAsync() |> Async.StartImmediate)
        onClick "OpenWebsiteButton" (fun viewModel -> viewModel.OpenWebsite())
        onClick "OpenInstagramButton" (fun viewModel -> viewModel.OpenInstagram())
        onClick "OpenLatestVersionButton" (fun viewModel -> viewModel.OpenLatestVersionPage())
        onClick "StartContainerEngineButton" (fun viewModel -> viewModel.StartContainerEngineAsync() |> Async.StartImmediate)

        // ボリューム操作（読み取り専用）
        onClick "RefreshVolumesButton" (fun viewModel -> viewModel.RefreshVolumesAsync() |> Async.StartImmediate)

        // コンテナ操作
        onClick "StartButton" (fun viewModel -> viewModel.StartSelectedAsync() |> Async.StartImmediate)
        onClick "StopButton" (fun viewModel -> viewModel.StopSelectedAsync() |> Async.StartImmediate)
        onClick "RestartButton" (fun viewModel -> viewModel.RestartSelectedAsync() |> Async.StartImmediate)
        onClick "EnterContainerButton" (fun viewModel -> viewModel.EnterContainerAsync() |> Async.StartImmediate)
        onClick "HostTerminalButton" (fun viewModel -> viewModel.OpenHostTerminal())
        onClick "RemoveContainerButton" (fun viewModel -> viewModel.RemoveSelectedContainerAsync() |> Async.StartImmediate)
        onClick "OpenInferenceDialogButton" (fun viewModel -> viewModel.OpenInferenceDialog())
        onClick "InferredServicesCardButton" (fun viewModel -> viewModel.OpenInferenceDialog())
        onClick "CloseInferenceDialogButton" (fun viewModel -> viewModel.CloseInferenceDialog())
        onClick "OpenDiagnosticsButton" (fun viewModel -> viewModel.OpenDiagnosticsDialog())
        onClick "CloseDiagnosticsDialogButton" (fun viewModel -> viewModel.CloseDiagnosticsDialog())
        onClick "RerunDiagnosticsButton" (fun viewModel -> viewModel.RunDiagnostics())
        onClick "InferenceDialogRunDiagnosticsButton" (fun viewModel ->
            viewModel.CloseInferenceDialog()
            viewModel.OpenDiagnosticsDialog())

        // イメージ操作
        onClick "PullImageButton" (fun viewModel -> viewModel.PullImageAsync() |> Async.StartImmediate)
        onClick "RemoveImageButton" (fun viewModel -> viewModel.RemoveSelectedImageAsync() |> Async.StartImmediate)

        // Compose操作
        onClick "RegisterComposeButton" (fun viewModel -> viewModel.RegisterComposeProject())
        onClick "StartComposeButton" (fun viewModel -> viewModel.StartComposeAsync() |> Async.StartImmediate)
        onClick "StopComposeButton" (fun viewModel -> viewModel.StopComposeAsync() |> Async.StartImmediate)
        onClick "RestartComposeButton" (fun viewModel -> viewModel.RestartComposeAsync() |> Async.StartImmediate)
        onClick "ComposeLogsButton" (fun viewModel -> viewModel.ShowComposeLogsAsync() |> Async.StartImmediate)
        onClick "ComposeTerminalButton" (fun viewModel -> viewModel.OpenComposeTerminal())
        onClick "OpenComposeBrowserButton" (fun viewModel -> viewModel.OpenComposeBrowser())
        onClick "RemoveComposeProjectButton" (fun viewModel -> viewModel.RemoveSelectedComposeProject())

        // SSH接続先管理
        onClick "SaveSshButton" (fun viewModel -> viewModel.SaveSshConnection())
        onClick "TestSshButton" (fun viewModel -> viewModel.TestSshConnectionAsync() |> Async.StartImmediate)
        onClick "ClearSshButton" (fun viewModel -> viewModel.ClearSshEditor())
        onClick "RemoveSshButton" (fun viewModel -> viewModel.RemoveSshConnection())
        this.Opened.Add(fun _ ->
            match this.DataContext with
            | :? MainViewModel as viewModel ->
                let manager = WindowNotificationManager(this, Position = NotificationPosition.TopRight, MaxItems = 3)
                notificationSubscription <- Some(viewModel.AlertRaised.Subscribe(fun (title, message) ->
                    manager.Show(Notification(title, message, NotificationType.Warning, TimeSpan.FromSeconds(8.0)))))
            | _ -> ())
        this.Closed.Add(fun _ ->
            notificationSubscription |> Option.iter (fun subscription -> subscription.Dispose())
            match this.DataContext with
            | :? IDisposable as viewModel -> viewModel.Dispose()
            | _ -> ())
        this.FindControl<Button>("BrowseSshKeyButton").Click.Add(fun _ ->
            match this.DataContext with
            | :? MainViewModel as viewModel ->
                async {
                    try
                        let options = FilePickerOpenOptions(Title = viewModel.PrivateKeyLabel, AllowMultiple = false)
                        let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                        if files.Count > 0 then viewModel.NewSshKey <- files.[0].Path.LocalPath
                    with error -> viewModel.ReportFolderPickerError(error.Message)
                } |> Async.StartImmediate
            | _ -> ())
        this.FindControl<Button>("BrowseComposePathButton").Click.Add(fun _ ->
            match this.DataContext with
            | :? MainViewModel as viewModel ->
                async {
                    try
                        let options = FolderPickerOpenOptions()
                        options.Title <- viewModel.FolderPickerTitle
                        options.AllowMultiple <- false
                        let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                        if folders.Count > 0 then
                            viewModel.NewComposePath <- folders.[0].Path.LocalPath
                    with ex -> viewModel.ReportFolderPickerError(ex.Message)
                }
                |> Async.StartImmediate
            | _ -> ())

    /// <summary>XAML からウィンドウの UI 要素を読み込む。</summary>
    member private this.InitializeComponent() =
        AvaloniaXamlLoader.Load(this)
