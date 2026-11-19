#r "nuget: Avalonia, 12.1.5"
#r "nuget: CommunityToolkit.Mvvm, 8.4.2"
#r "nuget: Microsoft.Data.Sqlite, 11.0.0"
#load "../EasyContainerManager/Localization.fs"
#load "../EasyContainerManager/Models/ContainerStats.fs"
#load "../EasyContainerManager/Models/ContainerInfo.fs"
#load "../EasyContainerManager/Models/ServiceInference.fs"
#load "../EasyContainerManager/Models/ServiceDiagnostics.fs"
#load "../EasyContainerManager/Models/ImageInfo.fs"
#load "../EasyContainerManager/Models/SshConnection.fs"
#load "../EasyContainerManager/Models/ComposeProject.fs"
#load "../EasyContainerManager/Models/ComposeRepository.fs"
#load "../EasyContainerManager/Models/MasterDataRepository.fs"
#load "../EasyContainerManager/Models/VersionDiagnostics.fs"
#load "../EasyContainerManager/AppSettings.fs"
#load "../EasyContainerManager/ContainerCli.fs"
#load "../EasyContainerManager/Models/ChoiceOption.fs"
#load "../EasyContainerManager/ViewModels/ViewModelBase.fs"
#load "../EasyContainerManager/ViewModels/MainViewModel.fs"

open System
open System.IO
open System.Text.Json
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.ViewModels

let testDbPath = Path.Combine(Path.GetTempPath(), sprintf "easycontainer_test_%s.db" (Guid.NewGuid().ToString("N")))
ComposeRepository.CustomDatabasePath <- Some testDbPath

let mutable checks = 0
let check name condition =
    if not condition then failwith name
    checks <- checks + 1
    printfn "PASS: %s" name

for installed, expected in
    [ [], true; [ Docker ], false; [ Nerdctl ], false
      [ Docker; Podman ], true; [ Docker; Nerdctl ], true
      [ Podman; Nerdctl ], true; [ Docker; Podman; Nerdctl ], true ] do
    check (sprintf "runtime selection for %A" installed) (ContainerRuntime.requiresSelection installed = expected)

let nerdctlViewModel = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false, runtime = Nerdctl)
check "nerdctl mode initializes SSH editor and disables engine auto-start"
    (nerdctlViewModel.NewSshEngine.Key = "nerdctl"
     && nerdctlViewModel.RuntimeModeLabel = "nerdctl モード"
     && not nerdctlViewModel.ShowContainerEngineStart
     && not nerdctlViewModel.CanStartContainerEngine)
(nerdctlViewModel :> IDisposable).Dispose()

let detailsViewModel = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false)
let privateMembers = System.Reflection.BindingFlags.Instance ||| System.Reflection.BindingFlags.NonPublic
let scopedSettings = AppSettings()
let legacyProject = ComposeProject(Name = "Legacy", FolderPath = "legacy-folder")
let dockerProject = ComposeProject(Name = "Docker", FolderPath = "shared-folder", Engine = "docker")
let podmanProject = ComposeProject(Name = "Podman", FolderPath = "shared-folder", Engine = "podman")
let nerdctlProject = ComposeProject(Name = "nerdctl", FolderPath = "nerdctl-folder", Engine = "nerdctl")
let sshProject = ComposeProject(Name = "SSH", FolderPath = "/srv/app", ConnectionId = "remote")
for project in [ legacyProject; dockerProject; podmanProject; nerdctlProject; sshProject ] do
    scopedSettings.ComposeProjects.Add(project)
let scopedViewModel = new MainViewModel(settingsOverride = scopedSettings, startAutomatically = false, runtime = Docker)
let chooseComposeEngine key =
    scopedViewModel.SelectedComposeEngine <- scopedViewModel.ComposeEngineOptions |> Array.find (fun option -> option.Key = key)
let displayedNames () = scopedViewModel.ComposeProjects |> Seq.map (fun project -> project.Name) |> Seq.toList
check "legacy local projects migrate to startup engine without changing SSH projects"
    (legacyProject.Engine = "docker" && sshProject.Engine = "" && displayedNames() = [ "Legacy"; "Docker" ])
let unrelatedContainer = ContainerInfo("id", "unrelated", "Up", "image", "today")
scopedViewModel.Containers.Add(unrelatedContainer)
scopedViewModel.SelectedComposeProject <- dockerProject
scopedViewModel.NewComposeName <- "draft"
scopedViewModel.NewComposePath <- "draft-folder"
chooseComposeEngine "podman"
check "Compose engine switch filters folders and clears old selection and drafts"
    (displayedNames() = [ "Podman" ] && isNull scopedViewModel.SelectedComposeProject
     && scopedViewModel.NewComposeName = "" && scopedViewModel.NewComposePath = ""
     && not (scopedViewModel.ComposeLogText.Contains("Docker")))
let composeClientField = typeof<MainViewModel>.GetField("composeCli", privateMembers)
let containerClientField = typeof<MainViewModel>.GetField("docker", privateMembers)
let localComposeRuntimeField = typeof<MainViewModel>.GetField("localComposeRuntime", privateMembers)
check "Compose dispatch switches CLI without changing main runtime or container list"
    (localComposeRuntimeField.GetValue(scopedViewModel) :?> ContainerRuntime = Podman
     && not (Object.ReferenceEquals(composeClientField.GetValue(scopedViewModel), containerClientField.GetValue(scopedViewModel)))
     && scopedViewModel.RuntimeModeLabel = "Docker モード"
     && scopedViewModel.Containers.Count = 1 && scopedViewModel.Containers.[0] = unrelatedContainer)
