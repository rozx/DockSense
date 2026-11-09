using System.Text;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

/// <summary>Read-only diagnostic report (<c>DockSense.exe --probe [file]</c>). Changes nothing on the system.</summary>
internal static class ProbeReport
{
    public static string Build(DockSenseConfig config)
    {
        var text = new StringBuilder();
        text.AppendLine($"DockSense {typeof(ProbeReport).Assembly.GetName().Version} probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Windows {Environment.OSVersion.Version}");
        text.AppendLine();

        text.AppendLine("Display adapters:");
        foreach (var gpu in GpuProbe.GetAllAdapterDetails())
        {
            var info = gpu.ToGpuInfo();
            text.AppendLine($"  {gpu.Name}");
            text.AppendLine($"    instance:            {gpu.InstanceId}");
            text.AppendLine($"    removal policy:      {gpu.RemovalPolicy?.ToString() ?? "-"}");
            text.AppendLine($"    in local container:  {gpu.InLocalMachineContainer?.ToString() ?? "-"}");
            text.AppendLine($"    problem code:        {gpu.ProblemCode}");
            text.AppendLine($"    => external GPU:     {info.IsExternal}");
        }

        text.AppendLine();
        text.AppendLine("Active displays:");
        foreach (var display in DisplayProbe.GetActiveDisplayDetails())
        {
            text.AppendLine($"  {(string.IsNullOrEmpty(display.Name) ? "(no name)" : display.Name)}: {display.Kind} (output technology 0x{display.OutputTechnology:X})");
            text.AppendLine($"    {display.Id}");
        }

        var snapshot = new DeviceSnapshot(
            GpuProbe.GetWorkingAdapters(),
            DisplayProbe.GetActiveDisplays(),
            System.Windows.SystemParameters.PowerLineStatus == System.Windows.PowerLineStatus.Online);

        text.AppendLine();
        text.AppendLine($"AC power:            {snapshot.OnAcPower}");
        text.AppendLine($"Resolved mode:       {ModeResolver.Resolve(snapshot, config.Detection)}");
        text.AppendLine($"eGPU:                {ModeResolver.FindEgpu(snapshot, config.Detection)?.Name ?? "-"}");

        text.AppendLine();
        text.AppendLine($"Active power plan:   {PowerPlans.Describe(PowerPlans.GetActive())}");
        var dockSensePlan = config.DockSensePerformancePlanId;
        text.AppendLine($"DockSense plan:      {(dockSensePlan is { } id ? PowerPlans.Describe(id) : "not created yet")}");
        text.AppendLine("All power plans:");
        foreach (var plan in PowerPlans.ListAll())
        {
            var type = plan.Personality switch { 0 => "Power saver", 1 => "High performance", 2 => "Balanced", _ => "unknown" };
            text.AppendLine($"  {plan.Name} {{{plan.Id}}} ({type} type)");
        }

        text.AppendLine();
        text.AppendLine($"FSE available:       {FseSettings.IsAvailable}");
        text.AppendLine($"FSE at sign-in:      {FseSettings.StartupToFse?.ToString() ?? "-"}");
        text.AppendLine($"FSE home app:        {FseSettings.HomeApp ?? "-"}");
        text.AppendLine($"FSE live API:        supported {FullScreenExperience.IsSupported}, active {FullScreenExperience.IsActive?.ToString() ?? "-"}, can switch {FullScreenExperience.CanSet}");
        text.AppendLine($"FSE enter restarts:  {FseSettings.RestartsOnEnter}");

        text.AppendLine();
        if (AsusAcpi.ReadIgpuMemory() is { } igpu)
        {
            text.AppendLine($"iGPU memory:         {igpu.ToMb(igpu.CurrentRaw)} MB stored in the firmware (raw 0x{igpu.CurrentRaw:X})");
            text.AppendLine($"iGPU memory options: {string.Join(", ", igpu.SizesMb.Select(mb => mb == 0 ? "Auto" : $"{mb} MB"))}");
        }
        else
        {
            text.AppendLine("iGPU memory:         not adjustable on this device");
        }

        var gameMode = GameModeDiagnostics.Read();
        text.AppendLine();
        text.AppendLine($"Game Mode enabled:   {gameMode.GameModeEnabled}");
        text.AppendLine($"Active plan type:    {gameMode.ActivePlanPersonality switch { 0 => "Power saver", 1 => "High performance", 2 => "Balanced", _ => "unknown" }}");
        text.AppendLine("GameMode profile overrides (provisioned, AC):");
        if (gameMode.Overrides.Count == 0)
        {
            text.AppendLine("  none");
        }

        foreach (var entry in gameMode.Overrides)
        {
            text.AppendLine($"  {entry.Setting} [{entry.Scheme}] = {entry.AcValue?.ToString() ?? "-"}");
        }

        text.AppendLine($"=> In-game CPU throttle with the current plan: {(gameMode.ThrottlesActivePlan ? "YES" : "no")}");
        return text.ToString();
    }
}
