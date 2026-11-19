#r "nuget: Avalonia, 12.1.5"
#r "nuget: CommunityToolkit.Mvvm, 8.4.2"
#r "nuget: Microsoft.Data.Sqlite, 11.0.0"
#load "../EasyContainerManager/Localization.fs"
#load "../EasyContainerManager/Models/ContainerStats.fs"
#load "../EasyContainerManager/Models/ContainerInfo.fs"
#load "../EasyContainerManager/Models/ServiceInference.fs"
#load "../EasyContainerManager/Models/ServiceDiagnostics.fs"
#load "../EasyContainerManager/Models/ImageInfo.fs"
#load "../EasyContainerManager/Models/SshConnection.fs"
#load "../EasyContainerManager/Models/ComposeProject.fs"
#load "../EasyContainerManager/Models/ComposeRepository.fs"
#load "../EasyContainerManager/Models/MasterDataRepository.fs"
#load "../EasyContainerManager/Models/VersionDiagnostics.fs"
#load "../EasyContainerManager/AppSettings.fs"
#load "../EasyContainerManager/ContainerCli.fs"
#load "../EasyContainerManager/Models/ChoiceOption.fs"
#load "../EasyContainerManager/ViewModels/ViewModelBase.fs"
#load "../EasyContainerManager/ViewModels/MainViewModel.fs"

open System
open EasyContainerManager
open EasyContainerManager.Models
open EasyContainerManager.ViewModels

let mutable checks = 0
let check name condition =
    if not condition then failwith (sprintf "FAILED: %s" name)
    checks <- checks + 1
    printfn "PASS: %s" name

// 1. 多言語辞書キーが全11言語で揃っているかの確認
for key in [ "推定サービス"; "イメージ名とポートからの推定です。実際と異なる場合があります"; "Webサーバー"; "メール (SMTP)"; "（ポート {0}）"; "（ポート {0}・全インターフェースで公開）"
             "サービス推定の詳細"; "イメージ名から"; "ポートから"; "推定できる材料がありません"; "候補の列挙です。実際の構成と異なる場合があります"; "閉じる"; "（全インターフェースで公開）"; "（イメージ：{0}）" ] do
    check (sprintf "Localization has all 11 translations for %s" key) (Localization.hasAllTranslations key)

// 2. テスト用 Compose（kuma-web1 / kuma-web2）の Webサーバー推定
let webBinding = [ ContainerPortBinding("0.0.0.0", "8080", "80/tcp") ]
let kumaWebServices = ServiceInference.infer "kuma-web1:latest" webBinding
check "kuma-web1 with port 8080 infers Web Server" (
    kumaWebServices.Length = 1
    && kumaWebServices.[0].Category = Web
    && kumaWebServices.[0].Port = Some 8080
    && kumaWebServices.[0].Exposure = Some AllInterfaces
)
let kumaWebTextJa = ServiceInference.formatAll "ja-JP" kumaWebServices
check "kuma-web1 format ja has 🌐 Web and Port 8080 with all-interfaces" (
    kumaWebTextJa = "🌐 Webサーバー（ポート 8080・全インターフェースで公開）"
)

// 3. DB コンテナ (mysql 等) で 🗄 が出ること
let mysqlBinding = [ ContainerPortBinding("0.0.0.0", "3306", "3306/tcp") ]
let mysqlServices = ServiceInference.infer "mysql:8.0" mysqlBinding
check "mysql container infers Database" (
    mysqlServices.Length = 1
    && mysqlServices.[0].Category = Database
    && mysqlServices.[0].ServiceName = "MySQL"
    && mysqlServices.[0].Port = Some 3306
)
let mysqlTextJa = ServiceInference.formatAll "ja-JP" mysqlServices
check "mysql format ja matches design memo" (
    mysqlTextJa = "🗄 MySQL（ポート 3306・全インターフェースで公開）"
)

// 4. LocalOnly ポートバインドの文面確認
let localWebBinding = [ ContainerPortBinding("127.0.0.1", "8080", "80/tcp") ]
let localWebServices = ServiceInference.infer "nginx:alpine" localWebBinding
check "nginx local-only format ja matches design memo" (
    ServiceInference.formatAll "ja-JP" localWebServices = "🌐 Nginx（ポート 8080）"
)
let localWebTextEn = ServiceInference.formatAll "en-US" localWebServices
check "nginx local-only format en" (
    localWebTextEn = "🌐 Nginx (Port 8080)"
)

// 5. 無名の自作イメージ＋無名ポートで何も出ないこと（沈黙の確認）
let unknownBinding = [ ContainerPortBinding("0.0.0.0", "9999", "9999/tcp") ]
let unknownServices = ServiceInference.infer "my-custom-image:v1" unknownBinding
check "unknown image and unknown port is silent" (List.isEmpty unknownServices)

