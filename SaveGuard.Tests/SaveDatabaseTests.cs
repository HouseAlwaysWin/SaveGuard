using System;
using System.IO;
using SaveGuard.Services;
using Xunit;

namespace SaveGuard.Tests;

// Placeholder expansion is Windows-specific; the build/test machine is Windows.
public class SaveDatabaseTests
{
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Expands_known_windows_placeholders()
    {
        Assert.Equal(Path.Combine(AppData, "StardewValley", "Saves"),
            SaveDatabase.Expand("<winAppData>/StardewValley/Saves"));

        Assert.Equal(Path.Combine(Home, "Documents", "x"),
            SaveDatabase.Expand("<home>/Documents/x"));
    }

    [Fact]
    public void Expands_steam_install_and_userdata_when_supplied()
    {
        Assert.Equal(Path.Combine(@"C:\Games\X", "saves"),
            SaveDatabase.Expand("<steamInstall>/saves", steamInstallDir: @"C:\Games\X"));

        Assert.Equal(Path.Combine(@"C:\ud", "remote"),
            SaveDatabase.Expand("<steamUserData>/remote", steamUserDataDir: @"C:\ud"));
    }

    [Fact]
    public void Returns_null_when_a_required_placeholder_is_unavailable()
    {
        // <steamUserData> used but no userdata dir supplied.
        Assert.Null(SaveDatabase.Expand("<steamUserData>/remote"));
        // <steamInstall> used but no install dir supplied.
        Assert.Null(SaveDatabase.Expand("<steamInstall>/saves"));
    }

    [Fact]
    public void Returns_null_for_unknown_placeholder()
    {
        Assert.Null(SaveDatabase.Expand("<totallyBogus>/x"));
    }

    [Fact]
    public void Normalizes_forward_slashes_to_backslashes_on_windows()
    {
        var result = SaveDatabase.Expand("<winAppData>/a/b/c");
        Assert.NotNull(result);
        Assert.DoesNotContain('/', result!);
    }

    [Fact]
    public void Empty_template_returns_null()
    {
        Assert.Null(SaveDatabase.Expand(""));
        Assert.Null(SaveDatabase.Expand("   "));
    }
}
