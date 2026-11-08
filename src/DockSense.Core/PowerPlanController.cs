namespace DockSense.Core;

/// <summary>Persisted between app runs so the pre-takeover plan can be restored after a restart.</summary>
/// <param name="OwnedPlan">The plan DockSense activated and is keeping active, if any.</param>
/// <param name="RestoreTo">The plan to go back to when DockSense stops managing the plan.</param>
public sealed record PowerPlanState(Guid? OwnedPlan, Guid? RestoreTo);

/// <summary>
/// Decides which power plan to activate. It does not call Windows itself: every method returns the plan GUID to
/// activate, or null for "do nothing".
/// <list type="bullet">
/// <item>Entering a managed mode remembers the current plan and activates the target.</item>
/// <item>Leaving to an unmanaged mode restores the remembered plan, but only if our plan is still active.</item>
/// <item>If other software switches away from our plan, that plan becomes the one to restore later, and ours is
/// re-applied. After too many reverts in a short window it gives up and stops fighting.</item>
/// </list>
/// </summary>
public sealed class PowerPlanController
{
    private readonly Queue<DateTimeOffset> _recentReasserts = new();
    private Guid? _owned;
    private Guid? _restoreTo;

    public PowerPlanController(PowerPlanState? savedState = null)
    {
        _owned = savedState?.OwnedPlan;
        _restoreTo = savedState?.RestoreTo;
    }

    public bool ReassertEnabled { get; init; } = true;

    public int MaxReasserts { get; init; } = 5;

    public TimeSpan ReassertWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>True after DockSense stopped re-applying its plan because something kept switching it away.</summary>
    public bool GaveUp { get; private set; }

    public PowerPlanState State => new(_owned, _restoreTo);

    /// <summary>Called when a mode's profile is applied (including at startup).</summary>
    /// <param name="target">The plan the new mode wants, or null if the mode leaves the plan unmanaged.</param>
    /// <param name="currentActive">The plan active right now.</param>
    /// <param name="fallback">What to restore if no previous plan is known (e.g. Balanced).</param>
    public Guid? OnModeApplied(Guid? target, Guid currentActive, Guid fallback)
    {
        GaveUp = false;
        _recentReasserts.Clear();

        if (target is { } wanted)
        {
            if (_owned is null && currentActive != wanted)
            {
                _restoreTo = currentActive;
            }

            _owned = wanted;
            return currentActive == wanted ? null : wanted;
        }

        if (_owned is not { } owned)
        {
            return null;
        }

        var restore = _restoreTo ?? fallback;
        _owned = null;
        _restoreTo = null;

        // If something else already moved off our plan, respect that choice instead of overriding it.
        return currentActive == owned && restore != currentActive ? restore : null;
    }

    /// <summary>Called when Windows reports that the active plan changed (from any source, including us).</summary>
    public Guid? OnActivePlanChanged(Guid newActive, DateTimeOffset now)
    {
        if (_owned is not { } owned || newActive == owned)
        {
            return null;
        }

        if (!ReassertEnabled)
        {
            // The user or vendor software took over; stop managing until the next mode change.
            _owned = null;
            _restoreTo = null;
            return null;
        }

        while (_recentReasserts.Count > 0 && now - _recentReasserts.Peek() > ReassertWindow)
        {
            _recentReasserts.Dequeue();
        }

        if (_recentReasserts.Count >= MaxReasserts)
        {
            GaveUp = true;
            _owned = null;
            _restoreTo = null;
            return null;
        }

        // Vendor software picked this plan for the current conditions (e.g. Armoury Crate on an AC/DC change),
        // so it is the right one to restore when we stop managing.
        _restoreTo = newActive;
        _recentReasserts.Enqueue(now);
        return owned;
    }
}
