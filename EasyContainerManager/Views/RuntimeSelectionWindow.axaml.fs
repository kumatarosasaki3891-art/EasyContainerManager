namespace EasyContainerManager.Views

open Avalonia.Controls
open Avalonia.Markup.Xaml
open EasyContainerManager

/// <summary>起動エンジンを選択するダイアログ（複数検出時または未検出時に表示）。</summary>
type RuntimeSelectionWindow() as this =
    inherit Window()

    let runtimeSelected = Event<ContainerRuntime>()
    let mutable currentLanguage = "ja-JP"
    let mutable detectedRuntimes: ContainerRuntime list = []

    /// <summary>現在の言語設定に合わせてウィンドウタイトルおよび各ボタンのテキストを更新する。</summary>
    let updateLabels () =
        let localize = Localization.translate (Localization.normalizeCode currentLanguage)
        this.Title <- localize "コンテナエンジンの選択" "Choose a container engine"
        let promptKey, promptEn =
            if List.isEmpty detectedRuntimes then
                "コンテナエンジンが検出されませんでした。使用するエンジンを選択してください", "No container engine was detected. Choose the container engine to use"
            else
                "使用するコンテナエンジンを選択してください", "Choose the container engine to use"
        this.FindControl<TextBlock>("PromptText").Text <- localize promptKey promptEn
        this.FindControl<Button>("DockerButton").Content <- localize "Dockerを使用" "Use Docker"
        this.FindControl<Button>("PodmanButton").Content <- localize "Podmanを使用" "Use Podman"
        this.FindControl<Button>("NerdctlButton").Content <- localize "nerdctlを使用" "Use nerdctl"

    /// <summary>検出されたランタイムのみボタンを表示する（未検出時は全ランタイムを表示）。</summary>
    let updateButtons () =
        let visibleRuntimes =
            if List.isEmpty detectedRuntimes then ContainerRuntime.all
            else detectedRuntimes
        for runtime, name in [ Docker, "DockerButton"; Podman, "PodmanButton"; Nerdctl, "NerdctlButton" ] do
            this.FindControl<Button>(name).IsVisible <- List.contains runtime visibleRuntimes

    do
        AvaloniaXamlLoader.Load(this)
        this.FindControl<Button>("DockerButton").Click.Add(fun _ -> runtimeSelected.Trigger(Docker))
        this.FindControl<Button>("PodmanButton").Click.Add(fun _ -> runtimeSelected.Trigger(Podman))
        this.FindControl<Button>("NerdctlButton").Click.Add(fun _ -> runtimeSelected.Trigger(Nerdctl))

    /// <summary>検出されたランタイムに応じてボタン表示とプロンプトを設定する（0件検出時は全ランタイムを表示）。</summary>
    member this.SetAvailableRuntimes(runtimes: ContainerRuntime list) =
        detectedRuntimes <- runtimes
        updateButtons()
        updateLabels()

    /// <summary>指定された言語コードでダイアログ上のラベルをローカライズする。</summary>
    member this.SetLanguage(languageCode: string) =
        currentLanguage <- languageCode
        updateLabels()

    /// <summary>ユーザーがいずれかのランタイムを選択した際に発行されるイベント。</summary>
    member _.RuntimeSelected = runtimeSelected.Publish