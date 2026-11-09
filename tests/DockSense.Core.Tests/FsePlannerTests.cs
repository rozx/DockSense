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

        Assert.True(FsePlanner.LiveTarget(config, DockMode.Egpu, DockMode.Handheld));
        Assert.False(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Egpu));
    }

    [Fact]
    public void Exit_on_leave_leaves_fse_when_the_new_mode_has_no_live_switch()
    {
        var config = new DockSenseConfig { Handheld = LiveFse with { FseExitOnLeave = true }, Docked = NotLive, Egpu = NotLive };

        Assert.False(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Docked));
        Assert.False(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Egpu));
    }

    [Fact]
    public void Without_exit_on_leave_fse_is_left_alone()
    {
        var config = new DockSenseConfig { Handheld = LiveFse, Docked = NotLive };

        Assert.Null(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Docked));
    }

    [Fact]
    public void New_modes_live_setting_beats_exit_on_leave()
    {
        var config = new DockSenseConfig { Handheld = LiveFse with { FseExitOnLeave = true }, Docked = LiveFse };

        Assert.True(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Docked));
    }

    [Fact]
    public void Exit_on_leave_works_without_a_sign_in_setting()
    {
        // FSE entered by hand (Win+F11) in handheld mode, left when docking.
        var config = new DockSenseConfig { Handheld = new ModeProfile { FseExitOnLeave = true }, Docked = NotLive };

        Assert.False(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Docked));
    }

    [Fact]
    public void Startup_and_same_mode_do_not_trigger_exit_on_leave()
    {
        var config = new DockSenseConfig { Handheld = new ModeProfile { FseExitOnLeave = true }, Docked = NotLive };

        Assert.Null(FsePlanner.LiveTarget(config, previous: null, DockMode.Docked));
        Assert.Null(FsePlanner.LiveTarget(config, DockMode.Handheld, DockMode.Handheld));
    }
}
