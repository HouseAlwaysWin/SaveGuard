using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    public bool HasProfile => SelectedProfile != null;

    public string WatchStateLabel
    {
        get
        {
            if (SelectedProfile == null) return "";
            if (!SelectedProfile.AutoWatch) return "Auto-backup off";
            return _watcher.IsWatching(SelectedProfile) ? "Watching — backs up on save" : "Auto-backup on (idle)";
        }
    }

    public MainWindowViewModel(ProfileStore store, BackupEngine engine, WatchService watcher)
    {
        _store = store;
        _engine = engine;
        _watcher = watcher;

        _watcher.BackupCompleted += OnAutoBackupCompleted;

        foreach (var p in _store.Load())
            Profiles.Add(p);

        SelectedProfile = Profiles.FirstOrDefault();

        // Begin watching any auto-watch profiles.
        foreach (var p in Profiles)
            _watcher.Start(p);
    }

    partial void OnSelectedProfileChanged(GameProfile? value) => RefreshSnapshots();

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
        if (SelectedProfile == null) return;
        if (BackupEngine.ValidateProfile(SelectedProfile) != null) return;
        foreach (var s in _engine.ListSnapshots(SelectedProfile))
            Snapshots.Add(s);
        OnPropertyChanged(nameof(WatchStateLabel));
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
        Status = "Set the watch folder, then save the profile.";
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
    private void SaveProfile()
    {
        if (SelectedProfile == null) return;
        var err = BackupEngine.ValidateProfile(SelectedProfile);
        if (err != null)
        {
            Status = err;
            return;
        }
        Persist();
        _watcher.Refresh(SelectedProfile);
        RefreshSnapshots();
        Status = $"Saved. {WatchStateLabel}.";
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

    [RelayCommand]
    private void ToggleWatch()
    {
        if (SelectedProfile == null) return;
        SelectedProfile.AutoWatch = !SelectedProfile.AutoWatch;
        _watcher.Refresh(SelectedProfile);
        Persist();
        OnPropertyChanged(nameof(WatchStateLabel));
        Status = SelectedProfile.AutoWatch ? "Auto-backup on." : "Auto-backup off.";
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
}
