using DockSense.Core;

namespace DockSense.Core.Tests;

public class ModeStabilizerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Fact]
    public void Observing_the_current_mode_changes_nothing()
    {
        var stabilizer = new ModeStabilizer(DockMode.Handheld, Settle);

        Assert.False(stabilizer.Observe(DockMode.Handheld, T0));
        Assert.Null(stabilizer.PendingDueAt);
    }

    [Fact]
    public void New_mode_commits_only_after_settle_time()
    {
        var stabilizer = new ModeStabilizer(DockMode.Handheld, Settle);

        Assert.False(stabilizer.Observe(DockMode.Egpu, T0));
        Assert.Equal(T0 + Settle, stabilizer.PendingDueAt);
        Assert.False(stabilizer.Observe(DockMode.Egpu, T0.AddSeconds(4.9)));
        Assert.Equal(DockMode.Handheld, stabilizer.Current);

        Assert.True(stabilizer.Observe(DockMode.Egpu, T0.AddSeconds(5)));
        Assert.Equal(DockMode.Egpu, stabilizer.Current);
        Assert.Null(stabilizer.PendingDueAt);
    }

    [Fact]
    public void Flapping_restarts_the_timer()
    {
        var stabilizer = new ModeStabilizer(DockMode.Handheld, Settle);

        stabilizer.Observe(DockMode.Docked, T0);
        stabilizer.Observe(DockMode.Egpu, T0.AddSeconds(3));

        Assert.False(stabilizer.Observe(DockMode.Egpu, T0.AddSeconds(6)));
        Assert.True(stabilizer.Observe(DockMode.Egpu, T0.AddSeconds(8)));
        Assert.Equal(DockMode.Egpu, stabilizer.Current);
    }

    [Fact]
    public void Returning_to_current_mode_cancels_the_pending_change()
    {
        var stabilizer = new ModeStabilizer(DockMode.Egpu, Settle);

        stabilizer.Observe(DockMode.Handheld, T0);
        stabilizer.Observe(DockMode.Egpu, T0.AddSeconds(2));

        Assert.Null(stabilizer.PendingDueAt);
        Assert.False(stabilizer.Observe(DockMode.Handheld, T0.AddSeconds(6)));
        Assert.Equal(DockMode.Egpu, stabilizer.Current);
    }

    [Fact]
    public void Zero_settle_time_commits_immediately()
    {
        var stabilizer = new ModeStabilizer(DockMode.Handheld, TimeSpan.Zero);

        Assert.True(stabilizer.Observe(DockMode.Docked, T0));
        Assert.Equal(DockMode.Docked, stabilizer.Current);
    }

    [Fact]
    public void Negative_settle_time_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModeStabilizer(DockMode.Handheld, TimeSpan.FromSeconds(-1)));
    }
}
