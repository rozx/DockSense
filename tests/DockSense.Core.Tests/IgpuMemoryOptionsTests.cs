using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class IgpuMemoryOptionsTests
{
    /// <summary>DSTS reply for 0x000600C4 on a ROG Xbox Ally X (RC74XA, BIOS 303), set to 8 GB.</summary>
    private static readonly byte[] XboxAllyXReply = Convert.FromHexString(
        "12000300" + "10000000" +
        "0100" + "0200" + "0400" + "0600" + "0800" + "0A00" + "0C00" + "0E00" + "1000" +
        "1200" + "1400" + "1600" + "1800" + "1A00" + "1C00" + "1E00" + "2000" + "0000" +
        new string('0', 40));

    [Fact]
    public void Decodes_the_xbox_ally_x_reply()
    {
        var options = IgpuMemoryOptions.Parse(XboxAllyXReply);

        Assert.NotNull(options);
        Assert.Equal(512, options.UnitMb);
        Assert.Equal(18, options.RawValues.Count);
        Assert.Equal(0u, options.RawValues[0]);
        Assert.Equal(16u, options.CurrentRaw);
        Assert.Equal(8192, options.ToMb(options.CurrentRaw));
        Assert.Equal([0, 512, 1024, 2048, 3072, 4096], options.SizesMb.Take(6));
        Assert.Equal(16384, options.SizesMb.Last());
    }

    [Fact]
    public void Writes_only_sizes_the_firmware_offers()
    {
        var options = IgpuMemoryOptions.Parse(XboxAllyXReply)!;

        Assert.Equal(1u, options.RawToWrite(512));
        Assert.Equal(0u, options.RawToWrite(0)); // Auto
        Assert.Null(options.RawToWrite(8192));   // already set
        Assert.Null(options.RawToWrite(1536));   // not offered
        Assert.Null(options.RawToWrite(null));   // don't change
    }

    [Fact]
    public void Unsupported_or_unknown_replies_are_rejected()
    {
        Assert.Null(IgpuMemoryOptions.Parse(new byte[64]));                          // all zero: not supported
        Assert.Null(IgpuMemoryOptions.Parse(Convert.FromHexString("12000900" + new string('0', 120)))); // unsupported flag
        Assert.Null(IgpuMemoryOptions.Parse(Convert.FromHexString("03000100" + "00000000" + "0100" + "0000" + new string('0', 104)))); // zero option
        Assert.Null(IgpuMemoryOptions.Parse([1, 2, 3]));
    }

    [Fact]
    public void One_megabyte_units_are_supported()
    {
        // Status 0x10003: supported, 1 MB units, Auto + 2 options; current 4096 MB.
        var reply = Convert.FromHexString("03000100" + "00100000" + "0010" + "0020" + new string('0', 104));

        var options = IgpuMemoryOptions.Parse(reply)!;

        Assert.Equal(1, options.UnitMb);
        Assert.Equal([0, 4096, 8192], options.SizesMb);
        Assert.Equal(4096, options.ToMb(options.CurrentRaw));
    }
}
