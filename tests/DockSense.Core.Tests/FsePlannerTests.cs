using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class FsePlannerTests
{
    private static readonly ModeProfile LiveFse = new() { Fse = FseStartup.FullScreenExperience, FseApplyImmediately = true };
    private static readonly ModeProfile LiveDesktop = new() { Fse = FseStartup.Desktop, FseApplyImmediately = true };
    private static readonly ModeProfile NotLive = new() { Fse = FseStartup.Desktop };

    [Fact]
    public void Entering_mode_with_live_switch_decides()
    {
        var config = new DockSenseConfig { Handheld = LiveFse, Egpu = LiveDesktop };

        Assert.True(FsePlanner.LiveTarget(config, DockMode.Handheld));
        Assert.False(FsePlanner.LiveTarget(config, DockMode.Egpu));
    }

    [Fact]
    public void A_mode_without_the_live_switch_leaves_fse_alone()
    {
        var config = new DockSenseConfig { Handheld = LiveFse, Docked = NotLive };

        Assert.Null(FsePlanner.LiveTarget(config, DockMode.Docked));
    }

    [Fact]
    public void The_live_switch_needs_a_target()
    {
        var config = new DockSenseConfig { Docked = new ModeProfile { FseApplyImmediately = true } };

        Assert.Null(FsePlanner.LiveTarget(config, DockMode.Docked));
    }

    [Theory]
    [InlineData(2, 18)] // started by the sign-in task, before the shell
    [InlineData(19.5, 0.5)]
    [InlineData(20, 0)]
    [InlineData(36, 0)] // started from the Run key in Xbox mode: the shell is long up
    [InlineData(3600, 0)] // started by the user
    public void The_startup_wait_counts_from_sign_in(double secondsSinceSignIn, double expectedWait)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedWait), FsePlanner.StartupDelay(TimeSpan.FromSeconds(secondsSinceSignIn)));
    }

    [Fact]
    public void An_unknown_or_impossible_sign_in_time_waits_the_fixed_time()
    {
        Assert.Equal(FsePlanner.UnknownSignInDelay, FsePlanner.StartupDelay(null));
        Assert.Equal(FsePlanner.UnknownSignInDelay, FsePlanner.StartupDelay(TimeSpan.FromSeconds(-5))); // clock set back
    }
}
