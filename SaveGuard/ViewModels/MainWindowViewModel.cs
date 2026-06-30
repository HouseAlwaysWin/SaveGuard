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
    private readonly UpdateService _updates;
    private readonly SteamLibraryScanner _scanner;
    private readonly SaveDatabase _saveDb;

    // ---- auto-save (debounced) ----
    private readonly DispatcherTimer _saveTimer;
    private bool _watcherDirty;    // a watcher-relevant field changed since last flush
    private bool _snapshotsDirty;  // a field that changes which backups are listed/previewed

    /// <summary>Fields that, when changed, require rebuilding the file watcher.</summary>
    private static readonly HashSet<string> WatcherProps = new()
    {
        nameof(GameProfile.WatchPath), nameof(GameProfile.Recursive),
        nameof(GameProfile.TriggerExtensions), nameof(GameProfile.DebounceMs),
        nameof(GameProfile.AutoWatch), nameof(GameProfile.ExcludePatterns),
    };

    /// <summary>Fields that change which snapshots are listed or which image previews —
    /// editing any of these must re-read the backups list (e.g. fixing BackupRoot).</summary>
    private static readonly HashSet<string> SnapshotProps = new()
    {
        nameof(GameProfile.WatchPath), nameof(GameProfile.BackupRoot),
        nameof(GameProfile.Name), nameof(GameProfile.ImageExtensions),
    };

    /// <summary>Set by the View. Opens a folder picker starting at the given path
    /// (may be null/empty), and returns the chosen path or null.</summary>
    public Func<string, string?, Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the View. Shows a yes/no confirm dialog.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by the View. Shows the "Import from Steam" dialog for the given VM.</summary>
    public Func<SteamImportViewModel, Task>? ShowSteamImport { get; set; }

    /// <summary>Set by the View. Shows the Settings dialog for the given VM.</summary>
    public Func<SettingsViewModel, Task>? ShowSettings { get; set; }

    /// <summary>Set by the View. Opens an image file picker, returns the chosen path or null.</summary>
    public Func<Task<string?>>? PickImage { get; set; }

    /// <summary>Set by the View. Opens a file picker (any type) starting at the given
    /// directory, and returns the chosen path or null.</summary>
    public Func<string, string?, Task<string?>>? PickFile { get; set; }

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

    // Auto-update: shown in the status bar once a new version is downloaded. The actual
    // state lives in UpdateService (shared with the Settings page); this just mirrors it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
    private bool _updateReady;

    public string UpdateBannerText => L.Format("Update.Ready", _updates.PendingVersion);

    public bool HasProfile => SelectedProfile != null;

    public bool HasPreviewImage => PreviewImage != null;

    /// <summary>Where the selected profile's snapshots actually land, after resolving any
    /// shared backup folder — shown as a hint under the per-game backup field.</summary>
    public string EffectiveBackupDir => SelectedProfile == null ? "" : _engine.ProfileBackupDir(SelectedProfile);

    public string WatchStateLabel
    {
        get
        {
            if (SelectedProfile == null) return "";
            if (!SelectedProfile.AutoWatch) return L["Watch.Off"];
            return _watcher.IsWatching(SelectedProfile) ? L["Watch.Watching"] : L["Watch.Idle"];
        }
    }

    // ---- language ----
    // (The picker itself lives in the Settings dialog now; this VM keeps the live
    // localizer reference and applies the startup language in the constructor.)
    private static Localizer L => Localizer.Instance;

    // Remember the current status as a key + args so it can be re-translated live
    // when the language changes (rather than freezing in the old language).
    private string _statusKey = "Status.Ready";
    private object[] _statusArgs = Array.Empty<object>();

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        Status = L.Format(key, args);
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(WatchStateLabel));
        OnPropertyChanged(nameof(UpdateBannerText));
        Status = L.Format(_statusKey, _statusArgs);
    }

    private static string DetectSystemLanguage()
    {
        var c = System.Globalization.CultureInfo.CurrentUICulture;
        var name = c.Name; // e.g. zh-TW, zh-CN, ja-JP
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko";
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // Traditional for TW/HK/MO/Hant, Simplified otherwise.
            if (name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("HK", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("MO", StringComparison.OrdinalIgnoreCase))
                return "zh-Hant";
            return "zh-Hans";
        }
        return "en";
    }

    public MainWindowViewModel(ProfileStore store, BackupEngine engine, WatchService watcher,
                               UiStateStore uiStore, UpdateService updates,
                               SteamLibraryScanner scanner, SaveDatabase saveDb)
    {
        _store = store;
        _engine = engine;
        _watcher = watcher;
        _uiStore = uiStore;
        _updates = updates;
        _updates.Changed += OnUpdatesChanged;
        _scanner = scanner;
        _saveDb = saveDb;

        _watcher.BackupCompleted += OnAutoBackupCompleted;

        // Restore persisted accordion fold state (set backing fields directly so the
        // generated change handlers don't re-save during construction).
        _ui = _uiStore.Load();
        _foldersExpanded = _ui.FoldersExpanded;
        _optionsExpanded = _ui.OptionsExpanded;
        _backupsExpanded = _ui.BackupsExpanded;
        _engine.GlobalBackupRoot = _ui.GlobalBackupRoot; // shared backup folder, if any

        // Apply the saved (or OS-default) language before the window binds its text.
        L.SetLanguage(string.IsNullOrEmpty(_ui.Language) ? DetectSystemLanguage() : _ui.Language);
        L.CultureChanged += OnCultureChanged;
        _status = L["Status.Ready"];

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

        // Profiles imported before icons existed have no IconPath — fill it in from
        // Steam now so they show their icon instead of a letter placeholder.
        BackfillSteamIcons();

        // Check GitHub Releases for a newer version in the background (no-op in dev).
        _ = CheckForUpdatesAsync();
    }

    private void BackfillSteamIcons()
    {
        var needIcon = Profiles
            .Where(p => p.SteamAppId != 0 && string.IsNullOrEmpty(p.IconPath))
            .ToList();
        if (needIcon.Count == 0) return;

        var root = _scanner.FindSteamRoot();
        if (root == null) return;

        foreach (var p in needIcon)
        {
            var icon = _scanner.FindGameIcon(root, p.SteamAppId);
            if (icon != null) p.IconPath = icon; // change persists via the debounced auto-save
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        // UpdateService owns the state; the banner refreshes via the Changed event.
        try { await _updates.CheckAndDownloadAsync(); }
        catch { /* offline / not installed — ignore */ }
    }

    private void OnUpdatesChanged() => Dispatcher.UIThread.Post(() =>
    {
        UpdateReady = _updates.State == UpdateService.UpdateState.Ready;
        OnPropertyChanged(nameof(UpdateBannerText));
    });

    [RelayCommand]
    private void RestartToUpdate() => _updates.ApplyPending();

    partial void OnSelectedProfileChanged(GameProfile? value)
    {
        OnPropertyChanged(nameof(EffectiveBackupDir));
        RefreshSnapshots();
    }

    partial void OnSelectedSnapshotChanged(Snapshot? value) => RefreshPreview();

    private void OnAutoBackupCompleted(GameProfile p, Snapshot? snap, Exception? err)
    {
        // Marshal to the UI thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (err != null)
            {
                SetStatus("Status.AutoBackupFailed", p.Name, err.Message);
                return;
            }
            SetStatus("Status.AutoBackedUp", p.Name, snap!.TakenAt.ToString("HH:mm:ss"), snap.FileCount);
            if (ReferenceEquals(p, SelectedProfile)) RefreshSnapshots();
        });
    }

    private void RefreshSnapshots()
    {
        Snapshots.Clear();
        if (SelectedProfile == null) { RefreshPreview(); return; }
        if (_engine.ValidateProfile(SelectedProfile) != null) { RefreshPreview(); return; }
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
            Name = L["Profile.New"],
            BackupRoot = NewProfileBackupRoot(),
            AutoWatch = false, // off until the user points it somewhere valid
        };
        Profiles.Add(p);
        SelectedProfile = p;
        SetStatus("Status.SetWatchFolder");
    }

    [RelayCommand]
    private async Task ImportFromSteam()
    {
        if (ShowSteamImport == null) return;

        var ivm = new SteamImportViewModel(_scanner, _saveDb, Profiles.Select(p => p.SteamAppId));
        ivm.GamesConfirmed += OnSteamGamesConfirmed;
        try { await ShowSteamImport(ivm); }
        finally { ivm.GamesConfirmed -= OnSteamGamesConfirmed; }
    }

    private void OnSteamGamesConfirmed(IReadOnlyList<SteamGameRow> rows)
    {
        GameProfile? last = null;
        foreach (var row in rows)
        {
            var p = new GameProfile
            {
                Name = row.Name,
                WatchPath = row.SavePath,
                BackupRoot = NewProfileBackupRoot(),
                AutoWatch = false, // off until the user confirms the path is right
                SteamAppId = row.Game.AppId,
                IconPath = row.IconPath ?? "",
                TriggerExtensions = row.PresetTriggerExtensions ?? "",
                CompanionFiles = row.PresetCompanionFiles ?? "",
            };
            Profiles.Add(p);
            last = p;
        }
        if (last != null) SelectedProfile = last;
        Persist();
        SetStatus("Status.SteamImported", rows.Count);
    }

    // New profiles use the shared backup folder (blank → resolves under it) when one is
    // set; otherwise they fall back to the app-data default absolute path.
    private string NewProfileBackupRoot()
        => string.IsNullOrEmpty(_engine.GlobalBackupRoot) ? _store.DefaultBackupRoot : "";

    // ---------- settings (shared backup folder + language) ----------

    [RelayCommand]
    private async Task Settings()
    {
        if (ShowSettings == null) return;

        var svm = new SettingsViewModel(_updates)
        {
            GlobalBackupRoot = _ui.GlobalBackupRoot,
            SelectedLanguage = L.AvailableLanguages.FirstOrDefault(l => l.Code == L.CurrentLanguage),
        };
        svm.LanguageChanged += OnSettingsLanguageChanged;
        svm.ApplyToAllRequested += OnSettingsApplyToAll;
        try { await ShowSettings(svm); }
        finally
        {
            svm.LanguageChanged -= OnSettingsLanguageChanged;
            svm.ApplyToAllRequested -= OnSettingsApplyToAll;
            svm.Detach(); // unsubscribe from the app-lifetime UpdateService / localizer
            ApplyGlobalBackupRoot(svm.GlobalBackupRoot); // commit the (possibly edited) shared folder
        }
    }

    private void OnSettingsLanguageChanged(string code)
    {
        L.SetLanguage(code);
        _ui.Language = code;
        SaveUi();
    }

    private void OnSettingsApplyToAll(string globalRoot)
    {
        ApplyGlobalBackupRoot(globalRoot);
        // Switch every game to the shared folder by clearing its per-game path.
        foreach (var p in Profiles) p.BackupRoot = "";
        Persist();
        RefreshAfterBackupRootChange();
        SetStatus("Status.BackupAppliedToAll", Profiles.Count);
    }

    private void ApplyGlobalBackupRoot(string value)
    {
        value = (value ?? "").Trim();
        if (value == _ui.GlobalBackupRoot) return;
        _ui.GlobalBackupRoot = value;
        SaveUi();
        _engine.GlobalBackupRoot = value;
        RefreshAfterBackupRootChange();
    }

    // The effective backup path changed for every profile — rebuild watchers (validation
    // may now pass/fail) and refresh the visible backups list + effective-path hint.
    private void RefreshAfterBackupRootChange()
    {
        foreach (var p in Profiles) _watcher.Refresh(p);
        RefreshSnapshots();
        OnPropertyChanged(nameof(WatchStateLabel));
        OnPropertyChanged(nameof(EffectiveBackupDir));
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (SelectedProfile == null) return;
        var ok = Confirm == null || await Confirm(L["Dialog.DeleteProfile.Title"],
            L.Format("Dialog.DeleteProfile.Body", SelectedProfile.Name));
        if (!ok) return;

        _watcher.Stop(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        Persist();
        SetStatus("Status.ProfileRemoved");
    }

    [RelayCommand]
    private async Task BrowseWatch()
    {
        if (SelectedProfile == null || PickFolder == null) return;
        var path = await PickFolder(L["Picker.WatchTitle"], SelectedProfile.WatchPath);
        if (path != null) SelectedProfile.WatchPath = path;
    }

    [RelayCommand]
    private async Task BrowseBackup()
    {
        if (SelectedProfile == null || PickFolder == null) return;
        var path = await PickFolder(L["Picker.BackupTitle"], SelectedProfile.BackupRoot);
        if (path != null) SelectedProfile.BackupRoot = path;
    }

    [RelayCommand]
    private async Task BrowseIcon()
    {
        if (SelectedProfile == null || PickImage == null) return;
        var path = await PickImage();
        if (path != null) SelectedProfile.IconPath = path;
    }

    [RelayCommand]
    private void ClearIcon()
    {
        if (SelectedProfile != null) SelectedProfile.IconPath = "";
    }

    [RelayCommand]
    private async Task AddCompanionFile()
    {
        if (SelectedProfile == null || PickFile == null) return;
        var path = await PickFile(L["Picker.AddFileTitle"], SelectedProfile.WatchPath);
        if (path == null) return;
        // Companion files can live anywhere — store the absolute path.
        SelectedProfile.CompanionFiles = AppendLine(SelectedProfile.CompanionFiles, path);
    }

    [RelayCommand]
    private async Task AddExcludeFile()
    {
        if (SelectedProfile == null || PickFile == null) return;
        var path = await PickFile(L["Picker.AddFileTitle"], SelectedProfile.WatchPath);
        if (path == null) return;
        // Excludes are relative to the watch folder — store a portable pattern.
        SelectedProfile.ExcludePatterns =
            AppendLine(SelectedProfile.ExcludePatterns, ToExcludePattern(SelectedProfile.WatchPath, path));
    }

    /// <summary>A picked file becomes a path relative to the watch folder when it sits
    /// inside it, otherwise just its file name.</summary>
    private static string ToExcludePattern(string watchPath, string picked)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(watchPath))
            {
                var root = Path.GetFullPath(watchPath);
                var full = Path.GetFullPath(picked);
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (full.StartsWith(prefix, cmp))
                    return Path.GetRelativePath(root, full).Replace('\\', '/');
            }
        }
        catch { /* fall back to the file name */ }
        return Path.GetFileName(picked);
    }

    /// <summary>Appends a trimmed line to a multi-line field, skipping blanks and dupes.</summary>
    private static string AppendLine(string existing, string line)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var lines = new List<string>();
        foreach (var l in existing.Replace("\r\n", "\n").Split('\n'))
        {
            var t = l.Trim();
            if (t.Length > 0) lines.Add(t);
        }
        if (!lines.Contains(line, cmp)) lines.Add(line);
        return string.Join("\n", lines);
    }

    // ---------- backup / restore commands ----------

    [RelayCommand]
    private async Task BackupNow()
    {
        if (SelectedProfile == null) return;
        var err = _engine.ValidateProfile(SelectedProfile);
        if (err != null) { Status = err; return; }

        Busy = true;
        SetStatus("Status.BackingUp");
        try
        {
            var snap = await _engine.CreateSnapshotAsync(SelectedProfile, "manual");
            RefreshSnapshots();
            SetStatus("Status.BackedUp", snap.FileCount, snap.TakenAt.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            SetStatus("Status.BackupFailed", ex.Message);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (SelectedProfile == null || SelectedSnapshot == null) return;

        var ok = Confirm == null || await Confirm(L["Dialog.Restore.Title"],
            L.Format("Dialog.Restore.Body", SelectedSnapshot.TakenAtDisplay));
        if (!ok) return;

        Busy = true;
        SetStatus("Status.Restoring");
        try
        {
            // Pause auto-watch so the restore's writes don't fire a spurious backup.
            var wasWatching = _watcher.IsWatching(SelectedProfile);
            if (wasWatching) _watcher.Stop(SelectedProfile);

            await _engine.RestoreAsync(SelectedProfile, SelectedSnapshot);

            if (wasWatching) _watcher.Start(SelectedProfile);
            RefreshSnapshots();
            SetStatus("Status.Restored", SelectedSnapshot.TakenAtDisplay);
        }
        catch (Exception ex)
        {
            SetStatus("Status.RestoreFailed", ex.Message);
        }
        finally { Busy = false; }
    }

    /// <summary>Deletes every selected snapshot (the list comes from the multi-select
    /// ListBox); falls back to the single selected one if nothing was passed.</summary>
    [RelayCommand]
    private async Task DeleteSnapshots(System.Collections.IList? selected)
    {
        var snaps = selected?.Cast<Snapshot>().ToList() ?? new List<Snapshot>();
        if (snaps.Count == 0 && SelectedSnapshot != null) snaps.Add(SelectedSnapshot);
        if (snaps.Count == 0) return;

        var ok = Confirm == null || await Confirm(L["Dialog.DeleteBackup.Title"],
            snaps.Count == 1
                ? L.Format("Dialog.DeleteBackup.Body", snaps[0].TakenAtDisplay)
                : L.Format("Dialog.DeleteBackups.Body", snaps.Count));
        if (!ok) return;

        int failed = 0;
        foreach (var s in snaps)
        {
            try { _engine.DeleteSnapshot(s); }
            catch { failed++; }
        }

        RefreshSnapshots();
        if (failed > 0) SetStatus("Status.DeleteFailed", failed.ToString());
        else SetStatus("Status.BackupDeleted");
    }

    /// <summary>Re-scan the backup folder and reload the snapshots list.</summary>
    [RelayCommand]
    private void Refresh() => RefreshSnapshots();

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (SelectedProfile == null) return;
        if (_engine.ValidateProfile(SelectedProfile) != null) return; // no valid backup path yet
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
        ScheduleSave(); // the list shape changed → persist
    }

    private void Hook(GameProfile p) => p.PropertyChanged += OnProfilePropertyChanged;
    private void Unhook(GameProfile p) => p.PropertyChanged -= OnProfilePropertyChanged;

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        if (name != null && WatcherProps.Contains(name)) _watcherDirty = true;
        if (name != null && SnapshotProps.Contains(name)) _snapshotsDirty = true;
        // Reflect Name/AutoWatch edits in the header label immediately (the watcher
        // itself only rebuilds on the debounced flush).
        if (ReferenceEquals(sender, SelectedProfile) &&
            (name == nameof(GameProfile.Name) || name == nameof(GameProfile.AutoWatch)))
            OnPropertyChanged(nameof(WatchStateLabel));
        // Keep the effective-backup-path hint in sync as the path/name is edited.
        if (ReferenceEquals(sender, SelectedProfile) &&
            (name == nameof(GameProfile.BackupRoot) || name == nameof(GameProfile.Name)))
            OnPropertyChanged(nameof(EffectiveBackupDir));
        ScheduleSave();
    }

    /// <summary>Flush any debounced edit immediately — call before the window hides
    /// or the app exits so a quick edit-then-quit never loses changes.</summary>
    public void SaveNow()
    {
        if (_saveTimer.IsEnabled || _watcherDirty || _snapshotsDirty) FlushSave();
    }

    /// <summary>Restart the quiet-window (debounce) timer.</summary>
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        Persist();

        bool refreshWatcher = _watcherDirty;
        bool refreshSnapshots = _snapshotsDirty;
        _watcherDirty = false;
        _snapshotsDirty = false;
        if (SelectedProfile == null) return;

        if (refreshWatcher)
        {
            _watcher.Refresh(SelectedProfile); // Start→ValidateProfile gates invalid/half-typed paths
            OnPropertyChanged(nameof(WatchStateLabel));
        }
        // Re-read the backups list so edits to BackupRoot/Name/WatchPath/ImageExtensions
        // are reflected live (this is what makes "fix the path → backups reappear" work).
        if (refreshSnapshots) RefreshSnapshots();
    }

    // ---------- fold-state persistence ----------
    // Folds toggle on deliberate clicks (never per-keystroke), so saving immediately
    // is cheap and robust against a hard kill.

    partial void OnFoldersExpandedChanged(bool value) { _ui.FoldersExpanded = value; SaveUi(); }
    partial void OnOptionsExpandedChanged(bool value) { _ui.OptionsExpanded = value; SaveUi(); }
    partial void OnBackupsExpandedChanged(bool value) { _ui.BackupsExpanded = value; SaveUi(); }

    private void SaveUi() => _uiStore.Save(_ui);
}
