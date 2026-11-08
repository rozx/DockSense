using Microsoft.Win32;

namespace DockSense.App.Platform;

/// <summary>
/// Xbox Full Screen Experience ("Xbox mode") settings, stored per user in
/// HKCU\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration.
/// </summary>
internal static class FseSettings
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    private const string StartupValue = "StartupToGamingHome";
    private const string HomeAppValue = "GamingHomeApp";

    /// <summary>True when this Windows build has the FSE configuration key (FSE is available on the device).</summary>
    public static bool IsAvailable
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key is not null;
        }
    }

    /// <summary>Whether Windows boots/signs in straight into FSE. Null when not set or not available.</summary>
    public static bool? StartupToFse
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(StartupValue) is int value ? value != 0 : null;
        }
    }

    /// <summary>The app FSE uses as its home (e.g. the Xbox app, or a launcher set by a third-party tool).</summary>
    public static string? HomeApp
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(HomeAppValue) as string;
        }
    }

    /// <summary>Sets boot-into-FSE. Returns true if the value changed. Does nothing when FSE is not available.</summary>
    public static bool SetStartupToFse(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null || StartupToFse == enabled)
        {
            return false;
        }

        key.SetValue(StartupValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        return true;
    }
}
