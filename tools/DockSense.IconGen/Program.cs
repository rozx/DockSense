using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using DockSense.IconGen;

// Generates every DockSense icon from the pixel grids in Sprites.cs.
// Usage: dotnet run --project tools/DockSense.IconGen -- <repo>/assets
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "assets");
var iconDir = Path.Combine(outDir, "icons");
Directory.CreateDirectory(iconDir);

// App icon: the 24px handheld at integer scales, the 16px handheld where 24 does not divide evenly.
var app16 = Render(Sprites.Handheld16, "app", 1);
WriteIco(Path.Combine(iconDir, "app.ico"),
[
    app16,
    Scale(app16, 20),
    Render(Sprites.Handheld24, "app", 1),
    Render(Sprites.Handheld16, "app", 2),
    Scale(Render(Sprites.Handheld24, "app", 1), 40),
    Render(Sprites.Handheld24, "app", 2),
    Render(Sprites.Handheld16, "app", 4),
    Render(Sprites.Handheld24, "app", 4),
    Render(Sprites.Handheld16, "app", 8),
    Pad(Render(Sprites.Handheld24, "app", 10), 256),
]);

// Tray icons, one per state.
var tray = new (string Name, string[] Sprite, string Palette)[]
{
    ("handheld", Sprites.Handheld16, "handheld"),
    ("docked", Sprites.Monitor16, "docked"),
    ("egpu", Sprites.Gpu16, "egpu"),
    ("paused", Sprites.Paused16, "paused"),
};

foreach (var (name, sprite, palette) in tray)
{
    var oneX = Render(sprite, palette, 1);
    WriteIco(Path.Combine(iconDir, $"tray-{name}.ico"),
    [
        oneX,
        Scale(oneX, 20),
        Scale(oneX, 24),
        Render(sprite, palette, 2),
        Scale(oneX, 40),
        Render(sprite, palette, 3),
        Render(sprite, palette, 4),
    ]);

    // 1x for the UI (scaled with nearest neighbor), 3x for the README, 8x for toast notifications.
    SavePng(oneX, Path.Combine(iconDir, $"{name}.png"));
    SavePng(Render(sprite, palette, 3), Path.Combine(iconDir, $"{name}@3x.png"));
    SavePng(Render(sprite, palette, 8), Path.Combine(iconDir, $"{name}@8x.png"));
}

SavePng(Render(Sprites.Handheld24, "app", 1), Path.Combine(iconDir, "app.png"));
// Title bar: the 16 px sprite at 2x, shown at 16x16 DIP (exact at 100% and 200% scaling).
SavePng(Render(Sprites.Handheld16, "app", 2), Path.Combine(iconDir, "app-small.png"));
SavePng(Render(Sprites.Handheld24, "app", 8), Path.Combine(iconDir, "app@8x.png"));
SavePng(Render(Sprites.Handheld24, "app", 8), Path.Combine(outDir, "logo.png"));

Console.WriteLine($"Icons written to {outDir}");
return 0;

static Bitmap Render(string[] sprite, string paletteName, int scale)
{
    var (screen, highlight) = Sprites.Palettes[paletteName];
    var size = sprite.Length;
    var bitmap = new Bitmap(size * scale, size * scale, PixelFormat.Format32bppArgb);
    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var key = sprite[y][x];
            if (key == '.')
            {
                continue;
            }

            var color = ParseColor(key switch
            {
                'S' => screen,
                's' => highlight,
                _ => Sprites.Base[key],
            });

            for (var dy = 0; dy < scale; dy++)
            {
                for (var dx = 0; dx < scale; dx++)
                {
                    bitmap.SetPixel(x * scale + dx, y * scale + dy, color);
                }
            }
        }
    }

    return bitmap;
}

// Nearest-neighbor resize (keeps hard pixel edges at non-integer tray sizes such as 20 and 24 px).
static Bitmap Scale(Bitmap source, int size)
{
    var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            result.SetPixel(x, y, source.GetPixel(x * source.Width / size, y * source.Height / size));
        }
    }

    return result;
}

static Bitmap Pad(Bitmap source, int size)
{
    var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    var offset = (size - source.Width) / 2;
    for (var y = 0; y < source.Height; y++)
    {
        for (var x = 0; x < source.Width; x++)
        {
            result.SetPixel(x + offset, y + offset, source.GetPixel(x, y));
        }
    }

    return result;
}

static Color ParseColor(string hex) => Color.FromArgb(
    255,
    int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
    int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
    int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

static void SavePng(Bitmap bitmap, string path) => bitmap.Save(path, ImageFormat.Png);

// ICO with classic 32-bit DIB entries (best compatibility for small sizes) and a PNG entry for 256 px.
static void WriteIco(string path, Bitmap[] images)
{
    var payloads = images.Select(image => image.Width >= 256 ? PngBytes(image) : DibBytes(image)).ToArray();
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Length);

    var offset = 6 + 16 * images.Length;
    for (var i = 0; i < images.Length; i++)
    {
        writer.Write((byte)(images[i].Width >= 256 ? 0 : images[i].Width));
        writer.Write((byte)(images[i].Height >= 256 ? 0 : images[i].Height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(payloads[i].Length);
        writer.Write(offset);
        offset += payloads[i].Length;
    }

    foreach (var payload in payloads)
    {
        writer.Write(payload);
    }
}

static byte[] PngBytes(Bitmap image)
{
    using var memory = new MemoryStream();
    image.Save(memory, ImageFormat.Png);
    return memory.ToArray();
}

static byte[] DibBytes(Bitmap image)
{
    int width = image.Width, height = image.Height;
    var maskStride = (width + 31) / 32 * 4;
    using var memory = new MemoryStream();
    using var writer = new BinaryWriter(memory);
    writer.Write(40);               // BITMAPINFOHEADER size
    writer.Write(width);
    writer.Write(height * 2);       // color + AND mask
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);                // BI_RGB
    writer.Write(width * height * 4 + maskStride * height);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var c = image.GetPixel(x, y);
            writer.Write(c.B);
            writer.Write(c.G);
            writer.Write(c.R);
            writer.Write(c.A);
        }
    }

    for (var y = height - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < width; x++)
        {
            if (image.GetPixel(x, y).A == 0)
            {
                row[x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }

        writer.Write(row);
    }

    return memory.ToArray();
}
