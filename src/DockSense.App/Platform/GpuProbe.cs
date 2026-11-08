using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DockSense.Core;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>Raw facts about one display adapter, kept for diagnostics.</summary>
internal sealed record GpuDetails(string InstanceId, string Name, uint? RemovalPolicy, bool? InLocalMachineContainer, uint ProblemCode)
{
    public GpuInfo ToGpuInfo() => new(InstanceId, Name, HardwareClassifier.IsExternalGpu(InstanceId, RemovalPolicy, InLocalMachineContainer));
}

/// <summary>Enumerates present display adapters through SetupAPI.</summary>
internal static class GpuProbe
{
    /// <summary>Working adapters only: adapters with a problem code (disabled, failed, being removed) are skipped.</summary>
    public static IReadOnlyList<GpuInfo> GetWorkingAdapters() =>
        GetAllAdapterDetails().Where(d => d.ProblemCode == 0).Select(d => d.ToGpuInfo()).ToList();

    public static IReadOnlyList<GpuDetails> GetAllAdapterDetails()
    {
        var classGuid = GUID_DEVCLASS_DISPLAY;
        var set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");
        }

        try
        {
            var result = new List<GpuDetails>();
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var instanceId = ReadString(set, ref data, DEVPKEY_Device_InstanceId);
                if (string.IsNullOrEmpty(instanceId))
                {
                    continue;
                }

                var name = ReadString(set, ref data, DEVPKEY_Device_FriendlyName)
                    ?? ReadString(set, ref data, DEVPKEY_Device_DeviceDesc)
                    ?? instanceId;

                result.Add(new GpuDetails(
                    instanceId,
                    name,
                    ReadUInt32(set, ref data, DEVPKEY_Device_RemovalPolicy),
                    ReadBoolean(set, ref data, DEVPKEY_Device_InLocalMachineContainer),
                    ReadUInt32(set, ref data, DEVPKEY_Device_ProblemCode) ?? 0));
            }

            return result;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static byte[]? ReadRaw(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref data, ref key, out _, null, 0, out var size, 0);
        if (size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, size, out _, 0) ? buffer : null;
    }

    private static string? ReadString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key);
        return raw is null ? null : Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    private static uint? ReadUInt32(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key);
        return raw is { Length: >= 4 } ? BitConverter.ToUInt32(raw, 0) : null;
    }

    /// <summary>DEVPROP_TYPE_BOOLEAN is one byte: 0xFF (DEVPROP_TRUE) or 0x00.</summary>
    private static bool? ReadBoolean(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var raw = ReadRaw(set, ref data, key);
        return raw is { Length: >= 1 } ? raw[0] != 0 : null;
    }
}
