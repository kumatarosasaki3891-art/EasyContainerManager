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
open System.Text
open Microsoft.Data.Sqlite
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.Services
open EasyContainerManager.ViewModels

let testDbPath = Path.Combine(Path.GetTempPath(), sprintf "easycontainer_v12mobile_%s.db" (Guid.NewGuid().ToString("N")))
ComposeRepository.CustomDatabasePath <- Some testDbPath

let mutable checks = 0
let check name condition =
    if not condition then failwith (sprintf "FAILED: %s" name)
    checks <- checks + 1
    printfn "PASS: %s" name

try
    // 1. FAQ / アラート / スマホセッションの多言語辞書キー（全11言語）
    let requiredKeys = [
        "初心者向け FAQ"
        "コンテナ・Compose・ボリューム・スマホ連携の基本Q&A"
        "キーワードで絞り込み..."
        "すべて"
        "この言語では FAQ 本文を英語で表示しています。"
        "音声読み上げ（🔊）はスマホ連携ページの FAQ タブで利用できます。"
        "一致する質問が見つかりませんでした。"
        "アラート履歴"
        "アラート履歴はまだありません。"
        "コンテナ異常終了"
        "OOMKilled 検出"
        "再起動回数の急増"
        "サービス診断（危険）"
        "ボリューム容量超過"
        "スマホ連携 (QR)"
        "セッション開始"
        "セッション停止"
        "QR再発行 (ロック解除)"
        "接続用 IP アドレス"
        "接続 URL"
        "停止中（ボタン操作時のみ起動します）"
        "待機中（最初にQRを読み取った1台のみ接続できます）"
        "1対1接続中: {0}"
        "タイムアウトで切断されました"
        "同一 Wi-Fi 内・閲覧専用（操作や外部公開は行いません）。無通信15分で自動切断します。"
        "サーバー起動に失敗しました: {0}"
        "QRをスマホで読み取ると同じWi-Fi内から閲覧専用ページを開けます。"
        "このループバックアドレスは同じPC内の確認用です。スマホからは 192.168.x.x 等を選択してください。"
        "アラートを閉じる"
    ]
    for key in requiredKeys do
        check (sprintf "Localization has all 11 translations for %s" key) (Localization.hasAllTranslations key)

    // 2. 初心者向け FAQ データ・言語フォールバック・検索
    check "FaqData resolves ja/en and falls back non-JA/EN languages to English" (
        FaqData.resolveFaqLanguage "ja-JP" = "ja-JP"
        && FaqData.resolveFaqLanguage "en-US" = "en-US"
        && FaqData.resolveFaqLanguage "fr-FR" = "en-US"
        && not (FaqData.isEnglishFallback "ja-JP")
        && not (FaqData.isEnglishFallback "en-US")
        && FaqData.isEnglishFallback "de-DE")

    let allJaFaq = FaqData.filterItems "ja-JP" None ""
    let allEnFaq = FaqData.filterItems "en-US" None ""
    let composeFaq = FaqData.filterItems "ja-JP" None "Compose"
    check "FaqData contains rich JA & EN items and filters cleanly" (
        allJaFaq.Length >= 10
        && allEnFaq.Length = allJaFaq.Length
        && composeFaq |> List.exists (fun i -> i.Question.Contains("Compose")))

    // 3. アラート差分検知（ベースライン・異常終了・OOMKilled・再起動急増・ボリューム閾値・診断危険）
    let tBase = DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)
    let snapRunning = { Id = "c1"; Name = "api"; IsRunning = true; IsRestarting = false; OomKilled = false; ExitCode = 0; RestartCount = 0 }
    let baselineAlerts = AlertMonitor.detectContainerAnomalies true Map.empty [ snapRunning ] tBase
    check "baseline refresh produces 0 anomaly alerts" (List.isEmpty baselineAlerts)

    let prevMap = Map.ofList [ ("c1", snapRunning) ]
    let snapExited = { snapRunning with IsRunning = false; ExitCode = 137 }
    let exitAlerts = AlertMonitor.detectContainerAnomalies false prevMap [ snapExited ] (tBase.AddSeconds(10.0))
    check "transition Up -> Exited(137) emits ContainerAbnormalExit" (
        exitAlerts.Length = 1 && exitAlerts.[0].Category = AlertCategory.ContainerAbnormalExit)

    let snapOom = { snapRunning with IsRunning = false; OomKilled = true; ExitCode = 137 }
    let oomAlerts = AlertMonitor.detectContainerAnomalies false prevMap [ snapOom ] (tBase.AddSeconds(20.0))
    check "OOMKilled=true emits ContainerOomKilled without duplicate AbnormalExit" (
        oomAlerts.Length = 1 && oomAlerts.[0].Category = AlertCategory.ContainerOomKilled)

    let snapRestart = { snapRunning with RestartCount = 3; IsRestarting = true }
    let restartAlerts = AlertMonitor.detectContainerAnomalies false prevMap [ snapRestart ] (tBase.AddSeconds(30.0))
    check "RestartCount surge emits ContainerRestartSurge" (
        restartAlerts.Length = 1 && restartAlerts.[0].Category = AlertCategory.ContainerRestartSurge)

    let volWarnSnap =
        { Name = "bigdata"; Driver = "local"; Mountpoint = "/mnt/big"; CreatedAt = ""; SizeBytes = Some (6L * 1024L * 1024L * 1024L); SizeText = "6 GB"; Links = Some 1; IsAnonymous = false; IsUnused = false; AttachedContainers = [] }
    let volAlerts1, volLvls1 = AlertMonitor.detectVolumeThresholdAlerts VolumeParser.volumeWarningThresholdBytes VolumeParser.volumeDangerThresholdBytes Map.empty [ volWarnSnap ] tBase
    let volAlertsSame, _ = AlertMonitor.detectVolumeThresholdAlerts VolumeParser.volumeWarningThresholdBytes VolumeParser.volumeDangerThresholdBytes volLvls1 [ volWarnSnap ] (tBase.AddSeconds(10.0))
    let volDangerSnap = { volWarnSnap with SizeBytes = Some (25L * 1024L * 1024L * 1024L); SizeText = "25 GB" }
    let volAlertsUp, _ = AlertMonitor.detectVolumeThresholdAlerts VolumeParser.volumeWarningThresholdBytes VolumeParser.volumeDangerThresholdBytes volLvls1 [ volDangerSnap ] (tBase.AddSeconds(20.0))
    check "volume alerts trigger Warning -> dedup -> Danger escalation only" (
        volAlerts1.Length = 1 && volAlerts1.[0].Severity = AlertSeverity.Warning
        && List.isEmpty volAlertsSame
        && volAlertsUp.Length = 1 && volAlertsUp.[0].Severity = AlertSeverity.Danger)

    let diagAlert =
        AlertMonitor.createDiagnosticsDangerAlert "c1" "api" [ { Severity = FindingSeverity.Danger; TitleKey = "OOMKilled"; SummaryKey = ""; EvidenceLines = []; Count = 1 } ] tBase
    check "createDiagnosticsDangerAlert creates DiagnosticsDanger alert when Danger finding exists" (
        diagAlert.IsSome && diagAlert.Value.Category = AlertCategory.DiagnosticsDanger)

    // 4. SQLite alert_history 永続化・保持上限 200 件
    AlertHistoryRepository.Initialize()
    for i in 1 .. 205 do
        AlertHistoryRepository.Record({
            Id = 0L
            OccurredAt = tBase.AddSeconds(float i).ToString("o")
            Severity = AlertSeverity.Warning
            Category = AlertCategory.VolumeThresholdExceeded
            TargetId = sprintf "vol:%d" i
            TargetName = sprintf "v-%d" i
            TitleKey = "ボリューム容量超過"
            Message = sprintf "msg #%d" i
        }) |> ignore
    let storedAlerts = AlertHistoryRepository.GetRecent(limit = 300)
    check "AlertHistoryRepository enforces 200 newest entries cap" (
        storedAlerts.Length = 200 && storedAlerts.[0].Message = "msg #205")

    // 5. QR コードマトリクス生成（Version 1..10、正方形＋ファインダパターン）
    let qrMatrix = QrCodeGenerator.encodeText "http://192.168.1.50:54830/?t=abcdef1234567890"
    let qrSize = qrMatrix.GetLength(0)
    check "QrCodeGenerator encodes typical LAN URL into valid square matrix with finder patterns" (
        qrSize = qrMatrix.GetLength(1)
        && qrSize >= 21
        && qrMatrix.[0, 0] && qrMatrix.[6, 6] && not qrMatrix.[1, 1]
        && qrMatrix.[0, qrSize - 1] && qrMatrix.[qrSize - 1, 0])

    // 6. スマホ連携 1対1 セッション認証・読み取り専用・無通信タイムアウト・SSE/Snapshot・再発行
    check "MobileSessionServer default port does not collide with 54820 or 5050/5058/5070" (
        MobileSessionServer.DefaultPort = 54830)

    let srv = new MobileSessionServer()
    srv.UpdateSnapshot(
        { MobileDashboardSnapshot.Empty with
            EngineMode = "Docker"
            EngineRunning = true
            Containers = [ { Id = "c1"; Name = "<web&test>"; Status = "Up 1 hour"; StateKey = "running"; Image = "nginx:1.31"; Ports = "80->80/tcp"; ResourceSummary = "CPU 1%"; MountsSummary = "data"; HasDangerDiagnostics = false; HasWarningDiagnostics = false; DiagnosticsNote = "" } ]
            Volumes = [ { Name = "data"; DisplayName = "data"; Driver = "local"; SizeText = "1.2 GB"; SizeBytes = Some 1_200_000_000L; SizeLevel = 0; IsAnonymous = false; IsUnused = false; AttachedSummary = "<web&test>" } ]
            Alerts = [] })

    let stInit = srv.StartInMemorySessionForTest(idleTimeoutMinutes = 5.0, nowUtc = tBase)
    let sendHttp (rawReq: string) (ip: string) (ts: DateTimeOffset) =
        let respBytes = srv.ProcessHttpBytes(Encoding.UTF8.GetBytes(rawReq), remoteIp = ip, nowUtc = ts)
        Encoding.UTF8.GetString(respBytes)

    let badTokResp = sendHttp "GET /?t=wrongtoken HTTP/1.1\r\nHost: localhost\r\n\r\n" "192.168.1.10" tBase
    check "unclaimed session rejects wrong QR token with 403" (badTokResp.StartsWith("HTTP/1.1 403 Forbidden"))

    let okFirstResp = sendHttp (sprintf "GET /?t=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" stInit.QrToken) "192.168.1.10" (tBase.AddSeconds(5.0))
    check "first device with valid QR token claims 1-on-1 lock, sets cookie, and serves read-only HTML with TTS" (
        okFirstResp.StartsWith("HTTP/1.1 200 OK")
        && okFirstResp.Contains("Set-Cookie: ecm_session=")
        && okFirstResp.Contains("speechSynthesis")
        && okFirstResp.Contains("EventSource")
        && okFirstResp.Contains("\\u003Cweb\\u0026test\\u003E")
        && srv.GetStatus().LockedClientIp = Some "192.168.1.10")

    // Cookie 抽出
    let cookieLine =
        okFirstResp.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.find (fun l -> l.StartsWith("Set-Cookie: ecm_session="))
    let sid = cookieLine.Substring("Set-Cookie: ecm_session=".Length).Split(';').[0]

    let secondPhoneResp = sendHttp (sprintf "GET /?t=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" stInit.QrToken) "192.168.1.20" (tBase.AddSeconds(10.0))
    check "second device IP is rejected with 403 even with valid QR token" (
        secondPhoneResp.StartsWith("HTTP/1.1 403 Forbidden"))

    let snapResp = sendHttp (sprintf "GET /api/snapshot?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddSeconds(15.0))
    check "claimed device reads /api/snapshot JSON" (
        snapResp.StartsWith("HTTP/1.1 200 OK") && snapResp.Contains("\"engineMode\":\"Docker\""))

    let sseResp = sendHttp (sprintf "GET /api/events?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddSeconds(18.0))
    check "claimed device connects to /api/events SSE" (
        sseResp.StartsWith("HTTP/1.1 200 OK")
        && sseResp.Contains("text/event-stream")
        && sseResp.Contains("event: hello"))

    let hbResp = sendHttp (sprintf "POST /api/heartbeat?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddMinutes(4.0))
    check "POST /api/heartbeat extends inactivity timer" (
        hbResp.StartsWith("HTTP/1.1 200 OK") && hbResp.Contains("\"active\":true"))

    // ハートビートで延長されたので tBase+6分 はまだ有効（4分 + 5分 = 9分まで）
    let stillAliveResp = sendHttp (sprintf "GET /api/snapshot?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddMinutes(6.0))
    check "session remains alive after heartbeat beyond original deadline" (
        stillAliveResp.StartsWith("HTTP/1.1 200 OK"))

    let deleteResp = sendHttp (sprintf "DELETE /?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddMinutes(6.5))
    let postMutationResp = sendHttp (sprintf "POST /api/containers/stop?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddMinutes(6.5))
    check "mutating HTTP methods/endpoints return 405 Method Not Allowed" (
        deleteResp.StartsWith("HTTP/1.1 405 Method Not Allowed")
        && postMutationResp.StartsWith("HTTP/1.1 405 Method Not Allowed"))

    let expiredResp = sendHttp (sprintf "GET /?s=%s HTTP/1.1\r\nHost: localhost\r\n\r\n" sid) "192.168.1.10" (tBase.AddMinutes(15.0))
    check "expired session after idle timeout returns 410 Gone and clears lock" (
        expiredResp.StartsWith("HTTP/1.1 410 Gone")
        && srv.GetStatus().TimedOut
        && srv.GetStatus().LockedClientIp.IsNone)

    // 7. MainViewModel 結合（自動起動しない・実サーバー起動停止・バナー・アラート配信・FAQ）
    let mutable nextContainers =
        [ ContainerInfo("vm1", "web", "Up 1 hour", "nginx:1.31", "today", initialRestartCount = 0, initialExitCode = 0) ]
    let mutable osAlerts = ResizeArray<string * string>()
    let vmSettings = AppSettings(NotificationsEnabled = true)
    let vm =
        new MainViewModel(
            settingsOverride = vmSettings,
            startAutomatically = false,
            containerListFetcher = (fun () -> async { return Ok nextContainers }),
            imageListFetcher = (fun () -> async { return Ok [] }),
            statsFetcher = (fun () -> async { return Ok [] }),
            connectionChecker = (fun () -> async { return Ok () }),
            volumeListFetcher = (fun () -> async { return Ok [] }),
            osNotifier = (fun title msg -> osAlerts.Add(title, msg))
        )

    check "MobileSessionServer is NOT running automatically on startup" (not vm.IsMobileServerRunning)

    vm.OpenFaqDialog()
    check "FAQ ChoiceOption getters return cached reference-equal instances" (
        obj.ReferenceEquals(vm.SelectedFaqLanguage, vm.FaqLanguageOptions.[0])
        && obj.ReferenceEquals(vm.SelectedFaqCategory, vm.FaqCategoryOptions.[0]))
    vm.FaqSearchText <- "ボリューム"
    check "MainViewModel FAQ dialog opens and searches items" (
        vm.IsFaqDialogOpen && not vm.HasNoFaqMatches)
    vm.CloseFaqDialog()

    // 実ソケットでの手動 Start / Reissue / Stop 確認（エフェメラルまたは 54830 番台）
    vm.StartMobileSession()
    check "StartMobileSession starts local server on demand and populates URL" (
        vm.IsMobileServerRunning && vm.MobileSessionUrl.StartsWith("http://"))

    // 1回目更新（ベースライン記録：通知なし）、2回目更新（OOMKilled 検知：バナー＋履歴＋OS通知）
    osAlerts.Clear()
    vm.RefreshAsync() |> Async.RunSynchronously
    let osCountAfterBaseline = osAlerts.Count
    nextContainers <- [ ContainerInfo("vm1", "web", "Exited (137) 1 second ago", "nginx:1.31", "today", initialRestartCount = 1, initialOomKilled = true, initialExitCode = 137) ]
    vm.RefreshAsync() |> Async.RunSynchronously
    check "anomaly on second refresh sets ActiveAlertBanner, records SQLite, and invokes OS notifier" (
        osCountAfterBaseline = 0
        && vm.HasActiveAlertBanner
        && vm.AlertHistory.Length >= 1
        && vm.AlertHistory.[0].Entry.Category = AlertCategory.ContainerOomKilled
        && osAlerts.Count >= 1)

    vm.DismissAlertBanner()
    check "DismissAlertBanner clears banner while preserving AlertHistory" (
        not vm.HasActiveAlertBanner && vm.AlertHistory.Length >= 1)

    vm.StopMobileSession()
    check "StopMobileSession stops server cleanly" (not vm.IsMobileServerRunning)

    (vm :> IDisposable).Dispose()
    (srv :> IDisposable).Dispose()
    printfn "\nAll %d v1.2 alerts, FAQ, QR, and mobile session checks passed!" checks
finally
    ComposeRepository.CustomDatabasePath <- None
    SqliteConnection.ClearAllPools()
    try if File.Exists(testDbPath) then File.Delete(testDbPath) with _ -> ()
