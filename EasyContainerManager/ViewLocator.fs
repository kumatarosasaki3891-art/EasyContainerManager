namespace EasyContainerManager

open System
open Avalonia.Controls
open Avalonia.Controls.Templates
open EasyContainerManager.ViewModels

/// <summary>ViewModel の型名から対応する View を解決して生成するテンプレート。</summary>
type ViewLocator() =
    interface IDataTemplate with
        
        /// <summary>指定された ViewModel に対応する View インスタンスを生成する。</summary>
        member this.Build(data) =
            if isNull data then
                null
            else    
                let name = data.GetType().FullName.Replace("ViewModel", "View", StringComparison.Ordinal)
                let typ = Type.GetType(name)
                if isNull typ then
                    upcast TextBlock(Text = sprintf "Not Found: %s" name)
                else
                    downcast Activator.CreateInstance(typ)
                
        /// <summary>対象データが ViewModelBase の派生型かを判定する。</summary>
        member this.Match(data) = data :? ViewModelBase
