using System;
using System.IO;
using System.Threading.Tasks;
using SaveGuard.Models;
using SaveGuard.Services;
using Xunit;

namespace SaveGuard.Tests;

public class ExcludeMatcherTests
{
    private static bool Excl(string rel, params string[] patterns) => BackupEngine.IsExcluded(rel, patterns);

    [Fact]
    public void No_patterns_excludes_nothing()
    {
        Assert.False(BackupEngine.IsExcluded("a.log", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("debug.log")]
    [InlineData("sub/debug.log")]
    [InlineData("a/b/c/debug.log")]
    public void Name_glob_matches_basename_at_any_depth(string rel)
    {
        Assert.True(Excl(rel, "*.log"));
    }

    [Fact]
    public void Name_glob_does_not_match_other_extensions()
    {
        Assert.False(Excl("save.dat", "*.log"));
        Assert.False(Excl("logs/save.dat", "*.log"));
    }

    [Fact]
    public void Bare_name_matches_file_or_folder_at_any_depth()
    {
        Assert.True(Excl("Thumbs.db", "Thumbs.db"));
        Assert.True(Excl("sub/Thumbs.db", "Thumbs.db"));
        // "cache" as a folder name excludes everything under it.
        Assert.True(Excl("cache/x.tmp", "cache"));
        Assert.True(Excl("a/cache/x.tmp", "cache"));
    }

    [Fact]
    public void Trailing_slash_restricts_to_folders()
    {
        Assert.True(Excl("cache/x.tmp", "cache/"));   // cache is a parent folder
        Assert.False(Excl("cache", "cache/"));        // a file literally named "cache" is kept
        Assert.True(Excl("cache", "cache"));          // without the slash it matches
    }

    [Fact]
    public void Path_glob_is_anchored_and_single_star_stays_in_segment()
    {
        Assert.True(Excl("logs/a.txt", "logs/*.txt"));
        Assert.False(Excl("logs/sub/a.txt", "logs/*.txt")); // * does not span '/'
        Assert.False(Excl("other/a.txt", "logs/*.txt"));    // anchored at the root
    }

    [Fact]
    public void Double_star_spans_subfolders()
    {
        Assert.True(Excl("cache/a", "cache/**"));
        Assert.True(Excl("cache/a/b/c.tmp", "cache/**"));
        Assert.False(Excl("cachex/a", "cache/**"));
    }

    [Fact]
    public void Path_pattern_excludes_its_whole_subtree()
    {
        Assert.True(Excl("mods/disabled/x.pak", "mods/disabled"));
        Assert.True(Excl("mods/disabled", "mods/disabled"));
        Assert.False(Excl("mods/enabled/x.pak", "mods/disabled"));
    }

    [Fact]
    public void Backslashes_in_input_are_normalized()
    {
        Assert.True(Excl(@"sub\debug.log", "*.log"));
        Assert.True(Excl(@"cache\x.tmp", "cache/**"));
    }

    [Fact]
    public void Blank_patterns_are_ignored()
    {
        Assert.False(Excl("a.log", "", "   "));
    }

    [Fact]
    public void Matching_is_case_insensitive_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(Excl("DEBUG.LOG", "*.log"));
        Assert.True(Excl("a.log", "*.LOG"));
    }

    [Fact]
    public void Match_all_star_patterns_are_ignored_so_backups_are_never_wiped()
    {
        Assert.False(Excl("save.dat", "*"));
        Assert.False(Excl("a/b/c.txt", "**"));
        Assert.False(Excl("x", "***"));
        Assert.False(Excl("deep/file.dat", "**/"));
        // Scoped broad excludes still work — only the unanchored whole-tree token is a no-op.
        Assert.True(Excl("cache/x", "cache/**"));
        Assert.True(Excl("a.log", "*.log"));
    }

    [Fact]
    public void Trailing_slash_path_pattern_excludes_subtree_not_the_file()
    {
        Assert.False(Excl("a/b", "a/b/"));       // a file literally at a/b is kept
        Assert.True(Excl("a/b/c.dat", "a/b/"));  // but its subtree is excluded
    }

    [Fact]
    public void Pathological_pattern_does_not_hang()
    {
        // A catastrophic-backtracking shape against a long non-matching input must
        // return quickly (NonBacktracking guarantees linear time), not freeze.
        var evil = string.Concat(System.Linq.Enumerable.Repeat("*a", 20));
        var input = new string('a', 60) + "/x";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = Excl(input, evil);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"matching took {sw.ElapsedMilliseconds}ms");
    }
}

