using System.Globalization;
using System.Text;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

/// <summary>
/// Read-only diagnostic report (<c>DockSense.exe --probe [file]</c>, About &gt; Copy diagnostic report). Changes
/// nothing on the system. People paste it into public issues, so it holds no user or computer name, no system
/// serial number and nothing about the programs a profile starts but their number; the user's profile folder is
/// written as %USERPROFILE% wherever a path contains it. Device instance IDs stay: detection is decided on them.
/// </summary>
internal static class ProbeReport
{
    public static string Build(DockSenseConfig config)
    {
        var text = new StringBuilder();
        text.AppendLine($"DockSense {typeof(ProbeReport).Assembly.GetName().Version} probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Windows {Environment.OSVersion.Version} ({Try(HardwareDiagnostics.WindowsRelease)})");

        // Each part on its own: a report with one part missing still helps, and new hardware is exactly where a
        // part may fail.
        Section(text, "Device:", () =>
        {
            foreach (var (label, value) in HardwareDiagnostics.DeviceFacts())
            {
                Line(text, 2, label, value);
            }
        });

        IReadOnlyList<GpuDetails> adapters = [];
        Section(text, "Display adapters:", () =>
        {
            adapters = GpuProbe.GetAllAdapterDetails();
            foreach (var gpu in adapters)
            {
                AppendAdapter(text, gpu);
            }
        });

        Section(text, "Active displays:", () =>
        {
            foreach (var display in DisplayProbe.GetActiveDisplayDetails(withAdapters: true))
            {
                text.AppendLine($"  {(string.IsNullOrEmpty(display.Name) ? "(no name)" : display.Name)}: {display.Kind} (output technology 0x{display.OutputTechnology:X})");
                text.AppendLine($"    {display.Id}");
                var mode = display.Width == 0 ? "?" : string.Create(CultureInfo.InvariantCulture, $"{display.Width} x {display.Height}, {display.RefreshHz:0.##} Hz");
                Line(text, 4, "mode", mode);
                var adapter = adapters.FirstOrDefault(a => a.InstanceId.Equals(display.AdapterInstanceId, StringComparison.OrdinalIgnoreCase));
                Line(text, 4, "driven by", adapter?.Name ?? (display.AdapterInstanceId.Length == 0 ? "?" : display.AdapterInstanceId));
            }
        });

        Section(text, null, () =>
        {
            var snapshot = DeviceProbe.TakeSnapshot();
            Line(text, 0, "AC power", snapshot.OnAcPower);
            Line(text, 0, "Resolved mode", ModeResolver.Resolve(snapshot, config.Detection));
            Line(text, 0, "eGPU", ModeResolver.FindEgpu(snapshot, config.Detection)?.Name ?? "-");
        });

        Section(text, null, () =>
        {
            Line(text, 0, "Active power plan", PowerPlans.Describe(PowerPlans.GetActive()));
            Line(text, 0, "Power mode", HardwareDiagnostics.PowerMode());
            Line(text, 0, "DockSense plan", config.DockSensePerformancePlanId is { } id ? PowerPlans.Describe(id) : "not created yet");
            text.AppendLine("All power plans:");
            foreach (var plan in PowerPlans.ListAll())
            {
                var type = plan.Personality switch { 0 => "Power saver", 1 => "High performance", 2 => "Balanced", _ => "unknown" };
                text.AppendLine($"  {plan.Name} {{{plan.Id}}} ({type} type)");
            }
        });

        Section(text, null, () =>
        {
            Line(text, 0, "FSE available", FseSettings.IsAvailable);
            Line(text, 0, "FSE at sign-in", FseSettings.StartupToFse?.ToString() ?? "-");
            Line(text, 0, "FSE home app", FseSettings.HomeApp ?? "-");
            Line(text, 0, "FSE live API", $"supported {FullScreenExperience.IsSupported}, active {FullScreenExperience.IsActive?.ToString() ?? "-"}, can switch {FullScreenExperience.CanSet}");
            Line(text, 0, "FSE enter restarts", FseSettings.RestartsOnEnter);
            const string gaming = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
            Line(text, 0, "FSE prompts", Joined(HardwareDiagnostics.UserRegistryValues($@"{gaming}\SystemDialogResults")));
            Line(text, 0, "FSE startup apps", Joined(HardwareDiagnostics.UserRegistryValues($@"{gaming}\Startup\Run")));
        });

        Section(text, null, () => AppendIgpuMemory(text));

        Section(text, null, () => AppendTdp(text));

        Section(text, "Vendor firmware interfaces (ACPI devices):", () =>
        {
            foreach (var (id, name) in HardwareDiagnostics.VendorAcpiDevices())
            {
                text.AppendLine($"  {id,-22} {name}");
            }
        });

        Section(text, "Vendor services (how each starts):", () => AppendList(text, HardwareDiagnostics.VendorServices()));
        Section(text, "Vendor programs running:", () => text.AppendLine($"  {Joined(HardwareDiagnostics.VendorProcesses())}"));

        Section(text, null, () =>
        {
            var startup = AutoStart.Status;
            Line(text, 0, "Start with Windows", startup.State switch
            {
                StartupState.On => startup.WaitsForDesktop ? "on, but in Xbox mode only after the first switch to the desktop" : "on",
                StartupState.DisabledByWindows => "entry exists, switched off in Windows (Task Manager / Settings > Apps > Startup)",
                StartupState.OtherCopy => $"another copy: {startup.OtherCommand}",
                _ => "off",
            });
            Line(text, 0, "Sign-in task", $"{AutoStart.DescribeLogonTask()}{(AutoStart.StartsLate(startup) ? " (DockSense starts late, from the Run key)" : string.Empty)}");
            Line(text, 0, "Signed in", SignInSession.SignInTimeUtc is { } signIn ? signIn.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "unknown");
            Line(text, 0, "Updates", $"{config.Updates}; install folder writable without admin: {UpdateInstaller.CanWriteInstallFolder()}; self-updating build: {UpdateInstaller.CanSelfUpdate}");
        });

        Section(text, "DockSense settings:", () => AppendSettings(text, config));

        Section(text, null, () =>
        {
            var gameMode = GameModeDiagnostics.Read();
            Line(text, 0, "Game Mode enabled", gameMode.GameModeEnabled);
            Line(text, 0, "Active plan type", gameMode.ActivePlanPersonality switch { 0 => "Power saver", 1 => "High performance", 2 => "Balanced", _ => "unknown" });
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
        });

        // A copy of DockSense in the user's folders shows up in the startup lines with its path.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 3
            ? text.ToString().Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            : text.ToString();
    }

