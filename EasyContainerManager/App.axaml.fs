namespace EasyContainerManager

open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Data.Core
open Avalonia.Data.Core.Plugins
open Avalonia.Markup.Xaml
open Avalonia.Styling
open EasyContainerManager.ViewModels
open EasyContainerManager.Views

/// <summary>Avalonia アプリケーションの初期化とメインウィンドウ起動を管理するクラス。</summary>
type App() =
    inherit Application()

    /// <summary>XAML リソースを読み込んでアプリケーションを初期化する。</summary>
    override this.Initialize() =
            AvaloniaXamlLoader.Load(this)

    /// <summary>フレームワーク初期化完了後に設定を読み込み、利用可能なランタイムに応じてウィンドウを表示する。</summary>
    override this.OnFrameworkInitializationCompleted() =
        let settings = AppSettings.Load()
        this.RequestedThemeVariant <-
            match settings.Theme with
            | "Light" -> ThemeVariant.Light
            | "Dark" -> ThemeVariant.Dark
            | _ -> ThemeVariant.Default

        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let createMainWindow runtime =
                let needsComposeMigration =
                    settings.ComposeProjects
                    |> Seq.exists (fun project -> project.ConnectionId = "local" && System.String.IsNullOrWhiteSpace(project.Engine))
                let viewModel = new MainViewModel(settingsOverride = settings, runtime = runtime)
                if needsComposeMigration then AppSettings.Save(settings)
                MainWindow(DataContext = viewModel)
            let installed = ContainerRuntime.detectInstalled()
            match ContainerRuntime.resolveStartupRuntime installed with
            | Some runtime -> desktop.MainWindow <- createMainWindow runtime
            | None ->
                let selectionWindow = RuntimeSelectionWindow()
                selectionWindow.SetLanguage(settings.Language)
                selectionWindow.SetAvailableRuntimes(installed)
                desktop.MainWindow <- selectionWindow
                selectionWindow.RuntimeSelected.Add(fun runtime ->
                    let mainWindow = createMainWindow runtime
                    desktop.MainWindow <- mainWindow
                    mainWindow.Show()
                    selectionWindow.Close())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
