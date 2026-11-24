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
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.ViewModels

let mutable checks = 0
let check name condition =
    if not condition then failwith (sprintf "FAILED: %s" name)
    checks <- checks + 1
    printfn "PASS: %s" name

// 1. 多言語辞書キーが全11言語で揃っているかの確認
let requiredKeys = [
    "サービス診断"
    "コンテナ内サービス診断"
    "診断を実行"
    "再診断"
    "診断中..."
    "目立つ異常は見つかりませんでした"
    "走査ログおよびコンテナ状態に、既知のエラーパターンや再起動などの異常は記録されていません。"
    "走査ログ: 直近 {0} 行"
    "対象サービス: {0}"
    "汎用チェックのみ"
    "ログとコンテナ状態に基づく診断です。実際の設定や動作と異なる場合があります"
    "根拠:"
    "該当: {0}件"
    "所見一覧"
    "危険"
    "警告"
    "情報"
    "コンテナ再起動の繰り返し"
    "短時間に複数回の再起動が検知されました。起動直後に異常終了している可能性があります。"
    "コンテナ再起動を検知"
    "コンテナの再起動が記録されています。"
    "メモリ不足による強制終了 (OOMKilled)"
    "コンテナがメモリ上限を超過したため、OSにより強制終了されました。"
    "Upstream接続エラー"
    "バックエンドサーバーへの接続失敗、タイムアウト、または利用不可が記録されています。"
    "サーバーエラー (5xx)"
    "HTTP 5xx のサーバー内部エラーまたはゲートウェイエラーがログに記録されています。"
    "ワーカープロセスの異常終了"
    "サーバーのワーカープロセスがシグナル等により異常終了しました。"
    "データベース起動・実行エラー"
    "データベースの起動失敗または重大な実行時エラーが記録されています。"
    "接続数上限到達"
    "クライアントの接続数が上限に達し、新しい接続が拒否されています。"
    "InnoDBストレージエンジンエラー"
    "InnoDBのデータ破損、I/Oエラー、またはテーブル破損が記録されています。"
    "接続拒否・接続数上限"
    "接続スロットの上限到達、または認証失敗が発生しています。"
    "チェックポイント障害・容量不足"
    "ディスク容量不足またはチェックポイントの破損等の重大障害が記録されています。"
    "サーバープロセスの異常終了"
    "データベースのサーバープロセスがシグナル等により異常終了しました。"
    "永続化保存エラー (RDB/AOF)"
    "スナップショット (RDB) または AOF ファイルのディスク保存に失敗しています。"
    "メモリ上限到達 (OOM)"
    "設定された最大メモリ上限 (maxmemory) に到達し、書き込みが制限されている可能性があります。"
    "ログ内のエラー・例外"
    "ログ内にエラーまたは例外を示すメッセージが記録されています。"
]

for key in requiredKeys do
    check (sprintf "Localization has all 11 translations for %s" key) (Localization.hasAllTranslations key)

// 2. inspect JSON パース検証
let sampleInspectJson = """
[
  {
    "Id": "c1234567890abcdef",
    "Name": "/test-container",
    "RestartCount": 4,
    "State": {
      "Status": "restarting",
      "Running": false,
      "Restarting": true,
      "OOMKilled": true,
      "ExitCode": 137
    }
  }
]
"""
let parsedInspectOpt = ServiceDiagnostics.parseInspectJson sampleInspectJson
check "parseInspectJson succeeds" parsedInspectOpt.IsSome
let parsedInspect = parsedInspectOpt.Value
check "parseInspectJson Id" (parsedInspect.Id = "c1234567890abcdef")
check "parseInspectJson Name" (parsedInspect.Name = "test-container")
check "parseInspectJson RestartCount" (parsedInspect.RestartCount = 4)
check "parseInspectJson State OOMKilled" (parsedInspect.State.Value.OOMKilled = true)
check "parseInspectJson State ExitCode" (parsedInspect.State.Value.ExitCode = 137)

