# SaveGuard

通用遊戲存檔備份器。監看任意遊戲的存檔資料夾，每當遊戲存檔時自動把整個資料夾打成一個帶時間戳的快照，出事時一鍵還原。為「博得之門 3 榮譽模式」這種不能團滅、存檔會被原地覆寫的情境而設計，但對任何遊戲都通用。

## 為什麼這樣設計

**一個備份單位 = 存檔資料夾的完整快照。** 不做個別檔案的差異比對，每次都整包複製到 `BackupRoot/<遊戲名>/<時間戳>/`。理由是還原的正確性——遊戲存檔常常是多檔關聯的（BG3 的 `meta.lsf` 必須跟 `.lsv` 配對），只還原其中一個就會壞掉。整包快照天生避開這問題。

**還原前先自動快照當前狀態**（標記 `pre-restore`），所以連「還原本身還錯」都有保險。榮譽模式沒有第二次機會，這層特別重要。

**引擎與 UI 完全解耦。** `Services/BackupEngine.cs` 是純 .NET file IO、無任何 UI 依賴、跨平台、可單元測試。之後要做成 CLI 或系統托盤版，直接複用引擎那層。

## 架構

```
Models/
  GameProfile.cs     一個被監看的遊戲（路徑、過濾、輪替設定）
  Snapshot.cs        一份快照（時間戳、大小、檔數、標籤）
Services/
  BackupEngine.cs    快照 / 輪替 / 還原；路徑安全驗證
  WatchService.cs    FileSystemWatcher + debounce（每遊戲一個）
  ProfileStore.cs    Profile 持久化 (JSON)；首次啟動自動建 BG3 profile
ViewModels/
  MainWindowViewModel.cs   master/detail、所有指令
Views/
  MainWindow.axaml(.cs)    UI
  ConfirmDialog.cs         自製確認對話框（Avalonia 無內建 message box）
  Converters.cs            標籤顏色 / 監看狀態顏色
```

## 工程上處理掉的坑

- **寫入未完成就觸發**：`WatchService` 用 debounce（預設等 2 秒寫入靜下來）才打包，避免複製到半截檔案。
- **一次存檔轟炸多個事件**：debounce timer 收斂，最後一發才動手。
- **遊戲還鎖著存檔**：用 `FileShare.ReadWrite` 共享讀取複製；真的鎖死的檔案跳過而不是整包失敗。
- **備份備份進去的無限遞迴**：`ValidateProfile` 擋掉 `BackupRoot` 落在 `WatchPath` 內（或反過來）的設定。
- **還原時的二次存檔**：還原期間暫停 watcher，避免還原寫入又觸發一次自動備份。
- **輪替優先砍 pre-restore**：超過上限時，安全副本比你真正的備份先被淘汰。

## 建置與執行

需要 .NET 10 SDK。

```bash
cd SaveGuard/SaveGuard
dotnet restore
dotnet run
```

首次啟動時，如果偵測到 BG3 存檔路徑
（`%LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\PlayerProfiles\Public\Savegames\Story`），
會自動幫你建好一個 BG3 profile，過濾 `.lsv`、保留 25 份、自動監看。確認一下 `Save folder` 指對地方
（榮譽模式存檔在那個 `Story` 目錄底下），按 **Back up now** 測一次，看備份清單有沒有跑出來。

Profile 設定存在 `%APPDATA%\SaveGuard\profiles.json`，預設備份輸出在 `%APPDATA%\SaveGuard\Backups`。

## 給其他遊戲用

按左下「+ Add a game」，指定那個遊戲的存檔資料夾就好。常見路徑：

- `Documents\My Games\<遊戲>`
- `%LOCALAPPDATA%\<遊戲>`
- `%APPDATA%\<遊戲>`
- Steam 雲端同步：`Steam\userdata\<id>\<appid>\remote`

`Only trigger on these types` 留空就是任何變動都觸發；不確定就留空。

## 之後可以加的

- 系統托盤常駐（核心引擎不用動，加個 `H.NotifyIcon` 的 UI 殼）
- 快照備註 / 釘選（避免被輪替砍掉）
- 還原前的差異預覽
- zip 壓縮選項（引擎留了介面空間）
