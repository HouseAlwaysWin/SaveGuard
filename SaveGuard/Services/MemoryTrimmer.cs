using System;
using System.Runtime.InteropServices;

namespace SaveGuard.Services;

/// <summary>
/// Collapses the process working set after the app goes idle (collapsed to tray /
/// minimized) or finishes a task (backup / restore). Forces a GC, then asks Windows to
/// trim the working set to zero — paged-out pages fault back in on demand, but for an
/// idle tray watcher the figure Task Manager shows drops from ~100 MB to a few MB.
/// This trims the working set, not real commit; it's cosmetic-but-wanted, never a leak
/// fix. Windows-only, best-effort, throttled, and never throws.
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    // 0, not long.MinValue: the first call computes (now - _lastTrim), and subtracting
    // long.MinValue overflows to a negative value that the throttle reads as "too soon".
    // TickCount64 is always well past 1500 by the time the app runs, so 0 lets the first
    // trim through.
    private static long _lastTrim;

    public static void Trim()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Avoid back-to-back trims (e.g. minimize immediately followed by hide-to-tray).
        var now = Environment.TickCount64;
        if (_lastTrim != 0 && now - _lastTrim < 1500) return;
        _lastTrim = now;

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // min = max = (SIZE_T)-1 tells Windows to temporarily trim the working set to zero.
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // Best-effort and purely cosmetic — never let it affect the app.
        }
    }
}
