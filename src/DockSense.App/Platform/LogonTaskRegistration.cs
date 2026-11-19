using System.Security.Principal;
using DockSense.Core;

namespace DockSense.App.Platform;

/// <summary>
/// Registers, reads and removes the sign-in task (<see cref="LogonTaskDefinition"/>) through the Task Scheduler
/// COM API. A task that runs as the signed-in user with a logon trigger for that same user needs no admin rights.
/// Nothing here throws: without the task DockSense still starts from the Run key, only later. The calls are
/// late-bound, so a failure can arrive as almost any exception type (a COM error, the .NET exception an HRESULT
/// maps to, a binder error); they are all caught and logged.
/// </summary>
internal static class LogonTaskRegistration
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const string RootFolder = @"\";

    /// <summary>The registered task, or null when there is none or Task Scheduler cannot be asked.</summary>
    public static LogonTaskInfo? Read()
    {
        try
        {
            var folder = OpenRootFolder();
            if (folder is null || CurrentUser() is not { } user)
            {
                return null;
            }

            var task = folder.GetTask(LogonTaskDefinition.NameFor(user.Sid));
            return LogonTaskDefinition.Parse((string)task.Xml);
        }
        catch (FileNotFoundException)
        {
            return null; // No such task.
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the sign-in task: {ex.Message}");
            return null;
        }
    }

    /// <summary>Creates the task, or replaces it. Returns false when Windows refused.</summary>
    public static bool Register(string executable, string arguments)
    {
        try
        {
            var folder = OpenRootFolder();
            if (folder is null || CurrentUser() is not { } user)
            {
                return false;
            }

            var xml = LogonTaskDefinition.BuildXml(executable, arguments, user.Name, user.Sid);
            folder.RegisterTask(LogonTaskDefinition.NameFor(user.Sid), xml, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not register the sign-in task: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes the task. Returns true when it is gone (also when there was none).</summary>
    public static bool Remove()
    {
        try
        {
            var folder = OpenRootFolder();
            if (folder is null || CurrentUser() is not { } user)
            {
                return false;
            }

            folder.DeleteTask(LogonTaskDefinition.NameFor(user.Sid), 0);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the sign-in task: {ex.Message}");
            return false;
        }
    }

    /// <summary>The root task folder (late-bound <c>ITaskFolder</c>), or null without Task Scheduler.</summary>
    private static dynamic? OpenRootFolder()
    {
        if (Type.GetTypeFromProgID("Schedule.Service") is not { } type || Activator.CreateInstance(type) is not { } instance)
        {
            Log.Warn("Task Scheduler is not available.");
            return null;
        }

        dynamic service = instance;
        service.Connect();
        return service.GetFolder(RootFolder);
    }

    private static (string Name, string Sid)? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } sid ? (identity.Name, sid.Value) : null;
    }
}
