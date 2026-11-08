using System.Diagnostics;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

/// <summary>Owns the tray icon and the app lifetime. No main window.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ProjectUrl = "https://github.com/rozx/DockSense";

    private readonly DockSenseService _service;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _modeItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _planItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _fseItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("Pause automatic switching") { CheckOnClick = true };
    private readonly ToolStripMenuItem _autoStartItem = new("Start with Windows") { CheckOnClick = true };
    private readonly Dictionary<DockMode, Icon> _icons = new()
    {
        [DockMode.Handheld] = TrayIcons.Create("H", Color.FromArgb(0x2E, 0x7D, 0x32)),
        [DockMode.Docked] = TrayIcons.Create("D", Color.FromArgb(0x15, 0x65, 0xC0)),
        [DockMode.Egpu] = TrayIcons.Create("E", Color.FromArgb(0xC6, 0x28, 0x28)),
    };

    private readonly Icon _pausedIcon = TrayIcons.Create("II", Color.FromArgb(0x61, 0x61, 0x61));

    public TrayApplicationContext(DockSenseService service)
    {
        _service = service;

        _pauseItem.CheckedChanged += (_, _) => _service.SetPaused(_pauseItem.Checked);
        _autoStartItem.Checked = AutoStart.IsEnabled;
        _autoStartItem.CheckedChanged += (_, _) => AutoStart.SetEnabled(_autoStartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _modeItem,
            _planItem,
            _fseItem,
            new ToolStripSeparator(),
            _pauseItem,
            new ToolStripMenuItem("Re-check now", null, (_, _) => _service.ScheduleRescan("requested from tray")),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Edit settings (config.json)", null, (_, _) => Open(_service.EnsureConfigFile())),
            new ToolStripMenuItem("Reload settings", null, (_, _) => _service.ReloadConfig()),
            new ToolStripMenuItem("Open log", null, (_, _) => Open(Log.FilePath)),
            new ToolStripSeparator(),
            _autoStartItem,
            new ToolStripMenuItem("About DockSense", null, (_, _) => Open(ProjectUrl)),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);

        _trayIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _service.StatusChanged += (_, _) => UpdateStatus();
        _service.Notice += message => _trayIcon.ShowBalloonTip(10_000, "DockSense", message, ToolTipIcon.Warning);
        UpdateStatus();
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _pausedIcon.Dispose();
        base.ExitThreadCore();
    }

    private void UpdateStatus()
    {
        var mode = _service.Mode;
        var egpu = _service.Snapshot is { } snapshot ? ModeResolver.FindEgpu(snapshot, _service.Config.Detection) : null;
        var modeText = mode switch
        {
            DockMode.Egpu => $"Mode: eGPU ({egpu?.Name ?? "external GPU"})",
            DockMode.Docked => "Mode: Docked (external display)",
            _ => "Mode: Handheld",
        };

        string planName;
        try
        {
            planName = PowerPlans.GetName(PowerPlans.GetActive()) ?? "unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            planName = "unknown";
        }

        _modeItem.Text = modeText + (_service.Paused ? " - paused" : string.Empty);
        _planItem.Text = $"Power plan: {planName}";
        _fseItem.Text = $"Full Screen Experience at sign-in: {FseSettings.StartupToFse switch { true => "on", false => "off", null => "n/a" }}";
        _trayIcon.Icon = _service.Paused ? _pausedIcon : _icons[mode];

        var tooltip = $"DockSense: {mode}{(_service.Paused ? " (paused)" : string.Empty)} - {planName}";
        _trayIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }
}
