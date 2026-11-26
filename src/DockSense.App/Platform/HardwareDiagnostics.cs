using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>A device above a display adapter in the device tree: the port, switch or dock it hangs off.</summary>
internal sealed record AncestorDevice(string InstanceId, string Name, uint? RemovalPolicy, bool? InLocalMachineContainer, uint? Capabilities);

/// <summary>Facts about one display adapter that DockSense does not decide on, kept for the diagnostic report.</summary>
/// <param name="DedicatedMemoryBytes">The driver's "dedicated video memory": for an iGPU, the UMA frame buffer in effect.</param>
/// <param name="Ancestors">The devices it is connected through, nearest first.</param>
internal sealed record AdapterFacts(
    string? DriverVersion, DateTime? DriverDate, string? DriverProvider, long? DedicatedMemoryBytes,
    string? Location, IReadOnlyList<string> LocationPaths, string? BusDescription, uint? Capabilities,
    IReadOnlyList<AncestorDevice> Ancestors);

/// <summary>
/// Read-only facts for the diagnostic report that help to understand a device DockSense has not met yet: what it
/// is, how its graphics adapters are connected, and which vendor interfaces and vendor software it has. None of it
/// names the user: no system serial number, no user or computer name, no file paths.
/// </summary>
internal static partial class HardwareDiagnostics
{
    private const uint DIGCF_ALLCLASSES = 0x4;
    private const int MaxAncestors = 8;

