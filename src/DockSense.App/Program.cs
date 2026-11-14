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

        // DockSense.exe --apply-update <new exe> <sha256>: the elevated step of an update in a protected folder.
        if (args.Length > 0 && args[0].Equals(UpdateInstaller.ApplyArgument, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateInstaller.ApplyFromCommandLine(args);
        }

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\DockSense", out var isFirstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DockSense.Show");
        if (!isFirstInstance && args.Contains(UpdateInstaller.AfterUpdateArgument, StringComparer.OrdinalIgnoreCase))
        {
            // Started by the version this one replaces, which is on its way out.
            isFirstInstance = WaitForPreviousInstance(singleInstance);
        }

        if (!isFirstInstance)
        {
            // Already running: ask that instance to show its window.
            showSignal.Set();
            return 0;
        }

        // Automatic updates go in here, before anything is shown; the new version then takes over.
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
