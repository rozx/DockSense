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
/// <para>
/// Windows runs the Run key late (half a minute to well over a minute after sign-in), so the setting also registers
/// a sign-in task (<see cref="LogonTaskDefinition"/>) that starts DockSense about two seconds after sign-in. The Run
/// entry stays the setting itself and the fallback: the task only starts DockSense while Windows would start it from
/// the Run entry too (<see cref="AllowsLogonTaskStart"/>), and whichever of the two comes second exits silently.
/// </para>
/// </summary>
internal static class AutoStart
{
    /// <summary>Passed by the Run key and the sign-in task, so DockSense can tell sign-in starts from starts by the user.</summary>
    public const string Argument = "--autostart";

    /// <summary>Passed by the sign-in task in addition to <see cref="Argument"/>.</summary>
    public const string TaskArgument = "--logon-task";

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
                    // The key alone does not prove the feature (0.2.1 created it by mistake); Windows has to say so.
                    xboxModeAvailable: gaming is not null && FullScreenExperience.IsSupported);
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

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    private static string TaskArguments => $"{Argument} {TaskArgument}";

    // Asking Task Scheduler takes a COM round trip and the settings page refreshes often, so the answer is kept.
    // DockSense is the only one expected to change the task; a change made in Task Scheduler shows after a restart.
    private static LogonTaskInfo? _logonTask;
    private static bool _logonTaskRead;

    private static LogonTaskInfo? LogonTask
    {
        get
        {
            if (!_logonTaskRead)
            {
                _logonTask = LogonTaskRegistration.Read();
                _logonTaskRead = true;
            }

            return _logonTask;
        }
    }

    /// <summary>
    /// True when Start with Windows is on but DockSense only starts when Windows gets to its Run key, because the
    /// sign-in task is missing, disabled or for another copy.
    /// </summary>
    public static bool StartsLate(StartupStatus status) =>
        StartupEntry.AllowsLogonTask(status)
        && !(LogonTask is { Enabled: true } task && LogonTaskDefinition.Starts(task, ExecutablePath, TaskArguments));

    /// <summary>The sign-in task in words, for the diagnostic report.</summary>
    public static string DescribeLogonTask() => LogonTask switch
    {
        null => "none",
        { } task when !LogonTaskDefinition.Starts(task, ExecutablePath, TaskArguments) => $"starts something else: {task.Command} {task.Arguments}",
        { Enabled: false } => "registered for this copy, but disabled in Task Scheduler",
        _ => "registered for this copy",
    };

    /// <summary>
    /// Called when the sign-in task started this process. False means: exit, Windows would not start DockSense now
    /// (Start with Windows was switched off in Windows, or DockSense is to wait for the desktop in Xbox mode). A
    /// task left behind without a Run entry for this copy is removed.
    /// </summary>
    public static bool AllowsLogonTaskStart()
    {
        var status = Status;
        if (StartupEntry.AllowsLogonTask(status))
        {
            return true;
        }

        if (status.State is StartupState.Off or StartupState.OtherCopy)
        {
            Log.Info($"The sign-in task started DockSense, but Start with Windows is {(status.State == StartupState.Off ? "off" : "set for another copy")}; removing the task.");
            RemoveLogonTask();
        }
        else
        {
            Log.Info("The sign-in task started DockSense, but Windows holds its startup entry back; leaving the start to Windows.");
        }

        return false;
    }

    /// <summary>
    /// Registers the sign-in task when Start with Windows is on and no task starts this copy yet: for entries made
    /// by older versions, and after DockSense was moved. A task the user disabled in Task Scheduler is left alone.
    /// </summary>
    public static void EnsureLogonTask()
    {
        if (StartupEntry.AllowsLogonTask(Status) && !LogonTaskDefinition.Starts(LogonTask, ExecutablePath, TaskArguments))
        {
            RegisterLogonTask();
        }
    }

    private static void RegisterLogonTask()
    {
        if (LogonTaskRegistration.Register(ExecutablePath, TaskArguments))
        {
            Log.Info("Start with Windows: registered the sign-in task, so DockSense starts right at sign-in.");
        }

        _logonTaskRead = false;
    }

    private static void RemoveLogonTask()
    {
        LogonTaskRegistration.Remove();
        _logonTaskRead = false;
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

        // After the registry: the task is only wanted when Windows starts the Run entry at sign-in.
        if (enabled && StartupEntry.AllowsLogonTask(Status))
        {
            RegisterLogonTask();
        }
        else
        {
            RemoveLogonTask();
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
