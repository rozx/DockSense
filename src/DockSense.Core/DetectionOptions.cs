namespace DockSense.Core;

/// <summary>User-tunable rules for mode detection.</summary>
public sealed record DetectionOptions
{
    /// <summary>
    /// Adapters (PnP instance IDs) that always count as an eGPU. Use this for OCuLink docks, which Windows may
    /// report as internal, non-removable PCIe devices.
    /// </summary>
    public IReadOnlyList<string> ForceEgpuInstanceIds { get; init; } = [];

    /// <summary>Adapters (PnP instance IDs) that never count as an eGPU.</summary>
    public IReadOnlyList<string> IgnoreGpuInstanceIds { get; init; } = [];

    /// <summary>When true, an external display only means <see cref="DockMode.Docked"/> while on AC power.</summary>
    public bool DockedRequiresAcPower { get; init; }

    /// <summary>
    /// How long a newly detected mode must stay unchanged before its profile is applied. Hardware state settles
    /// slowly when a dock is unplugged: AC power drops ~10 s before Windows removes the eGPU.
    /// </summary>
    public double SettleSeconds { get; init; } = 5;
}
