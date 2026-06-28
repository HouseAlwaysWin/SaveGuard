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

    public ProfileStore()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
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
        var bg3 = TryBg3SavePath();
        if (bg3 != null)
        {
            list.Add(new GameProfile
            {
                Name = "Baldur's Gate 3 (Honour)",
                WatchPath = bg3,
                BackupRoot = DefaultBackupRoot,
                Recursive = true,
                TriggerExtensions = ".lsv",
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
    /// when present, else the Larian folder — the user can refine it.
    /// </summary>
    private static string? TryBg3SavePath()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var story = Path.Combine(local, "Larian Studios", "Baldur's Gate 3",
            "PlayerProfiles", "Public", "Savegames", "Story");
        if (Directory.Exists(story)) return story;

        var larian = Path.Combine(local, "Larian Studios", "Baldur's Gate 3");
        return Directory.Exists(larian) ? larian : null;
    }
}
