using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SaveGuard.Services;

/// <summary>Where a resolved save path came from — drives the confidence ranking
/// and the label shown in the import dialog.</summary>
public enum SaveSource { None, KnownDatabase, SteamCloud, Heuristic }

/// <summary>A candidate save folder for a game, with its provenance and a preset
/// carried over from the curated database (when the candidate came from there).</summary>
public sealed record SaveCandidate(
    string Path,
    SaveSource Source,
    int Confidence,
    string? TriggerExtensions = null,
    string? CompanionFiles = null);

/// <summary>
/// Resolves likely save folders for a Steam game using three layered tiers:
/// (a) the bundled curated database, (b) the game's Steam Cloud <c>remote</c>
/// folder, and (c) a name-match scan of common save roots. Only existing folders
/// are returned, best-confidence first; an empty result means "let the user pick".
/// </summary>
public sealed class SaveLocationResolver
{
    private readonly string _steamRoot;
    private readonly IReadOnlyList<string> _steamUserIds;
    private readonly SaveDatabase _db;

    public SaveLocationResolver(string steamRoot, IReadOnlyList<string> steamUserIds, SaveDatabase db)
    {
        _steamRoot = steamRoot;
        _steamUserIds = steamUserIds;
        _db = db;
    }

    public IReadOnlyList<SaveCandidate> Resolve(SteamGame game)
    {
        var found = new List<SaveCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, SaveSource src, int conf, string? trig = null, string? comp = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (!Directory.Exists(full) || !seen.Add(full)) return;
            found.Add(new SaveCandidate(full, src, conf, trig, comp));
        }

        // (a) curated known-games database
        var entry = _db.Lookup(game.AppId);
        if (entry != null)
        {
            var companion = ExpandCompanion(entry.CompanionFiles, game);
            foreach (var template in entry.SavePaths)
                foreach (var userData in UserDataDirsFor(template))
                    Add(SaveDatabase.Expand(template, game.InstallDir, userData),
                        SaveSource.KnownDatabase, 95, entry.TriggerExtensions, companion);
        }

        // (b) Steam Cloud: userdata\<id>\<appid>\remote
        foreach (var id in _steamUserIds)
        {
            var remote = Path.Combine(_steamRoot, "userdata", id, game.AppId.ToString(), "remote");
            if (Directory.Exists(remote) && HasAnyEntry(remote))
                Add(remote, SaveSource.SteamCloud, 80);
        }

        // (c) heuristic name match across common save roots
        foreach (var c in HeuristicMatches(game))
            Add(c.Path, SaveSource.Heuristic, c.Confidence);

        return found.OrderByDescending(c => c.Confidence).ToList();
    }

    // Only fan out over Steam accounts when the template actually needs one;
    // otherwise expand once (so the DB tier still runs with no accounts present).
    private IEnumerable<string?> UserDataDirsFor(string template)
    {
        if (template.IndexOf("<steamUserData>", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (var id in _steamUserIds)
                yield return Path.Combine(_steamRoot, "userdata", id);
        }
        else
        {
            yield return null;
        }
    }

    private static string? ExpandCompanion(string? raw, SteamGame game)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lines = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var ex = SaveDatabase.Expand(t, game.InstallDir);
            if (ex != null) lines.Add(ex);
        }
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private IEnumerable<SaveCandidate> HeuristicMatches(SteamGame game)
    {
        var roots = new List<(string root, int weight)>();
        void AddRoot(string? p, int w)
        {
            if (!string.IsNullOrEmpty(p)) roots.Add((p!, w));
        }

        if (OperatingSystem.IsWindows())
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            if (!string.IsNullOrEmpty(docs)) AddRoot(Path.Combine(docs, "My Games"), 60);
            if (!string.IsNullOrEmpty(home)) AddRoot(Path.Combine(home, "Saved Games"), 58);
            AddRoot(docs, 50);
            if (!string.IsNullOrEmpty(home)) AddRoot(Path.Combine(home, "AppData", "LocalLow"), 48);
            AddRoot(roaming, 45);
            AddRoot(local, 45);
        }

        var target = Normalize(game.Name);
        if (target.Length < 3) yield break;

        foreach (var (root, weight) in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch { continue; }

            foreach (var sub in subs)
            {
                var norm = Normalize(Path.GetFileName(sub));
                if (norm.Length < 3) continue;
                int bonus = MatchScore(target, norm);
                if (bonus == 0) continue;
                yield return new SaveCandidate(sub, SaveSource.Heuristic, Math.Min(weight + bonus, 75));
            }
        }
    }

    // Exact normalized match scores highest; a length-gated containment is weaker.
    private static int MatchScore(string a, string b)
    {
        if (a == b) return 15;
        if (a.Length >= 4 && b.Length >= 4 && (a.Contains(b) || b.Contains(a))) return 8;
        return 0;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static bool HasAnyEntry(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }
}
