namespace DockSense.IconGen;

/// <summary>
/// Pixel-art sources for every DockSense icon. One character = one pixel; '.' is transparent.
/// Letters map to colors through <see cref="Palettes"/>. Edit these grids and re-run the generator to change the icons.
/// </summary>
internal static class Sprites
{
    /// <summary>Base palette (Sweetie 16 based). S/s are the screen/accent colors and come from the state palette.</summary>
    public static readonly Dictionary<char, string> Base = new()
    {
        ['K'] = "#1A1C2C", // outline
        ['W'] = "#F4F4F4", // body
        ['G'] = "#94B0C2", // light gray / metal
        ['D'] = "#566C86", // controls
        ['T'] = "#333C57", // dark shroud
        ['Y'] = "#FFCD75", // gold
    };

    public static readonly Dictionary<string, (string S, string s)> Palettes = new()
    {
        ["app"] = ("#41A6F6", "#73EFF7"),
        ["handheld"] = ("#38B764", "#A7F070"),
        ["docked"] = ("#3B5DC9", "#41A6F6"),
        ["egpu"] = ("#7B5CD6", "#B8A4F5"),
        ["paused"] = ("#566C86", "#94B0C2"),
    };

    public static readonly string[] Handheld16 =
    [
        "................",
        "................",
        "................",
        "................",
        "..KKKKKKKKKKKK..",
        ".KWWWWWWWWWWWWK.",
        "KWWWKKKKKKKKWWWK",
        "KWDWKsSSSSSKWDWK",
        "KDDDKSSSSSSKDWDK",
        "KWDWKSSSSSSKWDWK",
        "KWWWKKKKKKKKWWWK",
        ".KWWWWWWWWWWWWK.",
        "..KKKKKKKKKKKK..",
        "................",
        "................",
        "................",
    ];

    public static readonly string[] Paused16 =
    [
        "................",
        "................",
        "................",
        "................",
        "..KKKKKKKKKKKK..",
        ".KWWWWWWWWWWWWK.",
        "KWWWKKKKKKKKWWWK",
        "KWDWKSGSSGSKWDWK",
        "KDDDKSGSSGSKDWDK",
        "KWDWKSGSSGSKWDWK",
        "KWWWKKKKKKKKWWWK",
        ".KWWWWWWWWWWWWK.",
        "..KKKKKKKKKKKK..",
        "................",
        "................",
        "................",
    ];

    public static readonly string[] Monitor16 =
    [
        "................",
        "................",
        ".KKKKKKKKKKKKKK.",
        ".KWWWWWWWWWWWWK.",
        ".KWKKKKKKKKKKWK.",
        ".KWKsSSSSSSSKWK.",
        ".KWKSSSSSSSSKWK.",
        ".KWKSSSSSSSSKWK.",
        ".KWKSSSSSSSSKWK.",
        ".KWKKKKKKKKKKWK.",
        ".KGGGGGGGGGGGGK.",
        ".KKKKKKKKKKKKKK.",
        "......KWWK......",
        "....KKKWWKKK....",
        "....KGGGGGGK....",
        "....KKKKKKKK....",
    ];

    public static readonly string[] Gpu16 =
    [
        "................",
        "................",
        "KK..............",
        "KGKKKKKKKKKKKKKK",
        "KGKTTTKKKTTTTTTK",
        "KGKTTKSSSKTTGTGK",
        "KGKTKSSsSSKTGTGK",
        "KGKTKSsKsSKTGTGK",
        "KGKTKSSsSSKTGTGK",
        "KGKTTKSSSKTTGTGK",
        "KGKTTTKKKTTTTTTK",
        "KGKKKKKKKKKKKKKK",
        "KK...KYYYYYYK...",
        ".....KKKKKKKK...",
        "................",
        "................",
    ];

    /// <summary>The app icon at 24x24: one-piece widescreen handheld.</summary>
    public static readonly string[] Handheld24 = BuildHandheld24();

    private static string[] BuildHandheld24()
    {
        var g = new char[24, 24];
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                g[y, x] = '.';
            }
        }

        Rect(g, 1, 7, 22, 17, 'W');
        const int r = 3; // stepped corners
        for (var i = 0; i < r; i++)
        {
            for (var j = 0; j < r - i; j++)
            {
                g[7 + i, 1 + j] = g[7 + i, 22 - j] = g[17 - i, 1 + j] = g[17 - i, 22 - j] = '.';
            }
        }

        Outline(g, 'W', 'K');
        Rect(g, 6, 8, 17, 16, 'K');
        Rect(g, 7, 9, 16, 15, 'S');
        g[9, 7] = g[9, 8] = g[9, 9] = g[10, 7] = 's';
        Rect(g, 2, 12, 4, 12, 'D');
        Rect(g, 3, 11, 3, 13, 'D');
        g[10, 20] = g[14, 20] = g[12, 19] = g[12, 21] = 'D';

        return Enumerable.Range(0, 24)
            .Select(y => new string(Enumerable.Range(0, 24).Select(x => g[y, x]).ToArray()))
            .ToArray();
    }

    private static void Rect(char[,] g, int x0, int y0, int x1, int y1, char c)
    {
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                g[y, x] = c;
            }
        }
    }

    private static void Outline(char[,] g, char body, char line)
    {
        var size = g.GetLength(0);
        var marks = new List<(int X, int Y)>();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (g[y, x] == body)
                {
                    continue;
                }

                if ((x > 0 && g[y, x - 1] == body) || (x < size - 1 && g[y, x + 1] == body) ||
                    (y > 0 && g[y - 1, x] == body) || (y < size - 1 && g[y + 1, x] == body))
                {
                    marks.Add((x, y));
                }
            }
        }

        foreach (var (x, y) in marks)
        {
            g[y, x] = line;
        }
    }
}
