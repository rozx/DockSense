using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using DockSense.Core;
using Microsoft.Win32.SafeHandles;

namespace DockSense.App.Platform;

/// <summary>
/// The ASUS ATK ACPI device (<c>\\.\ATKACPI</c>), used here for the iGPU memory (UMA frame buffer) setting and for
/// re-sending the performance mode. Standard users can open it; no admin rights are needed. Writes are limited to
/// values the firmware lists itself, and for the mode to the three every model has.
/// </summary>
internal static class AsusAcpi
{
    private const string DevicePath = @"\\.\ATKACPI";
    private const uint ControlCode = 0x0022240C;
    private const uint Dsts = 0x53545344; // get device status
    private const uint Devs = 0x53564544; // set device status
    private const uint VramMemory = 0x000600C4;
    private const uint PerformanceMode = 0x00120075;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE = 3;
    private const uint OPEN_EXISTING = 3;

    /// <summary>The iGPU memory options, or null when this device does not offer the setting.</summary>
    public static IgpuMemoryOptions? ReadIgpuMemory() =>
        QueryIgpuMemory(out _) is { } reply ? IgpuMemoryOptions.Parse(reply) : null;

    /// <summary>
    /// The firmware's raw reply to the iGPU memory query (64 bytes), or null with the reason in
    /// <paramref name="why"/>. The diagnostic report shows it, so that a layout DockSense does not know yet can be
    /// worked out from a report.
    /// </summary>
    public static byte[]? QueryIgpuMemory(out string why)
    {
        using var device = Open();
        if (device is null)
        {
            why = "no ASUS ATK ACPI device";
            return null;
        }

        if (!IsAmdCpu())
        {
            why = "ASUS ATK ACPI device present, not an AMD processor"; // Intel ASUS laptops use a different mechanism.
            return null;
        }

        var input = new byte[20];
        BitConverter.GetBytes(Dsts).CopyTo(input, 0);
        BitConverter.GetBytes(12u).CopyTo(input, 4);
        BitConverter.GetBytes(VramMemory).CopyTo(input, 8);
        var output = new byte[64];
        if (!Control(device, input, output))
        {
            why = $"ASUS ATK ACPI device present, the query failed (error {Marshal.GetLastWin32Error()})";
            return null;
        }

        why = string.Empty;
        return output;
    }

    /// <summary>
    /// Stores a new iGPU memory size in the firmware; it applies at the next restart. The value is re-validated
    /// against the firmware's own option list first. Returns true on success.
    /// </summary>
    public static bool WriteIgpuMemory(uint raw)
    {
        if (ReadIgpuMemory() is not { } options || !options.RawValues.Contains(raw))
        {
            Log.Warn($"Refusing to write iGPU memory value 0x{raw:X}: the firmware does not offer it.");
            return false;
        }

        return Set(VramMemory, raw);
    }

    /// <summary>
    /// Sends a performance mode (0 Performance, 1 Turbo, 2 Silent), as Armoury Crate does when its mode is
    /// switched. The firmware loads that mode's own power limits again, which is how DockSense gives the TDP back
    /// after an override. Returns true when the firmware accepted it. Measured on the RC74XA: Silent holds 13 W,
    /// Performance settles at 17 W, Turbo runs at about 45 W; any other value (4, "Manual" on some laptops)
    /// behaves as Performance, so only these three are sent.
    /// </summary>
    public static bool WritePerformanceMode(uint mode) => mode <= 2 && Set(PerformanceMode, mode);

    private static bool Set(uint deviceId, uint value)
    {
        using var device = Open();
        if (device is null)
        {
            return false;
        }

        var input = new byte[16];
        BitConverter.GetBytes(Devs).CopyTo(input, 0);
        BitConverter.GetBytes(8u).CopyTo(input, 4);
        BitConverter.GetBytes(deviceId).CopyTo(input, 8);
        BitConverter.GetBytes(value).CopyTo(input, 12);
        var output = new byte[16];
        return Control(device, input, output) && BitConverter.ToInt32(output, 0) == 1;
    }

    private static bool IsAmdCpu()
    {
        if (!X86Base.IsSupported)
        {
            return false;
        }

        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<byte> vendor = stackalloc byte[12];
        BitConverter.TryWriteBytes(vendor, ebx);
        BitConverter.TryWriteBytes(vendor[4..], edx);
        BitConverter.TryWriteBytes(vendor[8..], ecx);
        return System.Text.Encoding.ASCII.GetString(vendor) == "AuthenticAMD";
    }

    private static SafeFileHandle? Open()
    {
        var handle = CreateFileW(DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null; // Not an ASUS device, or the ATK driver is not installed.
        }

        return handle;
    }

    private static bool Control(SafeFileHandle device, byte[] input, byte[] output) =>
        DeviceIoControl(device, ControlCode, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, byte[] inBuffer, uint inBufferSize, byte[] outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
