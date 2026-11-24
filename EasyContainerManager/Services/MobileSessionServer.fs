namespace EasyContainerManager.Services

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open EasyContainerManager
open EasyContainerManager.Models

/// <summary>検出されたローカル IPv4 アダプタ候補。</summary>
type LanIpCandidate =
    { Label: string
      Address: string
      Score: int }

/// <summary>ローカル IPv4 アダプタ検出と優先度ソートを行うモジュール。</summary>
module NetworkHelpers =

    let private isPrivateLanIp (addr: IPAddress) : bool =
        let b = addr.GetAddressBytes()
        if b.Length <> 4 then false
        else
            (b.[0] = 192uy && b.[1] = 168uy)
            || b.[0] = 10uy
            || (b.[0] = 172uy && b.[1] >= 16uy && b.[1] <= 31uy)

    let private isVirtualAdapterName (name: string) (desc: string) : bool =
        let combined = ((if isNull name then "" else name) + " " + (if isNull desc then "" else desc)).ToLowerInvariant()
        let keywords =
            [| "vethernet"; "wsl"; "hyper-v"; "vmware"; "virtualbox"; "loopback"
               "teredo"; "tailscale"; "zerotier"; "hamachi"; "docker"; "virtual"
               "wireguard"; "npcap"; "default switch"; "bluetooth" |]
        keywords |> Array.exists (fun k -> combined.Contains(k))

    /// <summary>物理 Wi-Fi / LAN アダプタを優先して IPv4 候補一覧を返す（末尾に 127.0.0.1 を追加）。</summary>
    let detectLocalIpCandidates () : LanIpCandidate list =
        let candidates = ResizeArray<LanIpCandidate>()
        try
            for ni in NetworkInterface.GetAllNetworkInterfaces() do
                if ni.OperationalStatus = OperationalStatus.Up
                   && ni.NetworkInterfaceType <> NetworkInterfaceType.Loopback
                   && ni.NetworkInterfaceType <> NetworkInterfaceType.Tunnel then
                    let props = ni.GetIPProperties()
                    let hasGateway =
                        props.GatewayAddresses
                        |> Seq.exists (fun g ->
                            g.Address.AddressFamily = AddressFamily.InterNetwork
                            && not (IPAddress.Any.Equals(g.Address))
                            && not (IPAddress.IsLoopback(g.Address)))
                    let isVirt = isVirtualAdapterName ni.Name ni.Description
                    for ua in props.UnicastAddresses do
                        if ua.Address.AddressFamily = AddressFamily.InterNetwork
                           && not (IPAddress.IsLoopback(ua.Address)) then
                            let ipStr = ua.Address.ToString()
                            if not (ipStr.StartsWith("169.254.", StringComparison.Ordinal)) then
                                let mutable score = 0
                                if isVirt then
                                    score <- -1000
                                else
                                    score <- 1000
                                    if hasGateway then score <- score + 500
                                    if ni.NetworkInterfaceType = NetworkInterfaceType.Wireless80211 then score <- score + 300
                                    elif ni.NetworkInterfaceType = NetworkInterfaceType.Ethernet then score <- score + 200
                                if isPrivateLanIp ua.Address then score <- score + 100
                                candidates.Add({ Label = sprintf "%s (%s)" ni.Name ipStr; Address = ipStr; Score = score })
        with _ -> ()

        let sorted =
            candidates
            |> Seq.sortByDescending (fun c -> c.Score)
            |> Seq.distinctBy (fun c -> c.Address)
            |> Seq.toList

        sorted @ [ { Label = "Localhost (127.0.0.1)"; Address = "127.0.0.1"; Score = -2000 } ]

/// <summary>スマホセッション 1 対 1 排他ロックとハートビート・タイムアウト判定を行う純粋認証モジュール。</summary>
module MobileSessionAuth =

    type SessionState = {
        IsActive: bool
        QrToken: string
        SessionToken: string
        LockedClientIp: string option
        LastActivityUtc: DateTimeOffset
        IdleTimeout: TimeSpan
    }

    type AccessDeniedReason =
        | SessionNotActive
        | SessionExpired
        | InvalidToken
        | LockedToAnotherDevice

    type AccessDecision =
        | Allowed of updatedState: SessionState * shouldSetCookie: bool
        | Denied of reason: AccessDeniedReason

    let newRandomHexToken (byteLen: int) : string =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(max 4 byteLen)).ToLowerInvariant()

    let fixedTimeEquals (a: string) (b: string) : bool =
        if String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b) then false
        else
            let bytesA = Encoding.UTF8.GetBytes(a)
            let bytesB = Encoding.UTF8.GetBytes(b)
            bytesA.Length = bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB)

    let createInactiveState () : SessionState =
        {
            IsActive = false
            QrToken = ""
            SessionToken = ""
            LockedClientIp = None
            LastActivityUtc = DateTimeOffset.MinValue
            IdleTimeout = TimeSpan.FromMinutes(15.0)
        }

    let createActiveState (nowUtc: DateTimeOffset) (idleTimeout: TimeSpan) : SessionState =
        {
            IsActive = true
            QrToken = newRandomHexToken 8
            SessionToken = newRandomHexToken 12
            LockedClientIp = None
            LastActivityUtc = nowUtc
            IdleTimeout = idleTimeout
        }

    let isExpired (nowUtc: DateTimeOffset) (state: SessionState) : bool =
        state.IsActive && (nowUtc - state.LastActivityUtc) > state.IdleTimeout

    let remainingSeconds (nowUtc: DateTimeOffset) (state: SessionState) : int =
        if not state.IsActive then 0
        else
            let rem = state.IdleTimeout - (nowUtc - state.LastActivityUtc)
            max 0 (int (Math.Ceiling(rem.TotalSeconds)))

    /// <summary>リクエスト元 IP・QR トークン・セッショントークンを検証し、1対1ロックを判定・更新する。</summary>
    let evaluateAccess
        (nowUtc: DateTimeOffset)
        (remoteIp: string)
        (providedQrToken: string option)
        (providedSessionToken: string option)
        (state: SessionState)
        : AccessDecision =
        if not state.IsActive then
            Denied SessionNotActive
        elif isExpired nowUtc state then
            Denied SessionExpired
        else
            let normIp = if String.IsNullOrWhiteSpace(remoteIp) then "127.0.0.1" else remoteIp.Trim()
            let qrMatches =
                match providedQrToken with
                | Some t -> fixedTimeEquals (t.Trim()) state.QrToken
                | None -> false
            let sidMatches =
                match providedSessionToken with
                | Some s -> fixedTimeEquals (s.Trim()) state.SessionToken
                | None -> false

            match state.LockedClientIp with
            | None ->
                if qrMatches then
                    let next = { state with LockedClientIp = Some normIp; LastActivityUtc = nowUtc }
                    Allowed (next, true)
                elif sidMatches then
                    let next = { state with LockedClientIp = Some normIp; LastActivityUtc = nowUtc }
                    Allowed (next, false)
                else
                    Denied InvalidToken
            | Some lockedIp ->
                let sameIp = String.Equals(lockedIp, normIp, StringComparison.OrdinalIgnoreCase)
                if not sameIp then
                    Denied LockedToAnotherDevice
                elif sidMatches then
                    Allowed ({ state with LastActivityUtc = nowUtc }, false)
                elif qrMatches then
                    Allowed ({ state with LastActivityUtc = nowUtc }, true)
                else
                    Denied InvalidToken

    /// <summary>接続中スマホからのハートビートで無通信タイマーを延長する。</summary>
    let recordHeartbeat (nowUtc: DateTimeOffset) (remoteIp: string) (providedQrToken: string option) (providedSessionToken: string option) (state: SessionState) : AccessDecision =
        evaluateAccess nowUtc remoteIp providedQrToken providedSessionToken state

