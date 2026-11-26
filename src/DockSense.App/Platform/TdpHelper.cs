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

    /// <summary>Longer than the task's own execution limit of one minute (<see cref="TdpHelperTask.BuildXml"/>).</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(75);

    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2.5);
    private static long lastStartTicks = -(long)MinimumGap.TotalMilliseconds;

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

                // Only a plain path on a fixed local drive can be judged by its permissions: the task stores the
                // path as it is spelled, and a link anywhere in it could later lead somewhere else than the
                // place whose permissions were read here.
                if (new DriveInfo(file.FullName).DriveType != DriveType.Fixed || IsLink(file))
                {
                    return false;
                }

                // And only under Program Files: the task keeps the path for as long as it exists, and those are
                // the folders that stay administrator-managed whatever is installed or removed inside them. Any
                // other tree, however well protected today, can be taken away and its path made again by anyone.
                if (!IsUnderProgramFiles(folder.FullName))
                {
                    return false;
                }

                var ancestors = new List<LocationAccess>();
                for (var above = folder; above is not null; above = above.Parent)
                {
                    if (IsLink(above))
                    {
                        return false;
                    }

                    if (above != folder)
                    {
                        ancestors.Add(ReadAccess(above.GetAccessControl(AccessSections)));
                    }
                }

                return ElevatedLocation.IsProtected(
                    ReadAccess(file.GetAccessControl(AccessSections)), ReadAccess(folder.GetAccessControl(AccessSections)), ancestors);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or InvalidOperationException or IdentityNotMappedException or Win32Exception or ArgumentException)
            {
                return false;
            }
        }
    }

    private const AccessControlSections AccessSections = AccessControlSections.Access | AccessControlSections.Owner;

    /// <summary>True for a junction, a symbolic link or any other reparse point.</summary>
    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>True for a folder inside one of the Program Files folders (not for those folders themselves).</summary>
    private static bool IsUnderProgramFiles(string folder)
    {
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(special);
            if (root.Length > 0 && folder.StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// True when the helper task is registered for this copy of DockSense, enabled, and set to run with the
    /// highest privileges. A task changed to run without them could not open the driver.
    /// </summary>
    public static bool IsInstalled => ReadTaskXml() is { } xml
        && TdpHelperTask.RunsElevated(xml)
        && TdpHelperTask.Starts(LogonTaskDefinition.Parse(xml), ExecutablePath);

    /// <summary>True when a task with the helper's name exists for this user, whatever it does.</summary>
    public static bool TaskExists => ReadTaskXml() is not null;

    /// <summary>What, if anything, keeps a TDP override from being written.</summary>
    public static TdpReadiness Readiness() =>
        !PawnIo.IsInstalled ? TdpReadiness.DriverMissing
        : !InstallFolderIsProtected ? TdpReadiness.UnprotectedFolder
        : !IsInstalled ? TdpReadiness.HelperMissing
        : TdpReadiness.Ready;

    /// <summary>For the diagnostic report.</summary>
    public static string DescribeTask()
    {
        if (ReadTaskXml() is not { } xml)
        {
            return "not registered";
        }

        return LogonTaskDefinition.Parse(xml) switch
        {
            null => "registered, but not a task that starts a program",
            { } task when !LogonTaskDefinition.Starts(task, ExecutablePath, TdpHelperTask.Arguments) => $"starts something else: {task.Command} {task.Arguments}",
            { Enabled: false } => "registered, but disabled",
            _ when !TdpHelperTask.RunsElevated(xml) => "registered, but not set to run with the highest privileges",
            _ => "registered",
        };
    }

    /// <summary>Registers the helper task. Windows asks for administrator approval.</summary>
    public static TdpSetupResult Install() =>
        CurrentUserSid() is { } sid ? RunElevated(ExecutablePath, [InstallArgument, sid], expectInstalled: true) : TdpSetupResult.Failed;

    /// <summary>
    /// Removes the helper task. Windows asks for administrator approval, for its own Task Scheduler tool: removing
    /// must also work from a copy of DockSense that is not in a protected place (the task may be a leftover from
    /// one that was), and such a copy must not be the thing that gets administrator rights.
    /// </summary>
    public static TdpSetupResult Remove() =>
        CurrentUserSid() is { } sid
            ? RunElevated(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), ["/Delete", "/TN", TdpHelperTask.NameFor(sid), "/F"], expectInstalled: false)
            : TdpSetupResult.Failed;

    /// <summary>
    /// Starts the helper task with <paramref name="watts"/> and waits for it. Blocks until the task has ended
    /// (normally well under a second, at most <see cref="WriteTimeout"/>); call it off the UI thread, one call
    /// at a time.
    /// </summary>
    public static TdpWriteOutcome Write(int watts)
    {
        var running = false;
        try
        {
            if (LogonTaskRegistration.OpenRootFolder() is not { } folder || CurrentUserSid() is not { } sid)
            {
                return new TdpWriteOutcome(TdpWriteResult.Failed);
            }

            var name = TdpHelperTask.NameFor(sid);

            // The task reports its last run to the second. Runs from this process are kept that far apart, so
            // that the result read below belongs to this one and not to the run before it. Measured on a clock
            // that only runs forward: the wall clock can be set back.
            var sinceLast = TimeSpan.FromMilliseconds(Environment.TickCount64 - lastStartTicks);
            if (sinceLast < MinimumGap)
            {
                Thread.Sleep(MinimumGap - sinceLast);
            }

            var started = DateTime.Now;
            lastStartTicks = Environment.TickCount64;
            var instance = folder.GetTask(name).Run(watts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            running = true;

            // Waited for longer than the task may run (Task Scheduler ends it after a minute), so that giving up
            // means it is no longer there to set anything late.
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
                if ((DateTime)task.LastRunTime >= started.AddSeconds(-1) && exitCode != TaskStillRunning)
                {
                    // A write that went through says how many watts it sent. Any code the helper does not use
                    // means it did not end by itself (ended by Task Scheduler, or crashed), possibly after a
                    // limit had gone in.
                    return TdpHelperTask.OutcomeOf(exitCode);
                }

                Thread.Sleep(50);
            }

            Log.Warn("The TDP helper task did not report back.");
            return new TdpWriteOutcome(TdpWriteResult.Unconfirmed);
        }
        catch (FileNotFoundException)
        {
            // The task was removed meanwhile.
            return new TdpWriteOutcome(running ? TdpWriteResult.Unconfirmed : TdpWriteResult.Failed);
        }
        catch (Exception ex)
        {
            // Late-bound COM: a failure can arrive as almost any exception type (see LogonTaskRegistration).
            // Once the task was started, losing track of it does not mean it did nothing.
            Log.Warn($"Could not run the TDP helper task: {ex.Message}");
            return new TdpWriteOutcome(running ? TdpWriteResult.Unconfirmed : TdpWriteResult.Failed);
        }
    }

    /// <summary>The elevated half of <see cref="Install"/>. Returns the exit code.</summary>
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
            // Checked here as well, not only before asking: this command can be started by anyone, and approving
            // its prompt must never leave a task behind that runs a file the user's programs can replace.
            if (!InstallFolderIsProtected)
            {
                return 3;
            }

            // Quiet: this runs elevated, and the log is in the user's folders.
            if (LogonTaskRegistration.OpenRootFolder(quiet: true) is not { } folder)
            {
                return 1;
            }

            var name = TdpHelperTask.NameFor(sid);
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

    private static TdpSetupResult RunElevated(string program, string[] arguments, bool expectInstalled)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var what = expectInstalled ? "setup" : "removal";
        try
        {
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(SetupTimeout))
            {
                Log.Warn($"The TDP helper {what} did not finish.");
                return TdpSetupResult.Failed;
            }

            // Installed means this copy's task, ready to use; removed means no task under that name at all.
            if (expectInstalled ? (process.ExitCode != 0 || !IsInstalled) : TaskExists)
            {
                Log.Warn($"The TDP helper {what} failed with exit code {process.ExitCode}; task: {DescribeTask()}.");
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

    /// <summary>The registered helper task as Task Scheduler XML, or null when there is none.</summary>
    private static string? ReadTaskXml()
    {
        try
        {
            if (LogonTaskRegistration.OpenRootFolder() is not { } folder || CurrentUserSid() is not { } sid)
            {
                return null;
            }

            return (string)folder.GetTask(TdpHelperTask.NameFor(sid)).Xml;
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
