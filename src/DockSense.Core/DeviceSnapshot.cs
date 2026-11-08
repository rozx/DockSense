namespace DockSense.Core;

/// <summary>A display adapter as reported by Windows.</summary>
/// <param name="InstanceId">PnP device instance ID, e.g. <c>PCI\VEN_10DE&amp;DEV_2C18&amp;...</c>.</param>
/// <param name="Name">Friendly name, e.g. "NVIDIA GeForce RTX 5090 Laptop GPU".</param>
/// <param name="IsExternal">
/// True when Windows reports the adapter as hot-pluggable (removal policy 2 or 3) or as outside the
/// local machine's device container. That is the case for Thunderbolt / USB4 eGPUs. Integrated GPUs report false.
/// </param>
public sealed record GpuInfo(string InstanceId, string Name, bool IsExternal);

/// <summary>An active display path target.</summary>
/// <param name="Id">Stable identifier of the monitor (device path or instance ID).</param>
/// <param name="IsInternal">True for the built-in panel (output technology INTERNAL).</param>
public sealed record DisplayInfo(string Id, bool IsInternal);

/// <summary>Everything mode detection looks at, captured at one point in time.</summary>
public sealed record DeviceSnapshot(IReadOnlyList<GpuInfo> Gpus, IReadOnlyList<DisplayInfo> Displays, bool OnAcPower)
{
    public bool HasExternalDisplay => Displays.Any(d => !d.IsInternal);
}
