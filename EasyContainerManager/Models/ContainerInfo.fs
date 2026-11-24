namespace EasyContainerManager.Models

open System
open Avalonia
open Avalonia.Media
open Avalonia.Styling
open CommunityToolkit.Mvvm.ComponentModel
open EasyContainerManager

/// <summary>公開ポートのバインド範囲区分。</summary>
type PortExposure =
    /// <summary>すべてのネットワークインターフェース（0.0.0.0 / [::]）にバインド。</summary>
    | AllInterfaces
    /// <summary>ローカルマシンのみ（127.0.0.1 / localhost / [::1]）にバインド。</summary>
    | LocalOnly
    /// <summary>特定のIPアドレスにバインド。</summary>
    | SpecificAddress

/// <summary>コンテナの公開ポートバインド 1 件分を表すモデル。</summary>
type ContainerPortBinding(rawHostIp: string, rawHostPort: string, rawContainerPort: string) =
    static let normalizeHostIp (value: string) =
        let trimmed = if isNull value then "" else value.Trim()
        if String.IsNullOrWhiteSpace(trimmed) || trimmed = "0.0.0.0" || trimmed = "*" then "0.0.0.0"
        elif trimmed = "::" || trimmed = "[::]" || trimmed = "0:0:0:0:0:0:0:0" then "[::]"
        elif trimmed = "::1" || trimmed = "[::1]" then "[::1]"
        elif trimmed.Contains(":") && not (trimmed.StartsWith("[", StringComparison.Ordinal)) then sprintf "[%s]" trimmed
        else trimmed

    static let normalizeContainerPort (value: string) =
        let trimmed = if isNull value then "" else value.Trim()
        if String.IsNullOrWhiteSpace(trimmed) then ""
        elif trimmed.Contains("/") then trimmed
        else trimmed + "/tcp"

    let hostIp = normalizeHostIp rawHostIp
    let hostPort = if isNull rawHostPort then "" else rawHostPort.Trim()
    let containerPort = normalizeContainerPort rawContainerPort
    let exposure =
        if hostIp = "0.0.0.0" || hostIp = "[::]" then AllInterfaces
        elif hostIp = "127.0.0.1"
             || hostIp.Equals("localhost", StringComparison.OrdinalIgnoreCase)
             || hostIp = "[::1]"
             || hostIp.StartsWith("127.", StringComparison.Ordinal) then LocalOnly
        else SpecificAddress

    /// <summary>ホスト側のバインドIPアドレス。</summary>
    member _.HostIp = hostIp

    /// <summary>ホスト側の公開ポート番号。</summary>
    member _.HostPort = hostPort

    /// <summary>コンテナ側の内部ポート番号とプロトコル（例: 80/tcp）。</summary>
    member _.ContainerPort = containerPort

    /// <summary>ポートの公開範囲区分（外部公開 / ローカル限定 / 特定IP）。</summary>
    member _.Exposure = exposure

    /// <summary>すべてのインターフェースから接続可能かどうか。</summary>
    member _.IsAllInterfaces = exposure = AllInterfaces

    /// <summary>ローカル（このPC）からのみ接続可能かどうか。</summary>
    member _.IsLocalOnly = exposure = LocalOnly

    /// <summary>UI表示用のポートバインド文字列（例: 0.0.0.0:80->80/tcp）。</summary>
    member _.Display = sprintf "%s:%s->%s" hostIp hostPort containerPort

/// <summary>コンテナにマウントされているボリューム／バインド情報。</summary>
type ContainerMountInfo = {
    MountType: string
    VolumeName: string
    Source: string
    Destination: string
    ReadWrite: bool
}

