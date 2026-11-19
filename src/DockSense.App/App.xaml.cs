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

    /// <summary>Start with automatic switching paused (passed on when DockSense restarts itself while paused).</summary>
    internal const string PausedArgument = "--paused";

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

    /// <summary>The tray's "Restart for &lt;mode&gt;": the confirmation is a dialog in the main window.</summary>
    private void RestartFor(DockMode mode)
    {
        ShowMainWindow();
        ViewModel.RestartForCommand.Execute(mode);
    }

    /// <summary>The "Restart now" button of the iGPU memory notification.</summary>
    private void RestartFromToast()
    {
        if (Service.IgpuMemoryPendingMb is null)
        {
            // An old notification: the PC has restarted since, or the device went back to the mode it started in.
            // Restarting now would change nothing, so only show where things stand.
            ShowMainWindow();
            return;
        }

        SystemPower.Restart();
    }

    /// <summary>The restart notification promises a change that a restart no longer brings: take it away.</summary>
    private static void ClearStaleRestartToast()
    {
        if (Service.IgpuMemoryPendingMb is null)
        {
            Toasts.ClearRestartNeeded();
        }
    }

    /// <summary>The mode from <c>--restarted=&lt;mode&gt;</c>: the profile the previous instance had applied, if any.</summary>
    private static DockMode? ResumedMode(string[] args)
    {
        var prefix = UpdateInstaller.RestartedArgument + "=";
        var value = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
        return Enum.TryParse<DockMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : null;
    }

    /// <summary>
    /// The arguments that bring DockSense back the way it is now, for the restart after an update: with the mode
    /// whose profile is applied (so nothing is applied again), with the window open if it is open now, and paused
    /// if switching is paused.
    /// </summary>
    private IReadOnlyList<string> RelaunchArguments()
    {
        // The last applied mode, not the detected one: while paused the two can differ, and the transition between
        // them (leave and enter programs, the full screen switch) must still happen when switching is resumed.
        List<string> args = [];
        if (Service.LastAppliedMode is { } applied)
        {
            args.Add($"{UpdateInstaller.RestartedArgument}={applied}");
        }
        if (_window?.IsVisible == true)
        {
            args.Add(ShowArgument);
        }

        if (Service.Paused)
        {
            args.Add(PausedArgument);
        }

        return args;
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

        // Before the service reads the full screen experience settings.
        AutoStart.RemoveMistakenXboxModeKey();
        Service = new DockSenseService(store);

        // Subscribe before starting: the startup apply can already store a new iGPU memory size or hit a problem.
        Toasts.Initialize(onOpen: () => Dispatcher.BeginInvoke(ShowMainWindow), onRestart: () => Dispatcher.BeginInvoke(RestartFromToast));
        var showWindowAtStart = e.Args.Contains(ShowArgument, StringComparer.OrdinalIgnoreCase);
        var startedWithWindows = e.Args.Contains(AutoStart.Argument, StringComparer.OrdinalIgnoreCase);
        var resumeFrom = ResumedMode(e.Args);
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
        Service.StatusChanged += (_, _) => ClearStaleRestartToast();

        Service.Start(resumeFrom, paused: e.Args.Contains(PausedArgument, StringComparer.OrdinalIgnoreCase));
        ClearStaleRestartToast();
        AutoStart.UpgradeLegacyEntry();
        AutoStart.EnsureStartsInXboxMode();
        AutoStart.EnsureLogonTask();

        Updates = new UpdateService(Service, RelaunchArguments, Quit);
        Updates.UpdateFound += offer => Toasts.ShowUpdateAvailable(ReleaseVersion.Format(offer.Version), Updates.InstallsWithoutAsking);
        Updates.Start(installFailed: e.Args.Contains(UpdateInstaller.FailedArgument, StringComparer.OrdinalIgnoreCase));

        ViewModel = new MainViewModel(Service, Updates);
        _tray = new TrayIconService(Service, ShowMainWindow, RestartFor, Quit);

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
