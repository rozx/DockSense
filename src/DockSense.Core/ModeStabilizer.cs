namespace DockSense.Core;

/// <summary>
/// Debounces mode changes. A new mode is committed only after it has been observed continuously for the settle
/// time, so transient states while a dock is being plugged or unplugged are ignored.
/// </summary>
public sealed class ModeStabilizer
{
    private readonly TimeSpan _settleTime;
    private DockMode? _candidate;
    private DateTimeOffset _candidateSince;

    public ModeStabilizer(DockMode initial, TimeSpan settleTime)
    {
        if (settleTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settleTime), "Settle time cannot be negative.");
        }

        Current = initial;
        _settleTime = settleTime;
    }

    /// <summary>The committed mode.</summary>
    public DockMode Current { get; private set; }

    /// <summary>
    /// When the pending candidate becomes due, or null if nothing is pending. Call <see cref="Observe"/> again at
    /// that time (with a fresh observation) to commit it.
    /// </summary>
    public DateTimeOffset? PendingDueAt => _candidate is null ? null : _candidateSince + _settleTime;

    /// <summary>Feeds an observation. Returns true when this call committed a mode change.</summary>
    public bool Observe(DockMode observed, DateTimeOffset now)
    {
        if (observed == Current)
        {
            _candidate = null;
            return false;
        }

        if (_candidate != observed)
        {
            _candidate = observed;
            _candidateSince = now;
        }

        if (now - _candidateSince < _settleTime)
        {
            return false;
        }

        Current = observed;
        _candidate = null;
        return true;
    }
}
