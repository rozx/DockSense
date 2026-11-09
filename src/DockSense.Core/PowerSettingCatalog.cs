namespace DockSense.Core;

/// <summary>How a power setting's raw value is shown and edited.</summary>
public enum PowerSettingKind
{
    /// <summary>0–100 %.</summary>
    Percent,

    /// <summary>MHz; 0 means no limit.</summary>
    Megahertz,

    /// <summary>Stored in seconds, edited in whole minutes; 0 means never.</summary>
    Minutes,

    /// <summary>One of a fixed list of values (see <see cref="PowerSettingDefinition.Choices"/>).</summary>
    Choice,
}

/// <summary>
/// A power plan setting DockSense lets the user edit. <see cref="Key"/> names the UI strings
/// (<c>PS_{Key}</c>, <c>PS_{Key}_Desc</c> and, for choices, <c>PS_{Key}_{value}</c>).
/// </summary>
/// <param name="Key">Stable identifier, also used for the UI strings.</param>
/// <param name="Subgroup">Power setting subgroup GUID.</param>
/// <param name="Setting">Power setting GUID that is read and written.</param>
/// <param name="Kind">How the value is shown and edited.</param>
/// <param name="Twins">Settings written with the same value, e.g. the efficiency-class-1 copy on hybrid CPUs.</param>
/// <param name="Choices">Allowed raw values for <see cref="PowerSettingKind.Choice"/>.</param>
public sealed record PowerSettingDefinition(
    string Key,
    Guid Subgroup,
    Guid Setting,
    PowerSettingKind Kind,
    IReadOnlyList<Guid> Twins,
    IReadOnlyList<uint> Choices)
{
    public double Minimum => 0;

    public double Maximum => Kind switch
    {
        PowerSettingKind.Percent => 100,
        PowerSettingKind.Megahertz => 10_000,
        PowerSettingKind.Minutes => 99_999,
        _ => Choices.Count == 0 ? 0 : Choices.Max(),
    };

    /// <summary>Raw (stored) value to the value shown in the editor.</summary>
    public double ToDisplay(uint raw) => Kind == PowerSettingKind.Minutes ? Math.Round(raw / 60.0) : raw;

    /// <summary>Editor value to the raw value Windows stores. Clamps to the allowed range.</summary>
    public uint ToRaw(double display)
    {
        var value = Math.Clamp(Math.Round(display), Minimum, Maximum);
        return Kind == PowerSettingKind.Minutes ? (uint)(value * 60) : (uint)value;
    }

    /// <summary>True when <paramref name="raw"/> can be written (a listed choice, or inside the range).</summary>
    public bool IsValid(uint raw) => Kind switch
    {
        PowerSettingKind.Choice => Choices.Contains(raw),
        PowerSettingKind.Minutes => raw / 60.0 <= Maximum,
        _ => raw <= Maximum,
    };
}

/// <summary>The power plan settings shown in DockSense's plan editor, and well-known plan GUIDs.</summary>
public static class PowerSettingCatalog
{
    public static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid DisplaySubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    public static readonly Guid PowerSaverPlan = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformancePlan = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Plans that ship with Windows. DockSense never deletes them.</summary>
    public static IReadOnlySet<Guid> BuiltInPlans { get; } = new HashSet<Guid>
    {
        PowerSaverPlan, BalancedPlan, HighPerformancePlan, UltimatePerformancePlan,
    };

    /// <summary>
    /// Power mode overlays (Settings → Power mode). They are stored next to the plans but are not plans, so they
    /// are never listed.
    /// </summary>
    public static IReadOnlySet<Guid> Overlays { get; } = new HashSet<Guid>
    {
        new("961cc777-2547-4f9d-8174-7d86181b8a7a"), // best power efficiency
        new("3af9b8d9-7c97-431d-ad78-34a8bfea439f"), // better performance
        new("ded574b5-45a0-4f42-8737-46345c09c238"), // best performance
    };

    public static IReadOnlyList<PowerSettingDefinition> Settings { get; } =
    [
        Processor("MinState", "893dee8e-2bef-41e0-89c6-b55d0929964c", PowerSettingKind.Percent, "893dee8e-2bef-41e0-89c6-b55d0929964d"),
        Processor("MaxState", "bc5038f7-23e0-4960-96da-33abaf5935ec", PowerSettingKind.Percent, "bc5038f7-23e0-4960-96da-33abaf5935ed"),
        Processor("Epp", "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", PowerSettingKind.Percent, "36687f9e-e3a5-4dbf-b1dc-15eb381c6864"),
        Processor("Boost", "be337238-0d82-4146-a960-4f3749d470c7", PowerSettingKind.Choice, choices: [0, 1, 2, 3, 4, 5, 6]),
        Processor("MaxFrequency", "75b0ae3f-bce0-45a7-8c89-c9611c25e100", PowerSettingKind.Megahertz, "75b0ae3f-bce0-45a7-8c89-c9611c25e101"),
        Processor("CorePreference", "93b8b6dc-0698-4d1c-9ee4-0644e900c85d", PowerSettingKind.Choice, "bae08b81-2d5e-4688-ad6a-13243356654b", [0, 1, 2, 3, 4, 5]),
        new("DisplayOff", DisplaySubgroup, new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), PowerSettingKind.Minutes, [], []),
        new("Sleep", SleepSubgroup, new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), PowerSettingKind.Minutes, [], []),
    ];

    private static PowerSettingDefinition Processor(string key, string setting, PowerSettingKind kind, string? twin = null, uint[]? choices = null) =>
        new(key, ProcessorSubgroup, new Guid(setting), kind, twin is null ? [] : [new Guid(twin)], choices ?? []);
}
