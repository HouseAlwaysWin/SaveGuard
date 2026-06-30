using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SaveGuard.Services;
using SaveGuard.ViewModels;

namespace SaveGuard.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Give the ViewModel access to platform dialogs without coupling it to the View.</summary>
    public void WireDialogs(MainWindowViewModel vm)
    {
        vm.PickFolder = async (title, startPath) =>
        {
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };

            // Open the picker at whatever is already typed in the field (or the nearest
            // existing parent), so it's not the OS default each time.
            var existing = NearestExistingDir(startPath);
            if (existing != null)
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(existing);

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            var first = folders.FirstOrDefault();
            return first?.TryGetLocalPath();
        };

        vm.Confirm = (title, message) => ConfirmDialog.Show(this, title, message);

        vm.ShowSteamImport = async ivm =>
        {
            var dialog = new SteamImportWindow { DataContext = ivm };
            dialog.WireDialogs(ivm);
            await dialog.ShowDialog(this);
        };

        vm.PickImage = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localizer.Instance["Picker.IconTitle"],
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.ico" },
                    },
                },
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        };
    }

    /// <summary>The path itself if it's an existing directory, else its nearest existing
    /// ancestor — so a half-typed or not-yet-created path still opens somewhere sensible.</summary>
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
