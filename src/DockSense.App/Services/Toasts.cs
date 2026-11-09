using DockSense.App.Localization;
using DockSense.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DockSense.App.Services;

/// <summary>Native Windows toast notifications for mode changes and problems.</summary>
public static class Toasts
{
    /// <summary>Clicking a toast calls <paramref name="onActivated"/> (on a background thread).</summary>
    public static void Initialize(Action onActivated)
    {
        ToastNotificationManagerCompat.OnActivated += _ => onActivated();
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
            .AddText(Loc.F("Toast_IgpuMessage", DockSenseService.FormatMemory(megabytes)));
        Show(builder, IconAssets.App, tag: "igpu");
    }

    public static void ShowWarning(string message)
    {
        var builder = new ToastContentBuilder()
            .AddText(Loc.T("Toast_WarningTitle"))
            .AddText(message);
        Show(builder, IconAssets.App, tag: "warning");
    }

    private static void Show(ToastContentBuilder builder, string iconName, string tag)
    {
        try
        {
            builder.AddAppLogoOverride(new Uri(IconAssets.ToastImagePath(iconName)));
            // Replace the previous toast of the same kind instead of stacking them up.
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = "docksense";
            });
        }
        catch (Exception ex)
        {
            // Notifications are best effort (e.g. disabled by Focus Assist or group policy).
            Log.Warn($"Could not show a notification: {ex.Message}");
        }
    }
}
