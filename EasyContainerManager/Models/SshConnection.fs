namespace EasyContainerManager.Models

open System
open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Serialization
open CommunityToolkit.Mvvm.ComponentModel

/// <summary>SSH リモート接続プロファイルの設定値と検証ロジックを保持するモデル。</summary>
[<AllowNullLiteral>]
type SshConnection() =
    inherit ObservableObject()

    let mutable lastConnectedAt = ""
    let mutable connectionState = ""

    /// <summary>接続プロファイルを一意に識別する GUID 文字列。</summary>
    member val Id = Guid.NewGuid().ToString("N") with get, set

    /// <summary>UI 上に表示される接続プロファイル名。</summary>
    member val Name = "" with get, set

    /// <summary>接続先 SSH ホスト名または IP アドレス。</summary>
    member val Host = "" with get, set

    /// <summary>SSH ポート番号（既定値: 22）。</summary>
    member val Port = 22 with get, set

    /// <summary>接続先 SSH ユーザー名。</summary>
    member val User = "" with get, set

    /// <summary>SSH 秘密鍵ファイルのパス（空欄時は ssh-agent または既定鍵を使用）。</summary>
    member val PrivateKeyPath = "" with get, set

    /// <summary>リモートホストで使用するコンテナエンジン（"docker", "podman", "nerdctl"）。</summary>
    member val Engine = "" with get, set

    /// <summary>最終接続成功日時の文字列表記。</summary>
    member this.LastConnectedAt
        with get () = lastConnectedAt
        and set value = this.SetProperty(&lastConnectedAt, value) |> ignore

    /// <summary>現在の接続状態（"正常", "接続失敗", "SSH接続成功" 等、保存対象外）。</summary>
    [<JsonIgnore>]
    member this.ConnectionState
        with get () = connectionState
        and set value = this.SetProperty(&connectionState, value) |> ignore

    /// <summary>先頭のチルダ（~）をユーザーホームディレクトリに展開した秘密鍵パスを返す（空欄時は空文字）。</summary>
    member this.ExpandedKeyPath =
        let trimmed = if isNull this.PrivateKeyPath then "" else this.PrivateKeyPath.Trim()
        if String.IsNullOrWhiteSpace(trimmed) then ""
        elif trimmed.StartsWith("~/") || trimmed.StartsWith("~\\") then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed.Substring(2))
        else trimmed

    /// <summary>SSH 接続設定の各フィールドを検証し、不正があればエラーメッセージを返す（秘密鍵パス空欄は ssh-agent / 既定鍵として許可）。</summary>
    member this.Validate() =
        if String.IsNullOrWhiteSpace(this.Name) then Error "接続名を入力してください。"
        elif not (Regex.IsMatch(this.Host, "^[A-Za-z0-9][A-Za-z0-9.:-]*$")) then Error "ホスト名またはIPアドレスを確認してください。"
        elif not (Regex.IsMatch(this.User, "^[A-Za-z0-9_][A-Za-z0-9_.-]*$")) then Error "SSHユーザー名を確認してください。"
        elif this.Port < 1 || this.Port > 65535 then Error "ポートは1から65535で指定してください。"
        elif this.Engine <> "" && this.Engine <> "docker" && this.Engine <> "podman" && this.Engine <> "nerdctl" then Error "コンテナエンジンを確認してください。"
        elif not (String.IsNullOrWhiteSpace(this.PrivateKeyPath)) && not (File.Exists(this.ExpandedKeyPath)) then Error "秘密鍵ファイルが見つかりません。"
        else Ok ()