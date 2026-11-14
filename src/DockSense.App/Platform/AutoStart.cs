using DockSense.Core;
using Microsoft.Win32;

namespace DockSense.App.Platform;

/// <summary>
/// "Start with Windows" through the per-user Run key. Only changed when the user turns the setting on or off.
/// <para>
/// A Run entry alone does not mean DockSense starts (see <see cref="StartupEntry"/>): Windows has its own on/off
/// switch for it (Task Manager, Settings &gt; Apps &gt; Startup), and when Windows boots into the Xbox full screen
/// experience it holds Run entries back until the first switch to the desktop, unless the entry is set to "Start
/// at log in". DockSense sets that for its own entry, because the Handheld profile is the one that boots into the
/// full screen experience.
/// </para>
/// </summary>
internal static class AutoStart
{
    /// <summary>Passed by the Run key, so DockSense can tell sign-in starts from starts by the user.</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string GamingKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string XboxModeRunSubKey = @"Startup\Run";
    private const string ValueName = "DockSense";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    /// <summary>The command written by v0.1, without <see cref="Argument"/>.</summary>
    private static string LegacyCommand => $"\"{Environment.ProcessPath}\"";

    /// <summary>What really happens at sign-in. Never throws: an unreadable registry counts as off.</summary>
    public static StartupStatus Status
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                using var gaming = Registry.CurrentUser.OpenSubKey(GamingKey);
                using var xboxMode = gaming?.OpenSubKey(XboxModeRunSubKey);
                return StartupEntry.Evaluate(
                    run?.GetValue(ValueName) as string,
                    [Command, LegacyCommand],
                    approved?.GetValue(ValueName) as byte[],
                    xboxMode?.GetValue(ValueName) as int?,
                    xboxModeAvailable: gaming is not null);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn($"Could not read the Start with Windows entry: {ex.Message}");
                return new StartupStatus(StartupState.Off);
            }
        }
    }

    /// <summary>True when this copy of DockSense starts at sign-in.</summary>
    public static bool IsEnabled => Status.State == StartupState.On;

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

        // Best effort: the Run entry above is what the setting is about.
        try
        {
            // Turning the setting on here also undoes "Disabled" in Task Manager or Windows Settings; with no value
            // Windows treats the entry as enabled. Turning it off leaves nothing behind either.
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            {
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            SetXboxModeStartAtLogIn(enabled);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not update the Windows startup settings for DockSense: {ex.Message}");
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

            using var gaming = Registry.CurrentUser.OpenSubKey(GamingKey, writable: true);
            if (gaming is null)
            {
                return; // No full screen experience on this PC.
            }

            using var key = gaming.CreateSubKey(XboxModeRunSubKey);
            if (key.GetValue(ValueName) is null)
            {
                key.SetValue(ValueName, StartupEntry.XboxModeStartAtLogIn, RegistryValueKind.DWord);
                Log.Info("Start with Windows: DockSense now also starts at sign-in in Xbox mode.");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not update the Xbox mode startup setting: {ex.Message}");
        }
    }

    /// <summary>
    /// v0.2.1 created the full screen experience key on PCs that do not have the feature, which made DockSense
    /// treat it as available there. Removes the key again when it holds nothing but what DockSense wrote and
    /// Windows says the feature is not supported. Call this before anything reads <see cref="FseSettings"/>.
    /// </summary>
    public static void RemoveMistakenXboxModeKey()
    {
        try
        {
            if (FullScreenExperience.IsSupported)
            {
                return;
            }

            using (var gaming = Registry.CurrentUser.OpenSubKey(GamingKey))
            {
                if (gaming is null
                    || gaming.GetValueNames().Any(name => !name.Equals("StartupToGamingHome", StringComparison.OrdinalIgnoreCase))
                    || !gaming.GetSubKeyNames().SequenceEqual(["Startup"], StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                using var startup = gaming.OpenSubKey("Startup");
                using var run = startup?.OpenSubKey("Run");
                if (startup is null || run is null
                    || startup.ValueCount != 0 || startup.SubKeyCount != 1
                    || run.SubKeyCount != 0
                    || run.GetValueNames().Any(name => !name.Equals(ValueName, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(GamingKey, throwOnMissingSubKey: false);
            Log.Info("Removed the full screen experience key that v0.2.1 created on this PC, which does not have the feature.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not check the full screen experience key: {ex.Message}");
        }
    }

    /// <summary>
    /// Stores or removes "Start at log in" for DockSense. Never creates the full screen experience key itself: on
    /// a PC without the feature that key must stay absent.
    /// </summary>
    private static void SetXboxModeStartAtLogIn(bool enabled)
    {
        using var gaming = Registry.CurrentUser.OpenSubKey(GamingKey, writable: true);
        if (gaming is null)
        {
            return;
        }

        if (enabled)
        {
            using var key = gaming.CreateSubKey(XboxModeRunSubKey);
            key.SetValue(ValueName, StartupEntry.XboxModeStartAtLogIn, RegistryValueKind.DWord);
        }
        else
        {
            using var key = gaming.OpenSubKey(XboxModeRunSubKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
