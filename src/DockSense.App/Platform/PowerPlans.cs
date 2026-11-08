using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

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
    /// Creates "DockSense Performance": a copy of High Performance (personality 1), so the GameMode PPM overrides
    /// provisioned for Balanced-type plans do not apply. Min processor state is lowered to 5% to avoid idle heat,
    /// and game threads prefer performance cores.
    /// </summary>
    public static Guid CreateDockSensePerformancePlan()
    {
        var source = HighPerformance;
        var destination = IntPtr.Zero;
        Check(PowerDuplicateScheme(IntPtr.Zero, ref source, ref destination), "PowerDuplicateScheme");
        Guid plan;
        try
        {
            plan = Marshal.PtrToStructure<Guid>(destination);
        }
        finally
        {
            LocalFree(destination);
        }

        WriteString(plan, DockSensePlanName, PowerWriteFriendlyName, "PowerWriteFriendlyName");
        WriteString(plan, DockSensePlanDescription, PowerWriteDescription, "PowerWriteDescription");
        WriteProcessorValue(plan, MinProcessorState, 5);
        WriteProcessorValue(plan, MinProcessorStateClass1, 5);
        WriteProcessorValue(plan, HeteroThreadPolicy, 2);       // prefer performant processors
        WriteProcessorValue(plan, HeteroShortThreadPolicy, 2);  // prefer performant processors
        return plan;
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
