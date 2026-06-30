using System;
using Avalonia.Headless.XUnit;
using SaveGuard.Services;
using SaveGuard.ViewModels;
using SaveGuard.Views;
using Xunit;

namespace SaveGuard.Tests;

public class SteamImportWindowTests
{
    [AvaloniaFact]
    public void Dialog_loads_xaml_and_window_resources()
    {
        var vm = new SteamImportViewModel(new SteamLibraryScanner(), SaveDatabase.Load(), Array.Empty<long>());

        // Constructing the window runs AvaloniaXamlLoader.Load: it parses the whole
        // axaml and resolves the window-level StaticResource references. This throws
        // if the XAML is malformed or a referenced brush/key is missing.
        var ex = Record.Exception(() =>
        {
            var w = new SteamImportWindow { DataContext = vm };
            w.WireDialogs(vm);
        });

        Assert.Null(ex);
    }

    [AvaloniaFact]
    public void MainWindow_loads_xaml_and_resources()
    {
        // Loads the full MainWindow axaml (left-rail icon template, editor-header
        // icon picker, the PathToBitmap converter resource). Throws on malformed
        // XAML or a missing resource key.
        var ex = Record.Exception(() => _ = new MainWindow());
        Assert.Null(ex);
    }

    [AvaloniaFact]
    public void Curated_database_loads_via_assetloader()
    {
        // Exercises the real avares:// resource path used at app startup.
        var db = SaveDatabase.Load();
        Assert.NotNull(db.Lookup(1086940)); // Baldur's Gate 3
        Assert.NotNull(db.Lookup(1145360)); // Hades

        var bg3 = db.Lookup(1086940)!;
        Assert.Equal(".lsv", bg3.TriggerExtensions);
        Assert.NotEmpty(bg3.SavePaths);
    }
}