public sealed class BackupExcludeIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _watch;
    private readonly string _backup;
    private readonly BackupEngine _engine = new();

    public BackupExcludeIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sgexcl_" + Guid.NewGuid().ToString("N"));
        _watch = Path.Combine(_root, "save");
        _backup = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_watch);
        Directory.CreateDirectory(_backup);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GameProfile Profile(string exclude) => new()
    {
        Name = "T",
        WatchPath = _watch,
        BackupRoot = _backup,
        AutoWatch = false,
        MaxSnapshots = 20,
        ExcludePatterns = exclude,
    };

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_watch, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    [Fact]
    public async Task Snapshot_omits_excluded_files()
    {
        Write("save.dat", "v1");
        Write("debug.log", "noise");
        Write("cache/c.tmp", "tmp");

        var snap = await _engine.CreateSnapshotAsync(Profile("*.log\ncache"), "auto",
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(snap.FolderPath, "save.dat")));
        Assert.False(File.Exists(Path.Combine(snap.FolderPath, "debug.log")));
        Assert.False(File.Exists(Path.Combine(snap.FolderPath, "cache", "c.tmp")));
    }

    [Fact]
    public async Task Restore_preserves_excluded_live_file()
    {
        Write("data.sav", "v1");
        Write("keep.cfg", "live");

        var profile = Profile("keep.cfg");
        var snap = await _engine.CreateSnapshotAsync(profile, "auto", TestContext.Current.CancellationToken);

        // The excluded file was never captured.
        Assert.False(File.Exists(Path.Combine(snap.FolderPath, "keep.cfg")));

        // Change the live folder, then restore the snapshot.
        File.WriteAllText(Path.Combine(_watch, "data.sav"), "v2");
        File.WriteAllText(Path.Combine(_watch, "keep.cfg"), "live-modified");

        await _engine.RestoreAsync(profile, snap, TestContext.Current.CancellationToken);

        // data.sav rolled back; keep.cfg left exactly as it was (not deleted, not overwritten).
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_watch, "data.sav")));
        Assert.Equal("live-modified", File.ReadAllText(Path.Combine(_watch, "keep.cfg")));
    }

    [Fact]
    public async Task Restore_does_not_overwrite_an_excluded_file_an_old_snapshot_still_contains()
    {
        // Snapshot taken BEFORE the exclude existed — it DOES contain keep.cfg.
        Write("data.sav", "v1");
        Write("keep.cfg", "snapshot-version");
        var snap = await _engine.CreateSnapshotAsync(Profile(""), "auto", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(snap.FolderPath, "keep.cfg")));

        // Now exclude keep.cfg and change the live copy, then restore the old snapshot.
        File.WriteAllText(Path.Combine(_watch, "data.sav"), "v2");
        File.WriteAllText(Path.Combine(_watch, "keep.cfg"), "live-version");

        await _engine.RestoreAsync(Profile("keep.cfg"), snap, TestContext.Current.CancellationToken);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(_watch, "data.sav")));          // rolled back
        Assert.Equal("live-version", File.ReadAllText(Path.Combine(_watch, "keep.cfg"))); // NOT clobbered
    }

    [Fact]
    public async Task Star_exclude_is_a_no_op_and_does_not_wipe_the_snapshot()
    {
        Write("save.dat", "v1");
        Write("more.dat", "v2");

        var snap = await _engine.CreateSnapshotAsync(Profile("*"), "auto", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(snap.FolderPath, "save.dat")));
        Assert.True(File.Exists(Path.Combine(snap.FolderPath, "more.dat")));
    }

    [Fact]
    public async Task Snapshot_does_not_leak_an_empty_excluded_subtree_directory()
    {
        Write("save.dat", "v1");
        Write("cache/c.tmp", "tmp");

        var snap = await _engine.CreateSnapshotAsync(Profile("cache/**"), "auto", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(snap.FolderPath, "save.dat")));
        Assert.False(File.Exists(Path.Combine(snap.FolderPath, "cache", "c.tmp")));
        Assert.False(Directory.Exists(Path.Combine(snap.FolderPath, "cache"))); // no empty-dir leak
    }

    [Fact]
    public async Task Restore_preserves_an_excluded_empty_directory()
    {
        Write("data.sav", "v1");
        Directory.CreateDirectory(Path.Combine(_watch, "logs")); // empty + excluded

        var profile = Profile("logs");
        var snap = await _engine.CreateSnapshotAsync(profile, "auto", TestContext.Current.CancellationToken);

        File.WriteAllText(Path.Combine(_watch, "data.sav"), "v2");
        await _engine.RestoreAsync(profile, snap, TestContext.Current.CancellationToken);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(_watch, "data.sav")));
        Assert.True(Directory.Exists(Path.Combine(_watch, "logs"))); // left untouched, not pruned
    }
}