    private static readonly DEVPROPKEY DriverVersionKey = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 3);
    private static readonly DEVPROPKEY DriverDateKey = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 2);
    private static readonly DEVPROPKEY DriverProviderKey = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 9);
    private static readonly DEVPROPKEY DriverKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 11);
    private static readonly DEVPROPKEY LocationInfoKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 15);
    private static readonly DEVPROPKEY CapabilitiesKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 17);
    private static readonly DEVPROPKEY LocationPathsKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 37);
    private static readonly DEVPROPKEY BusReportedDescriptionKey = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    private static readonly DEVPROPKEY ParentKey = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);

    /// <summary>
    /// Names of vendor tools and services that manage power, graphics or the handheld itself, and so may act on the
    /// same settings as DockSense. Windows' own iSCSI and installer services also start with "msi".
    /// </summary>
    [GeneratedRegex(
        @"asus|armou?ry|^rog|xg ?mobile|msi(?!server|scsi|exec)|claw|legion|lenovo|ayaneo|ayaspace|^gpd|onex|^amd|radeon|nvidia|^nv(container|display)|intel.*(graphics|arc)|^igcc|^g-?helper|handheld|^hhd|steam|playnite|anyfse|thunderbolt",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorName();

    /// <summary>ACPI devices every PC has: buses, buttons, processors, thermal zones.</summary>
    [GeneratedRegex(@"^ACPI\\(PNP0(?!C14)|ACPI0|AuthenticAMD|GenuineIntel|ThermalZone|FixedButton)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericAcpiDevice();

    // ------------------------------------------------------------------ The device itself

    /// <summary>Maker, model, firmware, processor, memory, battery and sleep type, as label and value.</summary>
    public static IReadOnlyList<(string Label, string Value)> DeviceFacts()
    {
        List<(string, string)> facts = [];
        using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
        {
            // Plain strings on the devices seen so far; a multi-string is taken by its first line.
            string Value(string name) => bios?.GetValue(name) switch
            {
                string text => text.Trim(),
                string[] lines => lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty,
                _ => string.Empty,
            };
            facts.Add(("maker / model", Join(Value("SystemManufacturer"), Value("SystemProductName"))));
            facts.Add(("family / SKU", Join(Value("SystemFamily"), Value("SystemSKU"))));
            facts.Add(("board", Join(Value("BaseBoardManufacturer"), Value("BaseBoardProduct"))));
            var ec = bios?.GetValue("ECFirmwareMajorRelease") is int major && bios.GetValue("ECFirmwareMinorRelease") is int minor
                ? $", EC {major}.{minor}"
                : string.Empty;
            facts.Add(("BIOS", $"{Join(Value("BIOSVersion"), Value("BIOSReleaseDate"))}{ec}"));
        }

        using (var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            var name = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "-";
            facts.Add(("processor", $"{name}, {Environment.ProcessorCount} logical processors"));
        }

        facts.Add(("memory", DescribeMemory()));
        facts.Add(("battery", DescribeBattery()));
        facts.Add(("sleep", DescribeSleep()));
        facts.Add(("touch", GetSystemMetrics(SM_MAXIMUMTOUCHES) is var touches and > 0 ? $"{touches} touch points" : "none"));
        return facts;
    }

    /// <summary>"26H2, build 26300.9974, Core": the release, the full build number and the edition.</summary>
    public static string WindowsRelease()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        var revision = key?.GetValue("UBR") is int ubr ? $".{ubr}" : string.Empty;
        return $"{key?.GetValue("DisplayVersion") as string ?? "?"}, build {build}{revision}, {key?.GetValue("EditionID") as string ?? "?"}";
    }

    private static string DescribeMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double? visibleGb = GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys / 1073741824.0 : null;
        double? installedGb = GetPhysicallyInstalledSystemMemory(out var kilobytes) ? kilobytes / 1048576.0 : null;
        var text = string.Create(CultureInfo.InvariantCulture, $"{Number(installedGb)} GB installed, {Number(visibleGb)} GB visible to Windows");

        // What the firmware keeps for itself is mostly the iGPU memory in effect.
        return installedGb is { } installed && visibleGb is { } visible
            ? string.Create(CultureInfo.InvariantCulture, $"{text} ({installed - visible:0.00} GB held back by the firmware)")
            : text;

        static string Number(double? value) => value is { } gb ? gb.ToString("0.00", CultureInfo.InvariantCulture) : "?";
    }

    private static string DescribeBattery()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return "unknown";
        }

        if (status.BatteryFlag == 0xFF)
        {
            return "unknown";
        }

        if ((status.BatteryFlag & 0x80) != 0)
        {
            return "none";
        }

        return status.BatteryLifePercent <= 100 ? $"present, {status.BatteryLifePercent} %" : "present";
    }

    /// <summary>Modern Standby matters here: on such PCs Windows hides High performance-type plans.</summary>
    private static string DescribeSleep()
    {
        // SYSTEM_POWER_CAPABILITIES: LidPresent at offset 2, SystemS3 at 5, AoAc (Modern Standby) at 20.
        var capabilities = new byte[76];
        if (CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, capabilities, (uint)capabilities.Length) != 0)
        {
            return "unknown";
        }

        var kind = capabilities[20] != 0 ? "Modern Standby (S0 low power idle)" : capabilities[5] != 0 ? "S3" : "no S3, no Modern Standby";
        return $"{kind}; lid {(capabilities[2] != 0 ? "present" : "none")}";
    }

    /// <summary>The Windows power mode (the slider in Settings), which is an overlay on the active plan.</summary>
    public static string PowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) != 0)
            {
                return "unknown";
            }

            var name = overlay.ToString() switch
            {
                "00000000-0000-0000-0000-000000000000" => "Balanced (no overlay)",
                "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best power efficiency",
                "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "Better performance",
                "ded574b5-45a0-4f42-8737-46345c09c238" => "Best performance",
                _ => "unknown overlay",
            };
            return $"{name} {{{overlay}}}";
        }
        catch (EntryPointNotFoundException)
        {
            return "unknown";
        }
    }

    // ------------------------------------------------------------------ Display adapters

    /// <summary>Driver, memory and connection facts of one adapter, or null when the device cannot be opened.</summary>
    public static AdapterFacts? DescribeAdapter(string instanceId) => WithDevice(instanceId, (set, data) =>
    {
        var driverDate = GpuProbe.ReadRaw(set, ref data, DriverDateKey) is { Length: >= 8 } raw
            ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(raw, 0))
            : (DateTime?)null;
        var paths = GpuProbe.ReadString(set, ref data, LocationPathsKey)?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];

        List<AncestorDevice> ancestors = [];
        var parent = GpuProbe.ReadString(set, ref data, ParentKey);
        while (!string.IsNullOrEmpty(parent) && ancestors.Count < MaxAncestors && !IsTreeRoot(parent))
        {
            var id = parent;
            var next = WithDevice(id, (parentSet, parentData) =>
            {
                ancestors.Add(new AncestorDevice(
                    id,
                    GpuProbe.ReadString(parentSet, ref parentData, DEVPKEY_Device_FriendlyName)
                        ?? GpuProbe.ReadString(parentSet, ref parentData, DEVPKEY_Device_DeviceDesc) ?? "?",
                    GpuProbe.ReadUInt32(parentSet, ref parentData, DEVPKEY_Device_RemovalPolicy),
                    GpuProbe.ReadBoolean(parentSet, ref parentData, DEVPKEY_Device_InLocalMachineContainer),
                    GpuProbe.ReadUInt32(parentSet, ref parentData, CapabilitiesKey)));
                return GpuProbe.ReadString(parentSet, ref parentData, ParentKey);
            });
            parent = next;
        }

        return new AdapterFacts(
            GpuProbe.ReadString(set, ref data, DriverVersionKey),
            driverDate,
            GpuProbe.ReadString(set, ref data, DriverProviderKey),
            DedicatedMemory(GpuProbe.ReadString(set, ref data, DriverKey)),
            GpuProbe.ReadString(set, ref data, LocationInfoKey),
            paths,
            GpuProbe.ReadString(set, ref data, BusReportedDescriptionKey),
            GpuProbe.ReadUInt32(set, ref data, CapabilitiesKey),
            ancestors);
    });

    /// <summary>The ACPI root and what is above it say nothing about how an adapter is connected.</summary>
    private static bool IsTreeRoot(string instanceId) =>
        instanceId.StartsWith(@"ACPI_HAL\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The memory size the display driver publishes for its adapter (<c>HardwareInformation.qwMemorySize</c>).</summary>
    private static long? DedicatedMemory(string? driverKey)
    {
        if (string.IsNullOrEmpty(driverKey))
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
        return (key?.GetValue("HardwareInformation.qwMemorySize") ?? key?.GetValue("HardwareInformation.MemorySize")) switch
        {
            long size => size,
            int size => unchecked((uint)size),
            byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
            byte[] { Length: >= 4 } bytes => BitConverter.ToUInt32(bytes, 0),
            _ => null,
        };
    }

    private static T? WithDevice<T>(string instanceId, Func<IntPtr, SP_DEVINFO_DATA, T?> read)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE)
        {
            return default;
        }

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            return SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data) ? read(set, data) : default;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    // ------------------------------------------------------------------ Vendor interfaces and software

    /// <summary>
    /// ACPI devices that are specific to this maker or chipset, with their names: the firmware interfaces vendor
    /// tools talk to (ASUS' ATK device, WMI-ACPI bridges and so on). Generic ones every PC has are left out.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> VendorAcpiDevices()
    {
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "ACPI", IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE)
        {
            return [];
        }

        try
        {
            SortedDictionary<string, string> devices = new(StringComparer.OrdinalIgnoreCase);
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var instanceId = GpuProbe.ReadString(set, ref data, DEVPKEY_Device_InstanceId);
                if (string.IsNullOrEmpty(instanceId) || GenericAcpiDevice().IsMatch(instanceId))
                {
                    continue;
                }

                // "ACPI\ASUS2018\2&DABA3FF&0" -> "ACPI\ASUS2018". A WMI-ACPI bridge keeps its last part: it is the
                // name the firmware gave that bridge ("ATK", "AOD"), not an address.
                var parts = instanceId.Split('\\');
                var id = parts.Length >= 3 && !parts[1].Equals("PNP0C14", StringComparison.OrdinalIgnoreCase)
                    ? $@"{parts[0]}\{parts[1]}"
                    : instanceId;
                devices.TryAdd(id, GpuProbe.ReadString(set, ref data, DEVPKEY_Device_FriendlyName)
                    ?? GpuProbe.ReadString(set, ref data, DEVPKEY_Device_DeviceDesc) ?? "?");
            }

            return devices.Select(d => (d.Key, d.Value)).ToList();
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Installed services of hardware vendors and handheld tools, with how each one starts.</summary>
    public static IReadOnlyList<string> VendorServices()
    {
        List<string> found = [];
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in services?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var service = services!.OpenSubKey(name);

                // 0x10 and 0x20 are services in a process of their own or a shared one; the rest are drivers.
                if (service?.GetValue("Type") is not int type || (type & 0x30) == 0)
                {
                    continue;
                }

                // "@oem56.inf,%ServiceDisplayName%;Armoury Crate Control Interface" -> the readable part.
                var display = service.GetValue("DisplayName") as string ?? string.Empty;
                display = display.StartsWith('@') ? display[(display.LastIndexOf(';') + 1)..] : display;
                display = display.StartsWith('@') ? string.Empty : display; // a resource reference without a readable part
                if (!VendorName().IsMatch(name) && !VendorName().IsMatch(display))
                {
                    continue;
                }

                var start = service.GetValue("Start") switch { 2 => "automatic", 3 => "manual", 4 => "disabled", _ => "?" };
                found.Add(display.Length == 0 || display.Equals(name, StringComparison.OrdinalIgnoreCase) ? $"{name} ({start})" : $"{name}: {display} ({start})");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A service whose key this user may not read: nothing to report about it.
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>Names of running programs of hardware vendors and handheld tools.</summary>
    public static IReadOnlyList<string> VendorProcesses()
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (VendorName().IsMatch(process.ProcessName))
                {
                    names.Add(process.ProcessName);
                }
            }
        }

        return [.. names];
    }

    /// <summary>The values of a registry key under the user's hive as "name = value", for settings Windows keeps there.</summary>
    public static IReadOnlyList<string> UserRegistryValues(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key is null ? [] : key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).Select(name => $"{name} = {key.GetValue(name)}").ToList();
    }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 3);
        foreach (var value in bytes)
        {
            text.Append(CultureInfo.InvariantCulture, $"{value:X2} ");
        }

        return text.ToString().TrimEnd();
    }

    private static string Join(string first, string second) =>
        (first.Length, second.Length) switch
        {
            (0, 0) => "-",
            (_, 0) => first,
            (0, _) => second,
            _ => $"{first} {second}",
        };

    // ------------------------------------------------------------------ Native

    private const int SM_MAXIMUMTOUCHES = 95;
    private const int SystemPowerCapabilities = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, byte[] outputBuffer, uint outputBufferLength);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);
}
