namespace DockSense.Core;

/// <summary>Decides whether a mode change should enter or leave the Xbox full screen experience (FSE) right away.</summary>
public static class FsePlanner
{
    /// <summary>
    /// How long after sign-in the shell is given to finish starting (and, when Windows boots into it, entering the
    /// full screen experience) before DockSense checks the live state and switches it.
    /// </summary>
    public static readonly TimeSpan SignInSettleTime = TimeSpan.FromSeconds(20);

    /// <summary>The wait used when the sign-in time is not known.</summary>
    public static readonly TimeSpan UnknownSignInDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long DockSense waits at its own start before switching the full screen experience live. Measured from
    /// sign-in, not from the start of DockSense: started by the sign-in task it is up before the shell and has to
    /// wait, started later (by Windows' Run key, or by the user) there is nothing left to wait for.
    /// </summary>
    /// <param name="sinceSignIn">Time since the user signed in, or null when unknown.</param>
    public static TimeSpan StartupDelay(TimeSpan? sinceSignIn)
    {
        if (sinceSignIn is not { } elapsed || elapsed < TimeSpan.Zero)
        {
            return UnknownSignInDelay;
        }

        return elapsed >= SignInSettleTime ? TimeSpan.Zero : SignInSettleTime - elapsed;
    }

    /// <summary>
    /// Returns true to enter FSE, false to leave it, or null to leave FSE as it is. Only the mode being entered
    /// decides: its profile needs <see cref="ModeProfile.FseApplyImmediately"/> and a target.
    /// </summary>
    /// <param name="config">The current configuration.</param>
    /// <param name="mode">The mode being applied.</param>
    public static bool? LiveTarget(DockSenseConfig config, DockMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);

        var entering = config.ProfileFor(mode);
        return entering.FseApplyImmediately && entering.Fse != FseStartup.Unchanged
            ? entering.Fse == FseStartup.FullScreenExperience
            : null;
    }
}