// 3. レベル1チェック（Inspect チェック: OOMKilled と再起動頻発）
let inspectResult = ServiceDiagnostics.diagnose (Some parsedInspect) [] []
check "inspect findings has 2 items (OOM and restart)" (inspectResult.Findings.Length = 2)
check "first finding is OOMKilled (Danger)" (
    inspectResult.Findings.[0].Severity = FindingSeverity.Danger
    && inspectResult.Findings.[0].TitleKey.Contains("OOMKilled")
)
check "second finding is restart loop (Danger)" (
    inspectResult.Findings.[1].Severity = FindingSeverity.Danger
    && inspectResult.Findings.[1].TitleKey.Contains("コンテナ再起動の繰り返し")
)

// 単発再起動（Warning）の検証
let singleRestartInspect: ContainerInspectData = {
    Id = "c1"
    Name = "stable-app"
    RestartCount = 1
    State = Some {
        Status = "running"
        Running = true
        Restarting = false
        OOMKilled = false
        ExitCode = 0
    }
}
let singleRestartResult = ServiceDiagnostics.diagnose (Some singleRestartInspect) [] []
check "single restart has 1 finding" (singleRestartResult.Findings.Length = 1)
check "single restart finding is Warning" (singleRestartResult.Findings.[0].Severity = FindingSeverity.Warning)

// 正常な Inspect（所見0件）の検証
let healthyInspect: ContainerInspectData = {
    Id = "c2"
    Name = "healthy-app"
    RestartCount = 0
    State = Some {
        Status = "running"
        Running = true
        Restarting = false
        OOMKilled = false
        ExitCode = 0
    }
}
let healthyInspectResult = ServiceDiagnostics.diagnose (Some healthyInspect) [] []
check "healthy inspect has 0 findings (silence principle)" (List.isEmpty healthyInspectResult.Findings)

// 4. レベル2: Nginx / Apache 辞書チェック
let nginxErrorLogs = [
    "2026/11/18 10:00:00 [notice] 1#1: using the \"epoll\" event method"
    "2026/11/18 10:00:00 [notice] 1#1: nginx/1.25.3"
    "2026/11/18 10:01:05 [error] 12#12: *1 connect() failed (111: Connection refused) while connecting to upstream, client: 172.18.0.1, server: localhost, request: \"GET /api HTTP/1.1\", upstream: \"http://127.0.0.1:8000/api\""
    "2026/11/18 10:01:06 [error] 12#12: *2 connect() failed (111: Connection refused) while connecting to upstream, client: 172.18.0.1, server: localhost, request: \"GET /api HTTP/1.1\", upstream: \"http://127.0.0.1:8000/api\""
    "172.18.0.1 - - [18/Nov/2026:10:01:06 +0000] \"GET /api HTTP/1.1\" 502 157 \"-\" \"curl/8.5.0\""
    "2026/11/18 10:05:00 [alert] 1#1: worker process 12 exited on signal 9"
]
let nginxResult = ServiceDiagnostics.diagnose (Some healthyInspect) [ ServiceId.WebServer ] nginxErrorLogs
check "nginx diagnosis found findings" (nginxResult.Findings.Length = 3)
check "nginx upstream error finding exists" (
    nginxResult.Findings |> List.exists (fun f -> f.TitleKey = "Upstream接続エラー" && f.Count = 2)
)
check "nginx 5xx finding exists" (
    nginxResult.Findings |> List.exists (fun f -> f.TitleKey = "サーバーエラー (5xx)" && f.Count = 1)
)
check "nginx worker died finding is Danger" (
    nginxResult.Findings |> List.exists (fun f -> f.TitleKey = "ワーカープロセスの異常終了" && f.Severity = FindingSeverity.Danger)
)
// 根拠行の確認
let upstreamFinding = nginxResult.Findings |> List.find (fun f -> f.TitleKey = "Upstream接続エラー")
check "upstream finding has 2 evidence lines" (upstreamFinding.EvidenceLines.Length = 2)
check "upstream evidence line contains connect() failed" (upstreamFinding.EvidenceLines.[0].Contains("connect() failed"))