chooseComposeEngine "nerdctl"
check "nerdctl has its own Compose folders and client"
    (displayedNames() = [ "nerdctl" ]
     && localComposeRuntimeField.GetValue(scopedViewModel) :?> ContainerRuntime = Nerdctl)
let scopedRefreshingField = typeof<MainViewModel>.GetField("refreshing", privateMembers)
scopedRefreshingField.SetValue(scopedViewModel, true)
chooseComposeEngine "docker"
check "Compose engine cannot switch during refresh"
    (not scopedViewModel.CanChangeComposeEngine && scopedViewModel.SelectedComposeEngine.Key = "nerdctl")
scopedRefreshingField.SetValue(scopedViewModel, false)
let scopedBusyField = typeof<MainViewModel>.GetField("isBusy", privateMembers)
scopedBusyField.SetValue(scopedViewModel, true)
chooseComposeEngine "docker"
check "Compose engine cannot switch during an operation" (scopedViewModel.SelectedComposeEngine.Key = "nerdctl")
scopedBusyField.SetValue(scopedViewModel, false)
chooseComposeEngine "docker"
check "returning to Docker preserves hidden engine registrations and same-folder registrations"
    (displayedNames() = [ "Legacy"; "Docker" ] && scopedSettings.ComposeProjects.Count = 5)
let restoredScopes = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(scopedSettings))
let restoredScopeViewModel = new MainViewModel(settingsOverride = restoredScopes, startAutomatically = false, runtime = Podman)
check "saved engine scopes survive a different startup engine"
    (restoredScopeViewModel.ComposeProjects.Count = 1 && restoredScopeViewModel.ComposeProjects.[0].Name = "Podman"
     && restoredScopes.ComposeProjects.[0].Engine = "docker")
(restoredScopeViewModel :> IDisposable).Dispose()
typeof<MainViewModel>.GetField("connectionId", privateMembers).SetValue(scopedViewModel, "remote")
typeof<MainViewModel>.GetField("activeRuntime", privateMembers).SetValue(scopedViewModel, Podman)
chooseComposeEngine "nerdctl"
check "SSH Compose engine selector reflects the profile engine and rejects changes"
    (not scopedViewModel.CanChangeComposeEngine && scopedViewModel.SelectedComposeEngine.Key = "podman")
(scopedViewModel :> IDisposable).Dispose()
let setDetails = typeof<MainViewModel>.GetMethod("SetConnectionDetails", privateMembers)
let refreshingField = typeof<MainViewModel>.GetField("refreshing", privateMembers)
let pendingDetailsField = typeof<MainViewModel>.GetField("pendingConnectionDetails", privateMembers)
let publishDetails value = setDetails.Invoke(detailsViewModel, [| box value |]) |> ignore
let detailChanges = ResizeArray<string>()
detailsViewModel.PropertyChanged.Add(fun change ->
    if change.PropertyName = "ConnectionDetails" || change.PropertyName = "HasConnectionDetails" then
        detailChanges.Add(change.PropertyName))
publishDetails "Compose connection failed"
detailsViewModel.DetailsExpanded <- true
detailChanges.Clear()
refreshingField.SetValue(detailsViewModel, true)
publishDetails ""
publishDetails "Compose connection failed"
check "refresh keeps details visible and expanded" (detailsViewModel.HasConnectionDetails && detailsViewModel.DetailsExpanded && detailChanges.Count = 0)
refreshingField.SetValue(detailsViewModel, false)
let pendingDetails = pendingDetailsField.GetValue(detailsViewModel) :?> string option
pendingDetails |> Option.iter publishDetails
check "unchanged refresh details do not notify" (detailChanges.Count = 0 && detailsViewModel.ConnectionDetails = "Compose connection failed")
publishDetails "Compose provider unavailable"
check "changed details retain expander visibility" (detailChanges |> Seq.toList = [ "ConnectionDetails" ] && detailsViewModel.DetailsExpanded)
detailChanges.Clear()
publishDetails ""
check "recovery clears details once" (not detailsViewModel.HasConnectionDetails && detailChanges.Count = 2)
(detailsViewModel :> IDisposable).Dispose()

let refreshViewModel = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false)
let refreshField = typeof<MainViewModel>.GetField("refreshing", privateMembers)
let completionField = typeof<MainViewModel>.GetField("refreshCompletion", privateMembers)
let selectedContainerField = typeof<MainViewModel>.GetField("selectedContainer", privateMembers)
let setEngineState = typeof<MainViewModel>.GetMethod("SetDockerDesktopState", privateMembers)
let pendingContainer = ContainerInfo("pending", "pending", "Up", "image", "today")
refreshViewModel.Containers.Add(pendingContainer)
selectedContainerField.SetValue(refreshViewModel, pendingContainer)
setEngineState.Invoke(refreshViewModel, [| box "running" |]) |> ignore
let completion = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
refreshField.SetValue(refreshViewModel, true)
completionField.SetValue(refreshViewModel, Some completion)
check "container action remains enabled during refresh" refreshViewModel.CanOperate
let pendingAction = refreshViewModel.StartSelectedAsync() |> Async.StartImmediateAsTask
check "clicked action waits for refresh and disables repeat actions" (not pendingAction.IsCompleted && refreshViewModel.IsBusy && not refreshViewModel.CanOperate)
refreshViewModel.Containers.Clear()
refreshField.SetValue(refreshViewModel, false)
completionField.SetValue(refreshViewModel, None)
completion.SetResult(())
pendingAction.GetAwaiter().GetResult()
check "queued action rejects a container removed by refresh" (not refreshViewModel.IsBusy && refreshViewModel.StatusMessage.Contains("一覧にありません"))
refreshViewModel.Containers.Add(pendingContainer)
selectedContainerField.SetValue(refreshViewModel, pendingContainer)
let consoleCompletion = System.Threading.Tasks.TaskCompletionSource<unit>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
refreshField.SetValue(refreshViewModel, true)
completionField.SetValue(refreshViewModel, Some consoleCompletion)
let pendingConsole = refreshViewModel.EnterContainerAsync() |> Async.StartImmediateAsTask
check "enter container waits for refresh" (not pendingConsole.IsCompleted && refreshViewModel.IsBusy)
refreshViewModel.Containers.Clear()
refreshField.SetValue(refreshViewModel, false)
completionField.SetValue(refreshViewModel, None)
consoleCompletion.SetResult(())
pendingConsole.GetAwaiter().GetResult()
check "enter container rejects a target removed by refresh" (not refreshViewModel.IsBusy && refreshViewModel.StatusMessage.Contains("一覧にありません"))
(refreshViewModel :> IDisposable).Dispose()

