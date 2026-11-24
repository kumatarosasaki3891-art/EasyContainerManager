namespace EasyContainerManager.Models

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions
open Avalonia.Media
open CommunityToolkit.Mvvm.ComponentModel
open EasyContainerManager

/// <summary>ボリュームを参照しているコンテナの紐付け情報。</summary>
type ContainerVolumeRef = {
    ContainerId: string
    ContainerName: string
    Destination: string
    ReadWrite: bool
}

/// <summary>CLI volume ls から取得した生のボリューム定義レコード。</summary>
type VolumeRawRecord = {
    Name: string
    Driver: string
    Mountpoint: string
    Scope: string
    CreatedAt: string
    ExplicitAnonymous: bool option
}

/// <summary>CLI volume inspect から取得した詳細レコード。</summary>
type VolumeInspectRecord = {
    Name: string
    Driver: string
    Mountpoint: string
    Scope: string
    CreatedAt: string
    ExplicitAnonymous: bool option
}

/// <summary>CLI system df -v から取得した容量・参照数レコード。</summary>
type VolumeDfRecord = {
    Name: string
    Links: int option
    SizeText: string
    SizeBytes: int64 option
}

/// <summary>一覧表示および診断・監視で使用する統合済みボリュームスナップショット。</summary>
type VolumeSnapshot = {
    Name: string
    Driver: string
    Mountpoint: string
    CreatedAt: string
    SizeBytes: int64 option
    SizeText: string
    Links: int option
    IsAnonymous: bool
    IsUnused: bool
    AttachedContainers: ContainerVolumeRef list
}

