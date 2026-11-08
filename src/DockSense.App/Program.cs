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
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DockSense.Show");
        if (!isFirstInstance)
        {
            // Already running: ask that instance to show its window.
            showSignal.Set();
            return 0;
        }

        var app = new App(showSignal);
        app.InitializeComponent();
        return app.Run();
    }
}
