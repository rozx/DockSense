using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DockSense.Core;

namespace DockSense.App.Platform;

/// <summary>How setting up or removing the helper task ended.</summary>
internal enum TdpSetupResult
{
    Done,

    /// <summary>The user said no at the Windows prompt.</summary>
    Declined,

    Failed,
}

/// <summary>
/// The unelevated half of the TDP override: whether it can work here, the helper task
/// (<see cref="TdpHelperTask"/>: registered and removed with one administrator approval each), and starting that
/// task to have <see cref="TdpWriter"/> set a value.
/// </summary>
internal static class TdpHelper
{
    /// <summary><c>--install-tdp-helper &lt;user SID&gt;</c>: registers the task. Run elevated.</summary>
    public const string InstallArgument = "--install-tdp-helper";

    /// <summary><c>--remove-tdp-helper &lt;user SID&gt;</c>: removes the task. Run elevated.</summary>
    public const string RemoveArgument = "--remove-tdp-helper";

    /// <summary>Where the driver comes from. DockSense does not install it.</summary>
    public const string PawnIoUrl = "https://pawnio.eu/";

    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskStateQueued = 2;
    private const int TaskStateRunning = 4;
    private const int ErrorCancelled = 1223;

    /// <summary>SCHED_S_TASK_RUNNING: what a task reports as its last result while an instance runs.</summary>
    private const int TaskStillRunning = 0x41301;

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);

    /// <summary>True on a device and processor DockSense can set the TDP of.</summary>
    public static bool IsSupported => TdpWriter.Device is not null && TdpWriter.Commands is not null;

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    /// <summary>
    /// True when the helper task may run this copy: it sits where only administrators can change files
    /// (<see cref="LocationIsProtected"/>). Debug builds skip the check so the feature can be developed.
    /// </summary>
    public static bool InstallFolderIsProtected
    {
        get
        {
#if DEBUG
            return true;
#else
            return LocationIsProtected;
#endif
        }
    }

    /// <summary>
    /// True when nobody without administrator rights can replace this executable: not the file, not its folder,
    /// not a folder above it (<see cref="ElevatedLocation"/>). The helper task runs this file elevated, so from
    /// anywhere else a program could put its own code in its place and get administrator rights without a prompt.
    /// Read from the permissions, so the answer is the same whether or not the caller is elevated. A location
    /// whose permissions cannot be read counts as unprotected.
    /// </summary>
    public static bool LocationIsProtected
    {
        get
        {
            try
            {
                var file = new FileInfo(ExecutablePath);
                if (!file.Exists || file.Directory is not { } folder)
                {
                    return false;
                }

                var ancestors = new List<LocationAccess>();
                for (var above = folder.Parent; above is not null; above = above.Parent)
                {
                    ancestors.Add(ReadAccess(above.GetAccessControl(AccessSections)));
                }

                return ElevatedLocation.IsProtected(
                    ReadAccess(file.GetAccessControl(AccessSections)), ReadAccess(folder.GetAccessControl(AccessSections)), ancestors);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or InvalidOperationException or IdentityNotMappedException)
            {
                return false;
            }
        }
    }

    private const AccessControlSections AccessSections = AccessControlSections.Access | AccessControlSections.Owner;

    private static LocationAccess ReadAccess(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        var grants = new List<LocationGrant>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // Entries that are only handed down to children do not apply to the object itself.
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            grants.Add(new LocationGrant(rule.IdentityReference.Value, ElevatedLocation.RightsFrom((int)rule.FileSystemRights)));
        }

        return new LocationAccess(owner, grants);
    }

    /// <summary>True when the helper task is registered for this copy of DockSense.</summary>
    public static bool IsInstalled => TdpHelperTask.Starts(ReadTask(), ExecutablePath);

    /// <summary>What, if anything, keeps a TDP override from being written.</summary>
    public static TdpReadiness Readiness() =>
        !PawnIo.IsInstalled ? TdpReadiness.DriverMissing
        : !InstallFolderIsProtected ? TdpReadiness.UnprotectedFolder
        : !IsInstalled ? TdpReadiness.HelperMissing
        : TdpReadiness.Ready;

    /// <summary>For the diagnostic report.</summary>
    public static string DescribeTask() => ReadTask() switch
    {
        null => "not registered",
        { } task when !LogonTaskDefinition.Starts(task, ExecutablePath, TdpHelperTask.Arguments) => $"starts something else: {task.Command} {task.Arguments}",
        { Enabled: false } => "registered, but disabled",
        _ => "registered",
    };

    /// <summary>Registers the helper task. Windows asks for administrator approval.</summary>
    public static TdpSetupResult Install() => RunElevated(InstallArgument, expectInstalled: true);

    /// <summary>Removes the helper task. Windows asks for administrator approval.</summary>
    public static TdpSetupResult Remove() => RunElevated(RemoveArgument, expectInstalled: false);

    /// <summary>
    /// Starts the helper task with <paramref name="watts"/> and waits for it. Blocks for up to
    /// <see cref="WriteTimeout"/>; call it off the UI thread.
    /// </summary>
    public static TdpWriteResult Write(int watts)
    {
        var running = false;
        try
        {
            if (LogonTaskRegistration.OpenRootFolder() is not { } folder || CurrentUserSid() is not { } sid)
            {
                return TdpWriteResult.Failed;
            }

            var name = TdpHelperTask.NameFor(sid);
            var started = DateTime.Now;
            var instance = folder.GetTask(name).Run(watts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            running = true;

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < WriteTimeout && IsRunning(instance))
            {
                Thread.Sleep(50);
            }

            // The result shows up on the task a moment after the instance is gone.
            while (clock.Elapsed < WriteTimeout)
            {
                var task = folder.GetTask(name); // A fresh object: an old one keeps what it read first.
                var exitCode = (int)task.LastTaskResult;
                if ((DateTime)task.LastRunTime >= started.AddSeconds(-2) && exitCode != TaskStillRunning)
                {
                    return Enum.IsDefined(typeof(TdpWriteResult), exitCode) ? (TdpWriteResult)exitCode : TdpWriteResult.Failed;
                }

                Thread.Sleep(50);
            }

            // Giving up on waiting does not stop the task: it may still set the value.
            Log.Warn("The TDP helper task did not finish in time.");
            return TdpWriteResult.Unconfirmed;
        }
        catch (FileNotFoundException)
        {
            return running ? TdpWriteResult.Unconfirmed : TdpWriteResult.Failed; // The task was removed meanwhile.
        }
        catch (Exception ex)
        {
            // Late-bound COM: a failure can arrive as almost any exception type (see LogonTaskRegistration).
            // Once the task was started, losing track of it does not mean it did nothing.
            Log.Warn($"Could not run the TDP helper task: {ex.Message}");
            return running ? TdpWriteResult.Unconfirmed : TdpWriteResult.Failed;
        }
    }

    /// <summary>The elevated half of <see cref="Install"/> and <see cref="Remove"/>. Returns the exit code.</summary>
    public static int SetupFromCommandLine(string[] args)
    {
        // The task belongs to the user who asked. Approval given with another account's password means that user
        // is not an administrator, and then the task could not run elevated for them either.
        if (args.Length != 2 || CurrentUserSid() is not { } sid || !string.Equals(args[1], sid, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        try
        {
            if (LogonTaskRegistration.OpenRootFolder() is not { } folder)
            {
                return 1;
            }

            var name = TdpHelperTask.NameFor(sid);
            if (args[0].Equals(RemoveArgument, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    folder.DeleteTask(name, 0);
                }
                catch (FileNotFoundException)
                {
                    // Already gone.
                }

                return 0;
            }

            // Checked here as well, not only before asking: this command can be started by anyone, and approving
            // its prompt must never leave a task behind that runs a file the user's programs can replace.
            if (!InstallFolderIsProtected)
            {
                return 3;
            }

            var xml = TdpHelperTask.BuildXml(ExecutablePath, sid);
            folder.RegisterTask(name, xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, TdpHelperTask.SecurityDescriptorFor(sid));
            return 0;
        }
        catch (Exception)
        {
            return 1; // No log here: this runs elevated, and the user's folders are not a safe place to write then.
        }
    }

    /// <summary>True while the started instance (<c>IRunningTask</c>) is queued or running.</summary>
    private static bool IsRunning(dynamic instance)
    {
        try
        {
            instance.Refresh();
            return (int)instance.State is TaskStateRunning or TaskStateQueued;
        }
        catch (Exception)
        {
            return false; // Task Scheduler no longer knows the instance: it has ended.
        }
    }

    private static TdpSetupResult RunElevated(string argument, bool expectInstalled)
    {
        if (CurrentUserSid() is not { } sid)
        {
            return TdpSetupResult.Failed;
        }

        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add(sid);

        try
        {
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(SetupTimeout))
            {
                Log.Warn($"The TDP helper setup ({argument}) did not finish.");
                return TdpSetupResult.Failed;
            }

            if (process.ExitCode != 0 || IsInstalled != expectInstalled)
            {
                Log.Warn($"The TDP helper setup ({argument}) failed with exit code {process.ExitCode}; task: {DescribeTask()}.");
                return TdpSetupResult.Failed;
            }

            Log.Info(expectInstalled ? "Registered the TDP helper task." : "Removed the TDP helper task.");
            return TdpSetupResult.Done;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return TdpSetupResult.Declined;
        }
        catch (Win32Exception ex)
        {
            Log.Error("Could not ask for administrator approval", ex);
            return TdpSetupResult.Failed;
        }
    }

    private static LogonTaskInfo? ReadTask()
    {
        try
        {
            if (LogonTaskRegistration.OpenRootFolder() is not { } folder || CurrentUserSid() is not { } sid)
            {
                return null;
            }

            return LogonTaskDefinition.Parse((string)folder.GetTask(TdpHelperTask.NameFor(sid)).Xml);
        }
        catch (FileNotFoundException)
        {
            return null; // No such task.
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the TDP helper task: {ex.Message}");
            return null;
        }
    }

    private static string? CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }
}