let noPortsServices = ServiceInference.infer "my-custom-image:v1" []
check "unknown image without ports is silent" (List.isEmpty noPortsServices)

// 6. 矛盾ケース（nginx イメージで 3306 を公開）で出ないこと
let conflictBinding = [ ContainerPortBinding("0.0.0.0", "3306", "3306/tcp") ]
let conflictServices = ServiceInference.infer "nginx:latest" conflictBinding
check "conflict case (nginx with 3306) is silent" (List.isEmpty conflictServices)

// 7. 代表ポートのみの無名イメージ（DB系等）でポートから推定されること
let customDbBinding = [ ContainerPortBinding("127.0.0.1", "5432", "5432/tcp") ]
let customDbServices = ServiceInference.infer "my-backend-db:latest" customDbBinding
check "custom image with postgres port infers postgres" (
    customDbServices.Length = 1
    && customDbServices.[0].Category = Database
    && customDbServices.[0].ServiceName = "PostgreSQL"
    && customDbServices.[0].Port = Some 5432
)

// 8. 最大 2 件まで、重複はまとめる
let multiPortBinding = [
    ContainerPortBinding("0.0.0.0", "80", "80/tcp")
    ContainerPortBinding("0.0.0.0", "443", "443/tcp")
    ContainerPortBinding("127.0.0.1", "22", "22/tcp")
    ContainerPortBinding("127.0.0.1", "6379", "6379/tcp")
]
let multiServices = ServiceInference.infer "custom-app:v1" multiPortBinding
check "multi services deduplicated and truncated to at most 2" (
    multiServices.Length = 2
    && multiServices.[0].Category = ServiceCategory.Web
    && multiServices.[1].Category = ServiceCategory.Ssh
)

// 9. MainViewModel でのリアクティブ動作と表示制御
let vm = new MainViewModel(settingsOverride = AppSettings(), startAutomatically = false)
check "initially no container selected, HasInferredServices is false" (
    not vm.HasInferredServices
    && vm.InferredServicesText = ""
)

let webContainer = ContainerInfo("c1", "kuma-web1", "Up 2 hours", "kuma-web1:latest", "yesterday", initialPorts = webBinding)
let conflictContainer = ContainerInfo("c2", "broken-nginx", "Up 1 hour", "nginx:latest", "yesterday", initialPorts = conflictBinding)
let silentContainer = ContainerInfo("c3", "silent-app", "Up 1 hour", "custom:1.0", "yesterday", initialPorts = unknownBinding)

vm.Containers.Add(webContainer)
vm.Containers.Add(conflictContainer)
vm.Containers.Add(silentContainer)

vm.SelectedContainer <- webContainer
check "selecting web container shows inferred services" (
    vm.HasInferredServices
    && vm.InferredServicesText = "🌐 Webサーバー（ポート 8080・全インターフェースで公開）"
)

vm.SelectedContainer <- conflictContainer
check "selecting conflict container hides inferred services" (
    not vm.HasInferredServices
    && vm.InferredServicesText = ""
)

vm.SelectedContainer <- silentContainer
check "selecting silent container hides inferred services" (
    not vm.HasInferredServices
    && vm.InferredServicesText = ""
)

// 10. 言語切り替えで推定サービス表示・注記・ラベルが更新されること
vm.SelectedContainer <- webContainer
let enOption = vm.Languages |> Array.find (fun opt -> opt.Key = "en-US")
vm.Language <- enOption
printfn "Label: %s" vm.InferredServicesLabel
printfn "Note: %s" vm.InferredServicesNote
printfn "Text: %s" vm.InferredServicesText
check "switching language to English updates label, note, and text" (
    vm.InferredServicesLabel = "Likely services"
    && vm.InferredServicesNote = "Inferred from the image name and ports; may differ from the actual services."
    && vm.InferredServicesText = "🌐 Web server (Port 8080 · Public to all interfaces)"
)

let jaOption = vm.Languages |> Array.find (fun opt -> opt.Key = "ja-JP")
vm.Language <- jaOption
check "switching language back to Japanese restores Japanese strings" (
    vm.InferredServicesLabel = "推定サービス"
    && vm.InferredServicesNote = "イメージ名とポートからの推定です。実際と異なる場合があります"
    && vm.InferredServicesText = "🌐 Webサーバー（ポート 8080・全インターフェースで公開）"
)

// 11. コンテナ選択解除で非表示になること
vm.SelectedContainer <- null
check "clearing selection hides inferred services" (
    not vm.HasInferredServices
    && vm.InferredServicesText = ""
)

