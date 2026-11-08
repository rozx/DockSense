using System.Runtime.InteropServices;
using System.Windows.Interop;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>
/// A hidden top-level window that receives the Windows notifications DockSense reacts to:
/// display adapters and monitors arriving or leaving, display changes, AC/DC changes, resume from sleep,
/// and the active power plan changing (from any process). Events are raised on the UI thread.
/// </summary>
internal sealed class SystemNotifications : IDisposable
{
    private readonly HwndSource _window;
    private readonly List<IntPtr> _powerRegistrations = [];
    private readonly List<IntPtr> _deviceRegistrations = [];

    public SystemNotifications()
    {
        // A top-level (not message-only) window: WM_DISPLAYCHANGE is only broadcast to top-level windows.
        _window = new HwndSource(new HwndSourceParameters("DockSense notifications") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(WndProc);

        foreach (var setting in new[] { GUID_ACTIVE_POWERSCHEME, GUID_ACDC_POWER_SOURCE })
        {
            var guid = setting;
            var registration = RegisterPowerSettingNotification(_window.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (registration != IntPtr.Zero)
            {
                _powerRegistrations.Add(registration);
            }
        }

        foreach (var deviceInterface in new[] { GUID_DEVINTERFACE_DISPLAY_ADAPTER, GUID_DEVINTERFACE_MONITOR })
        {
            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                dbcc_classguid = deviceInterface,
            };
            var registration = RegisterDeviceNotificationW(_window.Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (registration != IntPtr.Zero)
            {
                _deviceRegistrations.Add(registration);
            }
        }
    }

    /// <summary>Something that can change the mode happened. The argument is a short reason for the log.</summary>
    public event Action<string>? HardwareChanged;

    /// <summary>The active power plan changed. Windows also sends the current plan once right after registration.</summary>
    public event Action<Guid>? ActivePlanChanged;

    public void Dispose()
    {
        foreach (var registration in _powerRegistrations)
        {
            UnregisterPowerSettingNotification(registration);
        }

        foreach (var registration in _deviceRegistrations)
        {
            UnregisterDeviceNotification(registration);
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_POWERBROADCAST:
                HandlePowerBroadcast((int)wParam, lParam);
                handled = true;
                return new IntPtr(1);

            case WM_DEVICECHANGE when (int)wParam == DBT_DEVICEARRIVAL:
                HardwareChanged?.Invoke("device arrived (display adapter or monitor)");
                break;

            case WM_DEVICECHANGE when (int)wParam == DBT_DEVICEREMOVECOMPLETE:
                HardwareChanged?.Invoke("device removed (display adapter or monitor)");
                break;

            case WM_DISPLAYCHANGE:
                HardwareChanged?.Invoke("display configuration changed");
                break;
        }

        return IntPtr.Zero;
    }

    private void HandlePowerBroadcast(int eventType, IntPtr data)
    {
        switch (eventType)
        {
            case PBT_APMRESUMEAUTOMATIC:
                HardwareChanged?.Invoke("resumed from sleep");
                break;

            case PBT_POWERSETTINGCHANGE when data != IntPtr.Zero:
                var header = Marshal.PtrToStructure<POWERBROADCAST_SETTING_HEADER>(data);
                if (header.PowerSetting == GUID_ACTIVE_POWERSCHEME && header.DataLength >= 16)
                {
                    var plan = Marshal.PtrToStructure<Guid>(data + Marshal.SizeOf<POWERBROADCAST_SETTING_HEADER>());
                    ActivePlanChanged?.Invoke(plan);
                }
                else if (header.PowerSetting == GUID_ACDC_POWER_SOURCE)
                {
                    HardwareChanged?.Invoke("power source changed");
                }

                break;
        }
    }
}
