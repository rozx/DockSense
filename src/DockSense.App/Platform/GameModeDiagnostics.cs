using Microsoft.Win32;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>One provisioned GameMode override value.</summary>
public sealed record GameModeOverride(string Setting, string Scheme, uint? AcValue);

public sealed record GameModeReport(
    bool GameModeEnabled,
    int? ActivePlanPersonality,
    IReadOnlyList<GameModeOverride> Overrides)
{
    /// <summary>
    /// True when a game in the foreground will get the throttling GameMode profile with the current plan:
    /// Game Mode is on, the active plan is Balanced-type, and the provisioned Balanced GameMode profile sets a
    /// power-saving EPP or prefers efficient cores.
    /// </summary>
    public bool ThrottlesActivePlan =>
        GameModeEnabled
        && ActivePlanPersonality == 2
        && Overrides.Any(o => o.Scheme == "Balanced" && o.AcValue is { } v &&
            (o.Setting.StartsWith("Energy performance preference", StringComparison.Ordinal) ? v >= 80 : v is 3 or 4));
}

/// <summary>
/// Reads the GameMode processor power profile overrides that provisioning packages install (they are invisible to
/// powercfg). See docs/game-mode-throttle.md.
/// </summary>
internal static class GameModeDiagnostics
{
    private const string ProcessorSettingsKey =
        @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00";

    private const string GameModeProfile = "d4140c81-ebba-4e60-8561-6918290359cd";

    private static readonly (string Name, string Guid)[] Settings =
    [
        ("Energy performance preference", "36687f9e-e3a5-4dbf-b1dc-15eb381c6863"),
        ("Energy performance preference (efficiency class 1)", "36687f9e-e3a5-4dbf-b1dc-15eb381c6864"),
        ("Heterogeneous thread scheduling policy", "93b8b6dc-0698-4d1c-9ee4-0644e900c85d"),
        ("Heterogeneous short running thread scheduling policy", "bae08b81-2d5e-4688-ad6a-13243356654b"),
    ];

    private static readonly (string Name, string Guid)[] Schemes =
    [
        ("Balanced", "381b4222-f694-41f0-9685-ff5bb260df2e"),
        ("Best performance overlay", "ded574b5-45a0-4f42-8737-46345c09c238"),
        ("Best power efficiency overlay", "961cc777-2547-4f9d-8174-7d86181b8a7a"),
        ("High performance", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
    ];

    public static GameModeReport Read()
    {
        var overrides = new List<GameModeOverride>();
        foreach (var (settingName, settingGuid) in Settings)
        {
            foreach (var (schemeName, schemeGuid) in Schemes)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{ProcessorSettingsKey}\{settingGuid}\{GameModeProfile}\{schemeGuid}");
                if (key is not null)
                {
                    overrides.Add(new GameModeOverride(settingName, schemeName, key.GetValue("ProvAcSettingIndex") is int v ? (uint)v : null));
                }
            }
        }

        return new GameModeReport(IsGameModeEnabled(), ReadActivePersonality(), overrides);
    }

    /// <summary>Settings → Gaming → Game Mode. Windows treats a missing value as "on".</summary>
    public static bool IsGameModeEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        return key?.GetValue("AutoGameModeEnabled") is not int value || value != 0;
    }

    private static int? ReadActivePersonality()
    {
        try
        {
            var scheme = PowerPlans.GetActive();
            var subgroup = NO_SUBGROUP_GUID;
            var setting = GUID_POWERSCHEME_PERSONALITY;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0 ? (int)value : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
