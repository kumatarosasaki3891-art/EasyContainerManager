namespace EasyContainerManager

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open EasyContainerManager.Models

/// <summary>サポート対象のコンテナランタイム種別。</summary>
type ContainerRuntime =
    | Docker
    | Podman
    | Nerdctl

/// <summary>Flatpak サンドボックス内で動作しているかを判定し、ホスト側コマンド実行用の ProcessStartInfo を組み立てるモジュール。</summary>
module FlatpakHost =
    /// <summary>/.flatpak-info の存在によって Flatpak サンドボックス内実行かを判定する。</summary>
    let isRunningInFlatpak () =
        not (OperatingSystem.IsWindows()) && File.Exists("/.flatpak-info")

    /// <summary>実行環境に応じて直接または flatpak-spawn --host 経由の ProcessStartInfo を生成する。</summary>
    let createStartInfo (inFlatpak: bool) (executable: string) (workingDirectory: string option) (environmentVars: (string * string) list) (arguments: string list) : ProcessStartInfo =
        let startInfo = ProcessStartInfo(if inFlatpak then "flatpak-spawn" else executable)
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        if inFlatpak then
            startInfo.WorkingDirectory <- "/"
            startInfo.ArgumentList.Add("--host")
            match workingDirectory with
            | Some path when not (String.IsNullOrWhiteSpace(path)) ->
                startInfo.ArgumentList.Add("--directory=" + path)
            | _ -> ()
            for (key, value) in environmentVars do
                startInfo.ArgumentList.Add(sprintf "--env=%s=%s" key value)
            startInfo.ArgumentList.Add(executable)
            for argument in arguments do
                startInfo.ArgumentList.Add(argument)
        else
            for (key, value) in environmentVars do
                startInfo.Environment.[key] <- value
            match workingDirectory with
            | Some path -> startInfo.WorkingDirectory <- path
            | None -> ()
            for argument in arguments do
                startInfo.ArgumentList.Add(argument)
        startInfo

/// <summary>コンテナランタイムのコマンド解決とインストール検出を行うモジュール。</summary>
module ContainerRuntime =
    /// <summary>ランタイムに対応する CLI コマンド名を返す。</summary>
    let command = function
        | Docker -> "docker"
        | Podman -> "podman"
        | Nerdctl -> "nerdctl"

    /// <summary>UI 表示用のランタイム名を返す。</summary>
    let displayName = function
        | Docker -> "Docker"
        | Podman -> "Podman"
        | Nerdctl -> "nerdctl"

    /// <summary>サポートしているすべてのランタイム一覧。</summary>
    let all = [ Docker; Podman; Nerdctl ]

    /// <summary>環境変数 PATH（Windows ではユーザー・システム環境変数も含む）から実行ファイルの候補パスを生成する。</summary>
    let private pathCandidates executableName =
        let rawPaths =
            [ yield Environment.GetEnvironmentVariable("PATH")
              if OperatingSystem.IsWindows() then
                  yield Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)
                  yield Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ]
        let directories =
            rawPaths
            |> List.choose Option.ofObj
            |> List.collect (fun value ->
                value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList)
            |> List.map (fun dir ->
                try Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'))
                with _ -> dir.Trim().Trim('"'))
            |> List.filter (fun dir -> not (String.IsNullOrWhiteSpace(dir)))
            |> List.distinct
        let extensions = if OperatingSystem.IsWindows() then [ ".exe"; ""; ".cmd"; ".bat" ] else [ "" ]
        [ for directory in directories do
            for extension in extensions do
                let combined =
                    try Some(Path.Combine(directory, executableName + extension))
                    with _ -> None
                match combined with
                | Some path -> yield path
                | None -> () ]

    /// <summary>PATH と既定インストール先から実行ファイル候補パスの一覧を返す。</summary>
    let executableCandidates runtime =
        let name = command runtime
        let knownLocations =
            if OperatingSystem.IsWindows() then
                let localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                let localPrograms = Path.Combine(localAppData, "Programs")
                let programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                let programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                let programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                match runtime with
                | Podman ->
                    [ Path.Combine(localPrograms, "Podman", "podman.exe")
                      Path.Combine(localPrograms, "Podman", "bin", "podman.exe")
                      Path.Combine(localPrograms, "RedHat", "Podman", "podman.exe")
                      Path.Combine(localPrograms, "RedHat", "Podman", "bin", "podman.exe")
                      Path.Combine(programFiles, "RedHat", "Podman", "podman.exe")
                      Path.Combine(programFiles, "RedHat", "Podman", "bin", "podman.exe")
                      Path.Combine(programFiles, "Podman", "podman.exe")
                      Path.Combine(programFiles, "Podman", "bin", "podman.exe")
                      if not (String.IsNullOrWhiteSpace(programFilesX86)) then
                          Path.Combine(programFilesX86, "RedHat", "Podman", "podman.exe")
                          Path.Combine(programFilesX86, "RedHat", "Podman", "bin", "podman.exe")
                      Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "podman.exe")
                      Path.Combine(userProfile, "scoop", "shims", "podman.exe")
                      Path.Combine(programData, "chocolatey", "bin", "podman.exe") ]
                | Docker ->
                    [ Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe")
                      Path.Combine(localPrograms, "Docker", "Docker", "resources", "bin", "docker.exe") ]
                | Nerdctl ->
                    [ Path.Combine(localPrograms, "nerdctl", "nerdctl.exe")
                      Path.Combine(programFiles, "nerdctl", "nerdctl.exe")
                      Path.Combine(userProfile, "scoop", "shims", "nerdctl.exe")
                      Path.Combine(programData, "chocolatey", "bin", "nerdctl.exe") ]
            else
                [ "/usr/bin/" + name
                  "/usr/local/bin/" + name
                  "/opt/podman/bin/" + name
                  "/snap/bin/" + name ]
        (pathCandidates name @ knownLocations) |> List.distinct

    /// <summary>候補リストから最初に見つかった実在ファイルを返す（見つからなければフォールバック）。</summary>
    let resolveExecutable fallback candidates =
        candidates |> List.tryFind File.Exists |> Option.defaultValue fallback

    /// <summary>ランタイム実行ファイルのパスを環境（ホスト / Flatpak）に応じて解決して返す。</summary>
    let executableFor (inFlatpak: bool) runtime =
        if inFlatpak then command runtime
        else resolveExecutable (command runtime) (executableCandidates runtime)

    /// <summary>ランタイム実行ファイルのパスを解決して返す。</summary>
    let executable runtime = executableFor (FlatpakHost.isRunningInFlatpak()) runtime

    /// <summary>指定された実行ファイルパスで --version を試行し、ランタイムが応答するか判定する。</summary>
    let private probeExecutableFor (inFlatpak: bool) runtime candidatePath =
        try
            let startInfo = FlatpakHost.createStartInfo inFlatpak candidatePath None [] [ "--version" ]
            use engineProcess = new Process(StartInfo = startInfo)
            if engineProcess.Start() then
                let exited = engineProcess.WaitForExit(8000)
                if exited then
                    let output = engineProcess.StandardOutput.ReadToEnd()
                    engineProcess.ExitCode = 0 && not (String.IsNullOrWhiteSpace(output))
                else
                    try engineProcess.Kill(true) with _ -> ()
                    // 実在する既定インストール先バイナリが初回起動遅延等でタイムアウトした場合、ファイル実在をもって導入済みとみなす
                    if inFlatpak then false else File.Exists(candidatePath) && FileInfo(candidatePath).Length > 0L
            else false
        with _ -> false

    /// <summary>--version 実行によってランタイム CLI が利用可能か判定する。</summary>
    let isInstalled runtime =
        if FlatpakHost.isRunningInFlatpak() then
            probeExecutableFor true runtime (command runtime)
        else
            let existing = executableCandidates runtime |> List.filter File.Exists
            if List.isEmpty existing then
                probeExecutableFor false runtime (command runtime)
            else
                existing |> List.exists (probeExecutableFor false runtime)

    /// <summary>現在インストールされているランタイム一覧を検出する。</summary>
    let detectInstalled () = all |> List.filter isInstalled
    /// <summary>ランタイム選択画面が必要かを判定する（1件のみ検出された場合以外は選択が必要）。</summary>
    let requiresSelection installed = List.length installed <> 1
    /// <summary>検出されたランタイム一覧から自動確定できる起動時ランタイムを返す（1件のみ検出時以外は None）。</summary>
    let resolveStartupRuntime installed =
        match installed with
        | [ single ] -> Some single
        | _ -> None

