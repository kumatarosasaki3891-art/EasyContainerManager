# EasyContainerManager（簡単コンテナ管理）

[![Releases](https://img.shields.io/github/v/release/kumatarosasaki3891-art/EasyContainerManager?label=Linux%20Release&color=0284c7)](https://github.com/kumatarosasaki3891-art/EasyContainerManager/releases)
[![Windows 10/11](https://img.shields.io/badge/Microsoft%20Store-Windows%2010%2F11-f97316)](#-ダウンロード--download)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[English](README_EN.md) | 日本語

ローカルおよびリモート（SSH）の Docker・Podman・nerdctl コンテナ、イメージ、Compose プロジェクトを 1 画面で管理できる Avalonia（F# / .NET 10）デスクトップアプリです。  
リアルタイムのリソース監視、公開ポートの露出強調表示、読み取り専用のコンテナ内サービス・バージョン診断も備えています。

> A lightweight cross-platform desktop manager for Docker, Podman, and nerdctl containers, images, and Compose projects—supporting local & SSH remote hosts, resource monitoring, and local read-only diagnostics.

---

## 📥 ダウンロード / Download

- **🐧 Linux版 (Flatpakバンドル)**: [GitHub Releases](https://github.com/kumatarosasaki3891-art/EasyContainerManager/releases)（下記「Flatpak パッケージのビルド」および [FLATHUB_GUIDE.md](FLATHUB_GUIDE.md) からビルドも可能）
- **🪟 Windows 10 / 11版**: [紹介サイト](https://sites.google.com/view/kuma-applications/) / Microsoft Store（公開準備中。下記手順からソースビルド・MSIX パッケージ作成も可能です）

---

## 📸 画面イメージ / Screenshots

<!-- ↓ GitHubの編集画面にスクリーンショット画像やデモ動画(.webm)をドラッグ＆ドロップして貼り付けてください -->
| メイン画面（コンテナ一覧・公開ポート表示） | リソース監視・サービス／バージョン診断 |
| :---: | :---: |
| *Screenshots coming soon* | *Screenshots coming soon* |

---

## 主な特徴

- **マルチコンテナエンジン自動検出 (`Docker` / `Podman` / `nerdctl`)**
  - 起動時に `docker`、`podman`、`nerdctl` の CLI を検出します。1 種類だけ導入されている場合はそのモードで起動し、複数ある場合や未検出時は選択画面を表示します。
- **公開ポートの全インターフェース露出チェック**
  - コンテナ一覧では `inspect` から取得した公開ポート（`0.0.0.0:8080->80/tcp` や `127.0.0.1:8080->80/tcp` など）を表示し、`0.0.0.0` / `[::]` の全ネットワーク公開バインドを警告色で強調します。
- **サービス推定・コンテナ内サービス診断 (`ServiceInference.fs` / `ServiceDiagnostics.fs`)**
  - イメージ名とポートからコンテナ内の推定サービスを提示し、ワンクリックで「サービス推定の詳細」や「コンテナ内サービス診断」を実行できます。
  - 再起動の繰り返し・`OOMKilled`・ログ内のエラーパターン（Nginx / Apache / MySQL / MariaDB / PostgreSQL / Redis / 汎用エラー）を根拠ログ行付きで読み取り専用診断します。
- **バージョン診断とローカル SQLite キャッシュ (`VersionDiagnostics.fs` / `MasterDataRepository.fs`)**
  - 公開マスターデータ（[`kuma-master-data`](https://github.com/kumatarosasaki3891-art/kuma-master-data)）と内蔵 SQLite キャッシュを用いて、エンジンやコンテナ内ランタイム（nginx / PHP / MySQL / MariaDB / PostgreSQL / Redis / httpd / Node.js / Python）のサポート終了（EOL）、パッチ遅れ、精選された緊急アドバイザリを照合します。
  - 診断処理は端末内で完結し、ユーザーデータや診断結果を外部へ送信しません。
- **リソース監視＆アプリ内警告通知**
  - コンテナ別の CPU・メモリ・通信量（Net I/O）および Compose プロジェクトの稼働数を監視し、閾値超過や異常時にアプリ内通知を行います。
- **セキュアな SSH リモート管理 (`StrictHostKeyChecking=yes`)**
  - ローカルの OpenSSH `ssh` クライアントを用いてリモートの Docker・Podman・nerdctl を管理します。秘密鍵の中身やパスワードは保存せず、`BatchMode=yes` で安全に接続します。
- **コンテナシェル・ホストターミナル連携**
  - ワンクリックでコンテナ内シェル（`bash` 確認後 `sh` にフォールバック）やホスト・Compose フォルダのターミナルを起動できます。
- **11言語対応＆テーマ切り替え (`Localization.fs`)**
  - 日本語、英語、簡体字中国語、繁体字中国語、韓国語、フランス語、ドイツ語、スペイン語、イタリア語、ポルトガル語、ロシア語の 11 言語と、ライト / ダーク / OS 設定追従テーマに対応しています。

---

## 使い方・各機能の詳細

### 基本動作・自動更新

- **自動更新**: 詳細欄の結果を更新完了時にまとめて反映し、内容が同じ場合は再描画の通知を行いません。エラーが継続する間は詳細欄の表示と開閉状態を保持し、正常復旧時に詳細をクリアします。
- **操作の排他制御**: 自動更新中もコンテナ・イメージ・Compose の操作ボタンは使用できます（更新完了後に実行し、対象が消えていた場合はスキップ）。接続先の変更と SSH 設定の操作は更新完了まで待機します。
- **Compose ログ**: Compose タブではプロジェクト選択時点で直近 50 行のログを自動表示し、「ログ表示」ボタンで全件を再取得します。

### 監視 (`Monitor` タブ)

- **CPU**: 50% 未満は正常、50% 以上は注意、80% 以上は警告。複数コアで 100% を超える値も扱います。
- **メモリ**: 70% 未満は正常、70% 以上は注意、90% 以上は警告。使用量・制限値・使用率を表示します。
- **Compose**: Docker モードでは `docker compose`、Podman モードでは `podman compose`、nerdctl モードでは `nerdctl compose` を使い、既存コンテナの稼働数／総数を表示します。停止・`unhealthy` のサービス名を警告欄に表示します（未作成サービスは総数に含みません。なお、`nerdctl` の Compose 出力には Health 情報がないため `unhealthy` の判定はできません）。
- **パス表示**: Compose 一覧のフォルダパスにマウスを重ねると、ツールチップでパス全体を表示します。
- **警告通知**: 「警告通知」を ON にすると、高 CPU・高メモリ・Compose 異常をアプリ内通知で表示します。同じ異常が継続するだけでは再通知せず、復旧後の再発で通知します。OS の通知センターへの通知・アプリ終了後の監視は行いません。

### バージョン診断とマスターデータ連携

- 「サービス診断」ダイアログでは、コンテナのログ・状態チェックに加えて、公開マスターデータ（`kuma-master-data`）に基づくエンジン（Docker / Podman / nerdctl）およびコンテナ内ランタイムのバージョン診断を行います。
- 照合は常にアプリ内蔵のローカル SQLite キャッシュに対して完結し、オフライン環境でも初期スナップショットやキャッシュでそのまま動作します。
- 通信は起動時（1日1回まで）または ℹ️（アプリ情報）ダイアログの「定義データを更新」押下時に、公開定義 JSON（`penguin/engines.json`・`penguin/runtimes.json`・`apps.json`）を GitHub から読み取り専用で取得する場合にのみ行われます。ユーザーデータや診断結果を外部へ送信することはありません。

### SSH 接続

ローカルに OpenSSH クライアントの `ssh` コマンド、接続先の Linux に選択したモードの CLI とコンテナエンジンが必要です。Podman モードでは接続先にも Compose プロバイダーが必要です。

1. **SSH接続** タブに接続名・ホスト・ポート・ユーザー・秘密鍵のパス（空欄の場合はデフォルト鍵・`ssh-agent` を使用）を入力し、接続先の Docker・Podman・nerdctl を選択します。秘密鍵の中身やパスワードは保存しません。
2. 初回は自分のターミナルから SSH 接続し、ホスト鍵の指紋を信頼できる方法で確認して `known_hosts` に登録してください。
3. 暗号化された鍵は `ssh-agent` に読み込ませ、秘密鍵のパスを空欄にして接続できます（Windows では OpenSSH `ssh-agent` サービスが起動しているか確認してください）。アプリからパスワード・パスフレーズの入力は求めません。
4. 「接続テスト」で確認し、「保存／更新」で登録後、画面上部の「接続先」で選択します。

- `BatchMode=yes` と `StrictHostKeyChecking=yes` で実行します（ホスト鍵検証を回避・自動承認しません）。接続ユーザーは `sudo` 入力なしで選択した CLI を実行できる必要があります。
- 接続テストは SSH の `echo OK` を実行し、コンテナエンジンの確認は接続先選択時に行います。登録済み接続先の最終接続日時は保存し、接続状態はアプリ起動中のみ表示します。
- Compose 登録は接続先別です。リモート選択時はサーバー上の絶対パスを入力してください（秘密鍵の参照ボタンはローカルファイルを選択します）。
- ローカルの Compose タブでは「エンジン」プルダウンで Docker・Podman・nerdctl を切り替えられます（エンジン別に独立保存。SSH 接続中は変更不可）。エンジン切り替え時や接続先変更時は選択行・表示ログ・入力欄をクリアし、ログ追尾を停止します。使用中の SSH 設定を編集・削除する場合は、いったんローカルへ切り替えてください。
- 登録したブラウザ URL は手元の既定ブラウザで開きます（SSH ポートフォワーディングは行いません）。

### コンテナ・ターミナル・エンジン別の補足

- **ターミナル連携**: 「コンテナに入る」は選択コンテナで `bash` を実行できるか確認してから端末を開き、失敗時は `sh` を試します。「ターミナル」は接続先ホストのシェルを開き、Compose の「ターミナル」は登録フォルダに移動して開きます。Windows では Windows Terminal（なければ PowerShell）、Linux では GNOME Terminal または Konsole を使用し、リモートでは OpenSSH で対話端末を開きます。
- **nerdctl モード**: PATH 上の `nerdctl` を使用します。containerd と CNI プラグイン・BuildKit 等は事前に設定してください。containerd の自動起動や WSL への自動接続は行いません。
- **Podman モード**: Compose 操作には `podman-compose` または `docker-compose` が必要です。Windows で `docker-compose` を使用する場合は Podman machine の API 転送（`win-sshproxy` と名前付きパイプ）も必要です。machine の再起動は実行中コンテナに影響するため、先に作業を保存してください。

---

## 起動・ビルド方法

### 1. ソースからの起動と検証（.NET 10 SDK）

```powershell
# アプリの起動
dotnet run --project EasyContainerManager/EasyContainerManager.fsproj

# 検証スクリプトの実行（Docker や SSH サーバーへの接続は不要）
dotnet fsi tests/VerifyV04.fsx
dotnet fsi tests/VerifyServiceInference.fsx
dotnet fsi tests/VerifyServiceDiagnostics.fsx
dotnet fsi tests/VerifyVersionDiagnostics.fsx
```

### 2. MSIX パッケージのビルド（Windows / Microsoft Store 配信用）

> **Note:** GitHub 公開用に [`EasyContainerManager/Packaging/msix/AppxManifest.xml`](EasyContainerManager/Packaging/msix/AppxManifest.xml) および [`EasyContainerManager/EasyContainerManager.fsproj`](EasyContainerManager/EasyContainerManager.fsproj) 内の Microsoft Store 識別情報・発行者情報は `XXX` に置き換えています。ご自身の環境でパッケージングする場合は適宜書き換えてください。

Windows 10/11 SDK（`makeappx.exe` / `signtool.exe`）がインストールされた環境でビルドできます：

```powershell
# Microsoft Store 提出用の未署名 MSIX（win-x64）を作成
powershell -ExecutionPolicy Bypass -File EasyContainerManager/build-msix.ps1

# x64 / arm64 をまとめた .msixbundle を作成する場合
powershell -ExecutionPolicy Bypass -File EasyContainerManager/build-msix.ps1 -Architectures x64,arm64 -Bundle

# ローカル環境でのテストインストール用にテスト証明書で署名する場合
powershell -ExecutionPolicy Bypass -File EasyContainerManager/build-msix.ps1 -SignTestCert

# MSBuild ターゲットから実行する場合
dotnet msbuild EasyContainerManager/EasyContainerManager.fsproj -t:BuildMsix -p:Configuration=Release
```

出力先は `EasyContainerManager/bin/msix/` です。

### 3. Flatpak パッケージのビルド（Linux 配信用）

`org.freedesktop.Platform//25.08` と `.NET 10 SDK` 拡張向けの設定ファイル一式を同梱しています。手順の詳細は [FLATHUB_GUIDE.md](FLATHUB_GUIDE.md) を参照してください。

```bash
flatpak-builder --user --install --force-clean build-dir io.github.kumatarosasaki3891_art.EasyContainerManager.yaml
flatpak run io.github.kumatarosasaki3891_art.EasyContainerManager
```

---

## ディレクトリ構成

```text
EasyContainerManager/
├── EasyContainerManager/                             # 本体アプリ (Avalonia UI / F# / net10.0)
│   ├── Assets/
│   │   └── MasterData/                               # 初期スナップショット定義 JSON (apps / engines / runtimes)
│   ├── Models/                                       # コンテナ・イメージ・Compose・SSH・診断・キャッシュモデル
│   ├── Packaging/msix/                               # Windows MSIX 用マニフェスト (識別情報は XXX でマスク済み) とアイコン
│   ├── ViewModels/                                   # MVVM ViewModel (MainViewModel.fs 等)
│   ├── Views/                                        # Avalonia ウィンドウ定義 (MainWindow / RuntimeSelectionWindow)
│   ├── AppSettings.fs                                # アプリ設定永続化
│   ├── ContainerCli.fs                               # Docker / Podman / nerdctl / SSH CLI 実行ラッパー
│   ├── Localization.fs                               # 11言語ローカライズ定義
│   ├── build-msix.ps1                                # Windows MSIX パッケージビルドスクリプト
│   └── EasyContainerManager.fsproj                   # プロジェクトファイル
├── tests/                                            # F# スクリプト検証スイート (Verify*.fsx)
├── FLATHUB_GUIDE.md                                  # Flathub / Flatpak パッケージングガイド
├── io.github.kumatarosasaki3891_art.EasyContainerManager.desktop
├── io.github.kumatarosasaki3891_art.EasyContainerManager.metainfo.xml
├── io.github.kumatarosasaki3891_art.EasyContainerManager.yaml
├── nuget-sources.json                                # Flatpak オフラインビルド用 NuGet 依存定義
├── EasyContainerManager.slnx                         # ソリューションファイル
└── LICENSE                                           # MIT ライセンス
```

---

## ライセンス

[MIT License](LICENSE)
