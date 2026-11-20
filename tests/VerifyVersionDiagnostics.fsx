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
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.ViewModels

let testDbPath = Path.Combine(Path.GetTempPath(), sprintf "easycontainer_verdiag_%s.db" (Guid.NewGuid().ToString("N")))
ComposeRepository.CustomDatabasePath <- Some testDbPath

let mutable checks = 0
let check name condition =
    if not condition then failwith (sprintf "FAILED: %s" name)
    checks <- checks + 1
    printfn "PASS: %s" name

try
    // 1. 多言語辞書キーが全11言語で揃っているかの確認
    let requiredKeys = [
        "定義データ: {0}時点"
        "定義データ: 未取得"
        "バージョン診断"
        "緊急アドバイザリの対象バージョン (条件付き)"
        "対象バージョンです。HTTP/2 を有効にしている場合は危険です。"
        "対象バージョンです。成立条件に該当する場合は危険です。"
        "緊急アドバイザリの対象バージョン"
        "重大な脆弱性の対象バージョンです。修正版への更新を推奨します。"
        "サポート終了 (EOL) ブランチの使用"
        "使用中のランタイムブランチはサポート期限を終了しています。保守中のブランチへの更新を推奨します。"
        "ランタイムのパッチバージョン遅れ"
        "使用中のブランチに新しいパッチバージョンが公開されています。"
        "ランタイムの旧マイナーブランチ使用"
        "より新しい安定版ブランチが公開されています。"
        "コンテナエンジンのバージョン遅れ"
        "使用中のコンテナエンジンに新しい安定版が公開されています。"
        "アプリ情報"
        "現在のバージョン"
        "最新バージョン"
        "未確認"
        "新しいバージョンがあります"
        "最新版を見る"
        "定義データを更新"
        "更新中..."
        "定義データを更新しました"
        "定義データの取得に失敗しました（キャッシュを使用します）"
        "紹介サイト"
        "診断処理は端末内で完結します。バージョン定義データの取得時のみ GitHub へ通信し、ユーザーのデータや診断結果を送信することはありません。"
    ]
    for key in requiredKeys do
        check (sprintf "Localization has all 11 translations for %s" key) (Localization.hasAllTranslations key)

    // 2. バージョン解析・数値比較
    let v127 = VersionDiagnostics.tryParseVersion "1.27.0"
    let v19 = VersionDiagnostics.tryParseVersion "1.9.0"
    check "1.27.0 > 1.9.0 numerically" (v127.IsSome && v19.IsSome && VersionDiagnostics.compareVersions v127.Value v19.Value > 0)
    check "v28.5.1 parses" (VersionDiagnostics.tryParseVersion "v28.5.1" = Some { Raw = "28.5.1"; Segments = [ 28; 5; 1 ] })
    check "8.3-fpm parses" (VersionDiagnostics.tryParseVersion "8.3-fpm" = Some { Raw = "8.3"; Segments = [ 8; 3 ] })
    check "16.4-alpine parses" (VersionDiagnostics.tryParseVersion "16.4-alpine" = Some { Raw = "16.4"; Segments = [ 16; 4 ] })
    check "latest tag is silent" (VersionDiagnostics.tryParseVersion "latest" |> Option.isNone)
    check "alpine tag is silent" (VersionDiagnostics.tryParseVersion "alpine" |> Option.isNone)
    check "date 20261119 tag is silent" (VersionDiagnostics.tryParseVersion "20261119" |> Option.isNone)
    check "date 2026-11-19 tag is silent" (VersionDiagnostics.tryParseVersion "2026-11-19" |> Option.isNone)

    // 3. イメージタグ抽出と沈黙ケース
    check "extracts nginx:1.30.6" (
        match VersionDiagnostics.tryExtractImageRuntimeAndVersion "nginx:1.30.6" with
        | Some ("nginx", "1.30.6", v) -> v.Segments = [ 1; 30; 6 ]
        | _ -> false)
    check "extracts registry/php:8.3-fpm" (
        match VersionDiagnostics.tryExtractImageRuntimeAndVersion "docker.io/library/php:8.3-fpm" with
        | Some ("php", "8.3-fpm", v) -> v.Segments = [ 8; 3 ]
        | _ -> false)
    check "extracts port registry/postgres:16.2@sha256" (
        match VersionDiagnostics.tryExtractImageRuntimeAndVersion "localhost:5000/org/postgresql:16.2@sha256:deadbeef" with
        | Some ("postgres", "16.2", v) -> v.Segments = [ 16; 2 ]
        | _ -> false)
    check "nginx:latest is silent" (VersionDiagnostics.tryExtractImageRuntimeAndVersion "nginx:latest" |> Option.isNone)
    check "nginx without tag is silent" (VersionDiagnostics.tryExtractImageRuntimeAndVersion "nginx" |> Option.isNone)
    check "custom-nginx:1.0.0 is silent" (VersionDiagnostics.tryExtractImageRuntimeAndVersion "my-custom-nginx:1.0.0" |> Option.isNone)

    // 4. SQLite 初期化と初期スナップショット
    MasterDataRepository.Initialize()
    let snapshot = MasterDataRepository.GetSnapshot()
    check "initial snapshot loaded engines, runtimes, apps" (
        snapshot.Engines.Length >= 3
        && snapshot.Runtimes.Length >= 6
        && snapshot.Apps.Length >= 2)
    check "definition date text is 2026-11-19" (MasterDataRepository.GetDefinitionDateText snapshot = Some "2026-11-19")
    check "initial ShouldAutoFetch is true because fetched_at is empty" (MasterDataRepository.ShouldAutoFetch DateTimeOffset.UtcNow)

    // 5. 緊急 advisory 照合（境界・ブランチ別修正版・条件付き文言・EOL重複抑制）
    let refDate = DateOnly(2026, 11, 19)
    let advFromStart = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.9.5" refDate snapshot
    check "nginx:1.9.5 (exact from) triggers Danger advisory" (
        advFromStart.Length = 1 && advFromStart.[0].Severity = FindingSeverity.Danger)

    let adv1306 = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.30.6" refDate snapshot
    check "nginx:1.30.6 triggers Danger conditional http2 advisory" (
        adv1306.Length = 1
        && adv1306.[0].Severity = FindingSeverity.Danger
        && adv1306.[0].TitleKey = "緊急アドバイザリの対象バージョン (条件付き)"
        && adv1306.[0].SummaryKey = "対象バージョンです。HTTP/2 を有効にしている場合は危険です。"
        && (adv1306.[0].EvidenceLines |> String.concat "\n").Contains("CVE-2026-123007")
        && (adv1306.[0].EvidenceLines |> String.concat "\n").Contains("1.30.7")
        && (adv1306.[0].EvidenceLines |> String.concat "\n").Contains("https://nginx.org/en/security_advisories.html"))

    let adv1307 = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.30.7" refDate snapshot
    check "nginx:1.30.7 (exact toExclusive / fixed) has 0 findings" (List.isEmpty adv1307)

    let adv1310 = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.31.0" refDate snapshot
    check "nginx:1.31.0 (exact from of second range) triggers Danger advisory" (
        adv1310.Length = 1 && adv1310.[0].Severity = FindingSeverity.Danger)

    let adv1318 = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.31.8" refDate snapshot
    check "nginx:1.31.8 triggers Danger advisory" (
        adv1318.Length = 1 && adv1318.[0].Severity = FindingSeverity.Danger)

    let adv1319 = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.31.9" refDate snapshot
    check "nginx:1.31.9 (exact toExclusive / fixed) has 0 findings" (List.isEmpty adv1319)

    let advAndEolOverlap = VersionDiagnostics.diagnoseRuntimeImage "nginx:1.26" refDate snapshot
    check "nginx:1.26 (both advisory and EOL) deduplicates to 1 Danger advisory finding" (
        advAndEolOverlap.Length = 1 && advAndEolOverlap.[0].Severity = FindingSeverity.Danger)

    // 6. EOL 境界（当日と翌日）・パッチ遅れ閾値・旧マイナーブランチ・沈黙ケース
    let mysqlOnEolDay = VersionDiagnostics.diagnoseRuntimeImage "mysql:8.0.41" (DateOnly(2026, 4, 30)) snapshot
    check "mysql:8.0.41 on exact EOL day (2026-04-30) is not EOL yet" (List.isEmpty mysqlOnEolDay)

    let mysqlAfterEolDay = VersionDiagnostics.diagnoseRuntimeImage "mysql:8.0.41" (DateOnly(2026, 5, 1)) snapshot
    check "mysql:8.0.41 on day after EOL (2026-05-01) triggers Warning" (
        mysqlAfterEolDay.Length = 1
        && mysqlAfterEolDay.[0].Severity = FindingSeverity.Warning
        && mysqlAfterEolDay.[0].TitleKey = "サポート終了 (EOL) ブランチの使用")

    let phpPatch2Behind = VersionDiagnostics.diagnoseRuntimeImage "php:8.4.3" refDate snapshot
    check "php:8.4.3 (2 patches behind 8.4.5) is silent" (List.isEmpty phpPatch2Behind)

    let phpPatch3Behind = VersionDiagnostics.diagnoseRuntimeImage "php:8.4.2" refDate snapshot
    check "php:8.4.2 (3 patches behind 8.4.5) triggers Info patch lag" (
        phpPatch3Behind.Length = 1
        && phpPatch3Behind.[0].Severity = FindingSeverity.Info
        && phpPatch3Behind.[0].TitleKey = "ランタイムのパッチバージョン遅れ")

    let phpOlderMinor = VersionDiagnostics.diagnoseRuntimeImage "php:8.2" refDate snapshot
    check "php:8.2 (2 minor branches behind 8.4) triggers Info minor branch lag" (
        phpOlderMinor.Length = 1
        && phpOlderMinor.[0].Severity = FindingSeverity.Info
        && phpOlderMinor.[0].TitleKey = "ランタイムの旧マイナーブランチ使用")

    let phpCurrentMinor = VersionDiagnostics.diagnoseRuntimeImage "php:8.3" refDate snapshot
    check "php:8.3 (1 minor branch behind 8.4) is silent" (List.isEmpty phpCurrentMinor)

    check "nginx:latest produces 0 findings" (List.isEmpty (VersionDiagnostics.diagnoseRuntimeImage "nginx:latest" refDate snapshot))
    check "unknown runtime produces 0 findings" (List.isEmpty (VersionDiagnostics.diagnoseRuntimeImage "myservice:1.0.0" refDate snapshot))

    // 7. エンジンバージョン診断
    let oldDocker = VersionDiagnostics.diagnoseEngine "docker" "Docker version 27.5.1, build e180ab8" snapshot
    check "old Docker 27.5.1 triggers Info engine lag" (
        oldDocker.Length = 1
        && oldDocker.[0].Severity = FindingSeverity.Info
        && oldDocker.[0].TitleKey = "コンテナエンジンのバージョン遅れ")

    let currentDocker = VersionDiagnostics.diagnoseEngine "docker" "Docker version 28.5.0, build abc" snapshot
    check "Docker 28.5.0 (same major/minor) is silent" (List.isEmpty currentDocker)

    // 8. スキーマ不一致の拒否とキャッシュ維持
    let futureAppsJson = """{"schemaVersion":2,"updatedAt":"2027-01-01T00:00:00Z","apps":[{"id":"simple-docker-manager","displayName":"X","latest":"9.9.9"}]}"""
    check "rejects schemaVersion 2 apps.json" (not (MasterDataRepository.ImportAppsJson(futureAppsJson, "2027-01-01T00:00:00Z")))
    let afterFutureSnapshot = MasterDataRepository.GetSnapshot()
    let appRecAfterFuture = MasterDataRepository.TryGetApp("simple-docker-manager", afterFutureSnapshot)
    check "retains cached app latest 1.0.0 after rejected schema" (
        appRecAfterFuture.IsSome && appRecAfterFuture.Value.Latest = "1.0.0")

    let futureRuntimesJson = """{"schemaVersion":99,"updatedAt":"2027-01-01T00:00:00Z","runtimes":[]}"""
    check "rejects schemaVersion 99 runtimes.json" (not (MasterDataRepository.ImportRuntimesJson(futureRuntimesJson, "2027-01-01T00:00:00Z")))
    check "retains cached runtimes after rejected schema" (MasterDataRepository.GetSnapshot().Runtimes.Length >= 6)

    // 9. オフライン失敗時と 24 時間スロットル
    let t0 = DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero)
    let offlineFetcher (_: string) = async { return Error "network unreachable" }
    let offlineOk = MasterDataRepository.FetchAndImportAsync(customFetcher = offlineFetcher, nowUtc = t0) |> Async.RunSynchronously
    check "offline fetch returns false without throwing" (not offlineOk)
    check "cache remains intact after offline fetch" (MasterDataRepository.GetSnapshot().Runtimes.Length >= 6)

    let updatedAppsJson = """{"schemaVersion":1,"updatedAt":"2026-11-20T00:00:00Z","apps":[{"id":"simple-docker-manager","displayName":"簡単コンテナ管理","latest":"1.1.0","storeUrl":"https://sites.google.com/view/kuma-applications/"}]}"""
    let successFetcher (url: string) = async {
        if url = MasterDataRepository.AppsUrl then return Ok updatedAppsJson
        elif url = MasterDataRepository.EnginesUrl then
            return Ok """{"schemaVersion":1,"updatedAt":"2026-11-20T00:00:00Z","engines":[{"id":"docker","displayName":"Docker Engine","latest":"28.5.1","releases":["28.5.1"]}]}"""
        else
            return Ok """{"schemaVersion":1,"updatedAt":"2026-11-20T00:00:00Z","runtimes":[{"id":"nginx","displayName":"nginx","branches":[{"branch":"1.31","latest":"1.31.9","eol":null,"support":true}]}]}"""
    }
    let onlineOk = MasterDataRepository.FetchAndImportAsync(customFetcher = successFetcher, nowUtc = t0) |> Async.RunSynchronously
    check "online fetch succeeds and updates cache" onlineOk
    check "ShouldAutoFetch is false 1 hour after fetch" (not (MasterDataRepository.ShouldAutoFetch(t0.AddHours(1.0))))
    check "ShouldAutoFetch is true 24 hours after fetch" (MasterDataRepository.ShouldAutoFetch(t0.AddHours(24.0)))

    // 10. MainViewModel の ℹ️ ダイアログ・アプリ古さ判定・多言語切替
    let vm = new MainViewModel(
        settingsOverride = AppSettings(),
        startAutomatically = false,
        masterDataFetcher = successFetcher,
        appVersionOverride = "1.0.0"
    )
    check "VM reads updated latest 1.1.0 and marks app outdated" (
        vm.CurrentAppVersionText = "1.0.0"
        && vm.LatestAppVersionText = "1.1.0"
        && vm.IsAppOutdated
        && vm.HasStoreUpdateLink
        && vm.DiagnosticsDefinitionDateText = "定義データ: 2026-11-20時点")

    vm.OpenInfoDialog()
    check "OpenInfoDialog opens dialog" vm.IsInfoDialogOpen
    vm.CloseInfoDialog()
    check "CloseInfoDialog closes dialog" (not vm.IsInfoDialogOpen)

    let enOption = vm.Languages |> Array.find (fun opt -> opt.Key = "en-US")
    vm.Language <- enOption
    check "switching to English localizes Info and Definition texts" (
        vm.InfoDialogTitle = "About"
        && vm.AppUpdateAvailableText = "A new version is available"
        && vm.DiagnosticsDefinitionDateText = "Definitions: as of 2026-11-20"
        && vm.UpdateMasterDataButtonLabel = "Update definitions")

    let vmLatest = new MainViewModel(
        settingsOverride = AppSettings(),
        startAutomatically = false,
        appVersionOverride = "1.1.0"
    )
    check "when current version equals latest (1.1.0), IsAppOutdated is false" (not vmLatest.IsAppOutdated)
    (vmLatest :> IDisposable).Dispose()
    (vm :> IDisposable).Dispose()

    printfn "\nAll %d version diagnostics and master data checks passed!" checks
finally
    ComposeRepository.CustomDatabasePath <- None
    try if File.Exists(testDbPath) then File.Delete(testDbPath) with _ -> ()