/// <summary>Windows 版 Docker Desktop 実行ファイルの検索を行うモジュール。</summary>
module DockerDesktop =
    /// <summary>Docker Desktop の代表的なインストール先パス候補を生成する。</summary>
    let executableCandidates localApplicationData programFiles =
        [ Path.Combine(localApplicationData, "Programs", "DockerDesktop", "Docker Desktop.exe")
          Path.Combine(localApplicationData, "Programs", "Docker", "Docker", "Docker Desktop.exe")
          Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe") ]

    /// <summary>候補パスの中から最初に存在する Docker Desktop 実行ファイルを返す。</summary>
    let findExecutable candidates = candidates |> List.tryFind File.Exists

/// <summary>コンテナ inspect 1件分から抽出したメタデータ（ポート・マウント・状態情報）。</summary>
type ContainerInspectMeta = {
    Id: string
    Name: string
    Ports: ContainerPortBinding list
    Mounts: ContainerMountInfo list
    RestartCount: int
    OomKilled: bool
    ExitCode: int
    InspectStatus: string
}

/// <summary>コンテナ CLI の JSON / NDJSON 出力を解析するモジュール。</summary>
module ContainerOutput =
    /// <summary>JSON 配列または 1 行 1 JSON（NDJSON）形式の出力を JsonElement リストへ変換する。</summary>
    let objects (output: string) =
        let text = output.Trim()
        if String.IsNullOrWhiteSpace(text) then []
        elif text.StartsWith("[") then
            use document = JsonDocument.Parse(text)
            document.RootElement.EnumerateArray() |> Seq.map (fun item -> item.Clone()) |> Seq.toList
        else
            text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun line ->
                use document = JsonDocument.Parse(line)
                document.RootElement.Clone())
            |> Array.toList

    /// <summary>指定した候補プロパティ名のいずれかに一致する値を文字列として取得する。</summary>
    let valueAny names (element: JsonElement) =
        let property =
            element.EnumerateObject()
            |> Seq.tryFind (fun item -> names |> List.exists (fun name -> String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        match property with
        | Some item when item.Value.ValueKind = JsonValueKind.String -> item.Value.GetString() |> Option.ofObj |> Option.defaultValue ""
        | Some item when item.Value.ValueKind = JsonValueKind.Array ->
            item.Value.EnumerateArray()
            |> Seq.map (fun value -> if value.ValueKind = JsonValueKind.String then value.GetString() else value.ToString())
            |> String.concat ", "
        | Some item when item.Value.ValueKind <> JsonValueKind.Null -> item.Value.ToString()
        | _ -> ""

    /// <summary>単一プロパティ名の値を文字列として取得する。</summary>
    let value name element = valueAny [ name ] element

    /// <summary>JSON オブジェクトから大文字小文字を無視してプロパティ値を取得する。</summary>
    let private tryProperty (name: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then None
        else
            element.EnumerateObject()
            |> Seq.tryFind (fun (prop: JsonProperty) -> String.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun (prop: JsonProperty) -> prop.Value)

    /// <summary>Ports / PortBindings の JSON 要素から公開ポート一覧を抽出する。</summary>
    let private extractBindings (portsElement: JsonElement) =
        let results = ResizeArray<ContainerPortBinding>()
        let addBinding (hostIp: string) (hostPort: string) (containerPort: string) =
            let portText = if isNull hostPort then "" else hostPort.Trim()
            let cPortText = if isNull containerPort then "" else containerPort.Trim()
            if not (String.IsNullOrWhiteSpace(portText)) && not (String.IsNullOrWhiteSpace(cPortText)) then
                results.Add(ContainerPortBinding(hostIp, portText, cPortText))

        match portsElement.ValueKind with
        | JsonValueKind.Object ->
            for entry in portsElement.EnumerateObject() do
                let containerPort = entry.Name
                if entry.Value.ValueKind = JsonValueKind.Array then
                    for binding in entry.Value.EnumerateArray() do
                        if binding.ValueKind = JsonValueKind.Object then
                            let hostIp = valueAny [ "HostIp"; "HostIP"; "host_ip" ] binding
                            let hostPort = valueAny [ "HostPort"; "host_port" ] binding
                            addBinding hostIp hostPort containerPort
        | JsonValueKind.Array ->
            for item in portsElement.EnumerateArray() do
                if item.ValueKind = JsonValueKind.Object then
                    let hostIp = valueAny [ "host_ip"; "HostIp"; "HostIP" ] item
                    let hostPort = valueAny [ "host_port"; "HostPort" ] item
                    let containerPort = valueAny [ "container_port"; "ContainerPort" ] item
                    let protocol = valueAny [ "protocol"; "Protocol" ] item
                    let normalizedContainer =
                        if String.IsNullOrWhiteSpace(containerPort) then ""
                        elif containerPort.Contains("/") || String.IsNullOrWhiteSpace(protocol) then containerPort
                        else sprintf "%s/%s" containerPort protocol
                    addBinding hostIp hostPort normalizedContainer
        | _ -> ()

        results
        |> Seq.distinctBy (fun b -> b.Display)
        |> Seq.toList

    /// <summary>inspect JSON の 1 コンテナ要素から公開ポート一覧を取得する（NetworkSettings.Ports 優先、空なら HostConfig.PortBindings）。</summary>
    let portBindings (element: JsonElement) =
        let networkPorts =
            tryProperty "NetworkSettings" element
            |> Option.bind (tryProperty "Ports")
            |> Option.map extractBindings
            |> Option.defaultValue []
        if not (List.isEmpty networkPorts) then networkPorts
        else
            tryProperty "HostConfig" element
            |> Option.bind (tryProperty "PortBindings")
            |> Option.map extractBindings
            |> Option.defaultValue []

    /// <summary>inspect の JSON 出力から (ID, Name, PortBindings) の一覧を解析する。</summary>
    let inspectPorts (output: string) =
        objects output
        |> List.map (fun root ->
            let id = valueAny [ "Id"; "ID" ] root
            let name = (value "Name" root).TrimStart('/')
            id, name, portBindings root)

    /// <summary>inspect 結果からコンテナ ID または名前に対応する公開ポート一覧を検索する。</summary>
    let findContainerPorts (containerId: string) (containerName: string) (inspected: (string * string * ContainerPortBinding list) list) =
        let trimmedName = (if isNull containerName then "" else containerName.TrimStart('/')).Trim()
        let matchesId (inspectedId: string) =
            not (String.IsNullOrWhiteSpace(containerId))
            && not (String.IsNullOrWhiteSpace(inspectedId))
            && (inspectedId.StartsWith(containerId, StringComparison.OrdinalIgnoreCase)
                || containerId.StartsWith(inspectedId, StringComparison.OrdinalIgnoreCase))
        let matchesName (inspectedName: string) =
            not (String.IsNullOrWhiteSpace(trimmedName))
            && not (String.IsNullOrWhiteSpace(inspectedName))
            && String.Equals(inspectedName, trimmedName, StringComparison.OrdinalIgnoreCase)
        inspected
        |> List.tryFind (fun (id, name, _) -> matchesId id || matchesName name)
        |> Option.map (fun (_, _, ports) -> ports)
        |> Option.defaultValue []

    let private tryInt (names: string list) (element: JsonElement) : int option =
        names
        |> List.tryPick (fun n -> tryProperty n element)
        |> Option.bind (fun v ->
            match v.ValueKind with
            | JsonValueKind.Number ->
                match v.TryGetInt32() with
                | true, num -> Some num
                | _ -> None
            | JsonValueKind.String ->
                match Int32.TryParse(v.GetString()) with
                | true, num -> Some num
                | _ -> None
            | _ -> None)

    let private tryBool (names: string list) (element: JsonElement) : bool option =
        names
        |> List.tryPick (fun n -> tryProperty n element)
        |> Option.bind (fun v ->
            match v.ValueKind with
            | JsonValueKind.True -> Some true
            | JsonValueKind.False -> Some false
            | JsonValueKind.String ->
                match Boolean.TryParse(v.GetString()) with
                | true, b -> Some b
                | _ -> None
            | _ -> None)

    /// <summary>inspect JSON の 1 コンテナ要素からマウント一覧を抽出する。</summary>
    let extractMounts (element: JsonElement) : ContainerMountInfo list =
        match tryProperty "Mounts" element with
        | Some mountsElem when mountsElem.ValueKind = JsonValueKind.Array ->
            mountsElem.EnumerateArray()
            |> Seq.choose (fun m ->
                if m.ValueKind <> JsonValueKind.Object then None
                else
                    let mountType = valueAny [ "Type"; "type" ] m
                    let volumeName = valueAny [ "Name"; "name" ] m
                    let source = valueAny [ "Source"; "source" ] m
                    let destination = valueAny [ "Destination"; "destination"; "Target"; "target" ] m
                    let rw = tryBool [ "RW"; "rw" ] m |> Option.defaultValue true
                    if String.IsNullOrWhiteSpace(mountType) && String.IsNullOrWhiteSpace(volumeName) && String.IsNullOrWhiteSpace(destination) then
                        None
                    else
                        Some {
                            MountType = mountType
                            VolumeName = volumeName
                            Source = source
                            Destination = destination
                            ReadWrite = rw
                        })
            |> Seq.toList
        | _ -> []

    /// <summary>inspect の JSON 出力からポート・マウント・状態メタデータの一覧を解析する。</summary>
    let inspectContainerMeta (output: string) : ContainerInspectMeta list =
        objects output
        |> List.map (fun root ->
            let id = valueAny [ "Id"; "ID" ] root
            let name = (value "Name" root).TrimStart('/')
            let restartCount = tryInt [ "RestartCount" ] root |> Option.defaultValue 0
            let stateElem = tryProperty "State" root
            let oomKilled = stateElem |> Option.bind (tryBool [ "OOMKilled" ]) |> Option.defaultValue false
            let exitCode = stateElem |> Option.bind (tryInt [ "ExitCode" ]) |> Option.defaultValue 0
            let inspectStatus = stateElem |> Option.map (valueAny [ "Status" ]) |> Option.defaultValue ""
            {
                Id = id
                Name = name
                Ports = portBindings root
                Mounts = extractMounts root
                RestartCount = restartCount
                OomKilled = oomKilled
                ExitCode = exitCode
                InspectStatus = inspectStatus
            })

    /// <summary>inspect メタデータ一覧からコンテナ ID または名前に一致する要素を検索する。</summary>
    let findContainerMeta (containerId: string) (containerName: string) (inspected: ContainerInspectMeta list) : ContainerInspectMeta option =
        let trimmedName = (if isNull containerName then "" else containerName.TrimStart('/')).Trim()
        let matchesId (inspectedId: string) =
            not (String.IsNullOrWhiteSpace(containerId))
            && not (String.IsNullOrWhiteSpace(inspectedId))
            && (inspectedId.StartsWith(containerId, StringComparison.OrdinalIgnoreCase)
                || containerId.StartsWith(inspectedId, StringComparison.OrdinalIgnoreCase))
        let matchesName (inspectedName: string) =
            not (String.IsNullOrWhiteSpace(trimmedName))
            && not (String.IsNullOrWhiteSpace(inspectedName))
            && String.Equals(inspectedName, trimmedName, StringComparison.OrdinalIgnoreCase)
        inspected
        |> List.tryFind (fun item -> matchesId item.Id || matchesName item.Name)

    /// <summary>compose ps の JSON 出力から稼働数・総数・異常サービス一覧を集計する。</summary>
    let composeSnapshot output =
        let items = objects output
        let running = items |> List.filter (fun item -> value "State" item = "running") |> List.length
        let issues =
            items
            |> List.choose (fun item ->
                if value "State" item <> "running" || value "Health" item = "unhealthy" then
                    let service = value "Service" item
                    Some(if String.IsNullOrWhiteSpace(service) then value "Name" item else service)
                else None)
            |> List.distinct
        { Running = running; Total = items.Length; Issues = issues }

/// <summary>ローカルおよび SSH 経由での CLI 実行用 ProcessStartInfo を組み立てるモジュール。</summary>
module ContainerCommand =
    /// <summary>リモートシェル用に引数をシングルクォートで安全にエスケープする。</summary>
    let quoteShell (value: string) = "'" + value.Replace("'", "'\\''") + "'"

    /// <summary>実行環境（Flatpak / 直接）を指定してコンテナ CLI 実行用 ProcessStartInfo を生成する。</summary>
    let startInfoFor (inFlatpak: bool) runtime (connection: SshConnection option) workingDirectory arguments =
        match connection with
        | None ->
            let executable = ContainerRuntime.executableFor inFlatpak runtime
            let envVars =
                if runtime = Podman then
                    [ "PODMAN_COMPOSE_WARNING_LOGS", "false"
                      "NO_COLOR", "1"
                      "COMPOSE_ANSI", "never" ]
                else []
            FlatpakHost.createStartInfo inFlatpak executable workingDirectory envVars arguments
        | Some remote ->
            match remote.Validate() with
            | Error message -> invalidArg "connection" message
            | Ok () -> ()
            let command = "exec " + ContainerRuntime.command runtime + " " + (arguments |> List.map quoteShell |> String.concat " ")
            let remoteCommand =
                match workingDirectory with
                | Some path -> "cd -- " + quoteShell path + " && " + command
                | None -> command
            let keyArguments =
                if String.IsNullOrWhiteSpace(remote.PrivateKeyPath) then []
                else [ "-i"; remote.ExpandedKeyPath ]
            let sshArgs =
                [ yield! [ "-T"; "-o"; "BatchMode=yes"; "-o"; "StrictHostKeyChecking=yes"
                           "-o"; "ConnectTimeout=10"; "-o"; "ServerAliveInterval=15"; "-o"; "ServerAliveCountMax=2"
                           "-p"; string remote.Port ]
                  yield! keyArguments
                  yield! [ "-l"; remote.User; "--"; remote.Host; remoteCommand ] ]
            FlatpakHost.createStartInfo inFlatpak "ssh" None [] sshArgs

    /// <summary>ローカルまたは SSH 経由のコンテナ CLI 実行用 ProcessStartInfo を生成する。</summary>
    let startInfo runtime (connection: SshConnection option) workingDirectory arguments =
        startInfoFor (FlatpakHost.isRunningInFlatpak()) runtime connection workingDirectory arguments

    /// <summary>実行環境（Flatpak / 直接）を指定して SSH 疎通確認用の ProcessStartInfo を生成する。</summary>
    let sshTestStartInfoFor (inFlatpak: bool) runtime remote =
        let info = startInfoFor inFlatpak runtime (Some remote) None []
        info.ArgumentList.[info.ArgumentList.Count - 1] <- "echo OK"
        info

    /// <summary>SSH 疎通確認（echo OK）用の ProcessStartInfo を生成する。</summary>
    let sshTestStartInfo runtime remote =
        sshTestStartInfoFor (FlatpakHost.isRunningInFlatpak()) runtime remote

    /// <summary>コンテナ内で bash が利用可能か調べるコマンド引数を返す。</summary>
    let containerBashCheckArguments containerId = [ "exec"; containerId; "bash"; "-c"; "exit" ]

    /// <summary>ローカル実行時に作業ディレクトリが存在するかを事前検証する。</summary>
    let validateLocalWorkingDirectory (isRemote: bool) (workingDirectory: string option) (directoryExists: string -> bool) : Result<unit, string> =
        match isRemote, workingDirectory with
        | false, Some path when String.IsNullOrWhiteSpace(path) || not (directoryExists path) ->
            Error (sprintf "指定されたフォルダが見つかりません: %s" path)
        | _ -> Ok ()

    /// <summary>プロセス起動時の例外内容（フォルダ不在・CLI未導入・権限不足・その他）を判別して正確なエラーメッセージを返す。</summary>
    let describeStartException (runtime: ContainerRuntime) (isRemote: bool) (workingDirectory: string option) (directoryExists: string -> bool) (ex: exn) : string =
        match ex with
        | :? System.ComponentModel.Win32Exception as win32 ->
            match isRemote, workingDirectory with
            | false, Some path when String.IsNullOrWhiteSpace(path) || not (directoryExists path) ->
                sprintf "指定されたフォルダが見つかりません: %s" path
            | false, Some path when win32.NativeErrorCode = 267 || win32.NativeErrorCode = 20 ->
                sprintf "作業フォルダが無効です: %s (%s)" path (win32.Message.Trim())
            | _ ->
                match win32.NativeErrorCode with
                | 2 | 3 ->
                    if isRemote then "SSHコマンドが見つかりません"
                    else ContainerRuntime.displayName runtime + " CLIがインストールされていません"
                | 5 | 13 ->
                    if isRemote then "SSHの実行権限がありません"
                    else ContainerRuntime.displayName runtime + "実行権限がありません"
                | _ -> win32.Message.Trim()
        | :? UnauthorizedAccessException ->
            if isRemote then "SSHの実行権限がありません"
            else ContainerRuntime.displayName runtime + "実行権限がありません"
        | _ -> ex.Message.Trim()

/// <summary>対話型ターミナルの起動コマンド生成と起動処理を行うモジュール。</summary>
module TerminalCommand =
    /// <summary>コンテナ接続時に使用するシェル種別。</summary>
    type ContainerShell = Bash | Sh

    /// <summary>bash 存在確認の結果に応じて使用するシェル（bash または sh）を決定する。</summary>
    let shellForProbe = function
        | Ok _ -> Bash
        | Error _ -> Sh

    /// <summary>実行環境（Flatpak / 直接）を指定してターミナル実行コマンド、引数リスト、作業ディレクトリを決定する。</summary>
    let targetFor (inFlatpak: bool) runtime (connection: SshConnection option) workingDirectory container =
        let containerArgs (id, shell) =
            [ "exec"; "-it"; id; (match shell with Bash -> "bash" | Sh -> "sh") ]
        match connection with
        | None ->
            match container with
            | Some target -> ContainerRuntime.executableFor inFlatpak runtime, containerArgs target, workingDirectory
            | None -> (if OperatingSystem.IsWindows() then "powershell.exe" else "/bin/sh"), [], workingDirectory
        | Some remote ->
            match remote.Validate() with
            | Error message -> invalidArg "connection" message
            | Ok () -> ()
            let remoteCommand =
                match container, workingDirectory with
                | Some target, _ ->
                    "exec " + ContainerRuntime.command runtime + " "
                    + (containerArgs target |> List.map ContainerCommand.quoteShell |> String.concat " ")
                | None, Some path ->
                    "cd -- " + ContainerCommand.quoteShell path + " && exec \"${SHELL:-/bin/sh}\" -l"
                | None, None -> ""
            let keyArguments =
                if String.IsNullOrWhiteSpace(remote.PrivateKeyPath) then []
                else [ "-i"; remote.ExpandedKeyPath ]
            let arguments =
                [ yield! [ "-tt"; "-o"; "StrictHostKeyChecking=yes"; "-p"; string remote.Port ]
                  yield! keyArguments
                  yield! [ "-l"; remote.User; "--"; remote.Host ] ]
            "ssh", (if remoteCommand = "" then arguments else arguments @ [ remoteCommand ]), None

    /// <summary>ターミナルで実行するコマンド、引数リスト、作業ディレクトリを決定する。</summary>
    let target runtime (connection: SshConnection option) workingDirectory container =
        targetFor (FlatpakHost.isRunningInFlatpak()) runtime connection workingDirectory container

    /// <summary>PATH 上から指定された実行ファイル名を検索する。</summary>
    let private findExecutable name =
        let path = Environment.GetEnvironmentVariable("PATH") |> Option.ofObj |> Option.defaultValue ""
        path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun directory -> Path.Combine(directory.Trim('"'), name))
        |> Array.tryFind File.Exists

    /// <summary>Flatpak ホスト側に指定コマンドが存在するかを確認し、存在時はコマンド名をそのまま返す。</summary>
    let private findHostExecutable (name: string) : string option =
        try
            let info = FlatpakHost.createStartInfo true "sh" None [] [ "-c"; "command -v -- \"$1\" >/dev/null 2>&1"; "_"; name ]
            use proc = new Process(StartInfo = info)
            if proc.Start() then
                if proc.WaitForExit(3000) && proc.ExitCode = 0 then Some name
                else
                    try if not proc.HasExited then proc.Kill(true) with _ -> ()
                    None
            else None
        with _ -> None

    /// <summary>Linux（直接 / Flatpak ホスト経由）でターミナルを起動するための ProcessStartInfo を生成する。</summary>
    let linuxStartInfo (inFlatpak: bool) (terminalLookup: string -> string option) (executable: string) (arguments: string list) (directory: string option) : ProcessStartInfo =
        let terminal, termArgs =
            match terminalLookup "gnome-terminal", terminalLookup "konsole" with
            | Some terminal, _ ->
                let dirArgs =
                    match directory with
                    | Some path when not (String.IsNullOrWhiteSpace(path)) -> [ "--working-directory"; path ]
                    | _ -> []
                terminal, dirArgs @ [ "--" ]
            | None, Some terminal ->
                let dirArgs =
                    match directory with
                    | Some path when not (String.IsNullOrWhiteSpace(path)) -> [ "--workdir"; path ]
                    | _ -> []
                terminal, dirArgs @ [ "-e" ]
            | None, None -> failwith "gnome-terminal or konsole was not found."
        let fullArgs = termArgs @ (executable :: arguments)
        let startInfo = ProcessStartInfo(if inFlatpak then "flatpak-spawn" else terminal)
        startInfo.UseShellExecute <- false
        if inFlatpak then
            startInfo.WorkingDirectory <- "/"
            startInfo.ArgumentList.Add("--host")
            startInfo.ArgumentList.Add(terminal)
        for argument in fullArgs do
            startInfo.ArgumentList.Add(argument)
        startInfo

    /// <summary>OS に応じたターミナルアプリを起動してローカルまたは SSH セッションを開く。</summary>
    let start runtime connection workingDirectory container =
        try
            let inFlatpak = FlatpakHost.isRunningInFlatpak()
            let executable, arguments, directory = targetFor inFlatpak runtime connection workingDirectory container
            let startInfo =
                if OperatingSystem.IsWindows() then
                    let info = ProcessStartInfo()
                    info.UseShellExecute <- false
                    match findExecutable "wt.exe" with
                    | Some terminal ->
                        info.FileName <- terminal
                        directory |> Option.iter (fun path -> info.ArgumentList.Add("-d"); info.ArgumentList.Add(path))
                        info.ArgumentList.Add(executable)
                        for argument in arguments do info.ArgumentList.Add(argument)
                    | None ->
                        info.FileName <- "powershell.exe"
                        directory |> Option.iter (fun path -> info.WorkingDirectory <- path)
                        if executable <> "powershell.exe" || not arguments.IsEmpty then
                            let quotePowerShell (value: string) = "'" + value.Replace("'", "''") + "'"
                            info.ArgumentList.Add("-NoExit")
                            info.ArgumentList.Add("-Command")
                            info.ArgumentList.Add("& " + (executable :: arguments |> List.map quotePowerShell |> String.concat " "))
                    info
                elif OperatingSystem.IsLinux() then
                    let lookup = if inFlatpak then findHostExecutable else findExecutable
                    linuxStartInfo inFlatpak lookup executable arguments directory
                else
                    failwith "Opening a terminal is supported on Windows and Linux."
            use terminal = Process.Start(startInfo)
            if isNull terminal then Error "Could not start the terminal." else Ok ()
        with error -> Error error.Message

/// <summary>Docker / Podman / nerdctl の各 CLI 操作（ローカルおよび SSH 経由）を実行するクライアント。</summary>
type ContainerCli(?connection: SshConnection, ?runtime: ContainerRuntime) =
    let runtime = defaultArg runtime Docker
    let isRemote = connection.IsSome
    let inFlatpak = FlatpakHost.isRunningInFlatpak()
    let skipLocalDirCheck = isRemote || inFlatpak
    let makeStartInfo workingDirectory arguments = ContainerCommand.startInfo runtime connection workingDirectory arguments
    let jsonFormat = if runtime = Podman then "json" else "{{json .}}"

    /// <summary>指定ディレクトリと引数で CLI コマンドをキャンセル対応付きで非同期実行し、標準出力を返す。</summary>
    let executeAtWithCancel workingDirectory arguments (cancellationToken: System.Threading.CancellationToken option) = async {
        match ContainerCommand.validateLocalWorkingDirectory skipLocalDirCheck workingDirectory Directory.Exists with
        | Error message -> return Error message
        | Ok () ->
            try
                use dockerProcess = new Process(StartInfo = makeStartInfo workingDirectory arguments)
                dockerProcess.Start() |> ignore
                let outputTask = dockerProcess.StandardOutput.ReadToEndAsync()
                let errorTask = dockerProcess.StandardError.ReadToEndAsync()
                let! cancelled = async {
                    try
                        match cancellationToken with
                        | Some token -> do! dockerProcess.WaitForExitAsync(token) |> Async.AwaitTask
                        | None -> do! dockerProcess.WaitForExitAsync() |> Async.AwaitTask
                        return false
                    with :? OperationCanceledException ->
                        try
                            if not dockerProcess.HasExited then dockerProcess.Kill(true)
                        with _ -> ()
                        return true
                }
                if cancelled then
                    return Error "cancelled"
                else
                    let! output = outputTask |> Async.AwaitTask
                    let! error = errorTask |> Async.AwaitTask
                    if dockerProcess.ExitCode = 0 then return Ok output
                    else return Error (if String.IsNullOrWhiteSpace(error) then output.Trim() else error.Trim())
            with ex ->
                return Error (ContainerCommand.describeStartException runtime skipLocalDirCheck workingDirectory Directory.Exists ex)
    }

    /// <summary>指定ディレクトリと引数で CLI コマンドを非同期実行し、標準出力を返す。</summary>
    let executeAt workingDirectory arguments = executeAtWithCancel workingDirectory arguments None

    let execute arguments = executeAt None arguments

    /// <summary>出力を逐次コールバックへ通知しながら CLI コマンドを非同期実行する。</summary>
    let executeWithProgress workingDirectory arguments onProgress = async {
        match ContainerCommand.validateLocalWorkingDirectory skipLocalDirCheck workingDirectory Directory.Exists with
        | Error message -> return Error message
        | Ok () ->
            try
                use dockerProcess = new Process(StartInfo = makeStartInfo workingDirectory arguments)
                let errors = ConcurrentQueue<string>()
                let report (eventArgs: DataReceivedEventArgs) =
                    if not (isNull eventArgs.Data) then
                        errors.Enqueue(eventArgs.Data)
                        onProgress eventArgs.Data
                dockerProcess.OutputDataReceived.Add(report)
                dockerProcess.ErrorDataReceived.Add(report)
                dockerProcess.Start() |> ignore
                dockerProcess.BeginOutputReadLine()
                dockerProcess.BeginErrorReadLine()
                do! dockerProcess.WaitForExitAsync() |> Async.AwaitTask
                if dockerProcess.ExitCode = 0 then return Ok ()
                else return Error (errors.ToArray() |> String.concat Environment.NewLine)
            with ex ->
                return Error (ContainerCommand.describeStartException runtime skipLocalDirCheck workingDirectory Directory.Exists ex)
    }

    member _.IsRemote = connection.IsSome

    /// <summary>SSH リモート接続テスト（echo OK）を実行する。</summary>
    member _.TestSshConnection() = async {
        match connection with
        | None -> return Error "SSH接続先が指定されていません。"
        | Some remote ->
            try
                let info = ContainerCommand.sshTestStartInfo runtime remote
                use sshProcess = new Process(StartInfo = info)
                sshProcess.Start() |> ignore
                let stdout = sshProcess.StandardOutput.ReadToEndAsync()
                let stderr = sshProcess.StandardError.ReadToEndAsync()
                use timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                try
                    do! sshProcess.WaitForExitAsync(timeout.Token) |> Async.AwaitTask
                    let! output = stdout |> Async.AwaitTask
                    let! error = stderr |> Async.AwaitTask
                    if sshProcess.ExitCode = 0 && output.Trim() = "OK" then return Ok ()
                    else
                        let details = if String.IsNullOrWhiteSpace(error) then output.Trim() else error.Trim()
                        return Error (if String.IsNullOrWhiteSpace(details) then sprintf "SSH exited with code %d without output." sshProcess.ExitCode else details)
                with :? OperationCanceledException ->
                    if not sshProcess.HasExited then sshProcess.Kill(true)
                    return Error "SSH connection timed out after 15 seconds."
            with error -> return Error error.Message
    }

    /// <summary>すべてのコンテナ一覧と inspect 由来の公開ポートを取得して ContainerInfo リストとして返す。</summary>
    member _.ListContainers() = async {
        let! result = execute [ "ps"; "-a"; "--format"; "{{json .}}" ]
        match result with
        | Error message -> return Error message
        | Ok output ->
            try
                let parsed =
                    ContainerOutput.objects output
                    |> List.map (fun root ->
                        ContainerOutput.valueAny [ "ID"; "Id" ] root,
                        ContainerOutput.value "Names" root,
                        ContainerOutput.value "Status" root,
                        ContainerOutput.value "Image" root,
                        ContainerOutput.value "CreatedAt" root)
                let ids = parsed |> List.map (fun (id, _, _, _, _) -> id) |> List.filter (String.IsNullOrWhiteSpace >> not)
                let! inspectedMeta = async {
                    if List.isEmpty ids then return []
                    else
                        let! inspectResult = execute ("inspect" :: ids)
                        match inspectResult with
                        | Ok inspectOutput ->
                            try return ContainerOutput.inspectContainerMeta inspectOutput
                            with _ -> return []
                        | Error _ -> return []
                }
                let items =
                    parsed
                    |> List.map (fun (id, names, status, image, createdAt) ->
                        match ContainerOutput.findContainerMeta id names inspectedMeta with
                        | Some meta ->
                            ContainerInfo(
                                id,
                                names,
                                status,
                                image,
                                createdAt,
                                initialPorts = meta.Ports,
                                initialMounts = meta.Mounts,
                                initialRestartCount = meta.RestartCount,
                                initialOomKilled = meta.OomKilled,
                                initialExitCode = meta.ExitCode,
                                initialInspectStatus = meta.InspectStatus)
                        | None ->
                            ContainerInfo(id, names, status, image, createdAt))
                return Ok items
            with ex ->
                return Error (sprintf "コンテナ一覧を読み取れませんでした: %s" ex.Message)
    }

    /// <summary>指定コンテナに対して起動・停止・再起動・削除のいずれかを実行する。</summary>
    member _.RunContainerAction(action: string, containerId: string) =
        let command =
            match action with
            | "起動" -> "start"
            | "停止" -> "stop"
            | "削除" -> "rm"
            | _ -> "restart"
        execute [ command; containerId ]

    /// <summary>コンテナ内で bash が実行可能かを確認する。</summary>
    member _.CheckContainerBash(containerId: string) =
        execute (ContainerCommand.containerBashCheckArguments containerId)

    /// <summary>指定コンテナのログ（tail 指定時は末尾指定行数）を取得する。</summary>
    member _.GetLogs(containerId: string, ?tail: int) =
        match tail with
        | Some count when count > 0 -> execute [ "logs"; "--tail"; string count; containerId ]
        | _ -> execute [ "logs"; containerId ]

    /// <summary>指定コンテナの inspect JSON 情報を取得する。</summary>
    member _.GetInspect(containerId: string) = execute [ "inspect"; containerId ]

    /// <summary>info コマンドでコンテナエンジンの稼働状態を確認する。</summary>
    member _.GetEngineStatus() = async {
        let! result = execute [ "info" ]
        return result |> Result.map (fun _ -> ())
    }

    /// <summary>--version コマンドでコンテナエンジンのバージョン文字列を取得する。</summary>
    member _.GetEngineVersion() = execute [ "--version" ]

    /// <summary>Podman machine を起動する。</summary>
    member _.StartPodmanMachine() = execute [ "machine"; "start" ]

    /// <summary>CLI の存在確認（--version）とエンジン接続確認（info）を行い、失敗時は原因区分を返す。</summary>
    member _.CheckConnection() = async {
        let run isVersionProbe arguments = async {
            try
                use dockerProcess = new Process(StartInfo = makeStartInfo None arguments)
                dockerProcess.Start() |> ignore
                let output = dockerProcess.StandardOutput.ReadToEndAsync()
                let errors = dockerProcess.StandardError.ReadToEndAsync()
                use timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15.0))
                try
                    do! dockerProcess.WaitForExitAsync(timeout.Token) |> Async.AwaitTask
                    let! stdout = output |> Async.AwaitTask
                    let! stderr = errors |> Async.AwaitTask
                    if dockerProcess.ExitCode = 0 then return Ok ()
                    else
                        let details = if String.IsNullOrWhiteSpace(stderr) then stdout else stderr
                        let lower = details.ToLowerInvariant()
                        let state =
                            if lower.Contains("permission denied") || lower.Contains("access is denied") then "permission"
                            elif connection.IsSome && lower.Contains(ContainerRuntime.command runtime) && (lower.Contains("not found") || lower.Contains("command not found")) then "remoteRuntimeMissing"
                            elif inFlatpak && isVersionProbe && (lower.Contains("no such file or directory") || lower.Contains("not found")) then
                                if isRemote then "sshMissing" else "cliMissing"
                            else "unreachable"
                        return Error(state, details.Trim())
                with :? OperationCanceledException ->
                    if not dockerProcess.HasExited then dockerProcess.Kill(true)
                    return Error("unreachable", ContainerRuntime.displayName runtime + " command timed out after 15 seconds.")
            with
            | :? System.ComponentModel.Win32Exception as error when error.NativeErrorCode = 2 || error.NativeErrorCode = 3 ->
                return Error((if connection.IsSome then "sshMissing" else "cliMissing"), error.Message)
            | :? System.ComponentModel.Win32Exception as error when error.NativeErrorCode = 5 || error.NativeErrorCode = 13 ->
                return Error("permission", error.Message)
            | :? UnauthorizedAccessException as error -> return Error("permission", error.Message)
            | error -> return Error("unreachable", error.Message)
        }
        let! version = run true [ "--version" ]
        match version with
        | Error failure -> return Error failure
        | Ok () -> return! run false [ "info" ]
    }

    /// <summary>指定コンテナのログ追尾（logs --follow）プロセスを開始する。</summary>
    member _.StartLogFollow(containerId: string, onLine: string -> unit) =
        match ContainerCommand.validateLocalWorkingDirectory skipLocalDirCheck None Directory.Exists with
        | Error message -> Error message
        | Ok () ->
            try
                let startInfo = makeStartInfo None [ "logs"; "--follow"; "--tail"; "200"; containerId ]
                let dockerProcess = new Process(StartInfo = startInfo, EnableRaisingEvents = true)
                let report (eventArgs: DataReceivedEventArgs) =
                    if not (isNull eventArgs.Data) then onLine eventArgs.Data
                dockerProcess.OutputDataReceived.Add(report)
                dockerProcess.ErrorDataReceived.Add(report)
                dockerProcess.Start() |> ignore
                dockerProcess.BeginOutputReadLine()
                dockerProcess.BeginErrorReadLine()
                Ok dockerProcess
            with ex ->
                Error (ContainerCommand.describeStartException runtime skipLocalDirCheck None Directory.Exists ex)

    /// <summary>指定 Compose プロジェクトのログ追尾プロセスを開始する。</summary>
    member _.StartComposeLogFollow(folderPath: string, onLine: string -> unit) =
        let workingDirectory = Some folderPath
        match ContainerCommand.validateLocalWorkingDirectory skipLocalDirCheck workingDirectory Directory.Exists with
        | Error message -> Error message
        | Ok () ->
            try
                let startInfo = makeStartInfo workingDirectory [ "compose"; "logs"; "--follow"; "--tail"; "200" ]
                let dockerProcess = new Process(StartInfo = startInfo, EnableRaisingEvents = true)
                let report (eventArgs: DataReceivedEventArgs) =
                    if not (isNull eventArgs.Data) then onLine eventArgs.Data
                dockerProcess.OutputDataReceived.Add(report)
                dockerProcess.ErrorDataReceived.Add(report)
                dockerProcess.Start() |> ignore
                dockerProcess.BeginOutputReadLine()
                dockerProcess.BeginErrorReadLine()
                Ok dockerProcess
            with ex ->
                Error (ContainerCommand.describeStartException runtime skipLocalDirCheck workingDirectory Directory.Exists ex)

    /// <summary>ローカルまたはリモート上のイメージ一覧を取得する。</summary>
    member _.ListImages() = async {
        let! result = execute [ "image"; "ls"; "--format"; jsonFormat ]
        match result with
        | Error message -> return Error message
        | Ok output ->
            try
                let items =
                    ContainerOutput.objects output
                    |> List.map (fun root ->
                        ImageInfo(
                            ContainerOutput.valueAny [ "ID"; "Id" ] root,
                            ContainerOutput.value "Repository" root,
                            ContainerOutput.value "Tag" root,
                            ContainerOutput.value "Size" root,
                            ContainerOutput.value "CreatedAt" root))
                return Ok items
            with ex ->
                return Error (sprintf "イメージ一覧を読み取れませんでした: %s" ex.Message)
    }

    /// <summary>稼働中コンテナの CPU・メモリ・ネットワーク統計情報を取得する。</summary>
    member _.ListStats() = async {
        let arguments = [ "stats"; "--no-stream"; "--format"; jsonFormat ]
        let! result = execute (if runtime = Nerdctl then arguments @ [ "--no-trunc" ] else arguments)
        match result with
        | Error message -> return Error message
        | Ok output ->
            try
                let items =
                    ContainerOutput.objects output
                    |> List.map (fun root ->
                        ContainerStats(
                            ContainerOutput.valueAny [ "Name"; "name" ] root,
                            ContainerOutput.valueAny [ "CPUPerc"; "cpu_percent" ] root,
                            ContainerOutput.valueAny [ "MemUsage"; "mem_usage" ] root,
                            ContainerOutput.valueAny [ "NetIO"; "netio" ] root,
                            memoryPercent = ContainerOutput.valueAny [ "MemPerc"; "mem_percent" ] root))
                return Ok items
            with ex ->
                return Error (sprintf "統計情報を読み取れませんでした: %s" ex.Message)
    }

    /// <summary>指定イメージの pull を進捗通知付きで実行する。</summary>
    member _.PullImage(imageReference: string, onProgress: string -> unit) =
        executeWithProgress None [ "pull"; imageReference ] onProgress

    /// <summary>指定 ID のイメージを削除する。</summary>
    member _.RemoveImage(imageId: string) = execute [ "image"; "rm"; imageId ]

    /// <summary>Compose プロジェクトに対して起動・停止・再起動・ログ取得を実行する。</summary>
    member _.RunComposeAction(action: string, folderPath: string) =
        let command =
            match action with
            | "起動" -> [ "up"; "-d" ]
            | "停止" -> [ "down" ]
            | "ログ" -> [ "logs" ]
            | _ -> [ "restart" ]
        executeAt (Some folderPath) ("compose" :: command)

    /// <summary>Compose プロジェクトの稼働状態スナップショットを取得する。</summary>
    member _.GetComposeStatus(folderPath: string) = async {
        let! result = executeAt (Some folderPath) [ "compose"; "ps"; "-a"; "--format"; "json" ]
        match result with
        | Ok output ->
            try return Ok (ContainerOutput.composeSnapshot output)
            with error -> return Error error.Message
        | Error message -> return Error message
    }

    /// <summary>Compose プロジェクトのログを取得する（tail 指定時は末尾行数制限・キャンセル可）。</summary>
    member _.GetComposeLogs(folderPath: string, ?tail: int, ?cancellationToken: System.Threading.CancellationToken) =
        let arguments =
            match tail with
            | Some count when count > 0 -> [ "compose"; "logs"; "--tail"; string count ]
            | _ -> [ "compose"; "logs" ]
        executeAtWithCancel (Some folderPath) arguments cancellationToken

    /// <summary>進捗出力を通知しながら Compose の起動・停止・再起動を実行する。</summary>
    member _.RunComposeActionWithProgress(action: string, folderPath: string, onProgress: string -> unit) =
        let command =
            match action with
            | "起動" -> [ "up"; "-d" ]
            | "停止" -> [ "down" ]
            | _ -> [ "restart" ]
        executeWithProgress (Some folderPath) ("compose" :: command) onProgress

    /// <summary>ボリューム一覧（volume ls / inspect / system df -v）とコンテナ紐付けを統合取得する（読み取り専用）。</summary>
    member _.ListVolumes(containers: ContainerInfo list) : Async<Result<VolumeSnapshot list, string>> = async {
        let! lsResult = execute [ "volume"; "ls"; "--format"; jsonFormat ]
        match lsResult with
        | Error message -> return Error message
        | Ok lsOutput ->
            try
                let rawList = VolumeParser.parseVolumeLsJson lsOutput

                // 詳細（Mountpoint・CreatedAt・Driver 等）を volume inspect で補完
                let mutable inspectMap : Map<string, VolumeInspectRecord> = Map.empty
                let names = rawList |> List.map (fun r -> r.Name) |> List.distinct
                for chunk in names |> List.chunkBySize 50 do
                    if not (List.isEmpty chunk) then
                        let! inspRes = execute ("volume" :: "inspect" :: chunk)
                        match inspRes with
                        | Ok inspOut ->
                            for kv in VolumeParser.parseVolumeInspectJson inspOut do
                                inspectMap <- Map.add kv.Key kv.Value inspectMap
                        | Error _ -> ()

                // system df -v で容量・リンク数を取得（JSON 不可時はテキスト出力へフォールバック）
                let! dfJsonRes = execute [ "system"; "df"; "-v"; "--format"; jsonFormat ]
                let! dfMap = async {
                    match dfJsonRes with
                    | Ok dfOut ->
                        let parsed = VolumeParser.parseSystemDfJson dfOut
                        if not (Map.isEmpty parsed) then return parsed
                        else
                            let! dfTextRes = execute [ "system"; "df"; "-v" ]
                            return
                                match dfTextRes with
                                | Ok textOut -> VolumeParser.parseSystemDfText textOut
                                | Error _ -> Map.empty
                    | Error _ ->
                        let! dfTextRes = execute [ "system"; "df"; "-v" ]
                        return
                            match dfTextRes with
                            | Ok textOut -> VolumeParser.parseSystemDfText textOut
                            | Error _ -> Map.empty
                }

                // Linux ローカル環境では system df で容量が取れなかったマウント先に限り du -sb で補助計測
                // Windows + Docker Desktop ではボリューム実体が VM 内のためホスト側 du は実施しない
                let mutable duMap : Map<string, int64> = Map.empty
                if not isRemote && not (OperatingSystem.IsWindows()) then
                    let mutable probeCount = 0
                    for raw in rawList do
                        if probeCount < 30 then
                            let hasSize =
                                Map.tryFind raw.Name dfMap
                                |> Option.bind (fun d -> d.SizeBytes)
                                |> Option.isSome
                            if not hasSize then
                                let mountpoint =
                                    Map.tryFind raw.Name inspectMap
                                    |> Option.map (fun i -> i.Mountpoint)
                                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                    |> Option.defaultValue raw.Mountpoint
                                if not (String.IsNullOrWhiteSpace(mountpoint)) then
                                    probeCount <- probeCount + 1
                                    try
                                        let info = FlatpakHost.createStartInfo inFlatpak "du" None [] [ "-sb"; mountpoint ]
                                        use proc = new Process(StartInfo = info)
                                        if proc.Start() then
                                            if proc.WaitForExit(3000) && proc.ExitCode = 0 then
                                                let out = proc.StandardOutput.ReadToEnd().Trim()
                                                let parts = out.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                                                if parts.Length >= 1 then
                                                    match Int64.TryParse(parts.[0]) with
                                                    | true, bytes when bytes >= 0L ->
                                                        duMap <- Map.add raw.Name bytes duMap
                                                    | _ -> ()
                                            elif not proc.HasExited then
                                                try proc.Kill(true) with _ -> ()
                                    with _ -> ()

                let snapshots = VolumeParser.linkAndBuildVolumes rawList inspectMap dfMap duMap containers
                return Ok snapshots
            with ex ->
                return Error (sprintf "ボリューム一覧を読み取れませんでした: %s" ex.Message)
    }