namespace EasyContainerManager.ViewModels

open CommunityToolkit.Mvvm.ComponentModel

/// <summary>すべての ViewModel の基底クラス。</summary>
[<AbstractClass>]
type ViewModelBase() =
    inherit ObservableObject()
