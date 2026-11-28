namespace EasyContainerManager.ViewModels

open System
open System.Collections.ObjectModel
open System.Diagnostics
open System.IO
open Avalonia
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.Services

/// <summary>メイン画面のコンテナ・イメージ・ボリューム・Compose・監視・SSH接続・設定を統括する ViewModel。</summary>
type MainViewModel(
    ?settingsOverride: AppSettings,
    ?startAutomatically: bool,
    ?runtime: ContainerRuntime,
    ?composeLogsFetcher: (string -> int option -> System.Threading.CancellationToken -> Async<Result<string, string>>),
    ?masterDataFetcher: (string -> Async<Result<string, string>>),
    ?appVersionOverride: string,
    ?containerActionRunner: (string -> string -> Async<Result<string, string>>),
    ?composeActionRunner: (string -> string -> Async<Result<unit, string>>),
    ?containerListFetcher: (unit -> Async<Result<ContainerInfo list, string>>),
    ?imageListFetcher: (unit -> Async<Result<ImageInfo list, string>>),
    ?composeStatusFetcher: (string -> Async<Result<ComposeSnapshot, string>>),
    ?statsFetcher: (unit -> Async<Result<ContainerStats list, string>>),
    ?connectionChecker: (unit -> Async<Result<unit, string * string>>),
    ?volumeListFetcher: (unit -> Async<Result<VolumeSnapshot list, string>>),
    ?osNotifier: (string -> string -> unit)
) as this =
    inherit ViewModelBase()

    let runtime = defaultArg runtime Docker
    let mutable activeRuntime = runtime
    let mutable docker = ContainerCli(runtime = runtime)
    let mutable localComposeRuntime = runtime
    let mutable composeCli = ContainerCli(runtime = runtime)
    let settings = match settingsOverride with Some value -> value | None -> AppSettings.Load()
    do
        ComposeRepository.Initialize()
        MasterDataRepository.Initialize()
        OperationLogRepository.Initialize()
        AlertHistoryRepository.Initialize()
        if settings.ComposeProjects.Count > 0 then
            ComposeRepository.MigrateFrom(settings.ComposeProjects)
        let dbProjects = ComposeRepository.GetAll()
        for dbProj in dbProjects do
            let exists =
                settings.ComposeProjects
                |> Seq.exists (fun p ->
                    String.Equals(p.FolderPath, dbProj.FolderPath, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(p.ConnectionId, dbProj.ConnectionId, StringComparison.OrdinalIgnoreCase))
            if not exists then
                settings.ComposeProjects.Add(dbProj)
        let mutable migrated = false
        for project in settings.ComposeProjects do
            if String.Equals(project.ConnectionId, "local", StringComparison.OrdinalIgnoreCase) && String.IsNullOrWhiteSpace(project.Engine) then
                project.Engine <- ContainerRuntime.command runtime
                migrated <- true
                ComposeRepository.Save(project)
        if migrated then AppSettings.Save(settings)
    let mutable languageCode = Localization.normalizeCode(settings.Language)
    let localize japanese english = Localization.translate languageCode japanese english
    let format (japanese: string) (english: string) (arguments: obj array) =
        String.Format(localize japanese english, arguments)
    let languageOptions = Localization.languages |> Array.map (fun (code, name) -> ChoiceOption(code, name))
    let themeOptions =
        [| ChoiceOption("System", localize "OS設定" "System")
           ChoiceOption("Light", localize "ライト" "Light")
           ChoiceOption("Dark", localize "ダーク" "Dark") |]
    let localizeDockerError message =
        this.SetConnectionDetails(message)
        localize "コンテナ操作に失敗しました。詳細を確認してください。" "Container operation failed. See details."
    let containers = ObservableCollection<ContainerInfo>()
    let images = ObservableCollection<ImageInfo>()
    let volumes = ObservableCollection<VolumeInfo>()
    let filteredVolumes = ObservableCollection<VolumeInfo>()
    let stats = ObservableCollection<ContainerStats>()
    let composeProjects =
        ObservableCollection<ComposeProject>(
            settings.ComposeProjects
            |> Seq.filter (fun project ->
                String.Equals(project.ConnectionId, "local", StringComparison.OrdinalIgnoreCase)
                && (String.IsNullOrWhiteSpace(project.Engine)
                    || String.Equals(project.Engine, ContainerRuntime.command localComposeRuntime, StringComparison.OrdinalIgnoreCase))))
    let connectionOptions = ObservableCollection<ChoiceOption>()
    let mutable connectionId = "local"
    let mutable selectedSshConnection: SshConnection = null
    let mutable newSshName = ""
    let mutable newSshHost = ""
    let mutable newSshPort = "22"
    let mutable newSshUser = ""
    let mutable newSshKey = ""
    let sshEngineOptions =
        ContainerRuntime.all
        |> List.map (fun engine -> ChoiceOption(ContainerRuntime.command engine, ContainerRuntime.displayName engine))
        |> List.toArray
    let mutable newSshEngine = sshEngineOptions |> Array.find (fun option -> option.Key = ContainerRuntime.command runtime)
    let mutable notificationsEnabled = settings.NotificationsEnabled
    let resourceAlerts = AlertTracker()
    let composeAlerts = AlertTracker()
    let resourceHistoryTracker = AlertTracker()
    let composeHistoryTracker = AlertTracker()
    let alertRaised = Event<string * string>()
    let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds(float settings.RefreshIntervalSec))
    let mutable selectedContainer: ContainerInfo = null
    let mutable selectedImage: ImageInfo = null
    let mutable selectedVolume: VolumeInfo = null
    let mutable selectedComposeProject: ComposeProject = null
    let mutable showUnusedVolumesOnly = false
    let mutable volumeSearchText = ""
    let mutable updatingVolumeList = false
    let mutable volumeRefreshTickCount = 0
    let mutable containerMonitorBaselineDone = false
    let mutable previousContainerSnapshots = Map.empty<string, ContainerMonitorSnapshot>
    let mutable previousVolumeAlertLevels = Map.empty<string, int>
    let mutable alertHistoryEntries : AlertEventEntry list = []
    let mutable activeAlertBanner : AlertEventEntry option = None
    let mutable latestDiagnosticsSummaries = Map.empty<string, int * int * string list>
    let mutable isFaqDialogOpen = false
    let mutable selectedFaqLangKey = FaqData.resolveFaqLanguage settings.Language
    let mutable selectedFaqCategoryKey = "all"
    let mutable faqSearchText = ""
    let faqLanguageOptions =
        [| ChoiceOption("ja-JP", "日本語")
           ChoiceOption("en-US", "English") |]
    let mutable faqCategoryOptions : ChoiceOption array = [||]
    let mobileServer = new MobileSessionServer()
    let lanIpCandidates = ObservableCollection<ChoiceOption>()
    let mutable selectedLanIp: ChoiceOption = null
    let mutable isMobileSessionDialogOpen = false
    let mutable mobileQrBitmap: IImage = null
    let mutable isMobileServerRunning = false
    let mutable mobileSessionUrl = ""
    let mutable mobileSessionStatusText = ""
    let mutable mobileLockedClientText = ""
    let mutable mobileSessionError = ""
    let mutable logText = localize "ログを表示するコンテナを選択してください。" "Select a container to view its logs."
    let mutable composeLogText = localize "Composeプロジェクトを選択してください。" "Select a Compose project."
    let mutable statusMessage = localize "コンテナを読み込み中..." "Loading containers..."
    let mutable isErrorStatus = false
    let mutable isBusy = false
    let mutable autoRefresh = settings.AutoRefresh
    let mutable updatingContainerList = false
    let mutable updatingImageList = false
    let mutable isPullingImage = false
    let mutable followLogs = false
    let mutable imagePullReference = ""
    let mutable newComposeName = ""
    let mutable newComposePath = ""
    let mutable newComposeUrl = ""
    let mutable logProcess: Process option = None
    let mutable composeLogProcess: Process option = None
    let mutable composeLogCts: System.Threading.CancellationTokenSource option = None
    let mutable composeLogRequestVersion = 0
    let mutable followComposeLogs = false
    let isWindows = OperatingSystem.IsWindows()
    let dockerDesktopPaths =
        DockerDesktop.executableCandidates
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
    let mutable dockerDesktopStatus = localize "🔴 エンジン停止中" "🔴 Container engine stopped"
    let mutable dockerDesktopState = "stopped"
    let mutable connectionDetails = ""
    let mutable detailsExpanded = false
    let mutable refreshing = false
    let mutable refreshCompletion: System.Threading.Tasks.TaskCompletionSource<unit> option = None
    let mutable pendingConnectionDetails: string option = None
    let mutable pendingRefreshStatus: string option = None
    let mutable desktopLaunching = false
    let mutable isInferenceDialogOpen = false
    let mutable isDiagnosticsDialogOpen = false
    let mutable isDiagnosing = false
    let mutable currentDiagnosticsResult: DiagnosticsResult option = None
    let mutable currentVersionFindings: DiagnosticFinding list = []
    let mutable selectedContainerOperationLogs: OperationLogEntry list = []
    let mutable selectedComposeOperationLogs: OperationLogEntry list = []
    let mutable masterDataSnapshot = MasterDataRepository.GetSnapshot()
    let mutable isInfoDialogOpen = false
    let mutable isUpdatingMasterData = false
    let mutable masterDataUpdateStatus = ""
    let appId = "simple-docker-manager"
    let websiteUrl = "https://sites.google.com/view/kuma-applications/"
    let instagramUrl = "https://www.instagram.com/zefa_25_zefa/"
    let currentAppVersion =
        match appVersionOverride with
        | Some v when not (String.IsNullOrWhiteSpace v) -> v.Trim()
        | _ ->
            let asm = typeof<MainViewModel>.Assembly
            if asm.GetName().Name = "EasyContainerManager" then
                let v = asm.GetName().Version
                if isNull v then "1.2.1" else sprintf "%d.%d.%d" v.Major v.Minor (max 0 v.Build)
            else
                "1.2.1"
    let postOnUI action =
        if isNull Dispatcher.UIThread then action()
        else
            try
                if Dispatcher.UIThread.CheckAccess() then action()
                else Dispatcher.UIThread.Post(fun () -> action())
            with _ -> action()

    /// <summary>指定された Compose プロジェクトが現在の接続先・エンジンスコープに属するかを判定する。</summary>
    let isComposeInScope (project: ComposeProject) =
        String.Equals(project.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)
        && (not (String.Equals(connectionId, "local", StringComparison.OrdinalIgnoreCase))
            || String.IsNullOrWhiteSpace(project.Engine)
            || String.Equals(project.Engine, ContainerRuntime.command localComposeRuntime, StringComparison.OrdinalIgnoreCase))

    /// <summary>現在表示中の Compose プロジェクト一覧を設定ファイルおよび SQLite データベースへ保存する。</summary>
    let persistComposeProjects () =
        let otherProjects = settings.ComposeProjects |> Seq.filter (isComposeInScope >> not) |> Seq.toList
        settings.ComposeProjects <- ObservableCollection<ComposeProject>(Seq.append otherProjects composeProjects)
        for p in composeProjects do
            ComposeRepository.Save(p)
        AppSettings.Save(settings)

    /// <summary>実行中のコンテナログ追尾プロセスを停止して破棄する。</summary>
    let stopLogFollow () =
        match logProcess with
        | Some streamProcess ->
            try
                if not streamProcess.HasExited then streamProcess.Kill(true)
            with _ -> ()
            streamProcess.Dispose()
            logProcess <- None
        | None -> ()

    /// <summary>実行中の Compose ログ追尾プロセスを停止して破棄する。</summary>
    let stopComposeLogFollow () =
        match composeLogProcess with
        | Some streamProcess ->
            try
                if not streamProcess.HasExited then streamProcess.Kill(true)
            with _ -> ()
            streamProcess.Dispose()
            composeLogProcess <- None
        | None -> ()

    /// <summary>実行中の Compose ログ取得タスクをキャンセルして破棄する。</summary>
    let cancelComposeLogFetch () =
        match composeLogCts with
        | Some cts ->
            try cts.Cancel() with _ -> ()
            cts.Dispose()
            composeLogCts <- None
        | None -> ()

    /// <summary>コンテナ一覧を再取得し、選択状態を維持したままコレクションを同期する。</summary>
    let refreshCore () : Async<bool> = async {
        let! result =
            match containerListFetcher with
            | Some fetcher -> fetcher()
            | None -> docker.ListContainers()
        match result with
        | Ok items ->
            let selectedId = if isNull selectedContainer then None else Some selectedContainer.Id
            let incomingIds = items |> List.map (fun item -> item.Id) |> Set.ofList
            updatingContainerList <- true
            try
                for index = containers.Count - 1 downto 0 do
                    if not (incomingIds.Contains(containers.[index].Id)) then
                        containers.RemoveAt(index)
                for item in items do
                    match containers |> Seq.tryFind (fun current -> current.Id = item.Id) with
                    | Some current ->
                        current.UpdateFrom(item)
                        current.LanguageCode <- languageCode
                    | None ->
                        item.LanguageCode <- languageCode
                        containers.Add(item)
            finally
                updatingContainerList <- false
            match selectedId with
            | Some id when incomingIds.Contains(id) ->
                match containers |> Seq.tryFind (fun item -> item.Id = id) with
                | Some current when not (obj.ReferenceEquals(selectedContainer, current)) ->
                    this.SelectedContainer <- current
                | _ ->
                    this.NotifyProperty(nameof this.InferredServicesText)
                    this.NotifyProperty(nameof this.HasInferredServices)
                    this.NotifyProperty(nameof this.HasSelectedContainer)
                    this.NotifyProperty(nameof this.HasSelectedContainerMounts)
                    this.NotifyProperty(nameof this.SelectedContainerMountsText)
                    this.NotifyProperty(nameof this.HasImageCandidate)
                    this.NotifyProperty(nameof this.ImageCandidateText)
                    this.NotifyProperty(nameof this.HasPortCandidates)
                    this.NotifyProperty(nameof this.PortCandidatesTexts)
                    this.NotifyProperty(nameof this.HasNoInferenceCandidates)
                    this.NotifyDiagnosticsProperties()
            | Some _ ->
                this.SelectedContainer <- null
                this.ShowLogs(localize "選択中のコンテナは一覧から削除されました。" "The selected container is no longer available.")
            | None -> ()

            // 既存の定期更新に相乗りしてコンテナ状態差分を異常アラート化（初回はベースライン記録のみ）
            let snapshots = containers |> Seq.map AlertMonitor.snapshotFromContainer |> Seq.toList
            let anomalyAlerts =
                AlertMonitor.detectContainerAnomalies
                    (not containerMonitorBaselineDone)
                    previousContainerSnapshots
                    snapshots
                    DateTimeOffset.UtcNow
            containerMonitorBaselineDone <- true
            previousContainerSnapshots <- snapshots |> List.map (fun s -> s.Id, s) |> Map.ofList
            for anomaly in anomalyAlerts do
                this.RecordAndNotifyAlert(anomaly, true)
            this.PushMobileSnapshotIfRunning()

            let okMsg = format "{0}件のコンテナ" "{0} containers" [| box containers.Count |]
            if refreshing then
                pendingRefreshStatus <- Some okMsg
                if not isErrorStatus then this.ShowStatus(okMsg)
            else
                this.ShowStatus(okMsg)
                this.SetConnectionDetails("")
            return true
        | Error message ->
            this.ShowErrorStatus(localizeDockerError message)
            return false
    }

    /// <summary>ボリューム一覧を一覧更新・フィルタリングし、容量しきい値超過アラートを判定する。</summary>
    let refreshVolumesCore () : Async<bool> = async {
        let! result =
            match volumeListFetcher with
            | Some fetcher -> fetcher()
            | None when containerListFetcher.IsSome -> async.Return (Ok [])
            | None -> docker.ListVolumes(containers |> Seq.toList)
        match result with
        | Ok items ->
            let selectedName = if isNull selectedVolume then None else Some selectedVolume.Name
            let incomingNames = items |> List.map (fun item -> item.Name) |> Set.ofList
            updatingVolumeList <- true
            try
                for index = volumes.Count - 1 downto 0 do
                    if not (incomingNames.Contains(volumes.[index].Name)) then
                        volumes.RemoveAt(index)
                for snap in items do
                    match volumes |> Seq.tryFind (fun v -> v.Name = snap.Name) with
                    | Some existing ->
                        existing.UpdateFrom(snap)
                        existing.LanguageCode <- languageCode
                    | None ->
                        let v = VolumeInfo(snap)
                        v.LanguageCode <- languageCode
                        volumes.Add(v)
            finally
                updatingVolumeList <- false
            this.ApplyVolumeFilters()
            match selectedName with
            | Some name when incomingNames.Contains(name) ->
                match filteredVolumes |> Seq.tryFind (fun v -> v.Name = name) with
                | Some current when not (obj.ReferenceEquals(selectedVolume, current)) ->
                    this.SelectedVolume <- current
                | _ -> ()
            | Some _ -> this.SelectedVolume <- null
            | None -> ()

            this.NotifyProperty(nameof this.VolumeSummaryText)
            this.NotifyProperty(nameof this.UnusedVolumeSummaryText)
            this.NotifyProperty(nameof this.HasVolumes)
            this.NotifyProperty(nameof this.HasNoFilteredVolumes)
            this.NotifyProperty(nameof this.HasSelectedContainerMounts)
            this.NotifyProperty(nameof this.SelectedContainerMountsText)

            let volAlerts, nextLevels =
                AlertMonitor.detectVolumeThresholdAlerts
                    VolumeParser.volumeWarningThresholdBytes
                    VolumeParser.volumeDangerThresholdBytes
                    previousVolumeAlertLevels
                    items
                    DateTimeOffset.UtcNow
            previousVolumeAlertLevels <- nextLevels
            for alert in volAlerts do
                this.RecordAndNotifyAlert(alert, true)
            this.PushMobileSnapshotIfRunning()
            return true
        | Error message ->
            this.ShowErrorStatus(localizeDockerError message)
            return false
    }

    /// <summary>イメージ一覧を再取得してコレクションを更新する。</summary>
    let refreshImagesCore () : Async<bool> = async {
        let! result =
            match imageListFetcher with
            | Some fetcher -> fetcher()
            | None -> docker.ListImages()
        match result with
        | Ok items ->
            let selectedId = if isNull selectedImage then None else Some selectedImage.Id
            let incomingIds = items |> List.map (fun item -> item.Id) |> Set.ofList
            updatingImageList <- true
            try
                for index = images.Count - 1 downto 0 do
                    if not (incomingIds.Contains(images.[index].Id)) then
                        images.RemoveAt(index)
                for item in items do
                    if not (images |> Seq.exists (fun current -> current.Id = item.Id)) then
                        images.Add(item)
            finally
                updatingImageList <- false
            match selectedId with
            | Some id when incomingIds.Contains(id) ->
                match images |> Seq.tryFind (fun item -> item.Id = id) with
                | Some current when not (obj.ReferenceEquals(selectedImage, current)) ->
                    this.SelectedImage <- current
                | _ -> ()
            | Some _ -> this.SelectedImage <- null
            | None -> ()
            let okMsg = format "{0}件のイメージ" "{0} images" [| box images.Count |]
            if refreshing then
                pendingRefreshStatus <- Some okMsg
                if not isErrorStatus then this.ShowStatus(okMsg)
            else
                this.ShowStatus(okMsg)
                this.SetConnectionDetails("")
            return true
        | Error message ->
            this.ShowErrorStatus(localizeDockerError message)
            return false
    }

    /// <summary>登録済み Compose プロジェクトの稼働状態を更新し、異常があれば通知を発行する。</summary>
    let refreshComposeStatuses () : Async<bool> = async {
        let mutable allSucceeded = true
        for project in composeProjects |> Seq.toArray do
            project.LanguageCode <- languageCode
            let! result =
                match composeStatusFetcher with
                | Some fetcher -> fetcher project.FolderPath
                | None -> composeCli.GetComposeStatus(project.FolderPath)
            match result with
            | Ok snapshot ->
                project.UpdateSnapshot(snapshot)
            | Error message ->
                allSucceeded <- false
                project.UpdateStatus("unknown", 0)
                this.ShowErrorStatus(localizeDockerError message)
        let alerts =
            composeProjects
            |> Seq.filter (fun project -> project.StatusKey = "partial" || (project.StatusKey = "stopped" && project.TotalContainerCount > 0))
            |> Seq.map (fun project ->
                project,
                project.FolderPath, localize "Compose異常" "Compose warning",
                sprintf "%s: %s (%s) %s" project.Name project.Status project.RunningCountLabel project.Warning)
            |> Seq.toList
        let rawAlerts = alerts |> List.map (fun (_, key, title, msg) -> key, title, msg)
        for notification in composeAlerts.Update(notificationsEnabled, rawAlerts) do
            alertRaised.Trigger(notification)
            let (title, msg) = notification
            this.SendDesktopNotification(title, msg)
        for (key, _, msg) in composeHistoryTracker.UpdateDetailed(true, rawAlerts) do
            let projName =
                alerts
                |> List.tryPick (fun (p, k, _, _) -> if k = key then Some p.Name else None)
                |> Option.defaultValue "Compose"
            let entry =
                {
                    Id = 0L
                    OccurredAt = DateTimeOffset.UtcNow.ToString("o")
                    Severity = AlertSeverity.Warning
                    Category = AlertCategory.ComposeAnomaly
                    TargetId = key
                    TargetName = projName
                    TitleKey = "Compose異常"
                    Message = msg
                }
            this.RecordAndNotifyAlert(entry, false)
        if allSucceeded && not refreshing then
            this.ClearErrorStatus()
            if dockerDesktopState = "running" then this.SetConnectionDetails("")
        return allSucceeded
    }

    /// <summary>選択中接続先のコンテナエンジン疎通を確認し、切断時は一覧をクリアする。</summary>
    let refreshDockerEngineStatus () = async {
        let! result =
            match connectionChecker with
            | Some checker -> checker()
            | None -> docker.CheckConnection()
        if connectionId <> "local" then
            match settings.SshConnections |> Seq.tryFind (fun connection -> connection.Id = connectionId) with
            | Some connection ->
                match result with
                | Ok () ->
                    connection.ConnectionState <- localize "正常" "Connected"
                    if dockerDesktopState <> "running" then
                        connection.LastConnectedAt <- DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
                        AppSettings.Save(settings)
                | Error _ -> connection.ConnectionState <- localize "接続失敗" "Connection failed"
            | None -> ()
        match result with
        | Ok () ->
            this.SetConnectionDetails("")
            this.SetDockerDesktopState("running")
            return true
        | Error(state, details) ->
            this.SetConnectionDetails(details)
            this.SetDockerDesktopState(if desktopLaunching then "starting" else state)
            stopLogFollow()
            let composeUsesSameEngine = connectionId <> "local" || localComposeRuntime = runtime
            if composeUsesSameEngine then stopComposeLogFollow()
            containers.Clear()
            images.Clear()
            volumes.Clear()
            filteredVolumes.Clear()
            stats.Clear()
            resourceAlerts.Reset()
            resourceHistoryTracker.Reset()
            if composeUsesSameEngine then
                composeAlerts.Reset()
                composeHistoryTracker.Reset()
            containerMonitorBaselineDone <- false
            previousContainerSnapshots <- Map.empty
            previousVolumeAlertLevels <- Map.empty
            this.SelectedContainer <- null
            this.SelectedImage <- null
            this.SelectedVolume <- null
            this.NotifyProperty(nameof this.VolumeSummaryText)
            this.NotifyProperty(nameof this.UnusedVolumeSummaryText)
            this.NotifyProperty(nameof this.HasVolumes)
            this.NotifyProperty(nameof this.HasNoFilteredVolumes)
            if composeUsesSameEngine then
                for project in composeProjects do project.UpdateStatus("unknown", 0)
            this.PushMobileSnapshotIfRunning()
            this.ShowStatus("")
            return false
    }

    /// <summary>コンテナごとの CPU・メモリ・ネットワーク統計を更新し、しきい値超過時に通知を発行する。</summary>
    let refreshStatsCore () : Async<bool> = async {
        let! result =
            match statsFetcher with
            | Some fetcher -> fetcher()
            | None -> docker.ListStats()
        match result with
        | Ok items ->
            let incomingMap = items |> List.map (fun (item: ContainerStats) -> item.Container, item) |> Map.ofList
            for index = stats.Count - 1 downto 0 do
                if not (incomingMap.ContainsKey(stats.[index].Container)) then
                    stats.RemoveAt(index)
            for (item: ContainerStats) in items do
                item.LanguageCode <- languageCode
                match stats |> Seq.tryFind (fun existing -> existing.Container = item.Container) with
                | Some existing -> existing.UpdateFrom(item)
                | None -> stats.Add(item)
            for container in containers do
                container.UpdateResources(items |> List.tryFind (fun (item: ContainerStats) -> item.Container = container.Name))
            let alerts =
                items |> List.collect (fun (item: ContainerStats) ->
                    [ if item.CpuLevel = 2 then
                          yield "cpu:" + item.Container, localize "高CPU使用率" "High CPU usage", sprintf "%s: CPU %s" item.Container item.Cpu, item.Container, "高CPU使用率"
                      if item.MemoryLevel = 2 then
                          yield "memory:" + item.Container, localize "高メモリ使用率" "High memory usage", sprintf "%s: %s (%s)" item.Container item.Memory item.MemoryPercent, item.Container, "高メモリ使用率" ])
            let rawAlerts = alerts |> List.map (fun (k, t, m, _, _) -> k, t, m)
            for notification in resourceAlerts.Update(notificationsEnabled, rawAlerts) do
                alertRaised.Trigger(notification)
                let (title, msg) = notification
                this.SendDesktopNotification(title, msg)
            for (key, _, msg) in resourceHistoryTracker.UpdateDetailed(true, rawAlerts) do
                let targetName, titleKey =
                    alerts
                    |> List.tryPick (fun (k, _, _, cName, tKey) -> if k = key then Some(cName, tKey) else None)
                    |> Option.defaultValue ("", "警告")
                let entry =
                    {
                        Id = 0L
                        OccurredAt = DateTimeOffset.UtcNow.ToString("o")
                        Severity = AlertSeverity.Warning
                        Category = AlertCategory.HighResourceUsage
                        TargetId = key
                        TargetName = targetName
                        TitleKey = titleKey
                        Message = msg
                    }
                this.RecordAndNotifyAlert(entry, false)
            this.PushMobileSnapshotIfRunning()
            return true
        | Error message ->
            stats.Clear()
            for container in containers do container.UpdateResources(None)
            this.ShowErrorStatus(localizeDockerError message)
            return false
    }

    /// <summary>ダッシュボード全体（接続状態・コンテナ・イメージ・ボリューム・Compose・統計）を一括更新する。</summary>
    let refreshDashboard includeImages = async {
        if not refreshing then
            refreshing <- true
            let completion = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            refreshCompletion <- Some completion
            pendingConnectionDetails <- None
            pendingRefreshStatus <- None
            this.NotifyActivity()
            try
                let wasConnected = dockerDesktopState = "running"
                let! connected = refreshDockerEngineStatus()
                if connected then
                    let! containersOk = refreshCore()
                    let! imagesOk =
                        if includeImages || not wasConnected then refreshImagesCore()
                        else async.Return true
                    volumeRefreshTickCount <- volumeRefreshTickCount + 1
                    let shouldRefreshVolumes = includeImages || not wasConnected || (volumeRefreshTickCount % 6 = 0)
                    let! volumesOk =
                        if shouldRefreshVolumes then refreshVolumesCore()
                        else async.Return true
                    let! composeOk = refreshComposeStatuses()
                    let! statsOk = refreshStatsCore()
                    this.PushMobileSnapshotIfRunning()
                    if containersOk && imagesOk && volumesOk && composeOk && statsOk then
                        match pendingRefreshStatus with
                        | Some msg -> this.ShowStatus(msg)
                        | None -> this.ClearErrorStatus()
                        this.SetConnectionDetails("")
                elif connectionId = "local" && localComposeRuntime <> runtime then
                    let! composeOk = refreshComposeStatuses()
                    if composeOk then
                        this.ClearErrorStatus()
            finally
                try
                    refreshing <- false
                    pendingRefreshStatus <- None
                    pendingConnectionDetails |> Option.iter this.SetConnectionDetails
                    pendingConnectionDetails <- None
                    this.NotifyActivity()
                finally
                    refreshCompletion <- None
                    completion.TrySetResult(()) |> ignore
    }

    /// <summary>実行中の定期更新が完了するまで待機する。</summary>
    let waitForRefresh () = async {
        match refreshCompletion with
        | Some completion -> do! completion.Task |> Async.AwaitTask
        | None -> ()
    }

    /// <summary>選択中のコンテナに対して起動・停止・再起動・削除を実行する。</summary>
    let runAction action = async {
        match selectedContainer with
        | null -> ()
        | container when container.IsOperating -> ()
        | container ->
            container.IsOperating <- true
            this.SetBusy(true)
            try
                do! waitForRefresh()
                let current = containers |> Seq.tryFind (fun item -> item.Id = container.Id)
                match current with
                | None -> this.ShowStatus(localize "選択したコンテナは一覧にありません。" "The selected container is no longer available.")
                | Some _ when dockerDesktopState <> "running" ->
                    this.ShowErrorStatus(localize "コンテナエンジンに接続できません。" "Cannot connect to the container engine.")
                | Some item when action = "削除" &&
                                 not (item.Status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase)
                                      || item.Status.StartsWith("Created", StringComparison.OrdinalIgnoreCase)) ->
                    this.ShowStatus(localize "停止済みコンテナのみ削除できます" "Only stopped containers can be removed.")
                | Some _ ->
                    let! result =
                        match containerActionRunner with
                        | Some runner -> runner action container.Id
                        | None -> docker.RunContainerAction(action, container.Id)
                    let opKey = OperationLog.mapContainerAction action
                    match result with
                    | Ok _ ->
                        OperationLogRepository.Record(container.Id, container.Name, opKey, true)
                        this.RefreshSelectedContainerOperationLogs()
                        this.SetConnectionDetails("")
                        if action = "削除" then stopLogFollow()
                        let englishAction =
                            match action with
                            | "起動" -> "started"
                            | "停止" -> "stopped"
                            | "再起動" -> "restarted"
                            | _ -> "removed"
                        this.ShowStatus(format "コンテナ {0} に{1}しました" "Container {0} {1}." [| box container.Name; box (localize action englishAction) |])
                        let! _ = refreshCore()
                        ()
                    | Error message ->
                        OperationLogRepository.Record(container.Id, container.Name, opKey, false, detail = message)
                        this.RefreshSelectedContainerOperationLogs()
                        this.ShowErrorStatus(localizeDockerError message)
            finally
                container.IsOperating <- false
                this.SetBusy(false)
    }

    /// <summary>入力されたイメージ参照を pull して一覧を更新する。</summary>
    let pullImage () = async {
        let imageReference = imagePullReference.Trim()
        if isPullingImage then ()
        elif String.IsNullOrWhiteSpace(imageReference) then
            this.ShowStatus(localize "取得するイメージ名を入力してください" "Enter an image name to pull.")
        else
            isPullingImage <- true
            this.SetBusy(true)
            this.ShowStatus(format "{0} を取得中..." "Pulling {0}..." [| box imageReference |])
            try
                do! waitForRefresh()
                let! result = docker.PullImage(imageReference, fun line ->
                    Dispatcher.UIThread.Post(Action(fun () -> this.ShowStatus(line))))
                match result with
                | Ok () ->
                    this.SetConnectionDetails("")
                    this.ShowStatus(format "{0} の取得が完了しました" "Pulled {0} successfully." [| box imageReference |])
                    let! _ = refreshImagesCore()
                    ()
                | Error message -> this.ShowErrorStatus(localizeDockerError message)
            finally
                isPullingImage <- false
                this.SetBusy(false)
    }

    /// <summary>選択中のイメージを削除して一覧を更新する。</summary>
    let removeImage () = async {
        match selectedImage with
        | null -> ()
        | image when image.IsOperating -> ()
        | image ->
            image.IsOperating <- true
            this.SetBusy(true)
            try
                do! waitForRefresh()
                if images |> Seq.exists (fun item -> item.Id = image.Id) then
                    let! result = docker.RemoveImage(image.Id)
                    match result with
                    | Ok _ ->
                        this.SetConnectionDetails("")
                        this.ShowStatus(format "イメージ {0} を削除しました" "Removed image {0}." [| box image.Reference |])
                        let! _ = refreshImagesCore()
                        ()
                    | Error message -> this.ShowErrorStatus(localizeDockerError message)
                else
                    this.ShowStatus(localize "選択したイメージは一覧にありません。" "The selected image is no longer available.")
            finally
                image.IsOperating <- false
                this.SetBusy(false)
    }

    /// <summary>選択中の Compose プロジェクトのログを取得（tail 指定可）または追尾開始する。</summary>
    let loadComposeLogs tail project = async {
        stopComposeLogFollow()
        cancelComposeLogFetch()
        composeLogRequestVersion <- composeLogRequestVersion + 1
        let requestVersion = composeLogRequestVersion
        if followComposeLogs then
            this.ShowComposeLogs(localize "ログを追尾中..." "Following logs...")
            let onLine line =
                Dispatcher.UIThread.Post(Action(fun () ->
                    if selectedComposeProject = project && followComposeLogs then this.AppendComposeLog(line)))
            match composeCli.StartComposeLogFollow(project.FolderPath, onLine) with
            | Ok streamProcess -> composeLogProcess <- Some streamProcess
            | Error message -> this.ShowErrorStatus(localizeDockerError message)
        else
            let cts = new System.Threading.CancellationTokenSource()
            composeLogCts <- Some cts
            try
                let fetcher =
                    match composeLogsFetcher with
                    | Some customFetcher -> customFetcher
                    | None -> fun path limit token -> composeCli.GetComposeLogs(path, ?tail = limit, cancellationToken = token)
                let! result = fetcher project.FolderPath tail cts.Token
                let isCurrentRequest =
                    composeLogRequestVersion = requestVersion
                    && obj.ReferenceEquals(selectedComposeProject, project)
                    && not followComposeLogs
                    && not cts.IsCancellationRequested
                if isCurrentRequest then
                    match result with
                    | Ok output ->
                        this.ShowComposeLogs(if String.IsNullOrWhiteSpace(output) then localize "ログはありません。" "No logs available." else output)
                        this.ShowStatus(format "{0} のComposeログを表示中" "Showing Compose logs for {0}." [| box project.Name |])
                    | Error message when message = "cancelled" -> ()
                    | Error message -> this.ShowErrorStatus(localizeDockerError message)
            finally
                match composeLogCts with
                | Some current when obj.ReferenceEquals(current, cts) -> composeLogCts <- None
                | _ -> ()
                cts.Dispose()
    }

    /// <summary>選択中の Compose プロジェクトに対して起動・停止・再起動・ログ表示を実行する。</summary>
    let runComposeAction action = async {
        match selectedComposeProject with
        | null -> ()
        | project when project.IsOperating -> ()
        | project ->
            project.IsOperating <- true
            this.SetBusy(true)
            try
                do! waitForRefresh()
                if not (composeProjects.Contains(project)) then
                    this.ShowStatus(localize "選択したプロジェクトは一覧にありません。" "The selected project is no longer available.")
                elif action = "ログ" then
                    do! loadComposeLogs None project
                else
                    if action = "停止" then stopComposeLogFollow()
                    let! result =
                        match composeActionRunner with
                        | Some runner -> runner action project.FolderPath
                        | None ->
                            if action = "起動" || action = "停止" then
                                composeCli.RunComposeActionWithProgress(action, project.FolderPath, fun line ->
                                    Dispatcher.UIThread.Post(Action(fun () -> this.ShowStatus(line))))
                            else
                                async {
                                    let! output = composeCli.RunComposeAction(action, project.FolderPath)
                                    return output |> Result.map ignore
                                }
                    let opKey = OperationLog.mapComposeAction action
                    let targetId = OperationLog.composeTargetId project.Id
                    match result with
                    | Ok () ->
                        OperationLogRepository.Record(targetId, project.Name, opKey, true)
                        this.RefreshSelectedComposeOperationLogs()
                        this.SetConnectionDetails("")
                        let englishAction = if action = "起動" then "started" elif action = "停止" then "stopped" else "restarted"
                        this.ShowStatus(format "{0} を{1}しました" "{0} {1}." [| box project.Name; box (localize action englishAction) |])
                        let! _ = refreshComposeStatuses()
                        ()
                    | Error message ->
                        OperationLogRepository.Record(targetId, project.Name, opKey, false, detail = message)
                        this.RefreshSelectedComposeOperationLogs()
                        this.ShowErrorStatus(localizeDockerError message)
            finally
                project.IsOperating <- false
                this.SetBusy(false)
    }

    do
        connectionOptions.Add(ChoiceOption("local", localize "ローカル" "Local"))
        for connection in settings.SshConnections do connectionOptions.Add(ChoiceOption(connection.Id, connection.Name))
        this.RebuildFaqCategoryOptions()
        alertHistoryEntries <- AlertHistoryRepository.GetRecent()
        this.RefreshLanIpCandidates()
        mobileServer.SessionStateChanged.Add(fun st -> postOnUI (fun () -> this.ApplyMobileSessionStatus(st)))
        this.ApplyMobileSessionStatus(mobileServer.GetStatus())
        timer.Tick.Add(fun _ -> if not refreshing then refreshDashboard false |> Async.StartImmediate)
        if defaultArg startAutomatically true then
            if autoRefresh then timer.Start()
            refreshDashboard true |> Async.StartImmediate
            if MasterDataRepository.ShouldAutoFetch(DateTimeOffset.UtcNow) then
                this.UpdateMasterDataAsync(silent = true) |> Async.StartImmediate

    /// <summary>画面に一覧表示されるコンテナコレクション。</summary>
    member internal this.NotifyProperty(name: string) = this.OnPropertyChanged(name)

    member _.Containers = containers

    /// <summary>画面に一覧表示されるコンテナイメージコレクション。</summary>
    member _.Images = images

    /// <summary>画面に一覧表示されるコンテナ統計情報（CPU・メモリ・Net I/O）コレクション。</summary>
    member _.Stats = stats

    /// <summary>画面に一覧表示される Compose プロジェクトコレクション。</summary>
    member _.ComposeProjects = composeProjects

    /// <summary>実行中 OS が Windows かどうか。</summary>
    member _.IsWindows = isWindows

    /// <summary>現在選択されている接続先がローカル（Local）かどうか。</summary>
    member _.IsLocal = connectionId = "local"

    /// <summary>Compose で選択可能なコンテナエンジンの選択肢一覧。</summary>
    member _.ComposeEngineOptions = sshEngineOptions

    /// <summary>Compose エンジンを変更可能かどうか（ローカルかつ未処理中のみ）。</summary>
    member _.CanChangeComposeEngine = this.IsLocal && this.CanChangeConnection

    /// <summary>Compose 用に選択されているコンテナエンジン。</summary>
    member this.SelectedComposeEngine
        with get () =
            let engine = if connectionId = "local" then localComposeRuntime else activeRuntime
            sshEngineOptions |> Array.find (fun option -> option.Key = ContainerRuntime.command engine)
        and set (value: ChoiceOption) =
            if not (isNull value) && this.CanChangeComposeEngine then
                match ContainerRuntime.all |> List.tryFind (fun engine -> ContainerRuntime.command engine = value.Key) with
                | Some engine when engine <> localComposeRuntime ->
                    stopComposeLogFollow()
                    cancelComposeLogFetch()
                    composeLogRequestVersion <- composeLogRequestVersion + 1
                    this.SelectedComposeProject <- null
                    localComposeRuntime <- engine
                    composeCli <- ContainerCli(runtime = engine)
                    composeProjects.Clear()
                    for project in settings.ComposeProjects do
                        if isComposeInScope project then composeProjects.Add(project)
                    composeAlerts.Reset()
                    this.NewComposeName <- ""
                    this.NewComposePath <- ""
                    this.NewComposeUrl <- ""
                    this.ShowComposeLogs(localize "Composeプロジェクトを選択してください。" "Select a Compose project.")
                    if defaultArg startAutomatically true then async { let! _ = refreshComposeStatuses() in () } |> Async.StartImmediate
                | _ -> ()
            this.OnPropertyChanged(nameof this.SelectedComposeEngine)

    /// <summary>コンテナエンジンの起動ボタンを表示するかどうか。</summary>
    member _.ShowContainerEngineStart =
        connectionId = "local"
        && runtime <> Nerdctl
        && (isWindows || (runtime = Podman && OperatingSystem.IsMacOS()))
        && (dockerDesktopState = "stopped"
            || dockerDesktopState = "unreachable"
            || dockerDesktopState = "starting"
            || dockerDesktopState = "failed")

    /// <summary>接続先の切り替えが可能かどうか（更新中・処理中・起動中以外）。</summary>
    member _.CanChangeConnection = not refreshing && not isBusy && not desktopLaunching

    /// <summary>接続先（ローカル / SSH 各ホスト）の選択肢一覧。</summary>
    member _.Connections = connectionOptions

    /// <summary>設定に保存されている SSH 接続プロファイル一覧。</summary>
    member _.SshConnections = settings.SshConnections

    /// <summary>リソース使用率異常や Compose 異常が検出された際に発火するイベント。</summary>
    member _.AlertRaised = alertRaised.Publish

    /// <summary>現在選択されている接続先プロファイル（ローカルまたは SSH 接続）。</summary>
    member this.SelectedConnection
        with get () = connectionOptions |> Seq.find (fun option -> option.Key = connectionId)
        and set (value: ChoiceOption) =
            if not (isNull value) && value.Key <> connectionId then
                if this.CanChangeConnection then
                    if connectionId <> "local" then
                        settings.SshConnections
                        |> Seq.tryFind (fun item -> item.Id = connectionId)
                        |> Option.iter (fun item -> item.ConnectionState <- "")
                    stopLogFollow()
                    stopComposeLogFollow()
                    cancelComposeLogFetch()
                    composeLogRequestVersion <- composeLogRequestVersion + 1
                    this.SelectedContainer <- null
                    this.SelectedImage <- null
                    this.SelectedComposeProject <- null
                    connectionId <- value.Key
                    if connectionId = "local" then
                        activeRuntime <- runtime
                        docker <- ContainerCli(runtime = activeRuntime)
                        composeCli <- ContainerCli(runtime = localComposeRuntime)
                    else
                        let connection = settings.SshConnections |> Seq.find (fun item -> item.Id = connectionId)
                        activeRuntime <-
                            ContainerRuntime.all
                            |> List.tryFind (fun engine -> ContainerRuntime.command engine = connection.Engine)
                            |> Option.defaultValue runtime
                        docker <- ContainerCli(connection, runtime = activeRuntime)
                        composeCli <- docker
                    containers.Clear()
                    images.Clear()
                    volumes.Clear()
                    filteredVolumes.Clear()
                    stats.Clear()
                    composeProjects.Clear()
                    for project in settings.ComposeProjects do
                        if isComposeInScope project then composeProjects.Add(project)
                    resourceAlerts.Reset()
                    composeAlerts.Reset()
                    resourceHistoryTracker.Reset()
                    composeHistoryTracker.Reset()
                    containerMonitorBaselineDone <- false
                    previousContainerSnapshots <- Map.empty
                    previousVolumeAlertLevels <- Map.empty
                    latestDiagnosticsSummaries <- Map.empty
                    this.SelectedVolume <- null
                    this.NotifyProperty(nameof this.VolumeSummaryText)
                    this.NotifyProperty(nameof this.UnusedVolumeSummaryText)
                    this.NotifyProperty(nameof this.HasVolumes)
                    this.NotifyProperty(nameof this.HasNoFilteredVolumes)
                    this.PushMobileSnapshotIfRunning()
                    this.NewComposeName <- ""
                    this.NewComposePath <- ""
                    this.NewComposeUrl <- ""
                    this.SetDockerDesktopState("stopped")
                    this.OnPropertyChanged(nameof this.IsLocal)
                    this.OnPropertyChanged(nameof this.CanChangeComposeEngine)
                    this.OnPropertyChanged(nameof this.SelectedComposeEngine)
                    this.OnPropertyChanged(nameof this.ShowContainerEngineStart)
                    this.OnPropertyChanged(nameof this.RuntimeModeLabel)
                    refreshDashboard true |> Async.StartImmediate
                this.OnPropertyChanged(nameof this.SelectedConnection)

    /// <summary>コンテナエンジンの稼働状態ラベル文字列（アイコン付き）。</summary>
    member _.ContainerEngineStatus = dockerDesktopStatus

    /// <summary>コンテナエンジンを画面から起動可能かどうか。</summary>
    member _.CanStartContainerEngine = this.ShowContainerEngineStart && not desktopLaunching && dockerDesktopState <> "running" && dockerDesktopState <> "starting"

    /// <summary>接続エラーや詳細情報のログ文字列。</summary>
    member _.ConnectionDetails = connectionDetails

    /// <summary>接続詳細ログが存在するかどうか。</summary>
    member _.HasConnectionDetails = not (String.IsNullOrWhiteSpace(connectionDetails))

    /// <summary>現在の接続状態やエラー原因に応じたトラブルシューティングガイド文字列。</summary>
    member _.ConnectionHelp =
        if connectionId <> "local" && dockerDesktopState <> "running" then
            localize "SSH接続先・秘密鍵・ssh-agentサービスの状態・known_hosts・リモートコンテナエンジンの起動状態を確認してください。" "Check the SSH host, private key, ssh-agent service status, known_hosts, and remote container engine."
        else
            match dockerDesktopState with
            | "running" -> ""
            | "cliMissing" | "missing" ->
                match runtime with
                | Docker when isWindows ->
                    localize "Docker Desktopの導入とPATH設定を確認してください。" "Check that Docker Desktop is installed and available on PATH."
                | Docker ->
                    localize "Docker CLIの導入とPATH設定を確認してください。" "Check that the Docker CLI is installed and available on PATH."
                | Podman ->
                    localize "Podman CLIの導入とPATH設定を確認してください。" "Check that the Podman CLI is installed and available on PATH."
                | Nerdctl ->
                    localize "nerdctl CLIの導入とPATH設定を確認してください。" "Check that the nerdctl CLI is installed and available on PATH."
            | "permission" ->
                match runtime with
                | Docker -> localize "Dockerへのアクセス権限を確認してください。" "Check your permission to access Docker."
                | Podman -> localize "Podmanへのアクセス権限を確認してください。" "Check your permission to access Podman."
                | Nerdctl -> localize "nerdctlへのアクセス権限を確認してください。" "Check your permission to access nerdctl."
            | _ ->
                if runtime = Podman && (isWindows || OperatingSystem.IsMacOS()) then
                    localize "Podman machineを起動してください。" "Start the Podman machine."
                elif runtime = Docker && isWindows then
                    localize "Docker Desktopを起動してください。起動済みの場合は接続先設定を確認してください。" "Start Docker Desktop. If it is already running, check your Docker context."
                else
                    localize "コンテナエンジンの起動状態と接続先設定を確認してください。" "Check that the container engine is running and verify the connection settings."
    member _.DetailsLabel = localize "詳細を表示" "Show details"
    member this.DetailsExpanded
        with get () = detailsExpanded
        and set value = this.SetProperty(&detailsExpanded, value) |> ignore

    member _.Languages = languageOptions

    member this.Language
        with get () = languageOptions |> Array.find (fun option -> option.Key = languageCode)
        and set (option: ChoiceOption) =
            if not (isNull option) && languageCode <> option.Key then
                languageCode <- Localization.normalizeCode(option.Key)
                selectedFaqLangKey <- FaqData.resolveFaqLanguage languageCode
                this.OnPropertyChanged(nameof this.Language)
                settings.Language <- languageCode
                AppSettings.Save(settings)
                for container in containers do container.LanguageCode <- languageCode
                for vol in volumes do vol.LanguageCode <- languageCode
                for project in composeProjects do project.LanguageCode <- languageCode
                for item in stats do item.LanguageCode <- languageCode
                connectionOptions.[0].Label <- localize "ローカル" "Local"
                themeOptions.[0].Label <- localize "OS設定" "System"
                themeOptions.[1].Label <- localize "ライト" "Light"
                themeOptions.[2].Label <- localize "ダーク" "Dark"
                for propertyName in
                    [ nameof this.IsEnglish; nameof this.ThemeOptions; nameof this.Theme
                      nameof this.AppTitle; nameof this.AppSubtitle
                      nameof this.RuntimeModeLabel; nameof this.ShowContainerEngineStart; nameof this.CanStartContainerEngine
                      nameof this.ConnectionHelp; nameof this.DetailsLabel
                      nameof this.ConnectionLabel; nameof this.SshLabel; nameof this.HostLabel; nameof this.PortLabel
                      nameof this.EngineLabel; nameof this.LastConnectedLabel
                      nameof this.UserLabel; nameof this.PrivateKeyLabel; nameof this.PrivateKeyHintLabel; nameof this.PrivateKeyPlaceholder
                      nameof this.TestConnectionLabel
                      nameof this.SaveSshLabel; nameof this.NewSshLabel
                      nameof this.NotificationsLabel; nameof this.WarningLabel; nameof this.MemoryPercentLabel
                      nameof this.RefreshLabel; nameof this.RefreshIntervalLabel; nameof this.TabContainersLabel
                      nameof this.TabImagesLabel; nameof this.TabVolumesLabel; nameof this.TabComposeLabel
                      nameof this.ContainerNameLabel; nameof this.StateLabel; nameof this.PublishedPortsLabel
                      nameof this.ImageLabel; nameof this.CreatedAtLabel
                      nameof this.StartLabel; nameof this.StopLabel; nameof this.RestartLabel
                      nameof this.EnterContainerLabel; nameof this.TerminalLabel
                      nameof this.DeleteLabel; nameof this.FollowLogsLabel; nameof this.ContainerLogsLabel
                      nameof this.ImagePullPlaceholder; nameof this.PullImageLabel
                      nameof this.RepositoryLabel; nameof this.TagLabel; nameof this.SizeLabel
                      nameof this.DeleteImageLabel; nameof this.ComposeNamePlaceholder
                      nameof this.ComposePathPlaceholder; nameof this.BrowseFolderLabel
                      nameof this.RegisterLabel; nameof this.BrowserUrlPlaceholder
                      nameof this.NameLabel; nameof this.FolderPathLabel
                      nameof this.ComposeStatusLabel; nameof this.ComposeStartLabel
                      nameof this.ComposeStopLabel; nameof this.ComposeRestartLabel
                      nameof this.ComposeLogsLabel; nameof this.BrowserButtonLabel
                      nameof this.RemoveProjectLabel; nameof this.DesktopStartButtonLabel
                      nameof this.FolderPickerTitle; nameof this.MonitoringLabel; nameof this.DockerStatsLabel
                      nameof this.CpuLabel; nameof this.MemoryLabel; nameof this.NetworkLabel
                      nameof this.ComposeRunningCountLabel; nameof this.FollowComposeLogsLabel
                      nameof this.InferredServicesLabel; nameof this.InferredServicesNote; nameof this.InferredServicesText
                      nameof this.InferenceDialogTitle; nameof this.InferenceImageHeader; nameof this.InferencePortsHeader
                      nameof this.InferenceNoMaterialText; nameof this.InferenceDialogNote; nameof this.CloseLabel
                      nameof this.InferenceDetailButtonLabel; nameof this.ImageCandidateText; nameof this.PortCandidatesTexts
                      nameof this.OperationLogHeader; nameof this.OperationLogEmptyText
                      nameof this.SelectedContainerOperationLogs; nameof this.SelectedComposeOperationLogs
                      nameof this.MountedVolumesLabel; nameof this.SelectedContainerMountsText ] do
                    this.OnPropertyChanged(propertyName)
                this.NotifyDiagnosticsProperties()
                this.NotifyInfoProperties()
                this.NotifyV12Properties()
                this.ApplyMobileSessionStatus(mobileServer.GetStatus())
                this.PushMobileSnapshotIfRunning()
                if isNull selectedContainer then
                    this.ShowLogs(localize "ログを表示するコンテナを選択してください。" "Select a container to view its logs.")
                if isNull selectedComposeProject then
                    this.ShowComposeLogs(localize "Composeプロジェクトを選択してください。" "Select a Compose project.")
                this.RefreshDockerDesktopState()
                this.ShowStatus(localize "表示言語を日本語にしました" "Language changed to English")

    member _.IsEnglish = languageCode = "en-US"
    member _.AppTitle = localize "簡単コンテナ管理" "Easy Container Manager"
    member _.AppSubtitle = localize "ローカル＆リモート（SSH）のコンテナ管理" "Manage local & remote (SSH) containers"
    member _.RefreshLabel = localize "更新" "Refresh"
    member _.TabContainersLabel = localize "コンテナ" "Containers"
    member _.TabImagesLabel = localize "イメージ" "Images"
    member _.TabComposeLabel = "Compose"
    member _.ContainerNameLabel = localize "コンテナ名" "Container"
    member _.StateLabel = localize "状態" "Status"
    member _.PublishedPortsLabel = localize "公開ポート" "Published ports"
    member _.ImageLabel = localize "イメージ" "Image"
    member _.CreatedAtLabel = localize "作成日時" "Created"
    member _.StartLabel = localize "起動" "Start"
    member _.StopLabel = localize "停止" "Stop"
    member _.RestartLabel = localize "再起動" "Restart"
    member _.EnterContainerLabel = localize "コンテナに入る" "Enter container"
    member _.TerminalLabel = localize "ターミナル" "Terminal"
    member _.DeleteLabel = localize "削除" "Delete"
    member _.FollowLogsLabel = localize "ログを追尾" "Follow logs"
    member _.ContainerLogsLabel = localize "コンテナログ" "Container logs"
    member _.OperationLogHeader = localize "操作履歴" "Operation log"
    member _.OperationLogEmptyText = localize "操作履歴はまだありません。" "No operations recorded yet."
    member _.SelectedContainerOperationLogs : OperationLogDisplayItem list =
        selectedContainerOperationLogs |> List.map (fun entry -> OperationLogDisplayItem(entry, languageCode))
    member _.HasSelectedContainerOperationLogs = not (List.isEmpty selectedContainerOperationLogs)
    member _.HasNoSelectedContainerOperationLogs = not (isNull selectedContainer) && List.isEmpty selectedContainerOperationLogs
    member _.SelectedComposeOperationLogs : OperationLogDisplayItem list =
        selectedComposeOperationLogs |> List.map (fun entry -> OperationLogDisplayItem(entry, languageCode))
    member _.HasSelectedComposeOperationLogs = not (List.isEmpty selectedComposeOperationLogs)
    member _.HasNoSelectedComposeOperationLogs = not (isNull selectedComposeProject) && List.isEmpty selectedComposeOperationLogs
    member _.InferredServicesLabel = localize "推定サービス" "Likely services"
    member _.InferredServicesNote = localize "イメージ名とポートからの推定です。実際と異なる場合があります" "Inferred from the image name and ports; may differ from the actual services."
    member this.HasInferredServices = not (isNull selectedContainer) && not (String.IsNullOrWhiteSpace(this.InferredServicesText))
    member _.InferredServicesText =
        match selectedContainer with
        | null -> ""
        | container ->
            let inferred = ServiceInference.infer container.Image container.PortBindings
            ServiceInference.formatAll languageCode inferred

    /// <summary>サービス推定ダイアログが開いているかどうか。</summary>
    member this.IsInferenceDialogOpen
        with get () = isInferenceDialogOpen
        and set value = this.SetProperty(&isInferenceDialogOpen, value) |> ignore

    /// <summary>コンテナが選択されているかどうか。</summary>
    member _.HasSelectedContainer = not (isNull selectedContainer)

    /// <summary>サービス推定ダイアログを開く。</summary>
    member this.OpenInferenceDialog() =
        if not (isNull selectedContainer) then
            this.IsInferenceDialogOpen <- true

    /// <summary>サービス推定ダイアログを閉じる。</summary>
    member this.CloseInferenceDialog() =
        this.IsInferenceDialogOpen <- false

    member private _.CurrentCandidates =
        match selectedContainer with
        | null -> { FromImage = None; FromPorts = [] }
        | c -> ServiceInference.inferCandidates c.Image c.PortBindings

    member this.HasImageCandidate =
        this.CurrentCandidates.FromImage.IsSome

    member this.ImageCandidateText =
        match selectedContainer with
        | null -> ""
        | c ->
            match this.CurrentCandidates.FromImage with
            | Some candidate -> ServiceInference.formatCandidateImage languageCode c.Image candidate
            | None -> ""

    member this.HasPortCandidates =
        this.CurrentCandidates.FromPorts.Length > 0

    member this.PortCandidatesTexts =
        this.CurrentCandidates.FromPorts
        |> List.map (ServiceInference.formatCandidatePort languageCode)

    member this.HasNoInferenceCandidates =
        not this.HasImageCandidate && not this.HasPortCandidates

    member _.InferenceDialogTitle = localize "サービス推定の詳細" "Service inference details"
    member _.InferenceImageHeader = localize "イメージ名から" "From image name"
    member _.InferencePortsHeader = localize "ポートから" "From ports"
    member _.InferenceNoMaterialText = localize "推定できる材料がありません" "No material to infer from"
    member _.InferenceDialogNote = localize "候補の列挙です。実際の構成と異なる場合があります" "These are candidates only and may differ from the actual setup."
    member _.CloseLabel = localize "閉じる" "Close"
    member _.InferenceDetailButtonLabel = localize "サービス推定の詳細" "Service inference details"

    /// <summary>サービス診断ダイアログが開いているかどうか。</summary>
    member this.IsDiagnosticsDialogOpen
        with get () = isDiagnosticsDialogOpen
        and set value = this.SetProperty(&isDiagnosticsDialogOpen, value) |> ignore

    /// <summary>現在診断処理中かどうか。</summary>
    member this.IsDiagnosing
        with get () = isDiagnosing
        and set value = this.SetProperty(&isDiagnosing, value) |> ignore

    /// <summary>診断結果が取得済みかどうか。</summary>
    member _.HasDiagnosticsResult = currentDiagnosticsResult.IsSome

    /// <summary>所見が 1 件以上あるかどうか。</summary>
    member _.HasDiagnosticsFindings =
        match currentDiagnosticsResult with
        | Some r -> not (List.isEmpty r.Findings)
        | None -> false

    /// <summary>バージョン所見が 1 件以上あるかどうか。</summary>
    member _.HasDiagnosticsVersionFindings =
        not (List.isEmpty currentVersionFindings)

    /// <summary>所見が 0 件（目立つ異常なし）かどうか。</summary>
    member this.HasNoDiagnosticsFindings =
        match currentDiagnosticsResult with
        | Some r -> List.isEmpty r.Findings && List.isEmpty currentVersionFindings && not this.IsDiagnosing
        | None -> false

    /// <summary>所見一覧の表示用モデルリスト。</summary>
    member _.DiagnosticsFindingsList : FindingDisplayItem list =
        match currentDiagnosticsResult with
        | Some r -> r.Findings |> List.map (fun f -> FindingDisplayItem(f, languageCode))
        | None -> []

    /// <summary>バージョン診断所見の表示用モデルリスト。</summary>
    member _.DiagnosticsVersionFindingsList : FindingDisplayItem list =
        currentVersionFindings |> List.map (fun f -> FindingDisplayItem(f, languageCode))

    /// <summary>定義データの時点表示文字列。</summary>
    member _.DiagnosticsDefinitionDateText =
        match MasterDataRepository.GetDefinitionDateText masterDataSnapshot with
        | Some dateText -> format "定義データ: {0}時点" "Definitions: as of {0}" [| box dateText |]
        | None -> localize "定義データ: 未取得" "Definitions: not loaded"

    /// <summary>走査ログ行数テキスト（例: 走査ログ: 直近 1,000 行）。</summary>
    member _.DiagnosticsScannedLinesText =
        match currentDiagnosticsResult with
        | Some r ->
            let template = localize "走査ログ: 直近 {0} 行" "Scanned logs: Last {0} lines"
            String.Format(template, r.ScannedLines)
        | None -> ""

    /// <summary>対象サービス名テキスト（例: 対象サービス: Nginx (Web)）。</summary>
    member _.DiagnosticsTargetServiceText =
        match currentDiagnosticsResult with
        | Some r ->
            if List.isEmpty r.TargetServiceNames then
                localize "汎用チェックのみ" "Generic checks only"
            else
                let template = localize "対象サービス: {0}" "Target service: {0}"
                String.Format(template, String.concat ", " r.TargetServiceNames)
        | None -> ""

    member _.DiagnosticsDialogTitle = localize "コンテナ内サービス診断" "In-container service diagnostics"
    member _.DiagnosticsButtonLabel = localize "サービス診断" "Service diagnostics"
    member _.RunDiagnosticsLabel = localize "診断を実行" "Run diagnostics"
    member _.RerunDiagnosticsLabel = localize "再診断" "Re-diagnose"
    member _.DiagnosingLabel = localize "診断中..." "Diagnosing..."
    member _.DiagnosticsNote = localize "ログとコンテナ状態に基づく診断です。実際の設定や動作と異なる場合があります" "Diagnosed from logs and container state; may differ from actual setup or behavior."
    member _.DiagnosticsNoAnomalyTitle = localize "目立つ異常は見つかりませんでした" "No noticeable anomalies were found"
    member _.DiagnosticsNoAnomalyMessage = localize "走査ログおよびコンテナ状態に、既知のエラーパターンや再起動などの異常は記録されていません。" "No known error patterns, repeated restarts, or anomalies were found in the scanned logs or container state."
    member _.FindingsHeader = localize "所見一覧" "Findings"
    member _.VersionDiagnosticsHeader = localize "バージョン診断" "Version diagnostics"

    /// <summary>診断プロパティの一括更新通知を発行する。</summary>
    member this.NotifyDiagnosticsProperties() =
        this.OnPropertyChanged(nameof this.IsDiagnosticsDialogOpen)
        this.OnPropertyChanged(nameof this.IsDiagnosing)
        this.OnPropertyChanged(nameof this.HasDiagnosticsResult)
        this.OnPropertyChanged(nameof this.HasDiagnosticsFindings)
        this.OnPropertyChanged(nameof this.HasDiagnosticsVersionFindings)
        this.OnPropertyChanged(nameof this.HasNoDiagnosticsFindings)
        this.OnPropertyChanged(nameof this.DiagnosticsFindingsList)
        this.OnPropertyChanged(nameof this.DiagnosticsVersionFindingsList)
        this.OnPropertyChanged(nameof this.DiagnosticsDefinitionDateText)
        this.OnPropertyChanged(nameof this.DiagnosticsScannedLinesText)
        this.OnPropertyChanged(nameof this.DiagnosticsTargetServiceText)
        this.OnPropertyChanged(nameof this.DiagnosticsDialogTitle)
        this.OnPropertyChanged(nameof this.DiagnosticsButtonLabel)
        this.OnPropertyChanged(nameof this.RunDiagnosticsLabel)
        this.OnPropertyChanged(nameof this.RerunDiagnosticsLabel)
        this.OnPropertyChanged(nameof this.DiagnosingLabel)
        this.OnPropertyChanged(nameof this.DiagnosticsNote)
        this.OnPropertyChanged(nameof this.DiagnosticsNoAnomalyTitle)
        this.OnPropertyChanged(nameof this.DiagnosticsNoAnomalyMessage)
        this.OnPropertyChanged(nameof this.FindingsHeader)
        this.OnPropertyChanged(nameof this.VersionDiagnosticsHeader)

    /// <summary>サービス診断ダイアログを開き、最新状態の診断を開始する。</summary>
    member this.OpenDiagnosticsDialog() =
        if not (isNull selectedContainer) then
            this.IsDiagnosticsDialogOpen <- true
            this.RunDiagnostics()

    /// <summary>サービス診断ダイアログを閉じる。</summary>
    member this.CloseDiagnosticsDialog() =
        this.IsDiagnosticsDialogOpen <- false

    /// <summary>コンテナ内サービスの診断を実行する。</summary>
    member this.RunDiagnostics() =
        match selectedContainer with
        | null -> ()
        | container ->
            let containerId = container.Id
            let containerImage = container.Image
            let containerPorts = container.PortBindings
            this.IsDiagnosing <- true
            currentDiagnosticsResult <- None
            currentVersionFindings <- []
            this.NotifyDiagnosticsProperties()

            async {
                try
                    let! inspectResult = docker.GetInspect containerId
                    let inspectDataOpt =
                        match inspectResult with
                        | Ok inspectOutput -> ServiceDiagnostics.parseInspectJson inspectOutput
                        | Error _ -> None

                    let! logsResult = docker.GetLogs(containerId, tail = 1000)
                    let logLines =
                        match logsResult with
                        | Ok logsOutput ->
                            logsOutput.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) :> seq<string>
                        | Error _ -> Seq.empty

                    let candidates = ServiceInference.inferCandidates containerImage containerPorts
                    let fromImg = candidates.FromImage |> Option.map (fun (sId, _, _) -> sId) |> Option.toList
                    let fromPorts = candidates.FromPorts |> List.map (fun s -> s.ServiceId)
                    let targetServices = (fromImg @ fromPorts) |> List.distinct

                    let containerMounts = container.Mounts
                    let volumeSnapshotList = volumes |> Seq.map (fun v -> v.Snapshot) |> Seq.toList
                    let result = ServiceDiagnostics.diagnose inspectDataOpt targetServices logLines
                    let volumeFindings = VolumeParser.diagnoseContainerVolumes containerMounts volumeSnapshotList
                    let combinedFindings =
                        (result.Findings @ volumeFindings)
                        |> List.sortWith (fun a b ->
                            let rank = function FindingSeverity.Danger -> 0 | FindingSeverity.Warning -> 1 | FindingSeverity.Info -> 2
                            compare (rank a.Severity) (rank b.Severity))
                    let finalResult = { result with Findings = combinedFindings }

                    // ローカル SQLite のマスターデータを用いたバージョン診断
                    let snapshot = MasterDataRepository.GetSnapshot()
                    let! engineVersionRes = docker.GetEngineVersion()
                    let engineFindings =
                        match engineVersionRes with
                        | Ok versionOutput -> VersionDiagnostics.diagnoseEngine (ContainerRuntime.command activeRuntime) versionOutput snapshot
                        | Error _ -> []
                    let today = DateOnly.FromDateTime(DateTime.Now)
                    let runtimeFindings = VersionDiagnostics.diagnoseRuntimeImage containerImage today snapshot
                    let versionFindings =
                        (runtimeFindings @ engineFindings)
                        |> List.sortWith (fun a b ->
                            let rank = function FindingSeverity.Danger -> 0 | FindingSeverity.Warning -> 1 | FindingSeverity.Info -> 2
                            compare (rank a.Severity) (rank b.Severity))

                    let allFindingsForSummary = finalResult.Findings @ versionFindings
                    let dangerCount = allFindingsForSummary |> List.filter (fun f -> f.Severity = FindingSeverity.Danger) |> List.length
                    let warningCount = allFindingsForSummary |> List.filter (fun f -> f.Severity = FindingSeverity.Warning) |> List.length
                    let topTitles = allFindingsForSummary |> List.truncate 2 |> List.map (fun f -> Localization.translateKey languageCode f.TitleKey)
                    let dangerAlertOpt = AlertMonitor.createDiagnosticsDangerAlert containerId container.Name allFindingsForSummary DateTimeOffset.UtcNow

                    postOnUI (fun () ->
                        masterDataSnapshot <- snapshot
                        latestDiagnosticsSummaries <- Map.add containerId (dangerCount, warningCount, topTitles) latestDiagnosticsSummaries
                        dangerAlertOpt |> Option.iter (fun alert -> this.RecordAndNotifyAlert(alert, true))
                        this.PushMobileSnapshotIfRunning()
                        if not (isNull selectedContainer) && selectedContainer.Id = containerId then
                            currentDiagnosticsResult <- Some finalResult
                            currentVersionFindings <- versionFindings
                            this.IsDiagnosing <- false
                            this.NotifyDiagnosticsProperties()
                    )
                with _ ->
                    postOnUI (fun () ->
                        this.IsDiagnosing <- false
                        this.NotifyDiagnosticsProperties()
                    )
            }
            |> Async.StartImmediate

    // --- ℹ️ インフォメーションダイアログ・マスターデータ更新・アプリ版数比較 ---

    /// <summary>アプリ情報ダイアログが開いているかどうか。</summary>
    member this.IsInfoDialogOpen
        with get () = isInfoDialogOpen
        and set value = this.SetProperty(&isInfoDialogOpen, value) |> ignore

    /// <summary>マスターデータを更新中かどうか。</summary>
    member this.IsUpdatingMasterData
        with get () = isUpdatingMasterData
        and set value =
            if this.SetProperty(&isUpdatingMasterData, value) then
                this.OnPropertyChanged(nameof this.UpdateMasterDataButtonLabel)

    /// <summary>手動更新時の結果メッセージ。</summary>
    member this.MasterDataUpdateStatus
        with get () = masterDataUpdateStatus
        and set value =
            if this.SetProperty(&masterDataUpdateStatus, value) then
                this.OnPropertyChanged(nameof this.HasMasterDataUpdateStatus)

    member _.HasMasterDataUpdateStatus = not (String.IsNullOrWhiteSpace masterDataUpdateStatus)
    member _.InfoDialogTitle = localize "アプリ情報" "About"
    member _.CurrentVersionHeader = localize "現在のバージョン" "Current version"
    member _.LatestVersionHeader = localize "最新バージョン" "Latest version"
    member _.CurrentAppVersionText = currentAppVersion
    member _.LatestAppVersionText =
        match MasterDataRepository.TryGetApp(appId, masterDataSnapshot) with
        | Some app -> app.Latest
        | None -> localize "未確認" "Unverified"
    member _.IsAppOutdated =
        match MasterDataRepository.TryGetApp(appId, masterDataSnapshot) with
        | Some app -> VersionDiagnostics.isAppOutdated currentAppVersion app.Latest
        | None -> false
    member _.AppUpdateAvailableText = localize "新しいバージョンがあります" "A new version is available"
    member this.HasStoreUpdateLink =
        this.IsAppOutdated
        && (MasterDataRepository.TryGetApp(appId, masterDataSnapshot) |> Option.bind (fun a -> a.StoreUrl) |> Option.isSome)
    member _.ViewLatestVersionLabel = localize "最新版を見る" "View latest version"
    member _.UpdateMasterDataButtonLabel =
        if isUpdatingMasterData then localize "更新中..." "Updating..."
        else localize "定義データを更新" "Update definitions"
    member _.WebsiteButtonLabel = localize "紹介サイト" "Website"
    member _.WebsiteUrlText = websiteUrl
    member _.InstagramButtonLabel = "Instagram"
    member _.InstagramUrlText = instagramUrl
    member _.PrivacyNoticeText =
        localize
            "診断処理は端末内で完結します。バージョン定義データの取得時のみ GitHub へ通信し、ユーザーのデータや診断結果を送信することはありません。"
            "Diagnostics run locally on your device. Network access is used only to fetch version definitions from GitHub; no user data or diagnostic results are sent."

    /// <summary>ℹ️ ダイアログ関連プロパティの変更通知を発行する。</summary>
    member this.NotifyInfoProperties() =
        this.OnPropertyChanged(nameof this.InfoDialogTitle)
        this.OnPropertyChanged(nameof this.CurrentVersionHeader)
        this.OnPropertyChanged(nameof this.LatestVersionHeader)
        this.OnPropertyChanged(nameof this.CurrentAppVersionText)
        this.OnPropertyChanged(nameof this.LatestAppVersionText)
        this.OnPropertyChanged(nameof this.IsAppOutdated)
        this.OnPropertyChanged(nameof this.AppUpdateAvailableText)
        this.OnPropertyChanged(nameof this.HasStoreUpdateLink)
        this.OnPropertyChanged(nameof this.ViewLatestVersionLabel)
        this.OnPropertyChanged(nameof this.UpdateMasterDataButtonLabel)
        this.OnPropertyChanged(nameof this.WebsiteButtonLabel)
        this.OnPropertyChanged(nameof this.PrivacyNoticeText)
        this.OnPropertyChanged(nameof this.DiagnosticsDefinitionDateText)

    /// <summary>ℹ️ ダイアログを開く。</summary>
    member this.OpenInfoDialog() =
        this.MasterDataUpdateStatus <- ""
        masterDataSnapshot <- MasterDataRepository.GetSnapshot()
        this.NotifyInfoProperties()
        this.IsInfoDialogOpen <- true

    /// <summary>ℹ️ ダイアログを閉じる。</summary>
    member this.CloseInfoDialog() =
        this.IsInfoDialogOpen <- false

    /// <summary>マスターデータをオンライン取得して SQLite キャッシュを更新する。</summary>
    member this.UpdateMasterDataAsync(?silent: bool) =
        let isSilent = defaultArg silent false
        async {
            if not isUpdatingMasterData then
                postOnUI (fun () ->
                    this.IsUpdatingMasterData <- true
                    if not isSilent then this.MasterDataUpdateStatus <- "")
                try
                    let! updated =
                        match masterDataFetcher with
                        | Some fn -> MasterDataRepository.FetchAndImportAsync(customFetcher = fn)
                        | None -> MasterDataRepository.FetchAndImportAsync()
                    let snapshot = MasterDataRepository.GetSnapshot()
                    postOnUI (fun () ->
                        masterDataSnapshot <- snapshot
                        if not isSilent then
                            this.MasterDataUpdateStatus <-
                                if updated then localize "定義データを更新しました" "Definition data updated"
                                else localize "定義データの取得に失敗しました（キャッシュを使用します）" "Failed to fetch definition data (using cache)"
                        this.NotifyInfoProperties()
                        this.NotifyDiagnosticsProperties())
                finally
                    postOnUI (fun () -> this.IsUpdatingMasterData <- false)
        }

    /// <summary>外部 URL を OS の既定ブラウザーで開く。</summary>
    member _.OpenExternalUrl(url: string) =
        if not (String.IsNullOrWhiteSpace url)
           && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) then
            try
                Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
            with ex ->
                this.ShowErrorStatus(format "ブラウザを起動できませんでした: {0}" "Could not open the browser: {0}" [| box ex.Message |])

    member this.OpenWebsite() = this.OpenExternalUrl(websiteUrl)
    member this.OpenInstagram() = this.OpenExternalUrl(instagramUrl)
    member this.OpenLatestVersionPage() =
        MasterDataRepository.TryGetApp(appId, masterDataSnapshot)
        |> Option.bind (fun a -> a.StoreUrl)
        |> Option.iter this.OpenExternalUrl
    member _.ImagePullPlaceholder = localize "イメージ名（例: nginx:latest）" "Image name (e.g. nginx:latest)"
    member _.PullImageLabel = localize "取得" "Pull"
    member _.RepositoryLabel = "Repository"
    member _.TagLabel = "Tag"
    member _.SizeLabel = "Size"
    member _.DeleteImageLabel = localize "選択イメージを削除" "Remove selected image"
    member _.ComposeNamePlaceholder = localize "登録名" "Project name"
    member _.ComposePathPlaceholder = localize "Composeフォルダパス" "Compose folder path"
    member _.BrowseFolderLabel = localize "参照..." "Browse..."
    member _.RegisterLabel = localize "登録" "Add"
    member _.BrowserUrlPlaceholder = localize "ブラウザURL（任意、例: http://localhost:8080）" "Browser URL (optional, e.g. http://localhost:8080)"
    member _.NameLabel = localize "名称" "Name"
    member _.FolderPathLabel = localize "フォルダパス" "Folder path"
    member _.ComposeStatusLabel = localize "状態" "Status"
    member _.ComposeStartLabel = localize "起動" "Up"
    member _.ComposeStopLabel = localize "停止" "Down"
    member _.ComposeRestartLabel = localize "再起動" "Restart"
    member _.ComposeLogsLabel = localize "ログ表示" "Show logs"
    member _.BrowserButtonLabel = localize "ブラウザ" "Open browser"
    member _.RemoveProjectLabel = localize "登録解除" "Remove"
    member _.DesktopStartButtonLabel =
        match runtime with
        | Docker -> localize "Docker Desktopを起動" "Start Docker Desktop"
        | Podman -> localize "Podman machineを起動" "Start Podman machine"
        | Nerdctl -> ""
    member _.RuntimeModeLabel =
        match activeRuntime with
        | Docker -> localize "Docker モード" "Docker mode"
        | Podman -> localize "Podman モード" "Podman mode"
        | Nerdctl -> localize "nerdctl モード" "nerdctl mode"
    member _.FolderPickerTitle = localize "Composeフォルダを選択" "Select Compose folder"
    member _.MonitoringLabel = localize "監視" "Monitor"
    member _.DockerStatsLabel = localize "コンテナ統計情報" "Container stats"
    member _.CpuLabel = "CPU"
    member _.MemoryLabel = localize "メモリ" "Memory"
    member _.NetworkLabel = localize "通信量" "Net I/O"
    member _.ComposeRunningCountLabel = localize "稼働数" "Running"
    member _.FollowComposeLogsLabel = localize "Composeログを追尾" "Follow Compose logs"
    member _.ConnectionLabel = localize "接続先" "Connection"
    member _.SshLabel = localize "SSH接続" "SSH connections"
    member _.HostLabel = localize "ホスト" "Host"
    member _.PortLabel = localize "ポート" "Port"
    member _.EngineLabel = localize "エンジン" "Engine"
    member _.LastConnectedLabel = localize "最終接続" "Last connected"
    member _.UserLabel = localize "ユーザー" "User"
    member _.PrivateKeyLabel = localize "秘密鍵のパス" "Private key path"
    member _.PrivateKeyHintLabel = localize "空欄の場合はデフォルト鍵・エージェントを使用" "Leave blank to use default keys or ssh-agent"
    member _.PrivateKeyPlaceholder = localize "秘密鍵のパス（空欄の場合はデフォルト鍵・エージェントを使用）" "Private key path (leave blank for default key / ssh-agent)"
    member _.TestConnectionLabel = localize "接続テスト" "Test connection"
    member _.SaveSshLabel = localize "保存／更新" "Save / update"
    member _.NewSshLabel = localize "新規" "New"
    member _.NotificationsLabel = localize "警告通知" "Alert notifications"
    member _.WarningLabel = localize "警告" "Warning"
    member _.MemoryPercentLabel = localize "使用率" "Usage"
    member _.ThemeOptions = themeOptions

    member this.Theme
        with get () = themeOptions |> Array.find (fun option -> option.Key = settings.Theme)
        and set (value: ChoiceOption) =
            if not (isNull value) then
                settings.Theme <- if value.Key = "Light" || value.Key = "Dark" then value.Key else "System"
                AppSettings.Save(settings)
                this.ApplyTheme(settings.Theme)
                this.OnPropertyChanged(nameof this.Theme)

    member this.NotificationsEnabled
        with get () = notificationsEnabled
        and set value =
            if this.SetProperty(&notificationsEnabled, value) then
                settings.NotificationsEnabled <- value
                resourceAlerts.Reset()
                composeAlerts.Reset()
                AppSettings.Save(settings)

    member this.NewSshName
        with get () = newSshName
        and set value = this.SetProperty(&newSshName, value) |> ignore
    member this.NewSshHost
        with get () = newSshHost
        and set value = this.SetProperty(&newSshHost, value) |> ignore
    member this.NewSshPort
        with get () = newSshPort
        and set value = this.SetProperty(&newSshPort, value) |> ignore
    member this.NewSshUser
        with get () = newSshUser
        and set value = this.SetProperty(&newSshUser, value) |> ignore
    member this.NewSshKey
        with get () = newSshKey
        and set value = this.SetProperty(&newSshKey, value) |> ignore
    member _.SshEngineOptions = sshEngineOptions
    member this.NewSshEngine
        with get () = newSshEngine
        and set value =
            if not (isNull value) then this.SetProperty(&newSshEngine, value) |> ignore

    member this.SelectedSshConnection
        with get () = selectedSshConnection
        and set value =
            if this.SetProperty(&selectedSshConnection, value) && not (isNull value) then
                this.NewSshName <- value.Name
                this.NewSshHost <- value.Host
                this.NewSshPort <- string value.Port
                this.NewSshUser <- value.User
                this.NewSshKey <- value.PrivateKeyPath
                let engine = if value.Engine = "" then ContainerRuntime.command runtime else value.Engine
                match sshEngineOptions |> Array.tryFind (fun option -> option.Key = engine) with
                | Some option -> this.NewSshEngine <- option
                | None -> ()

    /// <summary>入力欄の内容から保存用・テスト用の SshConnection 下書きを生成する。</summary>
    member private _.SshDraft() =
        let draft = SshConnection()
        if not (isNull selectedSshConnection) then draft.Id <- selectedSshConnection.Id
        draft.Name <- newSshName.Trim()
        draft.Host <- newSshHost.Trim()
        draft.Port <- match Int32.TryParse(newSshPort) with true, port -> port | _ -> 0
        draft.User <- newSshUser.Trim()
        draft.PrivateKeyPath <- newSshKey.Trim()
        draft.Engine <- newSshEngine.Key
        if not (isNull selectedSshConnection)
           && selectedSshConnection.Host = draft.Host
           && selectedSshConnection.Port = draft.Port
           && selectedSshConnection.User = draft.User
           && selectedSshConnection.PrivateKeyPath = draft.PrivateKeyPath then
            draft.LastConnectedAt <- selectedSshConnection.LastConnectedAt
            draft.ConnectionState <- selectedSshConnection.ConnectionState
        draft

    /// <summary>SSH 接続先の編集入力欄を初期状態へリセットする。</summary>
    member _.ClearSshEditor() =
        this.SelectedSshConnection <- null
        this.NewSshName <- ""
        this.NewSshHost <- ""
        this.NewSshPort <- "22"
        this.NewSshUser <- ""
        this.NewSshKey <- ""
        this.NewSshEngine <- sshEngineOptions |> Array.find (fun option -> option.Key = ContainerRuntime.command runtime)

    /// <summary>入力された SSH 接続設定を検証し、新規追加または既存更新して保存する。</summary>
    member _.SaveSshConnection() =
        if this.CanChangeConnection then
            let draft = this.SshDraft()
            match draft.Validate() with
            | Error message ->
                this.SetConnectionDetails(message)
                this.ShowStatus(localize "SSH設定を確認してください。" "Check your SSH settings.")
            | Ok () when draft.Id = connectionId ->
                this.ShowStatus(localize "接続先をローカルに切り替えてから編集してください。" "Switch to Local before editing the active connection.")
            | Ok () ->
                if not (isNull selectedSshConnection) then
                    let index = settings.SshConnections.IndexOf(selectedSshConnection)
                    settings.SshConnections.[index] <- draft
                    let option = connectionOptions |> Seq.find (fun item -> item.Key = draft.Id)
                    option.Label <- draft.Name
                else
                    settings.SshConnections.Add(draft)
                    connectionOptions.Add(ChoiceOption(draft.Id, draft.Name))
                AppSettings.Save(settings)
                this.ClearSshEditor()
                this.ShowStatus(localize "SSH接続先を保存しました。" "SSH connection saved.")

    /// <summary>選択中の SSH 接続先と紐づく Compose 登録を設定および SQLite から削除する。</summary>
    member _.RemoveSshConnection() =
        if this.CanChangeConnection && not (isNull selectedSshConnection) then
            if selectedSshConnection.Id = connectionId then
                this.ShowStatus(localize "接続先をローカルに切り替えてから編集してください。" "Switch to Local before editing the active connection.")
            else
                let option = connectionOptions |> Seq.find (fun item -> item.Key = selectedSshConnection.Id)
                let sshConnId = selectedSshConnection.Id
                let removedProjects = settings.ComposeProjects |> Seq.filter (fun project -> project.ConnectionId = sshConnId) |> Seq.toList
                for p in removedProjects do
                    ComposeRepository.Delete(p)
                ComposeRepository.DeleteByConnectionId(sshConnId)
                let projects = settings.ComposeProjects |> Seq.filter (fun project -> project.ConnectionId <> sshConnId) |> Seq.toList
                settings.ComposeProjects <- ObservableCollection<ComposeProject>(projects)
                settings.SshConnections.Remove(selectedSshConnection) |> ignore
                connectionOptions.Remove(option) |> ignore
                this.ClearSshEditor()
                AppSettings.Save(settings)

    /// <summary>入力された SSH 設定でリモート接続テスト（echo OK）を非同期実行する。</summary>
    member _.TestSshConnectionAsync() = async {
        if this.CanChangeConnection then
            let draft = this.SshDraft()
            match draft.Validate() with
            | Error message ->
                this.SetConnectionDetails(message)
                this.ShowStatus(localize "SSH設定を確認してください。" "Check your SSH settings.")
            | Ok () ->
                this.SetBusy(true)
                try
                    let client = ContainerCli(draft, runtime = runtime)
                    let! result = client.TestSshConnection()
                    if not (isNull selectedSshConnection)
                       && selectedSshConnection.Id = draft.Id
                       && selectedSshConnection.Name = draft.Name
                       && selectedSshConnection.Host = draft.Host
                       && selectedSshConnection.Port = draft.Port
                       && selectedSshConnection.User = draft.User
                       && selectedSshConnection.PrivateKeyPath = draft.PrivateKeyPath then
                        match result with
                        | Ok () ->
                            selectedSshConnection.LastConnectedAt <- DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
                            selectedSshConnection.ConnectionState <- localize "SSH接続成功" "SSH connected"
                            AppSettings.Save(settings)
                        | Error _ -> selectedSshConnection.ConnectionState <- localize "接続失敗" "Connection failed"
                    match result with
                    | Ok () -> this.ShowStatus(localize "🟢 接続成功" "🟢 Connection successful")
                    | Error details ->
                        let agentHint = localize "SSH設定やssh-agentサービスの状態を確認してください。" "Check your SSH settings and the ssh-agent service status."
                        let detailedMessage =
                            if String.IsNullOrWhiteSpace(details) then agentHint
                            else details + Environment.NewLine + agentHint
                        this.SetConnectionDetails(detailedMessage)
                        this.ShowErrorStatus(localize "🔴 接続失敗" "🔴 Connection failed")
                finally
                    this.SetBusy(false)
    }

    /// <summary>指定されたテーマ名（Light / Dark / System）をアプリ全体へ適用する。</summary>
    member private _.ApplyTheme(themeName: string) =
        if not (isNull Application.Current) then
            Application.Current.RequestedThemeVariant <-
                match themeName with
                | "Light" -> ThemeVariant.Light
                | "Dark" -> ThemeVariant.Dark
                | _ -> ThemeVariant.Default
            for container in containers do container.RefreshTheme()

    /// <summary>一覧で現在選択されているコンテナモデル。</summary>
    member this.SelectedContainer
        with get () = selectedContainer
        and set value =
            if updatingContainerList && isNull value && not (isNull selectedContainer) then
                ()
            elif this.SetProperty(&selectedContainer, value) then
                this.OnPropertyChanged(nameof this.CanOperate)
                this.OnPropertyChanged(nameof this.CanRemoveContainer)
                this.OnPropertyChanged(nameof this.InferredServicesText)
                this.OnPropertyChanged(nameof this.HasInferredServices)
                this.OnPropertyChanged(nameof this.HasSelectedContainer)
                this.OnPropertyChanged(nameof this.HasSelectedContainerMounts)
                this.OnPropertyChanged(nameof this.SelectedContainerMountsText)
                this.OnPropertyChanged(nameof this.HasImageCandidate)
                this.OnPropertyChanged(nameof this.ImageCandidateText)
                this.OnPropertyChanged(nameof this.HasPortCandidates)
                this.OnPropertyChanged(nameof this.PortCandidatesTexts)
                this.OnPropertyChanged(nameof this.HasNoInferenceCandidates)
                currentDiagnosticsResult <- None
                currentVersionFindings <- []
                if isNull value then
                    this.IsInferenceDialogOpen <- false
                    this.IsDiagnosticsDialogOpen <- false
                this.NotifyDiagnosticsProperties()
                this.RefreshSelectedContainerOperationLogs()
                if not updatingContainerList then
                    match value with
                    | null ->
                        stopLogFollow()
                        this.ShowLogs(localize "ログを表示するコンテナを選択してください。" "Select a container to view its logs.")
                    | selected ->
                        stopLogFollow()
                        this.ShowLogs(format "{0} のログを読み込みます..." "Loading logs for {0}..." [| box selected.Name |])
                        if defaultArg startAutomatically true then
                            this.LoadLogsAsync(selected) |> Async.StartImmediate

    /// <summary>一覧で現在選択されているコンテナイメージモデル。</summary>
    member this.SelectedImage
        with get () = selectedImage
        and set value =
            if updatingImageList && isNull value && not (isNull selectedImage) then
                ()
            elif this.SetProperty(&selectedImage, value) then
                this.OnPropertyChanged(nameof this.CanRemoveImage)

    /// <summary>一覧で現在選択されている Compose プロジェクトモデル。</summary>
    member this.SelectedComposeProject
        with get () = selectedComposeProject
        and set value =
            if this.SetProperty(&selectedComposeProject, value) then
                stopComposeLogFollow()
                cancelComposeLogFetch()
                composeLogRequestVersion <- composeLogRequestVersion + 1
                this.OnPropertyChanged(nameof this.CanManageComposeProject)
                this.RefreshSelectedComposeOperationLogs()
                match value with
                | null ->
                    this.ShowComposeLogs(localize "Composeプロジェクトを選択してください。" "Select a Compose project.")
                | project ->
                    if followComposeLogs then
                        loadComposeLogs None project |> Async.StartImmediate
                    elif defaultArg startAutomatically true || composeLogsFetcher.IsSome then
                        this.ShowComposeLogs(format "{0} のログを読み込みます..." "Loading logs for {0}..." [| box project.Name |])
                        loadComposeLogs (Some 50) project |> Async.StartImmediate
                    else
                        this.ShowComposeLogs(format "{0} のログを読み込みます..." "Loading logs for {0}..." [| box project.Name |])

    /// <summary>コンテナログ表示欄のテキスト。</summary>
    member _.LogText = logText

    /// <summary>Compose ログ表示欄のテキスト。</summary>
    member _.ComposeLogText = composeLogText

    /// <summary>ステータスバーに表示されるメッセージ文字列。</summary>
    member _.StatusMessage = statusMessage

    /// <summary>現在のステータスメッセージが操作エラー表示中かどうか。</summary>
    member _.IsErrorStatus = isErrorStatus

    /// <summary>変更系コマンドや接続テストなどの処理中フラグ。</summary>
    member _.IsBusy = isBusy

    /// <summary>定期的なコンテナ一覧更新（ps更新）中フラグ。</summary>
    member _.IsRefreshing = refreshing

    /// <summary>選択中コンテナに対して操作（起動・停止・再起動・ターミナル）が可能かどうか。</summary>
    member this.CanOperate =
        not (isNull selectedContainer)
        && not selectedContainer.IsOperating
        && dockerDesktopState = "running"

    /// <summary>選択中コンテナが停止中（Exited / Created）で削除可能かどうか。</summary>
    member this.CanRemoveContainer =
        not (isNull selectedContainer)
        && not selectedContainer.IsOperating
        && (selectedContainer.Status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase)
            || selectedContainer.Status.StartsWith("Created", StringComparison.OrdinalIgnoreCase))

    /// <summary>選択中イメージが削除可能かどうか。</summary>
    member this.CanRemoveImage =
        not (isNull selectedImage)
        && not selectedImage.IsOperating

    /// <summary>選択中 Compose プロジェクトの管理操作が可能かどうか。</summary>
    member this.CanManageComposeProject =
        not (isNull selectedComposeProject)
        && not selectedComposeProject.IsOperating

    /// <summary>コンテナログのリアルタイム追尾が有効かどうか。</summary>
    member this.FollowLogs
        with get () = followLogs
        and set value =
            if this.SetProperty(&followLogs, value) then
                match selectedContainer with
                | null -> if not value then stopLogFollow()
                | selected ->
                    stopLogFollow()
                    this.LoadLogsAsync(selected) |> Async.StartImmediate

    /// <summary>Compose ログのリアルタイム追尾が有効かどうか。</summary>
    member this.FollowComposeLogs
        with get () = followComposeLogs
        and set value =
            if this.SetProperty(&followComposeLogs, value) then
                match selectedComposeProject with
                | null -> if not value then stopComposeLogFollow()
                | project ->
                    if value || defaultArg startAutomatically true || composeLogsFetcher.IsSome then
                        loadComposeLogs (if value then None else Some 50) project |> Async.StartImmediate

    /// <summary>イメージ取得（Pull）用に入力されたイメージ参照文字列。</summary>
    member this.ImagePullReference
        with get () = imagePullReference
        and set value = this.SetProperty(&imagePullReference, value) |> ignore

    /// <summary>新規 Compose 登録用に入力されたプロジェクト名。</summary>
    member this.NewComposeName
        with get () = newComposeName
        and set value = this.SetProperty(&newComposeName, value) |> ignore

    /// <summary>新規 Compose 登録用に入力されたフォルダパス。</summary>
    member this.NewComposePath
        with get () = newComposePath
        and set value = this.SetProperty(&newComposePath, value) |> ignore

    /// <summary>新規 Compose 登録用に入力されたブラウザ URL。</summary>
    member this.NewComposeUrl
        with get () = newComposeUrl
        and set value = this.SetProperty(&newComposeUrl, value) |> ignore

    /// <summary>定期的な自動更新が有効かどうか。</summary>
    member this.AutoRefresh
        with get () = autoRefresh
        and set value =
            if this.SetProperty(&autoRefresh, value) then
                settings.AutoRefresh <- value
                AppSettings.Save(settings)
                if value then timer.Start() else timer.Stop()

    /// <summary>Compose プロジェクト一覧の稼働ステータスを非同期に再取得する。</summary>
    member _.RefreshComposeProjectsAsync() = refreshDashboard false

    /// <summary>自動更新間隔の表示ラベル文字列。</summary>
    member _.RefreshIntervalLabel =
        format "自動更新（{0}秒）" "Auto refresh ({0}s)" [| box settings.RefreshIntervalSec |]

    /// <summary>ダッシュボード全体（コンテナ・イメージ・Compose・監視）を手動で再取得する。</summary>
    member _.RefreshAsync() = refreshDashboard true

    /// <summary>選択中のコンテナを起動する。</summary>
    member _.StartSelectedAsync() = runAction "起動"

    /// <summary>選択中のコンテナを停止する。</summary>
    member _.StopSelectedAsync() = runAction "停止"

    /// <summary>選択中のコンテナを再起動する。</summary>
    member _.RestartSelectedAsync() = runAction "再起動"

    /// <summary>選択中の停止済みコンテナを削除する。</summary>
    member _.RemoveSelectedContainerAsync() = runAction "削除"

    /// <summary>入力されたイメージ参照をリモートレジストリから取得（Pull）する。</summary>
    member _.PullImageAsync() = pullImage()

    /// <summary>選択中のイメージをローカルから削除する。</summary>
    member _.RemoveSelectedImageAsync() = removeImage()

    /// <summary>選択中の Compose プロジェクトを起動（docker compose up -d）する。</summary>
    member _.StartComposeAsync() = runComposeAction "起動"

    /// <summary>選択中の Compose プロジェクトを停止（docker compose down）する。</summary>
    member _.StopComposeAsync() = runComposeAction "停止"

    /// <summary>選択中の Compose プロジェクトを再起動する。</summary>
    member _.RestartComposeAsync() = runComposeAction "再起動"

    /// <summary>選択中の Compose プロジェクトのログ全体を取得して表示する。</summary>
    member _.ShowComposeLogsAsync() = runComposeAction "ログ"

    /// <summary>ローカルのコンテナエンジン（Docker Desktop または Podman machine）を起動し、接続可能になるまで待機する。</summary>
    member _.StartContainerEngineAsync() = async {
        if this.CanStartContainerEngine then
            desktopLaunching <- true
            this.SetDockerDesktopState("starting")
            let rec waitForEngine attempts startupError = async {
                let! result = docker.CheckConnection()
                match result, attempts with
                | Ok (), _ ->
                    this.SetConnectionDetails("")
                    this.SetDockerDesktopState("running")
                    do! refreshDashboard true
                | Error(_, details), remaining when remaining > 0 ->
                    this.SetConnectionDetails(defaultArg startupError details)
                    do! Async.Sleep(2000)
                    return! waitForEngine (remaining - 1) startupError
                | Error(state, details), _ ->
                    this.SetConnectionDetails(defaultArg startupError details)
                    this.SetDockerDesktopState(if startupError.IsSome then "failed" else state)
            }
            try
                try
                    match runtime with
                    | Docker ->
                        match DockerDesktop.findExecutable dockerDesktopPaths with
                        | None ->
                            this.SetConnectionDetails("Docker Desktop.exe: " + Environment.NewLine + String.concat Environment.NewLine dockerDesktopPaths)
                            this.SetDockerDesktopState("missing")
                        | Some dockerDesktopPath ->
                            Process.Start(ProcessStartInfo(dockerDesktopPath, UseShellExecute = true)) |> ignore
                            do! waitForEngine 30 None
                    | Podman ->
                        let! connection = docker.CheckConnection()
                        match connection with
                        | Ok () -> do! waitForEngine 0 None
                        | Error _ ->
                            let! result = docker.StartPodmanMachine()
                            match result with
                            | Ok _ -> do! waitForEngine 30 None
                            | Error details -> do! waitForEngine 5 (Some details)
                    | Nerdctl -> invalidOp "Automatic startup is not supported for nerdctl. Start containerd manually."
                with ex ->
                    this.SetConnectionDetails(ex.Message)
                    this.SetDockerDesktopState("failed")
            finally
                desktopLaunching <- false
                this.RefreshDockerDesktopState()
    }

    /// <summary>入力された Compose フォルダとブラウザ URL を検証して登録し、一覧を更新する。</summary>
    member _.RegisterComposeProject() =
        try
            if String.IsNullOrWhiteSpace(newComposeName) || String.IsNullOrWhiteSpace(newComposePath) then
                this.ShowStatus(localize "Compose名とフォルダパスを入力してください" "Enter a project name and folder path.")
            elif connectionId = "local" && not (Directory.Exists(newComposePath.Trim())) then
                this.ShowStatus(localize "指定したComposeフォルダが見つかりません" "The selected Compose folder does not exist.")
            else
                let fullPath = if connectionId = "local" then Path.GetFullPath(newComposePath.Trim()) else newComposePath.Trim()
                let hasComposeFile =
                    connectionId <> "local" ||
                    ([ "compose.yaml"; "compose.yml"; "docker-compose.yaml"; "docker-compose.yml" ]
                     |> List.exists (fun fileName -> File.Exists(Path.Combine(fullPath, fileName))))
                let browserUrl = newComposeUrl.Trim()
                let validBrowserUrl =
                    String.IsNullOrWhiteSpace(browserUrl)
                    || (Uri.IsWellFormedUriString(browserUrl, UriKind.Absolute)
                        && (browserUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || browserUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                if not hasComposeFile then
                    this.ShowStatus(localize "Composeファイルがフォルダ内に見つかりません" "No Compose file was found in the selected folder.")
                elif not validBrowserUrl then
                    this.ShowStatus(localize "ブラウザURLはhttpまたはhttpsで入力してください" "Browser URL must use http or https.")
                elif composeProjects |> Seq.exists (fun project -> String.Equals(project.FolderPath, fullPath, StringComparison.OrdinalIgnoreCase)) then
                    this.ShowStatus(localize "このComposeフォルダは登録済みです" "This Compose folder is already registered.")
                else
                    let project = ComposeProject()
                    project.ConnectionId <- connectionId
                    if connectionId = "local" then project.Engine <- ContainerRuntime.command localComposeRuntime
                    project.Name <- newComposeName.Trim()
                    project.FolderPath <- fullPath
                    project.BrowserUrl <- browserUrl
                    ComposeRepository.Save(project)
                    composeProjects.Add(project)
                    persistComposeProjects()
                    this.SelectedComposeProject <- project
                    this.NewComposeName <- ""
                    this.NewComposePath <- ""
                    this.NewComposeUrl <- ""
                    this.ShowStatus(format "{0} を登録しました" "Registered {0}." [| box project.Name |])
                    this.RefreshComposeProjectsAsync() |> Async.StartImmediate
        with ex -> this.ShowErrorStatus(format "登録に失敗しました: {0}" "Registration failed: {0}" [| box ex.Message |])

    /// <summary>フォルダ／ファイル選択ダイアログの失敗メッセージをステータスへ表示する。</summary>
    member _.ReportFolderPickerError(message: string) =
        this.ShowErrorStatus(
            sprintf "%s %s"
                (localize "フォルダ選択ダイアログを開けませんでした。" "Could not open the folder picker.")
                message)

    /// <summary>選択中の Compose プロジェクトの登録を解除して設定を保存する。</summary>
    member _.RemoveSelectedComposeProject() =
        if not (isNull selectedComposeProject) then
            let name = selectedComposeProject.Name
            stopComposeLogFollow()
            cancelComposeLogFetch()
            composeLogRequestVersion <- composeLogRequestVersion + 1
            ComposeRepository.Delete(selectedComposeProject)
            composeProjects.Remove(selectedComposeProject) |> ignore
            this.SelectedComposeProject <- null
            persistComposeProjects()
            this.ShowStatus(format "{0} の登録を解除しました" "Removed {0} from the project list." [| box name |])

    /// <summary>選択中の Compose プロジェクトに登録されたブラウザ URL を既定ブラウザで開く。</summary>
    member _.OpenComposeBrowser() =
        match selectedComposeProject with
        | null -> ()
        | project when String.IsNullOrWhiteSpace(project.BrowserUrl) ->
            this.ShowStatus(localize "このプロジェクトにはブラウザURLが登録されていません" "No browser URL is configured for this project.")
        | project ->
            try
                Process.Start(ProcessStartInfo(project.BrowserUrl, UseShellExecute = true)) |> ignore
            with ex -> this.ShowErrorStatus(format "ブラウザを起動できませんでした: {0}" "Could not open the browser: {0}" [| box ex.Message |])

    /// <summary>指定フォルダまたはコンテナを対象に、ローカルまたは SSH 先の対話型ターミナルを開き、成否を返す。</summary>
    member private this.OpenTerminalWithResult(folderPath: string option, container: (string * TerminalCommand.ContainerShell) option) : Result<unit, string> =
        let connection =
            if connectionId = "local" then None
            else settings.SshConnections |> Seq.tryFind (fun connection -> connection.Id = connectionId)
        if connectionId <> "local" && connection.IsNone then
            let message = localize "SSH接続先が見つかりません。" "SSH connection was not found."
            this.ShowErrorStatus(message)
            Error message
        else
            match TerminalCommand.start activeRuntime connection folderPath container with
            | Ok () -> Ok ()
            | Error message ->
                this.ShowErrorStatus(format "ターミナルを起動できませんでした: {0}" "Could not open the terminal: {0}" [| box message |])
                Error message

    /// <summary>指定フォルダまたはコンテナを対象に、ローカルまたは SSH 先の対話型ターミナルを開く。</summary>
    member private this.OpenTerminal(folderPath: string option, container: (string * TerminalCommand.ContainerShell) option) =
        this.OpenTerminalWithResult(folderPath, container) |> ignore

    /// <summary>接続先ホストのシェルを対話型ターミナルで開く。</summary>
    member _.OpenHostTerminal() = this.OpenTerminal(None, None)

    /// <summary>選択中のコンテナ内で bash（不可なら sh）を対話型ターミナルで開く。</summary>
    member _.EnterContainerAsync() = async {
        if this.CanOperate then
            let container = selectedContainer
            if not (isNull container) && not container.IsOperating then
                container.IsOperating <- true
                this.SetBusy(true)
                try
                    do! waitForRefresh()
                    if not (containers |> Seq.exists (fun item -> item.Id = container.Id)) then
                        this.ShowStatus(localize "選択したコンテナは一覧にありません。" "The selected container is no longer available.")
                    elif dockerDesktopState <> "running" then
                        this.ShowErrorStatus(localize "コンテナエンジンに接続できません。" "Cannot connect to the container engine.")
                    else
                        let! result = docker.CheckContainerBash(container.Id)
                        match result with
                        | Error details ->
                            this.ShowStatus(format "bashの確認に失敗したためshを試します: {0}" "Bash check failed; trying sh: {0}" [| box details |])
                        | Ok _ -> ()
                        match this.OpenTerminalWithResult(None, Some(container.Id, TerminalCommand.shellForProbe result)) with
                        | Ok () ->
                            OperationLogRepository.Record(container.Id, container.Name, "exec", true)
                            this.RefreshSelectedContainerOperationLogs()
                        | Error message ->
                            OperationLogRepository.Record(container.Id, container.Name, "exec", false, detail = message)
                            this.RefreshSelectedContainerOperationLogs()
                finally
                    container.IsOperating <- false
                    this.SetBusy(false)
    }

    /// <summary>選択中の Compose プロジェクトのフォルダを作業ディレクトリとしてターミナルを開く。</summary>
    member _.OpenComposeTerminal() =
        if this.CanManageComposeProject then this.OpenTerminal(Some selectedComposeProject.FolderPath, None)

    /// <summary>処理中フラグを更新し、関連する操作可否プロパティの変更を通知する。</summary>
    member private this.SetBusy(value: bool) =
        isBusy <- value
        this.OnPropertyChanged(nameof this.IsBusy)
        this.OnPropertyChanged(nameof this.CanOperate)
        this.OnPropertyChanged(nameof this.CanRemoveContainer)
        this.OnPropertyChanged(nameof this.CanRemoveImage)
        this.OnPropertyChanged(nameof this.CanManageComposeProject)
        this.NotifyActivity()

    member private this.NotifyActivity() =
        this.OnPropertyChanged(nameof this.CanChangeConnection)
        this.OnPropertyChanged(nameof this.CanChangeComposeEngine)

    /// <summary>接続エラーなどの詳細メッセージを保持し、更新完了後に表示へ反映する。</summary>
    member private this.SetConnectionDetails(value: string) =
        if refreshing then
            pendingConnectionDetails <- Some value
        elif connectionDetails <> value then
            let hadDetails = not (String.IsNullOrWhiteSpace(connectionDetails))
            connectionDetails <- value
            this.OnPropertyChanged(nameof this.ConnectionDetails)
            if hadDetails <> not (String.IsNullOrWhiteSpace(value)) then
                this.OnPropertyChanged(nameof this.HasConnectionDetails)

    /// <summary>コンテナエンジンの状態キー（running / starting / unreachable 等）を更新して表示を反映する。</summary>
    member private this.SetDockerDesktopStateInternal(value: string, force: bool) =
        if dockerDesktopState <> value || force then
            dockerDesktopState <- value
            dockerDesktopStatus <-
                match value with
                | "running" -> localize "🟢 エンジン起動中" "🟢 Container engine running"
                | "sshMissing" -> localize "🔴 SSHコマンドが見つかりません" "🔴 SSH command not found"
                | "remoteRuntimeMissing" -> localize "🔴 リモートエンジンが見つかりません" "🔴 Remote container engine not found"
                | "cliMissing" -> localize "🔴 コンテナエンジン CLIが見つかりません" "🔴 Container engine CLI not found"
                | "permission" -> localize "🔴 コンテナエンジンへのアクセス権限がありません" "🔴 Container engine access denied"
                | "starting" ->
                    match runtime with
                    | Docker -> localize "🟡 Docker Desktopを起動中..." "🟡 Starting Docker Desktop..."
                    | Podman -> localize "🟡 Podman machineを起動中..." "🟡 Starting Podman machine..."
                    | Nerdctl -> localize "🔴 エンジン停止中" "🔴 Container engine stopped"
                | "missing" -> localize "🔴 Docker Desktopが見つかりません" "🔴 Docker Desktop was not found"
                | "unreachable" -> localize "🔴 コンテナエンジンに接続できません" "🔴 Cannot connect to the container engine"
                | "failed" ->
                    match runtime with
                    | Docker -> localize "🔴 起動に失敗しました" "🔴 Failed to start Docker Desktop"
                    | Podman -> localize "🔴 Podman machineの起動に失敗しました" "🔴 Failed to start the Podman machine"
                    | Nerdctl -> localize "🔴 コンテナエンジンに接続できません" "🔴 Cannot connect to the container engine"
                | _ -> localize "🔴 エンジン停止中" "🔴 Container engine stopped"
            this.OnPropertyChanged(nameof this.ContainerEngineStatus)
            this.OnPropertyChanged(nameof this.ShowContainerEngineStart)
            this.OnPropertyChanged(nameof this.CanStartContainerEngine)
            this.OnPropertyChanged(nameof this.ConnectionHelp)
            this.OnPropertyChanged(nameof this.CanOperate)

    member private this.SetDockerDesktopState(value: string) =
        this.SetDockerDesktopStateInternal(value, false)

    member private this.RefreshDockerDesktopState() =
        this.SetDockerDesktopStateInternal(dockerDesktopState, true)

    /// <summary>選択中コンテナの操作履歴一覧を再読込して通知する。</summary>
    member private this.RefreshSelectedContainerOperationLogs() =
        selectedContainerOperationLogs <-
            if isNull selectedContainer || String.IsNullOrWhiteSpace(selectedContainer.Id) then []
            else OperationLogRepository.GetByContainerId(selectedContainer.Id)
        this.OnPropertyChanged(nameof this.SelectedContainerOperationLogs)
        this.OnPropertyChanged(nameof this.HasSelectedContainerOperationLogs)
        this.OnPropertyChanged(nameof this.HasNoSelectedContainerOperationLogs)

    /// <summary>選択中 Compose プロジェクトの操作履歴一覧を再読込して通知する。</summary>
    member private this.RefreshSelectedComposeOperationLogs() =
        selectedComposeOperationLogs <-
            if isNull selectedComposeProject || String.IsNullOrWhiteSpace(selectedComposeProject.Id) then []
            else OperationLogRepository.GetByContainerId(OperationLog.composeTargetId selectedComposeProject.Id)
        this.OnPropertyChanged(nameof this.SelectedComposeOperationLogs)
        this.OnPropertyChanged(nameof this.HasSelectedComposeOperationLogs)
        this.OnPropertyChanged(nameof this.HasNoSelectedComposeOperationLogs)

    /// <summary>画面上部のステータスメッセージを更新する。</summary>
    member private this.ShowStatus(message: string) =
        let wasError = isErrorStatus
        statusMessage <- message
        if wasError then
            isErrorStatus <- false
            this.OnPropertyChanged(nameof this.IsErrorStatus)
        this.OnPropertyChanged(nameof this.StatusMessage)

    /// <summary>画面上部のエラー系ステータスメッセージを更新する。</summary>
    member private this.ShowErrorStatus(message: string) =
        let wasError = isErrorStatus
        statusMessage <- message
        if not wasError then
            isErrorStatus <- true
            this.OnPropertyChanged(nameof this.IsErrorStatus)
        this.OnPropertyChanged(nameof this.StatusMessage)

    /// <summary>エラー系のステータスメッセージが表示中であればクリアする。</summary>
    member private this.ClearErrorStatus() =
        if isErrorStatus then this.ShowStatus("")

    /// <summary>コンテナログ欄のテキストを置き換える。</summary>
    member private this.ShowLogs(message: string) =
        logText <- message
        this.OnPropertyChanged(nameof this.LogText)

    /// <summary>Compose ログ欄のテキストを置き換える。</summary>
    member private this.ShowComposeLogs(message: string) =
        composeLogText <- message
        this.OnPropertyChanged(nameof this.ComposeLogText)

    /// <summary>Compose ログ欄の末尾に 1 行追記し、一定文字数を超えたら古い部分を切り詰める。</summary>
    member private this.AppendComposeLog(message: string) =
        let combined = if String.IsNullOrEmpty(composeLogText) then message else composeLogText + Environment.NewLine + message
        composeLogText <- if combined.Length > 250000 then combined.Substring(combined.Length - 200000) else combined
        this.OnPropertyChanged(nameof this.ComposeLogText)

    /// <summary>コンテナログ欄の末尾に 1 行追記し、一定文字数を超えたら古い部分を切り詰める。</summary>
    member private this.AppendLog(message: string) =
        let combined = if String.IsNullOrEmpty(logText) then message else logText + Environment.NewLine + message
        logText <- if combined.Length > 250000 then combined.Substring(combined.Length - 200000) else combined
        this.OnPropertyChanged(nameof this.LogText)

    /// <summary>選択中コンテナのログを取得または追尾開始する。</summary>
    member private this.LoadLogsAsync(container: ContainerInfo) =
        if followLogs then
            this.ShowLogs(format "{0} のログを追尾中..." "Following logs for {0}..." [| box container.Name |])
            let onLine line =
                Dispatcher.UIThread.Post(Action(fun () ->
                    if selectedContainer = container && followLogs then this.AppendLog(line)))
            match docker.StartLogFollow(container.Id, onLine) with
            | Ok streamProcess ->
                logProcess <- Some streamProcess
                this.ShowStatus(format "{0} のログを追尾中" "Following logs for {0}" [| box container.Name |])
            | Error message -> this.ShowErrorStatus(localizeDockerError message)
            async.Return(())
        else
            async {
                let! result = docker.GetLogs(container.Id)
                if selectedContainer = container then
                    match result with
                    | Ok output ->
                        this.ShowLogs(if String.IsNullOrWhiteSpace(output) then localize "ログはありません。" "No logs available." else output)
                        this.ShowStatus(format "{0} のログを表示中" "Showing logs for {0}" [| box container.Name |])
                    | Error message -> this.ShowErrorStatus(localizeDockerError message)
            }

    // --- 📦 ボリューム情報（v1.2・読み取り専用） ---

    /// <summary>読み込まれている全ボリュームコレクション。</summary>
    member _.Volumes = volumes

    /// <summary>検索・未使用フィルタ適用後のボリューム一覧。</summary>
    member _.FilteredVolumes = filteredVolumes

    /// <summary>現在選択中のボリューム。</summary>
    member this.SelectedVolume
        with get () = selectedVolume
        and set value =
            if updatingVolumeList && isNull value && not (isNull selectedVolume) then
                ()
            elif this.SetProperty(&selectedVolume, value) then
                this.OnPropertyChanged(nameof this.HasSelectedVolume)

    /// <summary>ボリュームが 1 件以上選択されているか。</summary>
    member _.HasSelectedVolume = not (isNull selectedVolume)

    /// <summary>未フィルタのボリュームが 1 件以上存在するか。</summary>
    member _.HasVolumes = volumes.Count > 0

    /// <summary>絞り込み結果が 0 件かどうか。</summary>
    member _.HasNoFilteredVolumes = filteredVolumes.Count = 0

    /// <summary>未使用ボリュームのみ表示するか。</summary>
    member this.ShowUnusedVolumesOnly
        with get () = showUnusedVolumesOnly
        and set value =
            if this.SetProperty(&showUnusedVolumesOnly, value) then
                this.ApplyVolumeFilters()

    /// <summary>ボリューム名・コンテナ名での検索クエリ。</summary>
    member this.VolumeSearchText
        with get () = volumeSearchText
        and set value =
            let next = if isNull value then "" else value
            if this.SetProperty(&volumeSearchText, next) then
                this.ApplyVolumeFilters()

    /// <summary>ボリューム一覧の絞り込みを適用する。</summary>
    member internal this.ApplyVolumeFilters() =
        let prevSelectedName = if isNull selectedVolume then None else Some selectedVolume.Name
        let q = volumeSearchText.Trim().ToLowerInvariant()
        let matches (v: VolumeInfo) =
            let unusedOk = (not showUnusedVolumesOnly) || v.IsUnused
            let searchOk =
                String.IsNullOrWhiteSpace(q)
                || v.Name.ToLowerInvariant().Contains(q)
                || v.DisplayName.ToLowerInvariant().Contains(q)
                || v.Driver.ToLowerInvariant().Contains(q)
                || v.AttachedContainersSummary.ToLowerInvariant().Contains(q)
            unusedOk && searchOk

        let desired = volumes |> Seq.filter matches |> Seq.toList
        let desiredNames = desired |> List.map (fun v -> v.Name) |> Set.ofList
        updatingVolumeList <- true
        try
            filteredVolumes.Clear()
            for v in desired do
                filteredVolumes.Add(v)
        finally
            updatingVolumeList <- false
        match prevSelectedName with
        | Some n when desiredNames.Contains(n) ->
            selectedVolume <- filteredVolumes |> Seq.tryFind (fun v -> v.Name = n) |> Option.defaultValue null
            this.OnPropertyChanged(nameof this.SelectedVolume)
            this.OnPropertyChanged(nameof this.HasSelectedVolume)
        | _ ->
            if not (isNull selectedVolume) then
                this.SelectedVolume <- null
        this.OnPropertyChanged(nameof this.HasNoFilteredVolumes)

    /// <summary>選択中コンテナがボリュームをマウントしているか。</summary>
    member _.HasSelectedContainerMounts =
        not (isNull selectedContainer) && selectedContainer.HasVolumeMounts

    /// <summary>選択中コンテナのマウント中ボリュームと判明容量の要約テキスト。</summary>
    member _.SelectedContainerMountsText =
        match selectedContainer with
        | null -> ""
        | c ->
            let mounts = c.VolumeMounts
            if List.isEmpty mounts then ""
            else
                mounts
                |> List.map (fun m ->
                    let vName = if String.IsNullOrWhiteSpace(m.VolumeName) then m.Source else m.VolumeName
                    let volOpt = volumes |> Seq.tryFind (fun v -> String.Equals(v.Name, vName, StringComparison.OrdinalIgnoreCase))
                    let shortName =
                        match volOpt with
                        | Some v -> v.DisplayName
                        | None -> VolumeParser.shortDisplayName vName (VolumeParser.isAnonymousName vName None)
                    let sizeSuffix =
                        match volOpt with
                        | Some v -> sprintf " (%s)" v.SizeText
                        | None -> ""
                    if String.IsNullOrWhiteSpace(m.Destination) then sprintf "%s%s" shortName sizeSuffix
                    else sprintf "%s → %s%s" shortName m.Destination sizeSuffix)
                |> String.concat ", "

    member _.TabVolumesLabel = localize "ボリューム" "Volumes"
    member _.VolumeNameLabel = localize "ボリューム名" "Volume name"
    member _.VolumeDriverLabel = localize "ドライバ" "Driver"
    member _.VolumeMountpointLabel = localize "マウント先" "Mount point"
    member _.VolumeSizeLabel = localize "使用容量" "Size"
    member _.VolumeAttachedLabel = localize "紐付けコンテナ" "Attached containers"
    member _.VolumeKindLabel = localize "種別" "Type"
    member _.VolumeReadOnlyNotice = localize "読み取り専用（削除・prune は実行しません）" "Read-only (no delete or prune is performed)"
    member _.VolumeUnusedOnlyLabel = localize "未使用のみ表示" "Show unused only"
    member _.VolumeSearchPlaceholder = localize "ボリューム名・コンテナ名で絞り込み..." "Filter by volume or container name..."
    member _.VolumesEmptyText = localize "ボリュームはありません。" "No volumes found."
    member _.VolumeDetailPlaceholder = localize "ボリュームを選択すると詳細と紐付けコンテナを表示します。" "Select a volume to inspect its details and attached containers."
    member _.VolumeDetailTitle = localize "ボリューム詳細" "Volume details"
    member _.MountedVolumesLabel = localize "マウント中ボリューム" "Mounted volumes"

    member _.VolumeSummaryText =
        let totalBytes = volumes |> Seq.choose (fun v -> v.SizeBytes) |> Seq.sum
        format "{0}件のボリューム / 合計 {1}" "{0} volumes / Total {1}" [| box volumes.Count; box (VolumeParser.formatByteSize totalBytes) |]

    member _.UnusedVolumeSummaryText =
        let unusedList = volumes |> Seq.filter (fun v -> v.IsUnused) |> Seq.toList
        let unusedBytes = unusedList |> List.choose (fun v -> v.SizeBytes) |> List.sum
        format "未使用: {0}件 ({1})" "Unused: {0} ({1})" [| box unusedList.Length; box (VolumeParser.formatByteSize unusedBytes) |]

    /// <summary>ボリューム一覧のみ手動再取得する。</summary>
    member _.RefreshVolumesAsync() = async {
        if dockerDesktopState = "running" then
            let! _ = refreshVolumesCore()
            ()
    }

    // --- 🚨 監視アラート履歴・アプリ内バナー・OS通知（v1.2） ---

    /// <summary>OS デスクトップ通知（ベストエフォート）を発行する。</summary>
    member private _.SendDesktopNotification(title: string, message: string) =
        match osNotifier with
        | Some custom -> try custom title message with _ -> ()
        | None ->
            if defaultArg startAutomatically true then
                OsNotifier.sendDesktopNotification title message

    /// <summary>アラートを SQLite に保存し、PC バナー・OS通知・スマホ SSE へ配信する。</summary>
    member internal this.RecordAndNotifyAlert(entry: AlertEventEntry, emitToastAndOs: bool) =
        let saved = AlertHistoryRepository.Record(entry)
        alertHistoryEntries <- AlertHistoryRepository.GetRecent()
        activeAlertBanner <- Some saved
        this.NotifyAlertProperties()
        if mobileServer.IsRunning then
            mobileServer.PushAlert(saved)
        if emitToastAndOs && notificationsEnabled then
            let title = Localization.translateKey languageCode saved.TitleKey
            let msg =
                if String.IsNullOrWhiteSpace(saved.TargetName) then saved.Message
                else sprintf "%s: %s" saved.TargetName saved.Message
            alertRaised.Trigger(title, msg)
            this.SendDesktopNotification(title, msg)

    member private this.NotifyAlertProperties() =
        this.OnPropertyChanged(nameof this.AlertHistory)
        this.OnPropertyChanged(nameof this.HasAlertHistory)
        this.OnPropertyChanged(nameof this.HasNoAlertHistory)
        this.OnPropertyChanged(nameof this.HasActiveAlertBanner)
        this.OnPropertyChanged(nameof this.ActiveAlertBannerTitle)
        this.OnPropertyChanged(nameof this.ActiveAlertBannerMessage)
        this.OnPropertyChanged(nameof this.ActiveAlertBannerSeverityBadge)
        this.OnPropertyChanged(nameof this.ActiveAlertBannerBrush)

    /// <summary>アラート履歴表示用一覧（最新順）。</summary>
    member _.AlertHistory : AlertDisplayItem list =
        alertHistoryEntries |> List.map (fun e -> AlertDisplayItem(e, languageCode))

    member _.HasAlertHistory = not (List.isEmpty alertHistoryEntries)
    member _.HasNoAlertHistory = List.isEmpty alertHistoryEntries
    member _.AlertHistoryHeader = localize "アラート履歴" "Alert history"
    member _.AlertHistoryEmptyText = localize "アラート履歴はまだありません。" "No alerts recorded yet."

    member _.HasActiveAlertBanner = activeAlertBanner.IsSome
    member _.ActiveAlertBannerSeverityBadge =
        match activeAlertBanner with
        | Some a -> AlertMonitor.severityBadge a.Severity
        | None -> ""
    member _.ActiveAlertBannerTitle =
        match activeAlertBanner with
        | Some a ->
            sprintf "%s (%s)" (Localization.translateKey languageCode a.TitleKey) (AlertMonitor.formatTimestamp a.OccurredAt)
        | None -> ""
    member _.ActiveAlertBannerMessage =
        match activeAlertBanner with
        | Some a ->
            if String.IsNullOrWhiteSpace(a.TargetName) then a.Message
            else sprintf "%s — %s" a.TargetName a.Message
        | None -> ""
    member _.ActiveAlertBannerBrush : IBrush =
        match activeAlertBanner with
        | Some a when a.Severity = AlertSeverity.Danger -> Brushes.Crimson
        | Some _ -> Brushes.DarkGoldenrod
        | None -> Brushes.Transparent
    member _.ActiveAlertBannerDismissTooltip = localize "アラートを閉じる" "Dismiss alert"

    /// <summary>画面上部のアラートバナーを閉じる。</summary>
    member this.DismissAlertBanner() =
        activeAlertBanner <- None
        this.NotifyAlertProperties()

    // --- ❓ 初心者向け FAQ ダイアログ（v1.2） ---

    member this.IsFaqDialogOpen
        with get () = isFaqDialogOpen
        and set value = this.SetProperty(&isFaqDialogOpen, value) |> ignore

    member private _.RebuildFaqCategoryOptions() =
        let allOpt = ChoiceOption("all", localize "すべて" "All")
        let catOpts =
            FaqData.categoriesForLanguage selectedFaqLangKey
            |> List.map (fun c -> ChoiceOption(c.Id, sprintf "%s %s" c.Icon c.Title))
        faqCategoryOptions <- (allOpt :: catOpts) |> List.toArray

    member this.OpenFaqDialog() =
        selectedFaqLangKey <- FaqData.resolveFaqLanguage languageCode
        this.NotifyFaqProperties()
        this.IsFaqDialogOpen <- true

    member this.CloseFaqDialog() =
        this.IsFaqDialogOpen <- false

    member _.FaqLanguageOptions = faqLanguageOptions

    member this.SelectedFaqLanguage
        with get () =
            faqLanguageOptions
            |> Array.tryFind (fun o -> o.Key = selectedFaqLangKey)
            |> Option.defaultValue faqLanguageOptions.[0]
        and set (value: ChoiceOption) =
            if not (isNull value) && selectedFaqLangKey <> value.Key then
                selectedFaqLangKey <- FaqData.resolveFaqLanguage value.Key
                selectedFaqCategoryKey <- "all"
                this.NotifyFaqProperties()

    member _.FaqCategoryOptions = faqCategoryOptions

    member this.SelectedFaqCategory
        with get () =
            faqCategoryOptions
            |> Array.tryFind (fun o -> o.Key = selectedFaqCategoryKey)
            |> Option.defaultValue faqCategoryOptions.[0]
        and set (value: ChoiceOption) =
            if not (isNull value) && selectedFaqCategoryKey <> value.Key then
                selectedFaqCategoryKey <- value.Key
                this.OnPropertyChanged(nameof this.SelectedFaqCategory)
                this.OnPropertyChanged(nameof this.FilteredFaqItems)
                this.OnPropertyChanged(nameof this.HasNoFaqMatches)

    member this.FaqSearchText
        with get () = faqSearchText
        and set value =
            let next = if isNull value then "" else value
            if this.SetProperty(&faqSearchText, next) then
                this.OnPropertyChanged(nameof this.FilteredFaqItems)
                this.OnPropertyChanged(nameof this.HasNoFaqMatches)

    member _.FilteredFaqItems : FaqDisplayItem list =
        let catOpt = if selectedFaqCategoryKey = "all" then None else Some selectedFaqCategoryKey
        FaqData.filterItems selectedFaqLangKey catOpt faqSearchText

    member this.HasNoFaqMatches = List.isEmpty this.FilteredFaqItems
    member _.ShowFaqEnglishFallbackNote = FaqData.isEnglishFallback languageCode && selectedFaqLangKey = "en-US"
    member _.FaqEnglishFallbackText = localize "この言語では FAQ 本文を英語で表示しています。" "FAQ articles are displayed in English for this language."
    member _.FaqDialogTitle = localize "初心者向け FAQ" "Beginner FAQ"
    member _.FaqDialogSubtitle = localize "コンテナ・Compose・ボリューム・スマホ連携の基本Q&A" "Essential Q&A for containers, Compose, volumes, and phone QR"
    member _.FaqSearchPlaceholder = localize "キーワードで絞り込み..." "Filter by keyword..."
    member _.FaqTtsHintText = localize "音声読み上げ（🔊）はスマホ連携ページの FAQ タブで利用できます。" "Read-aloud (🔊) is available in the FAQ tab of the Phone QR page."
    member _.FaqEmptyText = localize "一致する質問が見つかりませんでした。" "No matching questions were found."

    member private this.NotifyFaqProperties() =
        this.RebuildFaqCategoryOptions()
        this.OnPropertyChanged(nameof this.SelectedFaqLanguage)
        this.OnPropertyChanged(nameof this.FaqCategoryOptions)
        this.OnPropertyChanged(nameof this.SelectedFaqCategory)
        this.OnPropertyChanged(nameof this.FilteredFaqItems)
        this.OnPropertyChanged(nameof this.HasNoFaqMatches)
        this.OnPropertyChanged(nameof this.ShowFaqEnglishFallbackNote)
        this.OnPropertyChanged(nameof this.FaqEnglishFallbackText)
        this.OnPropertyChanged(nameof this.FaqDialogTitle)
        this.OnPropertyChanged(nameof this.FaqDialogSubtitle)
        this.OnPropertyChanged(nameof this.FaqSearchPlaceholder)
        this.OnPropertyChanged(nameof this.FaqTtsHintText)
        this.OnPropertyChanged(nameof this.FaqEmptyText)

    // --- 📱 スマホ連携 QR セッション（v1.2） ---

    member private this.BuildMobileSnapshot() : MobileDashboardSnapshot =
        let containerDtos =
            containers
            |> Seq.map (fun c ->
                let stateKey =
                    if c.Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) then "running"
                    elif c.Status.StartsWith("Restarting", StringComparison.OrdinalIgnoreCase) then "restarting"
                    else "stopped"
                let dangerCount, warnCount, topTitles =
                    Map.tryFind c.Id latestDiagnosticsSummaries |> Option.defaultValue (0, 0, [])
                {
                    Id = c.Id
                    Name = c.Name
                    Status = c.Status
                    StateKey = stateKey
                    Image = c.Image
                    Ports = c.PublishedPorts
                    ResourceSummary = c.ResourceSummary
                    MountsSummary = c.MountedVolumesSummary
                    HasDangerDiagnostics = dangerCount > 0
                    HasWarningDiagnostics = warnCount > 0
                    DiagnosticsNote = String.concat ", " topTitles
                })
            |> Seq.toList
        let volumeDtos =
            volumes
            |> Seq.map (fun v ->
                {
                    Name = v.Name
                    DisplayName = v.DisplayName
                    Driver = v.Driver
                    SizeText = v.SizeText
                    SizeBytes = v.SizeBytes
                    SizeLevel = v.SizeLevel
                    IsAnonymous = v.IsAnonymous
                    IsUnused = v.IsUnused
                    AttachedSummary = v.AttachedContainersSummary
                })
            |> Seq.toList
        let totalVolBytes = volumes |> Seq.choose (fun v -> v.SizeBytes) |> Seq.sum
        let unusedVols = volumes |> Seq.filter (fun v -> v.IsUnused) |> Seq.toList
        let unusedVolBytes = unusedVols |> List.choose (fun v -> v.SizeBytes) |> List.sum
        let alertDtos =
            alertHistoryEntries
            |> List.truncate 50
            |> List.map (fun a ->
                {
                    Id = a.Id
                    OccurredAt = a.OccurredAt
                    OccurredAtLocal = AlertMonitor.formatTimestamp a.OccurredAt
                    Severity = AlertMonitor.severityToDbString a.Severity
                    Category = AlertMonitor.categoryToDbString a.Category
                    TargetName = a.TargetName
                    Title = Localization.translateKey languageCode a.TitleKey
                    Message = a.Message
                })
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            LanguageCode = languageCode
            EngineMode = ContainerRuntime.displayName activeRuntime
            EngineRunning = (dockerDesktopState = "running")
            Containers = containerDtos
            Volumes = volumeDtos
            TotalVolumeBytes = totalVolBytes
            UnusedVolumeCount = unusedVols.Length
            UnusedVolumeBytes = unusedVolBytes
            Alerts = alertDtos
        }

    member private this.PushMobileSnapshotIfRunning() =
        if mobileServer.IsRunning then
            mobileServer.UpdateSnapshot(this.BuildMobileSnapshot())

    member private this.RefreshLanIpCandidates() =
        let prevAddr = if isNull selectedLanIp then None else Some selectedLanIp.Key
        let detected = NetworkHelpers.detectLocalIpCandidates ()
        lanIpCandidates.Clear()
        for c in detected do
            lanIpCandidates.Add(ChoiceOption(c.Address, c.Label))
        selectedLanIp <-
            match prevAddr with
            | Some addr ->
                lanIpCandidates |> Seq.tryFind (fun o -> o.Key = addr) |> Option.defaultWith (fun () -> if lanIpCandidates.Count > 0 then lanIpCandidates.[0] else null)
            | None ->
                if lanIpCandidates.Count > 0 then lanIpCandidates.[0] else null
        this.OnPropertyChanged(nameof this.LanIpCandidates)
        this.OnPropertyChanged(nameof this.SelectedLanIp)
        this.OnPropertyChanged(nameof this.IsLoopbackLanIpSelected)

    member private this.UpdateMobileUrlAndQr(st: MobileSessionStatus) =
        let ip = if isNull selectedLanIp then "127.0.0.1" else selectedLanIp.Key
        let url =
            if st.IsRunning then mobileServer.GetQrUrl(ip)
            else ""
        mobileSessionUrl <- url
        mobileQrBitmap <-
            if String.IsNullOrWhiteSpace(url) then null
            else
                try QrCodeGenerator.renderQrBitmap url 6 4 :> IImage
                with _ -> null
        this.OnPropertyChanged(nameof this.MobileSessionUrl)
        this.OnPropertyChanged(nameof this.MobileQrBitmap)

    member private this.ApplyMobileSessionStatus(st: MobileSessionStatus) =
        isMobileServerRunning <- st.IsRunning
        mobileSessionStatusText <-
            if st.IsRunning then
                match st.LockedClientIp with
                | Some ip -> format "1対1接続中: {0}" "1-on-1 connected: {0}" [| box ip |]
                | None -> localize "待機中（最初にQRを読み取った1台のみ接続できます）" "Waiting for phone (only the first scanner can connect)"
            elif st.TimedOut then
                localize "タイムアウトで切断されました" "Disconnected due to inactivity timeout"
            else
                localize "停止中（ボタン操作時のみ起動します）" "Stopped (starts only when you click Start session)"
        mobileLockedClientText <- st.LockedClientIp |> Option.defaultValue ""
        this.UpdateMobileUrlAndQr(st)
        this.OnPropertyChanged(nameof this.IsMobileServerRunning)
        this.OnPropertyChanged(nameof this.CanStartMobileSession)
        this.OnPropertyChanged(nameof this.MobileSessionStatusText)
        this.OnPropertyChanged(nameof this.MobileLockedClientText)
        this.OnPropertyChanged(nameof this.HasMobileLockedClient)

    member this.IsMobileSessionDialogOpen
        with get () = isMobileSessionDialogOpen
        and set value = this.SetProperty(&isMobileSessionDialogOpen, value) |> ignore

    member this.OpenMobileSessionDialog() =
        this.RefreshLanIpCandidates()
        this.ApplyMobileSessionStatus(mobileServer.GetStatus())
        this.NotifyV12Properties()
        this.IsMobileSessionDialogOpen <- true

    member this.CloseMobileSessionDialog() =
        this.IsMobileSessionDialogOpen <- false

    member _.LanIpCandidates = lanIpCandidates

    member this.SelectedLanIp
        with get () = selectedLanIp
        and set (value: ChoiceOption) =
            if this.SetProperty(&selectedLanIp, value) then
                this.UpdateMobileUrlAndQr(mobileServer.GetStatus())
                this.OnPropertyChanged(nameof this.IsLoopbackLanIpSelected)

    member _.IsLoopbackLanIpSelected =
        not (isNull selectedLanIp) && selectedLanIp.Key = "127.0.0.1"
    member _.MobileLoopbackHintText =
        localize
            "このループバックアドレスは同じPC内の確認用です。スマホからは 192.168.x.x 等を選択してください。"
            "Loopback (127.0.0.1) is for local testing on this PC. Select a LAN IP such as 192.168.x.x for phones."

    member _.IsMobileServerRunning = isMobileServerRunning
    member _.CanStartMobileSession = not isMobileServerRunning
    member _.MobileSessionUrl = mobileSessionUrl
    member _.MobileQrBitmap = mobileQrBitmap
    member _.MobileSessionStatusText = mobileSessionStatusText
    member _.MobileLockedClientText = mobileLockedClientText
    member _.HasMobileLockedClient = not (String.IsNullOrWhiteSpace(mobileLockedClientText))
    member _.MobileSessionError = mobileSessionError
    member _.HasMobileSessionError = not (String.IsNullOrWhiteSpace(mobileSessionError))

    member _.MobileSessionDialogTitle = localize "スマホ連携 (QR)" "Phone QR view"
    member _.MobileSessionSubtitle = localize "QRをスマホで読み取ると同じWi-Fi内から閲覧専用ページを開けます。" "Scan the QR code with your phone on the same Wi-Fi to open the read-only status page."
    member _.MobileSessionReadOnlyNotice = localize "同一 Wi-Fi 内・閲覧専用（操作や外部公開は行いません）。無通信15分で自動切断します。" "Same Wi-Fi only · Read-only (no control or public exposure). Auto-disconnects after 15 min of inactivity."
    member _.MobileLanIpLabel = localize "接続用 IP アドレス" "Connection IP address"
    member _.MobileUrlLabel = localize "接続 URL" "Session URL"
    member _.MobileStartLabel = localize "セッション開始" "Start session"
    member _.MobileStopLabel = localize "セッション停止" "Stop session"
    member _.MobileReissueLabel = localize "QR再発行 (ロック解除)" "Reissue QR (unlock)"

    /// <summary>ユーザー操作に応じてスマホ閲覧用のローカルサーバーを起動する。</summary>
    member this.StartMobileSession() =
        this.RefreshLanIpCandidates()
        mobileSessionError <- ""
        this.OnPropertyChanged(nameof this.MobileSessionError)
        this.OnPropertyChanged(nameof this.HasMobileSessionError)
        mobileServer.UpdateSnapshot(this.BuildMobileSnapshot())
        match mobileServer.Start() with
        | Ok _ ->
            this.ApplyMobileSessionStatus(mobileServer.GetStatus())
        | Error err ->
            mobileSessionError <- format "サーバー起動に失敗しました: {0}" "Failed to start server: {0}" [| box err |]
            this.OnPropertyChanged(nameof this.MobileSessionError)
            this.OnPropertyChanged(nameof this.HasMobileSessionError)

    /// <summary>スマホ閲覧用のローカルサーバーを停止する。</summary>
    member this.StopMobileSession() =
        mobileSessionError <- ""
        this.OnPropertyChanged(nameof this.MobileSessionError)
        this.OnPropertyChanged(nameof this.HasMobileSessionError)
        mobileServer.Stop()
        this.ApplyMobileSessionStatus(mobileServer.GetStatus())

    /// <summary>QR トークンを再発行し、既存の 1 対 1 端末ロックを解除する。</summary>
    member this.ReissueMobileSession() =
        if mobileServer.IsRunning then
            let st = mobileServer.ReissueSession()
            this.ApplyMobileSessionStatus(st)

    member private this.NotifyV12Properties() =
        this.OnPropertyChanged(nameof this.TabVolumesLabel)
        this.OnPropertyChanged(nameof this.VolumeNameLabel)
        this.OnPropertyChanged(nameof this.VolumeDriverLabel)
        this.OnPropertyChanged(nameof this.VolumeMountpointLabel)
        this.OnPropertyChanged(nameof this.VolumeSizeLabel)
        this.OnPropertyChanged(nameof this.VolumeAttachedLabel)
        this.OnPropertyChanged(nameof this.VolumeKindLabel)
        this.OnPropertyChanged(nameof this.VolumeReadOnlyNotice)
        this.OnPropertyChanged(nameof this.VolumeUnusedOnlyLabel)
        this.OnPropertyChanged(nameof this.VolumeSearchPlaceholder)
        this.OnPropertyChanged(nameof this.VolumesEmptyText)
        this.OnPropertyChanged(nameof this.VolumeDetailPlaceholder)
        this.OnPropertyChanged(nameof this.VolumeDetailTitle)
        this.OnPropertyChanged(nameof this.MountedVolumesLabel)
        this.OnPropertyChanged(nameof this.VolumeSummaryText)
        this.OnPropertyChanged(nameof this.UnusedVolumeSummaryText)
        this.OnPropertyChanged(nameof this.SelectedContainerMountsText)
        this.NotifyAlertProperties()
        this.OnPropertyChanged(nameof this.AlertHistoryHeader)
        this.OnPropertyChanged(nameof this.AlertHistoryEmptyText)
        this.OnPropertyChanged(nameof this.ActiveAlertBannerDismissTooltip)
        this.NotifyFaqProperties()
        this.OnPropertyChanged(nameof this.MobileSessionDialogTitle)
        this.OnPropertyChanged(nameof this.MobileSessionSubtitle)
        this.OnPropertyChanged(nameof this.MobileSessionReadOnlyNotice)
        this.OnPropertyChanged(nameof this.MobileLanIpLabel)
        this.OnPropertyChanged(nameof this.MobileUrlLabel)
        this.OnPropertyChanged(nameof this.MobileStartLabel)
        this.OnPropertyChanged(nameof this.MobileStopLabel)
        this.OnPropertyChanged(nameof this.MobileReissueLabel)
        this.OnPropertyChanged(nameof this.MobileLoopbackHintText)

    interface IDisposable with
        /// <summary>自動更新タイマー・ログ追尾・スマホサーバーを停止・解放する。</summary>
        member _.Dispose() =
            timer.Stop()
            stopLogFollow()
            stopComposeLogFollow()
            cancelComposeLogFetch()
            (mobileServer :> IDisposable).Dispose()
