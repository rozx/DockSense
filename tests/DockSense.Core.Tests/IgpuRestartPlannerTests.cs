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
}
