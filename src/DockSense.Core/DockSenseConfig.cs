namespace DockSense.Core;

/// <summary>User configuration, stored as JSON at <c>%APPDATA%\DockSense\config.json</c>.</summary>
public sealed record DockSenseConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ModeProfile Handheld { get; init; } = new() { Fse = FseStartup.FullScreenExperience };

    public ModeProfile Docked { get; init; } = new() { Fse = FseStartup.Desktop };

    public ModeProfile Egpu { get; init; } = new()
    {
        PowerPlan = PowerPlanChoice.DockSensePerformance,
        Fse = FseStartup.Desktop,
    };

    public DetectionOptions Detection { get; init; } = new();

    /// <summary>
    /// Switch back to the managed plan when other software (e.g. Armoury Crate) changes it while a managed mode is
    /// active. Gives up after repeated reverts (see <see cref="PowerPlanController"/>).
    /// </summary>
    public bool ReassertPowerPlan { get; init; } = true;

    /// <summary>GUID of the plan DockSense created for <see cref="PowerPlanKind.DockSensePerformance"/>, once it exists.</summary>
    public Guid? DockSensePerformancePlanId { get; init; }

    /// <summary>
    /// The board (see <see cref="TdpDevice.Board"/>) on which the user agreed to a TDP above the manufacturer's
    /// maximum, and the highest value agreed to there (<see cref="TdpConfirmedWatts"/>). A profile's value above
    /// the maximum is only sent while this covers it, so a settings file copied from another device cannot carry
    /// a value past the question.
    /// </summary>
    public string? TdpConfirmedBoard { get; init; }

    public int TdpConfirmedWatts { get; init; }

    /// <summary>Whether DockSense looks for new releases, and what it does when it finds one.</summary>
    public UpdateMode Updates { get; init; } = UpdateMode.Notify;

    public UiSettings Ui { get; init; } = new();

    public ModeProfile ProfileFor(DockMode mode) => mode switch
    {
        DockMode.Handheld => Handheld,
        DockMode.Docked => Docked,
        DockMode.Egpu => Egpu,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
