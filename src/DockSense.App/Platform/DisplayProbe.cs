using System.ComponentModel;
using System.Runtime.InteropServices;
using DockSense.Core;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>Raw facts about one active display target, kept for diagnostics.</summary>
/// <param name="Width">Desktop width in pixels, 0 when unknown.</param>
/// <param name="Height">Desktop height in pixels, 0 when unknown.</param>
/// <param name="RefreshHz">Refresh rate, 0 when unknown.</param>
/// <param name="AdapterInstanceId">Device instance ID of the display adapter that drives it, empty when unknown.</param>
internal sealed record DisplayDetails(
    string Id, string Name, uint OutputTechnology, DisplayKind Kind,
    uint Width = 0, uint Height = 0, double RefreshHz = 0, string AdapterInstanceId = "");

/// <summary>Enumerates active displays through the Windows display configuration API.</summary>
internal static class DisplayProbe
{
    /// <summary>Active, non-virtual displays.</summary>
    public static IReadOnlyList<DisplayInfo> GetActiveDisplays() =>
        GetActiveDisplayDetails()
            .Where(d => d.Kind != DisplayKind.Virtual)
            .Select(d => new DisplayInfo(d.Id, d.Kind == DisplayKind.Internal, d.Name))
            .ToList();

    /// <param name="withAdapters">Also ask which adapter drives each display. Only the diagnostic report wants that.</param>
    public static IReadOnlyList<DisplayDetails> GetActiveDisplayDetails(bool withAdapters = false)
    {
        // The topology can change between the size query and the actual query; retry in that case.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
            if (error != ERROR_SUCCESS)
            {
                throw new Win32Exception(error, "GetDisplayConfigBufferSizes failed");
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            error = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (error == ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (error != ERROR_SUCCESS)
            {
                throw new Win32Exception(error, "QueryDisplayConfig failed");
            }

            var result = new List<DisplayDetails>();
            for (var i = 0; i < pathCount; i++)
            {
                var target = paths[i].targetInfo;
                var (name, devicePath) = GetTargetName(target.adapterId, target.id);
                var id = string.IsNullOrEmpty(devicePath)
                    ? $"{target.adapterId.HighPart:X8}{target.adapterId.LowPart:X8}:{target.id}"
                    : devicePath;
                var modeIndex = paths[i].sourceInfo.modeInfoIdx;
                var mode = modeIndex < modeCount && modes[modeIndex].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ? modes[modeIndex] : default;
                var refresh = target.refreshRate.Denominator == 0 ? 0 : (double)target.refreshRate.Numerator / target.refreshRate.Denominator;
                result.Add(new DisplayDetails(
                    id, name, target.outputTechnology, HardwareClassifier.ClassifyDisplay(target.outputTechnology),
                    mode.sourceWidth, mode.sourceHeight, refresh, withAdapters ? GetAdapterInstanceId(target.adapterId) : string.Empty));
            }

            return result;
        }

        throw new InvalidOperationException("Display topology kept changing while it was being read.");
    }

    /// <summary>
    /// The adapter's device instance ID, from its interface path
    /// (<c>\\?\PCI#VEN_10DE&amp;DEV_2C18...#96E6...#{interface class}</c>). Empty when Windows does not tell.
    /// </summary>
    private static string GetAdapterInstanceId(LUID adapterId)
    {
        var request = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(),
                adapterId = adapterId,
            },
            adapterDevicePath = string.Empty,
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS || string.IsNullOrEmpty(request.adapterDevicePath))
        {
            return string.Empty;
        }

        var path = request.adapterDevicePath;
        var start = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? 4 : 0;
        var end = path.LastIndexOf("#{", StringComparison.Ordinal);
        return (end > start ? path[start..end] : path[start..]).Replace('#', '\\');
    }

    private static (string Name, string DevicePath) GetTargetName(LUID adapterId, uint targetId)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId,
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty,
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? (request.monitorFriendlyDeviceName ?? string.Empty, request.monitorDevicePath ?? string.Empty)
            : (string.Empty, string.Empty);
    }
}