/// <summary>スマホ閲覧用の読み取り専用コンテナ情報 DTO。</summary>
type MobileContainerDto = {
    Id: string
    Name: string
    Status: string
    StateKey: string
    Image: string
    Ports: string
    ResourceSummary: string
    MountsSummary: string
    HasDangerDiagnostics: bool
    HasWarningDiagnostics: bool
    DiagnosticsNote: string
}

/// <summary>スマホ閲覧用の読み取り専用ボリューム情報 DTO。</summary>
type MobileVolumeDto = {
    Name: string
    DisplayName: string
    Driver: string
    SizeText: string
    SizeBytes: int64 option
    SizeLevel: int
    IsAnonymous: bool
    IsUnused: bool
    AttachedSummary: string
}

/// <summary>スマホ閲覧用のアラート履歴 DTO。</summary>
type MobileAlertDto = {
    Id: int64
    OccurredAt: string
    OccurredAtLocal: string
    Severity: string
    Category: string
    TargetName: string
    Title: string
    Message: string
}

/// <summary>PC 側ダッシュボードからスマホサーバーへ渡す最新読み取り専用スナップショット。</summary>
type MobileDashboardSnapshot = {
    UpdatedAtUtc: string
    LanguageCode: string
    EngineMode: string
    EngineRunning: bool
    Containers: MobileContainerDto list
    Volumes: MobileVolumeDto list
    TotalVolumeBytes: int64
    UnusedVolumeCount: int
    UnusedVolumeBytes: int64
    Alerts: MobileAlertDto list
} with
    static member Empty =
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            LanguageCode = "ja-JP"
            EngineMode = "Docker"
            EngineRunning = false
            Containers = []
            Volumes = []
            TotalVolumeBytes = 0L
            UnusedVolumeCount = 0
            UnusedVolumeBytes = 0L
            Alerts = []
        }

/// <summary>PC UI へ通知するスマホセッション状態。</summary>
type MobileSessionStatus = {
    IsRunning: bool
    Port: int
    QrToken: string
    LockedClientIp: string option
    TimedOut: bool
}

