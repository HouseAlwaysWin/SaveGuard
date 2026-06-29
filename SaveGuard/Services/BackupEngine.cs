using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Validates a profile's paths. Returns null if OK, otherwise an error
    /// message. The key invariant: BackupRoot must never sit inside WatchPath
    /// (that would make us back up our own backups, recursively).
    /// </summary>
    public static string? ValidateProfile(GameProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.WatchPath))
            return "Watch folder is not set.";
        if (string.IsNullOrWhiteSpace(p.BackupRoot))
            return "Backup folder is not set.";
        if (!Directory.Exists(p.WatchPath))
            return $"Watch folder does not exist:\n{p.WatchPath}";

        var watch = NormalizeDir(p.WatchPath);
        var backup = NormalizeDir(p.BackupRoot);

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

            var (files, bytes) = CopyTree(p.WatchPath, dest, ct);

            // Write a tiny marker so labels survive restarts.
            try { File.WriteAllText(Path.Combine(dest, ".saveguard"), label); } catch { /* non-fatal */ }

            EnforceRotation(p);

            return new Snapshot
            {
                FolderPath = dest,
                TakenAt = DateTime.Now,
                SizeBytes = bytes,
                FileCount = files,
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

            // The game may have deleted the entire save folder (e.g. BG3 wipes an
            // Honour-mode save on death) — that is *exactly* when a restore is needed,
            // so we recreate the folder rather than refusing. A pre-restore safety
            // snapshot is only worth taking when there's actually a live save to
            // preserve; restoring over an already-gone save has nothing to back up.
            if (Directory.Exists(p.WatchPath) &&
                Directory.EnumerateFileSystemEntries(p.WatchPath).Any())
            {
                CreateSnapshotAsync(p, "pre-restore", ct).GetAwaiter().GetResult();
                ClearDirectoryContents(p.WatchPath);
            }
            else
            {
                Directory.CreateDirectory(p.WatchPath); // recreate the deleted/empty folder
            }

            // Copy the chosen snapshot back in, skipping our marker file.
            CopyTree(snap.FolderPath, p.WatchPath, ct, skipName: ".saveguard");
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
            foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f) == ".saveguard") continue;
                try { bytes += new FileInfo(f).Length; files++; } catch { /* skip */ }
            }

            string label = "auto";
            var marker = Path.Combine(sub, ".saveguard");
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
        => Path.Combine(p.BackupRoot, SanitizeFolderName(p.Name));

    // ---------- file helpers ----------

    private static (int files, long bytes) CopyTree(string sourceDir, string destDir, CancellationToken ct, string? skipName = null)
    {
        int files = 0; long bytes = 0;
        Directory.CreateDirectory(destDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (skipName != null && Path.GetFileName(file) == skipName) continue;

            var rel = Path.GetRelativePath(sourceDir, file);
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

    /// <summary>Copy allowing the source to be open elsewhere (game may hold it).</summary>
    private static void CopyFileShared(string source, string dest)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    private static void ClearDirectoryContents(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { /* skip */ }
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(d, recursive: true); } catch { /* skip */ }
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

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrEmpty(cleaned) ? "profile" : cleaned;
    }
}