    private static void AppendAdapter(StringBuilder text, GpuDetails gpu)
    {
        text.AppendLine($"  {gpu.Name}");
        Line(text, 4, "instance", gpu.InstanceId);
        if (HardwareDiagnostics.DescribeAdapter(gpu.InstanceId) is { } facts)
        {
            var date = facts.DriverDate is { } released ? released.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "?";
            Line(text, 4, "driver", $"{facts.DriverVersion ?? "?"} ({date}), {facts.DriverProvider ?? "?"}");
            Line(text, 4, "dedicated memory", facts.DedicatedMemoryBytes is { } bytes ? $"{bytes / 1048576} MB" : "?");
            Line(text, 4, "location", facts.Location ?? "-");
            foreach (var path in facts.LocationPaths)
            {
                Line(text, 4, "location path", path);
            }

            Line(text, 4, "bus description", facts.BusDescription ?? "-");
            Line(text, 4, "capabilities", facts.Capabilities is { } capabilities ? $"0x{capabilities:X}" : "-");
            text.AppendLine("    connected through (nearest first; removal policy / in local container / capabilities):");
            foreach (var ancestor in facts.Ancestors)
            {
                var flags = ancestor.Capabilities is { } value ? $"0x{value:X}" : "-";
                text.AppendLine($"      {ancestor.Name} [{ancestor.InstanceId}]: {ancestor.RemovalPolicy?.ToString(CultureInfo.InvariantCulture) ?? "-"} / {ancestor.InLocalMachineContainer?.ToString() ?? "-"} / {flags}");
            }
        }

        Line(text, 4, "removal policy", gpu.RemovalPolicy?.ToString(CultureInfo.InvariantCulture) ?? "-");
        Line(text, 4, "in local container", gpu.InLocalMachineContainer?.ToString() ?? "-");
        Line(text, 4, "problem code", gpu.ProblemCode);
        Line(text, 4, "=> external GPU", gpu.ToGpuInfo().IsExternal);
    }