// 12. サービス推定ダイアログの候補列挙テスト
// ケース1: nginx イメージ＋8080ポート
let nginxCandidates = ServiceInference.inferCandidates "nginx:latest" [ ContainerPortBinding("0.0.0.0", "8080", "80/tcp") ]
check "nginx candidate has FromImage" nginxCandidates.FromImage.IsSome
check "nginx candidate FromImage is Nginx" (
    match nginxCandidates.FromImage with
    | Some (_, ServiceCategory.Web, name) -> name = "Nginx"
    | _ -> false
)
check "nginx candidate has FromPorts Web server" (
    nginxCandidates.FromPorts.Length = 1
    && nginxCandidates.FromPorts.[0].Category = ServiceCategory.Web
)

// ケース2: 全部入り想定（apache イメージ＋80と3306公開）
// 通常表示は沈黙、候補列挙では両節に併記
let allInOnePorts = [
    ContainerPortBinding("0.0.0.0", "80", "80/tcp")
    ContainerPortBinding("0.0.0.0", "3306", "3306/tcp")
]
let allInOneInfer = ServiceInference.infer "httpd:latest" allInOnePorts
check "all-in-one container normal infer is silent" (List.isEmpty allInOneInfer)

let allInOneCandidates = ServiceInference.inferCandidates "httpd:latest" allInOnePorts
check "all-in-one candidate FromImage is Apache" (
    match allInOneCandidates.FromImage with
    | Some (_, ServiceCategory.Web, name) -> name = "Apache"
    | _ -> false
)
check "all-in-one candidate FromPorts contains both Web and MySQL" (
    allInOneCandidates.FromPorts.Length = 2
    && allInOneCandidates.FromPorts |> List.exists (fun s -> s.ServiceId = WebServer)
    && allInOneCandidates.FromPorts |> List.exists (fun s -> s.ServiceId = Mysql)
)

// ケース3: 無名イメージ＋無名ポートで材料なし
let noMaterialCandidates = ServiceInference.inferCandidates "custom-app:1.0" [ ContainerPortBinding("0.0.0.0", "9999", "9999/tcp") ]
check "unknown image and port has no candidates" (
    noMaterialCandidates.FromImage.IsNone
    && List.isEmpty noMaterialCandidates.FromPorts
)

// 13. ViewModel でのダイアログ操作と表示検証
let allInOneContainer = ContainerInfo("c4", "all-in-one", "Up", "httpd:latest", "today", initialPorts = allInOnePorts)
vm.Containers.Add(allInOneContainer)
vm.SelectedContainer <- allInOneContainer

check "all-in-one container has selected container" vm.HasSelectedContainer
check "all-in-one container has image candidate" vm.HasImageCandidate
check "all-in-one container has port candidates" vm.HasPortCandidates
check "all-in-one container does not have no-candidates" (not vm.HasNoInferenceCandidates)
check "image candidate text contains Apache and image name" (
    vm.ImageCandidateText.Contains("Apache") && vm.ImageCandidateText.Contains("httpd:latest")
)
check "port candidates contains both ports" (
    vm.PortCandidatesTexts.Length = 2
    && vm.PortCandidatesTexts.[0].Contains("80 →")
    && vm.PortCandidatesTexts.[1].Contains("3306 →")
)

// ダイアログ開閉
check "dialog initially closed" (not vm.IsInferenceDialogOpen)
vm.OpenInferenceDialog()
check "dialog opened" vm.IsInferenceDialogOpen
vm.CloseInferenceDialog()
check "dialog closed" (not vm.IsInferenceDialogOpen)

// 無名イメージの材料なしコンテナ選択
vm.SelectedContainer <- silentContainer
check "silent container has no candidates" vm.HasNoInferenceCandidates
check "silent container does not have image candidate" (not vm.HasImageCandidate)
check "silent container does not have port candidates" (not vm.HasPortCandidates)

// 言語切り替えでダイアログ文言が切り替わること
vm.SelectedContainer <- allInOneContainer
vm.Language <- enOption
check "switching to English updates dialog title and note" (
    vm.InferenceDialogTitle = "Service inference details"
    && vm.InferenceImageHeader = "From image name"
    && vm.InferencePortsHeader = "From ports"
    && vm.InferenceDialogNote = "These are candidates only and may differ from the actual setup."
    && vm.CloseLabel = "Close"
    && vm.ImageCandidateText.Contains("(Image: httpd:latest)")
)
vm.Language <- jaOption

(vm :> IDisposable).Dispose()

printfn "\nAll %d service inference tests passed successfully!" checks