// 5. Nginx 正常ログ（沈黙の確認）
let nginxNormalLogs = [
    "2026/11/18 10:00:00 [notice] 1#1: using the \"epoll\" event method"
    "2026/11/18 10:00:00 [notice] 1#1: start worker processes"
    "172.18.0.1 - - [18/Nov/2026:10:01:00 +0000] \"GET / HTTP/1.1\" 200 615 \"-\" \"curl/8.5.0\""
    "172.18.0.1 - - [18/Nov/2026:10:01:01 +0000] \"GET /index.html HTTP/1.1\" 304 0 \"-\" \"Mozilla/5.0\""
]
let nginxNormalResult = ServiceDiagnostics.diagnose (Some healthyInspect) [ ServiceId.WebServer ] nginxNormalLogs
check "nginx normal logs gives 0 findings" (List.isEmpty nginxNormalResult.Findings)

// 6. レベル2: MySQL / MariaDB 辞書チェック
let mysqlErrorLogs = [
    "2026-11-18T10:00:00.123456Z 0 [System] [MY-010116] [Server] /usr/sbin/mysqld: ready for connections."
    "2026-11-18T10:01:00.000000Z 12 [ERROR] [MY-010020] [Server] Too many connections"
    "2026-11-18T10:01:01.000000Z 13 [ERROR] [MY-010020] [Server] Too many connections"
    "2026-11-18T10:02:00.000000Z 0 [ERROR] [MY-012574] [InnoDB] InnoDB: Database page corruption on disk or a failed read of file block 42."
]
let mysqlResult = ServiceDiagnostics.diagnose (Some healthyInspect) [ ServiceId.Mysql ] mysqlErrorLogs
check "mysql diagnosis found findings" (mysqlResult.Findings.Length >= 2)
check "mysql too many connections finding exists" (
    mysqlResult.Findings |> List.exists (fun f -> f.TitleKey = "接続数上限到達" && f.Count = 2)
)
check "mysql innodb corruption finding is Danger" (
    mysqlResult.Findings |> List.exists (fun f -> f.TitleKey = "InnoDBストレージエンジンエラー" && f.Severity = FindingSeverity.Danger)
)

// 7. レベル2: PostgreSQL 辞書チェック
let postgresErrorLogs = [
    "2026-11-18 10:00:00.000 UTC [1] LOG:  starting PostgreSQL 16.1"
    "2026-11-18 10:01:00.000 UTC [100] FATAL:  sorry, too many clients already"
    "2026-11-18 10:02:00.000 UTC [1] PANIC:  could not locate a valid checkpoint record"
    "2026-11-18 10:02:01.000 UTC [1] LOG:  server process (PID 100) was terminated by signal 9: Killed"
]
let postgresResult = ServiceDiagnostics.diagnose (Some healthyInspect) [ ServiceId.Postgres ] postgresErrorLogs
check "postgres diagnosis found findings" (postgresResult.Findings.Length = 3)
check "postgres connection limit finding exists" (
    postgresResult.Findings |> List.exists (fun f -> f.TitleKey = "接続拒否・接続数上限")
)
check "postgres checkpoint panic finding exists" (
    postgresResult.Findings |> List.exists (fun f -> f.TitleKey = "チェックポイント障害・容量不足" && f.Severity = FindingSeverity.Danger)
)
check "postgres process terminated finding exists" (
    postgresResult.Findings |> List.exists (fun f -> f.TitleKey = "サーバープロセスの異常終了" && f.Severity = FindingSeverity.Danger)
)

