using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class IgpuRestartPlannerTests
{
    /// <summary>512 MB units: Auto, 0.5 GB, 1 GB, 4 GB and 8 GB.</summary>
    private static IgpuMemoryOptions Firmware(uint currentRaw) => new(512, [0, 1, 2, 8, 16], currentRaw);

    private static DockSenseConfig Config(int? handheld, int? docked, int? egpu) => new()
    {
        Handheld = new ModeProfile { IgpuMemoryMb = handheld },
        Docked = new ModeProfile { IgpuMemoryMb = docked },
        Egpu = new ModeProfile { IgpuMemoryMb = egpu },
    };

    [Fact]
    public void Offers_the_other_modes_whose_size_differs_from_the_one_in_effect()
    {
        var config = Config(handheld: 8192, docked: 4096, egpu: 512);

        var options = IgpuRestartPlanner.Options(config, DockMode.Egpu, Firmware(currentRaw: 1), effectiveRaw: 1);

        Assert.Equal(
            [new IgpuRestartOption(DockMode.Handheld, 8192, 16), new IgpuRestartOption(DockMode.Docked, 4096, 8)],
            options);
    }

    [Fact]
    public void Never_offers_the_current_mode()
    {
        // The eGPU profile's 512 MB is stored but 8 GB is still in effect: that is the ordinary pending restart.
        var config = Config(handheld: 8192, docked: null, egpu: 512);

        var options = IgpuRestartPlanner.Options(config, DockMode.Egpu, Firmware(currentRaw: 1), effectiveRaw: 16);

        Assert.Empty(options);
    }

    [Fact]
    public void Skips_a_mode_whose_size_is_already_in_effect()
    {
        // Booted as a handheld (8 GB), then docked without restarting: going back needs no restart.
        var config = Config(handheld: 8192, docked: 8192, egpu: 512);

        Assert.Empty(IgpuRestartPlanner.Options(config, DockMode.Egpu, Firmware(currentRaw: 1), effectiveRaw: 16));
    }

    [Fact]
    public void Skips_modes_that_leave_the_memory_alone_or_ask_for_a_size_the_firmware_lacks()
    {
        var config = Config(handheld: null, docked: 1536, egpu: 512);

        Assert.Empty(IgpuRestartPlanner.Options(config, DockMode.Egpu, Firmware(currentRaw: 1), effectiveRaw: 1));
    }

    [Fact]
    public void Auto_is_a_size_like_any_other()
    {
        var config = Config(handheld: 0, docked: null, egpu: 8192);

        var options = IgpuRestartPlanner.Options(config, DockMode.Egpu, Firmware(currentRaw: 16), effectiveRaw: 16);

        Assert.Equal([new IgpuRestartOption(DockMode.Handheld, 0, 0)], options);
        Assert.Empty(IgpuRestartPlanner.Options(config, DockMode.Egpu, Firmware(currentRaw: 0), effectiveRaw: 0));
    }

    private static readonly DateTime Started = new(2026, 11, 26, 8, 0, 0, DateTimeKind.Utc);

    private static BootSession Boot(uint? id, int minutesLater = 0) => new(id, Started.AddMinutes(minutesLater));

    [Fact]
    public void Waits_for_the_mode_the_pc_was_restarted_for_while_its_size_is_in_effect()
    {
        // "Restart for eGPU" as a handheld, and the eGPU gets connected only after sign-in.
        var config = Config(handheld: 8192, docked: null, egpu: 512);
        var target = new IgpuRestartTarget(DockMode.Egpu, Boot(41));

        var awaited = IgpuRestartPlanner.AwaitedMode(target, Boot(42, minutesLater: 2), DockMode.Handheld, config, Firmware(currentRaw: 1), effectiveRaw: 1);

        Assert.Equal(DockMode.Egpu, awaited);
    }

    [Fact]
    public void Does_not_wait_without_such_a_restart_or_when_the_pc_came_back_in_that_mode()
    {
        var config = Config(handheld: 8192, docked: null, egpu: 512);
        var target = new IgpuRestartTarget(DockMode.Egpu, Boot(41));

        Assert.Null(IgpuRestartPlanner.AwaitedMode(null, Boot(42), DockMode.Handheld, config, Firmware(currentRaw: 1), effectiveRaw: 1));
        Assert.Null(IgpuRestartPlanner.AwaitedMode(target, Boot(42, minutesLater: 2), DockMode.Egpu, config, Firmware(currentRaw: 1), effectiveRaw: 1));
    }

    [Fact]
    public void Does_not_wait_before_the_restart_or_on_a_later_boot()
    {
        var config = Config(handheld: 8192, docked: null, egpu: 512);
        var target = new IgpuRestartTarget(DockMode.Egpu, Boot(41));

        // DockSense restarted, Windows did not (the user cancelled the restart).
        Assert.Null(IgpuRestartPlanner.AwaitedMode(target, Boot(41), DockMode.Handheld, config, Firmware(currentRaw: 1), effectiveRaw: 16));
        Assert.Null(IgpuRestartPlanner.AwaitedMode(target, Boot(43, minutesLater: 90), DockMode.Handheld, config, Firmware(currentRaw: 1), effectiveRaw: 1));
    }

    [Fact]
    public void Does_not_wait_when_the_size_in_effect_is_not_the_one_restarted_for()
    {
        // The profile was changed since, or another tool stored something else before the restart.
        var target = new IgpuRestartTarget(DockMode.Egpu, Boot(41));

        Assert.Null(IgpuRestartPlanner.AwaitedMode(
            target, Boot(42, minutesLater: 2), DockMode.Handheld, Config(handheld: 8192, docked: null, egpu: 512), Firmware(currentRaw: 8), effectiveRaw: 8));
        Assert.Null(IgpuRestartPlanner.AwaitedMode(
            target, Boot(42, minutesLater: 2), DockMode.Handheld, Config(handheld: 8192, docked: null, egpu: null), Firmware(currentRaw: 1), effectiveRaw: 1));
    }

    [Fact]
    public void Without_a_boot_counter_the_start_time_tells_the_boots_apart()
    {
        var config = Config(handheld: 8192, docked: null, egpu: 512);
        var target = new IgpuRestartTarget(DockMode.Egpu, Boot(null));

        Assert.Null(IgpuRestartPlanner.AwaitedMode(target, Boot(null), DockMode.Handheld, config, Firmware(currentRaw: 1), effectiveRaw: 1));
        Assert.Equal(
            DockMode.Egpu,
            IgpuRestartPlanner.AwaitedMode(target, Boot(null, minutesLater: 2), DockMode.Handheld, config, Firmware(currentRaw: 1), effectiveRaw: 1));
    }
}