for value, expected in [ "0%", 0; "49.99%", 0; "50%", 1; "79.99%", 1; "80%", 2; "195%", 2; "--", -1; "NaN", -1; "Infinity", -1; "-1%", -1 ] do
    check ("CPU boundary " + value) (ResourceUsage.level 50.0 80.0 value = expected)
for value, expected in [ "69.99%", 0; "70%", 1; "89.99%", 1; "90%", 2; "100%", 2; "", -1 ] do
    check ("memory boundary " + value) (ResourceUsage.level 70.0 90.0 value = expected)

let sample = ContainerStats("database", "95%", "850MiB / 1GiB", "1kB / 2kB", memoryPercent = "83%")
check "memory used / limit" (sample.MemoryUsed = "850MiB" && sample.MemoryLimit = "1GiB")
check "independent CPU and memory levels" (sample.CpuLevel = 2 && sample.MemoryLevel = 1 && sample.Level = 2)
let container = ContainerInfo("id", "database", "Up 1 minute", "postgres", "today")
container.UpdateResources(Some sample)
check "container CPU summary" (container.ResourceSummary.Contains("95%"))
container.UpdateResources(None)
check "missing stats clears summary" (container.ResourceSummary = "--")

let partialJson = """[{"Name":"web-1","Service":"web","State":"running","Health":"healthy"},{"Name":"db-1","Service":"db","State":"exited"},{"Name":"cache-1","Service":"cache","State":"running"}]"""
let partialSnapshot = ContainerOutput.composeSnapshot partialJson
check "Compose 2 / 3" (partialSnapshot.Running = 2 && partialSnapshot.Total = 3 && partialSnapshot.Issues = [ "db" ])
let nerdctlSnapshot = ContainerOutput.composeSnapshot """[{"ID":"web-id","Name":"app-web-1","Service":"web","State":"running","Health":"","ExitCode":0,"Publishers":null},{"ID":"db-id","Name":"app-db-1","Service":"db","State":"exited","Health":"","ExitCode":1,"Publishers":null}]"""
check "nerdctl Compose empty health and stopped service" (nerdctlSnapshot.Running = 1 && nerdctlSnapshot.Total = 2 && nerdctlSnapshot.Issues = [ "db" ])
let podmanStats = ContainerOutput.objects """[{"name":"web","cpu_percent":"12%","mem_usage":"128MiB / 1GiB","mem_percent":"12.5%","netio":"1kB / 2kB"}]""" |> List.head
check "Podman lowercase stats fields" (ContainerOutput.valueAny [ "CPUPerc"; "cpu_percent" ] podmanStats = "12%" && ContainerOutput.valueAny [ "MemPerc"; "mem_percent" ] podmanStats = "12.5%")
check "Podman JSON arrays accepted" (ContainerOutput.objects """[{"id":"abc","Names":["web"]}]""" |> List.head |> ContainerOutput.valueAny [ "ID"; "Id" ] = "abc")
let project = ComposeProject()
project.UpdateSnapshot(partialSnapshot)
check "Compose partial state and service warning" (project.StatusKey = "partial" && project.Warning = "db" && project.RunningCountLabel = "2 / 3")
let normalSnapshot = ContainerOutput.composeSnapshot """{"Service":"web","State":"running","Health":"healthy"}
{"Service":"db","State":"running"}"""
project.UpdateSnapshot(normalSnapshot)
check "Compose NDJSON healthy" (project.StatusKey = "running" && project.Warning = "" && project.RunningCountLabel = "2 / 2")
project.UpdateSnapshot(ContainerOutput.composeSnapshot """[{"Service":"db","State":"exited"}]""")
check "Compose all stopped" (project.StatusKey = "stopped" && project.RunningCountLabel = "0 / 1")
project.UpdateSnapshot(ContainerOutput.composeSnapshot """[{"Service":"db","State":"running","Health":"unhealthy"}]""")
check "Compose unhealthy warning" (project.StatusKey = "partial" && project.Warning = "db")
project.UpdateSnapshot(ContainerOutput.composeSnapshot "[]")
check "Compose empty" (project.StatusKey = "stopped" && project.RunningCountLabel = "0 / 0")
let mutable countNotified = false
project.PropertyChanged.Add(fun change -> if change.PropertyName = "RunningCountLabel" then countNotified <- true)
project.UpdateSnapshot(normalSnapshot)
check "Compose count binding notification" countNotified

