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
