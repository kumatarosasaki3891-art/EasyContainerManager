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
#load "../EasyContainerManager/Models/OperationLog.fs"
#load "../EasyContainerManager/Models/MasterDataRepository.fs"
#load "../EasyContainerManager/Models/VersionDiagnostics.fs"
#load "../EasyContainerManager/AppSettings.fs"
#load "../EasyContainerManager/ContainerCli.fs"
#load "../EasyContainerManager/Models/ChoiceOption.fs"
#load "../EasyContainerManager/ViewModels/ViewModelBase.fs"
#load "../EasyContainerManager/ViewModels/MainViewModel.fs"

open System
open System.IO
open System.Reflection
open Microsoft.Data.Sqlite
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.ViewModels

let testDbPath = Path.Combine(Path.GetTempPath(), sprintf "easycontainer_oplog_%s.db" (Guid.NewGuid().ToString("N")))
ComposeRepository.CustomDatabasePath <- Some testDbPath

let mutable checks = 0
let check name condition =
    if not condition then failwith (sprintf "FAILED: %s" name)
    checks <- checks + 1
    printfn "PASS: %s" name

try
    // 1. 多言語辞書キー（全11言語）の確認
    let requiredKeys = [
        "操作履歴"
        "操作履歴はまだありません。"
        "コンテナ起動"
        "コンテナ停止"
        "コンテナ再起動"
        "コンテナ削除"
        "コンテナ接続 (exec)"
        "Compose 起動"
        "Compose 停止"
        "Compose 再起動"
    ]
    for key in requiredKeys do
        check (sprintf "Localization has all 11 translations for %s" key) (Localization.hasAllTranslations key)

    // 2. 純粋関数ユニットテスト (OperationLog)
    check "sanitizeDetail returns None for None and whitespace" (
        OperationLog.sanitizeDetail None |> Option.isNone
        && OperationLog.sanitizeDetail (Some "   \r\n\t ") |> Option.isNone)

    let longRaw = "line1\r\nline2\n" + String.replicate 250 "x"
    let sanitizedLong = OperationLog.sanitizeDetail (Some longRaw)
    check "sanitizeDetail collapses newlines and truncates to <= 200 chars" (
        sanitizedLong.IsSome
        && not (sanitizedLong.Value.Contains("\n"))
        && not (sanitizedLong.Value.Contains("\r"))
        && sanitizedLong.Value.StartsWith("line1 line2 ")
        && sanitizedLong.Value.EndsWith("...")
        && sanitizedLong.Value.Length <= OperationLog.maxDetailLength)

    check "action mappings normalize container and compose actions" (
        OperationLog.mapContainerAction "起動" = "start"
        && OperationLog.mapContainerAction "停止" = "stop"
        && OperationLog.mapContainerAction "再起動" = "restart"
        && OperationLog.mapContainerAction "削除" = "remove"
        && OperationLog.mapComposeAction "起動" = "compose_up"
        && OperationLog.mapComposeAction "停止" = "compose_down"
        && OperationLog.mapComposeAction "再起動" = "compose_restart"
        && OperationLog.composeTargetId "proj-1" = "compose:proj-1")

    check "operationLabel translates across languages and statusMark formats" (
        OperationLog.operationLabel "ja-JP" "start" = "コンテナ起動"
        && OperationLog.operationLabel "en-US" "start" = "Start container"
        && OperationLog.operationLabel "zh-CN" "compose_down" = "Compose 停止"
        && OperationLog.statusMark true = "✓"
        && OperationLog.statusMark false = "✗")

    // 純粋関数の保持上限・絞り込み・新しい順ソート
    let baseTime = DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)
    let mutable pureEntries: OperationLogEntry list = []
    for i in 1 .. 25 do
        let e = OperationLog.createEntry (baseTime.AddMinutes(float i)) "c-alpha" "alpha" "restart" true None
        pureEntries <- OperationLog.appendAndTrim 20 { e with Id = int64 i } pureEntries
    let otherEntry =
        { OperationLog.createEntry (baseTime.AddMinutes(1.0)) "c-beta" "beta" "stop" false (Some "exit 1") with Id = 99L }
    pureEntries <- OperationLog.appendAndTrim 20 otherEntry pureEntries

    let alphaFiltered = OperationLog.filterForContainer "C-ALPHA" pureEntries
    let betaFiltered = OperationLog.filterForContainer "c-beta" pureEntries
    check "pure trimPerContainer caps container A at 20 newest without affecting container B" (
        alphaFiltered.Length = 20
        && alphaFiltered.[0].Id = 25L
        && alphaFiltered.[19].Id = 6L
        && betaFiltered.Length = 1
        && betaFiltered.[0].Id = 99L
        && betaFiltered.[0].Detail = Some "exit 1")

    // 3. SQLite 永続化・新しい順取得・20件保持上限テスト
    OperationLogRepository.Initialize()
    for i in 1 .. 25 do
        let ok = (i % 2 = 1)
        let detailText = if ok then None else Some(sprintf "failure #%d" i)
        OperationLogRepository.Record(
            "db-c1",
            "web-1",
            "restart",
            ok,
            ?detail = detailText,
            executedAtUtc = baseTime.AddSeconds(float i)
        )
    for i in 1 .. 3 do
        OperationLogRepository.Record(
            "db-c2",
            "db-1",
            "start",
            true,
            executedAtUtc = baseTime.AddSeconds(float i)
        )

    let dbC1 = OperationLogRepository.GetByContainerId("db-c1")
    let dbC2 = OperationLogRepository.GetByContainerId("db-c2")
    let dbAll = OperationLogRepository.GetAll()
    check "SQLite retains 20 newest entries for db-c1 and all 3 for db-c2" (
        dbC1.Length = 20
        && dbC2.Length = 3
        && dbAll.Length = 23)
    check "SQLite returns newest entry #25 first (succeeded) and #24 second (failed with detail)" (
        dbC1.[0].Succeeded
        && dbC1.[0].Detail.IsNone
        && not dbC1.[1].Succeeded
        && dbC1.[1].Detail = Some "failure #24")

    // 4. MainViewModel 結合テスト（操作履歴の記録・除外・切替・多言語）
    let mutable nextContainersResult: Result<ContainerInfo list, string> =
        Ok [
            ContainerInfo("vm-c1", "app-one", "Up 1 hour", "nginx:latest", "today")
            ContainerInfo("vm-c2", "app-two", "Exited (0) 2 minutes ago", "redis:latest", "today")
        ]
    let mutable nextStatsResult: Result<ContainerStats list, string> = Ok []
    let mutable nextConnectionResult: Result<unit, string * string> = Ok ()
    let mutable nextContainerActionResult: Result<string, string> = Ok ""
    let mutable nextComposeActionResult: Result<unit, string> = Ok ()

    let vmSettings = AppSettings()
    let composeProj = ComposeProject(Id = "cmp-1", Name = "my-compose", FolderPath = "C:/dummy/cmp", Engine = "docker")
    vmSettings.ComposeProjects.Add(composeProj)

    let vm =
        new MainViewModel(
            settingsOverride = vmSettings,
            startAutomatically = false,
            runtime = Docker,
            containerActionRunner = (fun _ _ -> async { return nextContainerActionResult }),
            composeActionRunner = (fun _ _ -> async { return nextComposeActionResult }),
            containerListFetcher = (fun () -> async { return nextContainersResult }),
            imageListFetcher = (fun () -> async { return Ok [] }),
            composeStatusFetcher = (fun _ -> async { return Ok { Running = 1; Total = 1; Issues = [] } }),
            statsFetcher = (fun () -> async { return nextStatsResult }),
            connectionChecker = (fun () -> async { return nextConnectionResult })
        )

    // 初期 RefreshAsync は操作履歴を増やさないこと
    let countBeforeRefresh = OperationLogRepository.GetAll().Length
    vm.RefreshAsync() |> Async.RunSynchronously
    let countAfterRefresh = OperationLogRepository.GetAll().Length
    check "RefreshAsync does not add entries to operation_log" (
        countBeforeRefresh = countAfterRefresh && vm.Containers.Count = 2)

    // コンテナ1選択（最初は履歴空）
    vm.SelectedContainer <- vm.Containers.[0]
    check "newly selected container initially has empty operation logs" (
        not vm.HasSelectedContainerOperationLogs && vm.HasNoSelectedContainerOperationLogs)

    // start (Ok), stop (Error), restart (Ok) を順次実行
    nextContainerActionResult <- Ok ""
    vm.StartSelectedAsync() |> Async.RunSynchronously
    nextContainerActionResult <- Error "stop permission denied"
    vm.StopSelectedAsync() |> Async.RunSynchronously
    nextContainerActionResult <- Ok ""
    vm.RestartSelectedAsync() |> Async.RunSynchronously

    check "container operations recorded in newest-first order with failure detail" (
        vm.HasSelectedContainerOperationLogs
        && not vm.HasNoSelectedContainerOperationLogs
        && vm.SelectedContainerOperationLogs.Length = 3
        && vm.SelectedContainerOperationLogs.[0].OperationText = "コンテナ再起動"
        && vm.SelectedContainerOperationLogs.[0].ResultMark = "✓"
        && not vm.SelectedContainerOperationLogs.[0].HasDetail
        && vm.SelectedContainerOperationLogs.[1].OperationText = "コンテナ停止"
        && vm.SelectedContainerOperationLogs.[1].ResultMark = "✗"
        && vm.SelectedContainerOperationLogs.[1].Detail = "stop permission denied"
        && vm.SelectedContainerOperationLogs.[2].OperationText = "コンテナ起動"
        && vm.SelectedContainerOperationLogs.[2].ResultMark = "✓")

    // コンテナ2へ切替 → 履歴分離・削除操作記録確認
    vm.SelectedContainer <- vm.Containers.[1]
    check "switching to vm-c2 shows its own empty log list" (
        not vm.HasSelectedContainerOperationLogs && vm.HasNoSelectedContainerOperationLogs)

    nextContainerActionResult <- Ok ""
    vm.RemoveSelectedContainerAsync() |> Async.RunSynchronously
    check "remove action recorded for vm-c2" (
        vm.SelectedContainerOperationLogs.Length = 1
        && vm.SelectedContainerOperationLogs.[0].OperationText = "コンテナ削除"
        && vm.SelectedContainerOperationLogs.[0].ResultMark = "✓")

    // コンテナ選択解除で操作履歴表示フラグがクリアされる
    vm.SelectedContainer <- null
    check "clearing container selection clears operation logs display" (
        not vm.HasSelectedContainerOperationLogs
        && not vm.HasNoSelectedContainerOperationLogs
        && List.isEmpty vm.SelectedContainerOperationLogs)

    // 再度 vm-c1 選択中に言語切替で操作履歴の文言が切り替わること
    vm.SelectedContainer <- vm.Containers.[0]
    let enOpt = vm.Languages |> Array.find (fun opt -> opt.Key = "en-US")
    let jaOpt = vm.Languages |> Array.find (fun opt -> opt.Key = "ja-JP")
    vm.Language <- enOpt
    check "language switch translates OperationLogHeader and entry labels to English" (
        vm.OperationLogHeader = "Operation log"
        && vm.OperationLogEmptyText = "No operations recorded yet."
        && vm.SelectedContainerOperationLogs.[0].OperationText = "Restart container"
        && vm.SelectedContainerOperationLogs.[1].OperationText = "Stop container")
    vm.Language <- jaOpt

    // Compose 操作ログ（起動 Ok / 停止 Error）
    vm.SelectedComposeProject <- vm.ComposeProjects.[0]
    check "newly selected Compose project initially has empty operation logs" (
        not vm.HasSelectedComposeOperationLogs && vm.HasNoSelectedComposeOperationLogs)

    nextComposeActionResult <- Ok ()
    vm.StartComposeAsync() |> Async.RunSynchronously
    nextComposeActionResult <- Error "compose down timed out"
    vm.StopComposeAsync() |> Async.RunSynchronously
    check "Compose operations recorded under compose:<id> newest-first" (
        vm.HasSelectedComposeOperationLogs
        && vm.SelectedComposeOperationLogs.Length = 2
        && vm.SelectedComposeOperationLogs.[0].OperationText = "Compose 停止"
        && vm.SelectedComposeOperationLogs.[0].ResultMark = "✗"
        && vm.SelectedComposeOperationLogs.[0].Detail = "compose down timed out"
        && vm.SelectedComposeOperationLogs.[1].OperationText = "Compose 起動"
        && vm.SelectedComposeOperationLogs.[1].ResultMark = "✓")

    // 5. 失敗バナーの成功時クリア検証 (v1.1 項目3)
    // 5a: コンテナ操作失敗でエラー状態・詳細欄が立つ
    nextContainerActionResult <- Error "daemon temporarily unavailable"
    vm.StartSelectedAsync() |> Async.RunSynchronously
    check "failed container action sets IsErrorStatus and error banner + details" (
        vm.IsErrorStatus
        && vm.StatusMessage = "コンテナ操作に失敗しました。詳細を確認してください。"
        && vm.HasConnectionDetails
        && vm.ConnectionDetails = "daemon temporarily unavailable")

    // 5b: 次回 refresh が全失敗ならエラーバナーは維持される
    nextContainersResult <- Error "still connecting to daemon"
    vm.RefreshAsync() |> Async.RunSynchronously
    check "failed refresh preserves error banner and details" (
        vm.IsErrorStatus
        && vm.StatusMessage = "コンテナ操作に失敗しました。詳細を確認してください。"
        && vm.HasConnectionDetails
        && vm.ConnectionDetails = "still connecting to daemon")

    // 5c: 次回 refresh が一部失敗（ps は Ok、stats は Error）でもエラーバナーは消えない
    nextContainersResult <- Ok [
        ContainerInfo("vm-c1", "app-one", "Up 1 hour", "nginx:latest", "today")
        ContainerInfo("vm-c2", "app-two", "Exited (0) 2 minutes ago", "redis:latest", "today")
    ]
    nextStatsResult <- Error "stats stream failed"
    vm.RefreshAsync() |> Async.RunSynchronously
    check "partial failing refresh keeps error banner and error details" (
        vm.IsErrorStatus
        && vm.StatusMessage = "コンテナ操作に失敗しました。詳細を確認してください。"
        && vm.HasConnectionDetails
        && vm.ConnectionDetails = "stats stream failed")

    // 5d: 次回 refresh が全ステップ成功したらエラーバナーと詳細欄がクリアされ通常ステータスへ戻る
    nextStatsResult <- Ok []
    vm.RefreshAsync() |> Async.RunSynchronously
    check "fully succeeding refresh clears IsErrorStatus, updates status, and clears ConnectionDetails" (
        not vm.IsErrorStatus
        && vm.StatusMessage = "0件のイメージ"
        && not vm.HasConnectionDetails
        && vm.ConnectionDetails = "")

    // 5e: 非エラーの通常ステータスは ClearErrorStatus で消えないこと
    let clearErrorMethod = typeof<MainViewModel>.GetMethod("ClearErrorStatus", BindingFlags.Instance ||| BindingFlags.NonPublic)
    clearErrorMethod.Invoke(vm, [||]) |> ignore
    check "ClearErrorStatus does not clear non-error status message" (
        not vm.IsErrorStatus && vm.StatusMessage = "0件のイメージ")

    (vm :> IDisposable).Dispose()
    printfn "\nAll %d operation log and error-banner checks passed!" checks
finally
    ComposeRepository.CustomDatabasePath <- None
    SqliteConnection.ClearAllPools()
    try if File.Exists(testDbPath) then File.Delete(testDbPath) with _ -> ()