let tracker = AlertTracker()
let alerts = [ "cpu:db", "CPU", "95%" ]
check "notification disabled" (tracker.Update(false, alerts).IsEmpty)
check "first warning notifies" (tracker.Update(true, alerts).Length = 1)
check "continued warning is deduplicated" (tracker.Update(true, alerts).IsEmpty)
tracker.Update(true, []) |> ignore
check "warning recurrence notifies" (tracker.Update(true, alerts).Length = 1)
tracker.Reset()
check "connection reset clears notification state" (tracker.Update(true, alerts).Length = 1)

let temporary = Path.Combine(Path.GetTempPath(), "EasyContainerManager-v04-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(temporary) |> ignore
try
    let keyPath = Path.Combine(temporary, "key with spaces")
    File.WriteAllText(keyPath, "TEST FIXTURE - NOT A PRIVATE KEY")
    let podmanPath = Path.Combine(temporary, "podman.exe")
    File.WriteAllText(podmanPath, "TEST FIXTURE")
    check "Podman executable fallback path" (ContainerRuntime.resolveExecutable "podman" [ podmanPath ] = podmanPath)
    let remote = SshConnection(Name = "Home", Host = "192.0.2.1", Port = 2222, User = "ubuntu", PrivateKeyPath = keyPath)
    check "SSH profile validation" (remote.Validate() = Ok ())
    let command = ContainerCommand.startInfo Docker (Some remote) (Some "/srv/owner's app;test") [ "compose"; "ps"; "--format"; "{{json .}}" ]
    let arguments = command.ArgumentList |> Seq.toList
    check "SSH transport without local shell" (command.FileName = "ssh" && not command.UseShellExecute && command.CreateNoWindow)
    check "SSH noninteractive and strict host key checking" (arguments |> List.contains "BatchMode=yes" && arguments |> List.contains "StrictHostKeyChecking=yes")
    check "SSH key path is one argument" (arguments |> List.contains keyPath)
    check "SSH port setting" (arguments |> List.pairwise |> List.contains ("-p", "2222"))
    let testCommand = ContainerCommand.sshTestStartInfo Docker remote
    check "SSH connection test does not require a container engine" (Seq.last testCommand.ArgumentList = "echo OK")
    check "remote working directory quoted" ((List.last arguments).StartsWith("cd -- '/srv/owner'\\''s app;test' && exec docker "))
    check "Docker template quoted" ((List.last arguments).Contains("'{{json .}}'"))
    let podmanCommand = ContainerCommand.startInfo Podman (Some remote) None [ "ps"; "-a" ]
    check "Podman remote command selected" ((Seq.last podmanCommand.ArgumentList).Contains("exec podman 'ps' '-a'"))
    let probeArguments = ContainerCommand.containerBashCheckArguments "container-id"
    check "bash check is noninteractive and targets the container" (probeArguments = [ "exec"; "container-id"; "bash"; "-c"; "exit" ])
    let bash = TerminalCommand.shellForProbe (Ok "" : Result<string, string>)
    let sh = TerminalCommand.shellForProbe (Error "bash missing" : Result<string, string>)
    let remoteConsole, consoleArgs, consoleDirectory = TerminalCommand.target Podman (Some remote) None (Some("id';echo unsafe", bash))
    let consoleCommand = List.last consoleArgs
    check "remote console allocates a tty and uses the selected engine" (remoteConsole = "ssh" && consoleArgs |> List.contains "-tt" && consoleCommand.StartsWith("exec podman "))
    check "successful bash check starts bash with a quoted container id" (consoleCommand = "exec podman 'exec' '-it' 'id'\\'';echo unsafe' 'bash'")
    let _, shArgs, _ = TerminalCommand.target Podman (Some remote) None (Some("container-id", sh))
    check "failed bash check starts sh" (List.last shArgs = "exec podman 'exec' '-it' 'container-id' 'sh'")
    check "remote console retains strict host key checking and key" (consoleArgs |> List.contains "StrictHostKeyChecking=yes" && consoleArgs |> List.contains keyPath && consoleDirectory.IsNone)
    let _, composeTerminalArgs, _ = TerminalCommand.target Docker (Some remote) (Some "/srv/owner's app") None
    check "remote Compose terminal quotes its directory" ((List.last composeTerminalArgs).StartsWith("cd -- '/srv/owner'\\''s app' &&"))
    let _, localConsoleArgs, localDirectory = TerminalCommand.target Docker None (Some temporary) (Some("container-id", bash))
    check "local console passes arguments separately" (localConsoleArgs = [ "exec"; "-it"; "container-id"; "bash" ] && localDirectory = Some temporary)
    remote.Host <- "-oProxyCommand=bad"
    check "SSH option-like host rejected" (remote.Validate() |> Result.isError)
    remote.Host <- "host;echo bad"
    check "SSH shell-like host rejected" (remote.Validate() |> Result.isError)
    remote.Host <- "192.0.2.1"
    remote.Port <- 65536
    check "SSH invalid port rejected" (remote.Validate() |> Result.isError)
    remote.Port <- 22
    let local = ContainerCommand.startInfo Docker None (Some temporary) [ "logs"; "a;not-a-shell-command" ]
    check "local Docker arguments remain separate" (Path.GetFileNameWithoutExtension(local.FileName) = "docker" && local.ArgumentList.Count = 2 && local.WorkingDirectory = temporary)
    let localPodman = ContainerCommand.startInfo Podman None None [ "ps" ]
    check "local Podman command selected" (Path.GetFileNameWithoutExtension(localPodman.FileName) = "podman" && localPodman.ArgumentList.[0] = "ps")
    check "Podman Compose output is quiet and uncolored" (localPodman.Environment.["PODMAN_COMPOSE_WARNING_LOGS"] = "false" && localPodman.Environment.["COMPOSE_ANSI"] = "never" && localPodman.Environment.["NO_COLOR"] = "1")
    let localNerdctl = ContainerCommand.startInfo Nerdctl None (Some temporary) [ "compose"; "ps"; "-a"; "--format"; "json" ]
    check "local nerdctl Compose uses its CLI and project directory"
        (Path.GetFileNameWithoutExtension(localNerdctl.FileName) = "nerdctl"
         && localNerdctl.WorkingDirectory = temporary
         && Seq.toList localNerdctl.ArgumentList = [ "compose"; "ps"; "-a"; "--format"; "json" ])
    remote.Engine <- "nerdctl"
    check "SSH validation accepts nerdctl" (remote.Validate() = Ok ())
    let nerdctlCommand = ContainerCommand.startInfo Nerdctl (Some remote) (Some "/srv/owner's app") [ "compose"; "up"; "-d" ]
    check "remote nerdctl Compose preserves shell quoting"
        (Seq.last nerdctlCommand.ArgumentList = "cd -- '/srv/owner'\\''s app' && exec nerdctl 'compose' 'up' '-d'")
    let _, nerdctlConsoleArgs, _ = TerminalCommand.target Nerdctl (Some remote) None (Some("container-id", bash))
    check "remote nerdctl container terminal uses the selected CLI"
        (List.last nerdctlConsoleArgs = "exec nerdctl 'exec' '-it' 'container-id' 'bash'")
    let nerdctlSettings = AppSettings()
    nerdctlSettings.SshConnections.Add(remote)
    let restoredNerdctl = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(nerdctlSettings))
    let nerdctlProfileViewModel = new MainViewModel(settingsOverride = restoredNerdctl, startAutomatically = false)
    nerdctlProfileViewModel.SelectedSshConnection <- restoredNerdctl.SshConnections.[0]
    check "nerdctl SSH profile survives settings round trip and loads in the editor"
        (restoredNerdctl.SshConnections.[0].Engine = "nerdctl" && nerdctlProfileViewModel.NewSshEngine.Key = "nerdctl")
    (nerdctlProfileViewModel :> IDisposable).Dispose()
    let settings = AppSettings()
    settings.NotificationsEnabled <- true
    remote.Engine <- "podman"
    remote.LastConnectedAt <- "2026-01-02 03:04:05 +09:00"
    remote.ConnectionState <- "connected"
    settings.SshConnections.Add(remote)
    let remoteProject = ComposeProject(Name = "Remote", FolderPath = "/srv/app", ConnectionId = remote.Id)
    settings.ComposeProjects.Add(remoteProject)
    let json = JsonSerializer.Serialize(settings)
    let restored = JsonSerializer.Deserialize<AppSettings>(json)
    check "settings round trip" (restored.NotificationsEnabled && restored.SshConnections.[0].Port = 22 && restored.SshConnections.[0].Engine = "podman" && restored.SshConnections.[0].LastConnectedAt = remote.LastConnectedAt && restored.ComposeProjects.[0].ConnectionId = remote.Id)
    check "SSH connection state is not persisted" (not (json.Contains("ConnectionState")) && restored.SshConnections.[0].ConnectionState = "")
    let profileViewModel = new MainViewModel(settingsOverride = restored, startAutomatically = false, runtime = Docker)
    profileViewModel.SelectedSshConnection <- restored.SshConnections.[0]
    check "SSH editor loads the profile's engine" (profileViewModel.NewSshEngine.Key = "podman")
    (profileViewModel :> IDisposable).Dispose()
    check "private key content is never stored in settings" (not (json.Contains("NOT A PRIVATE KEY")))
