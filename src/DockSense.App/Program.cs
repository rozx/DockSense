using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // DockSense.exe --probe [report.txt]: write a read-only diagnostic report and exit.
        if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            var report = ProbeReport.Build(new ConfigStore(ConfigStore.DefaultPath).Load());
            var output = args.Length > 1 ? args[1] : Path.Combine(Log.DataDirectory, "probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, report);
            return 0;
        }

        // The two halves of an update (see UpdateInstaller). Neither shows any UI.
        if (args.Length > 0 && args[0].Equals(UpdateInstaller.InstallArgument, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateInstaller.InstallFromCommandLine(args);
        }

        if (args.Length > 0 && args[0].Equals(UpdateInstaller.ApplyArgument, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateInstaller.ApplyFromCommandLine(args);
        }

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\DockSense", out var isFirstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DockSense.Show");
        if (!isFirstInstance && args.Contains(UpdateInstaller.AfterUpdateArgument, StringComparer.OrdinalIgnoreCase))
        {
            // Started by the installer right after the previous version exited; it may not be quite gone yet.
            isFirstInstance = WaitForPreviousInstance(singleInstance);
        }

        var startedAtSignIn = args.Contains(AutoStart.Argument, StringComparer.OrdinalIgnoreCase);
        if (!isFirstInstance)
        {
            // Already running: ask that instance to show its window. Not at sign-in, though: the sign-in task and
            // the Run entry both start DockSense then, and the later of the two must not open the window.
            if (!startedAtSignIn)
            {
                showSignal.Set();
            }

            return 0;
        }

        // Started by the sign-in task: only when Windows would start DockSense from its Run entry as well.
        if (args.Contains(AutoStart.TaskArgument, StringComparer.OrdinalIgnoreCase) && !AutoStart.AllowsLogonTaskStart())
        {
            return 0;
        }

        if (startedAtSignIn && !args.Contains(App.PausedArgument, StringComparer.OrdinalIgnoreCase))
        {
            // Before anything else: this races the shell (see SignInFastPath).
            SignInFastPath.Run();
        }

        // Automatic updates go in here, before anything is shown: the installer takes over once this process exits.
        if (UpdateInstaller.TryInstallAtStartup(args))
        {
            return 0;
        }

        var app = new App(showSignal);
        app.InitializeComponent();
        return app.Run();
    }

    private static bool WaitForPreviousInstance(Mutex singleInstance)
    {
        try
        {
            return singleInstance.WaitOne(TimeSpan.FromSeconds(20));
        }
        catch (AbandonedMutexException)
        {
            return true; // The previous instance exited without releasing it: the mutex is ours now.
        }
    }
}
