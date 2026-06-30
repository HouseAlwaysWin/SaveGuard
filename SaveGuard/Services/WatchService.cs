using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SaveGuard.Models;

namespace SaveGuard.Services;

/// <summary>
/// Owns one FileSystemWatcher per auto-watched profile. Raw FS events are noisy:
/// a single save can fire many Changed events, and an event can arrive while the
/// file is still being written. We debounce — wait for a quiet period after the
/// last event before snapshotting — so we copy a settled folder exactly once.
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly BackupEngine _engine;
    private readonly Dictionary<string, Watch> _watches = new();
    private readonly object _gate = new();

    /// <summary>Raised on the thread-pool when an auto-backup completes (or fails).</summary>
    public event Action<GameProfile, Snapshot?, Exception?>? BackupCompleted;

    public WatchService(BackupEngine engine) => _engine = engine;

    public bool IsWatching(GameProfile p)
    {
        lock (_gate) return _watches.ContainsKey(p.Id);
    }

    public void Start(GameProfile p)
    {
        if (!p.AutoWatch) return;
        if (_engine.ValidateProfile(p) != null) return;

        lock (_gate)
        {
            if (_watches.ContainsKey(p.Id)) return;

            var fsw = new FileSystemWatcher(p.WatchPath)
            {
                IncludeSubdirectories = p.Recursive,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                             | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };

            var watch = new Watch(p, fsw);
            fsw.Changed += (_, e) => watch.OnEvent(e.FullPath);
            fsw.Created += (_, e) => watch.OnEvent(e.FullPath);
            fsw.Renamed += (_, e) => watch.OnEvent(e.FullPath);
            fsw.Deleted += (_, e) => watch.OnEvent(e.FullPath);
            watch.Fire = () => RunBackup(p);

            fsw.EnableRaisingEvents = true;
            _watches[p.Id] = watch;
        }
    }

    public void Stop(GameProfile p)
    {
        lock (_gate)
        {
            if (_watches.Remove(p.Id, out var w))
                w.Dispose();
        }
    }

    /// <summary>Re-evaluate a profile after its settings changed.</summary>
    public void Refresh(GameProfile p)
    {
        Stop(p);
        if (p.AutoWatch) Start(p);
    }

    private async void RunBackup(GameProfile p)
    {
        try
        {
            var snap = await _engine.CreateSnapshotAsync(p, "auto").ConfigureAwait(false);
            BackupCompleted?.Invoke(p, snap, null);
        }
        catch (Exception ex)
        {
            BackupCompleted?.Invoke(p, null, ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var w in _watches.Values) w.Dispose();
            _watches.Clear();
        }
    }

    /// <summary>Per-profile watcher state + debounce timer.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly GameProfile _p;
        private readonly FileSystemWatcher _fsw;
        private readonly IReadOnlyList<string> _exts;
        private readonly IReadOnlyList<string> _excludes;
        private readonly string _root;
        private readonly Timer _timer;
        public Action? Fire;

        public Watch(GameProfile p, FileSystemWatcher fsw)
        {
            _p = p;
            _fsw = fsw;
            _exts = p.TriggerExtensionList();
            _excludes = p.ExcludeList();
            _root = p.WatchPath;
            _timer = new Timer(_ => Fire?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void OnEvent(string fullPath)
        {
            // If an extension filter is set, ignore changes to other file types.
            if (_exts.Count > 0)
            {
                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (!_exts.Contains(ext)) return;
            }
            // Don't let an excluded file (e.g. a churning log) trigger a backup.
            if (_excludes.Count > 0)
            {
                string rel;
                try { rel = Path.GetRelativePath(_root, fullPath); } catch { rel = fullPath; }
                if (!rel.StartsWith("..", StringComparison.Ordinal) &&
                    BackupEngine.IsExcluded(rel, _excludes)) return;
            }
            // Reset the quiet timer: only fire once writes go silent.
            _timer.Change(_p.DebounceMs, Timeout.Infinite);
        }

        public void Dispose()
        {
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
            _timer.Dispose();
        }
    }
}