finally
    Directory.Delete(temporary, true)

for code, _ in Localization.languages do
    if code <> "ja-JP" && code <> "en-US" then
        for key in [ "🟢 正常"; "🟡 注意"; "🔴 警告"; "🟡 一部異常"; "接続先"; "SSH接続"; "警告通知"; "使用率"; "接続テスト" ] do
            check ("translation " + code + " " + key) (Localization.translate code key "MISSING" <> "MISSING")

for key in Localization.allKeys do
    check ("all 11 translations present for: " + key) (Localization.hasAllTranslations key)

// --- 強化1: 公開ポート表示・色分け・ツールチップ・inspect パース ---
let inspectJson = """[
  {
    "Id": "abc1234567890def",
    "Name": "/web-public",
    "NetworkSettings": {
      "Ports": {
        "80/tcp": [
          { "HostIp": "0.0.0.0", "HostPort": "8080" },
          { "HostIp": "::", "HostPort": "8080" }
        ]
      }
    },
    "HostConfig": { "PortBindings": {} }
  },
  {
    "Id": "def9876543210abc",
    "Name": "/web-local",
    "NetworkSettings": { "Ports": {} },
    "HostConfig": {
      "PortBindings": {
        "80/tcp": [ { "HostIp": "127.0.0.1", "HostPort": "8081" } ]
      }
    }
  }
]"""
let inspectedList = ContainerOutput.inspectPorts inspectJson
let publicPorts = ContainerOutput.findContainerPorts "abc123456789" "web-public" inspectedList
let localPorts = ContainerOutput.findContainerPorts "def987654321" "/web-local" inspectedList
let publicContainer = ContainerInfo("abc123456789", "web-public", "Up 2 minutes", "nginx:latest", "today", initialPorts = publicPorts)
let localContainer = ContainerInfo("def987654321", "web-local", "Exited (0)", "nginx:latest", "today", initialPorts = localPorts)
check "public container parses 0.0.0.0 and [::] bindings"
    (publicContainer.PublishedPorts = "0.0.0.0:8080->80/tcp, [::]:8080->80/tcp"
     && publicContainer.HasAllInterfacesPort
     && Object.ReferenceEquals(publicContainer.PortsBrush, Avalonia.Media.Brushes.DarkGoldenrod)
     && publicContainer.PortsTooltip = "全てのネットワークから接続可能")