/// <summary>
/// スマホと 1 対 1 で接続する読み取り専用のローカル Wi-Fi HTTP/SSE サーバー。
/// ユーザーのボタン操作時のみ起動し、無通信タイムアウトで自動終了します。
/// </summary>
type MobileSessionServer() =

    let syncLock = obj ()
    let mutable listener: TcpListener option = None
    let mutable cts: CancellationTokenSource option = None
    let mutable acceptTask: Task option = None
    let mutable watchdogTask: Task option = None
    let mutable activePort = 0
    let mutable authState = MobileSessionAuth.createInactiveState ()
    let mutable timedOutFlag = false
    let mutable snapshot = MobileDashboardSnapshot.Empty
    let sseClients = ConcurrentDictionary<Guid, Stream * SemaphoreSlim>()
    let sessionStateChanged = Event<MobileSessionStatus>()

    let enc (s: string) =
        if isNull s then ""
        else
            s.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&#39;")

    let isBrowserRestrictedPort (p: int) =
        p = 5060 || p = 5061 || p = 6000 || p = 6566 || (p >= 6665 && p <= 6669) || p = 10080

    let statusSnapshot () : MobileSessionStatus =
        lock syncLock (fun () ->
            {
                IsRunning = listener.IsSome && authState.IsActive
                Port = activePort
                QrToken = if authState.IsActive then authState.QrToken else ""
                LockedClientIp = if authState.IsActive then authState.LockedClientIp else None
                TimedOut = timedOutFlag
            })

    let notifyStatusChanged () =
        let st = statusSnapshot ()
        try sessionStateChanged.Trigger(st) with _ -> ()

    let closeAllSseClients () =
        for kv in sseClients do
            let mutable removed = Unchecked.defaultof<Stream * SemaphoreSlim>
            if sseClients.TryRemove(kv.Key, &removed) then
                let (st, _) = removed
                try st.Dispose() with _ -> ()

    let parseQueryParams (rawTarget: string) : Dictionary<string, string> =
        let dict = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let qIdx = rawTarget.IndexOf('?')
        if qIdx >= 0 && qIdx < rawTarget.Length - 1 then
            let q = rawTarget.Substring(qIdx + 1)
            for part in q.Split('&', StringSplitOptions.RemoveEmptyEntries) do
                let kv = part.Split('=', 2)
                if kv.Length = 2 then
                    dict.[Uri.UnescapeDataString(kv.[0])] <- Uri.UnescapeDataString(kv.[1])
                elif kv.Length = 1 then
                    dict.[Uri.UnescapeDataString(kv.[0])] <- ""
        dict

    let tryGetCookie (headerLines: string array) (cookieName: string) : string option =
        headerLines
        |> Array.tryPick (fun line ->
            if line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) then
                let raw = line.Substring("Cookie:".Length)
                raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryPick (fun seg ->
                    let kv = seg.Trim().Split('=', 2)
                    if kv.Length = 2 && kv.[0].Trim().Equals(cookieName, StringComparison.OrdinalIgnoreCase) then
                        Some(kv.[1].Trim())
                    else None)
            else None)

    let tryGetHeader (headerLines: string array) (headerName: string) : string option =
        let prefix = headerName + ":"
        headerLines
        |> Array.tryPick (fun line ->
            if line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                Some(line.Substring(prefix.Length).Trim())
            else None)

    let writeHttpResponse (stream: Stream) (statusCode: int) (statusText: string) (contentType: string) (extraHeaders: string list) (body: byte[]) =
        let extra =
            if List.isEmpty extraHeaders then ""
            else (String.concat "\r\n" extraHeaders) + "\r\n"
        let header =
            sprintf "HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nCache-Control: no-store\r\n%sConnection: close\r\n\r\n"
                statusCode statusText contentType body.Length extra
        let headerBytes = Encoding.ASCII.GetBytes(header)
        stream.Write(headerBytes, 0, headerBytes.Length)
        if body.Length > 0 then
            stream.Write(body, 0, body.Length)
        stream.Flush()

    let findHeaderEndIndex (buf: byte[]) (len: int) : int =
        let mutable found = -1
        let mutable i = 0
        while i <= len - 4 && found < 0 do
            if buf.[i] = 13uy && buf.[i + 1] = 10uy && buf.[i + 2] = 13uy && buf.[i + 3] = 10uy then
                found <- i
            i <- i + 1
        found

    let resolveLang (query: Dictionary<string, string>) (cookieLang: string option) (defaultLang: string) : string * bool =
        let hasQ, qLang = query.TryGetValue("lang")
        if hasQ && not (String.IsNullOrWhiteSpace(qLang)) then
            (FaqData.resolveFaqLanguage qLang, true)
        else
            match cookieLang with
            | Some c when not (String.IsNullOrWhiteSpace(c)) -> (FaqData.resolveFaqLanguage c, false)
            | _ -> (FaqData.resolveFaqLanguage defaultLang, false)

    let serializeAlertDto (dto: MobileAlertDto) : string =
        JsonSerializer.Serialize(
            {| id = dto.Id
               occurredAt = dto.OccurredAt
               occurredAtLocal = dto.OccurredAtLocal
               severity = dto.Severity
               category = dto.Category
               targetName = dto.TargetName
               title = dto.Title
               message = dto.Message |})

    let buildSnapshotJson (nowUtc: DateTimeOffset) (lang: string) : string =
        let snap, stateCopy =
            lock syncLock (fun () -> snapshot, authState)
        let remSec = MobileSessionAuth.remainingSeconds nowUtc stateCopy
        let isJa = (FaqData.resolveFaqLanguage lang) = "ja-JP"
        let alertsMapped =
            snap.Alerts
            |> List.map (fun a ->
                {| id = a.Id
                   occurredAt = a.OccurredAt
                   occurredAtLocal = a.OccurredAtLocal
                   severity = a.Severity
                   category = a.Category
                   targetName = a.TargetName
                   title = a.Title
                   message = a.Message |})
        let containersMapped =
            snap.Containers
            |> List.map (fun c ->
                {| id = c.Id
                   name = c.Name
                   status = c.Status
                   stateKey = c.StateKey
                   image = c.Image
                   ports = c.Ports
                   resourceSummary = c.ResourceSummary
                   mountsSummary = c.MountsSummary
                   hasDangerDiagnostics = c.HasDangerDiagnostics
                   hasWarningDiagnostics = c.HasWarningDiagnostics
                   diagnosticsNote = c.DiagnosticsNote |})
        let volumesMapped =
            snap.Volumes
            |> List.map (fun v ->
                {| name = v.Name
                   displayName = v.DisplayName
                   driver = v.Driver
                   sizeText = v.SizeText
                   sizeBytes = (v.SizeBytes |> Option.toNullable)
                   sizeLevel = v.SizeLevel
                   isAnonymous = v.IsAnonymous
                   isUnused = v.IsUnused
                   attachedSummary = v.AttachedSummary |})
        JsonSerializer.Serialize(
            {| updatedAtUtc = snap.UpdatedAtUtc
               lang = (if isJa then "ja-JP" else "en-US")
               engineMode = snap.EngineMode
               engineRunning = snap.EngineRunning
               remainingSeconds = remSec
               totalVolumeText = VolumeParser.formatByteSize snap.TotalVolumeBytes
               unusedVolumeCount = snap.UnusedVolumeCount
               unusedVolumeText = VolumeParser.formatByteSize snap.UnusedVolumeBytes
               containers = containersMapped
               volumes = volumesMapped
               alerts = alertsMapped |})

    let buildErrorHtml (lang: string) (reason: MobileSessionAuth.AccessDeniedReason) : string =
        let isJa = (FaqData.resolveFaqLanguage lang) = "ja-JP"
        let title, body =
            match reason with
            | MobileSessionAuth.SessionNotActive ->
                if isJa then "セッション未開始", "PC 側で「スマホ連携 (QR)」セッションが開始されていません。"
                else "Session Not Active", "No Phone QR session is currently active on the PC."
            | MobileSessionAuth.SessionExpired ->
                if isJa then "セッション期限切れ", "無通信タイムアウトのためセッションが切断されました。PC 側で QR コードを再発行してください。"
                else "Session Expired", "Session disconnected due to inactivity timeout. Reissue the QR code on the PC."
            | MobileSessionAuth.LockedToAnotherDevice ->
                if isJa then "他の端末で接続中", "1 対 1 セッションのため、最初に QR を読み取ったスマートフォン以外の端末からはアクセスできません。"
                else "Locked to Another Device", "This 1-on-1 session is already locked to another smartphone."
            | MobileSessionAuth.InvalidToken ->
                if isJa then "認証エラー", "セッションの QR トークンが無効です。PC 画面の QR コードをもう一度読み取ってください。"
                else "Access Denied", "Invalid or missing QR session token. Scan the QR code on your PC again."
        sprintf """<!DOCTYPE html>
<html lang="%s">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>%s</title>
<style>
  body { margin:0; padding:24px 16px; background:#0F172A; color:#F8FAFC; font-family:-apple-system,BlinkMacSystemFont,"Yu Gothic UI",sans-serif; }
  .card { max-width:520px; margin:40px auto; background:#1E293B; border:1px solid #334155; border-radius:14px; padding:22px; }
  h1 { margin:0 0 10px 0; font-size:20px; color:#F87171; }
  p { margin:0; line-height:1.6; color:#CBD5E1; font-size:14.5px; }
</style>
</head>
<body>
  <div class="card">
    <h1>🔒 %s</h1>
    <p>%s</p>
  </div>
</body>
</html>""" (if isJa then "ja" else "en") (enc title) (enc title) (enc body)

    let buildMobilePageHtml (nowUtc: DateTimeOffset) (lang: string) (sessionTokenForJs: string) : string =
        let faqLang = FaqData.resolveFaqLanguage lang
        let isJa = faqLang = "ja-JP"
        let snapJson = buildSnapshotJson nowUtc faqLang
        let faqJson =
            let cats = FaqData.categoriesForLanguage faqLang
            JsonSerializer.Serialize(
                cats
                |> List.map (fun c ->
                    {| id = c.Id
                       icon = c.Icon
                       title = c.Title
                       items =
                           c.Items
                           |> List.map (fun i -> {| id = i.Id; q = i.Question; a = i.Answer |}) |}))

        let tTitle = if isJa then "簡単コンテナ管理 — モバイル閲覧" else "Easy Container Manager — Mobile View"
        let tReadOnlyBadge = if isJa then "🔒 1対1セッション・閲覧専用（操作不可）" else "🔒 1-on-1 Session · Read-Only"
        let tTabOverview = if isJa then "一覧・状態" else "Overview"
        let tTabVolumes = if isJa then "ボリューム" else "Volumes"
        let tTabAlerts = if isJa then "アラート" else "Alerts"
        let tTabFaq = if isJa then "初心者 FAQ" else "Beginner FAQ"
        let tFooter = if isJa then "同一 Wi-Fi 内・読み取り専用 / 操作や外部公開は行いません" else "Same Wi-Fi Read-Only · No remote control or public exposure"

        let labelsJson =
            JsonSerializer.Serialize(
                {| running = if isJa then "起動中" else "Running"
                   stopped = if isJa then "停止" else "Stopped"
                   restarting = if isJa then "再起動中" else "Restarting"
                   unknown = if isJa then "不明" else "Unknown"
                   containersTitle = if isJa then "コンテナ一覧" else "Containers"
                   volumesTitle = if isJa then "ボリューム一覧" else "Volumes"
                   alertsTitle = if isJa then "アラート履歴" else "Alert History"
                   faqTitle = if isJa then "初心者向け FAQ" else "Beginner FAQ"
                   noContainers = if isJa then "コンテナはありません。" else "No containers."
                   noVolumes = if isJa then "ボリュームはありません。" else "No volumes."
                   noAlerts = if isJa then "アラートはまだありません。" else "No alerts yet."
                   unused = if isJa then "未使用" else "Unused"
                   inUse = if isJa then "使用中" else "In use"
                   anon = if isJa then "匿名" else "Anonymous"
                   named = if isJa then "名前付き" else "Named"
                   unusedWarn = if isJa then "未使用で容量を消費" else "Unused space"
                   diagDanger = if isJa then "診断: 危険" else "Diag: Danger"
                   diagWarn = if isJa then "診断: 注意" else "Diag: Warning"
                   speak = if isJa then "🔊 読み上げ" else "🔊 Read"
                   stopSpeak = if isJa then "⏹ 停止" else "⏹ Stop"
                   ttsLocale = if isJa then "ja-JP" else "en-US"
                   engineLbl = if isJa then "エンジン" else "Engine"
                   totalContainersLbl = if isJa then "コンテナ" else "Containers"
                   totalVolumesLbl = if isJa then "ボリューム合計" else "Volumes Total"
                   unusedVolLbl = if isJa then "未使用ボリューム" else "Unused Volumes"
                   sessionRemLbl = if isJa then "残り" else "Left"
                   liveAlertPrefix = if isJa then "🚨 新着アラート: " else "🚨 Alert: "
                   dismiss = if isJa then "閉じる" else "Dismiss" |})

        $"""<!DOCTYPE html>
<html lang="{if isJa then "ja" else "en"}">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1"/>
<title>{enc tTitle}</title>
<style>
  :root {{ color-scheme: dark; }}
  * {{ box-sizing: border-box; }}
  body {{ margin:0; padding:12px 12px 48px; background:#0F172A; color:#F1F5F9; font-family:-apple-system,BlinkMacSystemFont,"Hiragino Sans","Yu Gothic UI","Segoe UI",sans-serif; line-height:1.5; }}
  .wrap {{ max-width:680px; margin:0 auto; }}
  .topbar {{ display:flex; justify-content:space-between; align-items:center; gap:8px; margin-bottom:10px; flex-wrap:wrap; }}
  .brand {{ font-weight:800; font-size:15px; color:#38BDF8; }}
  .ro-badge {{ display:inline-block; background:#064E3B; color:#6EE7B7; border:1px solid #10B981; border-radius:999px; padding:3px 10px; font-size:11.5px; font-weight:700; }}
  .lang-sel {{ background:#1E293B; color:#E2E8F0; border:1px solid #475569; border-radius:8px; padding:4px 8px; font-size:12px; font-weight:700; }}
  .alert-banner {{ display:none; background:#450A0A; border:2px solid #EF4444; border-radius:12px; padding:12px; margin-bottom:12px; }}
  .alert-banner.warn {{ background:#451A03; border-color:#F59E0B; }}
  .alert-head {{ display:flex; justify-content:space-between; align-items:center; gap:8px; font-weight:800; font-size:14px; }}
  .alert-msg {{ font-size:13px; color:#FECACA; margin-top:4px; white-space:pre-wrap; }}
  .alert-banner.warn .alert-msg {{ color:#FDE68A; }}
  .summary-grid {{ display:grid; grid-template-columns:repeat(2, minmax(0,1fr)); gap:8px; margin-bottom:12px; }}
  .stat-card {{ background:#1E293B; border:1px solid #334155; border-radius:12px; padding:10px 12px; }}
  .stat-lbl {{ font-size:11.5px; color:#94A3B8; font-weight:600; }}
  .stat-val {{ font-size:16px; font-weight:800; margin-top:2px; color:#F8FAFC; }}
  .tabs {{ display:flex; gap:6px; overflow-x:auto; margin-bottom:12px; padding-bottom:2px; }}
  .tab-btn {{ flex:1; min-width:80px; background:#1E293B; color:#CBD5E1; border:1px solid #334155; border-radius:10px; padding:9px 8px; font-size:13px; font-weight:700; cursor:pointer; white-space:nowrap; }}
  .tab-btn.active {{ background:#0284C7; color:#FFFFFF; border-color:#38BDF8; }}
  .card {{ background:#1E293B; border:1px solid #334155; border-radius:12px; padding:12px 14px; margin-bottom:10px; }}
  .row-head {{ display:flex; justify-content:space-between; align-items:center; gap:8px; flex-wrap:wrap; }}
  .name {{ font-weight:750; font-size:15px; word-break:break-all; }}
  .pill {{ display:inline-block; padding:2px 8px; border-radius:999px; font-size:11.5px; font-weight:700; }}
  .pill-ok {{ background:#064E3B; color:#6EE7B7; }}
  .pill-warn {{ background:#451A03; color:#FCD34D; }}
  .pill-bad {{ background:#450A0A; color:#FCA5A5; }}
  .pill-muted {{ background:#334155; color:#CBD5E1; }}
  .meta {{ font-size:12.5px; color:#94A3B8; margin-top:4px; word-break:break-word; }}
  .tts-btn {{ background:#0F172A; color:#38BDF8; border:1px solid #0284C7; border-radius:999px; padding:4px 10px; font-size:12px; font-weight:700; cursor:pointer; }}
  .tts-btn.speaking {{ background:#0284C7; color:#FFFFFF; }}
  details.faq-item {{ background:#1E293B; border:1px solid #334155; border-radius:10px; padding:10px 12px; margin-bottom:8px; }}
  details.faq-item summary {{ cursor:pointer; font-weight:700; font-size:14px; color:#E2E8F0; }}
  .faq-ans {{ margin-top:8px; font-size:13.5px; color:#CBD5E1; white-space:pre-line; border-top:1px solid #334155; padding-top:8px; }}
  .footer {{ text-align:center; font-size:11.5px; color:#64748B; margin-top:18px; }}
</style>
</head>
<body>
<div class="wrap">
  <div class="topbar">
    <div>
      <div class="brand">🐧 {enc tTitle}</div>
      <span class="ro-badge">{enc tReadOnlyBadge}</span>
    </div>
    <select id="langSel" class="lang-sel" onchange="switchLang(this.value)">
      <option value="ja-JP" {if isJa then "selected" else ""}>日本語</option>
      <option value="en-US" {if not isJa then "selected" else ""}>English</option>
    </select>
  </div>

  <div id="liveAlertBanner" class="alert-banner">
    <div class="alert-head">
      <span id="liveAlertTitle"></span>
      <div style="display:flex; gap:6px;">
        <button type="button" id="btnSpeakAlert" class="tts-btn" onclick="speakCurrentAlert()">🔊</button>
        <button type="button" class="tts-btn" onclick="dismissBanner()">✕</button>
      </div>
    </div>
    <div id="liveAlertMsg" class="alert-msg"></div>
  </div>

  <div class="summary-grid" id="summaryGrid"></div>

  <div class="tabs">
    <button type="button" class="tab-btn active" onclick="showTab('containers', this)">🐳 {enc tTabOverview}</button>
    <button type="button" class="tab-btn" onclick="showTab('volumes', this)">📦 {enc tTabVolumes}</button>
    <button type="button" class="tab-btn" onclick="showTab('alerts', this)">🚨 {enc tTabAlerts}</button>
    <button type="button" class="tab-btn" onclick="showTab('faq', this)">❓ {enc tTabFaq}</button>
  </div>

  <div id="tab-containers"></div>
  <div id="tab-volumes" style="display:none;"></div>
  <div id="tab-alerts" style="display:none;"></div>
  <div id="tab-faq" style="display:none;"></div>

  <div class="footer">{enc tFooter}</div>
</div>

<script>
const SESSION_TOKEN = {JsonSerializer.Serialize(sessionTokenForJs)};
const L = {labelsJson};
const FAQ_DATA = {faqJson};
let SNAP = {snapJson};
let latestAlertText = '';
let activeSpeechBtn = null;

function esc(s) {{
  if (s === null || s === undefined) return '';
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}}

function switchLang(code) {{
  document.cookie = 'ecm_lang=' + encodeURIComponent(code) + '; Path=/; Max-Age=31536000; SameSite=Lax';
  const u = new URL(window.location.href);
  u.searchParams.set('lang', code);
  if (SESSION_TOKEN && !u.searchParams.has('s')) u.searchParams.set('s', SESSION_TOKEN);
  window.location.href = u.toString();
}}

function showTab(name, btn) {{
  ['containers','volumes','alerts','faq'].forEach(t => {{
    const el = document.getElementById('tab-' + t);
    if (el) el.style.display = (t === name) ? 'block' : 'none';
  }});
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
  if (btn) btn.classList.add('active');
}}

function stopTTS() {{
  if ('speechSynthesis' in window) window.speechSynthesis.cancel();
  if (activeSpeechBtn) {{
    activeSpeechBtn.classList.remove('speaking');
    activeSpeechBtn = null;
  }}
}}

function speakText(text, btn) {{
  if (!('speechSynthesis' in window) || !text) return;
  const wasSame = activeSpeechBtn === btn;
  stopTTS();
  if (wasSame) return;
  const utt = new SpeechSynthesisUtterance(text);
  utt.lang = L.ttsLocale;
  if (btn) {{
    btn.classList.add('speaking');
    activeSpeechBtn = btn;
  }}
  utt.onend = () => {{ if (activeSpeechBtn === btn) {{ btn.classList.remove('speaking'); activeSpeechBtn = null; }} }};
  utt.onerror = utt.onend;
  window.speechSynthesis.speak(utt);
}}

function speakCurrentAlert() {{
  speakText(latestAlertText, document.getElementById('btnSpeakAlert'));
}}

function showAlertBanner(alertObj) {{
  const banner = document.getElementById('liveAlertBanner');
  if (!banner || !alertObj) return;
  const isWarn = (alertObj.severity || '').toLowerCase() === 'warning';
  banner.className = 'alert-banner' + (isWarn ? ' warn' : '');
  const title = L.liveAlertPrefix + (alertObj.title || alertObj.category || '') + ' — ' + (alertObj.targetName || '');
  document.getElementById('liveAlertTitle').textContent = title;
  document.getElementById('liveAlertMsg').textContent = (alertObj.message || '') + (alertObj.occurredAtLocal ? ' (' + alertObj.occurredAtLocal + ')' : '');
  latestAlertText = title + '. ' + (alertObj.message || '');
  banner.style.display = 'block';
}}

function dismissBanner() {{
  stopTTS();
  const banner = document.getElementById('liveAlertBanner');
  if (banner) banner.style.display = 'none';
}}

function renderSnapshot() {{
  const sg = document.getElementById('summaryGrid');
  const runningCnt = (SNAP.containers || []).filter(c => c.stateKey === 'running').length;
  const totalCnt = (SNAP.containers || []).length;
  const mins = Math.floor((SNAP.remainingSeconds || 0) / 60);
  sg.innerHTML =
    '<div class="stat-card"><div class="stat-lbl">' + esc(L.engineLbl) + '</div><div class="stat-val">' + esc(SNAP.engineMode) + ' · ' + (SNAP.engineRunning ? '🟢' : '🔴') + '</div></div>' +
    '<div class="stat-card"><div class="stat-lbl">' + esc(L.totalContainersLbl) + '</div><div class="stat-val">' + runningCnt + ' / ' + totalCnt + ' ' + esc(L.running) + '</div></div>' +
    '<div class="stat-card"><div class="stat-lbl">' + esc(L.totalVolumesLbl) + '</div><div class="stat-val">' + (SNAP.volumes || []).length + ' (' + esc(SNAP.totalVolumeText) + ')</div></div>' +
    '<div class="stat-card"><div class="stat-lbl">' + esc(L.unusedVolLbl) + '</div><div class="stat-val">' + (SNAP.unusedVolumeCount || 0) + ' (' + esc(SNAP.unusedVolumeText) + ') · ' + mins + 'm ' + esc(L.sessionRemLbl) + '</div></div>';

  const cEl = document.getElementById('tab-containers');
  const containers = SNAP.containers || [];
  if (containers.length === 0) {{
    cEl.innerHTML = '<div class="card"><div class="meta">' + esc(L.noContainers) + '</div></div>';
  }} else {{
    cEl.innerHTML = containers.map(c => {{
      const pillCls = c.stateKey === 'running' ? 'pill-ok' : (c.stateKey === 'restarting' ? 'pill-warn' : 'pill-bad');
      const diagBadge = c.hasDangerDiagnostics ? '<span class="pill pill-bad">' + esc(L.diagDanger) + '</span>' : (c.hasWarningDiagnostics ? '<span class="pill pill-warn">' + esc(L.diagWarn) + '</span>' : '');
      return '<div class="card"><div class="row-head"><span class="name">' + esc(c.name) + '</span><span><span class="pill ' + pillCls + '">' + esc(c.status) + '</span> ' + diagBadge + '</span></div>' +
        '<div class="meta">🖼️ ' + esc(c.image) + '</div>' +
        (c.resourceSummary && c.resourceSummary !== '--' ? '<div class="meta">📊 ' + esc(c.resourceSummary) + '</div>' : '') +
        (c.ports && c.ports !== '--' ? '<div class="meta">🔌 ' + esc(c.ports) + '</div>' : '') +
        (c.mountsSummary && c.mountsSummary !== '--' ? '<div class="meta">📦 ' + esc(c.mountsSummary) + '</div>' : '') +
        (c.diagnosticsNote ? '<div class="meta">🩺 ' + esc(c.diagnosticsNote) + '</div>' : '') +
        '</div>';
    }}).join('');
  }}

  const vEl = document.getElementById('tab-volumes');
  const vols = SNAP.volumes || [];
  if (vols.length === 0) {{
    vEl.innerHTML = '<div class="card"><div class="meta">' + esc(L.noVolumes) + '</div></div>';
  }} else {{
    vEl.innerHTML = vols.map(v => {{
      const useCls = v.isUnused ? 'pill-warn' : 'pill-ok';
      const szCls = v.sizeLevel >= 2 ? 'pill-bad' : (v.sizeLevel === 1 ? 'pill-warn' : 'pill-muted');
      return '<div class="card"><div class="row-head"><span class="name">' + esc(v.displayName) + '</span><span><span class="pill pill-muted">' + esc(v.isAnonymous ? L.anon : L.named) + '</span> <span class="pill ' + useCls + '">' + esc(v.isUnused ? L.unused : L.inUse) + '</span> <span class="pill ' + szCls + '">' + esc(v.sizeText) + '</span></span></div>' +
        '<div class="meta">Driver: ' + esc(v.driver) + ' · ' + esc(v.attachedSummary) + '</div>' +
        '</div>';
    }}).join('');
  }}

  const aEl = document.getElementById('tab-alerts');
  const alerts = SNAP.alerts || [];
  if (alerts.length === 0) {{
    aEl.innerHTML = '<div class="card"><div class="meta">' + esc(L.noAlerts) + '</div></div>';
  }} else {{
    aEl.innerHTML = alerts.map(a => {{
      const cls = (a.severity || '').toLowerCase() === 'danger' ? 'pill-bad' : 'pill-warn';
      return '<div class="card"><div class="row-head"><span class="name"><span class="pill ' + cls + '">' + esc(a.severity) + '</span> ' + esc(a.title || a.category) + '</span><span class="meta">' + esc(a.occurredAtLocal) + '</span></div>' +
        '<div class="meta">' + esc(a.targetName) + ' — ' + esc(a.message) + '</div></div>';
    }}).join('');
  }}
}}

function renderFaq() {{
  const fEl = document.getElementById('tab-faq');
  let html = '';
  FAQ_DATA.forEach(cat => {{
    html += '<h3 style="margin:12px 0 6px; font-size:15px; color:#38BDF8;">' + esc(cat.icon) + ' ' + esc(cat.title) + '</h3>';
    cat.items.forEach((item, idx) => {{
      const speechPayload = esc(item.q + '. ' + item.a);
      html += '<details class="faq-item"><summary>' + esc(item.q) + '</summary>' +
        '<div class="faq-ans">' + esc(item.a) +
        '<div style="margin-top:8px;"><button type="button" class="tts-btn" data-tts="' + speechPayload + '" onclick="speakText(this.getAttribute(\'data-tts\'), this)">' + esc(L.speak) + '</button></div></div></details>';
    }});
  }});
  fEl.innerHTML = html;
}}

let lastSeenAlertId = (SNAP.alerts && SNAP.alerts.length > 0) ? SNAP.alerts[0].id : 0;

function apiQuerySuffix() {{
  return SESSION_TOKEN ? ('?s=' + encodeURIComponent(SESSION_TOKEN)) : '';
}}

async function pollSnapshot() {{
  try {{
    const res = await fetch('/api/snapshot' + apiQuerySuffix(), {{ cache: 'no-store' }});
    if (!res.ok) return;
    const next = await res.json();
    SNAP = next;
    renderSnapshot();
    if (next.alerts && next.alerts.length > 0 && next.alerts[0].id > lastSeenAlertId) {{
      lastSeenAlertId = next.alerts[0].id;
      showAlertBanner(next.alerts[0]);
    }}
  }} catch (e) {{}}
}}

function connectSse() {{
  if (!('EventSource' in window)) return;
  try {{
    const es = new EventSource('/api/events' + apiQuerySuffix());
    es.addEventListener('alert', ev => {{
      try {{
        const al = JSON.parse(ev.data);
        if (al && al.id) lastSeenAlertId = Math.max(lastSeenAlertId, al.id);
        showAlertBanner(al);
        pollSnapshot();
      }} catch (e) {{}}
    }});
  }} catch (e) {{}}
}}

async function sendHeartbeat() {{
  try {{
    await fetch('/api/heartbeat' + apiQuerySuffix(), {{ method: 'POST', cache: 'no-store' }});
  }} catch (e) {{}}
}}

renderSnapshot();
renderFaq();
connectSse();
setInterval(pollSnapshot, 5000);
setInterval(sendHeartbeat, 30000);
</script>
</body>
</html>"""

    let broadcastAlert (dto: MobileAlertDto) =
        let payload = sprintf "event: alert\ndata: %s\n\n" (serializeAlertDto dto)
        let bytes = Encoding.UTF8.GetBytes(payload)
        for kv in sseClients |> Seq.toArray do
            let (st, gate) = kv.Value
            Task.Run(fun () ->
                let mutable entered = false
                try
                    try
                        gate.Wait()
                        entered <- true
                        st.Write(bytes, 0, bytes.Length)
                        st.Flush()
                    with _ ->
                        let mutable removed = Unchecked.defaultof<Stream * SemaphoreSlim>
                        sseClients.TryRemove(kv.Key, &removed) |> ignore
                        try st.Dispose() with _ -> ()
                finally
                    if entered then try gate.Release() |> ignore with _ -> ()
            ) |> ignore

    let handleHttpExchange (inStream: Stream) (outStream: Stream) (remoteIp: string) (nowUtc: DateTimeOffset) (isSseLive: bool) =
        let buffer = Array.zeroCreate<byte> 32768
        let mutable totalRead = 0
        let mutable headerEnd = -1
        let mutable eof = false
        while totalRead < buffer.Length && headerEnd < 0 && not eof do
            let read = inStream.Read(buffer, totalRead, buffer.Length - totalRead)
            if read <= 0 then eof <- true
            else
                totalRead <- totalRead + read
                headerEnd <- findHeaderEndIndex buffer totalRead

        if totalRead > 0 then
            let headerText = Encoding.ASCII.GetString(buffer, 0, if headerEnd >= 0 then headerEnd else totalRead)
            let headerLines = headerText.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)
            if headerLines.Length > 0 then
                let parts = headerLines.[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                if parts.Length >= 2 then
                    let httpMethod = parts.[0].ToUpperInvariant()
                    let rawTarget = parts.[1]
                    let pathOnly =
                        let qIdx = rawTarget.IndexOf('?')
                        if qIdx >= 0 then rawTarget.Substring(0, qIdx) else rawTarget
                    let query = parseQueryParams rawTarget
                    let providedQr =
                        let ok, v = query.TryGetValue("t")
                        if ok && not (String.IsNullOrWhiteSpace(v)) then Some v else None
                    let querySid =
                        let ok, v = query.TryGetValue("s")
                        if ok && not (String.IsNullOrWhiteSpace(v)) then Some v else None
                    let cookieSid = tryGetCookie headerLines "ecm_session"
                    let headerSid = tryGetHeader headerLines "X-Session-Token"
                    let providedSid = headerSid |> Option.orElse cookieSid |> Option.orElse querySid

                    let cookieLang = tryGetCookie headerLines "ecm_lang"
                    let defaultLang = lock syncLock (fun () -> snapshot.LanguageCode)
                    let reqLang, shouldSetLangCookie = resolveLang query cookieLang defaultLang
                    let langHeaders =
                        if shouldSetLangCookie then
                            [ sprintf "Set-Cookie: ecm_lang=%s; Path=/; Max-Age=31536000; SameSite=Lax" (Uri.EscapeDataString(reqLang)) ]
                        else []

                    let evaluateAuth () =
                        let decision, changed =
                            lock syncLock (fun () ->
                                let prevLock = authState.LockedClientIp
                                let dec = MobileSessionAuth.evaluateAccess nowUtc remoteIp providedQr providedSid authState
                                match dec with
                                | MobileSessionAuth.Allowed (nextState, _) ->
                                    authState <- nextState
                                    dec, (prevLock <> nextState.LockedClientIp)
                                | MobileSessionAuth.Denied MobileSessionAuth.SessionExpired ->
                                    authState <- MobileSessionAuth.createInactiveState ()
                                    timedOutFlag <- true
                                    dec, true
                                | MobileSessionAuth.Denied _ ->
                                    dec, false)
                        if changed then notifyStatusChanged ()
                        decision

                    let writeAuthDenied (reason: MobileSessionAuth.AccessDeniedReason) (isJson: bool) =
                        let statusCode, statusPhrase =
                            match reason with
                            | MobileSessionAuth.SessionExpired -> 410, "Gone"
                            | _ -> 403, "Forbidden"
                        if isJson then
                            let errJson = JsonSerializer.Serialize({| error = reason.ToString() |})
                            writeHttpResponse outStream statusCode statusPhrase "application/json; charset=utf-8" langHeaders (Encoding.UTF8.GetBytes(errJson))
                        else
                            let html = buildErrorHtml reqLang reason
                            writeHttpResponse outStream statusCode statusPhrase "text/html; charset=utf-8" langHeaders (Encoding.UTF8.GetBytes(html))

                    // ルーティング（厳格な読み取り専用：更新・操作エンドポイントは存在しない）
                    if httpMethod = "POST" && pathOnly = "/api/heartbeat" then
                        match evaluateAuth () with
                        | MobileSessionAuth.Denied reason -> writeAuthDenied reason true
                        | MobileSessionAuth.Allowed (st, setCookie) ->
                            let rem = MobileSessionAuth.remainingSeconds nowUtc st
                            let headers =
                                (if setCookie then [ sprintf "Set-Cookie: ecm_session=%s; Path=/; Max-Age=3600; HttpOnly; SameSite=Lax" st.SessionToken ] else [])
                                @ langHeaders
                            let json = JsonSerializer.Serialize({| active = true; remainingSeconds = rem |})
                            writeHttpResponse outStream 200 "OK" "application/json; charset=utf-8" headers (Encoding.UTF8.GetBytes(json))

                    elif httpMethod <> "GET" && httpMethod <> "HEAD" then
                        let body = Encoding.UTF8.GetBytes("Method Not Allowed")
                        writeHttpResponse outStream 405 "Method Not Allowed" "text/plain; charset=utf-8" [] body

                    elif pathOnly = "/" then
                        match evaluateAuth () with
                        | MobileSessionAuth.Denied reason -> writeAuthDenied reason false
                        | MobileSessionAuth.Allowed (st, setCookie) ->
                            let headers =
                                (if setCookie then [ sprintf "Set-Cookie: ecm_session=%s; Path=/; Max-Age=3600; HttpOnly; SameSite=Lax" st.SessionToken ] else [])
                                @ langHeaders
                            let html = buildMobilePageHtml nowUtc reqLang st.SessionToken
                            let body = if httpMethod = "HEAD" then Array.empty else Encoding.UTF8.GetBytes(html)
                            writeHttpResponse outStream 200 "OK" "text/html; charset=utf-8" headers body

                    elif pathOnly = "/api/snapshot" then
                        match evaluateAuth () with
                        | MobileSessionAuth.Denied reason -> writeAuthDenied reason true
                        | MobileSessionAuth.Allowed (st, setCookie) ->
                            let headers =
                                (if setCookie then [ sprintf "Set-Cookie: ecm_session=%s; Path=/; Max-Age=3600; HttpOnly; SameSite=Lax" st.SessionToken ] else [])
                                @ langHeaders
                            let json = buildSnapshotJson nowUtc reqLang
                            let body = if httpMethod = "HEAD" then Array.empty else Encoding.UTF8.GetBytes(json)
                            writeHttpResponse outStream 200 "OK" "application/json; charset=utf-8" headers body

                    elif pathOnly = "/api/events" then
                        match evaluateAuth () with
                        | MobileSessionAuth.Denied reason -> writeAuthDenied reason true
                        | MobileSessionAuth.Allowed (_, _) ->
                            let sseHeader = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\nevent: hello\ndata: {\"ok\":true}\n\n"
                            let hdrBytes = Encoding.UTF8.GetBytes(sseHeader)
                            outStream.Write(hdrBytes, 0, hdrBytes.Length)
                            outStream.Flush()
                            if isSseLive then
                                let clientId = Guid.NewGuid()
                                let gate = new SemaphoreSlim(1, 1)
                                sseClients.[clientId] <- (outStream, gate)
                                try
                                    let token =
                                        lock syncLock (fun () ->
                                            match cts with Some c -> c.Token | None -> CancellationToken.None)
                                    while not token.IsCancellationRequested && lock syncLock (fun () -> authState.IsActive) do
                                        Thread.Sleep(15000)
                                        if not token.IsCancellationRequested && lock syncLock (fun () -> authState.IsActive) then
                                            let ka = Encoding.ASCII.GetBytes(": keepalive\n\n")
                                            gate.Wait()
                                            try
                                                outStream.Write(ka, 0, ka.Length)
                                                outStream.Flush()
                                            finally
                                                gate.Release() |> ignore
                                with _ -> ()
                                let mutable removed = Unchecked.defaultof<Stream * SemaphoreSlim>
                                sseClients.TryRemove(clientId, &removed) |> ignore
                    else
                        let body = Encoding.UTF8.GetBytes("Not Found")
                        writeHttpResponse outStream 404 "Not Found" "text/plain; charset=utf-8" [] body

    let handleTcpClient (client: TcpClient) =
        try
            use c = client
            c.ReceiveTimeout <- 15000
            c.SendTimeout <- 30000
            let remoteIp =
                try
                    match c.Client.RemoteEndPoint with
                    | :? IPEndPoint as ep -> ep.Address.ToString()
                    | _ -> "127.0.0.1"
                with _ -> "127.0.0.1"
            use stream = c.GetStream()
            handleHttpExchange stream stream remoteIp DateTimeOffset.UtcNow true
        with _ -> ()

    /// <summary>簡単コンテナ管理の既定ポート（54820 の簡単収納管理や 5050/5058/5070 と重複しない番号）。</summary>
    static member DefaultPort = 54830

    member _.IsRunning = lock syncLock (fun () -> listener.IsSome && authState.IsActive)
    member _.Port = lock syncLock (fun () -> activePort)
    member _.SessionStateChanged = sessionStateChanged.Publish
    member _.GetStatus() : MobileSessionStatus = statusSnapshot ()

    member _.GetQrUrl(ipAddress: string) : string =
        let st = statusSnapshot ()
        if not st.IsRunning || String.IsNullOrWhiteSpace(ipAddress) then ""
        else sprintf "http://%s:%d/?t=%s" (ipAddress.Trim()) st.Port st.QrToken

    /// <summary>ダッシュボードの読み取り専用スナップショットを更新する。</summary>
    member _.UpdateSnapshot(next: MobileDashboardSnapshot) =
        lock syncLock (fun () -> snapshot <- next)

    /// <summary>新規アラートをスナップショットおよび接続中の SSE クライアントへ配信する。</summary>
    member _.PushAlert(entry: AlertEventEntry) =
        let dto =
            let lang = lock syncLock (fun () -> snapshot.LanguageCode)
            {
                Id = entry.Id
                OccurredAt = entry.OccurredAt
                OccurredAtLocal = AlertMonitor.formatTimestamp entry.OccurredAt
                Severity = AlertMonitor.severityToDbString entry.Severity
                Category = AlertMonitor.categoryToDbString entry.Category
                TargetName = entry.TargetName
                Title = Localization.translateKey lang entry.TitleKey
                Message = entry.Message
            }
        lock syncLock (fun () ->
            let updatedAlerts =
                (dto :: snapshot.Alerts)
                |> List.distinctBy (fun a -> a.Id, a.OccurredAt, a.TargetName, a.Message)
                |> List.truncate 50
            snapshot <- { snapshot with Alerts = updatedAlerts; UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o") })
        if listener.IsSome then
            broadcastAlert dto

    /// <summary>生の HTTP/1.1 リクエストバイト列を処理する（単体テスト用）。</summary>
    member _.ProcessHttpBytes(requestBytes: byte[], ?remoteIp: string, ?nowUtc: DateTimeOffset) : byte[] =
        use reqMs = new MemoryStream(requestBytes)
        use respMs = new MemoryStream()
        handleHttpExchange reqMs respMs (defaultArg remoteIp "127.0.0.1") (defaultArg nowUtc DateTimeOffset.UtcNow) false
        respMs.ToArray()

    /// <summary>テストや状態検証用にインメモリセッション状態のみを開始する（ソケット待機なし）。</summary>
    member _.StartInMemorySessionForTest(?idleTimeoutMinutes: float, ?nowUtc: DateTimeOffset) : MobileSessionStatus =
        let mins = defaultArg idleTimeoutMinutes 15.0 |> max 1.0
        let now = defaultArg nowUtc DateTimeOffset.UtcNow
        lock syncLock (fun () ->
            timedOutFlag <- false
            authState <- MobileSessionAuth.createActiveState now (TimeSpan.FromMinutes(mins)))
        notifyStatusChanged ()
        statusSnapshot ()

    /// <summary>ユーザー操作に応じてローカルサーバーと 1 対 1 スマホセッションを起動する。</summary>
    member this.Start(?preferredPort: int, ?idleTimeoutMinutes: float) : Result<int, string> =
        if this.IsRunning then
            Ok activePort
        else
            let startPort = defaultArg preferredPort (MobileSessionServer.DefaultPort)
            let timeoutSpan = TimeSpan.FromMinutes(defaultArg idleTimeoutMinutes 15.0 |> max 1.0)
            try
                let mutable boundOpt : TcpListener option = None
                for p in startPort .. startPort + 10 do
                    if boundOpt.IsNone && not (isBrowserRestrictedPort p) then
                        try
                            let tcp = new TcpListener(IPAddress.Any, p)
                            tcp.Start()
                            boundOpt <- Some tcp
                        with _ -> ()

                if boundOpt.IsNone then
                    for _ in 1 .. 5 do
                        if boundOpt.IsNone then
                            let tcp = new TcpListener(IPAddress.Any, 0)
                            tcp.Start()
                            let p = (tcp.LocalEndpoint :?> IPEndPoint).Port
                            if isBrowserRestrictedPort p then
                                tcp.Stop()
                            else
                                boundOpt <- Some tcp

                let bound =
                    match boundOpt with
                    | Some b -> b
                    | None -> failwith "Could not bind any available TCP port."

                let assignedPort = (bound.LocalEndpoint :?> IPEndPoint).Port
                let tokenSource = new CancellationTokenSource()
                let ct = tokenSource.Token
                lock syncLock (fun () ->
                    listener <- Some bound
                    cts <- Some tokenSource
                    activePort <- assignedPort
                    timedOutFlag <- false
                    authState <- MobileSessionAuth.createActiveState DateTimeOffset.UtcNow timeoutSpan)

                let loop =
                    Task.Run(fun () ->
                        while not ct.IsCancellationRequested do
                            try
                                let client = bound.AcceptTcpClient()
                                Task.Run(fun () -> handleTcpClient client) |> ignore
                            with
                            | :? SocketException -> ()
                            | :? ObjectDisposedException -> ()
                            | _ -> ())

                let watchdog =
                    Task.Run(fun () ->
                        while not ct.IsCancellationRequested do
                            try
                                Thread.Sleep(10000)
                                if not ct.IsCancellationRequested then
                                    let expiredNow =
                                        lock syncLock (fun () ->
                                            if authState.IsActive && MobileSessionAuth.isExpired DateTimeOffset.UtcNow authState then
                                                authState <- MobileSessionAuth.createInactiveState ()
                                                timedOutFlag <- true
                                                true
                                            else
                                                false)
                                    if expiredNow then
                                        this.StopInternal(keepTimedOutFlag = true)
                            with _ -> ())

                acceptTask <- Some loop
                watchdogTask <- Some watchdog
                notifyStatusChanged ()
                Ok assignedPort
            with ex ->
                Error ex.Message

    member private _.StopInternal(keepTimedOutFlag: bool) =
        closeAllSseClients ()
        let oldCts, oldListener =
            lock syncLock (fun () ->
                let c = cts
                let l = listener
                cts <- None
                listener <- None
                activePort <- 0
                authState <- MobileSessionAuth.createInactiveState ()
                if not keepTimedOutFlag then timedOutFlag <- false
                c, l)
        oldCts |> Option.iter (fun c -> try c.Cancel() with _ -> ())
        oldListener |> Option.iter (fun l -> try l.Stop() with _ -> ())
        oldCts |> Option.iter (fun c -> try c.Dispose() with _ -> ())
        acceptTask <- None
        watchdogTask <- None
        notifyStatusChanged ()

    /// <summary>ローカルサーバーと現在のセッションを停止する。</summary>
    member this.Stop() : unit =
        this.StopInternal(keepTimedOutFlag = false)

    /// <summary>新しい QR トークンを発行し、既存端末の 1 対 1 ロックを解除する。</summary>
    member _.ReissueSession(?idleTimeoutMinutes: float) : MobileSessionStatus =
        closeAllSseClients ()
        lock syncLock (fun () ->
            if listener.IsSome then
                let timeoutSpan =
                    match idleTimeoutMinutes with
                    | Some mins -> TimeSpan.FromMinutes(max 1.0 mins)
                    | None -> authState.IdleTimeout
                timedOutFlag <- false
                authState <- MobileSessionAuth.createActiveState DateTimeOffset.UtcNow timeoutSpan)
        notifyStatusChanged ()
        statusSnapshot ()

    interface IDisposable with
        member this.Dispose() = this.Stop()
