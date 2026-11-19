using System.Windows;
using System.Windows.Controls;
using DockSense.App.Localization;
using DockSense.Core;
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

    // "Restart for <mode>": one item per mode, shown only while restarting for that mode is worth it.
    private readonly Dictionary<DockMode, MenuItem> _restartItems = Enum.GetValues<DockMode>().ToDictionary(mode => mode, _ => new MenuItem());
    private readonly Separator _restartSeparator = new();
    private readonly System.Windows.Threading.DispatcherTimer _createTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _currentIconName;

    /// <param name="restartFor">Called when the user picks "Restart for &lt;mode&gt;".</param>
    public TrayIconService(DockSenseService service, Action openWindow, Action<DockMode> restartFor, Action exit)
    {
        _service = service;
        _openItem.Click += (_, _) => openWindow();
        _pauseItem.Click += (_, _) => _service.SetPaused(_pauseItem.IsChecked);
        _recheckItem.Click += (_, _) => _service.ScheduleRescan("requested from the tray");
        _exitItem.Click += (_, _) => exit();
        foreach (var (mode, item) in _restartItems)
        {
            item.Click += (_, _) => restartFor(mode);
        }

        var menu = new ContextMenu();
        Control[] items = [_modeItem, new Separator(), _openItem, _pauseItem, _recheckItem, _restartSeparator, .. _restartItems.Values, new Separator(), _exitItem];
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        _icon = new TaskbarIcon
        {
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => openWindow();
        _createTimer.Tick += (_, _) => CreateIcon();
        CreateIcon();

        _service.StatusChanged += (_, _) => Update();
        Loc.Instance.PropertyChanged += (_, _) => Update();
        Update();
    }

    public void Dispose()
    {
        _createTimer.Stop();
        _icon.Dispose();
    }

    /// <summary>
    /// Adds the icon to the notification area. Started by the sign-in task, DockSense is up before the shell, and
    /// Windows refuses the icon until the taskbar exists: keep trying until it does. The icon and tooltip set
    /// meanwhile are kept and used when it is created.
    /// </summary>
    private void CreateIcon()
    {
        try
        {
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (InvalidOperationException)
        {
            if (!_createTimer.IsEnabled)
            {
                Log.Info("The notification area is not there yet; the tray icon follows once the shell has started.");
                _createTimer.Start();
            }

            return;
        }

        if (_createTimer.IsEnabled)
        {
            _createTimer.Stop();
            Log.Info("Tray icon created.");
        }
    }

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

        var restartOptions = _service.IgpuRestartOptions;
        foreach (var (itemMode, item) in _restartItems)
        {
            var option = restartOptions.FirstOrDefault(o => o.Mode == itemMode);
            item.Visibility = option is null ? Visibility.Collapsed : Visibility.Visible;
            if (option is not null)
            {
                item.Header = Loc.F("Tray_RestartFor", DockSenseService.ModeName(itemMode), DockSenseService.FormatMemory(option.MemoryMb));
            }
        }

        _restartSeparator.Visibility = restartOptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