/// <summary>ボリューム関連 CLI 出力の解析・容量計算・コンテナ紐付け・診断を行う純粋モジュール。</summary>
module VolumeParser =

    /// <summary>ボリューム容量の警告しきい値（5 GiB）。</summary>
    let volumeWarningThresholdBytes = 5L * 1024L * 1024L * 1024L

    /// <summary>ボリューム容量の危険しきい値（20 GiB）。</summary>
    let volumeDangerThresholdBytes = 20L * 1024L * 1024L * 1024L

    let private trimOrEmpty (s: string) =
        if isNull s then "" else s.Trim()

    let private parseObjects (output: string) : JsonElement list =
        let text = trimOrEmpty output
        if String.IsNullOrWhiteSpace(text) then []
        elif text.StartsWith("[", StringComparison.Ordinal) then
            use doc = JsonDocument.Parse(text)
            doc.RootElement.EnumerateArray() |> Seq.map (fun item -> item.Clone()) |> Seq.toList
        else
            text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun line ->
                let trimmed = line.Trim()
                if String.IsNullOrWhiteSpace(trimmed) then None
                else
                    use doc = JsonDocument.Parse(trimmed)
                    Some(doc.RootElement.Clone()))
            |> Array.toList

    let private tryPropAny (names: string list) (element: JsonElement) : JsonElement option =
        if element.ValueKind <> JsonValueKind.Object then None
        else
            element.EnumerateObject()
            |> Seq.tryFind (fun prop -> names |> List.exists (fun n -> String.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase)))
            |> Option.map (fun prop -> prop.Value)

    let private strPropAny (names: string list) (element: JsonElement) : string =
        match tryPropAny names element with
        | Some v when v.ValueKind = JsonValueKind.String -> v.GetString() |> trimOrEmpty
        | Some v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> v.ToString().Trim()
        | _ -> ""

    let private boolPropOpt (names: string list) (element: JsonElement) : bool option =
        match tryPropAny names element with
        | Some v when v.ValueKind = JsonValueKind.True -> Some true
        | Some v when v.ValueKind = JsonValueKind.False -> Some false
        | Some v when v.ValueKind = JsonValueKind.String ->
            match Boolean.TryParse(v.GetString()) with
            | true, b -> Some b
            | _ -> None
        | _ -> None

    let private intPropOpt (names: string list) (element: JsonElement) : int option =
        match tryPropAny names element with
        | Some v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, n -> Some n
            | _ -> None
        | Some v when v.ValueKind = JsonValueKind.String ->
            match Int32.TryParse(v.GetString()) with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    /// <summary>SI / IEC 容量表記文字列（例: 1.5GB, 256MiB, 0B, 1024）をバイト数へ解析する。</summary>
    let parseByteSize (raw: string) : int64 option =
        let text = trimOrEmpty raw
        if String.IsNullOrWhiteSpace(text)
           || text = "--"
           || text.Equals("N/A", StringComparison.OrdinalIgnoreCase)
           || text.Equals("unknown", StringComparison.OrdinalIgnoreCase) then
            None
        else
            let m = Regex.Match(text, @"^\s*([0-9]+(?:\.[0-9]+)?)\s*([a-zA-Z]*)\s*$")
            if not m.Success then None
            else
                match Double.TryParse(m.Groups.[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | false, _ -> None
                | true, value when not (Double.IsFinite(value)) || value < 0.0 -> None
                | true, value ->
                    let unit = m.Groups.[2].Value.Trim().ToLowerInvariant()
                    let multiplierOpt =
                        match unit with
                        | "" | "b" | "bytes" -> Some 1.0
                        | "k" | "kb" -> Some 1000.0
                        | "m" | "mb" -> Some (1000.0 * 1000.0)
                        | "g" | "gb" -> Some (1000.0 * 1000.0 * 1000.0)
                        | "t" | "tb" -> Some (1000.0 * 1000.0 * 1000.0 * 1000.0)
                        | "kib" -> Some 1024.0
                        | "mib" -> Some (1024.0 * 1024.0)
                        | "gib" -> Some (1024.0 * 1024.0 * 1024.0)
                        | "tib" -> Some (1024.0 * 1024.0 * 1024.0 * 1024.0)
                        | _ -> None
                    multiplierOpt
                    |> Option.map (fun mult -> int64 (Math.Round(value * mult)))

    /// <summary>バイト数を読みやすい容量文字列（B / KB / MB / GB / TB）へ整形する。</summary>
    let formatByteSize (bytes: int64) : string =
        let safeBytes = max 0L bytes
        if safeBytes < 1024L then
            sprintf "%d B" safeBytes
        else
            let units = [| "KB"; "MB"; "GB"; "TB" |]
            let mutable value = float safeBytes / 1024.0
            let mutable idx = 0
            while value >= 1024.0 && idx < units.Length - 1 do
                value <- value / 1024.0
                idx <- idx + 1
            if value >= 10.0 then
                String.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", value, units.[idx])
            else
                String.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units.[idx])

    /// <summary>ボリューム名が匿名ボリューム（64文字16進ハッシュ等）かを判定する。</summary>
    let isAnonymousName (name: string) (explicitAnonymous: bool option) : bool =
        match explicitAnonymous with
        | Some true -> true
        | _ ->
            let trimmed = trimOrEmpty name
            trimmed.Length = 64
            && trimmed |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))

    /// <summary>一覧表示用の短縮ボリューム名（匿名ハッシュは先頭12文字＋…）を生成する。</summary>
    let shortDisplayName (name: string) (isAnonymous: bool) : string =
        let trimmed = trimOrEmpty name
        if isAnonymous && trimmed.Length > 16 then
            trimmed.Substring(0, 12) + "…"
        else
            trimmed

    /// <summary>volume ls --format json / {{json .}} の出力を解析する。</summary>
    let parseVolumeLsJson (output: string) : VolumeRawRecord list =
        try
            parseObjects output
            |> List.choose (fun elem ->
                let name = strPropAny [ "Name"; "VolumeName" ] elem
                if String.IsNullOrWhiteSpace(name) then None
                else
                    Some {
                        Name = name
                        Driver = strPropAny [ "Driver" ] elem
                        Mountpoint = strPropAny [ "Mountpoint"; "MountPoint" ] elem
                        Scope = strPropAny [ "Scope" ] elem
                        CreatedAt = strPropAny [ "CreatedAt" ] elem
                        ExplicitAnonymous = boolPropOpt [ "Anonymous" ] elem
                    })
        with _ -> []

    /// <summary>volume inspect の JSON 出力をボリューム名キーの Map へ解析する。</summary>
    let parseVolumeInspectJson (output: string) : Map<string, VolumeInspectRecord> =
        try
            parseObjects output
            |> List.choose (fun elem ->
                let name = strPropAny [ "Name"; "VolumeName" ] elem
                if String.IsNullOrWhiteSpace(name) then None
                else
                    Some (name, {
                        Name = name
                        Driver = strPropAny [ "Driver" ] elem
                        Mountpoint = strPropAny [ "Mountpoint"; "MountPoint" ] elem
                        Scope = strPropAny [ "Scope" ] elem
                        CreatedAt = strPropAny [ "CreatedAt" ] elem
                        ExplicitAnonymous = boolPropOpt [ "Anonymous" ] elem
                    }))
            |> Map.ofList
        with _ -> Map.empty

    let private parseDfVolumeElement (elem: JsonElement) : (string * VolumeDfRecord) option =
        let name = strPropAny [ "Name"; "VolumeName" ] elem
        if String.IsNullOrWhiteSpace(name) then None
        else
            let links = intPropOpt [ "Links" ] elem
            let sizeProp = tryPropAny [ "Size" ] elem
            let sizeBytes, sizeText =
                match sizeProp with
                | Some v when v.ValueKind = JsonValueKind.Number ->
                    match v.TryGetInt64() with
                    | true, b when b >= 0L -> Some b, formatByteSize b
                    | _ -> None, ""
                | Some v when v.ValueKind = JsonValueKind.String ->
                    let raw = v.GetString() |> trimOrEmpty
                    let parsed = parseByteSize raw
                    let text =
                        match parsed with
                        | Some b when String.IsNullOrWhiteSpace(raw) -> formatByteSize b
                        | _ -> raw
                    parsed, text
                | _ -> None, ""
            Some (name, {
                Name = name
                Links = links
                SizeText = sizeText
                SizeBytes = sizeBytes
            })

    /// <summary>system df -v --format json / {{json .}} の JSON 出力からボリューム容量 Map を解析する。</summary>
    let parseSystemDfJson (output: string) : Map<string, VolumeDfRecord> =
        try
            let items = ResizeArray<string * VolumeDfRecord>()
            for elem in parseObjects output do
                match tryPropAny [ "Volumes" ] elem with
                | Some vols when vols.ValueKind = JsonValueKind.Array ->
                    for vElem in vols.EnumerateArray() do
                        match parseDfVolumeElement vElem with
                        | Some pair -> items.Add(pair)
                        | None -> ()
                | _ ->
                    // 1行1ボリューム形式、または Type=Volumes の場合は単体要素として処理
                    let typeProp = strPropAny [ "Type" ] elem
                    if String.IsNullOrWhiteSpace(typeProp) || typeProp.Equals("Volumes", StringComparison.OrdinalIgnoreCase) then
                        match parseDfVolumeElement elem with
                        | Some pair -> items.Add(pair)
                        | None -> ()
            items |> Seq.toList |> Map.ofList
        with _ -> Map.empty

    /// <summary>テキスト形式の system df -v 出力（Windows Docker Desktop 等）からボリューム容量 Map を解析する。</summary>
    let parseSystemDfText (output: string) : Map<string, VolumeDfRecord> =
        if String.IsNullOrWhiteSpace(output) then Map.empty
        else
            let results = ResizeArray<string * VolumeDfRecord>()
            let lines = output.Split([| '\r'; '\n' |])
            let mutable inVolumesSection = false
            let mutable skippedHeader = false

            for rawLine in lines do
                let trimmed = rawLine.Trim()
                if trimmed.IndexOf("Volumes space usage", StringComparison.OrdinalIgnoreCase) >= 0 then
                    inVolumesSection <- true
                    skippedHeader <- false
                elif inVolumesSection then
                    if String.IsNullOrWhiteSpace(trimmed) then
                        if skippedHeader then inVolumesSection <- false
                    elif trimmed.EndsWith(":", StringComparison.Ordinal) || trimmed.IndexOf("space usage", StringComparison.OrdinalIgnoreCase) >= 0 then
                        inVolumesSection <- false
                    elif not skippedHeader && trimmed.StartsWith("VOLUME NAME", StringComparison.OrdinalIgnoreCase) then
                        skippedHeader <- true
                    else
                        skippedHeader <- true
                        let m = Regex.Match(trimmed, @"^(\S+)\s+(\d+)\s+(.+?)\s*$")
                        if m.Success then
                            let name = m.Groups.[1].Value.Trim()
                            let links =
                                match Int32.TryParse(m.Groups.[2].Value.Trim()) with
                                | true, n -> Some n
                                | _ -> None
                            let rawSize = m.Groups.[3].Value.Trim()
                            let parsedBytes = parseByteSize rawSize
                            results.Add(name, {
                                Name = name
                                Links = links
                                SizeText = rawSize
                                SizeBytes = parsedBytes
                            })
            results |> Seq.toList |> Map.ofList

    let private mountMatchesVolume (volName: string) (mount: ContainerMountInfo) : bool =
        if not (String.IsNullOrWhiteSpace(mount.VolumeName)) then
            String.Equals(mount.VolumeName.Trim(), volName, StringComparison.OrdinalIgnoreCase)
        elif mount.MountType.Equals("volume", StringComparison.OrdinalIgnoreCase) && not (String.IsNullOrWhiteSpace(mount.Source)) then
            let src = mount.Source.TrimEnd('/', '\\')
            src.EndsWith("/" + volName + "/_data", StringComparison.OrdinalIgnoreCase)
            || src.EndsWith("\\" + volName + "\\_data", StringComparison.OrdinalIgnoreCase)
            || src.EndsWith("/" + volName, StringComparison.OrdinalIgnoreCase)
            || src.EndsWith("\\" + volName, StringComparison.OrdinalIgnoreCase)
        else
            false

    /// <summary>volume ls / inspect / system df / du / コンテナ一覧を統合して VolumeSnapshot リストを構築する。</summary>
    let linkAndBuildVolumes
        (rawList: VolumeRawRecord list)
        (inspectMap: Map<string, VolumeInspectRecord>)
        (dfMap: Map<string, VolumeDfRecord>)
        (duSizes: Map<string, int64>)
        (containers: ContainerInfo list)
        : VolumeSnapshot list =

        let rawMap =
            rawList
            |> List.map (fun r -> r.Name, r)
            |> Map.ofList

        let allNames =
            [ yield! rawList |> List.map (fun r -> r.Name)
              yield! dfMap |> Map.toList |> List.map fst ]
            |> List.distinct

        let firstNonEmpty values =
            values
            |> List.tryFind (fun s -> not (String.IsNullOrWhiteSpace(s)))
            |> Option.defaultValue ""

        allNames
        |> List.map (fun name ->
            let rawOpt = Map.tryFind name rawMap
            let inspOpt = Map.tryFind name inspectMap
            let dfOpt = Map.tryFind name dfMap
            let duOpt = Map.tryFind name duSizes

            let driver =
                firstNonEmpty [
                    inspOpt |> Option.map (fun i -> i.Driver) |> Option.defaultValue ""
                    rawOpt |> Option.map (fun r -> r.Driver) |> Option.defaultValue ""
                ]
            let mountpoint =
                firstNonEmpty [
                    inspOpt |> Option.map (fun i -> i.Mountpoint) |> Option.defaultValue ""
                    rawOpt |> Option.map (fun r -> r.Mountpoint) |> Option.defaultValue ""
                ]
            let createdAt =
                firstNonEmpty [
                    inspOpt |> Option.map (fun i -> i.CreatedAt) |> Option.defaultValue ""
                    rawOpt |> Option.map (fun r -> r.CreatedAt) |> Option.defaultValue ""
                ]
            let explicitAnon =
                inspOpt
                |> Option.bind (fun i -> i.ExplicitAnonymous)
                |> Option.orElse (rawOpt |> Option.bind (fun r -> r.ExplicitAnonymous))

            let isAnon = isAnonymousName name explicitAnon

            let sizeBytes, sizeText =
                match dfOpt |> Option.bind (fun d -> d.SizeBytes), duOpt with
                | Some b, _ ->
                    let txt = dfOpt |> Option.map (fun d -> d.SizeText) |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.defaultWith (fun () -> formatByteSize b)
                    Some b, txt
                | None, Some b -> Some b, formatByteSize b
                | None, None ->
                    let txt = dfOpt |> Option.map (fun d -> d.SizeText) |> Option.defaultValue ""
                    None, txt

            let links = dfOpt |> Option.bind (fun d -> d.Links)

            let attached =
                containers
                |> List.collect (fun c ->
                    c.Mounts
                    |> List.filter (mountMatchesVolume name)
                    |> List.map (fun m -> {
                        ContainerId = c.Id
                        ContainerName = c.Name
                        Destination = m.Destination
                        ReadWrite = m.ReadWrite
                    }))
                |> List.distinctBy (fun r -> r.ContainerId, r.Destination)

            let isUnused =
                List.isEmpty attached
                && (links |> Option.forall (fun n -> n <= 0))

            {
                Name = name
                Driver = driver
                Mountpoint = mountpoint
                CreatedAt = createdAt
                SizeBytes = sizeBytes
                SizeText = sizeText
                Links = links
                IsAnonymous = isAnon
                IsUnused = isUnused
                AttachedContainers = attached
            })
        |> List.sortWith (fun a b ->
            match a.SizeBytes, b.SizeBytes with
            | Some sa, Some sb when sa <> sb -> compare sb sa
            | Some _, None -> -1
            | None, Some _ -> 1
            | _ -> StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name))

    /// <summary>ボリューム容量の警戒レベル（-1: 不明, 0: 通常, 1: 警告 >=5GiB, 2: 危険 >=20GiB）を返す。</summary>
    let sizeLevel (sizeBytes: int64 option) : int =
        match sizeBytes with
        | Some bytes when bytes >= volumeDangerThresholdBytes -> 2
        | Some bytes when bytes >= volumeWarningThresholdBytes -> 1
        | Some _ -> 0
        | None -> -1

    /// <summary>コンテナにマウントされているボリュームの容量肥大化を診断所見として返す。</summary>
    let diagnoseContainerVolumes (containerMounts: ContainerMountInfo list) (volumes: VolumeSnapshot list) : DiagnosticFinding list =
        if List.isEmpty containerMounts || List.isEmpty volumes then []
        else
            let matched =
                volumes
                |> List.choose (fun vol ->
                    match containerMounts |> List.tryFind (mountMatchesVolume vol.Name) with
                    | Some m -> Some (vol, m, sizeLevel vol.SizeBytes)
                    | None -> None)
                |> List.filter (fun (_, _, lvl) -> lvl >= 1)

            if List.isEmpty matched then []
            else
                let maxLevel = matched |> List.map (fun (_, _, lvl) -> lvl) |> List.max
                let severity = if maxLevel >= 2 then FindingSeverity.Danger else FindingSeverity.Warning
                let evidence =
                    matched
                    |> List.truncate 3
                    |> List.map (fun (vol, m, _) ->
                        let sizeLabel = if String.IsNullOrWhiteSpace(vol.SizeText) then "unknown" else vol.SizeText
                        sprintf "Volume: %s (%s, mount: %s)" vol.Name sizeLabel m.Destination)
                [ {
                    Severity = severity
                    TitleKey = "ボリューム容量の肥大化"
                    SummaryKey = "マウント中のボリュームが大きなディスク容量を消費しています。"
                    EvidenceLines = evidence
                    Count = matched.Length
                } ]

