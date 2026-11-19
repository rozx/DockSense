using System.Runtime.InteropServices;

namespace DockSense.App.Platform;

/// <summary>When the user signed in to this Windows session.</summary>
internal static class SignInSession
{
    private const int WTS_CURRENT_SESSION = -1;
    private const int WTSSessionInfo = 24;

    // WTSINFOW: eight 32-bit fields, WinStationName[32], Domain[17] and UserName[21] (WCHAR), padding to 8 bytes,
    // then ConnectTime, DisconnectTime, LastInputTime, LogonTime and CurrentTime as FILETIME values.
    private const int LogonTimeOffset = 200;
    private const int WtsInfoSize = 216;

    /// <summary>The sign-in time, or null when Windows does not say.</summary>
    public static DateTime? SignInTimeUtc
    {
        get
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfo, out var buffer, out var bytes))
            {
                return null;
            }

            try
            {
                if (bytes < WtsInfoSize)
                {
                    return null;
                }

                var fileTime = Marshal.ReadInt64(buffer, LogonTimeOffset);
                return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null; // Not a valid file time.
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    /// <summary>Time since sign-in, or null when unknown.</summary>
    public static TimeSpan? SinceSignIn => SignInTimeUtc is { } signIn ? DateTime.UtcNow - signIn : null;

    /// <summary>
    /// How long until the Windows shell can be expected to be up. Started by the sign-in task, DockSense runs before
    /// the desktop, the taskbar and the notification center exist; what needs them (programs a profile starts,
    /// notifications) waits this long. Zero from <see cref="DockSense.Core.FsePlanner.SignInSettleTime"/> after
    /// sign-in on, and when the sign-in time is unknown.
    /// </summary>
    public static TimeSpan ShellWait => SinceSignIn is { } elapsed ? DockSense.Core.FsePlanner.StartupDelay(elapsed) : TimeSpan.Zero;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
