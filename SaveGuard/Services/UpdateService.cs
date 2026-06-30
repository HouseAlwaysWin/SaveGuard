using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace SaveGuard.Services;

/// <summary>
/// Wrapper over Velopack's UpdateManager pointed at this project's GitHub Releases,
/// and the single source of truth for update state. The startup auto-check (status-bar
/// banner) and the Settings "Check for updates" section both drive this one instance,
/// so they never disagree and never download the same release twice.
/// Does nothing when the app runs uninstalled (dev / xcopy), so it's safe to call anywhere.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/HouseAlwaysWin/SaveGuard";

    private readonly UpdateManager? _mgr;

    public enum UpdateState { Idle, Checking, UpToDate, Downloading, Ready, Failed, Unsupported }

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

        State = IsSupported ? UpdateState.Idle : UpdateState.Unsupported;
    }

    /// <summary>True only when launched from a Velopack install (updates are possible).</summary>
    public bool IsSupported => _mgr?.IsInstalled ?? false;

    /// <summary>Current state of the check/download flow.</summary>
    public UpdateState State { get; private set; }

    /// <summary>The downloaded-and-staged update, if any.</summary>
    public UpdateInfo? Pending { get; private set; }

    public string PendingVersion => Pending?.TargetFullRelease.Version.ToString() ?? "";

    /// <summary>
    /// Raised whenever <see cref="State"/>/<see cref="Pending"/> change. May fire on a
    /// background thread (a Velopack continuation) — consumers must marshal to the UI thread.
    /// </summary>
    public event Action? Changed;

    private void Set(UpdateState s)
    {
        State = s;
        Changed?.Invoke();
    }

    /// <summary>
    /// Version string for display. Prefers Velopack's installed version; in dev/xcopy
    /// (uninstalled) falls back to the entry assembly version, which equals csproj &lt;Version&gt;.
    /// </summary>
    public string CurrentVersion
    {
        get
        {
            // UpdateManager.CurrentVersion is null when uninstalled (it does not throw).
            var v = _mgr?.CurrentVersion;
            if (v != null) return v.ToString();

            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                var plus = info.IndexOf('+'); // strip any +commit build metadata
                return plus >= 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>
    /// Check GitHub for a newer release and, if found, download/stage it. Idempotent: if an
    /// update is already staged (Ready), returns immediately without re-downloading; a no-op
    /// when running uninstalled. Returns true when an update is staged and ready to apply.
    /// </summary>
    public async Task<bool> CheckAndDownloadAsync()
    {
        if (!IsSupported) { Set(UpdateState.Unsupported); return false; }
        if (State == UpdateState.Ready && Pending != null) return true; // already staged

        try
        {
            Set(UpdateState.Checking);
            var info = await _mgr!.CheckForUpdatesAsync();
            if (info == null) { Pending = null; Set(UpdateState.UpToDate); return false; }

            Set(UpdateState.Downloading);
            await _mgr.DownloadUpdatesAsync(info);
            Pending = info;
            Set(UpdateState.Ready);
            return true;
        }
        catch
        {
            // offline / transient — leave any prior staged update intact.
            Set(UpdateState.Failed);
            return false;
        }
    }

    /// <summary>Apply the staged update and relaunch into the new version. No-op if nothing is staged.</summary>
    public void ApplyPending()
    {
        if (IsSupported && Pending != null) _mgr!.ApplyUpdatesAndRestart(Pending.TargetFullRelease);
    }
}
