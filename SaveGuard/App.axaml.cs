using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using SaveGuard.Services;
using SaveGuard.ViewModels;
using SaveGuard.Views;

namespace SaveGuard;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private bool _reallyQuitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The app keeps watching saves in the background, so hiding the (only)
            // window must NOT terminate the process — only an explicit Quit does.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var store = new ProfileStore();
            var uiStore = new UiStateStore();
            var engine = new BackupEngine();
            var watcher = new WatchService(engine);
            var vm = new MainWindowViewModel(store, engine, watcher, uiStore);

            var window = new MainWindow { DataContext = vm };
            window.WireDialogs(vm);

            // Clicking the window's X minimizes to the tray instead of quitting.
            // Flush any debounced edit first so closing never drops a recent change.
            window.Closing += (_, e) =>
            {
                vm.SaveNow();
                if (!_reallyQuitting)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };

            _trayIcon = BuildTrayIcon(window, desktop, watcher);
            // Register with the Application so the platform actually shows it.
            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.SaveNow();
                watcher.Dispose();
                DisposeTray();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon BuildTrayIcon(Window window, IClassicDesktopStyleApplicationLifetime desktop, WatchService watcher)
    {
        var menu = new NativeMenu();

        var openItem = new NativeMenuItem("Open SaveGuard");
        openItem.Click += (_, _) => ShowMainWindow(window);
        menu.Items.Add(openItem);

        var quitItem = new NativeMenuItem("Quit SaveGuard");
        quitItem.Click += (_, _) => Quit(desktop, watcher);
        menu.Items.Add(quitItem);

        var tray = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "SaveGuard",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) => ShowMainWindow(window); // left-click restores
        return tray;
    }

    private static void ShowMainWindow(Window w)
    {
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop, WatchService watcher)
    {
        _reallyQuitting = true; // let the next Closing proceed
        watcher.Dispose();
        DisposeTray();
        desktop.TryShutdown(0);
    }

    private void DisposeTray()
    {
        if (_trayIcon == null) return;
        _trayIcon.IsVisible = false;         // remove the glyph immediately
        TrayIcon.SetIcons(this, new TrayIcons()); // unregister → Avalonia disposes it
        _trayIcon = null;
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://SaveGuard/Assets/saveguard.ico"));
        return new WindowIcon(stream);
    }
}
