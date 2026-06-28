<p align="center">
  <img src="SaveGuard/Assets/saveguard.png" width="96" alt="SaveGuard" />
</p>

<h1 align="center">SaveGuard</h1>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.zh-Hant.md">繁體中文</a> ·
  <b>简体中文</b> ·
  <a href="README.ja.md">日本語</a>
</p>

通用游戏存档备份器。监视任意游戏的存档文件夹，每当游戏存档时，自动把**整个文件夹**打成一份带时间戳的快照，出事时一键还原。为「博德之门 3 荣誉模式」这种不能团灭、存档会被原地覆盖的情境而设计，但对任何游戏都通用。

## 功能

- **每次存档自动快照**（含 debounce），可设定保留份数。
- **一键还原** — 而且还原前会先拍一份 `pre-restore` 安全快照，所以连「还原本身出错」都能撤销。
- **整包文件夹快照** — 存档常常是多文件关联（BG3 的 `meta.lsf` 必须与 `.lsv` 配对），整包复制才不会坏。
- **存档预览** — 在每份备份旁显示游戏内截图（可设定图片类型）。
- **常驻系统托盘** — 关闭窗口会缩小到系统托盘，后台持续监视。
- **自动更新** — 安装版会自动从 GitHub Releases 更新。
- **四种语言** — English、繁體中文、简体中文、日本語，可即时切换。

## 安装

到 [Releases](https://github.com/HouseAlwaysWin/SaveGuard/releases) 下载最新的 `Setup.exe` 运行即可，之后 App 会自动更新。

> 首次启动时，如果检测到博德之门 3 的存档路径，会自动帮你建一个 BG3 配置（过滤 `.lsv`、保留 25 份、自动监视）。把 **Save folder** 指向正确位置，按一次 **Back up now**，确认备份列表有出现。

按 **+ 新增游戏** 来添加其他游戏，指定该游戏的存档文件夹。常见路径：

- `Documents\My Games\<游戏>`
- `%LOCALAPPDATA%\<游戏>` 或 `%APPDATA%\<游戏>`
- Steam 云同步：`Steam\userdata\<id>\<appid>\remote`

**只在这些类型触发** 留空就是任何改动都触发；不确定就留空。

## 工作原理

- 每份备份都是存档文件夹的**完整复制**，放在 `BackupRoot/<游戏名>/<时间戳>/` — 不做单文件差异比对，因为存档只有「整组一起还原」才正确。
- **备份引擎与 UI 完全解耦**（`Services/BackupEngine.cs`）：纯 .NET file IO、跨平台、可单元测试。
- 设定存在 `%APPDATA%\SaveGuard\profiles.json`；备份默认输出在 `%APPDATA%\SaveGuard\Backups`。
- 已处理的坑：写入未完成 / 事件风暴（debounce）、游戏仍锁着的文件（共享读取复制，真锁死才跳过）、备份套备份的无限递归（`ValidateProfile` 拦截）、还原期间的二次备份（暂停 watcher）、轮替优先淘汰 `pre-restore` 安全副本而非你真正的备份。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project SaveGuard
```

## 发布版本

推一个 `v*` tag，GitHub Actions 会自动 build、用 Velopack 打包、发一个 GitHub Release，安装版就会自动更新过去。详见 [RELEASING.md](RELEASING.md)。

## 许可证

[MIT](LICENSE) © 2026 Martin Wang
