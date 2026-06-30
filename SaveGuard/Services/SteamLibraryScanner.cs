using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SaveGuard.Services;

/// <summary>One fully-installed Steam game discovered on disk.</summary>
public sealed record SteamGame(
    long AppId,
    string Name,
    string InstallDir,    // absolute: <library>\steamapps\common\<installdir>
    string LibraryRoot,   // absolute: the library root (parent of \steamapps)
    int StateFlags);

/// <summary>
/// Finds the local Steam installation and enumerates installed games by parsing
/// <c>libraryfolders.vdf</c> and the per-app <c>appmanifest_*.acf</c> files. These
/// are pure disk/registry reads (no UI), so callers run them on a background
/// thread. Returns empty results — never throws — when Steam isn't present.
/// </summary>
public sealed class SteamLibraryScanner
{
    // StateFlags is a bitfield; bit 2 (value 4) = StateFullyInstalled.
    private const int StateFullyInstalled = 4;

    // Runtimes/tools that install like games but have no user saves — hidden from
    // the import list (they show up in nearly every library otherwise).
    private static readonly HashSet<long> NonGameAppIds = new()
    {
        228980,   // Steamworks Common Redistributables
        1070560,  // Steam Linux Runtime 1.0 (scout)
        1391110,  // Steam Linux Runtime 2.0 (soldier)
        1628350,  // Steam Linux Runtime 3.0 (sniper)
        1493710,  // Proton Experimental
    };

