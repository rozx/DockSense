namespace DockSense.Core;

/// <summary>Decides which configured programs to start when a mode's profile is applied.</summary>
public static class ProgramPlanner
{
    /// <summary>
    /// Returns the programs to start, in order: first the <see cref="ProgramTrigger.Leave"/> programs of the mode
    /// being left, then the programs of the mode being entered.
    /// </summary>
    /// <param name="config">The current configuration.</param>
    /// <param name="previous">The mode whose profile was applied last, or null if none was applied yet.</param>
    /// <param name="current">The mode being applied.</param>
    /// <param name="isStartup">True when DockSense itself is starting (no mode change happened).</param>
    public static IReadOnlyList<ProgramLaunch> Select(DockSenseConfig config, DockMode? previous, DockMode current, bool isStartup)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (isStartup)
        {
            return Runnable(config.ProfileFor(current), ProgramTrigger.EnterOrStartup).ToList();
        }

        if (previous == current)
        {
            return [];
        }

        var leaving = previous is { } left ? Runnable(config.ProfileFor(left), ProgramTrigger.Leave) : [];
        var entering = Runnable(config.ProfileFor(current), ProgramTrigger.Enter, ProgramTrigger.EnterOrStartup);
        return leaving.Concat(entering).ToList();
    }

    private static IEnumerable<ProgramLaunch> Runnable(ModeProfile profile, params ProgramTrigger[] triggers) =>
        profile.Programs.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Path) && triggers.Contains(p.When));
}
