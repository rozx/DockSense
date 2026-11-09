using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class BootSessionTests
{
    private static readonly DateTime Start = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_boot_counter_is_the_same_boot_despite_clock_drift()
    {
        Assert.True(new BootSession(45, Start).IsSameBoot(new BootSession(45, Start.AddSeconds(30))));
    }

    [Fact]
    public void Quick_restart_is_a_new_boot_when_the_counter_changed()
    {
        // Restarted 40 s after the previous boot started: start times are close, but the counter moved on.
        Assert.False(new BootSession(45, Start).IsSameBoot(new BootSession(46, Start.AddSeconds(40))));
    }

    [Fact]
    public void Without_a_counter_start_times_must_match_closely()
    {
        Assert.True(new BootSession(null, Start).IsSameBoot(new BootSession(46, Start.AddSeconds(3))));
        Assert.False(new BootSession(null, Start).IsSameBoot(new BootSession(null, Start.AddSeconds(40))));
    }
}
