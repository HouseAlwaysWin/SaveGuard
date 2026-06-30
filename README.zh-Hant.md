<p align="center">
  <img src="SaveGuard/Assets/saveguard.png" width="96" alt="SaveGuard" />
</p>

<h1 align="center">SaveGuard</h1>

<p align="center">
  <a href="README.md">English</a> ·
  <b>繁體中文</b> ·
  <a href="README.zh-Hans.md">简体中文</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a>
</p>

通用遊戲存檔備份器。監看任意遊戲的存檔資料夾，每當遊戲存檔時，自動把**整個資料夾**打成一份帶時間戳的快照，出事時一鍵還原。為「博德之門 3 榮譽模式」這種不能團滅、存檔會被原地覆寫的情境而設計，但對任何遊戲都通用。

## 功能

- **每次存檔自動快照**（含 debounce），可設定保留份數。
- **一鍵還原** — 而且還原前會先拍一份 `pre-restore` 安全快照，所以連「還原本身還錯」都能復原。
- **整包資料夾快照** — 存檔常常是多檔關聯（BG3 的 `meta.lsf` 必須跟 `.lsv` 配對），整包複製才不會壞。
- **從 Steam 匯入** — 掃描你已安裝的 Steam 遊戲，自動偵測每款的存檔資料夾（內建資料庫、Steam 雲端，或依名稱比對）；找不到的就退回手動選擇。
- **遊戲圖示** — 從 Steam 匯入的遊戲會顯示它的商店圖示（或首字母頭像）；每款也能自訂圖示。
- **排除檔案** — 把存檔資料夾內不想備份的檔案排除（例如 log）；這些檔案不會被複製，還原時也不會被動到。
- **共用備份資料夾** — 在「設定」裡設一個所有遊戲共用的備份位置（每款還能再加自己的子資料夾），或讓每款遊戲各用自己的絕對路徑。
- **存檔預覽** — 在每份備份旁顯示遊戲內截圖（可設定圖片類型）。
- **常駐系統匣** — 關閉視窗會縮小到系統匣，背景持續監看。
- **自動更新** — 安裝版會自動從 GitHub Releases 更新。
- **五種語言** — English、繁體中文、简体中文、日本語、한국어，可即時切換。

## 安裝

到 [Releases](https://github.com/HouseAlwaysWin/SaveGuard/releases) 下載最新的 `Setup.exe` 執行即可，之後 App 會自動更新。

> 首次啟動時，如果偵測到博德之門 3 的存檔路徑，會自動幫你建一個 BG3 profile（過濾 `.lsv`、保留 25 份、自動監看）。把 **Save folder** 指到正確位置，按一次 **Back up now**，確認備份清單有跑出來。

按 **+ 新增遊戲** 來加其他遊戲，指定該遊戲的存檔資料夾。常見路徑：

- `Documents\My Games\<遊戲>`
- `%LOCALAPPDATA%\<遊戲>` 或 `%APPDATA%\<遊戲>`
- Steam 雲端同步：`Steam\userdata\<id>\<appid>\remote`

**只在這些類型觸發** 留空就是任何變動都觸發；不確定就留空。

或者按側邊欄的 **↓ 從 Steam 匯入**：SaveGuard 會掃描你已安裝的 Steam 遊戲，從內建資料庫、Steam 雲端資料夾或名稱比對自動填入每款的存檔資料夾。勾選要保護的遊戲即可 —— 找不到的會留空讓你自己手動選。

按 **⚙ 設定** 可以設定一個所有遊戲共用的**共用備份資料夾** —— 之後每款遊戲的備份路徑可以是相對子資料夾（或留空），按「套用到所有遊戲」就能一鍵把現有遊戲都切過去。語言選擇也在這裡。

## 運作原理

- 每份備份都是存檔資料夾的**完整複製**，落在 `BackupRoot/<遊戲名>/<時間戳>/` — 不做個別檔案差異比對，因為存檔只有「整組一起還原」才正確。
- **備份引擎與 UI 完全解耦**（`Services/BackupEngine.cs`）：純 .NET file IO、跨平台、可單元測試。
- 設定存在 `%APPDATA%\SaveGuard\profiles.json`；備份預設輸出在 `%APPDATA%\SaveGuard\Backups`，或你在**設定**裡指定的共用資料夾。
- 已處理的坑：寫入未完成 / 事件轟炸（debounce）、遊戲仍鎖著的檔案（共享讀取複製，真的鎖死才跳過）、備份備份進去的無限遞迴（`ValidateProfile` 擋掉）、還原期間的二次備份（暫停 watcher）、輪替優先淘汰 `pre-restore` 安全副本而非你真正的備份。

## 從原始碼建置

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project SaveGuard
```

## 發布版本

推一個 `v*` tag，GitHub Actions 會自動 build、用 Velopack 打包、發一個 GitHub Release，安裝版就會自動更新過去。詳見 [RELEASING.md](RELEASING.md)。

## 授權

[MIT](LICENSE) © 2026 Martin Wang
