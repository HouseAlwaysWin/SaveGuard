<p align="center">
  <img src="SaveGuard/Assets/saveguard.png" width="96" alt="SaveGuard" />
</p>

<h1 align="center">SaveGuard</h1>

<p align="center">
  <b>English</b> ·
  <a href="README.zh-Hant.md">繁體中文</a> ·
  <a href="README.zh-Hans.md">简体中文</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a>
</p>

Versioned backups for any game's saves. SaveGuard watches a game's save folder and, every time the game saves, snapshots the **whole folder** into a timestamped copy — so you can roll back to any earlier save in one click. Built for situations like Baldur's Gate 3 Honour Mode (no second chances, saves overwritten in place), but it works for any game.

## Features

- **Automatic snapshots** on every save (debounced), with configurable retention.
- **One-click restore** — and a `pre-restore` safety snapshot is taken first, so even a wrong restore is undoable.
- **Whole-folder snapshots** — saves are often multi-file (BG3's `meta.lsf` must match its `.lsv`); copying the whole folder keeps them consistent.
- **Import from Steam** — scan your installed Steam games and auto-detect each one's save folder (built-in database, Steam Cloud, or name match); anything it can't find falls back to manual selection.
- **Save preview** — shows the in-game screenshot next to each backup (configurable image types).
- **Runs in the tray** — closing the window hides it to the system tray, so watching keeps running in the background.
- **Auto-update** — installed builds update themselves from GitHub Releases.
- **5 languages** — English, 繁體中文, 简体中文, 日本語, 한국어 — switchable live.

## Install

Download the latest `Setup.exe` from [Releases](https://github.com/HouseAlwaysWin/SaveGuard/releases) and run it. After that the app updates itself automatically.

> On first launch, if a Baldur's Gate 3 save folder is detected, a BG3 profile is created for you (filters `.lsv`, keeps 25, auto-watch). Point **Save folder** at the right place, hit **Back up now**, and confirm the backups list fills in.

Add other games with **+ Add a game** and point it at that game's save folder. Common locations:

- `Documents\My Games\<game>`
- `%LOCALAPPDATA%\<game>` or `%APPDATA%\<game>`
- Steam cloud sync: `Steam\userdata\<id>\<appid>\remote`

Leave **Only trigger on these types** blank to react to any change (when unsure, leave it blank).

Or click **↓ Import from Steam** in the sidebar: SaveGuard scans your installed Steam games and pre-fills each one's save folder from a built-in database, your Steam Cloud folder, or a name match. Tick the games to protect — anything it couldn't locate is left blank for you to pick manually.

## How it works

- Each backup is a **complete copy** of the save folder into `BackupRoot/<game>/<timestamp>/` — never a per-file diff — because saves only restore correctly as a set.
- The **backup engine is UI-free** (`Services/BackupEngine.cs`): pure .NET file IO, cross-platform, unit-testable.
- Settings persist to `%APPDATA%\SaveGuard\profiles.json`; backups default to `%APPDATA%\SaveGuard\Backups`.
- Edge cases handled: write-in-progress and event storms (debounce), files the game still holds (shared-read copy, skip the truly locked ones), recursive backup-of-backups (`ValidateProfile` blocks it), spurious backup during a restore (the watcher is paused), and rotation that evicts `pre-restore` safety copies before your real backups.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project SaveGuard
```

## Releasing

Push a `v*` tag and GitHub Actions builds, packages (Velopack), and publishes a GitHub Release that installed apps auto-update to. See [RELEASING.md](RELEASING.md).

## License

[MIT](LICENSE) © 2026 Martin Wang
