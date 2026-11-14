using System.Windows;
using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.App.Services;
using DockSense.App.ViewModels;
using DockSense.App.Views;
using DockSense.Core;

namespace DockSense.App;

public partial class App : Application
{
    private const string ShowArgument = "--show";

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

    public static UpdateService Updates { get; private set; } = null!;

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
        Updates.Dispose();
        Service.Dispose();
        Shutdown();
    }

    /// <summary>After an update was installed: start the new version and leave. It comes back the way this one looks.</summary>
    private void RestartForUpdate()
    {
        List<string> args = [UpdateInstaller.AfterUpdateArgument, UpdateInstaller.RestartedArgument];
        if (_window?.IsVisible == true)
        {
            args.Add(ShowArgument);
        }

        // If the new version cannot be started, this one keeps running; the update takes effect at the next start.
        if (UpdateInstaller.Relaunch(args))
        {
            Quit();
        }
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
        var showWindowAtStart = e.Args.Contains(ShowArgument, StringComparer.OrdinalIgnoreCase);
        var startedWithWindows = e.Args.Contains(AutoStart.Argument, StringComparer.OrdinalIgnoreCase);
        var restarted = e.Args.Contains(UpdateInstaller.RestartedArgument, StringComparer.OrdinalIgnoreCase);
        Service.ModeApplied += applied =>
        {
            // Started by hand, DockSense goes straight to the tray: say that it is running in the background.
            // Not at sign-in (Start with Windows), and not when the window opens anyway.
            var notify = applied.IsStartup
                ? Service.Config.Ui.NotifyOnStartup && !startedWithWindows && !Service.IsFirstRun && !showWindowAtStart
                : Service.Config.Ui.NotifyOnModeChange;
            if (notify)
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

        Service.Start(restarted);
        AutoStart.UpgradeLegacyEntry();
        AutoStart.EnsureStartsInXboxMode();

        Updates = new UpdateService(Service, RestartForUpdate);
        Updates.UpdateFound += offer => Toasts.ShowUpdateAvailable(ReleaseVersion.Format(offer.Version), Updates.InstallsAtNextStart);
        Updates.Start();

        ViewModel = new MainViewModel(Service, Updates);
        _tray = new TrayIconService(Service, ShowMainWindow, Quit);

        // A second launch of DockSense.exe signals this instance to show its window.
        if (_showSignal is not null)
        {
            ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        if (Service.IsFirstRun || showWindowAtStart)
        {
            ShowMainWindow();
        }
    }

}
