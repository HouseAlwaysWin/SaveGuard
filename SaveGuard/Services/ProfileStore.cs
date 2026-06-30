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
        var (story, companions) = TryBg3Paths();
        if (story != null)
        {
            list.Add(new GameProfile
            {
                Name = "Baldur's Gate 3 (Honour)",
                WatchPath = story,
                BackupRoot = DefaultBackupRoot,
                Recursive = true,
                TriggerExtensions = ".lsv",
                // The Honour-mode failure flag lives in the profile metadata outside the
                // save folder — capture it so a restore actually clears a team-wipe.
                CompanionFiles = companions ?? "",
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
    /// the companion-file lines (the Honour-mode profile metadata, outside the save
    /// folder) when found.
    /// </summary>
    private static (string? story, string? companions) TryBg3Paths()
    {
        if (!OperatingSystem.IsWindows()) return (null, null);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profilesDir = Path.Combine(local, "Larian Studios", "Baldur's Gate 3", "PlayerProfiles");
        var publicDir = Path.Combine(profilesDir, "Public");
        var story = Path.Combine(publicDir, "Savegames", "Story");
        if (Directory.Exists(story))
        {
            // Honour-mode "run failed → Custom" state lives in the profile metadata,
            // OUTSIDE the save folder, so restoring the Story save alone never clears a
            // team-wipe. The PlayerProfiles-level playerprofiles8.lsf is the file that
            // flips on a wipe; the Public-level profile8.lsf is included for safety.
            var companions = string.Join("\n", new[]
            {
                Path.Combine(profilesDir, "playerprofiles8.lsf"),
                Path.Combine(publicDir, "profile8.lsf"),
            });
            return (story, companions);
        }

        var larian = Path.Combine(local, "Larian Studios", "Baldur's Gate 3");
        return (Directory.Exists(larian) ? larian : null, null);
    }
}
