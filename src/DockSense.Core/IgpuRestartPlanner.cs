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
}
