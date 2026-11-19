namespace EasyContainerManager.Models

open CommunityToolkit.Mvvm.ComponentModel

/// <summary>コンテナイメージの基本情報を保持するモデル。</summary>
[<AllowNullLiteral>]
type ImageInfo(id: string, repository: string, tag: string, size: string, created: string) =
    inherit ObservableObject()

    let mutable isOperating = false

    /// <summary>イメージの固有ID（ハッシュ値）。</summary>
    member _.Id = id

    /// <summary>リポジトリ名（例: nginx, alpine）。未タグ時は &lt;none&gt;。</summary>
    member _.Repository = repository

    /// <summary>イメージのタグ名（例: latest, 1.25-alpine）。</summary>
    member _.Tag = tag

    /// <summary>UI表示および操作参照用の一意な参照名（リポジトリ:タグ、またはID）。</summary>
    member _.Reference = if repository = "<none>" then id else sprintf "%s:%s" repository tag

    /// <summary>イメージのディスク占有サイズ文字列。</summary>
    member _.Size = size

    /// <summary>イメージの作成日時文字列。</summary>
    member _.Created = created

    /// <summary>このイメージに対する操作（削除など）が実行中かどうかを示す。</summary>
    member this.IsOperating
        with get () = isOperating
        and set value =
            if this.SetProperty(&isOperating, value) then
                this.OnPropertyChanged(nameof this.IsOperating)