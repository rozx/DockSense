using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DockSense.App.Platform;

/// <summary>
/// The PawnIO driver (https://pawnio.eu/): a signed kernel driver that runs small signed modules for hardware
/// access. DockSense uses its AMD SMU module to set the processor's power limits. The driver is not part of
/// DockSense, and it only talks to administrators.
/// </summary>
internal sealed class PawnIo : IDisposable
{
    public const int ErrorAccessDenied = 5;

    private const uint DeviceType = 41394u << 16;
    private const uint ExecuteCode = DeviceType | (0x841 << 2);
    private const uint LoadBinaryCode = DeviceType | (0x821 << 2);
    private const int FunctionNameBytes = 32;

    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_READ_WRITE = 3;
    private const uint OPEN_EXISTING = 3;

    // The second name is the one PawnIO used before version 2.1.
    private static readonly string[] DevicePaths = [@"\\?\GLOBALROOT\Device\PawnIO", @"\\.\PawnIO"];

    private readonly SafeFileHandle _device;

    private PawnIo(SafeFileHandle device) => _device = device;

    /// <summary>True when the driver is installed (its service exists). Readable without administrator rights.</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>The installed version as its uninstall entry gives it, or null.</summary>
    public static string? InstalledVersion
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                return key?.GetValue("DisplayVersion") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>Opens the driver. Null when it cannot be opened; <paramref name="error"/> is the Win32 error then.</summary>
    public static PawnIo? Open(out int error)
    {
        error = 0;
        foreach (var path in DevicePaths)
        {
            var handle = CreateFileW(path, GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return new PawnIo(handle);
            }

            var failure = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 0 || failure == ErrorAccessDenied)
            {
                error = failure; // "Access denied" says more than "not found" for the old name.
            }
        }

        return null;
    }

    /// <summary>Loads a module into this handle. PawnIO checks its signature. Returns false when it refuses.</summary>
    public bool Load(byte[] module) =>
        DeviceIoControl(_device, LoadBinaryCode, module, (uint)module.Length, null, 0, out _, IntPtr.Zero);

    /// <summary>Calls a function of the loaded module. Returns false when the driver or the module refuses.</summary>
    public bool Execute(string function, ReadOnlySpan<long> input, Span<long> output)
    {
        // 32 bytes of function name (ASCII, zero padded), then the arguments as 64-bit values.
        var request = new byte[FunctionNameBytes + (input.Length * sizeof(long))];
        Encoding.ASCII.GetBytes(function, request.AsSpan(0, FunctionNameBytes - 1));
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FunctionNameBytes));

        var response = new byte[output.Length * sizeof(long)];
        if (!DeviceIoControl(_device, ExecuteCode, request, (uint)request.Length, response, (uint)response.Length, out var returned, IntPtr.Zero))
        {
            return false;
        }

        if (returned < response.Length)
        {
            return false; // Fewer values than the function is known to return.
        }

        response.AsSpan().CopyTo(MemoryMarshal.AsBytes(output));
        return true;
    }

    public void Dispose() => _device.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, byte[] inBuffer, uint inBufferSize, byte[]? outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
