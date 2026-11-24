#r "nuget: Avalonia, 12.1.5"
#r "nuget: CommunityToolkit.Mvvm, 8.4.2"
#r "nuget: Microsoft.Data.Sqlite, 11.0.0"
#load "../EasyContainerManager/Localization.fs"
#load "../EasyContainerManager/Models/ContainerStats.fs"
#load "../EasyContainerManager/Models/ContainerInfo.fs"
#load "../EasyContainerManager/Models/ServiceInference.fs"
#load "../EasyContainerManager/Models/ServiceDiagnostics.fs"
#load "../EasyContainerManager/Models/VolumeInfo.fs"
#load "../EasyContainerManager/Models/FaqData.fs"
#load "../EasyContainerManager/Models/ImageInfo.fs"
#load "../EasyContainerManager/Models/SshConnection.fs"
#load "../EasyContainerManager/Models/ComposeProject.fs"
#load "../EasyContainerManager/Models/ComposeRepository.fs"
#load "../EasyContainerManager/Models/OperationLog.fs"
#load "../EasyContainerManager/Models/AlertMonitor.fs"
#load "../EasyContainerManager/Models/MasterDataRepository.fs"
#load "../EasyContainerManager/Models/VersionDiagnostics.fs"
#load "../EasyContainerManager/AppSettings.fs"
#load "../EasyContainerManager/ContainerCli.fs"
#load "../EasyContainerManager/Services/QrCodeGenerator.fs"
#load "../EasyContainerManager/Services/MobileSessionServer.fs"
#load "../EasyContainerManager/Models/ChoiceOption.fs"
#load "../EasyContainerManager/ViewModels/ViewModelBase.fs"
#load "../EasyContainerManager/ViewModels/MainViewModel.fs"

open System
open System.IO
open Microsoft.Data.Sqlite
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.ViewModels

let testDbPath = Path.Combine(Path.GetTempPath(), sprintf "easycontainer_v12vols_%s.db" (Guid.NewGuid().ToString("N")))
ComposeRepository.CustomDatabasePath <- Some testDbPath

let mutable checks = 0
let check name condition =
    if not condition then failwith (sprintf "FAILED: %s" name)
    checks <- checks + 1
    printfn "PASS: %s" name

