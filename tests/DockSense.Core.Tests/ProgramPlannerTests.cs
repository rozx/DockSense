using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class ProgramPlannerTests
{
    private static readonly ProgramLaunch HandheldEnter = new() { Path = "handheld-enter.exe", When = ProgramTrigger.Enter };
    private static readonly ProgramLaunch HandheldStartup = new() { Path = "handheld-startup.exe", When = ProgramTrigger.EnterOrStartup };
    private static readonly ProgramLaunch HandheldLeave = new() { Path = "handheld-leave.exe", When = ProgramTrigger.Leave };
    private static readonly ProgramLaunch EgpuEnter = new() { Path = "egpu-enter.exe", When = ProgramTrigger.Enter };
    private static readonly ProgramLaunch EgpuLeave = new() { Path = "egpu-leave.exe", When = ProgramTrigger.Leave };

    private static readonly DockSenseConfig Config = new()
    {
        Handheld = new ModeProfile { Programs = [HandheldEnter, HandheldStartup, HandheldLeave] },
        Egpu = new ModeProfile { Programs = [EgpuEnter, EgpuLeave] },
    };

    [Fact]
    public void Startup_runs_only_startup_programs_of_the_current_mode()
    {
        var programs = ProgramPlanner.Select(Config, previous: null, DockMode.Handheld, isStartup: true);

        Assert.Equal([HandheldStartup], programs);
    }

    [Fact]
    public void Mode_change_runs_leave_programs_first_then_enter_programs()
    {
        var programs = ProgramPlanner.Select(Config, DockMode.Handheld, DockMode.Egpu, isStartup: false);

        Assert.Equal([HandheldLeave, EgpuEnter], programs);
    }

    [Fact]
    public void Entering_runs_both_enter_and_startup_programs()
    {
        var programs = ProgramPlanner.Select(Config, DockMode.Egpu, DockMode.Handheld, isStartup: false);

        Assert.Equal([EgpuLeave, HandheldEnter, HandheldStartup], programs);
    }

    [Fact]
    public void Reapplying_the_same_mode_runs_nothing()
    {
        Assert.Empty(ProgramPlanner.Select(Config, DockMode.Egpu, DockMode.Egpu, isStartup: false));
    }

    [Fact]
    public void Disabled_and_empty_entries_are_skipped()
    {
        var config = new DockSenseConfig
        {
            Docked = new ModeProfile
            {
                Programs =
                [
                    new ProgramLaunch { Path = "off.exe", Enabled = false },
                    new ProgramLaunch { Path = "  " },
                    new ProgramLaunch { Path = "on.exe" },
                ],
            },
        };

        var programs = ProgramPlanner.Select(config, DockMode.Handheld, DockMode.Docked, isStartup: false);

        Assert.Equal("on.exe", Assert.Single(programs).Path);
    }
}
