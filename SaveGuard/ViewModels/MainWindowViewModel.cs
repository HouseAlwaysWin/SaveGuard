using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveGuard.Models;
using SaveGuard.Services;

namespace SaveGuard.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly BackupEngine _engine;
    private readonly WatchService _watcher;
    private readonly UiStateStore _uiStore;
    private readonly UiState _ui;

    // ---- auto-save (debounced) ----
    private readonly DispatcherTimer _saveTimer;
    private bool _watcherDirty;            // a watcher-relevant field changed since last flush
    private GameProfile? _pendingProfile;  // the profile whose watcher needs refreshing

    /// <summary>Profile fields that, when changed, require rebuilding the file watcher.</summary>
    private static readonly HashSet<string> WatcherProps = new()
    {
        nameof(GameProfile.WatchPath), nameof(GameProfile.Recursive),
        nameof(GameProfile.TriggerExtensions), nameof(GameProfile.DebounceMs),
        nameof(GameProfile.AutoWatch),
    };

    /// <summary>Set by the View. Opens a folder picker, returns the chosen path or null.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the View. Shows a yes/no confirm dialog.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    public ObservableCollection<GameProfile> Profiles { get; } = new();
    public ObservableCollection<Snapshot> Snapshots { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    [NotifyPropertyChangedFor(nameof(WatchStateLabel))]
    private GameProfile? _selectedProfile;

    [ObservableProperty] private Snapshot? _selectedSnapshot;
    [ObservableProperty] private string _status = "Ready.";
    [ObservableProperty] private bool _busy;

    /// <summary>Thumbnail of the selected backup (or the live save folder when no
    /// backup is selected), if one matching the profile's image types is found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewImage))]
    private Bitmap? _previewImage;

    // Accordion fold state — persisted so it survives restart / tray hide-restore.
    [ObservableProperty] private bool _foldersExpanded;
    [ObservableProperty] private bool _optionsExpanded;
    [ObservableProperty] private bool _backupsExpanded;

    public bool HasProfile => SelectedProfile != null;

    public bool HasPreviewImage => PreviewImage != null;

    public string WatchStateLabel
    {
        get
        {
            if (SelectedProfile == null) return "";
            if (!SelectedProfile.AutoWatch) return "Auto-backup off";
            return _watcher.IsWatching(SelectedProfile) ? "Watching — backs up on save" : "Auto-backup on (idle)";
        }
    }

    public MainWindowViewModel(ProfileStore store, BackupEngine engine, WatchService watcher, UiStateStore uiStore)
    {
        _store = store;
        _engine = engine;
        _watcher = watcher;
        _uiStore = uiStore;

        _watcher.BackupCompleted += OnAutoBackupCompleted;

        // Restore persisted accordion fold state (set backing fields directly so the
        // generated change handlers don't re-save during construction).
        _ui = _uiStore.Load();
        _foldersExpanded = _ui.FoldersExpanded;
        _optionsExpanded = _ui.OptionsExpanded;
        _backupsExpanded = _ui.BackupsExpanded;

        foreach (var p in _store.Load())
            Profiles.Add(p);

        SelectedProfile = Profiles.FirstOrDefault();

        // Begin watching any auto-watch profiles.
        foreach (var p in Profiles)
            _watcher.Start(p);

        // Auto-save: persist (and refresh the watcher) shortly after edits settle,
        // so typing doesn't write on every keystroke or thrash the FileSystemWatcher.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => FlushSave();
        Profiles.CollectionChanged += OnProfilesChanged;
        foreach (var p in Profiles) Hook(p);
    }

    partial void OnSelectedProfileChanged(GameProfile? value) => RefreshSnapshots();

    partial void OnSelectedSnapshotChanged(Snapshot? value) => RefreshPreview();

    private void OnAutoBackupCompleted(GameProfile p, Snapshot? snap, Exception? err)
    {
        // Marshal to the UI thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (err != null)
            {
                Status = $"Auto-backup failed for {p.Name}: {err.Message}";
                return;
            }
            Status = $"Auto-backed up {p.Name} at {snap!.TakenAt:HH:mm:ss} ({snap.FileCount} files).";
            if (ReferenceEquals(p, SelectedProfile)) RefreshSnapshots();
        });
    }

    private void RefreshSnapshots()
    {
        Snapshots.Clear();
        if (SelectedProfile == null) { RefreshPreview(); return; }
        if (BackupEngine.ValidateProfile(SelectedProfile) != null) { RefreshPreview(); return; }
        foreach (var s in _engine.ListSnapshots(SelectedProfile))
            Snapshots.Add(s);
        OnPropertyChanged(nameof(WatchStateLabel));
        RefreshPreview();
    }

    /// <summary>
    /// Loads the preview thumbnail: the newest image inside the selected snapshot,
    /// or — when no snapshot is selected — inside the live save folder. Silently
    /// shows nothing when the profile has no image types or no image is found.
    /// </summary>
    private void RefreshPreview()
    {
        string? folder = SelectedSnapshot?.FolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            folder = SelectedProfile?.WatchPath;

        var exts = SelectedProfile?.ImageExtensionList() ?? Array.Empty<string>();
        SetPreview(FindNewestImage(folder, exts));
    }

    private void SetPreview(string? path)
    {
        var previous = PreviewImage;
        Bitmap? next = null;
        if (path != null)
        {
            try
            {
                // Decode fully into memory so we don't keep a handle on the file.
                using var fs = File.OpenRead(path);
                next = new Bitmap(fs);
            }
            catch { next = null; }
        }
        PreviewImage = next;
        previous?.Dispose();
    }

    private static string? FindNewestImage(string? folder, IReadOnlyList<string> exts)
    {
        if (string.IsNullOrWhiteSpace(folder) || exts.Count == 0 || !Directory.Exists(folder))
            return null;
        try
        {
            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!exts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { bestTime = t; best = f; }
            }
            return best;
        }
        catch { return null; }
    }

    // ---------- profile commands ----------

    [RelayCommand]
    private void AddProfile()
    {
        var p = new GameProfile
        {
            Name = "New profile",
            BackupRoot = _store.DefaultBackupRoot,
            AutoWatch = false, // off until the user points it somewhere valid
        };
        Profiles.Add(p);
        SelectedProfile = p;
        Status = "Set the watch folder to start protecting its saves.";
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile == null) return;
        var ok = Confirm == null || await Confirm("Delete profile",
            $"Remove \"{SelectedProfile.Name}\" from SaveGuard?\n\nYour existing backup files are NOT deleted.");
        if (!ok) return;

        _watcher.Stop(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        Persist();
        Status = "Profile removed. Backup files were left on disk.";
    }

    [RelayCommand]
    private async Task BrowseWatch()
    {
        if (SelectedProfile == null || PickFolder == null) return;
        var path = await PickFolder("Choose the game's save folder");
        if (path != null) SelectedProfile.WatchPath = path;
    }

    [RelayCommand]
    private async Task BrowseBackup()
    {
        if (SelectedProfile == null || PickFolder == null) return;
        var path = await PickFolder("Choose where to store backups");
        if (path != null) SelectedProfile.BackupRoot = path;
    }

    // ---------- backup / restore commands ----------

    [RelayCommand]
    private async Task BackupNow()
    {
        if (SelectedProfile == null) return;
        var err = BackupEngine.ValidateProfile(SelectedProfile);
        if (err != null) { Status = err; return; }

        Busy = true;
        Status = "Backing up…";
        try
        {
            var snap = await _engine.CreateSnapshotAsync(SelectedProfile, "manual");
            RefreshSnapshots();
            Status = $"Backed up {snap.FileCount} files at {snap.TakenAt:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            Status = $"Backup failed: {ex.Message}";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (SelectedProfile == null || SelectedSnapshot == null) return;

        var ok = Confirm == null || await Confirm("Restore this backup",
            $"Replace the current save folder with the backup from\n{SelectedSnapshot.TakenAtDisplay}?\n\n" +
            "Your current saves will be snapshotted first (labelled \"pre-restore\"), " +
            "so this can be undone.");
        if (!ok) return;

        Busy = true;
        Status = "Restoring…";
        try
        {
            // Pause auto-watch so the restore's writes don't fire a spurious backup.
            var wasWatching = _watcher.IsWatching(SelectedProfile);
            if (wasWatching) _watcher.Stop(SelectedProfile);

            await _engine.RestoreAsync(SelectedProfile, SelectedSnapshot);

            if (wasWatching) _watcher.Start(SelectedProfile);
            RefreshSnapshots();
            Status = $"Restored backup from {SelectedSnapshot.TakenAtDisplay}. A pre-restore safety copy was saved.";
        }
        catch (Exception ex)
        {
            Status = $"Restore failed: {ex.Message}";
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task DeleteSnapshot()
    {
        if (SelectedSnapshot == null) return;
        var ok = Confirm == null || await Confirm("Delete backup",
            $"Permanently delete the backup from {SelectedSnapshot.TakenAtDisplay}?");
        if (!ok) return;

        try
        {
            _engine.DeleteSnapshot(SelectedSnapshot);
            RefreshSnapshots();
            Status = "Backup deleted.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't delete: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (SelectedProfile == null) return;
        var dir = _engine.ProfileBackupDir(SelectedProfile);
        Directory.CreateDirectory(dir);
        OpenInFileManager(dir);
    }

    [RelayCommand]
    private void OpenSnapshotFolder()
    {
        if (SelectedSnapshot == null) return;
        OpenInFileManager(SelectedSnapshot.FolderPath);
    }

    private static void OpenInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", $"\"{path}\"");
            else
                Process.Start("xdg-open", $"\"{path}\"");
        }
        catch { /* non-fatal */ }
    }

    private void Persist() => _store.Save(Profiles);

    // ---------- auto-save plumbing ----------

    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (GameProfile p in e.OldItems) Unhook(p);
        if (e.NewItems != null) foreach (GameProfile p in e.NewItems) Hook(p);
        ScheduleSave(watcherRelevant: false, profile: null); // the list shape changed → persist
    }

    private void Hook(GameProfile p) => p.PropertyChanged += OnProfilePropertyChanged;
    private void Unhook(GameProfile p) => p.PropertyChanged -= OnProfilePropertyChanged;

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var p = sender as GameProfile;
        bool watcherRelevant = e.PropertyName != null && WatcherProps.Contains(e.PropertyName);
        // Reflect Name/AutoWatch edits in the header label immediately (the watcher
        // itself only rebuilds on the debounced flush).
        if (ReferenceEquals(p, SelectedProfile) &&
            (e.PropertyName == nameof(GameProfile.Name) || e.PropertyName == nameof(GameProfile.AutoWatch)))
            OnPropertyChanged(nameof(WatchStateLabel));
        ScheduleSave(watcherRelevant, p);
    }

    /// <summary>Restart the quiet-window timer; remember if a watcher rebuild is owed.</summary>
    private void ScheduleSave(bool watcherRelevant, GameProfile? profile)
    {
        if (watcherRelevant) { _watcherDirty = true; _pendingProfile = profile; }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        Persist();
        if (_watcherDirty)
        {
            var p = _pendingProfile;
            _watcherDirty = false;
            _pendingProfile = null;
            if (p != null)
            {
                _watcher.Refresh(p); // Refresh→Start→ValidateProfile gates invalid/half-typed paths
                OnPropertyChanged(nameof(WatchStateLabel));
                if (ReferenceEquals(p, SelectedProfile)) RefreshSnapshots();
            }
        }
    }

    // ---------- fold-state persistence ----------
    // Folds toggle on deliberate clicks (never per-keystroke), so saving immediately
    // is cheap and robust against a hard kill.

    partial void OnFoldersExpandedChanged(bool value) { _ui.FoldersExpanded = value; SaveUi(); }
    partial void OnOptionsExpandedChanged(bool value) { _ui.OptionsExpanded = value; SaveUi(); }
    partial void OnBackupsExpandedChanged(bool value) { _ui.BackupsExpanded = value; SaveUi(); }

    private void SaveUi() => _uiStore.Save(_ui);
}
