namespace DockSense.Core;

/// <summary>A mode the PC can be restarted for, with the iGPU memory size its profile asks for.</summary>
/// <param name="Mode">The mode the user is about to switch to.</param>
/// <param name="MemoryMb">That mode's iGPU memory in MB (0 = Auto).</param>
/// <param name="Raw">The raw firmware value for that size.</param>
public sealed record IgpuRestartOption(DockMode Mode, int MemoryMb, uint Raw);

/// <summary>
/// Decides for which modes "Restart for &lt;mode&gt;" is worth offering: the iGPU memory only changes at a restart,
/// so storing the next mode's size before restarting saves the restart that would otherwise follow the mode change.
/// </summary>
public static class IgpuRestartPlanner
{
    /// <summary>
    /// The other modes whose profile sets an iGPU memory size that the firmware offers and that is not the size in
    /// effect now. A mode whose size is already in effect needs no restart: switching to it is enough.
    /// </summary>
    /// <param name="config">The current configuration.</param>
    /// <param name="current">The mode the device is in now.</param>
    /// <param name="firmware">The sizes the firmware offers.</param>
    /// <param name="effectiveRaw">The raw firmware value in effect during this Windows session.</param>
    public static IReadOnlyList<IgpuRestartOption> Options(DockSenseConfig config, DockMode current, IgpuMemoryOptions firmware, uint effectiveRaw)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(firmware);

        List<IgpuRestartOption> options = [];
        foreach (var mode in Enum.GetValues<DockMode>())
        {
            if (mode != current
                && config.ProfileFor(mode).IgpuMemoryMb is { } megabytes
                && firmware.RawFor(megabytes) is { } raw
                && raw != effectiveRaw)
            {
                options.Add(new IgpuRestartOption(mode, megabytes, raw));
            }
        }

        return options;
    }

    /// <summary>How long after a restart was prepared a boot may begin and still count as coming from it.</summary>
    public static readonly TimeSpan LongestRestart = TimeSpan.FromHours(2);

    /// <summary>
    /// The mode the user restarted the PC for, when it has come back up from that restart with that mode's size in
    /// effect but in another mode: the dock or eGPU that makes it that mode is not connected yet. Some PCs do not
    /// start with an eGPU attached, so connecting it after sign-in is a normal way to get there, and until then the
    /// size the user restarted for should stay. Null when there is nothing to wait for.
    /// </summary>
    /// <param name="target">The restart the user asked DockSense for, if any.</param>
    /// <param name="boot">The current boot.</param>
    /// <param name="current">The mode the device is in now.</param>
    /// <param name="config">The current configuration.</param>
    /// <param name="firmware">The sizes the firmware offers.</param>
    /// <param name="effectiveRaw">The raw firmware value in effect during this Windows session.</param>
    public static DockMode? AwaitedMode(
        IgpuRestartTarget? target, BootSession boot, DockMode current, DockSenseConfig config, IgpuMemoryOptions firmware, uint effectiveRaw)
    {
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(firmware);

        if (target is null || target.Mode == current || target.Boot.IsSameBoot(boot))
        {
            return null;
        }

        // Only a boot the PC came back up in from that restart, not one days later. Boots are not counted: a
        // restart can pass through more than one (the offline phase of a Windows update, Automatic Repair) before
        // anyone signs in. The boot must have begun soon after the restart was prepared.
        if (boot.StartedUtc - target.PreparedUtc > LongestRestart)
        {
            return null;
        }

        return config.ProfileFor(target.Mode).IgpuMemoryMb is { } megabytes && firmware.RawFor(megabytes) == effectiveRaw
            ? target.Mode
            : null;
    }

    /// <summary>
    /// How long the wait for the mode restarted for still has to run: all of <paramref name="wait"/> when it has
    /// not begun, what is left of it otherwise (never more than <paramref name="wait"/>, should the clock have been
    /// set back), and zero when it is over.
    /// </summary>
    public static TimeSpan RemainingWait(IgpuRestartTarget target, DateTime nowUtc, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.WaitUntilUtc is not { } until)
        {
            return wait;
        }

        var left = until - nowUtc;
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left < wait ? left : wait;
    }
}

/// <summary>
/// A restart the user started from DockSense to bring a mode's iGPU memory size into effect: "Restart for
/// &lt;mode&gt;", or a restart for the size stored for the current mode.
/// </summary>
/// <param name="Mode">The mode the user restarted for.</param>
/// <param name="Boot">The boot during which they did, i.e. the one before the restart.</param>
/// <param name="PreparedUtc">When the restart was prepared: before the size is stored, so the restart itself may still not happen.</param>
/// <param name="WaitUntilUtc">
/// Set once the PC is back up and DockSense waits for that mode: when the wait ends. It is kept with the record so
/// that closing DockSense and starting it again does not end the wait, nor start it over.
/// </param>
public sealed record IgpuRestartTarget(DockMode Mode, BootSession Boot, DateTime PreparedUtc, DateTime? WaitUntilUtc = null);
