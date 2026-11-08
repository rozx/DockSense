using System.ComponentModel;
using System.Runtime.InteropServices;
using DockSense.Core;
using static DockSense.App.Platform.NativeMethods;

namespace DockSense.App.Platform;

/// <summary>Raw facts about one active display target, kept for diagnostics.</summary>
internal sealed record DisplayDetails(string Id, string Name, uint OutputTechnology, DisplayKind Kind);

/// <summary>Enumerates active displays through the Windows display configuration API.</summary>
internal static class DisplayProbe
{
    /// <summary>Active, non-virtual displays.</summary>
    public static IReadOnlyList<DisplayInfo> GetActiveDisplays() =>
        GetActiveDisplayDetails()
            .Where(d => d.Kind != DisplayKind.Virtual)
            .Select(d => new DisplayInfo(d.Id, d.Kind == DisplayKind.Internal, d.Name))
            .ToList();

    public static IReadOnlyList<DisplayDetails> GetActiveDisplayDetails()
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
                result.Add(new DisplayDetails(id, name, target.outputTechnology, HardwareClassifier.ClassifyDisplay(target.outputTechnology)));
            }

            return result;
        }

        throw new InvalidOperationException("Display topology kept changing while it was being read.");
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
