using System.Windows.Controls;
using DockSense.App.Localization;
using H.NotifyIcon;

namespace DockSense.App.Services;

/// <summary>The notification-area icon: pixel icon per mode, tooltip, and a small menu.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly DockSenseService _service;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _modeItem = new() { IsEnabled = false };
    private readonly MenuItem _openItem = new() { FontWeight = System.Windows.FontWeights.SemiBold };
    private readonly MenuItem _pauseItem = new() { IsCheckable = true };
    private readonly MenuItem _recheckItem = new();
    private readonly MenuItem _exitItem = new();
    private string? _currentIconName;

    public TrayIconService(DockSenseService service, Action openWindow, Action exit)
    {
        _service = service;
        _openItem.Click += (_, _) => openWindow();
        _pauseItem.Click += (_, _) => _service.SetPaused(_pauseItem.IsChecked);
        _recheckItem.Click += (_, _) => _service.ScheduleRescan("requested from the tray");
        _exitItem.Click += (_, _) => exit();

        var menu = new ContextMenu();
        foreach (var item in new Control[] { _modeItem, new Separator(), _openItem, _pauseItem, _recheckItem, new Separator(), _exitItem })
        {
            menu.Items.Add(item);
        }

        _icon = new TaskbarIcon
        {
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => openWindow();
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _service.StatusChanged += (_, _) => Update();
        Loc.Instance.PropertyChanged += (_, _) => Update();
        Update();
    }

    public void Dispose() => _icon.Dispose();

    private void Update()
    {
        var iconName = IconAssets.NameFor(_service.Mode, _service.Paused);
        if (iconName != _currentIconName)
        {
            _icon.IconSource = IconAssets.TrayIcon(iconName);
            _currentIconName = iconName;
        }

        var mode = Loc.T($"ModeTitle_{_service.Mode}");
        var plan = DockSenseService.SafeGetActivePlanName() ?? Loc.T("Common_Unknown");
        var tooltip = _service.Paused ? $"DockSense - {Loc.T("Status_Paused")}" : $"DockSense - {mode}\n{plan}";
        _icon.ToolTipText = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        _modeItem.Header = Loc.F("Tray_Mode", _service.Paused ? Loc.T("Status_Paused") : mode);
        _openItem.Header = Loc.T("Tray_Open");
        _pauseItem.Header = Loc.T("Tray_Pause");
        _pauseItem.IsChecked = _service.Paused;
        _recheckItem.Header = Loc.T("Tray_Recheck");
        _exitItem.Header = Loc.T("Tray_Exit");
    }
}
