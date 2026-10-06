namespace QuotaDuck.Art;

/// <summary>The whole palette, by role. 0 is transparent. Everything in the art is drawn with these 17 inks.</summary>
public enum Ink : byte
{
    None = 0,
    Sky = 1,
    Sand = 2,
    SandDark = 3,
    WetSand = 4,
    Water = 5,
    WaterDeep = 6,
    WaterHi = 7,
    Reed = 8,
    ReedDark = 9,
    Stone = 10,
    StoneDark = 11,
    Yellow = 12,
    YellowShade = 13,
    Orange = 14,
    Outline = 15,
    White = 16,
    Alert = 17,
}

/// <summary>Maps inks to BGRA pixels (opaque, except <see cref="Ink.None"/>). Three variants: standard, subdued (stale/sleeping), high contrast.</summary>
public sealed class Palette
{
    private readonly uint[] _bgra = new uint[18];

    private Palette()
    {
    }

    public uint this[Ink ink] => _bgra[(int)ink];

    private static uint Argb(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    private static (byte R, byte G, byte B) Rgb(uint p) => ((byte)(p >> 16), (byte)(p >> 8), (byte)p);

    public static Palette Standard { get; } = BuildStandard();

    /// <summary>Stale / sleeping: every ink except outlines and alert marks is pulled 45% toward the sky, so the pond looks quiet.</summary>
    public static Palette Subdued { get; } = BuildSubdued();

    /// <summary>Windows High Contrast: four system colours only (window, text, highlight, gray text).</summary>
    public static Palette HighContrast(uint window, uint text, uint highlight, uint gray)
    {
        var p = new Palette();
        void Set(Ink i, uint c) => p._bgra[(int)i] = 0xFF000000u | (c & 0xFFFFFFu);
        Set(Ink.Sky, window);
        Set(Ink.Sand, window);
        Set(Ink.SandDark, text);
        Set(Ink.WetSand, gray);
        Set(Ink.Water, highlight);
        Set(Ink.WaterDeep, highlight);
        Set(Ink.WaterHi, window);
        Set(Ink.Reed, text);
        Set(Ink.ReedDark, text);
        Set(Ink.Stone, gray);
        Set(Ink.StoneDark, text);
        Set(Ink.Yellow, window);
        Set(Ink.YellowShade, gray);
        Set(Ink.Orange, text);
        Set(Ink.Outline, text);
        Set(Ink.White, window);
        Set(Ink.Alert, text);
        return p;
    }

    private static Palette BuildStandard()
    {
        var p = new Palette();
        void Set(Ink i, byte r, byte g, byte b) => p._bgra[(int)i] = Argb(r, g, b);
        Set(Ink.Sky, 0xE3, 0xEE, 0xE8);
        Set(Ink.Sand, 0xDC, 0xCB, 0x9E);
        Set(Ink.SandDark, 0xA8, 0x92, 0x5C);
        Set(Ink.WetSand, 0x8A, 0x77, 0x4C);
        Set(Ink.Water, 0x4F, 0xA6, 0xD6);
        Set(Ink.WaterDeep, 0x2E, 0x7C, 0xAE);
        Set(Ink.WaterHi, 0xB4, 0xE0, 0xF4);
        Set(Ink.Reed, 0x62, 0x92, 0x3C);
        Set(Ink.ReedDark, 0x3C, 0x62, 0x28);
        Set(Ink.Stone, 0x92, 0x92, 0x92);
        Set(Ink.StoneDark, 0x5C, 0x5C, 0x5C);
        Set(Ink.Yellow, 0xF3, 0xC3, 0x30);
        Set(Ink.YellowShade, 0xC9, 0x9A, 0x14);
        Set(Ink.Orange, 0xE5, 0x7A, 0x1C);
        Set(Ink.Outline, 0x2A, 0x2A, 0x2A);
        Set(Ink.White, 0xFF, 0xFF, 0xFF);
        Set(Ink.Alert, 0xC6, 0x28, 0x28);
        return p;
    }

    private static Palette BuildSubdued()
    {
        Palette s = Standard;
        var p = new Palette();
        (byte sr, byte sg, byte sb) = Rgb(s[Ink.Sky]);
        for (int i = 1; i < 18; i++)
        {
            if (i is (int)Ink.Outline or (int)Ink.Alert)
            {
                p._bgra[i] = s._bgra[i];
                continue;
            }

            (byte r, byte g, byte b) = Rgb(s._bgra[i]);
            p._bgra[i] = Argb(Mix(r, sr), Mix(g, sg), Mix(b, sb));
        }

        return p;

        static byte Mix(byte c, byte toward) => (byte)((c * 55 + toward * 45 + 50) / 100);
    }
}

/// <summary>A small paletted image (0 = transparent). All pixel art is composed on these, then converted once with a <see cref="Palette"/>.</summary>
public sealed class IndexedImage(int width, int height)
{
    private readonly Ink[] _px = new Ink[width * height];

