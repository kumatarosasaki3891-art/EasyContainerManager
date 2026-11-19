namespace EasyContainerManager.Models

/// <summary>コンボボックスなどの選択肢（キーと表示ラベル）を表すモデル。</summary>
[<AllowNullLiteral>]
type ChoiceOption(key: string, label: string) =
    inherit CommunityToolkit.Mvvm.ComponentModel.ObservableObject()

    let mutable label = label

    /// <summary>選択肢を一意に識別するキー値。</summary>
    member _.Key = key

    /// <summary>画面に表示されるローカライズ済みラベル文字列。</summary>
    member this.Label
        with get () = label
        and set value = this.SetProperty(&label, value) |> ignore