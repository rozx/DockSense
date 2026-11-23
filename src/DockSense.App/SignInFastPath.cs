using System.ComponentModel;
using System.Diagnostics;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

/// <summary>
/// The first thing a start at sign-in does, before any UI is loaded: put the "full screen experience at sign-in"
/// setting of the mode the device is in now in place.
/// <para>
/// Windows reads that setting once, when the shell starts, and the mode can have changed while the PC was off
/// (shut down as a handheld, switched on docked to the eGPU). Started by the sign-in task, DockSense runs about
/// as early as the shell does, so every millisecond before this write counts; everything else waits until after
/// it. When the write comes too late anyway, the normal startup switches the full screen experience live.
/// </para>
/// <para>
/// Up to the write, only the registry, the settings file and the hardware probe may be used. Nothing that asks
/// the Windows shell: in 0.3.3 the write came 16 to 52 seconds after DockSense had started at real sign-ins
/// (0.1 to 0.3 seconds with the shell already up), and the two calls into the gaming experience API ahead of it
/// (<see cref="FullScreenExperience.IsSupported"/>) were the only ones of that kind. The log line gives the
/// time each step took, so a slow one shows.
/// </para>
/// </summary>
internal static class SignInFastPath
{
    private static DateTime _mainEnteredUtc;
    private static long _mainEntered;
    private static bool? _fseChange;

    /// <summary>Call first thing in <c>Main</c>: where the time DockSense itself spends starts to count.</summary>
    public static void MarkMainEntered()
    {
        _mainEnteredUtc = DateTime.UtcNow;
        _mainEntered = Stopwatch.GetTimestamp();
    }

    public static void Run()
    {
        try
        {
            var entered = Stopwatch.GetTimestamp();

            // First run (no config yet) has nothing to apply. The key alone is enough here: on a PC without the
            // feature nothing reads the value, and the key 0.2.1 left behind there is removed by the normal startup.
            if (!File.Exists(ConfigStore.DefaultPath) || !FseSettings.IsAvailable)
            {
                return;
            }

            var config = new ConfigStore(ConfigStore.DefaultPath).Load();
            var configLoaded = Stopwatch.GetTimestamp();
            var adapters = GpuProbe.GetWorkingAdapters();
            var adaptersRead = Stopwatch.GetTimestamp();
            var displays = DisplayProbe.GetActiveDisplays();
            var displaysRead = Stopwatch.GetTimestamp();

            var mode = ModeResolver.Resolve(new DeviceSnapshot(adapters, displays, DeviceProbe.OnAcPower), config.Detection);
            var fse = config.ProfileFor(mode).Fse;
            if (fse == FseStartup.Unchanged)
            {
                return;
            }

            var wantFse = fse == FseStartup.FullScreenExperience;
            var changed = FseSettings.SetStartupToFse(wantFse);
            var writtenUtc = DateTime.UtcNow;
            var written = Stopwatch.GetTimestamp();
            if (changed)
            {
                _fseChange = wantFse;
            }

            // Only now anything that is not needed for the write. The times tell whether it beat the shell.
            using var process = Process.GetCurrentProcess();
            var startedUtc = process.StartTime.ToUniversalTime();
            var sinceSignIn = SignInSession.SignInTimeUtc is { } signIn
                ? $"{(writtenUtc - signIn).TotalSeconds:F1} s after sign-in"
                : "sign-in time unknown";
            var steps = string.Join(", ",
                Step("runtime", _mainEnteredUtc - startedUtc),
                Step("startup check", Stopwatch.GetElapsedTime(_mainEntered, entered)),
                Step("settings", Stopwatch.GetElapsedTime(entered, configLoaded)),
                Step("adapters", Stopwatch.GetElapsedTime(configLoaded, adaptersRead)),
                Step("displays", Stopwatch.GetElapsedTime(adaptersRead, displaysRead)),
                Step("write", Stopwatch.GetElapsedTime(displaysRead, written)));
            Log.Info($"Sign-in start in {mode} mode: Full Screen Experience at sign-in {(changed ? "->" : "already")} {(wantFse ? "on" : "off")} "
                + $"({(writtenUtc - startedUtc).TotalMilliseconds:F0} ms after DockSense started, {sinceSignIn}; {steps}).");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The normal startup applies the profile again and reports what does not work.
            Log.Warn($"Could not apply the sign-in setting early: {ex.Message}");
        }
    }

    /// <summary>
    /// True once when this start already turned the sign-in setting to <paramref name="wantFse"/>, so that the
    /// activity list can still say so (the normal startup finds the value in place and would stay silent).
    /// </summary>
    public static bool TakeFseChange(bool wantFse)
    {
        var changed = _fseChange == wantFse;
        _fseChange = null;
        return changed;
    }

    private static string Step(string name, TimeSpan time) => $"{name} {time.TotalMilliseconds:F0} ms";
}
