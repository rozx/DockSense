using System.Runtime.InteropServices;
using Microsoft.Win32;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>
/// A hidden top-level window that receives the Windows notifications DockSense reacts to:
/// display adapters arriving or leaving, AC/DC changes, resume from sleep, display topology changes,
/// and the active power plan changing (from any process).
/// Events are raised on the UI thread that created this object.
/// </summary>
internal sealed class SystemNotifications : NativeWindow, IDisposable
{
    private readonly List<IntPtr> _powerRegistrations = [];
    private readonly IntPtr _deviceRegistration;
    private readonly SynchronizationContext _uiContext;

    public SystemNotifications()
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create SystemNotifications on the UI thread.");

        CreateHandle(new CreateParams { Caption = "DockSense notifications" });

        foreach (var setting in new[] { GUID_ACTIVE_POWERSCHEME, GUID_ACDC_POWER_SOURCE })
        {
            var guid = setting;
            var registration = RegisterPowerSettingNotification(Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (registration != IntPtr.Zero)
            {
                _powerRegistrations.Add(registration);
            }
        }

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GUID_DEVINTERFACE_DISPLAY_ADAPTER,
        };
        _deviceRegistration = RegisterDeviceNotificationW(Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Something that can change the mode happened. The argument is a short reason for the log.</summary>
    public event Action<string>? HardwareChanged;

    /// <summary>The active power plan changed. Windows also sends the current plan once right after registration.</summary>
    public event Action<Guid>? ActivePlanChanged;

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var registration in _powerRegistrations)
        {
            UnregisterPowerSettingNotification(registration);
        }

        if (_deviceRegistration != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_deviceRegistration);
        }

        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_POWERBROADCAST:
                HandlePowerBroadcast((int)m.WParam, m.LParam);
                m.Result = new IntPtr(1);
                return;

            case WM_DEVICECHANGE when (int)m.WParam == DBT_DEVICEARRIVAL:
                HardwareChanged?.Invoke("display adapter arrived");
                break;

            case WM_DEVICECHANGE when (int)m.WParam == DBT_DEVICEREMOVECOMPLETE:
                HardwareChanged?.Invoke("display adapter removed");
                break;
        }

        base.WndProc(ref m);
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

    // SystemEvents may raise this on its own thread; hop to the UI thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _uiContext.Post(_ => HardwareChanged?.Invoke("display configuration changed"), null);
}
