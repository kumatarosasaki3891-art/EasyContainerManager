namespace EasyContainerManager.Models

open System
open EasyContainerManager

/// <summary>サービスの分類カテゴリ。</summary>
type ServiceCategory =
    | Web
    | Database
    | Monitoring
    | Mail
    | FileShare
    | Ssh
    | Remote
    | AiLlm

/// <summary>サービスカテゴリに対応する絵文字アイコン。</summary>
module ServiceCategory =
    let emoji = function
        | Web -> "🌐"
        | Database -> "🗄"
        | Monitoring -> "📊"
        | Mail -> "📮"
        | FileShare -> "📁"
        | Ssh -> "🔐"
        | Remote -> "🖥"
        | AiLlm -> "🤖"

/// <summary>内部判定・矛盾検出用のサービス識別子。</summary>
type ServiceId =
    | WebServer
    | Mysql
    | Postgres
    | Redis
    | Mongo
    | Elasticsearch
    | Kibana
    | Prometheus
    | Ssh
    | Mail
    | Samba
    | Rdp
    | Ollama

/// <summary>推定されたコンテナ内サービスの情報。</summary>
type InferredService = {
    ServiceId: ServiceId
    Category: ServiceCategory
    ServiceName: string
    Port: int option
    Exposure: PortExposure option
}

/// <summary>ダイアログ表示用の推定候補（イメージ名から、ポートから）。</summary>
type InferenceCandidates = {
    FromImage: (ServiceId * ServiceCategory * string) option
    FromPorts: InferredService list
}

