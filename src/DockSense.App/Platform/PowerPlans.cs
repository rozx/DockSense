using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DockSense.Core;
using Microsoft.Win32;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>A power plan as listed in the plan editor.</summary>
/// <param name="Personality">0 = Power saver, 1 = High performance, 2 = Balanced; null if unknown.</param>
public sealed record PlanInfo(Guid Id, string Name, int? Personality);

/// <summary>Windows power plan (power scheme) operations through powrprof.dll. No admin rights needed.</summary>
internal static class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public const string DockSensePlanName = "DockSense Performance";

    private const string DockSensePlanDescription =
        "Created by DockSense. Based on High Performance, so Windows Game Mode's GameMode power profile " +
        "(EPP=100, prefer efficient cores on some AMD handhelds) does not apply. Used while an eGPU is connected.";

    // Processor settings tuned on the DockSense plan (High Performance defaults are kept otherwise, e.g. EPP 0).
    private static readonly Guid MinProcessorState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid MinProcessorStateClass1 = new("893dee8e-2bef-41e0-89c6-b55d0929964d");
    private static readonly Guid HeteroThreadPolicy = new("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
    private static readonly Guid HeteroShortThreadPolicy = new("bae08b81-2d5e-4688-ad6a-13243356654b");

    public static Guid GetActive()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer), "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public static void SetActive(Guid scheme) => Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme");

    /// <summary>The plan's display name, or null when no such plan exists. Works for hidden plans too.</summary>
    public static string? GetName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    public static bool Exists(Guid scheme) => GetName(scheme) is not null;

    public static string Describe(Guid scheme) => $"{GetName(scheme) ?? "(unknown plan)"} {{{scheme}}}";

    /// <summary>
    /// Plans Windows lists for selection. On Modern Standby devices this can omit hidden High Performance-type plans,
    /// including DockSense's own.
    /// </summary>
    public static IReadOnlyList<(Guid Id, string Name)> List()
    {
        var result = new List<(Guid, string)>();
        for (uint index = 0; ; index++)
        {
            uint size = 16;
            var buffer = new byte[size];
            var error = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref size);
            if (error == ERROR_NO_MORE_ITEMS)
            {
                return result;
            }

            Check(error, "PowerEnumerate");
            var id = new Guid(buffer);
            result.Add((id, GetName(id) ?? id.ToString()));
        }
    }

    /// <summary>
    /// Every power plan on the system, including the hidden High Performance-type plans that <see cref="List"/>
    /// omits on Modern Standby devices (read from the registry, which needs no admin rights). Power mode overlays
    /// are left out.
    /// </summary>
    public static IReadOnlyList<PlanInfo> ListAll()
    {
        var ids = new List<Guid>();
        try
        {
            ids.AddRange(List().Select(p => p.Id));
        }
        catch (Win32Exception ex)
        {
            Log.Warn($"Could not enumerate power plans: {ex.Message}");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                if (Guid.TryParse(name, out var id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not read the power scheme list from the registry: {ex.Message}");
        }

        return ids
            .Where(id => !PowerSettingCatalog.Overlays.Contains(id))
            .Select(id => GetName(id) is { } name ? new PlanInfo(id, name, GetPersonality(id)) : null)
            .OfType<PlanInfo>()
            .ToList();
    }

    /// <summary>0 = Power saver, 1 = High performance, 2 = Balanced; null if unknown.</summary>
    public static int? GetPersonality(Guid scheme)
    {
        var subgroup = NO_SUBGROUP_GUID;
        var setting = GUID_POWERSCHEME_PERSONALITY;
        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0 ? (int)value : null;
    }

    /// <summary>Creates a plan as a copy of <paramref name="source"/> with a new name, and returns its GUID.</summary>
    public static Guid Create(string name, Guid source)
    {
        var plan = Duplicate(source);
        WriteString(plan, name, PowerWriteFriendlyName, "PowerWriteFriendlyName");
        WriteString(plan, $"Created with DockSense from {GetName(source) ?? source.ToString()}.", PowerWriteDescription, "PowerWriteDescription");
        return plan;
    }

    public static void Rename(Guid scheme, string name) => WriteString(scheme, name, PowerWriteFriendlyName, "PowerWriteFriendlyName");

    /// <summary>Deletes a plan. Windows refuses to delete the active plan.</summary>
    public static void Delete(Guid scheme) => Check(PowerDeleteScheme(IntPtr.Zero, ref scheme), "PowerDeleteScheme");

    /// <summary>Reads a setting's plugged-in (<paramref name="ac"/>) or on-battery value; null if the plan does not have it.</summary>
    public static uint? ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        var error = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out value);
        return error == 0 ? value : null;
    }

    public static void WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value) => Check(
        ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value)
           : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value),
        ac ? "PowerWriteACValueIndex" : "PowerWriteDCValueIndex");

    /// <summary>Changes to the active plan only take effect once it is activated again.</summary>
    public static void ReapplyIfActive(Guid scheme)
    {
        if (GetActive() == scheme)
        {
            SetActive(scheme);
        }
    }

    /// <summary>
    /// Creates "DockSense Performance": a copy of High Performance (personality 1), so the GameMode PPM overrides
    /// provisioned for Balanced-type plans do not apply. Min processor state is lowered to 5% to avoid idle heat,
    /// and game threads prefer performance cores.
    /// </summary>
    public static Guid CreateDockSensePerformancePlan()
    {
        var plan = Duplicate(HighPerformance);
        WriteString(plan, DockSensePlanName, PowerWriteFriendlyName, "PowerWriteFriendlyName");
        WriteString(plan, DockSensePlanDescription, PowerWriteDescription, "PowerWriteDescription");
        WriteProcessorValue(plan, MinProcessorState, 5);
        WriteProcessorValue(plan, MinProcessorStateClass1, 5);
        WriteProcessorValue(plan, HeteroThreadPolicy, 2);       // prefer performant processors
        WriteProcessorValue(plan, HeteroShortThreadPolicy, 2);  // prefer performant processors
        return plan;
    }

    private static Guid Duplicate(Guid source)
    {
        var destination = IntPtr.Zero;
        Check(PowerDuplicateScheme(IntPtr.Zero, ref source, ref destination), "PowerDuplicateScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(destination);
        }
        finally
        {
            LocalFree(destination);
        }
    }

    private delegate uint StringWriter(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

    private static void WriteString(Guid scheme, string value, StringWriter writer, string apiName)
    {
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        Check(writer(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length), apiName);
    }

    private static void WriteProcessorValue(Guid scheme, Guid setting, uint value)
    {
        var subgroup = GUID_PROCESSOR_SETTINGS_SUBGROUP;
        Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value), "PowerWriteACValueIndex");
        Check(PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, value), "PowerWriteDCValueIndex");
    }

    private static void Check(uint error, string apiName)
    {
        if (error != 0)
        {
            throw new Win32Exception((int)error, $"{apiName} failed");
        }
    }
}
