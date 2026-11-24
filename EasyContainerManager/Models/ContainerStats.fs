namespace EasyContainerManager.Models

open System
open System.Globalization
open Avalonia.Media
open CommunityToolkit.Mvvm.ComponentModel
open EasyContainerManager

/// <summary>CPU・メモリ使用率のしきい値判定と表示用色・ラベルを提供するモジュール。</summary>
module ResourceUsage =
    /// <summary>パーセント表記の文字列を数値へ解析する。</summary>
    let percentage (value: string) =
        match Double.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, number when Double.IsFinite(number) && number >= 0.0 -> Some number
        | _ -> None

    /// <summary>使用率を注意・警告のしきい値と比較してレベル（0〜2、不明は -1）を返す。</summary>
    let level caution warning value =
        match percentage value with
        | Some number when number >= warning -> 2
        | Some number when number >= caution -> 1
        | Some _ -> 0
        | None -> -1

    /// <summary>警戒レベルに対応する表示ブラシを返す。</summary>
    let brush level : IBrush =
        match level with
        | 2 -> Brushes.Crimson
        | 1 -> Brushes.DarkGoldenrod
        | 0 -> Brushes.SeaGreen
        | _ -> Brushes.Gray

    /// <summary>警戒レベルに対応するローカライズ済みステータス文字列を返す。</summary>
    let label languageCode level =
        let japanese, english =
            match level with
            | 2 -> "🔴 警告", "🔴 High"
            | 1 -> "🟡 注意", "🟡 Caution"
            | 0 -> "🟢 正常", "🟢 Normal"
            | _ -> "⚫ 不明", "⚫ Unknown"
        Localization.translate languageCode japanese english