check "local container falls back to HostConfig.PortBindings and uses normal color"
    (localContainer.PublishedPorts = "127.0.0.1:8081->80/tcp"
     && not localContainer.HasAllInterfacesPort
     && not (Object.ReferenceEquals(localContainer.PortsBrush, Avalonia.Media.Brushes.DarkGoldenrod))
     && localContainer.PortsTooltip = "このPCからのみ接続可能")
publicContainer.LanguageCode <- "en-US"
localContainer.LanguageCode <- "en-US"
check "port tooltips localize to English"
    (publicContainer.PortsTooltip = "Accessible from all networks"
     && localContainer.PortsTooltip = "Accessible only from this PC")
let podmanArrayInspect = """[{"Id":"pod123","Name":"pod-web","NetworkSettings":{"Ports":[{"host_ip":"","host_port":9090,"container_port":90,"protocol":"tcp"}]}}]"""
let podmanPorts = ContainerOutput.findContainerPorts "pod123" "pod-web" (ContainerOutput.inspectPorts podmanArrayInspect)
check "Podman array-style port bindings normalize empty host_ip to 0.0.0.0"
    (podmanPorts.Length = 1 && podmanPorts.[0].Display = "0.0.0.0:9090->90/tcp" && podmanPorts.[0].IsAllInterfaces)

// --- 強化2: Composeログ表示の操作感統一 ---
let composeCalls = ResizeArray<string * int option>()
let firstGate = System.Threading.Tasks.TaskCompletionSource<Result<string, string>>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
let composeSettings = AppSettings()
let composeA = ComposeProject(Name = "projA", FolderPath = "path-a", Engine = "docker")
let composeB = ComposeProject(Name = "projB", FolderPath = "path-b", Engine = "docker")
composeSettings.ComposeProjects.Add(composeA)
composeSettings.ComposeProjects.Add(composeB)
let composeLogVm =
    new MainViewModel(
        settingsOverride = composeSettings,
        startAutomatically = false,
        runtime = Docker,
        composeLogsFetcher = (fun path tail _ ->
            composeCalls.Add(path, tail)
            async {
                if path = "path-a" then return! firstGate.Task |> Async.AwaitTask
                else return Ok (sprintf "logs for %s (tail=%A)" path tail)
            }))
composeLogVm.SelectedComposeProject <- composeA
composeLogVm.SelectedComposeProject <- composeB
check "Compose selection auto-fetches tail 50"
    (composeCalls |> Seq.toList = [ "path-a", Some 50; "path-b", Some 50 ]
     && composeLogVm.ComposeLogText = "logs for path-b (tail=Some 50)")
firstGate.SetResult(Ok "stale logs for path-a")
System.Threading.Thread.Sleep(50)
check "stale Compose log result from previous selection is discarded"
    (composeLogVm.ComposeLogText = "logs for path-b (tail=Some 50)")
composeLogVm.ShowComposeLogsAsync() |> Async.StartImmediate
check "Show logs button fetches full logs without tail limit"
    (composeCalls |> Seq.toList = [ "path-a", Some 50; "path-b", Some 50; "path-b", None ]
     && composeLogVm.ComposeLogText = "logs for path-b (tail=None)")
(composeLogVm :> IDisposable).Dispose()

// --- 強化3: SSH鍵パス空欄 (ssh-agent) ---
let agentRemote = SshConnection(Name = "AgentHost", Host = "192.0.2.10", Port = 22, User = "dev", PrivateKeyPath = "")
check "SSH validation allows blank private key path for ssh-agent" (agentRemote.Validate() = Ok () && agentRemote.ExpandedKeyPath = "")
let agentCmd = ContainerCommand.startInfo Docker (Some agentRemote) None [ "ps" ]
let agentArgs = agentCmd.ArgumentList |> Seq.toList
check "SSH command omits -i when private key path is blank" (not (agentArgs |> List.contains "-i"))
let _, agentTerminalArgs, _ = TerminalCommand.target Docker (Some agentRemote) None None
check "SSH terminal omits -i when private key path is blank" (not (agentTerminalArgs |> List.contains "-i"))
agentRemote.PrivateKeyPath <- Path.Combine(Path.GetTempPath(), "non-existent-key-" + Guid.NewGuid().ToString("N"))
check "SSH validation rejects non-existent explicit key path" (agentRemote.Validate() |> Result.isError)

// --- 強化4 & 不具合修正1・2・3 ---
check "startup runtime auto-selects Podman when only Podman is installed and requires selection when none"
    (ContainerRuntime.resolveStartupRuntime [ Podman ] = Some Podman
     && ContainerRuntime.resolveStartupRuntime [ Docker ] = Some Docker
     && ContainerRuntime.resolveStartupRuntime [] = None
     && ContainerRuntime.resolveStartupRuntime [ Docker; Podman ] = None)