// 8. レベル2: Redis 辞書チェック
let redisErrorLogs = [
    "1:M 18 Nov 2026 10:00:00.000 * Ready to accept connections tcp"
    "1:M 18 Nov 2026 10:01:00.000 # Failed opening the RDB file dump.rdb (in server root dir /data) for saving: Permission denied"
    "1:M 18 Nov 2026 10:01:00.000 # Background saving error"
    "1:M 18 Nov 2026 10:02:00.000 # OOM command not allowed when used memory > 'maxmemory'"
]
let redisResult = ServiceDiagnostics.diagnose (Some healthyInspect) [ ServiceId.Redis ] redisErrorLogs
check "redis diagnosis found 2 findings" (redisResult.Findings.Length = 2)
check "redis rdb persistence error is Danger" (
    redisResult.Findings |> List.exists (fun f -> f.TitleKey = "永続化保存エラー (RDB/AOF)" && f.Severity = FindingSeverity.Danger)
)
check "redis oom warning exists" (
    redisResult.Findings |> List.exists (fun f -> f.TitleKey = "メモリ上限到達 (OOM)" && f.Severity = FindingSeverity.Warning)
)

// 9. 汎用フォールバック（辞書にない未知サービスのログ）
let unknownServiceLogs = [
    "2026-11-18 10:00:00 [info] Application started successfully."
    "2026-11-18 10:01:00 [error] Unhandled Exception: System.NullReferenceException: Object reference not set to an instance of an object."
    "2026-11-18 10:01:05 [info] 0 errors during health check" // 偽陽性除外の確認
]
let unknownResult = ServiceDiagnostics.diagnose (Some healthyInspect) [] unknownServiceLogs
check "unknown service generic error detected" (unknownResult.Findings.Length = 1)
check "unknown service finding is generic error" (unknownResult.Findings.[0].TitleKey = "ログ内のエラー・例外")
check "unknown service finding count is 1 (0 errors line excluded)" (unknownResult.Findings.[0].Count = 1)

// 10. 根拠抜粋上限（最大3行）の検証
let manyErrorsLogs = [
    for i in 1 .. 10 -> sprintf "2026-11-18 10:00:%02d [error] Error: failed operation #%d" i i
]
let manyErrorsResult = ServiceDiagnostics.diagnose (Some healthyInspect) [] manyErrorsLogs
check "many errors count is 10" (manyErrorsResult.Findings.[0].Count = 10)
check "many errors evidence lines capped at 3" (manyErrorsResult.Findings.[0].EvidenceLines.Length = 3)
check "many errors promoted to Danger due to frequency (>= 10)" (manyErrorsResult.Findings.[0].Severity = FindingSeverity.Danger)

// 11. 巨大な1行（改行なしロングテキスト）の切り捨て検証
let hugeLine = "2026-11-18 10:00:00 [error] " + String.replicate 5000 "A"
let truncated = ServiceDiagnostics.truncateLine hugeLine
check "huge line truncated to <= 2048 chars" (truncated.Length <= 2048)

// 12. 大量ログ走査（10,000行）の高速性・ストリーミング処理確認
let stopwatch = Diagnostics.Stopwatch.StartNew()
let massiveLogs = seq {
    for i in 1 .. 10000 do
        if i = 500 then yield "2026/11/18 10:00:00 [error] 12#12: connect() failed while connecting to upstream"
        else yield sprintf "172.18.0.1 - - [18/Nov/2026:10:00:%02d +0000] \"GET /item/%d HTTP/1.1\" 200 1234" (i % 60) i
}
let massiveResult = ServiceDiagnostics.diagnose (Some healthyInspect) [ ServiceId.WebServer ] massiveLogs
stopwatch.Stop()
printfn "Scanned %d lines in %d ms" massiveResult.ScannedLines stopwatch.ElapsedMilliseconds
check "massive logs scanned count is 10000" (massiveResult.ScannedLines = 10000)
check "massive logs found upstream error" (massiveResult.Findings.Length = 1)
check "massive logs completed fast (< 500 ms)" (stopwatch.ElapsedMilliseconds < 500L)