/// <summary>コンテナ1件分の CPU・メモリ・ネットワーク統計情報を表すモデル。</summary>
type ContainerStats(container: string, initialCpu: string, initialMemory: string, initialNetwork: string, ?memoryPercent: string) =
    inherit ObservableObject()

    let mutable languageCode = "ja-JP"
    let mutable cpu = initialCpu
    let mutable memory = initialMemory
    let mutable network = initialNetwork
    let mutable memoryPercent = defaultArg memoryPercent "--"
    let mutable memoryParts = initialMemory.Split('/', StringSplitOptions.TrimEntries)

    /// <summary>対象コンテナの名前。</summary>
    member _.Container = container

    /// <summary>メモリ使用量（例: 50MiB）。</summary>
    member _.MemoryUsed = if memoryParts.Length > 0 then memoryParts.[0] else "--"

    /// <summary>メモリ制限値（例: 2GiB）。</summary>
    member _.MemoryLimit = if memoryParts.Length > 1 then memoryParts.[1] else "--"

    /// <summary>CPU 使用率文字列（例: 2.50%）。</summary>
    member this.Cpu
        with get () = cpu
        and set value =
            if this.SetProperty(&cpu, value) then
                this.OnPropertyChanged(nameof this.CpuLevel)
                this.OnPropertyChanged(nameof this.Level)
                this.OnPropertyChanged(nameof this.CpuStatus)
                this.OnPropertyChanged(nameof this.Status)
                this.OnPropertyChanged(nameof this.CpuBrush)
                this.OnPropertyChanged(nameof this.StatusBrush)

    /// <summary>メモリ使用量 / 制限値の文字列（例: 50MiB / 2GiB）。</summary>
    member this.Memory
        with get () = memory
        and set value =
            if this.SetProperty(&memory, value) then
                memoryParts <- memory.Split('/', StringSplitOptions.TrimEntries)
                this.OnPropertyChanged(nameof this.MemoryUsed)
                this.OnPropertyChanged(nameof this.MemoryLimit)

    /// <summary>ネットワーク送受信量（例: 1.2kB / 5.4kB）。</summary>
    member this.Network
        with get () = network
        and set value = this.SetProperty(&network, value) |> ignore

    /// <summary>メモリ使用率パーセント文字列（例: 25.0%）。</summary>
    member this.MemoryPercent
        with get () = memoryPercent
        and set value =
            if this.SetProperty(&memoryPercent, value) then
                this.OnPropertyChanged(nameof this.MemoryLevel)
                this.OnPropertyChanged(nameof this.Level)
                this.OnPropertyChanged(nameof this.MemoryStatus)
                this.OnPropertyChanged(nameof this.Status)
                this.OnPropertyChanged(nameof this.MemoryBrush)
                this.OnPropertyChanged(nameof this.StatusBrush)

    /// <summary>CPU 使用率の警戒レベル（0: 正常, 1: 注意, 2: 警告）。</summary>
    member _.CpuLevel = ResourceUsage.level 50.0 80.0 cpu

    /// <summary>メモリ使用率の警戒レベル（0: 正常, 1: 注意, 2: 警告）。</summary>
    member _.MemoryLevel = ResourceUsage.level 70.0 90.0 memoryPercent

    /// <summary>CPU とメモリの最大警戒レベル。</summary>
    member this.Level = max this.CpuLevel this.MemoryLevel

    /// <summary>CPU 使用率のローカライズ済みステータス文字列。</summary>
    member this.CpuStatus = ResourceUsage.label languageCode this.CpuLevel

    /// <summary>メモリ使用率のローカライズ済みステータス文字列。</summary>
    member this.MemoryStatus = ResourceUsage.label languageCode this.MemoryLevel

    /// <summary>総合警戒レベルのローカライズ済みステータス文字列。</summary>
    member this.Status = ResourceUsage.label languageCode this.Level

    /// <summary>CPU 警戒レベルに応じた表示色ブラシ。</summary>
    member this.CpuBrush = ResourceUsage.brush this.CpuLevel

    /// <summary>メモリ警戒レベルに応じた表示色ブラシ。</summary>
    member this.MemoryBrush = ResourceUsage.brush this.MemoryLevel

    /// <summary>総合警戒レベルに応じた表示色ブラシ。</summary>
    member this.StatusBrush = ResourceUsage.brush this.Level

    /// <summary>現在の表示言語コード（ja-JP, en-US 等）。</summary>
    member this.LanguageCode
        with get () = languageCode
        and set value =
            if this.SetProperty(&languageCode, value) then
                this.OnPropertyChanged(nameof this.CpuStatus)
                this.OnPropertyChanged(nameof this.MemoryStatus)
                this.OnPropertyChanged(nameof this.Status)

    /// <summary>別の ContainerStats から変更のあった項目を取り込む。</summary>
    member this.UpdateFrom(source: ContainerStats) =
        this.Cpu <- source.Cpu
        this.Memory <- source.Memory
        this.Network <- source.Network
        this.MemoryPercent <- source.MemoryPercent

/// <summary>発生中の警告を追跡し、同一異常の重複通知を防ぐクラス。</summary>
type AlertTracker() =
    let mutable active = Set.empty<string>

    /// <summary>通知済みのアクティブ警告状態をすべてリセットする。</summary>
    member _.Reset() = active <- Set.empty

    /// <summary>最新の警告一覧を反映し、新規発生した警告（キー・タイトル・メッセージ）を返す。</summary>
    member _.UpdateDetailed(enabled: bool, alerts: (string * string * string) list) =
        let fresh = if enabled then alerts |> List.filter (fun (key, _, _) -> not (active.Contains(key))) else []
        active <- if enabled then alerts |> List.map (fun (key, _, _) -> key) |> Set.ofList else Set.empty
        fresh

    /// <summary>最新の警告一覧を反映し、新規発生した通知のみを返す。</summary>
    member this.Update(enabled: bool, alerts: (string * string * string) list) =
        this.UpdateDetailed(enabled, alerts)
        |> List.map (fun (_, title, message) -> title, message)