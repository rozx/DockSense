namespace DockSense.Core;

/// <summary>What to do about a mode's iGPU memory size when its profile is applied.</summary>
public enum IgpuMemoryStep
{
    /// <summary>Nothing: the profile leaves the memory alone, the firmware lacks the size, or it is stored already.</summary>
    None,

    /// <summary>Store the size in the firmware now.</summary>
    Store,

    /// <summary>Ask the user; store it only once they agree.</summary>
    Ask,
}

/// <param name="Step">What to do.</param>
/// <param name="MemoryMb">The size the profile asks for, in MB (0 = Auto). Only meaningful when there is a step.</param>
/// <param name="Raw">The raw firmware value for that size.</param>
public readonly record struct IgpuMemoryDecision(IgpuMemoryStep Step, int MemoryMb = 0, uint Raw = 0);

/// <summary>
/// Decides whether a mode's iGPU memory size is stored right away or only offered
/// (<see cref="ModeProfile.IgpuMemoryApply"/>).
/// </summary>
public static class IgpuMemoryPlanner
{
    /// <param name="profile">The profile of the mode that is active.</param>
    /// <param name="firmware">The sizes the firmware offers and the one it has stored.</param>
    /// <param name="effectiveRaw">The raw firmware value in effect during this Windows session, when known.</param>
    public static IgpuMemoryDecision Decide(ModeProfile profile, IgpuMemoryOptions firmware, uint? effectiveRaw)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(firmware);

        if (profile.IgpuMemoryMb is not { } megabytes
            || firmware.RawFor(megabytes) is not { } raw
            || raw == firmware.CurrentRaw)
        {
            return default;
        }

        // Putting back the size that is in effect only withdraws a change that was waiting for a restart. No
        // restart follows from it, so there is nothing to agree to.
        var ask = profile.IgpuMemoryApply == IgpuMemoryApply.Ask && raw != effectiveRaw;
        return new IgpuMemoryDecision(ask ? IgpuMemoryStep.Ask : IgpuMemoryStep.Store, megabytes, raw);
    }
}
