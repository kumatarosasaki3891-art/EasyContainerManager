namespace EasyContainerManager.Models

open System

// ============================================================================
// 運用メモ（文案共有）:
// 本ファイルの初心者向け FAQ 文案は、紹介サイト（https://sites.google.com/view/kuma-applications/）
// の 🐧 簡単コンテナ管理ページの FAQ アコーディオンと相互に流用する運用とする。
// どちらか一方の Q&A 文言を修正・追加した場合は、もう一方にも同じ内容を反映すること。
// ============================================================================

/// <summary>FAQ の 1 件分の質問と回答。</summary>
type FaqItem = {
    Id: string
    Question: string
    Answer: string
}

/// <summary>FAQ のカテゴリ定義。</summary>
type FaqCategory = {
    Id: string
    Icon: string
    Title: string
    Items: FaqItem list
}

/// <summary>UI 表示およびフィルタリング用の FAQ 項目。</summary>
type FaqDisplayItem = {
    Id: string
    CategoryId: string
    CategoryIcon: string
    CategoryTitle: string
    Question: string
    Answer: string
} with
    member this.HeaderText = sprintf "%s %s — %s" this.CategoryIcon this.CategoryTitle this.Question

/// <summary>初心者向け FAQ データ（日本語＋英語、他言語は英語へフォールバック）を提供するモジュール。</summary>
module FaqData =

    let private jaCategories : FaqCategory list =
        [
            {
                Id = "basics"
                Icon = "🐳"
                Title = "コンテナの基本"
                Items =
                    [
                        {
                            Id = "what-is-container"
                            Question = "コンテナってなに？"
                            Answer = "アプリと、その動作に必要な設定やライブラリを 1 つの「箱」にまとめて動かせる仕組みです。\nパソコン本体（ホスト）を汚しにくく、別の環境でも同じように動かせるのが特長です。"
                        }
                        {
                            Id = "image-vs-container"
                            Question = "イメージとコンテナの違いは？"
                            Answer = "「イメージ」はアプリを作るための設計図（ひな形）で、「コンテナ」はその設計図から起動した実体です。\n1 つのイメージから複数のコンテナを動かしたり、コンテナを削除してもイメージから作り直したりできます。"
                        }
                        {
                            Id = "engines-diff"
                            Question = "Docker・Podman・nerdctl の違いは？"
                            Answer = "どれも同じ形式のコンテナを動かせるエンジン（実行環境）です。\n・Docker：最も普及している標準的な環境\n・Podman：常駐デーモンなしでも動かせる互換環境\n・nerdctl：containerd を直接扱う軽量な CLI 環境\n簡単コンテナ管理は導入されているものを自動検出して切り替えられます。"
                        }
                    ]
            }
            {
                Id = "compose-volumes"
                Icon = "📦"
                Title = "Compose とボリューム"
                Items =
                    [
                        {
                            Id = "what-is-compose"
                            Question = "Compose ってなに？"
                            Answer = "Web サーバーとデータベースなど、複数のコンテナを 1 つの設定ファイル（compose.yaml など）でまとめて起動・停止できる仕組みです。\n「Compose」タブにフォルダを登録しておくと、プロジェクト単位でまとめて管理できます。"
                        }
                        {
                            Id = "what-is-volume"
                            Question = "ボリュームってなに？なぜ容量が増えるの？"
                            Answer = "ボリュームは、コンテナを削除してもデータベースやアップロードファイルなどのデータを消さずに残すための保管場所です。\nコンテナを作り直すたびに古い「未使用」ボリュームや匿名ボリュームが残り続けると、気づかないうちにディスク容量を圧迫します。\n※安全のため、本アプリの「ボリューム」タブは読み取り専用で、削除（prune）は実行しません。"
                        }
                        {
                            Id = "port-exposure-yellow"
                            Question = "公開ポートの黄色表示（0.0.0.0）ってなに？"
                            Answer = "「0.0.0.0」や「[::]」で公開されているポートは、同じネットワーク（LAN など）上の他の端末からもアクセスできる状態です。\n「127.0.0.1」はこのパソコンの中だけから接続できます。意図せず外部に開いていないか気づけるよう警告色で表示しています。"
                        }
                    ]
            }
            {
                Id = "diagnostics-logs"
                Icon = "🩺"
                Title = "診断と履歴"
                Items =
                    [
                        {
                            Id = "what-diagnostics-does"
                            Question = "サービス診断やバージョン診断では何をしていますか？"
                            Answer = "コンテナの状態（再起動の繰り返し・OOMKilled）や直近ログのエラー行、マウント中のボリューム容量の肥大化などを端末内で読み取り専用チェックします。\nさらに GitHub の公開定義データ（キャッシュ済み）と照合して、古いランタイムや EOL（サポート終了）ブランチも確認します。ユーザーデータを外部へ送信することはありません。"
                        }
                        {
                            Id = "where-history-saved"
                            Question = "操作履歴・アラート履歴はどこに保存されますか？"
                            Answer = "ユーザーが実行した起動・停止などの操作履歴と、定期更新時に検知した異常アラートは、PC 内の SQLite データベース（compose_projects.db）にローカル保存されます。\n10 秒ごとの定期更新自体は操作履歴に混ぜないため、後から「いつ誰が何をしたか」「いつ異常が起きたか」を追いやすくなっています。"
                        }
                    ]
            }
            {
                Id = "mobile-qr"
                Icon = "📱"
                Title = "スマホ連携と読み上げ"
                Items =
                    [
                        {
                            Id = "mobile-qr-safety"
                            Question = "スマホ連携（QRセッション）は安全ですか？"
                            Answer = "はい。ローカルサーバーは「スマホ連携 (QR)」画面で開始ボタンを押した時だけ起動し、常時待ち受けはしません。\n同じ自宅 Wi-Fi 内専用で外部公開はせず、最初に QR を読み取ったスマホ 1 台だけに 1 対 1 でセッションを固定します（無通信 15 分で自動切断）。スマホ画面は閲覧専用で、開始・停止・削除などの操作は一切行えません。"
                        }
                        {
                            Id = "mobile-qr-troubleshoot"
                            Question = "スマホから繋がらないときの確認ポイントは？"
                            Answer = "① スマホと PC が同じ Wi-Fi ルーターに繋がっているか（スマホが 4G/5G 回線になっていないか）確認してください。\n② IP アドレス選択欄で「192.168.x.x」などの実 Wi-Fi 用アドレスが選ばれているか確認してください（WSL や VPN の仮想アドレスでは繋がりません）。\n③ Windows ファイアウォールでプライベートネットワーク通信が許可されているかご確認ください。"
                        }
                        {
                            Id = "where-is-tts"
                            Question = "音声読み上げ（TTS）はどこで使えますか？"
                            Answer = "ブラウザ標準の音声読み上げ（Web Speech API）を、スマホ連携ページの最新アラートバナーおよびスマホ版 FAQ タブの各回答横（🔊 ボタン）に搭載しています。紹介サイトの FAQ ページでも利用できます。"
                        }
                    ]
            }
            {
                Id = "ssh-remote"
                Icon = "🔐"
                Title = "SSH リモート接続"
                Items =
                    [
                        {
                            Id = "ssh-credentials"
                            Question = "SSH 接続でパスワードや秘密鍵は保存されますか？"
                            Answer = "いいえ、保存しません。秘密鍵のファイルパスだけを設定に保持し、パスワードやパスフレーズはアプリ内に入力・保存しません。\n暗号化された鍵を使う場合は OS の ssh-agent に登録し、鍵パス欄を空欄にして接続してください。"
                        }
                        {
                            Id = "ssh-connect-fail"
                            Question = "SSH で接続できないときの確認ポイントは？"
                            Answer = "事前にご自身のターミナルから一度対象サーバーへ SSH 接続し、ホスト鍵の指紋を確認して known_hosts に登録してください。\n本アプリは安全のため StrictHostKeyChecking=yes と BatchMode=yes で接続するため、未登録ホストや対話入力は自動的に拒否されます。"
                        }
                    ]
            }
        ]

    let private enCategories : FaqCategory list =
        [
            {
                Id = "basics"
                Icon = "🐳"
                Title = "Container Basics"
                Items =
                    [
                        {
                            Id = "what-is-container"
                            Question = "What is a container?"
                            Answer = "A container packages an application together with its settings and libraries in an isolated box.\nIt keeps your PC clean and runs the same way across different machines."
                        }
                        {
                            Id = "image-vs-container"
                            Question = "What is the difference between an image and a container?"
                            Answer = "An image is the read-only blueprint of an application, while a container is a running or stopped instance created from that blueprint.\nYou can run multiple containers from one image or recreate a container anytime from its image."
                        }
                        {
                            Id = "engines-diff"
                            Question = "What is the difference between Docker, Podman, and nerdctl?"
                            Answer = "All three are container engines that run compatible containers:\n- Docker: The most widely used standard container engine.\n- Podman: A daemonless-capable alternative compatible with Docker commands.\n- nerdctl: A lightweight CLI for containerd.\nEasy Container Manager automatically detects what is installed and lets you switch."
                        }
                    ]
            }
            {
                Id = "compose-volumes"
                Icon = "📦"
                Title = "Compose & Volumes"
                Items =
                    [
                        {
                            Id = "what-is-compose"
                            Question = "What is Compose?"
                            Answer = "Compose lets you define and start/stop multiple related containers (such as a web app and its database) together using one file like compose.yaml.\nRegister a folder on the Compose tab to manage the whole stack in one place."
                        }
                        {
                            Id = "what-is-volume"
                            Question = "What is a volume, and why does its disk size grow?"
                            Answer = "A volume stores persistent data (such as database files or uploads) outside the container so data survives when containers are recreated.\nUnused or anonymous volumes left behind after recreating containers can quietly consume gigabytes of disk space.\nNote: For safety, the Volumes tab is strictly read-only and never runs delete or prune."
                        }
                        {
                            Id = "port-exposure-yellow"
                            Question = "Why are some published ports highlighted in yellow (0.0.0.0)?"
                            Answer = "Ports bound to 0.0.0.0 or [::] can be reached from other devices on your network (LAN).\nPorts bound to 127.0.0.1 are accessible only from this PC. Warning colors help you spot unintended network exposure."
                        }
                    ]
            }
            {
                Id = "diagnostics-logs"
                Icon = "🩺"
                Title = "Diagnostics & Logs"
                Items =
                    [
                        {
                            Id = "what-diagnostics-does"
                            Question = "What do Service Diagnostics and Version Diagnostics check?"
                            Answer = "They run strictly read-only local checks on container state (repeated restarts, OOMKilled), recent log error patterns, and large attached volumes.\nThey also check cached public definition data from GitHub for End-of-Life (EOL) runtimes and critical advisories. No user data is ever sent."
                        }
                        {
                            Id = "where-history-saved"
                            Question = "Where are operation logs and alert history stored?"
                            Answer = "User-triggered container/Compose actions and anomaly alerts detected during refresh are stored locally in SQLite (compose_projects.db).\nRoutine 10-second background refreshes are excluded from operation logs so real actions stay easy to trace."
                        }
                    ]
            }
            {
                Id = "mobile-qr"
                Icon = "📱"
                Title = "Phone QR & Read-Aloud"
                Items =
                    [
                        {
                            Id = "mobile-qr-safety"
                            Question = "Is the Phone QR Session safe?"
                            Answer = "Yes. The local web server starts only when you press Start session in the Phone QR dialog and never listens continuously in the background.\nIt works strictly on your local Wi-Fi (no internet exposure), locks 1-on-1 to the first phone that scans the QR code, times out after 15 minutes of inactivity, and is strictly read-only."
                        }
                        {
                            Id = "mobile-qr-troubleshoot"
                            Question = "What should I check if my phone cannot open the QR page?"
                            Answer = "1) Confirm your phone and PC are on the same Wi-Fi router (disable mobile 4G/5G).\n2) Select a real Wi-Fi LAN IP such as 192.168.x.x rather than a virtual WSL/VPN adapter.\n3) Ensure Windows Firewall permits private network access."
                        }
                        {
                            Id = "where-is-tts"
                            Question = "Where can I use text-to-speech read-aloud (🔊)?"
                            Answer = "Browser-based Web Speech API read-aloud (🔊) is available on live alert banners and on every FAQ answer in the Phone QR mobile page (as well as the website FAQ)."
                        }
                    ]
            }
            {
                Id = "ssh-remote"
                Icon = "🔐"
                Title = "SSH Remote Access"
                Items =
                    [
                        {
                            Id = "ssh-credentials"
                            Question = "Are SSH passwords or private keys saved?"
                            Answer = "No. Only the path to your private key file is saved. Passwords and passphrases are never stored or prompted.\nFor passphrase-protected keys, load them into ssh-agent and leave the private key path blank."
                        }
                        {
                            Id = "ssh-connect-fail"
                            Question = "What should I check if SSH connection fails?"
                            Answer = "Connect once manually from your terminal first to verify the host key fingerprint and record it in known_hosts.\nEasy Container Manager enforces StrictHostKeyChecking=yes and BatchMode=yes for safety."
                        }
                    ]
            }
        ]

    /// <summary>UI 言語コードに対応する FAQ 本文言語（ja-JP または en-US）を返す。</summary>
    let resolveFaqLanguage (languageCode: string) : string =
        if not (isNull languageCode) && languageCode.StartsWith("ja", StringComparison.OrdinalIgnoreCase) then
            "ja-JP"
        else
            "en-US"

    /// <summary>指定 UI 言語が日本語・英語以外のため英語フォールバック案内を表示すべきかを返す。</summary>
    let isEnglishFallback (languageCode: string) : bool =
        if String.IsNullOrWhiteSpace(languageCode) then false
        else
            not (languageCode.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            && not (languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))

    /// <summary>指定 FAQ 言語のカテゴリ一覧を返す。</summary>
    let categoriesForLanguage (faqLang: string) : FaqCategory list =
        if resolveFaqLanguage faqLang = "ja-JP" then jaCategories else enCategories

    /// <summary>カテゴリとキーワードで FAQ 項目を絞り込む。</summary>
    let filterItems (faqLang: string) (categoryIdOpt: string option) (query: string) : FaqDisplayItem list =
        let cats = categoriesForLanguage faqLang
        let terms =
            if String.IsNullOrWhiteSpace(query) then [||]
            else
                query.Trim().Split([| ' '; '\t'; '　' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun t -> t.ToLowerInvariant())

        cats
        |> List.filter (fun cat ->
            match categoryIdOpt with
            | Some cid when not (String.IsNullOrWhiteSpace(cid)) && cid <> "all" ->
                String.Equals(cat.Id, cid, StringComparison.OrdinalIgnoreCase)
            | _ -> true)
        |> List.collect (fun cat ->
            cat.Items
            |> List.map (fun item ->
                {
                    Id = item.Id
                    CategoryId = cat.Id
                    CategoryIcon = cat.Icon
                    CategoryTitle = cat.Title
                    Question = item.Question
                    Answer = item.Answer
                }))
        |> List.filter (fun item ->
            if terms.Length = 0 then true
            else
                let haystack = (item.CategoryTitle + " " + item.Question + " " + item.Answer).ToLowerInvariant()
                terms |> Array.forall (fun t -> haystack.Contains(t)))
