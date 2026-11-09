using System.Runtime.InteropServices;

namespace DockSense.App.Platform;

/// <summary>
/// Live control of the Xbox full screen experience (FSE) through the Windows gaming experience API
/// (<c>gamingexperience.h</c>, API set <c>api-ms-win-gaming-experience-l1-1-0</c>). Works without admin rights.
/// Every member degrades to "not supported" on Windows builds without the API.
/// </summary>
internal static class FullScreenExperience
{
    public const int E_ABORT = unchecked((int)0x80004004);

    private const string ApiSet = "api-ms-win-gaming-experience-l1-1-0.dll";

    private static readonly Api? Functions = Api.Load();
    private static ChangeRoutine? _changeRoutine; // kept alive while registered
    private static IntPtr _registration;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int BoolFunction();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetFunction(int active);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ChangeRoutine(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int RegisterFunction(ChangeRoutine routine, IntPtr context, out IntPtr registration);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void UnregisterFunction(IntPtr registration);

    /// <summary>This Windows build and device support FSE.</summary>
    public static bool IsSupported => Functions is { } api && api.IsSupported() != 0;

    /// <summary>Whether FSE is active right now, or null when FSE is not supported.</summary>
    public static bool? IsActive => IsSupported ? Functions!.IsActive() != 0 : null;

    /// <summary>Whether FSE can be entered or left on this device right now (e.g. a home app is set).</summary>
    public static bool CanSet => IsSupported && Functions!.CanSet() != 0;

    /// <summary>
    /// Enters or leaves FSE and returns the HRESULT. This can block until the user answers Windows' confirmation
    /// dialog, so never call it on the UI thread. <see cref="E_ABORT"/> means the user chose to stay.
    /// </summary>
    public static int Set(bool active) => Functions is { } api ? api.Set(active ? 1 : 0) : unchecked((int)0x80004001); // E_NOTIMPL

    /// <summary>Calls <paramref name="onChanged"/> (on an arbitrary thread) whenever FSE is entered or left.</summary>
    public static void Subscribe(Action onChanged)
    {
        if (Functions is not { } api || _registration != IntPtr.Zero)
        {
            return;
        }

        _changeRoutine = _ => onChanged();
        if (api.Register(_changeRoutine, IntPtr.Zero, out var registration) == 0)
        {
            _registration = registration;
        }
        else
        {
            _changeRoutine = null;
        }
    }

    public static void Unsubscribe()
    {
        if (Functions is { } api && _registration != IntPtr.Zero)
        {
            api.Unregister(_registration);
            _registration = IntPtr.Zero;
            _changeRoutine = null;
        }
    }

    private sealed record Api(
        BoolFunction IsSupported,
        BoolFunction IsActive,
        BoolFunction CanSet,
        SetFunction Set,
        RegisterFunction Register,
        UnregisterFunction Unregister)
    {
        public static Api? Load()
        {
            try
            {
                if (!NativeLibrary.TryLoad(ApiSet, out var library))
                {
                    return null;
                }

                return new Api(
                    Get<BoolFunction>(library, "IsGamingFullScreenExperienceSupported"),
                    Get<BoolFunction>(library, "IsGamingFullScreenExperienceActive"),
                    Get<BoolFunction>(library, "CanSetGamingFullScreenExperience"),
                    Get<SetFunction>(library, "SetGamingFullScreenExperience"),
                    Get<RegisterFunction>(library, "RegisterGamingFullScreenExperienceChangeNotification"),
                    Get<UnregisterFunction>(library, "UnregisterGamingFullScreenExperienceChangeNotification"));
            }
            catch (EntryPointNotFoundException ex)
            {
                Log.Warn($"The gaming experience API is incomplete on this Windows build: {ex.Message}");
                return null;
            }
        }

        private static T Get<T>(IntPtr library, string name)
            where T : Delegate =>
            NativeLibrary.TryGetExport(library, name, out var address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : throw new EntryPointNotFoundException(name);
    }
}
