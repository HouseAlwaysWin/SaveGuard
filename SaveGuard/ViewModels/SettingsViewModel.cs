using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveGuard.Services;

namespace SaveGuard.ViewModels;

/// <summary>
/// App-wide settings shown in a modal dialog: a shared backup folder applied to every game,
/// the UI language, and an Updates section (current version + manual check). The parent
/// view-model applies the changes — language live (via <see cref="LanguageChanged"/>), the
/// shared folder when the dialog closes, and "Apply to all games" on demand (via
/// <see cref="ApplyToAllRequested"/>). Updates run through the shared <see cref="UpdateService"/>.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static Localizer L => Localizer.Instance;

    private readonly UpdateService? _updates;

    public SettingsViewModel(UpdateService? updates = null)
    {
        _updates = updates;
        if (_updates != null)
        {
            _updates.Changed += OnUpdatesChanged;
            L.CultureChanged += OnCultureChanged;
            SyncFromService();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGlobalRoot))]
    private string _globalBackupRoot = "";

    public bool HasGlobalRoot => !string.IsNullOrWhiteSpace(GlobalBackupRoot);

    public IReadOnlyList<Localizer.LanguageOption> AvailableLanguages => L.AvailableLanguages;

    [ObservableProperty] private Localizer.LanguageOption? _selectedLanguage;

    /// <summary>Set by the View — opens a folder picker starting at the given path.</summary>
    public Func<string, string?, Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the View — closes the dialog.</summary>
    public Action? RequestClose { get; set; }

    /// <summary>Raised when the language changes; the parent applies it live and persists it.</summary>
    public event Action<string>? LanguageChanged;

    /// <summary>Raised by "Apply to all games" with the current shared folder.</summary>
    public event Action<string>? ApplyToAllRequested;

    partial void OnSelectedLanguageChanged(Localizer.LanguageOption? value)
    {
        if (value != null) LanguageChanged?.Invoke(value.Code);
    }

    [RelayCommand]
    private async Task BrowseGlobalBackup()
    {
        if (PickFolder == null) return;
        var path = await PickFolder(L["Picker.GlobalBackupTitle"], GlobalBackupRoot);
        if (path != null) GlobalBackupRoot = path;
    }

    [RelayCommand]
    private void ClearGlobalBackup() => GlobalBackupRoot = "";

    [RelayCommand]
    private void ApplyToAll()
    {
        if (HasGlobalRoot) ApplyToAllRequested?.Invoke(GlobalBackupRoot.Trim());
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    // ---------- updates ----------

    public string CurrentVersionText =>
        _updates == null ? "" : L.Format("Settings.VersionValue", _updates.CurrentVersion);

    public bool UpdatesSupported => _updates?.IsSupported ?? false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecking))]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(UpdateReady))]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    private UpdateService.UpdateState _updateState = UpdateService.UpdateState.Idle;

    public bool IsChecking =>
        UpdateState is UpdateService.UpdateState.Checking or UpdateService.UpdateState.Downloading;

    public bool CanCheck => UpdatesSupported && !IsChecking;

    public bool UpdateReady => UpdateState == UpdateService.UpdateState.Ready;

    public string UpdateStatusText => UpdateState switch
    {
        UpdateService.UpdateState.Unsupported => L["Settings.Update.Unsupported"],
        UpdateService.UpdateState.Idle        => L["Settings.Update.Idle"],
        UpdateService.UpdateState.Checking    => L["Settings.Update.Checking"],
        UpdateService.UpdateState.UpToDate    => L["Settings.Update.UpToDate"],
        UpdateService.UpdateState.Downloading => L["Settings.Update.Downloading"],
        UpdateService.UpdateState.Ready       => L.Format("Settings.Update.Ready", _updates?.PendingVersion ?? ""),
        UpdateService.UpdateState.Failed      => L["Settings.Update.Failed"],
        _ => "",
    };

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (_updates == null) return;
        await _updates.CheckAndDownloadAsync(); // state flows back via Changed → SyncFromService
    }

    [RelayCommand]
    private void ApplyUpdate() => _updates?.ApplyPending();

    private void OnUpdatesChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(SyncFromService);

    private void SyncFromService()
    {
        UpdateState = _updates?.State ?? UpdateService.UpdateState.Unsupported;
        OnPropertyChanged(nameof(UpdateStatusText)); // re-read PendingVersion for the Ready text
    }

    private void OnCultureChanged()
    {
        // Computed strings aren't bound via {loc:Loc}; refresh them when the user switches
        // language inside this dialog.
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(UpdateStatusText));
    }

    /// <summary>Detach from app-lifetime services so this short-lived dialog VM can be collected.</summary>
    internal void Detach()
    {
        if (_updates != null)
        {
            _updates.Changed -= OnUpdatesChanged;
            L.CultureChanged -= OnCultureChanged;
        }
    }
}