    public int Width { get; } = width;

    public int Height { get; } = height;

    public Ink this[int x, int y]
    {
        get => (uint)x < (uint)Width && (uint)y < (uint)Height ? _px[y * Width + x] : Ink.None;
        set
        {
            if ((uint)x < (uint)Width && (uint)y < (uint)Height)
            {
                _px[y * Width + x] = value;
            }
        }
    }

    public ReadOnlySpan<Ink> Pixels => _px;

    public void Fill(Ink ink) => Array.Fill(_px, ink);

    public void FillRect(int x, int y, int w, int h, Ink ink)
    {
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                this[x + i, y + j] = ink;
            }
        }
    }

    /// <summary>Filled ellipse with integer extents (centre cx,cy; radii rx,ry), drawn row by row.</summary>
    public void FillEllipse(int cx, int cy, int rx, int ry, Ink ink)
    {
        for (int dy = -ry; dy <= ry; dy++)
        {
            double t = ry == 0 ? 0 : (double)dy / ry;
            int half = (int)Math.Floor(rx * Math.Sqrt(Math.Max(0, 1 - t * t)) + 0.5);
            for (int dx = -half; dx <= half; dx++)
            {
                this[cx + dx, cy + dy] = ink;
            }
        }
    }

    public void OutlineEllipse(int cx, int cy, int rx, int ry, Ink outline, Ink fill)
    {
        FillEllipse(cx, cy, rx, ry, outline);
        if (rx > 1 && ry > 1)
        {
            FillEllipse(cx, cy, rx - 1, ry - 1, fill);
        }
    }

    /// <summary>Draws <paramref name="sprite"/> with its top-left at (x,y); transparent pixels are skipped. <paramref name="mirror"/> flips it horizontally.</summary>
    public void Blit(IndexedImage sprite, int x, int y, bool mirror = false)
    {
        for (int j = 0; j < sprite.Height; j++)
        {
            for (int i = 0; i < sprite.Width; i++)
            {
                Ink ink = sprite[mirror ? sprite.Width - 1 - i : i, j];
                if (ink != Ink.None)
                {
                    this[x + i, y + j] = ink;
                }
            }
        }
    }

    /// <summary>Replaces every opaque pixel by <paramref name="ink"/> where <paramref name="keep"/> is false (used to hollow out the "unknown" duck).</summary>
    public void Replace(Ink from, Ink to)
    {
        for (int i = 0; i < _px.Length; i++)
        {
            if (_px[i] == from)
            {
                _px[i] = to;
            }
        }
    }

    /// <summary>BGRA pixels at integer scale <paramref name="k"/> (nearest neighbour: every source pixel becomes a k x k block).</summary>
    public uint[] ToBgra(Palette palette, int k = 1)
    {
        if (k < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(k));
        }

        int w = Width * k, h = Height * k;
        var outPx = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy = y / k;
            for (int x = 0; x < w; x++)
            {
                outPx[y * w + x] = palette[_px[sy * Width + x / k]];
            }
        }

        return outPx;
    }

    public static IndexedImage FromRows(params string[] rows)
    {
        var img = new IndexedImage(rows[0].Length, rows.Length);
        for (int y = 0; y < rows.Length; y++)
        {
            if (rows[y].Length != img.Width)
            {
                throw new ArgumentException($"row {y} has width {rows[y].Length}, expected {img.Width}");
            }

            for (int x = 0; x < img.Width; x++)
            {
                img[x, y] = rows[y][x] switch
                {
                    '.' => Ink.None,
                    '#' => Ink.Outline,
                    'w' => Ink.White,
                    '!' => Ink.Alert,
                    'o' => Ink.Orange,
                    'y' => Ink.Yellow,
                    's' => Ink.YellowShade,
                    'b' => Ink.WaterDeep,
                    _ => throw new ArgumentException("unknown ink char " + rows[y][x]),
                };
            }
        }

        return img;
    }
}
