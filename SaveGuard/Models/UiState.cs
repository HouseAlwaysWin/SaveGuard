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
}
