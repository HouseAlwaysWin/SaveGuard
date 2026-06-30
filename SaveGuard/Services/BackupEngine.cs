using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SaveGuard.Models;

namespace SaveGuard.Services;

/// <summary>
/// Pure backup logic with no UI dependencies. A "snapshot" is always a complete
/// copy of the watched folder into a timestamped subfolder — never a per-file
/// diff — because game saves are usually multi-file and only restore correctly
/// as a set (e.g. BG3's meta.lsf must match its .lsv).
/// </summary>
public sealed class BackupEngine
{
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>Per-snapshot bookkeeping that must never land back in the save folder.</summary>
    private const string Marker = ".saveguard";
    private const string CompanionDir = "_companions";
    private const string CompanionManifest = "manifest.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly char[] DirSeparators = { '/', '\\' };
    private static readonly char[] WildChars = { '*', '?' };

    /// <summary>One captured companion file: where it lives in the snapshot and the
    /// absolute path it must be restored to.</summary>
    private sealed class CompanionEntry
    {
        public string Stored { get; set; } = "";
        public string Original { get; set; } = "";
    }

    /// <summary>Optional shared backup root applied to every profile (set from UiState).
    /// When set, a profile's BackupRoot may be relative/blank and resolves under it.</summary>
    public string GlobalBackupRoot { get; set; } = "";

    /// <summary>The effective backup root for a profile: its own BackupRoot when absolute,
    /// otherwise resolved under the shared <see cref="GlobalBackupRoot"/> (a blank per-game
    /// path → the shared root itself). When no shared root is set, returns the raw per-game
    /// value (which validation then requires to be absolute).</summary>
    public string ResolveBackupRoot(GameProfile p)
    {
        var perGame = p.BackupRoot ?? "";
        if (string.IsNullOrEmpty(GlobalBackupRoot)) return perGame;
        try { return Path.Combine(GlobalBackupRoot, perGame); } // absolute perGame wins; blank → shared root
        catch { return perGame; }
    }