    private static void AppendIgpuMemory(StringBuilder text)
    {
        var reply = AsusAcpi.QueryIgpuMemory(out var why);
        if (reply is not null && IgpuMemoryOptions.Parse(reply) is { } igpu)
        {
            Line(text, 0, "iGPU memory", $"{igpu.ToMb(igpu.CurrentRaw)} MB stored in the firmware (raw 0x{igpu.CurrentRaw:X})");
            Line(text, 0, "iGPU memory options", string.Join(", ", igpu.SizesMb.Select(mb => mb == 0 ? "Auto" : $"{mb} MB")));
        }
        else
        {
            Line(text, 0, "iGPU memory", $"not adjustable on this device ({(reply is null ? why : "the firmware's reply is not one DockSense knows")})");
        }

        if (reply is not null)
        {
            Line(text, 0, "iGPU memory reply", HardwareDiagnostics.Hex(reply.AsSpan(0, 48)));
        }

        // What DockSense falls back on where the ASUS interface is missing (experimental). Reading changes nothing.
        var adlx = AmdAdlx.ReadVariableGraphicsMemory(out var adlxNote);
        var viaAsus = reply is not null && IgpuMemoryOptions.Parse(reply) is not null;
        var viaAmdDriver = !viaAsus && adlx?.ToIgpuMemoryOptions() is not null;
        Line(text, 0, "iGPU memory set by", viaAsus ? "the ASUS firmware interface" : viaAmdDriver ? "the AMD driver (experimental)" : "nothing");
        if (adlx is null)
        {
            Line(text, 0, "AMD graphics memory", $"not read ({adlxNote})");
        }
        else if (!adlx.Supported)
        {
            Line(text, 0, "AMD graphics memory", $"not offered by the driver (ADLX {adlx.AdlxVersion})");
        }
        else
        {
            Line(text, 0, "AMD graphics memory", $"Variable Graphics Memory, ADLX {adlx.AdlxVersion}");
            Line(text, 2, "current", adlx.Current?.ToString() ?? "-");
            Line(text, 2, "default", adlx.Default?.ToString() ?? "-");
            Line(text, 2, "options", adlx.Options.Count);
            foreach (var option in adlx.Options)
            {
                text.AppendLine($"    {option}");
            }
        }
    }

    /// <summary>The settings that decide what DockSense does, without the programs' paths and arguments.</summary>
    private static void AppendSettings(StringBuilder text, DockSenseConfig config)
    {
        foreach (var mode in Enum.GetValues<DockMode>())
        {
            var profile = config.ProfileFor(mode);
            var plan = profile.PowerPlan.Kind == PowerPlanKind.Specific ? $"{{{profile.PowerPlan.PlanId}}}" : profile.PowerPlan.Kind.ToString();
            var memory = profile.IgpuMemoryMb is { } megabytes
                ? $"{(megabytes == 0 ? "Auto" : $"{megabytes} MB")} ({profile.IgpuMemoryApply})"
                : "unchanged";
            Line(text, 2, mode.ToString(), $"plan {plan}; FSE {profile.Fse}, switch immediately {profile.FseApplyImmediately}; iGPU memory {memory}; TDP {(profile.TdpWatts is { } watts ? $"{watts} W" : "unchanged")}; {profile.Programs.Count} program(s)");
        }

        var detection = config.Detection;
        Line(text, 2, "detection", string.Create(CultureInfo.InvariantCulture, $"settle {detection.SettleSeconds} s; docked needs AC power {detection.DockedRequiresAcPower}"));
        Line(text, 2, "always eGPU", Joined(detection.ForceEgpuInstanceIds));
        Line(text, 2, "never eGPU", Joined(detection.IgnoreGpuInstanceIds));
        Line(text, 2, "re-assert plan", config.ReassertPowerPlan);
    }

    private static void AppendList(StringBuilder text, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            text.AppendLine("  none");
        }

        foreach (var item in items)
        {
            text.AppendLine($"  {item}");
        }
    }

    /// <summary>"label:" padded to the report's value column, then the value.</summary>
    private static void Line(StringBuilder text, int indent, string label, object? value) =>
        text.Append(' ', indent).Append($"{label}:".PadRight(Math.Max(21, label.Length + 2))).AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture));

    private static string Joined(IEnumerable<string> items) => string.Join(", ", items) is { Length: > 0 } joined ? joined : "none";

    private static void Section(StringBuilder text, string? title, Action write)
    {
        text.AppendLine();
        if (title is not null)
        {
            text.AppendLine(title);
        }

        try
        {
            write();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            text.AppendLine($"  (this part could not be read: {ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string Try(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"? {ex.GetType().Name}";
        }
    }

    /// <summary>What the TDP override would work with here. Reads only; nothing is sent to the driver.</summary>
    private static void AppendTdp(StringBuilder text)
    {
        // The board is in the device facts above; this is the processor as the SMU commands are chosen by it.
        Line(text, 0, "Processor", $"{TdpWriter.DescribeProcessor()}{(TdpWriter.Commands is null ? " (no SMU commands known)" : string.Empty)}");
        Line(text, 0, "TDP limits", TdpWriter.Device is { } device
            ? $"{device.Board}: {device.MinWatts} W to {device.PluggedIn.Max} W plugged in / {device.OnBattery.Max} W on battery; never more than {device.PluggedIn.Ceiling} W / {device.OnBattery.Ceiling} W"
            : $"not known for this board ({TdpWriter.BoardName ?? "-"})");
        Line(text, 0, "PawnIO driver", PawnIo.IsInstalled ? $"installed{(PawnIo.InstalledVersion is { } version ? $" ({version})" : string.Empty)}" : "not installed");
        Line(text, 0, "TDP helper task", $"{TdpHelper.DescribeTask()}; install folder protected: {TdpHelper.InstallFolderIsProtected}");
        Line(text, 0, "Armoury Crate mode", $"{ArmouryCrate.SelectedMode(DeviceProbe.OnAcPower)?.ToString() ?? "-"} (for the current power source)");
        Line(text, 0, "TDP set by DockSense", StateStore.LoadTdp() is { } state ? $"{state.Watts} W" : "none");
    }
}
