<p align="center">
  <img src="SaveGuard/Assets/saveguard.png" width="96" alt="SaveGuard" />
</p>

<h1 align="center">SaveGuard</h1>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.zh-Hant.md">繁體中文</a> ·
  <a href="README.zh-Hans.md">简体中文</a> ·
  <b>日本語</b>
</p>

あらゆるゲームのセーブをバージョン管理してバックアップするツールです。ゲームのセーブフォルダーを監視し、セーブのたびに**フォルダー全体**をタイムスタンプ付きのスナップショットとして保存するので、いつでも以前のセーブにワンクリックで戻せます。バルダーズゲート3 のオナーモード（やり直し不可・セーブが上書きされる）のような状況のために作られていますが、どんなゲームでも使えます。

## 特長

- セーブのたびに**自動スナップショット**（デバウンス付き）、保持数は設定可能。
- **ワンクリック復元** — 復元前にまず `pre-restore` の安全スナップショットを取るので、間違った復元すら元に戻せます。
- **フォルダー丸ごとスナップショット** — セーブは複数ファイルで構成されがち（BG3 の `meta.lsf` は `.lsv` と対）なので、丸ごとコピーして整合性を保ちます。
- **セーブのプレビュー** — 各バックアップの隣にゲーム内スクリーンショットを表示（画像の種類は設定可能）。
- **トレイで常駐** — ウィンドウを閉じるとシステムトレイに格納され、監視はバックグラウンドで継続します。
- **自動アップデート** — インストール版は GitHub Releases から自動更新します。
- **4 言語対応** — English、繁體中文、简体中文、日本語をライブ切り替え。

## インストール

[Releases](https://github.com/HouseAlwaysWin/SaveGuard/releases) から最新の `Setup.exe` をダウンロードして実行してください。以降はアプリが自動的に更新します。

> 初回起動時、バルダーズゲート3 のセーブフォルダーが見つかると BG3 プロファイルが自動作成されます（`.lsv` でフィルター、25 件保持、自動監視）。**Save folder** を正しい場所に向け、**Back up now** を一度押して、バックアップ一覧が表示されることを確認してください。

**+ ゲームを追加** から他のゲームを追加し、そのゲームのセーブフォルダーを指定します。よくある場所：

- `Documents\My Games\<game>`
- `%LOCALAPPDATA%\<game>` または `%APPDATA%\<game>`
- Steam クラウド同期：`Steam\userdata\<id>\<appid>\remote`

**この種類のみで実行** を空欄にすると、あらゆる変更で実行されます（迷ったら空欄に）。

## 仕組み

- 各バックアップはセーブフォルダーの**完全コピー**で、`BackupRoot/<game>/<timestamp>/` に保存されます。ファイル単位の差分は取りません — セーブは「一式まとめて」でないと正しく復元できないためです。
- **バックアップエンジンは UI 非依存**（`Services/BackupEngine.cs`）：純粋な .NET ファイル IO、クロスプラットフォーム、ユニットテスト可能。
- 設定は `%APPDATA%\SaveGuard\profiles.json` に保存。バックアップの既定の出力先は `%APPDATA%\SaveGuard\Backups`。
- 対処済みのケース：書き込み途中・イベントの嵐（デバウンス）、ゲームがまだ掴んでいるファイル（共有読み取りでコピーし、本当にロックされたものはスキップ）、バックアップの無限再帰（`ValidateProfile` で防止）、復元中の余計なバックアップ（監視を一時停止）、ローテーションは実バックアップより先に `pre-restore` の安全コピーを削除。

## ソースからビルド

[.NET 10 SDK](https://dotnet.microsoft.com/download) が必要です。

```bash
dotnet run --project SaveGuard
```

## リリース

`v*` タグを push すると、GitHub Actions がビルド・パッケージ（Velopack）・GitHub Release の公開を行い、インストール版が自動更新します。[RELEASING.md](RELEASING.md) を参照してください。

## ライセンス

[MIT](LICENSE) © 2026 Martin Wang
