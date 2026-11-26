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

    [Fact]
    public void Driver_sizes_are_megabytes_without_auto()
    {
        // AMD Variable Graphics Memory: custom sizes from 0.5 to 16 GB, 8 GB stored.
        var options = IgpuMemoryOptions.FromDriverSizes([2048, 512, 1024, 8192, 16384, 1024, 0], storedMb: 8192);

        Assert.NotNull(options);
        Assert.Equal([512, 1024, 2048, 8192, 16384], options.SizesMb);
        Assert.Equal(8192, options.ToMb(options.CurrentRaw));
        Assert.Equal(512u, options.RawFor(512));
        Assert.Null(options.RawFor(0));
        Assert.Equal(512u, options.RawToWrite(512));
        Assert.Null(options.RawToWrite(8192));
        Assert.Null(options.RawToWrite(3072));
    }

    [Fact]
    public void Driver_sizes_keep_a_stored_size_that_is_not_on_offer()
    {
        // A preset of the driver (say "Medium", 3 GB) is stored, and only the custom sizes are offered.
        var options = IgpuMemoryOptions.FromDriverSizes([512, 1024], storedMb: 3072);

        Assert.NotNull(options);
        Assert.Equal(3072, options.ToMb(options.CurrentRaw));
        Assert.Equal(1024u, options.RawToWrite(1024));
    }

    [Fact]
    public void A_size_that_a_driver_preset_comes_to_is_not_stored_as_that_size()
    {
        // The driver is on its "Medium" preset, which comes to 4 GB today. A profile asking for 4 GB wants it fixed.
        var preset = IgpuMemoryOptions.FromDriverSizes([512, 4096, 8192], storedMb: 4096, storedIsFixed: false);
        var custom = IgpuMemoryOptions.FromDriverSizes([512, 4096, 8192], storedMb: 4096);

        Assert.NotNull(preset);
        Assert.NotNull(custom);
        Assert.False(preset.IsStored(4096));
        Assert.Equal(4096u, preset.RawToWrite(4096));
        Assert.Equal(4096, preset.ToMb(preset.CurrentRaw));
        Assert.True(custom.IsStored(4096));
        Assert.Null(custom.RawToWrite(4096));

        // Once it has been set, it is stored like any other size.
        Assert.Null((preset with { CurrentRaw = 4096, CurrentIsFixed = true }).RawToWrite(4096));
    }

    [Fact]
    public void Driver_sizes_need_a_size_on_offer_and_a_stored_size()
    {
        Assert.Null(IgpuMemoryOptions.FromDriverSizes([], storedMb: 512));
        Assert.Null(IgpuMemoryOptions.FromDriverSizes([0, -1], storedMb: 512));
        Assert.Null(IgpuMemoryOptions.FromDriverSizes([512, 1024], storedMb: null));
    }
}
