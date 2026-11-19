using System.Xml;
using System.Xml.Linq;

namespace DockSense.Core;

/// <summary>What a registered sign-in task does.</summary>
/// <param name="Command">The program it starts, as stored (it may be quoted).</param>
/// <param name="Arguments">The arguments it passes.</param>
/// <param name="Enabled">False when the task was disabled, e.g. in Task Scheduler.</param>
public sealed record LogonTaskInfo(string Command, string Arguments, bool Enabled);

/// <summary>
/// The Task Scheduler task that starts DockSense the moment the user signs in.
/// <para>
/// Windows runs the per-user Run key late: measured on the Ally X, 33 s after the shell started when booting into
/// the Xbox full screen experience and 83 s after sign-in on the desktop. A task with a logon trigger starts about
/// 2 s after sign-in, before the shell (explorer.exe came up 4 s later), so the mode's profile, and above all the
/// "full screen experience at sign-in" setting, can be in place before Windows acts on it.
/// </para>
/// </summary>
public static class LogonTaskDefinition
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// The task name. Task names are shared by all users of the PC, so it carries the user's SID (a standard user
    /// can register tasks in the root folder, but not create folders there).
    /// </summary>
    public static string NameFor(string userSid) => $"DockSense-{userSid}";

    /// <summary>The task definition as Task Scheduler XML.</summary>
    /// <param name="executable">Full path of DockSense.exe, unquoted.</param>
    /// <param name="arguments">The arguments to start it with.</param>
    /// <param name="userName">The account that signs in, as <c>DOMAIN\name</c>.</param>
    /// <param name="userSid">That account's SID.</param>
    public static string BuildXml(string executable, string arguments, string userName, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "DockSense"),
                new XElement(Ns + "Description",
                    "Starts DockSense as soon as you sign in, before Windows runs the other startup apps. "
                    + "DockSense creates and removes this task with its \"Start with Windows\" setting.")),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "UserId", userName))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                // The defaults would keep a handheld on battery from starting DockSense at all.
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                // DockSense keeps running: the default limit would end it after three days.
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                // Tasks default to 7, below normal, and the programs a profile starts would inherit that.
                new XElement(Ns + "Priority", "4")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", $"\"{executable}\""),
                    new XElement(Ns + "Arguments", arguments))));

        return task.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Reads a task's XML as Task Scheduler returns it. Null when it is not a task that starts a program.</summary>
    public static LogonTaskInfo? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var task = XDocument.Parse(xml).Root;
            if (task?.Element(Ns + "Actions")?.Element(Ns + "Exec") is not { } exec
                || exec.Element(Ns + "Command")?.Value is not { Length: > 0 } command)
            {
                return null;
            }

            // No <Enabled> means enabled.
            var enabled = task.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value;
            return new LogonTaskInfo(
                command,
                exec.Element(Ns + "Arguments")?.Value ?? string.Empty,
                !string.Equals(enabled?.Trim(), "false", StringComparison.OrdinalIgnoreCase));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the task starts <paramref name="executable"/> with <paramref name="arguments"/>, whether or not
    /// the task is enabled.
    /// </summary>
    public static bool Starts(LogonTaskInfo? task, string executable, string arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        return task is not null
            && string.Equals(task.Command.Trim().Trim('"'), executable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(task.Arguments.Trim(), arguments.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
