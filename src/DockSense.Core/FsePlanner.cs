namespace DockSense.Core;

/// <summary>Decides whether a mode change should enter or leave the Xbox full screen experience (FSE) right away.</summary>
public static class FsePlanner
{
    /// <summary>
    /// Returns true to enter FSE, false to leave it, or null to leave FSE as it is.
    /// The mode being entered decides first (<see cref="ModeProfile.FseApplyImmediately"/> with a target), then the
    /// mode being left (<see cref="ModeProfile.FseExitOnLeave"/>).
    /// </summary>
    /// <param name="config">The current configuration.</param>
    /// <param name="previous">The mode whose profile was applied before, or null at startup.</param>
    /// <param name="current">The mode being applied.</param>
    public static bool? LiveTarget(DockSenseConfig config, DockMode? previous, DockMode current)
    {
        ArgumentNullException.ThrowIfNull(config);

        var entering = config.ProfileFor(current);
        if (entering.FseApplyImmediately && entering.Fse != FseStartup.Unchanged)
        {
            return entering.Fse == FseStartup.FullScreenExperience;
        }

        if (previous is { } left && left != current && config.ProfileFor(left).FseExitOnLeave)
        {
            return false;
        }

        return null;
    }
}
