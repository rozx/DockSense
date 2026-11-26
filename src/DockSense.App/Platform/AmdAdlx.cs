using System.Globalization;
using System.Runtime.InteropServices;
using DockSense.Core;

namespace DockSense.App.Platform;

/// <summary>One Variable Graphics Memory choice the AMD driver offers.</summary>
/// <param name="Name">The driver's own name for it.</param>
/// <param name="Mode">ADLX_VARIABLE_GRAPHICS_MEMORY_MODE: 0 = auto (a preset of the driver), 1 = custom (a size).</param>
/// <param name="CarvedGb">Memory set aside for the iGPU, in GB.</param>
/// <param name="RemainingGb">Memory left for Windows, in GB.</param>
internal sealed record AdlxMemoryOption(string Name, int Mode, double CarvedGb, double RemainingGb)
{
    public bool IsCustom => Mode == 1;

    /// <summary>The size in MB, or null when the driver did not tell it.</summary>
    public int? CarvedMb => double.IsFinite(CarvedGb) && CarvedGb is >= 0 and < 1024 ? (int)Math.Round(CarvedGb * 1024) : null;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{Name} (mode {Mode}, {CarvedGb:0.##} GB for the iGPU, {RemainingGb:0.##} GB left)");
}

/// <summary>What the AMD driver says about Variable Graphics Memory.</summary>
/// <param name="AdlxVersion">The driver's ADLX version.</param>
/// <param name="Supported">Whether the driver offers the setting on this device.</param>
internal sealed record AdlxMemoryReport(
    string AdlxVersion, bool Supported, AdlxMemoryOption? Current, AdlxMemoryOption? Default, IReadOnlyList<AdlxMemoryOption> Options)
{
    /// <summary>
    /// The sizes DockSense offers from this: the driver's custom sizes, or every size it lists when it marks none
    /// as custom. Null when there is nothing to choose from or the stored size is unknown.
    /// </summary>
    public IgpuMemoryOptions? ToIgpuMemoryOptions()
    {
        if (!Supported)
        {
            return null;
        }

        var custom = Options.Where(o => o.IsCustom).ToList();
        var sizes = (custom.Count > 0 ? custom : Options).Select(o => o.CarvedMb ?? 0);
        return IgpuMemoryOptions.FromDriverSizes(sizes, Current?.CarvedMb);
    }
}

/// <summary>
/// AMD's ADLX library from the Radeon driver (<c>amdadlx64.dll</c>), used here only for Variable Graphics Memory:
/// the iGPU memory setting of AMD Software, which also exists on devices without the ASUS firmware interface (see
/// <see cref="AsusAcpi"/>), such as the MSI Claw A8. <b>Experimental</b>: written from AMD's SDK and never run on
/// a device whose driver offers the setting.
/// </summary>
/// <remarks>
/// ADLX objects are C++ interfaces; methods are called through their vtable slot, taken from the C declarations in
/// the ADLX SDK (<c>SDK/Include/ISystem.h</c> and <c>ISystem3.h</c>). Every use starts ADLX, does its work and
/// shuts it down again, on a thread of its own: ADLX wants all its calls on one thread, and a driver that hangs
/// must not take DockSense with it.
/// </remarks>
internal static class AmdAdlx
{
    private const string Library = "amdadlx64.dll";
    private static readonly TimeSpan ReadPatience = TimeSpan.FromSeconds(15);

    // The driver may show a prompt of its own or start a restart: give it time before calling it a failure.
    private static readonly TimeSpan WritePatience = TimeSpan.FromSeconds(30);

    // Slots 0 to 2 of every interface but IADLXSystem are Acquire, Release and QueryInterface.
    private const int Release = 1;
    private const int SystemQueryInterface = 2;      // IADLXSystem (not reference counted)
    private const int GetVariableGraphicsMemory = 6; // IADLXSystem3
    private const int VgmIsSupported = 3;            // IADLXVariableGraphicsMemory
    private const int VgmGetDefaultOption = 4;
    private const int VgmGetOption = 5;
    private const int VgmGetAvailableOptions = 6;
    private const int VgmSetOption = 7;
    private const int OptionName = 3;                // IADLXVariableGraphicsMemoryOption
    private const int OptionMode = 4;
    private const int OptionCarved = 5;
    private const int OptionRemaining = 6;
    private const int ListSize = 3;                  // IADLXVariableGraphicsMemoryOptionList
    private const int ListAt = 11;

    /// <summary>
    /// Development builds only: with the environment variable <c>DOCKSENSE_SIMULATE_AMD_VGM=1</c>, DockSense
    /// behaves as on a device without the ASUS interface whose AMD driver offers 0.5 to 16 GB, so that the
    /// experimental path can be looked at on other hardware. Nothing is read from or stored in the real driver,
    /// and storing always fails, so no restart follows.
    /// </summary>
    internal static bool Simulated =>
#if DEBUG
        Environment.GetEnvironmentVariable("DOCKSENSE_SIMULATE_AMD_VGM") == "1";
#else
        false;
#endif

    /// <summary>
    /// Reads the driver's Variable Graphics Memory state. Returns null when it cannot be read;
    /// <paramref name="note"/> then says why (no AMD driver, an older driver, an error). Changes nothing.
    /// </summary>
    public static AdlxMemoryReport? ReadVariableGraphicsMemory(out string note)
    {
        if (Simulated)
        {
            note = string.Empty;
            double[] sizes = [0.5, 1, 2, 3, 4, 6, 8, 12, 16];
            List<AdlxMemoryOption> options = [new("Minimum", 0, 0.5, 23.5), new("Medium", 0, 4, 20), new("High", 0, 8, 16)];
            options.AddRange(sizes.Select(gb => new AdlxMemoryOption("Custom", 1, gb, 24 - gb)));
            return new AdlxMemoryReport("simulated", Supported: true, options[1], options[1], options);
        }

        var (report, problem) = OnOwnThread<AdlxMemoryReport>(ReadPatience, () => WithMemoryInterface<AdlxMemoryReport>((vgm, version) =>
        {
            var result = Method<GetFlag>(vgm, VgmIsSupported)(vgm, out var supported);
            if (result != 0)
            {
                return ((AdlxMemoryReport?)null, $"ADLX {version}: IsSupported returned {result}");
            }

            return supported == 0
                ? (new AdlxMemoryReport(version, Supported: false, null, null, []), string.Empty)
                : (new AdlxMemoryReport(
                    version, Supported: true, ReadOption(vgm, VgmGetOption), ReadOption(vgm, VgmGetDefaultOption), ReadOptions(vgm)), string.Empty);
        }));
        note = problem;
        return report;
    }

    /// <summary>
    /// Stores a Variable Graphics Memory size, choosing the driver's own option of that size (a custom one when
    /// there is a choice). Returns false with the reason in <paramref name="note"/> when the driver does not offer
    /// the size or refuses.
    /// </summary>
    /// <remarks>
    /// AMD documents <c>SetOption</c> as restarting the PC by itself, so this may not return at all. Only ever call
    /// it after the user agreed to a restart.
    /// </remarks>
    public static bool SetVariableGraphicsMemory(int megabytes, out string note)
    {
        if (Simulated)
        {
            note = "simulated driver: nothing is stored";
            return false;
        }

        var (stored, problem) = OnOwnThread<bool>(WritePatience, () => WithMemoryInterface<bool>((vgm, version) =>
        {
            if (Method<GetFlag>(vgm, VgmIsSupported)(vgm, out var supported) != 0 || supported == 0)
            {
                return (false, $"ADLX {version}: Variable Graphics Memory is not offered");
            }

            if (Method<GetObject>(vgm, VgmGetAvailableOptions)(vgm, out var list) != 0 || list == IntPtr.Zero)
            {
                return (false, $"ADLX {version}: the driver lists no options");
            }

            var chosen = IntPtr.Zero;
            try
            {
                var chosenIsCustom = false;
                var count = Method<GetCount>(list, ListSize)(list);
                for (uint i = 0; i < count; i++)
                {
                    if (Method<GetItem>(list, ListAt)(list, i, out var option) != 0 || option == IntPtr.Zero)
                    {
                        continue;
                    }

                    var described = Describe(option);
                    if (described.CarvedMb == megabytes && (chosen == IntPtr.Zero || (described.IsCustom && !chosenIsCustom)))
                    {
                        Free(chosen);
                        chosen = option;
                        chosenIsCustom = described.IsCustom;
                    }
                    else
                    {
                        Free(option);
                    }
                }

                if (chosen == IntPtr.Zero)
                {
                    return (false, $"ADLX {version}: the driver does not offer {megabytes} MB");
                }

                var result = Method<SetObject>(vgm, VgmSetOption)(vgm, chosen);
                return result == 0 ? (true, string.Empty) : (false, $"ADLX {version}: SetOption returned {result}");
            }
            finally
            {
                Free(chosen);
                Free(list);
            }
        }));
        note = problem;
        return stored;
    }

    /// <summary>Starts ADLX, hands <paramref name="use"/> the Variable Graphics Memory interface and shuts ADLX down.</summary>
    private static (T? Value, string Problem) WithMemoryInterface<T>(Func<IntPtr, string, (T? Value, string Problem)> use)
    {
        var result = ADLXQueryFullVersion(out var fullVersion);
        if (result != 0)
        {
            return (default, $"ADLXQueryFullVersion returned {result}");
        }

        var version = $"{fullVersion >> 48}.{(fullVersion >> 32) & 0xFFFF}.{(fullVersion >> 16) & 0xFFFF}.{fullVersion & 0xFFFF}";

        // Not ADLXInitialize: that one treats an OEM's Radeon driver (MSI's, for one) as unknown.
        result = ADLXInitializeWithIncompatibleDriver(fullVersion, out var system);
        if (result is not (0 or 1 or 2) || system == IntPtr.Zero)
        {
            return (default, $"ADLX {version} did not start (result {result})");
        }

        IntPtr system3 = IntPtr.Zero, vgm = IntPtr.Zero;
        try
        {
            result = Method<QueryInterface>(system, SystemQueryInterface)(system, "IADLXSystem3", out system3);
            if (result != 0 || system3 == IntPtr.Zero)
            {
                return (default, $"ADLX {version} has no Variable Graphics Memory (no IADLXSystem3, result {result})");
            }

            result = Method<GetObject>(system3, GetVariableGraphicsMemory)(system3, out vgm);
            return result != 0 || vgm == IntPtr.Zero
                ? (default, $"ADLX {version}: GetVariableGraphicsMemory returned {result}")
                : use(vgm, version);
        }
        finally
        {
            // ADLX must not be shut down while interfaces are still held.
            Free(vgm);
            Free(system3);
            _ = ADLXTerminate();
        }
    }

    private static (T? Value, string Problem) OnOwnThread<T>(TimeSpan patience, Func<(T? Value, string Problem)> work)
    {
        (T? Value, string Problem) outcome = (default, string.Empty);
        var thread = new Thread(() =>
        {
            try
            {
                outcome = work();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                outcome = (default, NoDriver);
            }
        })
        { IsBackground = true, Name = "ADLX" };
        thread.Start();
        return thread.Join(patience) ? outcome : (default, $"the AMD driver did not answer within {patience.TotalSeconds:0} s");
    }

    /// <summary>The note of a read on a PC without the AMD driver: nothing to try again later.</summary>
    public const string NoDriver = "no AMD driver with ADLX";

    private static AdlxMemoryOption? ReadOption(IntPtr vgm, int slot)
    {
        if (Method<GetObject>(vgm, slot)(vgm, out var option) != 0 || option == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Describe(option);
        }
        finally
        {
            Free(option);
        }
    }

    private static List<AdlxMemoryOption> ReadOptions(IntPtr vgm)
    {
        List<AdlxMemoryOption> options = [];
        if (Method<GetObject>(vgm, VgmGetAvailableOptions)(vgm, out var list) != 0 || list == IntPtr.Zero)
        {
            return options;
        }

        try
        {
            var count = Method<GetCount>(list, ListSize)(list);
            for (uint i = 0; i < count; i++)
            {
                if (Method<GetItem>(list, ListAt)(list, i, out var option) != 0 || option == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    options.Add(Describe(option));
                }
                finally
                {
                    Free(option);
                }
            }
        }
        finally
        {
            Free(list);
        }

        return options;
    }

    /// <summary>Reads an option's fields. The name belongs to the option, so it is copied before the option is released.</summary>
    private static AdlxMemoryOption Describe(IntPtr option)
    {
        var name = Method<GetObject>(option, OptionName)(option, out var text) == 0 && text != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(text) ?? "?"
            : "?";
        var mode = Method<GetNumber>(option, OptionMode)(option, out var value) == 0 ? value : -1;
        var carved = Method<GetSize>(option, OptionCarved)(option, out var carvedGb) == 0 ? carvedGb : double.NaN;
        var remaining = Method<GetSize>(option, OptionRemaining)(option, out var remainingGb) == 0 ? remainingGb : double.NaN;
        return new AdlxMemoryOption(name, mode, carved, remaining);
    }

    private static void Free(IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            _ = Method<ReleaseObject>(instance, Release)(instance);
        }
    }

    private static T Method<T>(IntPtr instance, int slot)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterface(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string interfaceId, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetObject(IntPtr self, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetObject(IntPtr self, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFlag(IntPtr self, out byte value); // adlx_bool is a C++ bool

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNumber(IntPtr self, out int value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSize(IntPtr self, out double value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetCount(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetItem(IntPtr self, uint index, out IntPtr item);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseObject(IntPtr self);

    // From System32 only, where the driver installs it: never a copy next to DockSense or on the PATH.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADLXQueryFullVersion(out ulong fullVersion);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADLXInitializeWithIncompatibleDriver(ulong version, out IntPtr system);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ADLXTerminate();
}
