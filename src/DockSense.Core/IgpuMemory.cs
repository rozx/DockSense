using System.Buffers.Binary;

namespace DockSense.Core;

/// <summary>
/// The iGPU memory sizes (UMA frame buffer) the firmware offers, decoded from the ASUS ACPI <c>DSTS</c> reply of
/// device <c>0x000600C4</c> (the same interface Armoury Crate and G-Helper use). Changes apply at the next restart.
/// </summary>
/// <remarks>
/// Reply layout: u32 status at offset 0 (0x10000 = supported, 0x80000 = unsupported, 0x20000 = values are in 512 MB
/// units, otherwise 1 MB; low 16 bits = number of options including Auto), u32 current raw value at offset 4, then
/// u16 raw option values: option <c>i</c> (i ≥ 1) at offset <c>6 + 2i</c>. Option 0 is Auto (raw 0).
/// </remarks>
/// <param name="UnitMb">Size of one raw unit in MB.</param>
/// <param name="RawValues">Raw values the firmware accepts, starting with 0 (Auto).</param>
/// <param name="CurrentRaw">The value currently stored in the firmware (it takes effect at the next restart).</param>
public sealed record IgpuMemoryOptions(int UnitMb, IReadOnlyList<uint> RawValues, uint CurrentRaw)
{
    private const uint Supported = 0x10000;
    private const uint Unsupported = 0x80000;
    private const uint Units512Mb = 0x20000;

    /// <summary>Megabytes for a raw value; 0 means Auto.</summary>
    public int ToMb(uint raw) => checked((int)raw * UnitMb);

    /// <summary>Sizes offered, in MB (0 = Auto), in firmware order.</summary>
    public IEnumerable<int> SizesMb => RawValues.Select(ToMb);

    /// <summary>The raw firmware value for a size in MB, or null when the firmware does not offer that size.</summary>
    public uint? RawFor(int megabytes)
    {
        foreach (var raw in RawValues)
        {
            if (ToMb(raw) == megabytes)
            {
                return raw;
            }
        }

        return null;
    }

    /// <summary>
    /// The raw value to write so that <paramref name="targetMb"/> applies after the next restart, or null when
    /// nothing should be written (no target, already set, or a size the firmware does not offer).
    /// </summary>
    public uint? RawToWrite(int? targetMb) =>
        targetMb is { } mb && RawFor(mb) is { } raw && raw != CurrentRaw ? raw : null;

    /// <summary>Decodes a DSTS reply. Returns null when the device is not supported or the reply is malformed.</summary>
    public static IgpuMemoryOptions? Parse(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 8)
        {
            return null;
        }

        var status = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        if ((status & Supported) == 0 || (status & Unsupported) != 0)
        {
            return null;
        }

        var count = Math.Min((int)(status & 0xFFFF), (reply.Length - 6) / 2);
        if (count < 2)
        {
            return null;
        }

        var values = new List<uint>(count) { 0 };
        for (var i = 1; i < count; i++)
        {
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(reply[(6 + (i * 2))..]);
            if (raw == 0 || values.Contains(raw))
            {
                return null; // Not the layout we know: never guess firmware values.
            }

            values.Add(raw);
        }

        var unit = (status & Units512Mb) != 0 ? 512 : 1;
        return new IgpuMemoryOptions(unit, values, BinaryPrimitives.ReadUInt32LittleEndian(reply[4..]));
    }
}
