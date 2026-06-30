using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveGuard.Services;

namespace SaveGuard.ViewModels;

/// <summary>
/// App-wide settings shown in a modal dialog: a shared backup folder applied to every game,
/// and the UI language. The parent view-model applies the changes — language live (via
/// <see cref="LanguageChanged"/>), the shared folder when the dialog closes, and "Apply to
/// all games" on demand (via <see cref="ApplyToAllRequested"/>).
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static Localizer L => Localizer.Instance;

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
}
