using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SaveGuard.Services;
using SaveGuard.ViewModels;
using SaveGuard.Views;

namespace SaveGuard;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new ProfileStore();
            var engine = new BackupEngine();
            var watcher = new WatchService(engine);
            var vm = new MainWindowViewModel(store, engine, watcher);

            var window = new MainWindow { DataContext = vm };
            window.WireDialogs(vm);

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => watcher.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
