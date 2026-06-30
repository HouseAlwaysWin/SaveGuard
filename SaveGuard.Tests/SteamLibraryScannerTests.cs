using System;
using System.IO;
using System.Linq;
using SaveGuard.Services;
using Xunit;

namespace SaveGuard.Tests;

/// <summary>Exercises the scanner against a synthetic Steam tree in a temp folder,
/// so it's deterministic and never touches the real Steam install.</summary>
public sealed class SteamLibraryScannerTests : IDisposable
{
    private readonly string _root;
    private readonly SteamLibraryScanner _scanner = new();

    public SteamLibraryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sgtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Builds: <root>/steam (library 0) + <root>/lib2 (library 1), wires libraryfolders.vdf,
    // and returns the steam root path.
    private string BuildTree()
    {
        var steam = Path.Combine(_root, "steam");
        var lib2 = Path.Combine(_root, "lib2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));

        // Library 0 (steam itself)
        WriteManifest(steam, 100, "GameA", 4, "GameA", installDirExists: true);   // included
        WriteManifest(steam, 300, "GameC", 2, "GameC", installDirExists: true);   // not fully installed
        WriteManifest(steam, 500, "GameE", 4, "GhostGame", installDirExists: false); // install dir missing
        WriteManifest(steam, 228980, "Steamworks Common Redistributables", 4, "Steamworks Shared", installDirExists: true); // non-game

        // Library 1
        WriteManifest(lib2, 400, "GameD", 4, "GameD", installDirExists: true);     // included

        var vdf = $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"{{Escape(steam)}}"
                    "apps"
                    {
                        "100"		"1"
                    }
                }
                "1"
                {
                    "path"		"{{Escape(lib2)}}"
                    "apps"
                    {
                        "400"		"1"
                    }
                }
            }
            """;
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), vdf);
        return steam;
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    private static void WriteManifest(string library, long appId, string name, int stateFlags,
                                      string installDir, bool installDirExists)
    {
        var steamapps = Path.Combine(library, "steamapps");
        if (installDirExists)
            Directory.CreateDirectory(Path.Combine(steamapps, "common", installDir));

        var acf = $$"""
            "AppState"
            {
                "appid"		"{{appId}}"
                "name"		"{{name}}"
                "StateFlags"		"{{stateFlags}}"
                "installdir"		"{{installDir}}"
            }
            """;
        File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{appId}.acf"), acf);
    }

    [Fact]
    public void EnumerateLibraries_includes_steam_root_and_extra_libraries()
    {
        var steam = BuildTree();
        var libs = _scanner.EnumerateLibraries(steam);

        Assert.Equal(2, libs.Count);
        Assert.Contains(libs, l => string.Equals(Path.GetFileName(l), "steam", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(libs, l => string.Equals(Path.GetFileName(l), "lib2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnumerateInstalledGames_returns_only_fully_installed_real_games()
    {
        var steam = BuildTree();
        var games = _scanner.EnumerateInstalledGames(steam);

        var ids = games.Select(g => g.AppId).ToHashSet();
        Assert.Contains(100L, ids);   // GameA — fully installed
        Assert.Contains(400L, ids);   // GameD — in second library
        Assert.DoesNotContain(300L, ids);     // not fully installed (StateFlags 2)
        Assert.DoesNotContain(500L, ids);     // install dir missing
        Assert.DoesNotContain(228980L, ids);  // non-game runtime, filtered

        Assert.Equal(2, games.Count);
        // Sorted by name: GameA before GameD.
        Assert.Equal(new[] { "GameA", "GameD" }, games.Select(g => g.Name).ToArray());
    }

    [Fact]
    public void Game_install_dir_resolves_under_common()
    {
        var steam = BuildTree();
        var gameA = _scanner.EnumerateInstalledGames(steam).Single(g => g.AppId == 100);
        Assert.Equal(Path.Combine(steam, "steamapps", "common", "GameA"), gameA.InstallDir);
    }

    [Fact]
    public void EnumerateSteamUserIds_returns_numeric_account_folders_only()
    {
        var steam = BuildTree();
        var userdata = Path.Combine(steam, "userdata");
        Directory.CreateDirectory(Path.Combine(userdata, "127409516"));
        Directory.CreateDirectory(Path.Combine(userdata, "0"));          // skipped
        Directory.CreateDirectory(Path.Combine(userdata, "anonymous"));  // skipped

        var ids = _scanner.EnumerateSteamUserIds(steam);
        Assert.Equal(new[] { "127409516" }, ids.ToArray());
    }

    [Fact]
    public void EnumerateInstalledGames_on_empty_root_is_empty_not_throwing()
    {
        var empty = Path.Combine(_root, "nonexistent_steam");
        Assert.Empty(_scanner.EnumerateInstalledGames(empty));
        Assert.Empty(_scanner.EnumerateSteamUserIds(empty));
    }
}
