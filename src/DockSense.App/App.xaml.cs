using System.Windows;
using DockSense.App.Localization;
using DockSense.App.Services;
using DockSense.App.ViewModels;
using DockSense.App.Views;
using DockSense.Core;

namespace DockSense.App;

public partial class App : Application
{
    private readonly EventWaitHandle? _showSignal;
    private TrayIconService? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public App()
    {
    }

    internal App(EventWaitHandle showSignal)
    {
        _showSignal = showSignal;
    }

    public static DockSenseService Service { get; private set; } = null!;

    public static MainViewModel ViewModel { get; private set; } = null!;

    public static void ApplyTheme(AppTheme theme) => Appearance.Apply(theme);

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(ViewModel);
            _window.Closing += (_, e) =>
            {
                if (!_exiting)
                {
                    // Closing the window keeps DockSense running in the tray.
                    e.Cancel = true;
                    _window.Hide();
                }
            };
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Show();
        _window.Activate();
    }

    public void Quit()
    {
        _exiting = true;
        _window?.Close();
        _tray?.Dispose();
        Service.Dispose();
        Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        var store = new ConfigStore(ConfigStore.DefaultPath);
        var ui = store.Load(Log.Warn).Ui;
        Loc.Instance.SetLanguage(ui.Language);
        ApplyTheme(ui.Theme);

        Service = new DockSenseService(store);

        // Subscribe before starting: the startup apply can already store a new iGPU memory size or hit a problem.
        Toasts.Initialize(() => Dispatcher.BeginInvoke(ShowMainWindow));
        Service.ModeApplied += applied =>
        {
            if (!applied.IsStartup && Service.Config.Ui.NotifyOnModeChange)
            {
                Toasts.ShowModeApplied(applied);
            }
        };
        Service.ProgramStarted += started =>
        {
            if (Service.Config.Ui.NotifyOnProgramStart)
            {
                Toasts.ShowProgramStarted(started);
            }
        };
        Service.IgpuMemoryChanged += megabytes =>
        {
            if (Service.Config.Ui.NotifyOnModeChange)
            {
                Toasts.ShowRestartNeeded(megabytes);
            }
        };
        Service.Warning += message =>
        {
            if (Service.Config.Ui.NotifyOnWarnings)
            {
                Toasts.ShowWarning(message);
            }
        };

        Service.Start();
        ViewModel = new MainViewModel(Service);
        _tray = new TrayIconService(Service, ShowMainWindow, Quit);

        // A second launch of DockSense.exe signals this instance to show its window.
        if (_showSignal is not null)
        {
            ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        if (Service.IsFirstRun || e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
        {
            ShowMainWindow();
        }
    }

}
