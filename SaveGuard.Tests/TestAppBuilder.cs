using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using SaveGuard;
using SaveGuard.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SaveGuard.Tests;

/// <summary>Builds the real <see cref="App"/> on Avalonia's headless backend so
/// [AvaloniaFact] tests can construct windows and use avares:// resources without a
/// display. App.Initialize() runs (loading App.axaml's brushes/styles), but the
/// desktop lifetime — tray, profiles, watcher — does not.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