/// <summary>一覧に表示するコンテナの状態・公開ポート・イメージ・リソースサマリーを保持するモデル。</summary>
[<AllowNullLiteral>]
type ContainerInfo(id: string, initialName: string, initialStatus: string, initialImage: string, initialCreated: string, ?initialPorts: ContainerPortBinding list, ?initialMounts: ContainerMountInfo list, ?initialRestartCount: int, ?initialOomKilled: bool, ?initialExitCode: int, ?initialInspectStatus: string) =
    inherit ObservableObject()

    let mutable name = initialName
    let mutable status = initialStatus
    let mutable image = initialImage
    let mutable created = initialCreated
    let mutable languageCode = "ja-JP"
    let mutable resources: ContainerStats option = None
    let mutable portBindings: ContainerPortBinding list = defaultArg initialPorts []
    let mutable mounts: ContainerMountInfo list = defaultArg initialMounts []
    let mutable restartCount: int = defaultArg initialRestartCount 0
    let mutable oomKilled: bool = defaultArg initialOomKilled false
    let mutable exitCode: int = defaultArg initialExitCode 0
    let mutable inspectStatus: string = defaultArg initialInspectStatus ""
    let mutable isOperating = false

    static let isDarkTheme () =
        not (isNull Application.Current) && Application.Current.ActualThemeVariant = ThemeVariant.Dark

    static let formatMountLabel (mount: ContainerMountInfo) =
        let rawName =
            if not (String.IsNullOrWhiteSpace(mount.VolumeName)) then mount.VolumeName.Trim()
            else mount.Source.Trim()
        let isHex64 =
            rawName.Length = 64
            && rawName |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
        let shortName = if isHex64 then rawName.Substring(0, 12) + "…" else rawName
        if String.IsNullOrWhiteSpace(mount.Destination) then shortName
        else sprintf "%s → %s" shortName mount.Destination

    /// <summary>コンテナ固有のID（短縮IDまたはフルID）。</summary>
    member _.Id = id

    /// <summary>このコンテナ行に対する変更系コマンド（起動・停止・再起動・削除等）が実行中かどうかを示す。</summary>
    member this.IsOperating
        with get () = isOperating
        and set value =
            if this.SetProperty(&isOperating, value) then
                this.OnPropertyChanged(nameof this.IsOperating)

    /// <summary>コンテナ名。</summary>
    member this.Name
        with get () = name
        and set value = this.SetProperty(&name, value) |> ignore

    /// <summary>コンテナの生ステータス文字列（例: Up 2 hours, Exited (0) 10 minutes ago）。</summary>
    member this.Status
        with get () = status
        and set value =
            if this.SetProperty(&status, value) then this.OnPropertyChanged(nameof this.StatusLabel)

    /// <summary>コンテナ作成元のイメージ名。</summary>
    member this.Image
        with get () = image
        and set value = this.SetProperty(&image, value) |> ignore

    /// <summary>コンテナの作成日時文字列。</summary>
    member this.Created
        with get () = created
        and set value = this.SetProperty(&created, value) |> ignore

    /// <summary>バインドされている公開ポート一覧。</summary>
    member this.PortBindings
        with get () = portBindings
        and set (value: ContainerPortBinding list) =
            let currentDisplays = portBindings |> List.map (fun item -> item.Display)
            let nextDisplays = value |> List.map (fun item -> item.Display)
            if currentDisplays <> nextDisplays then
                portBindings <- value
                this.OnPropertyChanged(nameof this.PortBindings)
                this.OnPropertyChanged(nameof this.PublishedPorts)
                this.OnPropertyChanged(nameof this.HasAllInterfacesPort)
                this.OnPropertyChanged(nameof this.PortsBrush)
                this.OnPropertyChanged(nameof this.PortsTooltip)

    /// <summary>コンテナにマウントされているストレージ一覧。</summary>
    member this.Mounts
        with get () = mounts
        and set (value: ContainerMountInfo list) =
            if mounts <> value then
                mounts <- value
                this.OnPropertyChanged(nameof this.Mounts)
                this.OnPropertyChanged(nameof this.VolumeMounts)
                this.OnPropertyChanged(nameof this.HasVolumeMounts)
                this.OnPropertyChanged(nameof this.MountedVolumesSummary)

    /// <summary>ボリューム種別またはボリューム名を持つマウント一覧。</summary>
    member _.VolumeMounts =
        mounts
        |> List.filter (fun m ->
            m.MountType.Equals("volume", StringComparison.OrdinalIgnoreCase)
            || not (String.IsNullOrWhiteSpace(m.VolumeName)))

    /// <summary>マウント中ボリュームが 1 件以上あるかどうか。</summary>
    member this.HasVolumeMounts =
        not (List.isEmpty this.VolumeMounts)

    /// <summary>マウント中ボリュームの要約文字列。</summary>
    member this.MountedVolumesSummary =
        let vols = this.VolumeMounts
        if List.isEmpty vols then "--"
        else vols |> List.map formatMountLabel |> String.concat ", "

    /// <summary>コンテナの再起動回数（inspect の RestartCount）。</summary>
    member this.RestartCount
        with get () = restartCount
        and set value = this.SetProperty(&restartCount, value) |> ignore

    /// <summary>メモリ不足で強制終了されたか（inspect の State.OOMKilled）。</summary>
    member this.OomKilled
        with get () = oomKilled
        and set value = this.SetProperty(&oomKilled, value) |> ignore

    /// <summary>終了コード（inspect の State.ExitCode）。</summary>
    member this.ExitCode
        with get () = exitCode
        and set value = this.SetProperty(&exitCode, value) |> ignore

    /// <summary>inspect 由来の状態文字列（running / exited 等）。</summary>
    member this.InspectStatus
        with get () = inspectStatus
        and set value = this.SetProperty(&inspectStatus, value) |> ignore

    /// <summary>UI表示用の公開ポート文字列（カンマ区切り、未公開時は --）。</summary>
    member _.PublishedPorts =
        if List.isEmpty portBindings then "--"
        else portBindings |> List.map (fun item -> item.Display) |> String.concat ", "

    /// <summary>外部ネットワーク全般（0.0.0.0 など）に公開されているポートを含むかどうか。</summary>
    member _.HasAllInterfacesPort =
        portBindings |> List.exists (fun item -> item.IsAllInterfaces)

    /// <summary>公開ポートの警戒レベルに応じた表示色ブラシ。</summary>
    member this.PortsBrush : IBrush =
        if List.isEmpty portBindings then Brushes.Gray
        elif this.HasAllInterfacesPort then Brushes.DarkGoldenrod
        elif isDarkTheme () then Brushes.WhiteSmoke
        else Brushes.DimGray

    /// <summary>公開ポートの安全性に関するツールチップ説明文字列。</summary>
    member _.PortsTooltip : string =
        if List.isEmpty portBindings then null
        else
            let hasAll = portBindings |> List.exists (fun item -> item.IsAllInterfaces)
            let hasLocal = portBindings |> List.exists (fun item -> item.IsLocalOnly)
            if hasAll && hasLocal then
                sprintf "%s / %s"
                    (Localization.translate languageCode "全てのネットワークから接続可能" "Accessible from all networks")
                    (Localization.translate languageCode "このPCからのみ接続可能" "Accessible only from this PC")
            elif hasAll then
                Localization.translate languageCode "全てのネットワークから接続可能" "Accessible from all networks"
            else
                Localization.translate languageCode "このPCからのみ接続可能" "Accessible only from this PC"

    /// <summary>現在の表示言語コード（ja-JP, en-US 等）。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then
                resources |> Option.iter (fun item -> item.LanguageCode <- value)
                this.OnPropertyChanged(nameof this.StatusLabel)
                this.OnPropertyChanged(nameof this.ResourceSummary)
                this.OnPropertyChanged(nameof this.PortsTooltip)

    /// <summary>現在のテーマ変更に合わせてポート表示ブラシの再評価を通知する。</summary>
    member this.RefreshTheme() =
        this.OnPropertyChanged(nameof this.PortsBrush)

    /// <summary>CPU・メモリ使用率のリソースサマリー文字列。</summary>
    member _.ResourceSummary =
        match resources with
        | Some item -> sprintf "CPU %s · MEM %s · %s" item.Cpu item.MemoryPercent item.Status
        | None -> "--"

    /// <summary>リソース使用率の警戒レベルに応じたブラシ。</summary>
    member _.ResourceBrush =
        match resources with
        | Some item -> item.StatusBrush
        | None -> ResourceUsage.brush -1
    /// <summary>コンテナの最新のリソース統計情報を反映し、表示用プロパティを更新する。</summary>
    member this.UpdateResources(value: ContainerStats option) =
        resources <- value
        this.OnPropertyChanged(nameof this.ResourceSummary)
        this.OnPropertyChanged(nameof this.ResourceBrush)
    /// <summary>コンテナ状態文字列を現在の言語向けのアイコン付きラベルに変換する。</summary>
    member _.StatusLabel =
        if status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) then Localization.translate languageCode "🟢 実行中" "🟢 Running"
        elif status.StartsWith("Restarting", StringComparison.OrdinalIgnoreCase) then Localization.translate languageCode "🟡 再起動中" "🟡 Restarting"
        elif status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase)
             || status.StartsWith("Created", StringComparison.OrdinalIgnoreCase) then Localization.translate languageCode "🔴 停止中" "🔴 Stopped"
        else Localization.translate languageCode "⚫ 不明" "⚫ Unknown"

    /// <summary>別の ContainerInfo から変更のあった項目を取り込む。</summary>
    member this.UpdateFrom(source: ContainerInfo) =
        this.Name <- source.Name
        this.Status <- source.Status
        this.Image <- source.Image
        this.Created <- source.Created
        this.PortBindings <- source.PortBindings
        this.Mounts <- source.Mounts
        this.RestartCount <- source.RestartCount
        this.OomKilled <- source.OomKilled
        this.ExitCode <- source.ExitCode
        this.InspectStatus <- source.InspectStatus