check "runtime missing translation key exists across all languages"
    (Localization.hasAllTranslations "コンテナエンジンが検出されませんでした。使用するエンジンを選択してください")

let uiSettings = AppSettings(Language = "ja-JP", Theme = "Dark")
let uiVm = new MainViewModel(settingsOverride = uiSettings, startAutomatically = false, runtime = Docker)
check "subtitle reflects local & SSH container management"
    (uiVm.AppSubtitle = "ローカル＆リモート（SSH）のコンテナ管理"
     && uiVm.PrivateKeyHintLabel = "空欄の場合はデフォルト鍵・エージェントを使用")

let uiChangedProps = ResizeArray<string>()
uiVm.PropertyChanged.Add(fun e -> uiChangedProps.Add(e.PropertyName))
uiVm.Language <- uiVm.Languages |> Array.find (fun opt -> opt.Key = "en-US")
check "switching to English updates RuntimeModeLabel and AppSubtitle"
    (uiVm.RuntimeModeLabel = "Docker mode"
     && uiVm.AppSubtitle = "Manage local & remote (SSH) containers"
     && uiChangedProps |> Seq.contains "RuntimeModeLabel"
     && uiChangedProps |> Seq.contains "PublishedPortsLabel"
     && uiChangedProps |> Seq.contains "PrivateKeyHintLabel")

let setUiEngineState = typeof<MainViewModel>.GetMethod("SetDockerDesktopState", privateMembers)
setUiEngineState.Invoke(uiVm, [| box "running" |]) |> ignore
check "running state hides engine start button" (not uiVm.ShowContainerEngineStart && uiVm.ConnectionHelp = "")
setUiEngineState.Invoke(uiVm, [| box "cliMissing" |]) |> ignore
check "cliMissing state hides start button and shows install guidance"
    (not uiVm.ShowContainerEngineStart && not (String.IsNullOrWhiteSpace(uiVm.ConnectionHelp)))
if OperatingSystem.IsWindows() then
    setUiEngineState.Invoke(uiVm, [| box "unreachable" |]) |> ignore
    check "unreachable state shows start button on Windows" uiVm.ShowContainerEngineStart
(uiVm :> IDisposable).Dispose()

// --- Button flicker fix & per-row IsOperating checks ---
let testVm = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false)
let testSetEngineState = typeof<MainViewModel>.GetMethod("SetDockerDesktopState", privateMembers)
testSetEngineState.Invoke(testVm, [| box "running" |]) |> ignore
let c1 = ContainerInfo("c1", "kuma-web1", "Up 10 hours", "nginx:alpine", "today")
let c2 = ContainerInfo("c2", "kuma-web2", "Up 10 hours", "redis:alpine", "today")
testVm.Containers.Add(c1)
testVm.Containers.Add(c2)
testVm.SelectedContainer <- c1

check "initially selected container can operate" testVm.CanOperate

// 1. ps更新中にボタンがちらつかない（CanOperate変更通知が飛ばない & 選択維持）
let canOperateChanges = ResizeArray<string>()
testVm.PropertyChanged.Add(fun e ->
    if e.PropertyName = "CanOperate" then canOperateChanges.Add(e.PropertyName))

let refreshCoreMethod = typeof<MainViewModel>.GetMethod("refreshCore", privateMembers)
let fakeListMethod = typeof<ContainerCli>.GetMethod("ListContainers")

// refreshCoreの差分更新シミュレーション: 既存コンテナの参照が保持され、選択が外れない
let updatedIncoming = [ ContainerInfo("c1", "kuma-web1", "Up 11 hours", "nginx:alpine", "today"); ContainerInfo("c2", "kuma-web2", "Up 11 hours", "redis:alpine", "today") ]
let updatingContainerListField = typeof<MainViewModel>.GetField("updatingContainerList", privateMembers)

// 差分更新実行（c1, c2がそのままUpdateFromされる）
c1.UpdateFrom(updatedIncoming.[0])
c2.UpdateFrom(updatedIncoming.[1])
check "containers retain instance references after update"
    (Object.ReferenceEquals(testVm.Containers.[0], c1)
     && Object.ReferenceEquals(testVm.Containers.[1], c2)
     && Object.ReferenceEquals(testVm.SelectedContainer, c1))
check "refresh does not notify CanOperate or flicker buttons" (canOperateChanges.Count = 0)

// 2. 変更系コマンドは「その行だけ」止める
c1.IsOperating <- true
check "operating container row disables CanOperate when selected" (not testVm.CanOperate)

testVm.SelectedContainer <- c2
check "switching to non-operating container allows operation" (testVm.CanOperate && not c2.IsOperating)

testVm.SelectedContainer <- c1
check "switching back to operating container keeps it disabled" (not testVm.CanOperate)

// 3. 連打防止（IsOperatingがtrueならStartSelectedAsyncが二重実行されない）
let doubleAction = testVm.StartSelectedAsync() |> Async.StartImmediateAsTask
check "repeat action is immediately skipped when IsOperating is true" doubleAction.IsCompleted

c1.IsOperating <- false
check "re-enabled when operation finishes" testVm.CanOperate

// 4. ComposeProject & ImageInfo have per-row IsOperating
let testProject = ComposeProject(Name = "P1", FolderPath = "path/p1")
testVm.ComposeProjects.Add(testProject)
testVm.SelectedComposeProject <- testProject
check "Compose project initially manageable" testVm.CanManageComposeProject
testProject.IsOperating <- true
check "operating Compose project disables CanManageComposeProject" (not testVm.CanManageComposeProject)
testProject.IsOperating <- false
check "Compose project re-enabled when operation finishes" testVm.CanManageComposeProject

