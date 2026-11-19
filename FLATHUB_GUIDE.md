# Flathub / Flatpak パッケージ化ガイド (EasyContainerManager)

このドキュメントでは、**EasyContainerManager（簡単コンテナ管理）** を Flatpak パッケージとしてローカルでビルド・テストする手順、および Flathub へ申請する手順を説明します。

---

## 1. 作成・配置されたファイル一覧

| ファイル名 | 役割 |
| :--- | :--- |
| `io.github.kumatarosasaki3891_art.EasyContainerManager.yaml` | Flatpak ビルドマニフェスト（25.08 ランタイム + .NET 10 SDK 拡張） |
| `io.github.kumatarosasaki3891_art.EasyContainerManager.metainfo.xml` | AppStream メタ情報（Flathub ストア表示・日英対応） |
| `io.github.kumatarosasaki3891_art.EasyContainerManager.desktop` | デスクトップエントリ（アプリランチャー用） |
| `nuget-sources.json` | Flathub オフラインビルド用の NuGet パッケージ依存定義 |
| `LICENSE` | MIT ライセンスファイル |

### Application ID について
- **ID**: `io.github.kumatarosasaki3891_art.EasyContainerManager`
- GitHub ユーザー名のハイフン（`-`）は Flathub / AppStream ガイドラインに従いアンダースコア（`_`）へ置換しています。

### finish-args（付与権限）について
- `--socket=x11`, `--share=ipc`, `--device=dri`: Avalonia UI の GUI 描画。
- `--share=network`: マスター定義データ（GitHub Raw JSON）の取得および SSH 接続に必須。
- `--socket=ssh-auth`: SSH 接続時の `ssh-agent` 連携。
- `--talk-name=org.freedesktop.portal.Desktop`: フォルダ／秘密鍵ファイル選択ダイアログおよび既定ブラウザ起動のポータル連携。
- `--talk-name=org.freedesktop.Flatpak`: ホスト側のコンテナエンジン・ターミナル連携。

---

## 2. ローカルでのビルドと動作確認（Linux / WSL2 環境）

### 必要ツールのインストール (Ubuntu / Debian 例)
```bash
sudo apt update
sudo apt install flatpak flatpak-builder
flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo

# ランタイムと SDK、.NET 10 SDK 拡張機能のインストール
flatpak install flathub org.freedesktop.Platform//25.08
flatpak install flathub org.freedesktop.Sdk//25.08
flatpak install flathub org.freedesktop.Sdk.Extension.dotnet10//25.08
```

### ローカルソースからのビルド・インストール・起動確認
初期状態のマニフェストは `type: dir`（作業ディレクトリ直下）を参照しているため、そのままビルドできます。

```bash
flatpak-builder --user --install --force-clean build-dir io.github.kumatarosasaki3891_art.EasyContainerManager.yaml
flatpak run io.github.kumatarosasaki3891_art.EasyContainerManager
```

単体 `.flatpak` バンドルを出力したい場合:
```bash
flatpak-builder --repo=repo --force-clean build-dir io.github.kumatarosasaki3891_art.EasyContainerManager.yaml
flatpak build-bundle repo EasyContainerManager-1.0.0.flatpak io.github.kumatarosasaki3891_art.EasyContainerManager
```

---

## 3. Flathub への申請手順

1. GitHub でソースコードを公開し、リリースタグ（例：`linux-v1.0.0`）を Push します。
2. `io.github.kumatarosasaki3891_art.EasyContainerManager.yaml` の `sources` を `type: dir` から `type: git`（`url`, `tag`, `commit` 指定）へ切り替えます。
3. [flathub/flathub](https://github.com/flathub/flathub) をフォークし、`io.github.kumatarosasaki3891_art.EasyContainerManager` ブランチにマニフェストと `nuget-sources.json` を配置して `new-pr` ブランチへ PR を作成します。
