using System.Globalization;
using System.Runtime.InteropServices;

namespace DockSense.App.Platform;

/// <summary>One Variable Graphics Memory choice the AMD driver offers.</summary>
/// <param name="Name">The driver's own name for it.</param>
/// <param name="Mode">ADLX_VARIABLE_GRAPHICS_MEMORY_MODE: 0 = auto, 1 = custom.</param>
/// <param name="CarvedGb">Memory set aside for the iGPU, in GB.</param>
/// <param name="RemainingGb">Memory left for Windows, in GB.</param>
internal sealed record AdlxMemoryOption(string Name, int Mode, double CarvedGb, double RemainingGb)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{Name} (mode {Mode}, {CarvedGb:0.##} GB for the iGPU, {RemainingGb:0.##} GB left)");
}

/// <summary>What the AMD driver says about Variable Graphics Memory.</summary>
/// <param name="AdlxVersion">The driver's ADLX version.</param>
/// <param name="Supported">Whether the driver offers the setting on this device.</param>
internal sealed record AdlxMemoryReport(
    string AdlxVersion, bool Supported, AdlxMemoryOption? Current, AdlxMemoryOption? Default, IReadOnlyList<AdlxMemoryOption> Options);

/// <summary>
/// AMD's ADLX library from the Radeon driver (<c>amdadlx64.dll</c>), used here only to read Variable Graphics
/// Memory: the iGPU memory setting of AMD Software, which also exists on devices without the ASUS firmware
/// interface (see <see cref="AsusAcpi"/>). Reading changes nothing.
/// </summary>
/// <remarks>
/// ADLX objects are C++ interfaces; methods are called through their vtable slot, taken from the C declarations in
/// the ADLX SDK (<c>SDK/Include/ISystem.h</c> and <c>ISystem3.h</c>). <c>IADLXVariableGraphicsMemory::SetOption</c>
/// (slot 7) is deliberately not declared here: AMD documents it as restarting the PC by itself.
/// </remarks>
internal static class AmdAdlx
{
    private const string Library = "amdadlx64.dll";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    // Slots 0 to 2 of every interface but IADLXSystem are Acquire, Release and QueryInterface.
    private const int Release = 1;
    private const int SystemQueryInterface = 2;      // IADLXSystem (not reference counted)
    private const int GetVariableGraphicsMemory = 6; // IADLXSystem3
    private const int VgmIsSupported = 3;            // IADLXVariableGraphicsMemory
    private const int VgmGetDefaultOption = 4;
    private const int VgmGetOption = 5;
    private const int VgmGetAvailableOptions = 6;
    private const int OptionName = 3;                // IADLXVariableGraphicsMemoryOption
    private const int OptionMode = 4;
    private const int OptionCarved = 5;
    private const int OptionRemaining = 6;
    private const int ListSize = 3;                  // IADLXVariableGraphicsMemoryOptionList
    private const int ListAt = 11;

    /// <summary>
    /// Reads the driver's Variable Graphics Memory state. Returns null when it cannot be read;
    /// <paramref name="note"/> then says why (no AMD driver, an older driver, an error).
    /// </summary>
    public static AdlxMemoryReport? ReadVariableGraphicsMemory(out string note)
    {
        AdlxMemoryReport? report = null;
        var problem = string.Empty;

        // On a thread of its own: ADLX wants all its calls on one thread, and a driver that hangs must not take
        // the caller with it.
        var thread = new Thread(() =>
        {
            try
            {
                report = Read(out problem);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                problem = "no AMD driver with ADLX";
            }
        })
        { IsBackground = true, Name = "ADLX" };
        thread.Start();
        if (!thread.Join(Patience))
        {
            note = $"the AMD driver did not answer within {Patience.TotalSeconds:0} s";
            return null;
        }

        note = problem;
        return report;
    }

    private static AdlxMemoryReport? Read(out string problem)
    {
        var result = ADLXQueryFullVersion(out var fullVersion);
        if (result != 0)
        {
            problem = $"ADLXQueryFullVersion returned {result}";
            return null;
        }

        var version = $"{fullVersion >> 48}.{(fullVersion >> 32) & 0xFFFF}.{(fullVersion >> 16) & 0xFFFF}.{fullVersion & 0xFFFF}";

        // Not ADLXInitialize: that one treats an OEM's Radeon driver (MSI's, for one) as unknown.
        result = ADLXInitializeWithIncompatibleDriver(fullVersion, out var system);
        if (result is not (0 or 1 or 2) || system == IntPtr.Zero)
        {
            problem = $"ADLX {version} did not start (result {result})";
            return null;
        }

        IntPtr system3 = IntPtr.Zero, vgm = IntPtr.Zero;
        try
        {
            result = Method<QueryInterface>(system, SystemQueryInterface)(system, "IADLXSystem3", out system3);
            if (result != 0 || system3 == IntPtr.Zero)
            {
                problem = $"ADLX {version} has no Variable Graphics Memory (no IADLXSystem3, result {result})";
                return null;
            }

            result = Method<GetObject>(system3, GetVariableGraphicsMemory)(system3, out vgm);
            if (result != 0 || vgm == IntPtr.Zero)
            {
                problem = $"ADLX {version}: GetVariableGraphicsMemory returned {result}";
                return null;
            }

            result = Method<GetFlag>(vgm, VgmIsSupported)(vgm, out var supported);
            if (result != 0)
            {
                problem = $"ADLX {version}: IsSupported returned {result}";
                return null;
            }

            if (supported == 0)
            {
                problem = string.Empty;
                return new AdlxMemoryReport(version, Supported: false, null, null, []);
            }

            problem = string.Empty;
            return new AdlxMemoryReport(
                version, Supported: true, ReadOption(vgm, VgmGetOption), ReadOption(vgm, VgmGetDefaultOption), ReadOptions(vgm));
        }
        finally
        {
            // ADLX must not be shut down while interfaces are still held.
            Free(vgm);
            Free(system3);
            _ = ADLXTerminate();
        }
    }

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
