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

/// <summary>What DockSense applies when a mode becomes active.</summary>
public sealed record ModeProfile
{
    public PowerPlanChoice PowerPlan { get; init; } = PowerPlanChoice.Unmanaged;

    public FseStartup Fse { get; init; } = FseStartup.Unchanged;

    /// <summary>Experimental: also enter or leave FSE right away when the mode changes, not just at next sign-in.</summary>
    public bool FseApplyImmediately { get; init; }
}
