using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class IgpuMemoryPlannerTests
{
    /// <summary>512 MB units: Auto, 0.5 GB, 1 GB, 4 GB and 8 GB.</summary>
    private static IgpuMemoryOptions Firmware(uint currentRaw) => new(512, [0, 1, 2, 8, 16], currentRaw);

    private static ModeProfile Profile(int? megabytes, IgpuMemoryApply apply = IgpuMemoryApply.Automatic) =>
        new() { IgpuMemoryMb = megabytes, IgpuMemoryApply = apply };

    [Fact]
    public void Automatic_stores_a_size_that_is_not_stored_yet()
    {
        var decision = IgpuMemoryPlanner.Decide(Profile(512), Firmware(currentRaw: 16), effectiveRaw: 16);

        Assert.Equal(new IgpuMemoryDecision(IgpuMemoryStep.Store, 512, 1), decision);
    }

    [Fact]
    public void Ask_offers_the_size_instead_of_storing_it()
    {
        var decision = IgpuMemoryPlanner.Decide(Profile(512, IgpuMemoryApply.Ask), Firmware(currentRaw: 16), effectiveRaw: 16);

        Assert.Equal(new IgpuMemoryDecision(IgpuMemoryStep.Ask, 512, 1), decision);
    }

    [Theory]
    [InlineData(IgpuMemoryApply.Automatic)]
    [InlineData(IgpuMemoryApply.Ask)]
    public void Nothing_to_do_when_the_size_is_stored_already(IgpuMemoryApply apply)
    {
        // Stored and waiting for a restart (8 GB still in effect), or simply in effect.
        Assert.Equal(IgpuMemoryStep.None, IgpuMemoryPlanner.Decide(Profile(512, apply), Firmware(currentRaw: 1), effectiveRaw: 16).Step);
        Assert.Equal(IgpuMemoryStep.None, IgpuMemoryPlanner.Decide(Profile(512, apply), Firmware(currentRaw: 1), effectiveRaw: 1).Step);
    }

    [Theory]
    [InlineData(IgpuMemoryApply.Automatic)]
    [InlineData(IgpuMemoryApply.Ask)]
    public void Nothing_to_do_when_the_profile_leaves_the_memory_alone_or_the_firmware_lacks_the_size(IgpuMemoryApply apply)
    {
        Assert.Equal(default, IgpuMemoryPlanner.Decide(Profile(null, apply), Firmware(currentRaw: 16), effectiveRaw: 16));
        Assert.Equal(default, IgpuMemoryPlanner.Decide(Profile(1536, apply), Firmware(currentRaw: 16), effectiveRaw: 16));
    }

    [Fact]
    public void Ask_puts_the_size_in_effect_back_without_asking()
    {
        // The eGPU's 512 MB was agreed to and stored, then the eGPU was unplugged before any restart: the handheld
        // profile's 8 GB is still in effect, so storing it again only withdraws the pending change.
        var decision = IgpuMemoryPlanner.Decide(Profile(8192, IgpuMemoryApply.Ask), Firmware(currentRaw: 1), effectiveRaw: 16);

        Assert.Equal(new IgpuMemoryDecision(IgpuMemoryStep.Store, 8192, 16), decision);
    }

    [Fact]
    public void Ask_still_asks_when_the_size_in_effect_is_unknown()
    {
        var decision = IgpuMemoryPlanner.Decide(Profile(8192, IgpuMemoryApply.Ask), Firmware(currentRaw: 1), effectiveRaw: null);

        Assert.Equal(IgpuMemoryStep.Ask, decision.Step);
    }

    [Fact]
    public void Auto_is_a_size_like_any_other()
    {
        Assert.Equal(
            new IgpuMemoryDecision(IgpuMemoryStep.Ask, 0, 0),
            IgpuMemoryPlanner.Decide(Profile(0, IgpuMemoryApply.Ask), Firmware(currentRaw: 16), effectiveRaw: 16));
        Assert.Equal(IgpuMemoryStep.None, IgpuMemoryPlanner.Decide(Profile(0), Firmware(currentRaw: 0), effectiveRaw: 0).Step);
    }
}
