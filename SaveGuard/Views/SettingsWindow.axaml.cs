using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SaveGuard.ViewModels;

namespace SaveGuard.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Give the settings view-model the folder picker and a way to close itself.</summary>
    public void WireDialogs(SettingsViewModel vm)
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

    /// <summary>The path itself if it exists, else its nearest existing ancestor.</summary>
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
        catch { /* malformed path → use the default */ }
        return null;
    }
}