/// <summary>ボリューム一覧画面で表示・バインディングするための観測可能モデル。</summary>
[<AllowNullLiteral>]
type VolumeInfo(initial: VolumeSnapshot) =
    inherit ObservableObject()

    let mutable snapshot = initial
    let mutable languageCode = "ja-JP"

    /// <summary>内部の最新ボリュームスナップショット。</summary>
    member _.Snapshot = snapshot

    /// <summary>ボリューム名（生名）。</summary>
    member _.Name = snapshot.Name

    /// <summary>一覧表示用の短縮ボリューム名。</summary>
    member _.DisplayName = VolumeParser.shortDisplayName snapshot.Name snapshot.IsAnonymous

    /// <summary>ドライバ名（local 等）。</summary>
    member _.Driver = if String.IsNullOrWhiteSpace(snapshot.Driver) then "--" else snapshot.Driver

    /// <summary>ホスト上のマウント先パス。</summary>
    member _.Mountpoint = if String.IsNullOrWhiteSpace(snapshot.Mountpoint) then "--" else snapshot.Mountpoint

    /// <summary>作成日時文字列。</summary>
    member _.CreatedAt = if String.IsNullOrWhiteSpace(snapshot.CreatedAt) then "--" else snapshot.CreatedAt

    /// <summary>判明している使用容量（バイト）。</summary>
    member _.SizeBytes = snapshot.SizeBytes

    /// <summary>表示用の使用容量テキスト。</summary>
    member _.SizeText =
        if String.IsNullOrWhiteSpace(snapshot.SizeText) then
            Localization.translate languageCode "不明" "Unknown"
        else
            snapshot.SizeText

    /// <summary>匿名ボリュームかどうか。</summary>
    member _.IsAnonymous = snapshot.IsAnonymous

    /// <summary>どのコンテナからも参照されていない未使用ボリュームかどうか。</summary>
    member _.IsUnused = snapshot.IsUnused

    /// <summary>紐付けられたコンテナ一覧。</summary>
    member _.AttachedContainers = snapshot.AttachedContainers

    /// <summary>紐付けコンテナが 1 件以上存在するか。</summary>
    member _.HasAttachedContainers = not (List.isEmpty snapshot.AttachedContainers)

    /// <summary>一覧表示用の紐付けコンテナ要約文字列。</summary>
    member _.AttachedContainersSummary =
        if List.isEmpty snapshot.AttachedContainers then
            Localization.translate languageCode "未使用" "Unused"
        else
            snapshot.AttachedContainers
            |> List.map (fun r ->
                if String.IsNullOrWhiteSpace(r.Destination) then r.ContainerName
                else sprintf "%s (%s)" r.ContainerName r.Destination)
            |> String.concat ", "

    /// <summary>未使用かつ容量 > 0（容量消費あり）かどうか。</summary>
    member _.IsUnusedWithSize =
        snapshot.IsUnused && (snapshot.SizeBytes |> Option.exists (fun b -> b > 0L))

    /// <summary>未使用で容量を消費している場合の警告表示文言。</summary>
    member this.UnusedWarningText =
        if this.IsUnusedWithSize then
            Localization.translate languageCode "未使用で容量を消費しています" "Unused volume consuming disk space"
        else
            ""

    /// <summary>容量の警戒レベル。</summary>
    member _.SizeLevel = VolumeParser.sizeLevel snapshot.SizeBytes

    /// <summary>容量警戒レベルに応じた表示ブラシ。</summary>
    member this.SizeBrush : IBrush =
        match this.SizeLevel with
        | 2 -> Brushes.Crimson
        | 1 -> Brushes.DarkGoldenrod
        | 0 when this.IsUnusedWithSize -> Brushes.DarkOrange
        | _ -> Brushes.Gray

    /// <summary>種別バッジ（名前付き / 匿名）。</summary>
    member _.KindBadge =
        if snapshot.IsAnonymous then
            Localization.translate languageCode "匿名" "Anonymous"
        else
            Localization.translate languageCode "名前付き" "Named"

    /// <summary>使用状況バッジ（未使用 / 使用中）。</summary>
    member _.UsageBadge =
        if snapshot.IsUnused then
            Localization.translate languageCode "未使用" "Unused"
        else
            Localization.translate languageCode "使用中" "In use"

    /// <summary>使用状況に応じた表示ブラシ。</summary>
    member _.UsageBrush : IBrush =
        if snapshot.IsUnused then Brushes.DarkGoldenrod else Brushes.SeaGreen

    /// <summary>現在の表示言語コード。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then
                this.NotifyDisplayProperties()

    member private this.NotifyDisplayProperties() =
        this.OnPropertyChanged(nameof this.Name)
        this.OnPropertyChanged(nameof this.DisplayName)
        this.OnPropertyChanged(nameof this.Driver)
        this.OnPropertyChanged(nameof this.Mountpoint)
        this.OnPropertyChanged(nameof this.CreatedAt)
        this.OnPropertyChanged(nameof this.SizeBytes)
        this.OnPropertyChanged(nameof this.SizeText)
        this.OnPropertyChanged(nameof this.IsAnonymous)
        this.OnPropertyChanged(nameof this.IsUnused)
        this.OnPropertyChanged(nameof this.AttachedContainers)
        this.OnPropertyChanged(nameof this.HasAttachedContainers)
        this.OnPropertyChanged(nameof this.AttachedContainersSummary)
        this.OnPropertyChanged(nameof this.IsUnusedWithSize)
        this.OnPropertyChanged(nameof this.UnusedWarningText)
        this.OnPropertyChanged(nameof this.SizeLevel)
        this.OnPropertyChanged(nameof this.SizeBrush)
        this.OnPropertyChanged(nameof this.KindBadge)
        this.OnPropertyChanged(nameof this.UsageBadge)
        this.OnPropertyChanged(nameof this.UsageBrush)

    /// <summary>最新のスナップショットで表示内容を更新する。</summary>
    member this.UpdateFrom(next: VolumeSnapshot) =
        if snapshot <> next then
            snapshot <- next
            this.NotifyDisplayProperties()
