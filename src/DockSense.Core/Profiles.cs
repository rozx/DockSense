namespace DockSense.Core;

/// <summary>What Xbox Full Screen Experience (FSE, "Xbox mode") should do in a mode.</summary>
public enum FseStartup
{
    /// <summary>Leave the user's FSE setting alone.</summary>
    Unchanged,

    /// <summary>Boot/sign in to the normal Windows desktop.</summary>
    Desktop,

    /// <summary>Boot/sign in straight into the Full Screen Experience.</summary>
    FullScreenExperience,
}

public enum PowerPlanKind
{
    /// <summary>DockSense does not touch the power plan in this mode (vendor software stays in charge).</summary>
    Unmanaged,

    /// <summary>A specific existing power plan, chosen by GUID.</summary>
    Specific,

    /// <summary>
    /// DockSense's own plan, duplicated from High Performance. Windows Game Mode's GameMode PPM overrides are only
    /// provisioned for Balanced-type plans, so this plan avoids the in-game EPP=100 throttle.
    /// </summary>
    DockSensePerformance,
}

public sealed record PowerPlanChoice
{
    public PowerPlanKind Kind { get; init; }

    /// <summary>The plan GUID when <see cref="Kind"/> is <see cref="PowerPlanKind.Specific"/>.</summary>
    public Guid? PlanId { get; init; }

    public static PowerPlanChoice Unmanaged { get; } = new() { Kind = PowerPlanKind.Unmanaged };

    public static PowerPlanChoice DockSensePerformance { get; } = new() { Kind = PowerPlanKind.DockSensePerformance };

    public static PowerPlanChoice Specific(Guid planId) => new() { Kind = PowerPlanKind.Specific, PlanId = planId };
}

/// <summary>When a <see cref="ProgramLaunch"/> runs.</summary>
public enum ProgramTrigger
{
    /// <summary>When the device switches into the mode.</summary>
    Enter,

    /// <summary>When the device switches into the mode, and when DockSense starts in the mode (e.g. at sign-in).</summary>
    EnterOrStartup,

    /// <summary>When the device switches out of the mode.</summary>
    Leave,
}

/// <summary>A program DockSense starts on a mode change, e.g. a launcher, a fan tool or a script.</summary>
public sealed record ProgramLaunch
{
    public bool Enabled { get; init; } = true;

    /// <summary>Program, script or document to open. Environment variables such as %LOCALAPPDATA% are expanded.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Command-line arguments. Environment variables are expanded.</summary>
    public string Arguments { get; init; } = string.Empty;

    public ProgramTrigger When { get; init; } = ProgramTrigger.Enter;

    /// <summary>Start without a visible window (useful for scripts and console tools).</summary>
    public bool Hidden { get; init; }

    /// <summary>Start elevated. Windows shows a UAC prompt each time.</summary>
    public bool RunAsAdministrator { get; init; }
}

/// <summary>What DockSense applies when a mode becomes active.</summary>
public sealed record ModeProfile
{
    public PowerPlanChoice PowerPlan { get; init; } = PowerPlanChoice.Unmanaged;

    public FseStartup Fse { get; init; } = FseStartup.Unchanged;

    /// <summary>Also enter or leave FSE right away when the mode changes, not just at next sign-in.</summary>
    public bool FseApplyImmediately { get; init; }

    /// <summary>
    /// Leave FSE right away when switching from this mode to another one, unless the new mode's own live setting
    /// (<see cref="FseApplyImmediately"/>) asks for something else. See <see cref="FsePlanner"/>.
    /// </summary>
    public bool FseExitOnLeave { get; init; }

    /// <summary>Programs started when this mode begins or ends (see <see cref="ProgramLaunch.When"/>).</summary>
    public IReadOnlyList<ProgramLaunch> Programs
    {
        get;
        init => field = value ?? []; // "programs": null in a hand-edited config means none
    } = [];

    /// <summary>
    /// iGPU memory (UMA frame buffer) in MB to store in the firmware for this mode; 0 = Auto, null = don't change.
    /// Takes effect at the next restart. Only on devices whose firmware offers the setting (see <see cref="IgpuMemoryOptions"/>).
    /// </summary>
    public int? IgpuMemoryMb { get; init; }

    // Value equality for the program list too, so an unchanged profile compares equal after a JSON round trip.
    public bool Equals(ModeProfile? other) =>
        other is not null
        && PowerPlan == other.PowerPlan
        && Fse == other.Fse
        && FseApplyImmediately == other.FseApplyImmediately
        && FseExitOnLeave == other.FseExitOnLeave
        && IgpuMemoryMb == other.IgpuMemoryMb
        && Programs.SequenceEqual(other.Programs);

    public override int GetHashCode() => HashCode.Combine(PowerPlan, Fse, FseApplyImmediately, FseExitOnLeave, IgpuMemoryMb, Programs.Count);
}
