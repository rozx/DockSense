namespace DockSense.Core;

public enum DisplayKind
{
    /// <summary>The handheld's built-in panel.</summary>
    Internal,

    /// <summary>A real external monitor (HDMI, DisplayPort, USB-C, DisplayLink, ...).</summary>
    External,

    /// <summary>A virtual display (streaming apps, virtual display drivers). Ignored for mode detection.</summary>
    Virtual,
}

/// <summary>Turns raw Windows device properties into the facts mode detection needs.</summary>
public static class HardwareClassifier
{
    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values (wingdi.h).
    public const uint OutputDisplayPortEmbedded = 11;
    public const uint OutputUdiEmbedded = 13;
    public const uint OutputIndirectVirtual = 17;
    public const uint OutputInternal = 0x80000000;

    // CM_REMOVAL_POLICY values (cfgmgr32.h).
    public const uint RemovalPolicyExpectOrderlyRemoval = 2;
    public const uint RemovalPolicyExpectSurpriseRemoval = 3;

    /// <summary>
    /// A display adapter is external when it is a PCI(e) device that Windows treats as hot-pluggable, or that sits
    /// outside the local machine's device container. Thunderbolt / USB4 eGPUs match. Integrated GPUs do not.
    /// USB display adapters (DisplayLink) and virtual adapters (ROOT\, SWD\) are never eGPUs, even though they can be
    /// removable.
    /// </summary>
    public static bool IsExternalGpu(string instanceId, uint? removalPolicy, bool? inLocalMachineContainer)
    {
        if (!instanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return removalPolicy is RemovalPolicyExpectOrderlyRemoval or RemovalPolicyExpectSurpriseRemoval
            || inLocalMachineContainer == false;
    }

    public static DisplayKind ClassifyDisplay(uint outputTechnology) => outputTechnology switch
    {
        OutputInternal or OutputDisplayPortEmbedded or OutputUdiEmbedded => DisplayKind.Internal,
        OutputIndirectVirtual => DisplayKind.Virtual,
        _ => DisplayKind.External,
    };
}
