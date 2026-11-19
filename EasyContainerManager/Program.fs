namespace EasyContainerManager

open System
open Avalonia

/// <summary>アプリケーションのエントリーポイントを提供するモジュール。</summary>
module Program =

    /// <summary>Avalonia アプリケーションのビルダーを構成する。</summary>
    [<CompiledName "BuildAvaloniaApp">] 
    let buildAvaloniaApp () = 
        AppBuilder
            .Configure<App>()
            .WithInterFont()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace(areas = Array.empty)

    /// <summary>デスクトップライフタイムでアプリケーションを起動する。</summary>
    [<EntryPoint; STAThread>]
    let main argv =
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
