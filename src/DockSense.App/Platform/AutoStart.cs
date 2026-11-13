using Microsoft.Win32;

namespace DockSense.App.Platform;

/// <summary>
/// "Start with Windows" through the per-user Run key. Only changed when the user turns the setting on or off.
/// <para>
/// When Windows boots into the Xbox full screen experience it holds Run entries back until the first switch to
/// the desktop. Settings &gt; Apps &gt; Startup offers "Start at log in" per app, stored as a DWORD 1 named after
/// the Run value under <see cref="XboxModeRunKey"/>. DockSense sets it for its own entry, because the Handheld
/// profile is the one that boots into the full screen experience.
/// </para>
/// </summary>
internal static class AutoStart
{
    /// <summary>Passed by the Run key, so DockSense can tell sign-in starts from starts by the user.</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string XboxModeRunKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration\Startup\Run";
    private const string ValueName = "DockSense";
    private const int StartAtLogIn = 1;

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    /// <summary>The command written by v0.1, without <see cref="Argument"/>.</summary>
    private static string LegacyCommand => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value &&
                (string.Equals(value, Command, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(value, LegacyCommand, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        // Best effort: the Run entry above is what the setting shows.
        try
        {
            using var xboxMode = Registry.CurrentUser.CreateSubKey(XboxModeRunKey);
            if (enabled)
            {
                xboxMode.SetValue(ValueName, StartAtLogIn, RegistryValueKind.DWord);
            }
            else
            {
                xboxMode.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not update the Xbox mode startup setting: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes an entry from an older version start at sign-in in Xbox mode too. Only when Windows has no choice
    /// stored for DockSense, which is its default of waiting for the desktop: a stored choice is left alone.
    /// </summary>
    public static void EnsureStartsInXboxMode()
    {
        try
        {
            if (!IsEnabled)
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(XboxModeRunKey);
            if (key.GetValue(ValueName) is null)
            {
                key.SetValue(ValueName, StartAtLogIn, RegistryValueKind.DWord);
                Log.Info("Start with Windows: DockSense now also starts at sign-in in Xbox mode.");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not update the Xbox mode startup setting: {ex.Message}");
        }
    }

    /// <summary>Adds <see cref="Argument"/> to an entry written by an older version for this same executable.</summary>
    public static void UpgradeLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && string.Equals(value, LegacyCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
                Log.Info("Updated the Start with Windows entry to pass --autostart.");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not update the Start with Windows entry: {ex.Message}");
        }
    }
}
