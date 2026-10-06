using System.IO.Compression;

namespace QuotaDuck.Art;

/// <summary>Minimal deterministic PNG writer (RGBA, 8 bit) — used for the tray icon (PNG-in-ICO) and for review images. BCL only.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildTable();

    public static byte[] Encode(uint[] bgra, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width * 4 + 1);
            raw[o] = 0;                                                       // filter: none
            for (int x = 0; x < width; x++)
            {
                uint p = bgra[y * width + x];
                int i = o + 1 + x * 4;
                raw[i] = (byte)(p >> 16);
                raw[i + 1] = (byte)(p >> 8);
                raw[i + 2] = (byte)p;
                raw[i + 3] = (byte)(p >> 24);
            }
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", [.. BigEndian((uint)width), .. BigEndian((uint)height), 8, 6, 0, 0, 0]);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zs.Write(raw);
            }

            Chunk(ms, "IDAT", z.ToArray());
        }

        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        s.Write(BigEndian((uint)data.Length));
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in t)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        s.Write(BigEndian(crc ^ 0xFFFFFFFFu));
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }
}

/// <summary>The static tray icon: a duck head on a ripple, drawn once at 16 px logical size and scaled by integers to 16/32/48 px.</summary>
public static class TrayIcon
{
    public static IndexedImage Logical()
    {
        var img = new IndexedImage(16, 16);
        img.OutlineEllipse(8, 7, 6, 6, Ink.Outline, Ink.Yellow);              // head
        img.FillEllipse(6, 9, 2, 1, Ink.YellowShade);                         // cheek shade
        img[10, 5] = Ink.Outline;                                             // eye
        img.FillRect(13, 7, 3, 2, Ink.Orange);                                // beak
        img[13, 9] = Ink.Orange;
        img.FillRect(1, 13, 14, 1, Ink.WaterDeep);                            // water line
        img.FillRect(3, 14, 10, 1, Ink.Water);
        img.FillRect(5, 15, 6, 1, Ink.WaterHi);
        return img;
    }

    /// <summary>
    /// A multi-resolution ICO (16, 32, 48 px) with classic 32-bit DIB payloads (not PNG): Windows decodes those natively, so loading the icon
    /// does not pull GDI+/WIC into the process. Deterministic: the same bytes every time.
    /// </summary>
    public static byte[] BuildIco()
    {
        IndexedImage logical = Logical();
        (int Size, byte[] Dib)[] images = new[] { 1, 2, 3 }.Select(k => (16 * k, Dib(logical.ToBgra(Palette.Standard, k), 16 * k))).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Length);
        int offset = 6 + 16 * images.Length;
        foreach ((int size, byte[] dib) in images)
        {
            w.Write((byte)size);
            w.Write((byte)size);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(dib.Length);
            w.Write(offset);
            offset += dib.Length;
        }

        foreach ((_, byte[] dib) in images)
        {
            w.Write(dib);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Dib(uint[] bgra, int size)
    {
        int maskStride = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);                                   // BITMAPINFOHEADER
        w.Write(size);
        w.Write(size * 2);                             // XOR bitmap + AND mask
        w.Write((short)1);
        w.Write((short)32);
        w.Write(0);
        w.Write(size * size * 4 + maskStride * size);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        for (int y = size - 1; y >= 0; y--)            // bottom-up
        {
            for (int x = 0; x < size; x++)
            {
                w.Write(bgra[y * size + x]);
            }
        }

        w.Write(new byte[maskStride * size]);          // AND mask: all zero (the alpha channel carries the shape)
        w.Flush();
        return ms.ToArray();
    }
}
