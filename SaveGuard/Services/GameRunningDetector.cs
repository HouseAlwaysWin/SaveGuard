using System;
using Microsoft.Win32;

namespace SaveGuard.Services;

/// <summary>
/// Best-effort check for whether a Steam game is currently running, so a restore can
/// warn first: a running game keeps its saves in memory and overwrites a restore when it
/// exits (this is why a BG3 Honour restore reverts to Custom unless the game is closed).
/// Reads Steam's own running-state from the registry — no per-game config, works for any
/// Steam title via the profile's existing AppId. Returns false on any uncertainty
/// (non-Windows, no AppId, Steam not installed, registry error): it only ever adds a
/// warning, never blocks a restore.
/// </summary>
public static class GameRunningDetector
{
    public static bool IsSteamGameRunning(long steamAppId)
    {
        if (steamAppId <= 0 || !OperatingSystem.IsWindows()) return false;
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam is null) return false;

            // Steam sets RunningAppID to the launched game's id (0 when none); it also
            // sets Apps\<id>\Running to 1 while that game is running. Either confirms it.
            if (steam.GetValue("RunningAppID") is int running && running == steamAppId)
                return true;

            using var app = steam.OpenSubKey($@"Apps\{steamAppId}");
            return app?.GetValue("Running") is int r && r == 1;
        }
        catch
        {
            return false;
        }
    }
}
