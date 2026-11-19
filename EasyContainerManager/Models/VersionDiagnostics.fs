namespace EasyContainerManager.Models

open System
open System.Text.RegularExpressions
open EasyContainerManager

/// <summary>数値比較用に分解されたバージョン情報。</summary>
type ParsedVersion = {
    Raw: string
    Segments: int list
}

/// <summary>マスターデータを使用してエンジンおよびコンテナ内ランタイムのバージョン診断を行う純粋モジュール。</summary>
module VersionDiagnostics =

    let private dateTagRegex = Regex("""^\d{4}-\d{2}-\d{2}(?:$|[-_.])""")
    let private versionTagRegex = Regex("""^[vV]?(\d+(?:\.\d+)*)(?:$|[-+_].*)""")
    let private cliVersionRegex = Regex("""(\d+\.\d+(?:\.\d+)?)""")

    /// <summary>バージョン文字列をドット区切りの数値セグメントに解析する。日付タグや非数値タグは None を返す。</summary>
    let tryParseVersion (raw: string) : ParsedVersion option =
        if String.IsNullOrWhiteSpace(raw) then None
        else
            let trimmed = raw.Trim()
            if dateTagRegex.IsMatch(trimmed) then None
            else
                let m = versionTagRegex.Match(trimmed)
                if not m.Success then None
                else
                    let numericPart = m.Groups.[1].Value
                    let parts = numericPart.Split('.')
                    let parsed =
                        parts
                        |> Array.choose (fun p ->
                            match Int32.TryParse(p) with
                            | true, n when n >= 0 -> Some n
                            | _ -> None)
                        |> Array.toList
                    if parsed.Length <> parts.Length || List.isEmpty parsed then None
                    elif parsed.[0] >= 1000000 then
                        // 20261119 のような日付数値タグはバージョンとして扱わない
                        None
                    else
                        Some { Raw = numericPart; Segments = parsed }

    /// <summary>2つのバージョンを数値セグメント単位で比較する（a < b なら負、等しければ 0、a > b なら正）。</summary>
    let compareVersions (a: ParsedVersion) (b: ParsedVersion) : int =
        let maxLen = max a.Segments.Length b.Segments.Length
        let pad (segs: int list) =
            if segs.Length >= maxLen then segs
            else segs @ List.replicate (maxLen - segs.Length) 0
        let rec loop xs ys =
            match xs, ys with
            | [], [] -> 0
            | x :: xTail, y :: yTail ->
                let c = compare x y
                if c <> 0 then c else loop xTail yTail
            | _ -> 0
        loop (pad a.Segments) (pad b.Segments)

    /// <summary>バージョンが半開区間 [from, toExclusive) に入るかを判定する。</summary>
    let isInRange (version: ParsedVersion) (range: AffectedRange) : bool =
        match tryParseVersion range.From, tryParseVersion range.ToExclusive with
        | Some fromVer, Some toVer ->
            compareVersions version fromVer >= 0 && compareVersions version toVer < 0
        | _ -> false

    let private runtimeImageMap =
        Map.ofList [
            "nginx", "nginx"
            "nginx-unprivileged", "nginx"
            "php", "php"
            "mysql", "mysql"
            "mysql-server", "mysql"
            "mariadb", "mariadb"
            "postgres", "postgres"
            "postgresql", "postgres"
            "redis", "redis"
            "redis-stack", "redis"
            "httpd", "httpd"
            "apache", "httpd"
            "apache2", "httpd"
            "node", "node"
            "python", "python"
        ]

    /// <summary>イメージ参照文字列から（ランタイムID, タグ文字列, 解析済みバージョン）を抽出する。</summary>
    let tryExtractImageRuntimeAndVersion (rawImage: string) : (string * string * ParsedVersion) option =
        if String.IsNullOrWhiteSpace(rawImage) then None
        else
            let trimmed = rawImage.Trim()
            let withoutRegistry =
                match trimmed.LastIndexOf('/') with
                | -1 -> trimmed
                | idx -> trimmed.Substring(idx + 1)
            let withoutDigest =
                match withoutRegistry.IndexOf('@') with
                | -1 -> withoutRegistry
                | idx -> withoutRegistry.Substring(0, idx)
            match withoutDigest.IndexOf(':') with
            | -1 -> None
            | idx ->
                let imageName = withoutDigest.Substring(0, idx).Trim().ToLowerInvariant()
                let tag = withoutDigest.Substring(idx + 1).Trim()
                match Map.tryFind imageName runtimeImageMap, tryParseVersion tag with
                | Some runtimeId, Some version -> Some (runtimeId, tag, version)
                | _ -> None

    /// <summary>コンテナエンジンの --version 出力文字列からバージョンを抽出する。</summary>
    let tryParseEngineVersion (cliOutput: string) : ParsedVersion option =
        if String.IsNullOrWhiteSpace(cliOutput) then None
        else
            let m = cliVersionRegex.Match(cliOutput)
            if m.Success then tryParseVersion m.Groups.[1].Value
            else None

    /// <summary>指定基準日においてブランチがサポート終了（EOL 超過）かどうかを判定する。EOL 当日は対象外。</summary>
    let isBranchEol (referenceDate: DateOnly) (branch: RuntimeBranchRecord) : bool =
        match branch.Eol with
        | Some eolText ->
            match DateOnly.TryParse(eolText) with
            | true, eolDate -> referenceDate > eolDate
            | _ -> not branch.Support
        | None -> not branch.Support

    let private startsWithSegments (prefix: int list) (target: int list) : bool =
        if target.Length < prefix.Length then false
        else
            List.zip prefix (List.take prefix.Length target)
            |> List.forall (fun (a, b) -> a = b)

    let private tryFindBranch (version: ParsedVersion) (branches: RuntimeBranchRecord list) : (RuntimeBranchRecord * ParsedVersion) option =
        branches
        |> List.choose (fun b ->
            tryParseVersion b.Branch
            |> Option.bind (fun bv ->
                if startsWithSegments bv.Segments version.Segments then Some (b, bv)
                else None))
        |> List.sortByDescending (fun (_, bv) -> bv.Segments.Length)
        |> List.tryHead

    let private getSegmentAt (index: int) (segs: int list) =
        if index < segs.Length then segs.[index] else 0

    /// <summary>コンテナイメージのタグとマスターデータを照合してバージョン診断所見を返す。</summary>
    let diagnoseRuntimeImage (rawImage: string) (referenceDate: DateOnly) (snapshot: MasterDataSnapshot) : DiagnosticFinding list =
        match tryExtractImageRuntimeAndVersion rawImage with
        | None -> []
        | Some (runtimeId, tag, detectedVersion) ->
            match snapshot.Runtimes |> List.tryFind (fun r -> String.Equals(r.Id, runtimeId, StringComparison.OrdinalIgnoreCase)) with
            | None -> []
            | Some runtimeDef ->
                let defDate = MasterDataRepository.GetDefinitionDateText snapshot |> Option.defaultValue "unknown"
                let matchedBranchOpt = tryFindBranch detectedVersion runtimeDef.Branches

                // タグがブランチ指定のみ（例: nginx:1.30 や php:8.3）の場合は、そのブランチの最新版として範囲照合する
                let versionForAdvisoryRange =
                    match matchedBranchOpt with
                    | Some (branch, branchVer) when detectedVersion.Segments.Length <= branchVer.Segments.Length ->
                        tryParseVersion branch.Latest |> Option.defaultValue detectedVersion
                    | _ -> detectedVersion

                // 1. 最優先: 緊急 advisory 照合
                let matchingAdvisories =
                    runtimeDef.Advisories
                    |> List.filter (fun adv ->
                        adv.AffectedRanges |> List.exists (isInRange versionForAdvisoryRange))

                if not (List.isEmpty matchingAdvisories) then
                    // 最優先の 1 件にまとめて二重通知を防ぐ
                    let adv = matchingAdvisories.Head
                    let titleKey, summaryKey =
                        match adv.Requires with
                        | Some req when String.Equals(req, "http2", StringComparison.OrdinalIgnoreCase) ->
                            "緊急アドバイザリの対象バージョン (条件付き)",
                            "対象バージョンです。HTTP/2 を有効にしている場合は危険です。"
                        | Some _ ->
                            "緊急アドバイザリの対象バージョン (条件付き)",
                            "対象バージョンです。成立条件に該当する場合は危険です。"
                        | None ->
                            "緊急アドバイザリの対象バージョン",
                            "重大な脆弱性の対象バージョンです。修正版への更新を推奨します。"

                    let relatedText =
                        if List.isEmpty adv.RelatedIds then ""
                        else sprintf " (%s)" (String.concat ", " adv.RelatedIds)
                    let requiresText =
                        match adv.Requires with
                        | Some req -> sprintf " [requires: %s]" req
                        | None -> ""
                    let fixedText =
                        if List.isEmpty adv.FixedIn then "-"
                        else String.concat ", " adv.FixedIn
                    let sourceText = adv.Source |> Option.defaultValue ""

                    [ {
                        Severity = FindingSeverity.Danger
                        TitleKey = titleKey
                        SummaryKey = summaryKey
                        EvidenceLines = [
                            sprintf "Image: %s (Tag: %s, Detected: %s, Definitions: %s)" rawImage tag detectedVersion.Raw defDate
                            sprintf "Advisory: %s%s - %s%s" adv.Id relatedText adv.Summary requiresText
                            sprintf "Fixed in: %s%s" fixedText (if String.IsNullOrWhiteSpace(sourceText) then "" else sprintf " | Source: %s" sourceText)
                        ]
                        Count = 1
                    } ]
                else
                    // 2. ブランチ EOL / 古さ照合
                    match matchedBranchOpt with
                    | None -> []
                    | Some (branch, branchVer) ->
                        if isBranchEol referenceDate branch then
                            let eolLabel = branch.Eol |> Option.defaultValue "EOL"
                            [ {
                                Severity = FindingSeverity.Warning
                                TitleKey = "サポート終了 (EOL) ブランチの使用"
                                SummaryKey = "使用中のランタイムブランチはサポート期限を終了しています。保守中のブランチへの更新を推奨します。"
                                EvidenceLines = [
                                    sprintf "Image: %s (Tag: %s, Branch: %s, Definitions: %s)" rawImage tag branch.Branch defDate
                                    sprintf "Branch %s EOL: %s (Latest in branch: %s)" branch.Branch eolLabel branch.Latest
                                ]
                                Count = 1
                            } ]
                        else
                            // サポート中ブランチ内のパッチ遅れ（3パッチ以上）
                            let patchLagOpt =
                                if detectedVersion.Segments.Length > branchVer.Segments.Length then
                                    match tryParseVersion branch.Latest with
                                    | Some latestVer when compareVersions latestVer detectedVersion > 0 ->
                                        let idx = branchVer.Segments.Length
                                        let diff = getSegmentAt idx latestVer.Segments - getSegmentAt idx detectedVersion.Segments
                                        if diff >= 3 then Some latestVer else None
                                    | _ -> None
                                else None

                            match patchLagOpt with
                            | Some latestVer ->
                                [ {
                                    Severity = FindingSeverity.Info
                                    TitleKey = "ランタイムのパッチバージョン遅れ"
                                    SummaryKey = "使用中のブランチに新しいパッチバージョンが公開されています。"
                                    EvidenceLines = [
                                        sprintf "Image: %s (Detected: %s, Definitions: %s)" rawImage detectedVersion.Raw defDate
                                        sprintf "Branch %s latest patch: %s" branch.Branch latestVer.Raw
                                    ]
                                    Count = 1
                                } ]
                            | None ->
                                // 保守中ブランチ内で最新ブランチから 2 世代以上古い場合
                                let supportedBranches =
                                    runtimeDef.Branches
                                    |> List.filter (fun b -> not (isBranchEol referenceDate b))
                                    |> List.choose (fun b -> tryParseVersion b.Branch |> Option.map (fun bv -> b, bv))
                                    |> List.sortWith (fun (_, a) (_, b) -> compareVersions b a)

                                let branchIndex =
                                    supportedBranches
                                    |> List.tryFindIndex (fun (b, _) -> b.Branch = branch.Branch)
                                    |> Option.defaultValue 0

                                if branchIndex >= 2 && not (List.isEmpty supportedBranches) then
                                    let latestBranch, _ = supportedBranches.Head
                                    [ {
                                        Severity = FindingSeverity.Info
                                        TitleKey = "ランタイムの旧マイナーブランチ使用"
                                        SummaryKey = "より新しい安定版ブランチが公開されています。"
                                        EvidenceLines = [
                                            sprintf "Image: %s (Current branch: %s, Definitions: %s)" rawImage branch.Branch defDate
                                            sprintf "Latest stable branch: %s (latest: %s)" latestBranch.Branch latestBranch.Latest
                                        ]
                                        Count = 1
                                    } ]
                                else
                                    []

    /// <summary>コンテナエンジンのバージョンとマスターデータを照合して所見を返す。</summary>
    let diagnoseEngine (engineId: string) (engineVersionText: string) (snapshot: MasterDataSnapshot) : DiagnosticFinding list =
        match tryParseEngineVersion engineVersionText,
              snapshot.Engines |> List.tryFind (fun e -> String.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase)) with
        | Some detectedVer, Some engineDef ->
            match tryParseVersion engineDef.Latest with
            | Some latestVer when compareVersions latestVer detectedVer > 0 ->
                let defDate = MasterDataRepository.GetDefinitionDateText snapshot |> Option.defaultValue "unknown"
                let detMajor = getSegmentAt 0 detectedVer.Segments
                let latMajor = getSegmentAt 0 latestVer.Segments
                let detMinor = getSegmentAt 1 detectedVer.Segments
                let latMinor = getSegmentAt 1 latestVer.Segments
                let isSignificantlyBehind =
                    detMajor < latMajor || (detMajor = latMajor && (latMinor - detMinor) >= 2)
                if isSignificantlyBehind then
                    [ {
                        Severity = FindingSeverity.Info
                        TitleKey = "コンテナエンジンのバージョン遅れ"
                        SummaryKey = "使用中のコンテナエンジンに新しい安定版が公開されています。"
                        EvidenceLines = [
                            sprintf "Engine: %s (Detected: %s, Definitions: %s)" engineDef.DisplayName detectedVer.Raw defDate
                            sprintf "Latest stable: %s" engineDef.Latest
                        ]
                        Count = 1
                    } ]
                else []
            | _ -> []
        | _ -> []

    /// <summary>アプリの現在バージョンがマスターデータの最新バージョンより古いかを判定する。</summary>
    let isAppOutdated (currentVersion: string) (latestVersion: string) : bool =
        match tryParseVersion currentVersion, tryParseVersion latestVersion with
        | Some currentVer, Some latestVer -> compareVersions latestVer currentVer > 0
        | _ -> false
