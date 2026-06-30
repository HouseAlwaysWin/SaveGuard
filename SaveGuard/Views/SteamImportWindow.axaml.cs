using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SaveGuard.ViewModels;

namespace SaveGuard.Views;

public partial class SteamImportWindow : Window
{
    public SteamImportWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Give the import view-model access to the folder picker and a way to
    /// close itself — same indirection the main window uses for its dialogs.</summary>
    public void WireDialogs(SteamImportViewModel vm)
    {
        vm.PickFolder = async (title, startPath) =>
        {
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            var existing = NearestExistingDir(startPath);
            if (existing != null)
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(existing);

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };

        vm.RequestClose = Close;
    }

    // Kick off the scan as soon as the dialog appears.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is SteamImportViewModel vm && vm.ScanCommand.CanExecute(null))
            vm.ScanCommand.Execute(null);
    }

    // Cancel any in-flight background scan when the dialog closes.
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SteamImportViewModel)?.CancelScan();
        base.OnClosed(e);
    }

    /// <summary>The path itself if it exists, else its nearest existing ancestor, so a
    /// half-typed/not-yet-created path still opens the picker somewhere sensible.</summary>
    private static string? NearestExistingDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var dir = path.Trim();
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir)) return dir;
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
        }
        catch { /* malformed path → just use the default */ }
        return null;
    }
}
