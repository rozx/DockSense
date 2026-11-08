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

    public static void ShowModeApplied(ModeApplied applied)
    {
        var detail = applied.Mode switch
        {
            DockMode.Egpu => Loc.F("ModeDetail_Egpu",
                ModeResolver.FindEgpu(applied.Snapshot, App.Service.Config.Detection)?.Name ?? Loc.T("Mode_Egpu")),
            DockMode.Docked => Loc.F("ModeDetail_Docked", applied.Snapshot.Displays.Count(d => !d.IsInternal)),
            _ => Loc.T("ModeDetail_Handheld"),
        };

        var builder = new ToastContentBuilder()
            .AddText(Loc.T($"ModeTitle_{applied.Mode}"))
            .AddText(detail);
        if (applied.PlanName is { } plan)
        {
            builder.AddText(Loc.F("Toast_Plan", plan));
        }

        Show(builder, IconAssets.NameFor(applied.Mode), tag: "mode");
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
