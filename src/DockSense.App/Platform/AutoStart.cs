using Microsoft.Win32;

namespace DockSense.App.Platform;

/// <summary>"Start with Windows" through the per-user Run key. Only changed when the user turns the setting on or off.</summary>
internal static class AutoStart
{
    /// <summary>Passed by the Run key, so DockSense can tell sign-in starts from starts by the user.</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DockSense";

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
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
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
