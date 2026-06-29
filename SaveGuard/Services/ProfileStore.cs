using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SaveGuard.Models;

namespace SaveGuard.Services;

/// <summary>
/// Loads and saves the list of profiles as JSON under the user's app-data dir.
/// On first run it seeds a Baldur's Gate 3 profile with the save path filled in
/// (if that path exists on this machine), so the tool is useful immediately.
/// </summary>
public sealed class ProfileStore
{
    private readonly string _dir;
    private readonly string _file;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <param name="baseDir">App-data base directory. Defaults to %APPDATA%. Pass a
    /// throwaway directory in tests/tools so they never touch the user's real data.</param>
    public ProfileStore(string? baseDir = null)
    {
        baseDir ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _dir = Path.Combine(baseDir, "SaveGuard");
        _file = Path.Combine(_dir, "profiles.json");
    }

    /// <summary>Default place to put backups: AppData/SaveGuard/Backups.</summary>
    public string DefaultBackupRoot => Path.Combine(_dir, "Backups");

    public List<GameProfile> Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                var list = JsonSerializer.Deserialize<List<GameProfile>>(json);
                if (list is { Count: > 0 }) return list;
            }
        }
        catch { /* fall through to seed */ }

        var seeded = Seed();
        Save(seeded);
        return seeded;
    }

    public void Save(IEnumerable<GameProfile> profiles)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(profiles, JsonOpts);
        File.WriteAllText(_file, json);
    }

    private List<GameProfile> Seed()
    {
        var list = new List<GameProfile>();
        var (story, profileFlag) = TryBg3Paths();
        if (story != null)
        {
            list.Add(new GameProfile
            {
                Name = "Baldur's Gate 3 (Honour)",
                WatchPath = story,
                BackupRoot = DefaultBackupRoot,
                Recursive = true,
                TriggerExtensions = ".lsv",
                // Honour mode's run-failed flag lives in profile8.lsf, outside the
                // save folder — capture it so a restore actually clears a team-wipe.
                CompanionFiles = profileFlag ?? "",
                MaxSnapshots = 25,
                AutoWatch = true,
                DebounceMs = 2500,
            });
        }
        return list;
    }

    /// <summary>
    /// BG3 saves live under LOCALAPPDATA on Windows. The profile id between
    /// PlayerProfiles and Savegames varies, so we land on PlayerProfiles\Public
    /// when present, else the Larian folder — the user can refine it. Also returns
    /// the Public\profile8.lsf path (the Honour-mode failure flag) when found.
    /// </summary>
    private static (string? story, string? profileFlag) TryBg3Paths()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var publicDir = Path.Combine(local, "Larian Studios", "Baldur's Gate 3", "PlayerProfiles", "Public");
        var story = Path.Combine(publicDir, "Savegames", "Story");
        if (Directory.Exists(story))
            return (story, Path.Combine(publicDir, "profile8.lsf"));

        var larian = Path.Combine(local, "Larian Studios", "Baldur's Gate 3");
        return (Directory.Exists(larian) ? larian : null, null);
    }
}