/// <summary>イメージ名と公開ポートからコンテナ内サービスを推定する純粋モジュール。</summary>
module ServiceInference =
    // ポート辞書: ポート番号 -> (ServiceId, Category, サービス名キー, 英語サービス名)
    let private portDictionary =
        [ 80, (WebServer, Web, "Webサーバー", "Web server")
          443, (WebServer, Web, "Webサーバー", "Web server")
          8080, (WebServer, Web, "Webサーバー", "Web server")
          8443, (WebServer, Web, "Webサーバー", "Web server")
          8000, (WebServer, Web, "Webサーバー", "Web server")
          3306, (Mysql, Database, "MySQL / MariaDB", "MySQL / MariaDB")
          5432, (Postgres, Database, "PostgreSQL", "PostgreSQL")
          6379, (Redis, Database, "Redis", "Redis")
          27017, (Mongo, Database, "MongoDB", "MongoDB")
          9200, (Elasticsearch, Database, "Elasticsearch", "Elasticsearch")
          5601, (Kibana, Monitoring, "Kibana", "Kibana")
          9090, (Prometheus, Monitoring, "Prometheus", "Prometheus")
          22, (ServiceId.Ssh, ServiceCategory.Ssh, "SSH", "SSH")
          25, (ServiceId.Mail, ServiceCategory.Mail, "メール (SMTP)", "Mail (SMTP)")
          445, (Samba, FileShare, "Samba", "Samba")
          3389, (Rdp, Remote, "RDP", "RDP")
          11434, (Ollama, AiLlm, "Ollama", "Ollama") ]
        |> Map.ofList

    // イメージ名キーワード辞書: キーワード -> (ServiceId, Category, サービス名)
    // 部分一致、大文字小文字無視。より具体的なキーワードを先頭に配置。
    let private imageKeywords =
        [ "mariadb", (Mysql, Database, "MariaDB")
          "mysql", (Mysql, Database, "MySQL")
          "postgres", (Postgres, Database, "PostgreSQL")
          "redis", (Redis, Database, "Redis")
          "mongo", (Mongo, Database, "MongoDB")
          "nginx", (WebServer, Web, "Nginx")
          "httpd", (WebServer, Web, "Apache")
          "apache", (WebServer, Web, "Apache")
          "elasticsearch", (Elasticsearch, Database, "Elasticsearch")
          "kibana", (Kibana, Monitoring, "Kibana")
          "wordpress", (WebServer, Web, "WordPress")
          "prometheus", (Prometheus, Monitoring, "Prometheus")
          "ollama", (Ollama, AiLlm, "Ollama") ]

    /// <summary>イメージ名文字列からタグやレジストリを除いたベース名を取得する。</summary>
    let extractImageBaseName (rawImage: string) =
        if String.IsNullOrWhiteSpace(rawImage) then ""
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
            let withoutTag =
                match withoutDigest.IndexOf(':') with
                | -1 -> withoutDigest
                | idx -> withoutDigest.Substring(0, idx)
            withoutTag.ToLowerInvariant()

    /// <summary>ポート文字列からポート番号をパースする。</summary>
    let private tryParsePort (portText: string) =
        if String.IsNullOrWhiteSpace(portText) then None
        else
            let clean = portText.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries).[0].Trim()
            match Int32.TryParse(clean) with
            | true, port -> Some port
            | _ -> None

    /// <summary>イメージ名とポートバインドから辞書に一致する候補要素を抽出する内部共通関数。</summary>
    let private extractMatches (image: string) (ports: ContainerPortBinding list) =
        let baseName = extractImageBaseName image

        // イメージ名からのキーワードマッチ
        let imageMatch =
            if String.IsNullOrWhiteSpace(baseName) then None
            else
                imageKeywords
                |> List.tryFind (fun (kw, _) -> baseName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                |> Option.map snd

        // 各ポートバインドから辞書にマッチしたエントリを抽出
        // (ServiceId, Category, jaName, enName, displayPort, exposure)
        let matchedPorts =
            ports
            |> List.choose (fun binding ->
                let cPortOpt = tryParsePort binding.ContainerPort
                let hPortOpt = tryParsePort binding.HostPort
                // まずコンテナ側ポートで照合、なければホスト側ポートで照合
                let matchOpt =
                    match cPortOpt with
                    | Some cp when Map.containsKey cp portDictionary ->
                        Some (cp, portDictionary.[cp])
                    | _ ->
                        match hPortOpt with
                        | Some hp when Map.containsKey hp portDictionary ->
                            Some (hp, portDictionary.[hp])
                        | _ -> None

                match matchOpt with
                | Some (matchedPort, (serviceId, category, jaName, enName)) ->
                    let displayPort = hPortOpt |> Option.defaultValue matchedPort
                    Some (serviceId, category, jaName, enName, displayPort, binding.Exposure)
                | None -> None)

        imageMatch, matchedPorts

    /// <summary>イメージ名とポートバインド一覧からコンテナ内サービスを推定する。</summary>
    let infer (image: string) (ports: ContainerPortBinding list) : InferredService list =
        let imageMatch, matchedPorts = extractMatches image ports

        match imageMatch with
        | Some (imgServiceId, imgCategory, imgName) ->
            // イメージが特定できた場合
            let distinctPortServices =
                matchedPorts |> List.map (fun (sId, _, _, _, _, _) -> sId) |> List.distinct

            // 矛盾チェック: 公開ポートの中にイメージと異なるサービスがあるか？
            let hasConflict =
                distinctPortServices |> List.exists (fun sId -> sId <> imgServiceId)

            if hasConflict then
                // イメージとポートが別サービスを指したら沈黙
                []
            else
                // イメージの代表ポートが公開されているか？
                let matchingPortBinding =
                    matchedPorts
                    |> List.tryFind (fun (sId, _, _, _, _, _) -> sId = imgServiceId)

                match matchingPortBinding with
                | Some (_, _, _, _, dispPort, exposure) ->
                    // 代表ポートが公開されているので確定表示
                    [ { ServiceId = imgServiceId
                        Category = imgCategory
                        ServiceName = imgName
                        Port = Some dispPort
                        Exposure = Some exposure } ]
                | None ->
                    // ポートが代表ポートと一致しない、またはポート未公開の場合は沈黙
                    []
        | None ->
            // イメージが無名（辞書にない自作イメージ等）の場合:
            // ポートだけで推定。代表ポート（DB系、Web等）があれば最大2件まで表示。重複はまとめる。
            let seen = System.Collections.Generic.HashSet<ServiceId>()
            let results = ResizeArray<InferredService>()

            for (serviceId, category, jaName, enName, dispPort, exposure) in matchedPorts do
                if seen.Add(serviceId) then
                    results.Add(
                        { ServiceId = serviceId
                          Category = category
                          ServiceName = jaName
                          Port = Some dispPort
                          Exposure = Some exposure }
                    )

            results |> Seq.truncate 2 |> Seq.toList

    /// <summary>ダイアログ表示用に、イメージ名からとポートからの候補をそれぞれ列挙する（矛盾判定なし）。</summary>
    let inferCandidates (image: string) (ports: ContainerPortBinding list) : InferenceCandidates =
        let imageMatch, matchedPorts = extractMatches image ports
        let seen = System.Collections.Generic.HashSet<ServiceId>()
        let portServices = ResizeArray<InferredService>()

        for (serviceId, category, jaName, enName, dispPort, exposure) in matchedPorts do
            if seen.Add(serviceId) then
                portServices.Add(
                    { ServiceId = serviceId
                      Category = category
                      ServiceName = jaName
                      Port = Some dispPort
                      Exposure = Some exposure }
                )

        { FromImage = imageMatch; FromPorts = portServices |> Seq.toList }

    /// <summary>イメージ候補の表示用テキストを整形する（例: 🌐 Nginx（イメージ：nginx:latest））。</summary>
    let formatCandidateImage (languageCode: string) (rawImage: string) (candidate: ServiceId * ServiceCategory * string) : string =
        let _, category, serviceName = candidate
        let emoji = ServiceCategory.emoji category
        let imgTemplate = Localization.translate languageCode "（イメージ：{0}）" " (Image: {0})"
        let imgSuffix = if String.IsNullOrWhiteSpace(rawImage) then "" else String.Format(imgTemplate, rawImage)
        sprintf "%s %s%s" emoji serviceName imgSuffix

    /// <summary>ポート候補の表示用テキストを整形する（例: 8080 → 🌐 Webサーバー（全インターフェースで公開））。</summary>
    let formatCandidatePort (languageCode: string) (service: InferredService) : string =
        let emoji = ServiceCategory.emoji service.Category
        let rawName = service.ServiceName
        let localizedName =
            match rawName with
            | "Webサーバー" -> Localization.translate languageCode "Webサーバー" "Web server"
            | "メール (SMTP)" -> Localization.translate languageCode "メール (SMTP)" "Mail (SMTP)"
            | other -> other

        let portNum = service.Port |> Option.defaultValue 0
        let exposureSuffix =
            match service.Exposure with
            | Some AllInterfaces -> Localization.translate languageCode "（全インターフェースで公開）" " (Public to all interfaces)"
            | _ -> ""

        sprintf "%d → %s %s%s" portNum emoji localizedName exposureSuffix

    /// <summary>推定サービスを現在の言語に応じて表示用文字列に整形する。</summary>
    let formatService (languageCode: string) (service: InferredService) : string =
        let emoji = ServiceCategory.emoji service.Category
        let rawName = service.ServiceName
        let localizedName =
            match rawName with
            | "Webサーバー" -> Localization.translate languageCode "Webサーバー" "Web server"
            | "メール (SMTP)" -> Localization.translate languageCode "メール (SMTP)" "Mail (SMTP)"
            | other -> other

        let portSuffix =
            match service.Port with
            | Some port ->
                match service.Exposure with
                | Some AllInterfaces ->
                    let template = Localization.translate languageCode "（ポート {0}・全インターフェースで公開）" " (Port {0} · Public to all interfaces)"
                    String.Format(template, port)
                | _ ->
                    let template = Localization.translate languageCode "（ポート {0}）" " (Port {0})"
                    String.Format(template, port)
            | None -> ""

        sprintf "%s %s%s" emoji localizedName portSuffix

    /// <summary>推定サービス一覧を現在の言語に応じて連結フォーマットする。</summary>
    let formatAll (languageCode: string) (services: InferredService list) : string =
        if List.isEmpty services then ""
        else
            services
            |> List.map (formatService languageCode)
            |> String.concat "   "
