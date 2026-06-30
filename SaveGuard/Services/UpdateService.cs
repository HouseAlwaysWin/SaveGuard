using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SaveGuard.Services;

/// <summary>
/// Thin wrapper over Velopack's UpdateManager pointed at this project's GitHub
/// Releases. Does nothing when the app is running uninstalled (dev / xcopy), so
/// it's safe to call from anywhere.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/HouseAlwaysWin/SaveGuard";

    private readonly UpdateManager? _mgr;

    public UpdateService()
    {
        try
        {
            // public repo → no access token needed; pass true to also offer pre-releases.
            _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
        }
        catch
        {
            // Velopack not initialized (e.g. running under a test harness that didn't
            // call VelopackApp.Run()) — updates are simply unavailable.
            _mgr = null;
        }
    }

    /// <summary>True only when launched from a Velopack install (updates are possible).</summary>
    public bool IsSupported => _mgr?.IsInstalled ?? false;

    /// <summary>The running app version (e.g. "1.0.1"), read from the assembly — the CI
    /// build stamps it from the release tag, so it tracks the installed Velopack version
    /// (after an auto-update the swapped-in assembly carries the new version).</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public Task<UpdateInfo?> CheckAsync()
        => IsSupported ? _mgr!.CheckForUpdatesAsync() : Task.FromResult<UpdateInfo?>(null);

    public Task DownloadAsync(UpdateInfo info) => _mgr!.DownloadUpdatesAsync(info);

    /// <summary>Apply the staged update and relaunch into the new version.</summary>
    public void ApplyAndRestart(UpdateInfo info) => _mgr!.ApplyUpdatesAndRestart(info.TargetFullRelease);
}
