# Releasing SaveGuard

SaveGuard auto-updates via [Velopack](https://velopack.io) using GitHub Releases as
the update feed. Publishing a new version is one push.

## Cut a release

Easiest — use the helper (creates + pushes the tag, replacing it if it already exists):

```bat
release.bat v1.0.1
```

Or do it by hand:

```bash
git tag v1.0.1
git push origin v1.0.1
```

Either way, pushing the tag triggers `.github/workflows/release.yml`, which:

1. Publishes a self-contained `win-x64` build.
2. Packs it with the Velopack CLI (`vpk pack`) — producing `Setup.exe`, the
   release `.nupkg`, and the `RELEASES` manifest (deltas vs the previous release
   are generated automatically from earlier GitHub Releases).
3. Uploads them to a **GitHub Release** at that tag (`vpk upload github`).

Version = the tag without the leading `v` (so `v1.0.1` → `1.0.1`). Use SemVer and
always bump upward — Velopack compares versions to decide whether to update.

### What's in a Release

Each Release carries several assets — this is normal Velopack output, not a NuGet
publish (nothing is pushed to nuget.org):

- **`SaveGuard-win-Setup.exe`** — the installer. **This is the only thing end users need.**
- `SaveGuard-<ver>-full.nupkg` — Velopack's *update package* (its on-disk format is
  nupkg-based). The in-app updater downloads this to update; users never touch it.
- `releases.win.json` / `RELEASES` — the update manifest the app reads to find new versions.
- `SaveGuard-win-Portable.zip` — an optional no-install portable build.

## How users get it

- **First install:** download `Setup.exe` from the latest
  [Release](https://github.com/HouseAlwaysWin/SaveGuard/releases) and run it. The
  app installs per-user and creates a shortcut.
- **Updates:** on launch, the app checks the Releases feed in the background; when
  a newer version is found it downloads it and shows a **"Restart to update"**
  button in the status bar. Clicking it applies the update and relaunches. (Running
  from a dev build / unzipped folder, update checks are silently skipped.)

## Test a packaged build locally (optional)

```powershell
dotnet publish SaveGuard/SaveGuard.csproj -c Release -r win-x64 --self-contained true -o publish /p:Version=1.0.1
# vpk version MUST match the Velopack <PackageReference> in SaveGuard.csproj (currently 1.2.0).
dotnet tool install -g vpk --version 1.2.0
vpk pack --packId SaveGuard --packTitle SaveGuard --packVersion 1.0.1 --packDir publish --mainExe SaveGuard.exe --icon SaveGuard/Assets/saveguard.ico
# Outputs Setup.exe under .\Releases — run it to install, then bump the version and repeat to see an update.
```

## Notes

- The repo must be **public** (or the app needs an embedded token — avoid that) for
  the in-app updater to read the Releases feed without authentication.
- The CI uses the built-in `GITHUB_TOKEN`; no extra secrets needed.
- To ship pre-release/beta builds, tag like `v1.1.0-beta.1` and flip the
  `GithubSource(..., prerelease: true)` flag in `Services/UpdateService.cs`.