try
    // 1. ボリューム関連 多言語辞書キー（全11言語）
    let requiredVolumeKeys = [
        "ボリューム"
        "ボリューム名"
        "ドライバ"
        "マウント先"
        "使用容量"
        "紐付けコンテナ"
        "種別"
        "名前付き"
        "匿名"
        "未使用"
        "使用中"
        "読み取り専用（削除・prune は実行しません）"
        "未使用のみ表示"
        "ボリューム名・コンテナ名で絞り込み..."
        "{0}件のボリューム / 合計 {1}"
        "未使用: {0}件 ({1})"
        "未使用で容量を消費しています"
        "マウント中ボリューム"
        "ボリューム容量の肥大化"
        "マウント中のボリュームが大きなディスク容量を消費しています。"
        "ボリュームはありません。"
        "ボリュームを選択すると詳細と紐付けコンテナを表示します。"
        "不明"
        "ボリューム詳細"
    ]
    for key in requiredVolumeKeys do
        check (sprintf "Localization has all 11 translations for %s" key) (Localization.hasAllTranslations key)

    // 2. サイズ解析・整形
    check "parseByteSize handles B/kB/MB/GB/MiB/GiB/bare/invalid" (
        VolumeParser.parseByteSize "0B" = Some 0L
        && VolumeParser.parseByteSize "512kB" = Some 512_000L
        && VolumeParser.parseByteSize "2.5MB" = Some 2_500_000L
        && VolumeParser.parseByteSize "1.5GB" = Some 1_500_000_000L
        && VolumeParser.parseByteSize "5GiB" = Some (5L * 1024L * 1024L * 1024L)
        && VolumeParser.parseByteSize "1024" = Some 1024L
        && VolumeParser.parseByteSize "N/A" = None
        && VolumeParser.parseByteSize "--" = None
        && VolumeParser.parseByteSize "" = None)

    check "formatByteSize formats B/KB/GB cleanly" (
        VolumeParser.formatByteSize 512L = "512 B"
        && VolumeParser.formatByteSize 2048L = "2 KB"
        && VolumeParser.formatByteSize (5L * 1024L * 1024L * 1024L) = "5 GB")

    // 3. 匿名 vs 名前付きボリューム判定
    let hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    check "isAnonymousName detects 64-hex and Podman explicit flag" (
        VolumeParser.isAnonymousName hash64 None
        && not (VolumeParser.isAnonymousName "pgdata" None)
        && VolumeParser.isAnonymousName "anon-short" (Some true))
    check "shortDisplayName abbreviates 64-hex names" (
        VolumeParser.shortDisplayName hash64 true = "0123456789ab…"
        && VolumeParser.shortDisplayName "pgdata" false = "pgdata")

    // 4. Docker NDJSON / Podman JSON Array volume ls & inspect 解析
    let dockerLs = """{"Name":"pgdata","Driver":"local","Mountpoint":"/var/lib/docker/volumes/pgdata/_data"}
{"Name":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","Driver":"local","Mountpoint":""}"""
    let parsedLs = VolumeParser.parseVolumeLsJson dockerLs
    check "parseVolumeLsJson parses Docker NDJSON" (parsedLs.Length = 2 && parsedLs.[0].Name = "pgdata")

    let podmanLs = """[{"Name":"podvol","Driver":"local","Mountpoint":"/home/user/.local/share/containers/storage/volumes/podvol/_data","CreatedAt":"2026-11-24T10:00:00Z","Anonymous":true}]"""
    let parsedPodman = VolumeParser.parseVolumeLsJson podmanLs
    check "parseVolumeLsJson parses Podman array with Anonymous flag" (
        parsedPodman.Length = 1
        && parsedPodman.[0].ExplicitAnonymous = Some true
        && parsedPodman.[0].CreatedAt = "2026-11-24T10:00:00Z")

    let inspectJson = """[
      {"Name":"pgdata","Driver":"local","Mountpoint":"/var/lib/docker/volumes/pgdata/_data","CreatedAt":"2026-11-20T01:02:03Z"},
      {"Name":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","Driver":"local","Mountpoint":"/var/lib/docker/volumes/hash/_data","CreatedAt":"2026-11-21T02:03:04Z"}
    ]"""
    let inspectMap = VolumeParser.parseVolumeInspectJson inspectJson
    check "parseVolumeInspectJson populates createdAt and mountpoint" (
        inspectMap.Count = 2 && inspectMap.["pgdata"].CreatedAt = "2026-11-20T01:02:03Z")

    // 5. system df -v JSON & テキスト出力解析（Windows Docker Desktop 対応）
    let dfJson = """{"Volumes":[{"Name":"pgdata","Links":"1","Size":"6.5GiB"},{"Name":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","Links":"0","Size":"250MB"}]}"""
    let dfMapJson = VolumeParser.parseSystemDfJson dfJson
    check "parseSystemDfJson parses Volumes array" (
        dfMapJson.Count = 2
        && dfMapJson.["pgdata"].Links = Some 1
        && dfMapJson.["pgdata"].SizeBytes.IsSome)

    let dfText = """Images space usage:

REPOSITORY   TAG       IMAGE ID       CREATED       SIZE
nginx        latest    123456789abc   2 weeks ago   187MB

Local Volumes space usage:

VOLUME NAME                                                        LINKS     SIZE
pgdata                                                             1         6.5GiB
0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef   0         250MB

Build cache usage: 0B
"""
    let dfMapText = VolumeParser.parseSystemDfText dfText
    check "parseSystemDfText parses Windows Docker Desktop table" (
        dfMapText.Count = 2
        && dfMapText.["pgdata"].Links = Some 1
        && dfMapText.["pgdata"].SizeText = "6.5GiB"
        && dfMapText.[hash64].Links = Some 0
        && dfMapText.[hash64].SizeBytes = Some 250_000_000L)

    // 6. ContainerOutput.inspectContainerMeta & inspectPorts 後方互換確認
    let containerInspectJson = """[
      {
        "Id": "cid-111",
        "Name": "/db-main",
        "RestartCount": 2,
        "State": { "Status": "running", "Running": true, "Restarting": false, "OOMKilled": false, "ExitCode": 0 },
        "NetworkSettings": { "Ports": { "5432/tcp": [ { "HostIp": "127.0.0.1", "HostPort": "5432" } ] } },
        "Mounts": [
          { "Type": "volume", "Name": "pgdata", "Source": "/var/lib/docker/volumes/pgdata/_data", "Destination": "/var/lib/postgresql/data", "RW": true },
          { "Type": "bind", "Source": "/host/conf", "Destination": "/etc/pg", "RW": false }
        ]
      }
    ]"""
    let legacyPorts = ContainerOutput.inspectPorts containerInspectJson
    let metaList = ContainerOutput.inspectContainerMeta containerInspectJson
    check "inspectPorts remains compatible and inspectContainerMeta extracts mounts & state" (
        legacyPorts.Length = 1
        && metaList.Length = 1
        && metaList.[0].RestartCount = 2
        && metaList.[0].Mounts.Length = 2
        && metaList.[0].Mounts.[0].VolumeName = "pgdata"
        && metaList.[0].Mounts.[0].Destination = "/var/lib/postgresql/data")

    // 7. コンテナ紐付け・未使用判定・ソート・診断統合
    let cDb =
        ContainerInfo(
            "cid-111",
            "db-main",
            "Up 2 hours",
            "postgres:17",
            "today",
            initialPorts = metaList.[0].Ports,
            initialMounts = metaList.[0].Mounts,
            initialRestartCount = metaList.[0].RestartCount
        )
    let linked = VolumeParser.linkAndBuildVolumes parsedLs inspectMap dfMapText Map.empty [ cDb ]
    check "linkAndBuildVolumes attaches pgdata to db-main and marks 64-hex as unused & anonymous" (
        linked.Length = 2
        && linked.[0].Name = "pgdata"
        && not linked.[0].IsUnused
        && not linked.[0].IsAnonymous
        && linked.[0].AttachedContainers.Length = 1
        && linked.[0].AttachedContainers.[0].ContainerName = "db-main"
        && linked.[1].Name = hash64
        && linked.[1].IsUnused
        && linked.[1].IsAnonymous
        && linked.[1].AttachedContainers.IsEmpty)

    // ボリューム診断（5 GiB 警告 / 20 GiB 危険 / < 5 GiB サイレント）
    let diagWarn = VolumeParser.diagnoseContainerVolumes cDb.Mounts linked
    check "diagnoseContainerVolumes emits Warning >= 5GiB with evidence" (
        diagWarn.Length = 1
        && diagWarn.[0].Severity = FindingSeverity.Warning
        && diagWarn.[0].TitleKey = "ボリューム容量の肥大化"
        && diagWarn.[0].EvidenceLines.[0].Contains("pgdata"))

    let hugeVol = { linked.[0] with SizeBytes = Some (25L * 1024L * 1024L * 1024L); SizeText = "25 GB" }
    let diagDanger = VolumeParser.diagnoseContainerVolumes cDb.Mounts [ hugeVol ]
    check "diagnoseContainerVolumes emits Danger >= 20GiB" (
        diagDanger.Length = 1 && diagDanger.[0].Severity = FindingSeverity.Danger)

    let smallVol = { linked.[0] with SizeBytes = Some (100L * 1024L * 1024L); SizeText = "100 MB" }
    check "diagnoseContainerVolumes is silent < 5GiB" (
        List.isEmpty (VolumeParser.diagnoseContainerVolumes cDb.Mounts [ smallVol ]))

    // 8. MainViewModel 結合テスト（読み込み・フィルタ・コンテナ選択時のマウント要約・多言語）
    let vm =
        new MainViewModel(
            settingsOverride = AppSettings(),
            startAutomatically = false,
            containerListFetcher = (fun () -> async { return Ok [ cDb ] }),
            imageListFetcher = (fun () -> async { return Ok [] }),
            statsFetcher = (fun () -> async { return Ok [] }),
            connectionChecker = (fun () -> async { return Ok () }),
            volumeListFetcher = (fun () -> async { return Ok linked })
        )
    vm.RefreshAsync() |> Async.RunSynchronously
    check "MainViewModel loads volumes and computes summary" (
        vm.Volumes.Count = 2
        && vm.FilteredVolumes.Count = 2
        && vm.HasVolumes
        && not vm.HasNoFilteredVolumes)

    vm.ShowUnusedVolumesOnly <- true
    check "ShowUnusedVolumesOnly filters to unused hash volume" (
        vm.FilteredVolumes.Count = 1 && vm.FilteredVolumes.[0].IsUnused)

    vm.ShowUnusedVolumesOnly <- false
    vm.VolumeSearchText <- "db-main"
    check "VolumeSearchText filters by attached container name" (
        vm.FilteredVolumes.Count = 1 && vm.FilteredVolumes.[0].Name = "pgdata")

    vm.VolumeSearchText <- ""
    vm.SelectedContainer <- vm.Containers.[0]
    check "SelectedContainer exposes mounted volumes summary with size" (
        vm.HasSelectedContainerMounts
        && vm.SelectedContainerMountsText.Contains("pgdata")
        && vm.SelectedContainerMountsText.Contains("6.5GiB"))

    let enOpt = vm.Languages |> Array.find (fun o -> o.Key = "en-US")
    vm.Language <- enOpt
    check "language switch translates Volume UI properties" (
        vm.TabVolumesLabel = "Volumes"
        && vm.VolumeReadOnlyNotice.Contains("Read-only"))

    (vm :> IDisposable).Dispose()
    printfn "\nAll %d v1.2 volume checks passed!" checks
finally
    ComposeRepository.CustomDatabasePath <- None
    SqliteConnection.ClearAllPools()
    try if File.Exists(testDbPath) then File.Delete(testDbPath) with _ -> ()