    /// <summary>
    /// Validates a profile's paths. Returns null if OK, otherwise an error message.
    /// The key invariant: the (effective) backup root must never sit inside WatchPath
    /// (that would make us back up our own backups, recursively).
    /// </summary>
    public string? ValidateProfile(GameProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.WatchPath))
            return "Watch folder is not set.";

        var effectiveRoot = ResolveBackupRoot(p);
        if (string.IsNullOrWhiteSpace(effectiveRoot))
            return "Backup folder is not set. Enter a path, or set a shared backup folder in Settings.";
        if (!Path.IsPathRooted(effectiveRoot))
            return "Backup path must be absolute. Set a shared backup folder in Settings to use a relative subfolder here.";
        if (!Directory.Exists(p.WatchPath))
            return $"Watch folder does not exist:\n{p.WatchPath}";

        var watch = NormalizeDir(p.WatchPath);
        var backup = NormalizeDir(effectiveRoot);

        if (PathsEqual(watch, backup))
            return "Backup folder cannot be the same as the watch folder.";
        if (IsInside(backup, watch))
            return "Backup folder must be OUTSIDE the watch folder, or backups will pile up inside the save folder and re-trigger themselves.";
        if (IsInside(watch, backup))
            return "Watch folder cannot be inside the backup folder.";
        if (IsDriveRoot(watch))
            return "Refusing to watch a drive root — point at the game's save folder.";

        return null;
    }

    /// <summary>
    /// Copies the entire watch folder into BackupRoot/&lt;ProfileName&gt;/&lt;timestamp&gt;.
    /// Locked files (the game may still hold a handle) are copied with shared
    /// read where possible and skipped rather than aborting the whole snapshot.
    /// </summary>
    public Task<Snapshot> CreateSnapshotAsync(GameProfile p, string label = "auto", CancellationToken ct = default)
        => Task.Run(() =>
        {
            var err = ValidateProfile(p);
            if (err != null) throw new InvalidOperationException(err);

            var profileDir = ProfileBackupDir(p);
            Directory.CreateDirectory(profileDir);

            // Timestamp to the second; if two fire within one second, suffix it.
            var stamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var dest = Path.Combine(profileDir, stamp);
            int dup = 1;
            while (Directory.Exists(dest))
                dest = Path.Combine(profileDir, $"{stamp}_{dup++}");

            Directory.CreateDirectory(dest);

            var (files, bytes) = CopyTree(p.WatchPath, dest, ct, excludePatterns: p.ExcludeList());

            // Also grab any companion files that live outside the watch folder.
            var (cFiles, cBytes) = CaptureCompanions(p, dest, ct);

            // Write a tiny marker so labels survive restarts.
            try { File.WriteAllText(Path.Combine(dest, Marker), label); } catch { /* non-fatal */ }

            EnforceRotation(p);

            return new Snapshot
            {
                FolderPath = dest,
                TakenAt = DateTime.Now,
                SizeBytes = bytes + cBytes,
                FileCount = files + cFiles,
                Label = label,
            };
        }, ct);

    /// <summary>
    /// Restores a snapshot over the live save folder. Before touching anything
    /// it takes a "pre-restore" snapshot of the CURRENT state, so even a restore
    /// that turns out wrong is itself recoverable — critical for Honour Mode,
    /// where there is no second chance.
    /// </summary>
    public Task RestoreAsync(GameProfile p, Snapshot snap, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Directory.Exists(snap.FolderPath))
                throw new InvalidOperationException("That snapshot folder no longer exists.");

            // Clear the live folder so the restore replaces its contents cleanly. The
            // game may have deleted the entire save folder (e.g. BG3 wipes an Honour-mode
            // save on death) — that is *exactly* when a restore is needed — so recreate
            // it rather than refusing. No pre-restore safety snapshot is taken: it only
            // piled up the list on repeated restores (the user's own backups are the
            // recovery point).
            if (Directory.Exists(p.WatchPath) &&
                Directory.EnumerateFileSystemEntries(p.WatchPath).Any())
            {
                ClearDirectoryContents(p.WatchPath, p.ExcludeList());
            }
            else
            {
                Directory.CreateDirectory(p.WatchPath); // recreate the deleted/empty folder
            }

            // Copy the save tree back in, but skip the snapshot's bookkeeping —
            // the marker and the companion store must NOT pollute the save folder.
            // Also skip excluded paths: if an OLD snapshot still contains a file that
            // is now excluded, copying it back would clobber the preserved live copy.
            CopyTree(snap.FolderPath, p.WatchPath, ct,
                skipTopLevel: BookkeepingTop(), excludePatterns: p.ExcludeList());

            // Put each companion file back at its own original absolute path.
            RestoreCompanions(snap.FolderPath);
        }, ct);

    public List<Snapshot> ListSnapshots(GameProfile p)
    {
        var dir = ProfileBackupDir(p);
        var list = new List<Snapshot>();
        if (!Directory.Exists(dir)) return list;

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            DateTime taken;
            var name = Path.GetFileName(sub);
            var stampPart = name.Length >= 19 ? name[..19] : name;
            if (!DateTime.TryParseExact(stampPart, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out taken))
                taken = Directory.GetCreationTime(sub);

            long bytes = 0; int files = 0;
            var bookkeeping = BookkeepingTop();
            foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
            {
                // Count only real save files, not the marker or the companion store.
                if (bookkeeping.Contains(TopSegment(Path.GetRelativePath(sub, f)))) continue;
                try { bytes += new FileInfo(f).Length; files++; } catch { /* skip */ }
            }

            string label = "auto";
            var marker = Path.Combine(sub, Marker);
            if (File.Exists(marker))
            {
                try { label = File.ReadAllText(marker).Trim(); } catch { /* keep default */ }
            }

            list.Add(new Snapshot
            {
                FolderPath = sub,
                TakenAt = taken,
                SizeBytes = bytes,
                FileCount = files,
                Label = string.IsNullOrWhiteSpace(label) ? "auto" : label,
            });
        }

        return list.OrderByDescending(s => s.TakenAt).ToList();
    }

    public void DeleteSnapshot(Snapshot snap)
    {
        if (Directory.Exists(snap.FolderPath))
            Directory.Delete(snap.FolderPath, recursive: true);
    }

    /// <summary>Prunes oldest snapshots beyond MaxSnapshots. pre-restore snapshots
    /// are pruned too, but only after auto/manual ones of the same age — they are
    /// counted last so your real backups aren't evicted by safety copies.</summary>
    public void EnforceRotation(GameProfile p)
    {
        if (p.MaxSnapshots <= 0) return;
        var snaps = ListSnapshots(p);
        if (snaps.Count <= p.MaxSnapshots) return;

        // Order so that pre-restore safety copies are the first to go.
        var byEvictionPriority = snaps
            .OrderBy(s => s.Label == "pre-restore" ? 0 : 1)   // pre-restore first out
            .ThenBy(s => s.TakenAt)                            // then oldest
            .ToList();

        int toRemove = snaps.Count - p.MaxSnapshots;
        foreach (var s in byEvictionPriority.Take(toRemove))
        {
            try { DeleteSnapshot(s); } catch { /* best effort */ }
        }
    }

    public string ProfileBackupDir(GameProfile p)
        => Path.Combine(ResolveBackupRoot(p), SanitizeFolderName(p.Name));

    // ---------- file helpers ----------

    /// <summary>Recursively copies sourceDir into destDir. Any relative path whose
    /// first segment is in <paramref name="skipTopLevel"/> is skipped entirely
    /// (used to leave the _companions/ store and the marker out of a restore).</summary>
    private static (int files, long bytes) CopyTree(string sourceDir, string destDir, CancellationToken ct,
        ISet<string>? skipTopLevel = null, IReadOnlyList<string>? excludePatterns = null)
    {
        int files = 0; long bytes = 0;
        bool hasExcludes = excludePatterns is { Count: > 0 };
        Directory.CreateDirectory(destDir);

        // Mirror empty directories so intentionally-empty folders survive. We skip this
        // when excludes are active: pre-creating dirs would leak an empty excluded-subtree
        // folder (e.g. "cache" for "cache/**"). With excludes, directories are instead
        // created lazily as their non-excluded files are copied below.
        if (!hasExcludes)
        {
            foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(sourceDir, dir);
                if (skipTopLevel != null && skipTopLevel.Contains(TopSegment(rel))) continue;
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceDir, file);
            if (skipTopLevel != null && skipTopLevel.Contains(TopSegment(rel))) continue;
            if (hasExcludes && IsExcluded(rel, excludePatterns!)) continue;

            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                CopyFileShared(file, target);
                bytes += new FileInfo(target).Length;
                files++;
            }
            catch (IOException)
            {
                // File is locked and couldn't be read even shared — skip it rather
                // than fail the whole snapshot. Rare for committed save files.
            }
        }

        return (files, bytes);
    }

    /// <summary>The first path segment of a relative path (e.g. "_companions/x" → "_companions").</summary>
    private static string TopSegment(string relativePath)
    {
        int i = relativePath.IndexOfAny(DirSeparators);
        return i < 0 ? relativePath : relativePath[..i];
    }

    private static ISet<string> BookkeepingTop()
        => new HashSet<string>(PathStringComparer) { CompanionDir, Marker };

    // ---------- companion files (outside the watch folder) ----------

    /// <summary>Copies the profile's companion files into &lt;dest&gt;/_companions/ and
    /// writes a manifest of their original absolute paths. Missing files are skipped
    /// (e.g. a flag not yet written); locked files are skipped rather than failing the
    /// whole snapshot. Returns the count/bytes actually captured.</summary>
    private static (int files, long bytes) CaptureCompanions(GameProfile p, string dest, CancellationToken ct)
    {
        var resolved = ResolveCompanionFiles(p.CompanionFileList());
        if (resolved.Count == 0) return (0, 0);

        var compDir = Path.Combine(dest, CompanionDir);
        Directory.CreateDirectory(compDir);

        var entries = new List<CompanionEntry>();
        int files = 0; long bytes = 0;
        for (int i = 0; i < resolved.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = resolved[i];
            // Prefix keeps same-named files from different folders from colliding.
            var stored = $"{i:000}_{Path.GetFileName(src)}";
            var target = Path.Combine(compDir, stored);
            try
            {
                CopyFileShared(src, target);
                bytes += new FileInfo(target).Length;
                files++;
                entries.Add(new CompanionEntry { Stored = stored, Original = Path.GetFullPath(src) });
            }
            catch (IOException) { /* locked — skip */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        if (entries.Count > 0)
            try { File.WriteAllText(Path.Combine(compDir, CompanionManifest), JsonSerializer.Serialize(entries, JsonOpts)); }
            catch { /* non-fatal */ }
        else
            try { Directory.Delete(compDir, recursive: true); } catch { /* nothing captured */ }

        return (files, bytes);
    }

    /// <summary>Reads a snapshot's companion manifest and copies each stored file back
    /// to its original absolute path (creating the target directory if needed).</summary>
    private static void RestoreCompanions(string snapshotDir)
    {
        var manifestPath = Path.Combine(snapshotDir, CompanionDir, CompanionManifest);
        if (!File.Exists(manifestPath)) return;

        List<CompanionEntry>? entries;
        try { entries = JsonSerializer.Deserialize<List<CompanionEntry>>(File.ReadAllText(manifestPath)); }
        catch { return; }
        if (entries == null) return;

        foreach (var e in entries)
        {
            // e can be null if a manifest was hand-edited to contain a JSON `null`.
            if (e is null || string.IsNullOrWhiteSpace(e.Stored) || string.IsNullOrWhiteSpace(e.Original)) continue;
            var stored = Path.Combine(snapshotDir, CompanionDir, e.Stored);
            if (!File.Exists(stored)) continue;
            try
            {
                var targetDir = Path.GetDirectoryName(e.Original);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                CopyFileShared(stored, e.Original);
            }
            catch (IOException) { /* target locked — skip */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }
    }

    /// <summary>Expands the companion patterns into a de-duplicated list of absolute file paths.</summary>
    private static List<string> ResolveCompanionFiles(IReadOnlyList<string> patterns)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathStringComparer);
        foreach (var pattern in patterns)
            foreach (var f in ExpandPattern(pattern))
            {
                var full = Path.GetFullPath(f);
                if (seen.Add(full)) result.Add(full);
            }
        return result;
    }

    /// <summary>One pattern → the files it matches. No wildcard = the literal file (if it
    /// exists); "**" = recursive under the base dir; "*"/"?" = that directory only.</summary>
    private static IEnumerable<string> ExpandPattern(string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return Array.Empty<string>();

        if (pattern.IndexOfAny(WildChars) < 0)
            return File.Exists(pattern) ? new[] { pattern } : Array.Empty<string>();

        try
        {
            if (pattern.Contains("**"))
            {
                int idx = pattern.IndexOf("**", StringComparison.Ordinal);
                var baseDir = pattern[..idx].TrimEnd(DirSeparators);
                var filePart = pattern[(idx + 2)..].TrimStart(DirSeparators);
                if (filePart.Length == 0) filePart = "*";
                if (baseDir.Length == 0 || !Directory.Exists(baseDir)) return Array.Empty<string>();
                return Directory.GetFiles(baseDir, filePart, SearchOption.AllDirectories);
            }

            var dir = Path.GetDirectoryName(pattern);
            var name = Path.GetFileName(pattern);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, name, SearchOption.TopDirectoryOnly);
        }
        catch { return Array.Empty<string>(); }
    }

    // ---------- exclude patterns (inside the watch folder) ----------

    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new();

    /// <summary>
    /// True if a file at <paramref name="relativePath"/> (relative to the watch folder)
    /// matches any exclude pattern. A pattern with no "/" matches a path segment — a file
    /// OR folder name — at any depth (e.g. <c>*.log</c>, <c>Thumbs.db</c>, <c>cache</c>);
    /// a pattern with "/" matches the relative path from the watch folder, where <c>**</c>
    /// spans subfolders and <c>*</c>/<c>?</c> stay within one segment (e.g. <c>logs/*.txt</c>,
    /// <c>cache/**</c>). A trailing "/" restricts a name pattern to folders.
    /// </summary>
    public static bool IsExcluded(string relativePath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return false;
        var rel = relativePath.Replace('\\', '/').Trim('/');
        if (rel.Length == 0) return false;

        var segments = rel.Split('/');

        foreach (var raw in patterns)
        {
            var pat = raw.Trim().Replace('\\', '/');
            bool dirOnly = pat.EndsWith('/');
            pat = pat.Trim('/');
            if (pat.Length == 0) continue;

            // Refuse a "match everything" pattern (bare * / ** / ***): excluding the whole
            // folder would silently produce empty backups, so treat it as a no-op and keep
            // backing up. Scoped broad excludes like "cache/**" still work (they have a "/").
            if (!pat.Contains('/') && pat.All(c => c == '*')) continue;

            if (pat.Contains('/'))
            {
                // Path pattern anchored at the watch root, plus its whole subtree. A
                // trailing "/" means folder-only, so it must NOT match a file at that path.
                if (!dirOnly && GlobMatch(rel, pat)) return true;
                if (rel.StartsWith(pat + "/", PathComparison)) return true;
            }
            else
            {
                // Name pattern: match any segment (file or folder) by name.
                for (int i = 0; i < segments.Length; i++)
                {
                    if (!GlobMatch(segments[i], pat)) continue;
                    // "name/" must match a parent folder, never the file itself.
                    if (!dirOnly || i < segments.Length - 1) return true;
                }
            }
        }
        return false;
    }

    private static bool GlobMatch(string input, string glob) => GlobCache.GetOrAdd(glob, BuildGlobRegex).IsMatch(input);

    private static Regex BuildGlobRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; } // ** spans separators
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');

        // NonBacktracking guarantees linear-time matching, so a user-typed glob can never
        // cause catastrophic backtracking (ReDoS) when checked against thousands of files.
        var opts = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows()) opts |= RegexOptions.IgnoreCase;
        return new Regex(sb.ToString(), opts);
    }

    /// <summary>Copy allowing the source to be open elsewhere (game may hold it).</summary>
    private static void CopyFileShared(string source, string dest)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    /// <summary>Empties a folder before a restore. Excluded files are LEFT IN PLACE —
    /// SaveGuard never backs them up and never deletes them on restore.</summary>
    private static void ClearDirectoryContents(string dir, IReadOnlyList<string> excludePatterns)
    {
        if (excludePatterns.Count == 0)
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { /* skip */ }
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                try { Directory.Delete(d, recursive: true); } catch { /* skip */ }
            }
            return;
        }

        // Exclude-aware: delete everything that isn't excluded, then prune empty dirs
        // (keeping any folder that still holds a preserved excluded file).
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            if (IsExcluded(Path.GetRelativePath(dir, f), excludePatterns)) continue;
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { /* skip */ }
        }
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                                   .OrderByDescending(x => x.Length).ToList())
        {
            // An excluded directory is left untouched, even when empty.
            if (IsExcluded(Path.GetRelativePath(dir, d), excludePatterns)) continue;
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { /* skip */ }
        }
    }

    // ---------- path helpers ----------

    private static string NormalizeDir(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, PathComparison);

    /// <summary>True if <paramref name="child"/> is inside <paramref name="parent"/>.</summary>
    private static bool IsInside(string child, string parent)
    {
        var p = parent + Path.DirectorySeparatorChar;
        return child.StartsWith(p, PathComparison);
    }

    private static bool IsDriveRoot(string dir)
    {
        var root = Path.GetPathRoot(dir);
        return !string.IsNullOrEmpty(root) && PathsEqual(NormalizeDir(root), dir);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrEmpty(cleaned) ? "profile" : cleaned;
    }
}
