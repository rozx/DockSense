using System.ComponentModel;
using System.Diagnostics;

namespace DockSense.App.Platform;

/// <summary>Restarting Windows, only ever on the user's request.</summary>
internal static class SystemPower
{
    /// <summary>Asks Windows to restart right away. Returns false when the request could not be made.</summary>
    public static bool Restart()
    {
        try
        {
            // No /f: Windows still asks apps with unsaved work before restarting.
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
            Log.Info("Restart requested.");
            return true;
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"Could not restart: {ex.Message}");
            return false;
        }
    }
}
