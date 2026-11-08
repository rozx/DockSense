using DockSense.Core;

namespace DockSense.Core.Tests;

public class ModeResolverTests
{
    // Real values measured on a ROG Xbox Ally X docked to an XG Mobile RTX 5090.
    private static readonly GpuInfo Igpu890M = new(
        @"PCI\VEN_1002&DEV_150E&SUBSYS_19541043&REV_C7\4&35FE04F8&0&0041", "AMD Radeon(TM) 890M Graphics", IsExternal: false);

    private static readonly GpuInfo Egpu5090 = new(
        @"PCI\VEN_10DE&DEV_2C18&SUBSYS_225B1043&REV_A1\96E6DC66672DB04800", "NVIDIA GeForce RTX 5090 Laptop GPU", IsExternal: true);

    private static readonly DisplayInfo InternalPanel = new(@"DISPLAY\SDC4501", IsInternal: true);
    private static readonly DisplayInfo ExternalMonitor = new(@"DISPLAY\SAM7454", IsInternal: false);

    private static readonly DetectionOptions Defaults = new();

    [Fact]
    public void Handheld_when_only_internal_panel_and_igpu()
    {
        var snapshot = new DeviceSnapshot([Igpu890M], [InternalPanel], OnAcPower: false);

        Assert.Equal(DockMode.Handheld, ModeResolver.Resolve(snapshot, Defaults));
    }

    [Fact]
    public void Docked_when_external_display_without_egpu()
    {
        var snapshot = new DeviceSnapshot([Igpu890M], [InternalPanel, ExternalMonitor], OnAcPower: true);

        Assert.Equal(DockMode.Docked, ModeResolver.Resolve(snapshot, Defaults));
    }

    [Fact]
    public void Egpu_wins_over_docked()
    {
        var snapshot = new DeviceSnapshot([Igpu890M, Egpu5090], [InternalPanel, ExternalMonitor], OnAcPower: true);

        Assert.Equal(DockMode.Egpu, ModeResolver.Resolve(snapshot, Defaults));
        Assert.Equal(Egpu5090, ModeResolver.FindEgpu(snapshot, Defaults));
    }

    [Fact]
    public void Egpu_still_detected_after_ac_drops_during_unplug()
    {
        // Unplugging the XG Mobile drops AC ~10 s before Windows removes the GPU. The mode must not flip to
        // Handheld/Docked on the AC change alone.
        var snapshot = new DeviceSnapshot([Igpu890M, Egpu5090], [InternalPanel, ExternalMonitor], OnAcPower: false);

        Assert.Equal(DockMode.Egpu, ModeResolver.Resolve(snapshot, Defaults));
    }

    [Fact]
    public void Amd_vendor_id_alone_does_not_make_an_egpu()
    {
        var amdEgpu = new GpuInfo(@"PCI\VEN_1002&DEV_7550\EXT", "AMD Radeon RX 9070 XT", IsExternal: true);

        Assert.Equal(DockMode.Handheld, ModeResolver.Resolve(new DeviceSnapshot([Igpu890M], [InternalPanel], false), Defaults));
        Assert.Equal(DockMode.Egpu, ModeResolver.Resolve(new DeviceSnapshot([Igpu890M, amdEgpu], [InternalPanel], false), Defaults));
    }

    [Fact]
    public void Docked_can_require_ac_power()
    {
        var options = new DetectionOptions { DockedRequiresAcPower = true };

        Assert.Equal(DockMode.Handheld, ModeResolver.Resolve(new DeviceSnapshot([Igpu890M], [InternalPanel, ExternalMonitor], false), options));
        Assert.Equal(DockMode.Docked, ModeResolver.Resolve(new DeviceSnapshot([Igpu890M], [InternalPanel, ExternalMonitor], true), options));
    }

    [Fact]
    public void Forced_instance_id_counts_as_egpu_case_insensitively()
    {
        // OCuLink docks can show up as internal, non-removable PCIe devices.
        var oculinkGpu = new GpuInfo(@"PCI\VEN_10DE&DEV_2684\OCULINK", "NVIDIA GeForce RTX 4090", IsExternal: false);
        var options = new DetectionOptions { ForceEgpuInstanceIds = [oculinkGpu.InstanceId.ToLowerInvariant()] };

        Assert.Equal(DockMode.Egpu, ModeResolver.Resolve(new DeviceSnapshot([Igpu890M, oculinkGpu], [InternalPanel], true), options));
    }

    [Fact]
    public void Ignored_instance_id_never_counts_as_egpu()
    {
        var options = new DetectionOptions
        {
            IgnoreGpuInstanceIds = [Egpu5090.InstanceId],
            ForceEgpuInstanceIds = [Egpu5090.InstanceId],
        };

        Assert.Equal(DockMode.Handheld, ModeResolver.Resolve(new DeviceSnapshot([Igpu890M, Egpu5090], [InternalPanel], true), options));
    }
}