// 13. ViewModel との連携および UI 表示検証
let vm = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false)
check "initially diagnostics dialog closed" (not vm.IsDiagnosticsDialogOpen)
check "initially has no diagnostics result" (not vm.HasDiagnosticsResult)
check "initially not diagnosing" (not vm.IsDiagnosing)

let testContainer = ContainerInfo("c-test", "kuma-nginx", "Up 3 hours", "nginx:alpine", "today",
                                  initialPorts = [ ContainerPortBinding("0.0.0.0", "8080", "80/tcp") ])
vm.Containers.Add(testContainer)
vm.SelectedContainer <- testContainer

// ダイアログ開閉
check "can open diagnostics dialog" (
    vm.OpenDiagnosticsDialog()
    vm.IsDiagnosticsDialogOpen
)
vm.CloseDiagnosticsDialog()
check "dialog closed successfully" (not vm.IsDiagnosticsDialogOpen)

// 言語切り替えによるダイアログタイトルの更新確認
let enOption = vm.Languages |> Array.find (fun opt -> opt.Key = "en-US")
vm.Language <- enOption
check "switching language updates DiagnosticsDialogTitle to English" (
    vm.DiagnosticsDialogTitle = "In-container service diagnostics"
    && vm.DiagnosticsButtonLabel = "Service diagnostics"
    && vm.RerunDiagnosticsLabel = "Re-diagnose"
    && vm.DiagnosticsNoAnomalyTitle = "No noticeable anomalies were found"
)

let jaOption = vm.Languages |> Array.find (fun opt -> opt.Key = "ja-JP")
vm.Language <- jaOption
check "switching language restores Japanese strings" (
    vm.DiagnosticsDialogTitle = "コンテナ内サービス診断"
    && vm.DiagnosticsButtonLabel = "サービス診断"
    && vm.RerunDiagnosticsLabel = "再診断"
    && vm.DiagnosticsNoAnomalyTitle = "目立つ異常は見つかりませんでした"
)

// コンテナ選択解除でダイアログが閉じること
vm.OpenDiagnosticsDialog()
check "dialog opened before deselect" vm.IsDiagnosticsDialogOpen
vm.SelectedContainer <- null
check "deselecting container closes diagnostics dialog" (not vm.IsDiagnosticsDialogOpen)

// 14. 所見カード（FindingDisplayItem）の多言語化テスト（英語・中国語等で正しく翻訳されること）
let restartFinding = inspectResult.Findings |> List.find (fun f -> f.TitleKey = "コンテナ再起動の繰り返し")

let enItem = FindingDisplayItem(restartFinding, "en-US")
check "en-US item Title is English" (enItem.Title = "Repeated container restarts")
check "en-US item Summary is English" (enItem.Summary = "Multiple restarts detected in a short time. The container may be crashing right after startup.")
check "en-US item CountText is English" (enItem.CountText.StartsWith("Occurrences: "))
check "en-US item EvidenceLabel is English" (enItem.EvidenceLabel = "Evidence:")

let jaItem = FindingDisplayItem(restartFinding, "ja-JP")
check "ja-JP item Title is Japanese" (jaItem.Title = "コンテナ再起動の繰り返し")
check "ja-JP item Summary is Japanese" (jaItem.Summary = "短時間に複数回の再起動が検知されました。起動直後に異常終了している可能性があります。")
check "ja-JP item CountText is Japanese" (jaItem.CountText.StartsWith("該当: "))
check "ja-JP item EvidenceLabel is Japanese" (jaItem.EvidenceLabel = "根拠:")

let zhItem = FindingDisplayItem(restartFinding, "zh-CN")
check "zh-CN item Title is Chinese" (zhItem.Title = "容器反复重启")
check "zh-CN item Summary is Chinese" (zhItem.Summary = "短时间内检测到多次重启。容器可能在启动后立即崩溃。")

(vm :> IDisposable).Dispose()

printfn "\nAll %d service diagnostics checks passed successfully!" checks
