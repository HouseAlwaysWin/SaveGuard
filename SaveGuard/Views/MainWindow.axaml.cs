using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SaveGuard.ViewModels;

namespace SaveGuard.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Give the ViewModel access to platform dialogs without coupling it to the View.</summary>
    public void WireDialogs(MainWindowViewModel vm)
    {
        vm.PickFolder = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            var first = folders.FirstOrDefault();
            return first?.TryGetLocalPath();
        };

        vm.Confirm = (title, message) => ConfirmDialog.Show(this, title, message);
    }
}
