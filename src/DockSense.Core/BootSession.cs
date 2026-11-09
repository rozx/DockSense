namespace DockSense.Core;

/// <summary>Identifies one Windows boot, e.g. to tell whether a firmware change has been applied by a restart since.</summary>
/// <param name="BootId">Windows' boot counter (incremented on every boot), when it can be read.</param>
/// <param name="StartedUtc">When Windows started (approximate: derived from the tick count).</param>
public sealed record BootSession(uint? BootId, DateTime StartedUtc)
{
    /// <summary>How far two start times derived from the tick count may drift apart within one boot.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True when both describe the same boot. The boot counter decides when both have it, because a quick restart
    /// can start a new boot within seconds of the previous one. Otherwise the start times must match closely.
    /// </summary>
    public bool IsSameBoot(BootSession other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return BootId is { } id && other.BootId is { } otherId
            ? id == otherId
            : (StartedUtc - other.StartedUtc).Duration() <= StartTimeTolerance;
    }
}
