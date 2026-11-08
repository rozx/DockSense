using DockSense.Core;

namespace DockSense.Core.Tests;

public class HardwareClassifierTests
{
    [Fact]
    public void Ally_x_integrated_890m_is_not_external()
    {
        // Measured: RemovalPolicy=1, InLocalMachineContainer=True
        Assert.False(HardwareClassifier.IsExternalGpu(@"PCI\VEN_1002&DEV_150E&SUBSYS_19541043&REV_C7\4&35FE04F8&0&0041", 1, true));
    }

    [Fact]
    public void Xg_mobile_rtx_5090_is_external()
    {
        // Measured: RemovalPolicy=3, InLocalMachineContainer=False
        Assert.True(HardwareClassifier.IsExternalGpu(@"PCI\VEN_10DE&DEV_2C18&SUBSYS_225B1043&REV_A1\96E6DC66672DB04800", 3, false));
    }

    [Theory]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(1u, false)]
    public void Removal_policy_alone_decides_when_container_is_unknown(uint removalPolicy, bool expected)
    {
        Assert.Equal(expected, HardwareClassifier.IsExternalGpu(@"PCI\VEN_10DE&DEV_0000\X", removalPolicy, null));
    }

    [Fact]
    public void Device_outside_local_container_is_external()
    {
        Assert.True(HardwareClassifier.IsExternalGpu(@"PCI\VEN_1002&DEV_7550\X", null, false));
    }

    [Theory]
    [InlineData(@"USB\VID_17E9&PID_6000\DISPLAYLINK")]
    [InlineData(@"ROOT\DISPLAY\0000")]
    [InlineData(@"SWD\REMOTEDISPLAYENUM\VIRTUAL")]
    public void Non_pci_adapters_are_never_egpus(string instanceId)
    {
        Assert.False(HardwareClassifier.IsExternalGpu(instanceId, 3, false));
    }

    [Theory]
    [InlineData(0x80000000u, DisplayKind.Internal)]   // Ally X built-in panel
    [InlineData(11u, DisplayKind.Internal)]           // embedded DisplayPort (eDP)
    [InlineData(13u, DisplayKind.Internal)]           // embedded UDI
    [InlineData(10u, DisplayKind.External)]           // DisplayPort (Samsung on the eGPU)
    [InlineData(5u, DisplayKind.External)]            // HDMI (TCL on the eGPU)
    [InlineData(16u, DisplayKind.External)]           // indirect wired (DisplayLink)
    [InlineData(17u, DisplayKind.Virtual)]            // indirect virtual (virtual display drivers)
    public void Displays_are_classified_by_output_technology(uint outputTechnology, DisplayKind expected)
    {
        Assert.Equal(expected, HardwareClassifier.ClassifyDisplay(outputTechnology));
    }
}
