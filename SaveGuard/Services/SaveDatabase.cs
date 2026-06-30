using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;

namespace SaveGuard.Services;

/// <summary>One curated known-game entry: a templated save path (with placeholders)
/// plus an optional ready-made preset (trigger extensions, companion files).</summary>
public sealed class SaveDbEntry
{
    public string Name { get; set; } = "";

    /// <summary>Candidate save-path templates, most-preferred first. Each may use
    /// placeholders like <c>&lt;winAppData&gt;</c>; the first one that resolves and
    /// exists on disk wins.</summary>
    public List<string> SavePaths { get; set; } = new();

    /// <summary>Comma-separated trigger extensions for the imported profile (optional).</summary>
    public string? TriggerExtensions { get; set; }

    /// <summary>Companion file path(s), one per line, possibly templated (optional).</summary>
    public string? CompanionFiles { get; set; }
}

/// <summary>
/// Loads the bundled <c>Assets/savedata-db.json</c> (a small curated map of Steam
/// AppId → known save location) and expands path placeholders against this machine.
/// Wrong or missing entries degrade gracefully: an unresolved template just yields
/// no candidate, so the user falls back to picking the folder manually.
/// </summary>
public sealed class SaveDatabase
{
    private sealed class Root
    {
        public int Version { get; set; }
        public Dictionary<string, SaveDbEntry> Games { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<long, SaveDbEntry> _games;

    private SaveDatabase(Dictionary<long, SaveDbEntry> games) => _games = games;

    public SaveDbEntry? Lookup(long appId) => _games.TryGetValue(appId, out var e) ? e : null;

    public static SaveDatabase Load()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://SaveGuard/Assets/savedata-db.json"));
            var root = JsonSerializer.Deserialize<Root>(s, JsonOpts);
            var map = new Dictionary<long, SaveDbEntry>();
            if (root?.Games != null)
                foreach (var (key, entry) in root.Games)
                    if (long.TryParse(key, out var appId)) map[appId] = entry;
            return new SaveDatabase(map);
        }
        catch { return new SaveDatabase(new()); } // missing/corrupt DB → empty, like Localizer
    }

    /// <summary>
    /// Expand placeholders in a path template and normalize it. Returns null when a
    /// referenced placeholder can't be resolved on this OS (so the candidate is
    /// dropped). Does NOT check whether the path exists — the caller decides that.
    /// </summary>
    public static string? Expand(string template, string? steamInstallDir = null, string? steamUserDataDir = null)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        static string? Folder(Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            return string.IsNullOrEmpty(p) ? null : p;
        }

        var home = Folder(Environment.SpecialFolder.UserProfile);

        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["<home>"] = home,
            ["<steamInstall>"] = steamInstallDir,
            ["<steamUserData>"] = steamUserDataDir,
        };
        if (OperatingSystem.IsWindows())
        {
            map["<winAppData>"] = Folder(Environment.SpecialFolder.ApplicationData);
            map["<winLocalAppData>"] = Folder(Environment.SpecialFolder.LocalApplicationData);
            map["<winDocuments>"] = Folder(Environment.SpecialFolder.MyDocuments);
            map["<winLocalLow>"] = home == null ? null : Path.Combine(home, "AppData", "LocalLow");
            map["<winSavedGames>"] = home == null ? null : Path.Combine(home, "Saved Games");
        }

        var result = template;
        foreach (var (token, value) in map)
        {
            if (result.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (string.IsNullOrEmpty(value)) return null; // needed but unavailable on this OS
            result = result.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        }

        // Any leftover <...> token means an unknown/unsupported placeholder.
        if (result.IndexOf('<') >= 0 && result.IndexOf('>') >= 0) return null;

        result = result.Replace('/', Path.DirectorySeparatorChar);
        try { return Path.GetFullPath(result); } catch { return null; }
    }
}
