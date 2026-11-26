using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DockSense.App.Platform;

/// <summary>
/// What ASUS Armoury Crate stores about its performance mode (read-only, no admin rights). Armoury Crate keeps one
/// mode for AC power and one for battery and sends it to the firmware when the power source or its own setting
/// changes; the firmware then loads that mode's power limits, replacing a TDP DockSense had set.
/// </summary>
internal static class ArmouryCrate
{
    private const string ThrottleKey = @"SOFTWARE\ASUS\ARMOURY CRATE Service\ThrottlePlugin";
    private const string StatusKey = ThrottleKey + @"\ROG ATKStatus";

    private const uint REG_NOTIFY_CHANGE_NAME = 0x1;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    private const uint REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    /// <summary>
    /// The mode selected for the power source (0 Windows, 1 Silent, 2 Performance, 3 Turbo, 4 Manual), or null
    /// without Armoury Crate. Seen on the RC74XA: 3 for AC, 2 for battery.
    /// </summary>
    public static int? SelectedMode(bool onAcPower)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(StatusKey);
            return key?.GetValue(onAcPower ? "ThrottleModeOnAC" : "ThrottleModeOnDC") as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Calls <paramref name="changed"/> (on a thread pool thread) whenever Armoury Crate writes its throttle
    /// settings. Null when there is nothing to watch. Dispose the result to stop.
    /// </summary>
    public static IDisposable? WatchModeChanges(Action changed)
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(ThrottleKey) is { } key ? new Watcher(key, changed) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private sealed class Watcher : IDisposable
    {
        private readonly RegistryKey _key;
        private readonly Action _changed;
        private readonly AutoResetEvent _signal = new(false);
        private readonly RegisteredWaitHandle _wait;
        private readonly object _gate = new();
        private bool _disposed;

        public Watcher(RegistryKey key, Action changed)
        {
            _key = key;
            _changed = changed;
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => OnSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
            Arm();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _wait.Unregister(null);
            _key.Dispose(); // Closing the key signals the event once more; OnSignal sees the flag.
            _signal.Dispose();
        }

        private void OnSignal()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                Arm(); // One notification per registration.
            }

            _changed();
        }

        private void Arm() =>
            _ = RegNotifyChangeKeyValue(_key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC, _signal.SafeWaitHandle, true);
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
