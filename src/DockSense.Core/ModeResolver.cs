namespace DockSense.Core;

/// <summary>Maps a <see cref="DeviceSnapshot"/> to a <see cref="DockMode"/>. Priority: Egpu &gt; Docked &gt; Handheld.</summary>
public static class ModeResolver
{
    public static DockMode Resolve(DeviceSnapshot snapshot, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        if (FindEgpu(snapshot, options) is not null)
        {
            return DockMode.Egpu;
        }

        if (snapshot.HasExternalDisplay && (!options.DockedRequiresAcPower || snapshot.OnAcPower))
        {
            return DockMode.Docked;
        }

        return DockMode.Handheld;
    }

    /// <summary>The first adapter that counts as an eGPU, or null.</summary>
    public static GpuInfo? FindEgpu(DeviceSnapshot snapshot, DetectionOptions options) =>
        snapshot.Gpus.FirstOrDefault(gpu => IsEgpu(gpu, options));

    private static bool IsEgpu(GpuInfo gpu, DetectionOptions options)
    {
        if (ContainsId(options.IgnoreGpuInstanceIds, gpu.InstanceId))
        {
            return false;
        }

        return gpu.IsExternal || ContainsId(options.ForceEgpuInstanceIds, gpu.InstanceId);
    }

    private static bool ContainsId(IReadOnlyList<string> ids, string instanceId) =>
        ids.Any(id => string.Equals(id, instanceId, StringComparison.OrdinalIgnoreCase));
}
