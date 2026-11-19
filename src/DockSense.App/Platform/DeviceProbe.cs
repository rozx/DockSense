using System.ComponentModel;
using System.Runtime.InteropServices;
using DockSense.Core;

namespace DockSense.App.Platform;

/// <summary>The hardware state the mode is resolved from. Plain Win32, so it also works before any UI is loaded.</summary>
internal static class DeviceProbe
{
    private const byte AcOnline = 1;

    /// <summary>Adapters, displays and AC power right now.</summary>
    /// <exception cref="Win32Exception">A Windows API failed.</exception>
    /// <exception cref="InvalidOperationException">The display configuration could not be read.</exception>
    public static DeviceSnapshot TakeSnapshot() =>
        new(GpuProbe.GetWorkingAdapters(), DisplayProbe.GetActiveDisplays(), OnAcPower);

    /// <summary>True on AC power. An unknown state counts as battery, as it did with WPF's PowerLineStatus.</summary>
    public static bool OnAcPower =>
        GetSystemPowerStatus(out var status)
            ? status.ACLineStatus == AcOnline
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemPowerStatus failed");

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