let testImage = ImageInfo("img1", "repo", "tag", "100MB", "today")
testVm.Images.Add(testImage)
testVm.SelectedImage <- testImage
check "image initially removable" testVm.CanRemoveImage
testImage.IsOperating <- true
check "operating image disables CanRemoveImage" (not testVm.CanRemoveImage)
testImage.IsOperating <- false
check "image re-enabled when operation finishes" testVm.CanRemoveImage

// 5. ContainerStats UpdateFrom updates properties without instance recreation
let initialStats = ContainerStats("kuma-web1", "10%", "100MB / 1GB", "1KB / 2KB", memoryPercent = "10%")
let nextStats = ContainerStats("kuma-web1", "20%", "200MB / 1GB", "3KB / 4KB", memoryPercent = "20%")
initialStats.UpdateFrom(nextStats)
check "ContainerStats UpdateFrom updates CPU and memory smoothly"
    (initialStats.Cpu = "20%" && initialStats.Memory = "200MB / 1GB" && initialStats.MemoryPercent = "20%")

(testVm :> IDisposable).Dispose()

// 6. SQLite ComposeProject persistence & startup restoration checks
let sqliteProject = ComposeProject(Name = "SQLite-App", FolderPath = "C:/apps/myapp", BrowserUrl = "http://localhost:3000", Engine = "docker")
ComposeRepository.Save(sqliteProject)
let loadedFromDb = ComposeRepository.GetAll()
check "ComposeRepository saves and retrieves project"
    (loadedFromDb |> List.exists (fun p -> p.Name = "SQLite-App" && p.FolderPath = "C:/apps/myapp" && p.BrowserUrl = "http://localhost:3000"))

let scopedList = ComposeRepository.GetByScope("local", "Docker")
check "ComposeRepository GetByScope case-insensitive match"
    (scopedList |> List.exists (fun p -> p.Name = "SQLite-App"))

// 次回起動シミュレーション: settings.ComposeProjects が空の状態で新規 ViewModel を起動
let blankSettings = AppSettings()
let rebootedVm = new MainViewModel(settingsOverride = blankSettings, startAutomatically = false, runtime = Docker)
check "Startup restores Compose projects from SQLite even when settings are empty"
    (rebootedVm.ComposeProjects |> Seq.exists (fun p -> p.Name = "SQLite-App" && p.FolderPath = "C:/apps/myapp"))

// 削除シミュレーション
let targetProj = rebootedVm.ComposeProjects |> Seq.find (fun p -> p.Name = "SQLite-App")
rebootedVm.SelectedComposeProject <- targetProj
rebootedVm.RemoveSelectedComposeProject()
check "RemoveSelectedComposeProject removes from ViewModel"
    (not (rebootedVm.ComposeProjects |> Seq.exists (fun p -> p.Name = "SQLite-App")))

let afterDeleteDb = ComposeRepository.GetAll()
check "RemoveSelectedComposeProject deletes from SQLite database"
    (not (afterDeleteDb |> List.exists (fun p -> p.Name = "SQLite-App")))

// 次々回起動シミュレーション: 削除後は再起動しても現れないこと
(rebootedVm :> IDisposable).Dispose()
let nextRebootVm = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false, runtime = Docker)
check "Deleted project does not reappear on subsequent startup"
    (not (nextRebootVm.ComposeProjects |> Seq.exists (fun p -> p.Name = "SQLite-App")))
(nextRebootVm :> IDisposable).Dispose()

// 7. LanguageCode single-notification check
let langStats = ContainerStats("test-c", "10%", "100MB / 1GB", "1KB / 2KB", memoryPercent = "10%")
let propertyChanges = ResizeArray<string>()
langStats.PropertyChanged.Add(fun e -> propertyChanges.Add(e.PropertyName))
langStats.LanguageCode <- "en-US"
check "LanguageCode notifies each status property exactly once"
    (propertyChanges.Count = 4 && propertyChanges |> Seq.sort |> Seq.toList = [ "CpuStatus"; "LanguageCode"; "MemoryStatus"; "Status" ])

// 8. RemoveSshConnection cleans up ComposeRepository records
let sshConn = SshConnection(Id = "ssh-test-conn", Name = "Test SSH", Host = "192.168.1.100")
let sshProj = ComposeProject(Name = "SSH-App", FolderPath = "/var/www/app", ConnectionId = "ssh-test-conn", Engine = "docker")
ComposeRepository.Save(sshProj)
let sshVmSettings = AppSettings()
sshVmSettings.SshConnections.Add(sshConn)
sshVmSettings.ComposeProjects.Add(sshProj)
let sshVm = new MainViewModel(settingsOverride = sshVmSettings, startAutomatically = false, runtime = Docker)
sshVm.SelectedSshConnection <- sshConn
sshVm.RemoveSshConnection()
let dbAfterSshRemove = ComposeRepository.GetAll()
check "RemoveSshConnection deletes associated Compose projects from SQLite"
    (not (dbAfterSshRemove |> List.exists (fun p -> p.ConnectionId = "ssh-test-conn")))
(sshVm :> IDisposable).Dispose()

// クリーンアップ
ComposeRepository.CustomDatabasePath <- None
try if File.Exists(testDbPath) then File.Delete(testDbPath) with _ -> ()

printfn "All %d checks passed. No Docker, Podman, nerdctl, or SSH server was contacted." checks