    /// <summary>Locate the Steam root (the folder containing <c>\steamapps</c>), or null.</summary>
    public string? FindSteamRoot()
    {
        foreach (var candidate in CandidateSteamRoots())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var root = candidate.Replace('/', Path.DirectorySeparatorChar);
            try
            {
                if (Directory.Exists(Path.Combine(root, "steamapps")))
                    return Path.GetFullPath(root);
            }
            catch { /* malformed path — try the next candidate */ }
        }
        return null;
    }

    private static IEnumerable<string?> CandidateSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            // The canonical signal; written by the running Steam client.
            yield return ReadRegistry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            // 64-bit Windows stores the 32-bit client's install path under WOW6432Node.
            yield return ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            yield return ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "Steam");
            if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "Steam");
        }
        else
        {
            // Best-effort fallbacks for completeness; the app targets Windows.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".steam", "steam");
                yield return Path.Combine(home, ".local", "share", "Steam");
                yield return Path.Combine(home, "Library", "Application Support", "Steam");
            }
        }
    }

    private static string? ReadRegistry(RegistryHive hive, string subkey, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subkey);
            return key?.GetValue(value) as string;
        }
        catch { return null; }
    }

    /// <summary>All library roots (each contains <c>\steamapps</c>), including the
    /// Steam install itself. Deduped, existing directories only.</summary>
    public IReadOnlyList<string> EnumerateLibraries(string steamRoot)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            string full;
            try { full = Path.GetFullPath(root.Replace('/', Path.DirectorySeparatorChar)); }
            catch { return; }
            if (!seen.Add(full)) return;
            if (Directory.Exists(Path.Combine(full, "steamapps"))) libs.Add(full);
        }

        Add(steamRoot); // the Steam install itself is always a library

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var node = VdfNode.Parse(File.ReadAllText(vdf));
                var libsNode = node.GetChild("libraryfolders") ?? node;
                // Modern form: numbered children each with a "path".
                foreach (var (_, child) in libsNode.Children)
                    Add(child.GetValue("path"));
                // Legacy flat form: "1" "D:\\Steam" (numbered keys → path strings).
                foreach (var (_, val) in libsNode.Values)
                    Add(val);
            }
            catch { /* keep whatever we already collected */ }
        }

        return libs;
    }

    /// <summary>Fully-installed games across all libraries (StateFlags &amp; 4) whose
    /// install directory exists. Deduped by AppId, sorted by name.</summary>
    public IReadOnlyList<SteamGame> EnumerateInstalledGames(string steamRoot)
    {
        var games = new Dictionary<long, SteamGame>();

        foreach (var lib in EnumerateLibraries(steamRoot))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            string[] manifests;
            try { manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
            catch { continue; }

            foreach (var file in manifests)
            {
                var game = TryParseManifest(file, lib);
                if (game != null && !games.ContainsKey(game.AppId))
                    games[game.AppId] = game;
            }
        }

        return games.Values
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static SteamGame? TryParseManifest(string file, string libraryRoot)
    {
        try
        {
            var app = VdfNode.Parse(File.ReadAllText(file)).GetChild("AppState");
            if (app == null) return null;

            if (!long.TryParse(app.GetValue("appid"), out var appId)) return null;
            if (NonGameAppIds.Contains(appId)) return null;
            var name = app.GetValue("name");
            if (string.IsNullOrWhiteSpace(name)) name = $"App {appId}";
            var installDir = app.GetValue("installdir") ?? "";
            int.TryParse(app.GetValue("StateFlags"), out var flags);

            if ((flags & StateFullyInstalled) == 0) return null;
            if (string.IsNullOrWhiteSpace(installDir)) return null;

            var fullInstall = Path.Combine(libraryRoot, "steamapps", "common", installDir);
            if (!Directory.Exists(fullInstall)) return null;

            return new SteamGame(appId, name, fullInstall, libraryRoot, flags);
        }
        catch { return null; }
    }

    // Standard (non-icon) art files in a librarycache app folder; the icon is the
    // remaining hash-named .jpg.
    private static readonly HashSet<string> NonIconArt = new(StringComparer.OrdinalIgnoreCase)
    {
        "header.jpg", "library_600x900.jpg", "library_600x900_2x.jpg", "library_hero.jpg",
        "library_hero_blur.jpg", "logo.png", "capsule_231x87.jpg", "page_bg_generated_v6b.jpg",
    };

    /// <summary>Best-effort path to a game's icon image from Steam's library cache, or
    /// null. Modern layout: <c>appcache\librarycache\&lt;appid&gt;\&lt;hash&gt;.jpg</c> (the
    /// square icon); falls back to header art or the older flat <c>&lt;appid&gt;_icon.jpg</c>.</summary>
    public string? FindGameIcon(string steamRoot, long appId)
    {
        var cache = Path.Combine(steamRoot, "appcache", "librarycache");
        var dir = Path.Combine(cache, appId.ToString());
        try
        {
            if (Directory.Exists(dir))
            {
                // The hash-named jpg (not one of the standard art names) is the icon.
                var icon = Directory.EnumerateFiles(dir, "*.jpg")
                    .FirstOrDefault(f => !NonIconArt.Contains(Path.GetFileName(f)));
                if (icon != null) return icon;

                foreach (var fallback in new[] { "header.jpg", "library_600x900.jpg" })
                {
                    var p = Path.Combine(dir, fallback);
                    if (File.Exists(p)) return p;
                }
            }

            // Older flat layout.
            var flat = Path.Combine(cache, $"{appId}_icon.jpg");
            if (File.Exists(flat)) return flat;
        }
        catch { /* unreadable cache — no icon */ }
        return null;
    }

    /// <summary>Numeric Steam account-id folders under <c>&lt;steamRoot&gt;\userdata</c>
    /// (skips "0" and non-numeric entries). One per account used on this machine.</summary>
    public IReadOnlyList<string> EnumerateSteamUserIds(string steamRoot)
    {
        var ids = new List<string>();
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return ids;
        try
        {
            foreach (var dir in Directory.GetDirectories(userdata))
            {
                var name = Path.GetFileName(dir);
                if (long.TryParse(name, out var id) && id > 0) ids.Add(name);
            }
        }
        catch { /* ignore unreadable userdata */ }
        return ids;
    }
}
