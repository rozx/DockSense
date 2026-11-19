using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DockSense.App.Services;

/// <summary>Native Windows toast notifications for mode changes and problems.</summary>
public static class Toasts
{
    private const string Group = "docksense";
    private const string RestartTag = "igpu";
    private const string ActionKey = "action";
    private const string RestartAction = "restart";

    // True at first: a toast from before DockSense (or Windows) restarted may still sit in the notification center.
    private static bool _restartToastMayExist = true;

    // Counts requests to show or clear the restart toast (all on the UI thread); a held toast only goes out while
    // its own request is still the latest.
    private static int _restartRequest;
    private static bool _removeFailureLogged;

    /// <summary>
    /// Clicking a toast calls <paramref name="onOpen"/>; its "Restart now" button calls <paramref name="onRestart"/>
    /// (both on a background thread). Windows also starts DockSense for a click on a toast left in the notification
    /// center, so <paramref name="onRestart"/> must check that a restart is still called for.
    /// </summary>
    public static void Initialize(Action onOpen, Action onRestart)
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            if (IsRestartAction(e.Argument))
            {
                onRestart();
            }
            else
            {
                onOpen();
            }
        };
    }

    /// <summary>
    /// A mode change, or DockSense started by the user (<see cref="ModeApplied.IsStartup"/>): then the toast says it
    /// is running in the background and where to find it.
    /// </summary>
    public static void ShowModeApplied(ModeApplied applied)
    {
        var modeTitle = Loc.T($"ModeTitle_{applied.Mode}");

        var detail = applied.Mode switch
        {
            DockMode.Egpu => Loc.F("ModeDetail_Egpu",
                ModeResolver.FindEgpu(applied.Snapshot, App.Service.Config.Detection)?.Name ?? Loc.T("Mode_Egpu")),
            DockMode.Docked => Loc.F("ModeDetail_Docked", applied.Snapshot.Displays.Count(d => !d.IsInternal)),
            _ => Loc.T("ModeDetail_Handheld"),
        };

        var builder = new ToastContentBuilder();
        if (applied.IsStartup)
        {
            builder.AddText(Loc.T("Toast_Started")).AddText($"{modeTitle} · {detail}");
        }
        else
        {
            builder.AddText(modeTitle).AddText(detail);
        }

        if (applied.PlanName is { } plan)
        {
            builder.AddText(Loc.F("Toast_Plan", plan));
        }

        if (applied.IsStartup)
        {
            builder.AddAttributionText(Loc.T("Toast_TrayHint"));
        }

        Show(builder, IconAssets.NameFor(applied.Mode), tag: "mode");
    }

    public static void ShowProgramStarted(ProgramStarted started)
    {
        var mode = DockSenseService.ModeName(started.Mode);
        var reason = started.AtStartup ? Loc.F("Toast_ProgramStartup", mode)
            : started.Trigger == ProgramTrigger.Leave ? Loc.F("Toast_ProgramLeave", mode)
            : Loc.F("Toast_ProgramEnter", mode);
        var builder = new ToastContentBuilder()
            .AddText(Loc.F("Toast_ProgramTitle", started.Name))
            .AddText(reason);

        // One toast per program, so several programs started together all stay visible.
        var tag = "program-" + started.Name;
        Show(builder, IconAssets.NameFor(started.Mode), tag: tag.Length > 64 ? tag[..64] : tag);
    }

    public static void ShowRestartNeeded(int megabytes)
    {
        var builder = new ToastContentBuilder()
            .AddText(Loc.T("Toast_IgpuTitle"))
            .AddText(Loc.F("Toast_IgpuMessage", DockSenseService.FormatMemory(megabytes)))
            .AddButton(new ToastButton().SetContent(Loc.T("Dash_RestartNow")).AddArgument(ActionKey, RestartAction));
        _restartToastMayExist = true;

        // The toast may be held back until the shell is up (see Show). If the restart stops being needed in the
        // meantime, ClearRestartNeeded moves the request on and the held toast is dropped.
        var request = ++_restartRequest;
        Show(builder, IconAssets.App, tag: RestartTag, stillWanted: () => request == _restartRequest);
    }

    /// <summary>
    /// Takes the restart toast away once no restart is pending any more, so its button cannot mislead. That
    /// includes one that is still held back and not shown yet.
    /// </summary>
    public static void ClearRestartNeeded()
    {
        _restartRequest++;
        if (!_restartToastMayExist)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.History.Remove(RestartTag, Group);
            _restartToastMayExist = false;
        }
        catch (Exception ex)
        {
            // Tried again at the next status change; logged once, as the cause (notifications unavailable) tends to last.
            if (!_removeFailureLogged)
            {
                _removeFailureLogged = true;
                Log.Warn($"Could not remove a notification: {ex.Message}");
            }
        }
    }

    private static bool IsRestartAction(string argument)
    {
        try
        {
            return ToastArguments.Parse(argument).TryGetValue(ActionKey, out var action) && action == RestartAction;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <param name="installsAtNextStart">True when the update goes in by itself the next time DockSense starts.</param>
    public static void ShowUpdateAvailable(string version, bool installsAtNextStart)
    {
        var builder = new ToastContentBuilder()
            .AddText(Loc.F("Upd_AvailableTitle", version))
            .AddText(Loc.T(installsAtNextStart ? "Toast_UpdateNextStart" : "Toast_UpdateOpen"));
        Show(builder, IconAssets.App, tag: "update");
    }

    public static void ShowWarning(string message)
    {
        var builder = new ToastContentBuilder()
            .AddText(Loc.T("Toast_WarningTitle"))
            .AddText(message);
        Show(builder, IconAssets.App, tag: "warning");
    }

    /// <param name="stillWanted">Asked after a wait: false drops the notification instead of showing it late.</param>
    private static async void Show(ToastContentBuilder builder, string iconName, string tag, Func<bool>? stillWanted = null)
    {
        // Started by the sign-in task, DockSense is up before the Windows shell: a notification raised now (a
        // restart is needed, a warning) is held until there is something to show it.
        if (SignInSession.ShellWait is var wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
            if (stillWanted?.Invoke() == false)
            {
                return;
            }
        }

        try
        {
            builder.AddAppLogoOverride(new Uri(IconAssets.ToastImagePath(iconName)));
            // Replace the previous toast of the same kind instead of stacking them up.
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = Group;
            });
        }
        catch (Exception ex)
        {
            // Notifications are best effort (e.g. disabled by Focus Assist or group policy).
            Log.Warn($"Could not show a notification: {ex.Message}");
        }
    }
}
