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
/// (shut down as a handheld, switched on docked to the eGPU). Started by the sign-in task, DockSense runs a few
/// seconds before the shell, so every millisecond before this write counts; everything else waits until after it.
/// When the write comes too late anyway, the normal startup switches the full screen experience live.
/// </para>
/// </summary>
internal static class SignInFastPath
{
    private static bool? _fseChange;

    public static void Run()
    {
        try
        {
            // First run (no config yet) has nothing to apply. The key check must not see the key 0.2.1 left behind.
            AutoStart.RemoveMistakenXboxModeKey();
            if (!File.Exists(ConfigStore.DefaultPath) || !FseSettings.IsAvailable)
            {
                return;
            }

            var config = new ConfigStore(ConfigStore.DefaultPath).Load();
            var mode = ModeResolver.Resolve(DeviceProbe.TakeSnapshot(), config.Detection);
            var fse = config.ProfileFor(mode).Fse;
            if (fse == FseStartup.Unchanged)
            {
                return;
            }

            var wantFse = fse == FseStartup.FullScreenExperience;
            var changed = FseSettings.SetStartupToFse(wantFse);
            if (changed)
            {
                _fseChange = wantFse;
            }

            // Only now anything that is not needed for the write. The times tell whether it beat the shell.
            using var process = Process.GetCurrentProcess();
            var sinceStart = DateTime.Now - process.StartTime;
            var sinceSignIn = SignInSession.SinceSignIn is { } elapsed ? $"{elapsed.TotalSeconds:F1} s after sign-in" : "sign-in time unknown";
            Log.Info($"Sign-in start in {mode} mode: Full Screen Experience at sign-in {(changed ? "->" : "already")} {(wantFse ? "on" : "off")} "
                + $"({sinceStart.TotalMilliseconds:F0} ms after DockSense started, {sinceSignIn}).");
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
}
