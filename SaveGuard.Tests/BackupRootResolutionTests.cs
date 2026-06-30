using System;
using System.IO;
using System.Threading.Tasks;
using SaveGuard.Models;
using SaveGuard.Services;
using Xunit;

namespace SaveGuard.Tests;

public class ResolveBackupRootTests
{
    private static GameProfile P(string backupRoot) => new() { Name = "G", BackupRoot = backupRoot };

    [Fact]
    public void No_global_returns_the_per_game_value()
    {
        var e = new BackupEngine { GlobalBackupRoot = "" };
        Assert.Equal(@"D:\X", e.ResolveBackupRoot(P(@"D:\X")));
        Assert.Equal("sub", e.ResolveBackupRoot(P("sub")));
        Assert.Equal("", e.ResolveBackupRoot(P("")));
    }

    [Fact]
    public void Global_set_resolves_relative_and_blank_under_it()
    {
        var e = new BackupEngine { GlobalBackupRoot = @"D:\Shared" };
        Assert.Equal(@"D:\Shared", e.ResolveBackupRoot(P("")));
        Assert.Equal(Path.Combine(@"D:\Shared", "BG3"), e.ResolveBackupRoot(P("BG3")));
    }

    [Fact]
    public void Per_game_absolute_overrides_the_global_folder()
    {
        var e = new BackupEngine { GlobalBackupRoot = @"D:\Shared" };
        Assert.Equal(@"E:\Own", e.ResolveBackupRoot(P(@"E:\Own")));
    }
}

public sealed class ValidateProfileBackupRootTests : IDisposable
{
    private readonly string _root;
    private readonly string _watch;
    private readonly BackupEngine _engine = new();

    public ValidateProfileBackupRootTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sgvr_" + Guid.NewGuid().ToString("N"));
        _watch = Path.Combine(_root, "save");
        Directory.CreateDirectory(_watch);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private GameProfile P(string backupRoot) => new() { Name = "G", WatchPath = _watch, BackupRoot = backupRoot };

    [Fact]
    public void Relative_or_blank_without_a_global_folder_is_rejected()
    {
        _engine.GlobalBackupRoot = "";
        Assert.NotNull(_engine.ValidateProfile(P("sub"))); // relative, no shared folder
        Assert.NotNull(_engine.ValidateProfile(P("")));    // blank
    }

    [Fact]
    public void Absolute_without_a_global_folder_is_ok()
    {
        _engine.GlobalBackupRoot = "";
        Assert.Null(_engine.ValidateProfile(P(Path.Combine(_root, "backups"))));
    }

    [Fact]
    public void Relative_or_blank_with_a_global_folder_is_ok()
    {
        _engine.GlobalBackupRoot = Path.Combine(_root, "shared");
        Assert.Null(_engine.ValidateProfile(P("sub")));
        Assert.Null(_engine.ValidateProfile(P("")));
    }
}

public sealed class SharedBackupRootSnapshotTests : IDisposable
{
    private readonly string _root;
    private readonly string _watch;
    private readonly string _shared;
    private readonly BackupEngine _engine = new();

    public SharedBackupRootSnapshotTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sgsr_" + Guid.NewGuid().ToString("N"));
        _watch = Path.Combine(_root, "save");
        _shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(_watch);
        File.WriteAllText(Path.Combine(_watch, "a.sav"), "v1");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public async Task Snapshot_lands_under_shared_then_subfolder_then_name()
    {
        _engine.GlobalBackupRoot = _shared;
        var p = new GameProfile { Name = "MyGame", WatchPath = _watch, BackupRoot = "sub", MaxSnapshots = 20 };

        var snap = await _engine.CreateSnapshotAsync(p, "auto", TestContext.Current.CancellationToken);

        Assert.StartsWith(Path.Combine(_shared, "sub", "MyGame"), snap.FolderPath);
        Assert.True(File.Exists(Path.Combine(snap.FolderPath, "a.sav")));
    }

    [Fact]
    public async Task Blank_per_game_path_lands_directly_under_shared()
    {
        _engine.GlobalBackupRoot = _shared;
        var p = new GameProfile { Name = "MyGame", WatchPath = _watch, BackupRoot = "", MaxSnapshots = 20 };

        var snap = await _engine.CreateSnapshotAsync(p, "auto", TestContext.Current.CancellationToken);

        Assert.StartsWith(Path.Combine(_shared, "MyGame"), snap.FolderPath);
    }
}
