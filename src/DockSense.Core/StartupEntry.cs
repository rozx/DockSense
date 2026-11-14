namespace DockSense.Core;

/// <summary>Whether DockSense really starts with Windows.</summary>
public enum StartupState
{
    /// <summary>No startup entry for DockSense.</summary>
    Off,

    /// <summary>The entry exists, points at this copy and Windows runs it.</summary>
    On,

    /// <summary>The entry exists, but it was switched off in Task Manager or Windows Settings.</summary>
    DisabledByWindows,

    /// <summary>The entry starts a different copy of DockSense.</summary>
    OtherCopy,
}

/// <param name="State">What happens at sign-in.</param>
/// <param name="WaitsForDesktop">
/// True when the entry is on, but Windows holds it back until the first switch to the desktop when it boots into the
/// Xbox full screen experience.
/// </param>
/// <param name="OtherCommand">The command of the other copy, for <see cref="StartupState.OtherCopy"/>.</param>
public sealed record StartupStatus(StartupState State, bool WaitsForDesktop = false, string? OtherCommand = null);

/// <summary>
/// Reads the three places that decide whether a per-user Run entry starts: the Run value itself, Windows' own
/// on/off switch for it (<c>Explorer\StartupApproved\Run</c>), and the Xbox mode choice
/// (<c>GamingConfiguration\Startup\Run</c>).
/// </summary>
public static class StartupEntry
{
    /// <summary>The Xbox mode value for "Start at log in".</summary>
    public const int XboxModeStartAtLogIn = 1;

    /// <param name="runCommand">The Run value, or null when there is none.</param>
    /// <param name="expectedCommands">The commands that start this copy (current and legacy form).</param>
    /// <param name="approved">The StartupApproved value for the entry, or null when there is none.</param>
    /// <param name="xboxModeValue">The Xbox mode value for the entry, or null when there is none.</param>
    /// <param name="xboxModeAvailable">Whether this PC has the Xbox full screen experience at all.</param>
    public static StartupStatus Evaluate(
        string? runCommand, IReadOnlyCollection<string> expectedCommands, byte[]? approved, int? xboxModeValue, bool xboxModeAvailable)
    {
        ArgumentNullException.ThrowIfNull(expectedCommands);
        if (string.IsNullOrWhiteSpace(runCommand))
        {
            return new StartupStatus(StartupState.Off);
        }

        if (!expectedCommands.Contains(runCommand, StringComparer.OrdinalIgnoreCase))
        {
            return new StartupStatus(StartupState.OtherCopy, OtherCommand: runCommand);
        }

        if (IsDisabledByWindows(approved))
        {
            return new StartupStatus(StartupState.DisabledByWindows);
        }

        return new StartupStatus(StartupState.On, WaitsForDesktop: xboxModeAvailable && xboxModeValue != XboxModeStartAtLogIn);
    }

    /// <summary>
    /// Windows marks a disabled entry with an odd first byte (3 in older builds, 1 in current ones; 2 and 0 mean
    /// enabled). No value at all means the entry was never switched, which is enabled.
    /// </summary>
    public static bool IsDisabledByWindows(byte[]? approved) => approved is { Length: > 0 } && (approved[0] & 1) != 0;
}
