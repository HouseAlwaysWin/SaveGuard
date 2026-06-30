using CommunityToolkit.Mvvm.ComponentModel;

namespace SaveGuard.Models;

/// <summary>
/// App-level UI preferences that aren't tied to a specific profile (e.g. which
/// accordion sections are expanded). Persisted separately from profiles so the
/// layout survives restarts and tray hide/restore.
/// </summary>
public sealed partial class UiState : ObservableObject
{
    [ObservableProperty] private bool _foldersExpanded = true;
    [ObservableProperty] private bool _optionsExpanded = true;
    [ObservableProperty] private bool _backupsExpanded = true;

    /// <summary>UI language code (e.g. "en", "zh-Hant"). Empty = follow the OS on first run.</summary>
    [ObservableProperty] private string _language = "";

    /// <summary>Optional shared backup root applied to every game. When set, a profile's
    /// BackupRoot may be a relative subfolder (or blank) and resolves under this; when
    /// blank, each profile needs its own absolute BackupRoot. Empty = not used.</summary>
    [ObservableProperty] private string _globalBackupRoot = "";
}

