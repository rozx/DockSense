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

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\DockSense", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);

        using var service = new DockSenseService(new ConfigStore(ConfigStore.DefaultPath));
        var tray = new TrayApplicationContext(service);
        service.Start();
        Application.Run(tray);
        return 0;
    }
}
