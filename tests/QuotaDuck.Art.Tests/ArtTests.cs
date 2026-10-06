using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace QuotaDuck.Art.Tests;

internal static class Px
{
    public static int Count(IndexedImage img, params Ink[] inks) => img.Pixels.ToArray().Count(p => inks.Contains(p));

    public static string Hash(IndexedImage img) => Convert.ToHexString(SHA256.HashData(img.Pixels.ToArray().Select(p => (byte)p).ToArray()))[..16];

    public static PondScene Pond(double? r, PondCondition c = PondCondition.Normal, DuckPose? duck = null, bool right = true, int frame = 0, bool alert = false, bool busy = false) =>
        new(r, c, duck ?? (PondGeometry.IsShallow(r) ? DuckPose.Standing : DuckPose.Floating), right, alert, busy, frame);
}

public class PaletteAndImageTests
{
    [Fact]
    public void ThePaletteIsMinimal_SeventeenInks_AllDistinctlyDefined()
    {
        Assert.Equal(18, Enum.GetValues<Ink>().Length);                                   // None + 17
        uint[] colours = Enum.GetValues<Ink>().Where(i => i != Ink.None).Select(i => Palette.Standard[i]).ToArray();
        Assert.Equal(colours.Length, colours.Distinct().Count());
        Assert.All(colours, c => Assert.Equal(0xFFu, c >> 24));                           // opaque
        Assert.Equal(0u, Palette.Standard[Ink.None]);
    }

    [Fact]
    public void Subdued_PullsColoursTowardTheSky_ButKeepsOutlinesAndAlertsSharp()
    {
        Assert.Equal(Palette.Standard[Ink.Outline], Palette.Subdued[Ink.Outline]);
        Assert.Equal(Palette.Standard[Ink.Alert], Palette.Subdued[Ink.Alert]);
        foreach (Ink i in new[] { Ink.Water, Ink.Yellow, Ink.Reed })
        {
            Assert.NotEqual(Palette.Standard[i], Palette.Subdued[i]);
        }

        static int Dist(uint a, uint b) => Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255)) + Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) + Math.Abs((int)(a & 255) - (int)(b & 255));
        Assert.True(Dist(Palette.Subdued[Ink.Water], Palette.Subdued[Ink.Sky]) < Dist(Palette.Standard[Ink.Water], Palette.Standard[Ink.Sky]));
    }

    [Fact]
    public void HighContrast_UsesOnlyTheFourSystemColours()
    {
        uint win = 0xFF000000, text = 0xFFFFFFFF, hi = 0xFF1AEBFF, gray = 0xFF3FF23F;
        Palette p = Palette.HighContrast(win, text, hi, gray);
        uint[] used = Enum.GetValues<Ink>().Where(i => i != Ink.None).Select(i => p[i]).Distinct().ToArray();
        Assert.Equal(new[] { win, text, hi, gray }.Order(), used.Order());
    }

    [Fact]
    public void ToBgra_ScalesByWholeBlocks_NearestNeighbour()
    {
        var img = new IndexedImage(2, 2);
        img[0, 0] = Ink.Water;
        img[1, 0] = Ink.Reed;
        img[0, 1] = Ink.Yellow;
        uint[] px = img.ToBgra(Palette.Standard, 3);
        Assert.Equal(6 * 6, px.Length);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                Assert.Equal(Palette.Standard[Ink.Water], px[y * 6 + x]);
                Assert.Equal(Palette.Standard[Ink.Reed], px[y * 6 + x + 3]);
                Assert.Equal(Palette.Standard[Ink.Yellow], px[(y + 3) * 6 + x]);
                Assert.Equal(0u, px[(y + 3) * 6 + x + 3]);
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => img.ToBgra(Palette.Standard, 0));
    }

    [Fact]
    public void Blit_SkipsTransparent_ClipsAtTheEdges_AndMirrors()
    {
        var sprite = IndexedImage.FromRows("y.", "#o");
        var canvas = new IndexedImage(4, 4);
        canvas.Fill(Ink.Sky);
        canvas.Blit(sprite, 1, 1);
        Assert.Equal(Ink.Yellow, canvas[1, 1]);
        Assert.Equal(Ink.Sky, canvas[2, 1]);                                              // transparent sprite pixel left the canvas alone
        canvas.Blit(sprite, 3, 3);                                                        // partly outside: no exception
        var mirrored = new IndexedImage(2, 2);
        mirrored.Blit(sprite, 0, 0, mirror: true);
        Assert.Equal(Ink.None, mirrored[0, 0]);
        Assert.Equal(Ink.Yellow, mirrored[1, 0]);
        Assert.Equal(Ink.Orange, mirrored[0, 1]);
        Assert.Equal(Ink.Outline, mirrored[1, 1]);
    }

    [Fact]
    public void FromRows_RejectsRaggedRowsAndUnknownChars()
    {
        Assert.Throws<ArgumentException>(() => IndexedImage.FromRows("##", "#"));
        Assert.Throws<ArgumentException>(() => IndexedImage.FromRows("#?"));
    }

    [Theory]
    [InlineData(144, 300, 1.0, 2)]
    [InlineData(144, 300, 1.25, 2)]
    [InlineData(144, 300, 1.5, 3)]
    [InlineData(144, 300, 1.75, 3)]
    [InlineData(144, 300, 2.0, 4)]
    [InlineData(120, 240, 1.0, 2)]
    [InlineData(144, 10, 1.0, 1)]
    [InlineData(144, 300, 0.0, 1)]
    public void IntegerScale_IsAWholeNumberThatFits(int logical, double targetDip, double dpi, int expected)
    {
        Assert.Equal(expected, PixelScale.Choose(logical, targetDip, dpi));
        Assert.True(PixelScale.Choose(logical, targetDip, dpi) * logical <= Math.Max(targetDip * Math.Max(0.5, dpi), logical));   // never wider than allowed (except the 1x floor)
    }
}

public class DuckTests
{
    [Fact]
    public void FloatingDucksAreShorter_StandingDucksHaveLegs_AllSameWidth()
    {
        foreach (DuckPose pose in Enum.GetValues<DuckPose>())
        {
            IndexedImage d = DuckSprites.Build(pose);
            Assert.Equal(DuckSprites.Width, d.Width);
            bool standing = pose is DuckPose.Standing or DuckPose.SleepingStanding or DuckPose.Alert or DuckPose.Unknown;
            Assert.Equal(standing ? DuckSprites.StandingHeight : DuckSprites.FloatingHeight, d.Height);
        }

        Assert.True(Px.Count(DuckSprites.Build(DuckPose.Standing), Ink.Orange) > Px.Count(DuckSprites.Build(DuckPose.Floating), Ink.Orange));   // legs
    }

    [Fact]
    public void AwakeDuck_HasAnOpenEye_Sleeping_AClosedOne_Unknown_IsHollow()
    {
        IndexedImage awake = DuckSprites.Build(DuckPose.Floating), asleep = DuckSprites.Build(DuckPose.SleepingFloating), unknown = DuckSprites.Build(DuckPose.Unknown);
        Assert.Equal(Ink.Outline, awake[15, 3]);                                          // eye
        Assert.Equal(Ink.Yellow, asleep[15, 4]);                                          // (the awake duck's eye position is plain feather when asleep)
        Assert.Equal(Ink.Outline, asleep[14, 5]);                                         // closed-eye line
        Assert.Equal(Ink.Outline, asleep[15, 5]);
        Assert.True(Px.Count(awake, Ink.Yellow) > 40);
        Assert.Equal(0, Px.Count(unknown, Ink.Yellow, Ink.YellowShade, Ink.Orange));      // hollow: outline only
    }

    [Fact]
    public void Sprites_AreDeterministic()
    {
        foreach (DuckPose pose in Enum.GetValues<DuckPose>())
        {
            Assert.Equal(Px.Hash(DuckSprites.Build(pose)), Px.Hash(DuckSprites.Build(pose)));
        }
    }
}

public class PondTests
{
    private static readonly double[] Levels = [1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2, 0.1, 0.05, 0.0];

    [Theory]
    [InlineData(1.0, 16)]
    [InlineData(0.75, 12)]
    [InlineData(0.5, 8)]
    [InlineData(0.25, 4)]
    [InlineData(0.1, 2)]
    [InlineData(0.03, 1)]
    [InlineData(0.0, 1)]
    public void DepthRows_FollowRemaining_WithATinyPuddleAtZero(double r, int rows) => Assert.Equal(rows, PondGeometry.DepthRows(r));

    [Fact]
    public void UnknownRemaining_HasNoWaterDepth_AndShallowThresholds()
    {
        Assert.Equal(0, PondGeometry.DepthRows(null));
        Assert.True(PondGeometry.IsShallow(0.3));
        Assert.True(PondGeometry.IsShallow(0.0));
        Assert.False(PondGeometry.IsShallow(0.5));
        Assert.False(PondGeometry.IsShallow(1.0));
    }

    [Fact]
    public void BowlNarrowsWithDepth()
    {
        int prev = int.MaxValue;
        for (int d = 0; d < PondGeometry.MaxDepth; d++)
        {
            int hw = PondGeometry.HalfWidth(d);
            Assert.True(hw <= prev);
            Assert.InRange(hw, 1, PondGeometry.RimHalf);
            prev = hw;
        }

        Assert.Equal(PondGeometry.RimHalf, PondGeometry.HalfWidth(0));
    }

    [Fact]
    public void WaterRecedes_Monotonically_AsRemainingFalls_AndTheShoreAppears()
    {
        int lastWater = int.MaxValue, lastWet = -1, lastStone = -1;
        foreach (double r in Levels)
        {
            IndexedImage img = Px.Pond(r).Render();
            int water = Px.Count(img, Ink.Water, Ink.WaterDeep);
            int wet = Px.Count(img, Ink.WetSand);
            int stone = Px.Count(img, Ink.Stone);
            Assert.True(water <= lastWater, $"water grew at r={r}");
            Assert.True(wet >= lastWet, $"exposed shore shrank at r={r}");
            Assert.True(stone >= lastStone, $"stones disappeared at r={r}");
            (lastWater, lastWet, lastStone) = (water, wet, stone);
        }

        Assert.Equal(0, Px.Count(Px.Pond(1.0).Render(), Ink.Stone));                      // a full pond hides its stones
        Assert.Equal(9, Px.Count(Px.Pond(0.0).Render(), Ink.Stone));                      // a puddle shows all three stones (3 px each)
    }

    [Fact]
    public void AFullPond_FillsTheBowlToTheRim_APuddleIsTinyAndOneRowDeep()
    {
        IndexedImage full = Px.Pond(1.0).Render();
        int rimY = PondGeometry.GroundY;
        int rimWater = Enumerable.Range(0, full.Width).Count(x => full[x, rimY + 1] is Ink.Water or Ink.WaterHi or Ink.WaterDeep);
        Assert.InRange(rimWater, 90, 100);

        IndexedImage puddle = Px.Pond(0.0).Render();
        int floorY = PondGeometry.GroundY + PondGeometry.MaxDepth - 1;
        int waterCols = Enumerable.Range(0, puddle.Width).Count(x => puddle[x, floorY] is Ink.Water or Ink.WaterHi or Ink.WaterDeep);
        Assert.InRange(waterCols, 8, 14);
        Assert.Equal(0, Enumerable.Range(0, puddle.Width).Count(x => puddle[x, floorY - 1] is Ink.Water or Ink.WaterDeep));
    }

    [Fact]
    public void ElapsedWindow_IsAHatchedFullBowl_NeverSolidWater()
    {
        IndexedImage unknown = Px.Pond(null, PondCondition.Unknown, DuckPose.Unknown).Render();
        IndexedImage full = Px.Pond(1.0).Render();
        Assert.NotEqual(Px.Hash(full), Px.Hash(unknown));
        Assert.Equal(0, Px.Count(unknown, Ink.WaterDeep));                                 // no solid deep water anywhere
        int water = Px.Count(unknown, Ink.Water), hi = Px.Count(unknown, Ink.WaterHi);
        Assert.True(water > 100 && hi > 100);
        Assert.InRange(water / (double)(water + hi), 0.35, 0.65);                          // stripes, roughly half and half
        Assert.True(Px.Count(unknown, Ink.Yellow) == 0);                                   // hollow duck
        Assert.True(Px.Count(unknown, Ink.Outline) > 30);
    }

    [Fact]
    public void NoDataAndError_AreDryHatchedBowls_WithNoWater()
    {
        foreach (PondCondition c in new[] { PondCondition.Empty, PondCondition.Error })
        {
            IndexedImage img = Px.Pond(null, c, c == PondCondition.Error ? DuckPose.Alert : DuckPose.Unknown).Render();
            Assert.Equal(0, Px.Count(img, Ink.Water, Ink.WaterDeep));
            Assert.True(Px.Count(img, Ink.WetSand) > 100);
        }

        Assert.True(Px.Count(Px.Pond(null, PondCondition.Error, DuckPose.Alert).Render(), Ink.Alert) > 8);                  // the "!"
        Assert.Equal(0, Px.Count(Px.Pond(null, PondCondition.Empty, DuckPose.Unknown).Render(), Ink.Alert));
    }

    [Fact]
    public void AlertFlag_AddsAWarningMark_ToARealPond()
    {
        int plain = Px.Count(Px.Pond(0.6).Render(), Ink.Alert);
        int alert = Px.Count(Px.Pond(0.6, alert: true).Render(), Ink.Alert);
        Assert.Equal(0, plain);
        Assert.True(alert > 8);
    }

    [Fact]
    public void Animation_IsTwoFramesOnly_RippleZzzAndBusyDots_AndParityRepeats()
    {
        Assert.NotEqual(Px.Hash(Px.Pond(0.7, frame: 0).Render()), Px.Hash(Px.Pond(0.7, frame: 1).Render()));         // ripple
        Assert.Equal(Px.Hash(Px.Pond(0.7, frame: 0).Render()), Px.Hash(Px.Pond(0.7, frame: 2).Render()));           // 2-frame cycle
        PondScene sleeping(int f) => Px.Pond(0.7, PondCondition.Subdued, DuckPose.SleepingFloating, frame: f);
        Assert.NotEqual(Px.Hash(sleeping(0).Render()), Px.Hash(sleeping(1).Render()));                              // z bobs
        string Sky(PondScene s)                                                                                       // only the rows above the bank: the z, not the ripple
        {
            IndexedImage img = s.Render();
            var rows = new IndexedImage(img.Width, PondGeometry.GroundY);
            for (int y = 0; y < rows.Height; y++) { for (int x = 0; x < img.Width; x++) { rows[x, y] = img[x, y]; } }
            return Px.Hash(rows);
        }

        Assert.NotEqual(Sky(sleeping(0)), Sky(sleeping(1)));                                                         // the z itself moves between the two frames
        Assert.Equal(Sky(Px.Pond(0.7, frame: 0)), Sky(Px.Pond(0.7, frame: 1)));                                      // and an awake duck's sky does not
        Assert.NotEqual(Px.Hash(Px.Pond(0.7, busy: true, frame: 0).Render()), Px.Hash(Px.Pond(0.7, busy: false, frame: 0).Render()));
        Assert.NotEqual(Px.Hash(Px.Pond(0.7, busy: true, frame: 0).Render()), Px.Hash(Px.Pond(0.7, busy: true, frame: 1).Render()));
        // a still scene (no ripple, no z) does not change with the frame
        PondScene still(int f) => Px.Pond(null, PondCondition.Empty, DuckPose.Unknown, frame: f);
        Assert.Equal(Px.Hash(still(0).Render()), Px.Hash(still(1).Render()));
    }

    [Fact]
    public void SleepingDuck_AndStandingDuck_AreDistinctPoses()
    {
        IndexedImage floatAwake = Px.Pond(0.8).Render();
        IndexedImage floatAsleep = Px.Pond(0.8, PondCondition.Subdued, DuckPose.SleepingFloating).Render();
        IndexedImage standing = Px.Pond(0.1).Render();
        Assert.NotEqual(Px.Hash(floatAwake), Px.Hash(floatAsleep));
        Assert.True(Px.Count(standing, Ink.Orange) > Px.Count(floatAwake, Ink.Orange));
    }

    [Fact]
    public void AFloatingPose_OnAShallowPond_FallsBackToStanding_NeverFloatsOnNothing()
    {
        IndexedImage forced = Px.Pond(0.05, duck: DuckPose.Floating).Render();
        IndexedImage standing = Px.Pond(0.05, duck: DuckPose.Standing).Render();
        Assert.Equal(Px.Hash(standing), Px.Hash(forced));
    }

    [Fact]
    public void FaceDirection_MirrorsTheDuck_NotThePond()
    {
        IndexedImage right = Px.Pond(0.7, right: true).Render(), left = Px.Pond(0.7, right: false).Render();
        Assert.NotEqual(Px.Hash(right), Px.Hash(left));
        // the water body (excluding the duck) is symmetric in count
        Assert.InRange(Math.Abs(Px.Count(right, Ink.WaterDeep) - Px.Count(left, Ink.WaterDeep)), 0, 25);
    }

    [Fact]
    public void Rendering_IsDeterministic_AndTheRightSize()
    {
        PondScene s = Px.Pond(0.42);
        Assert.Equal(Px.Hash(s.Render()), Px.Hash(s.Render()));
        Assert.Equal((144, 32), (s.Render().Width, s.Render().Height));
        Assert.Equal((144, 32), (s.Width, s.Height));
        Assert.Same(Palette.Subdued, Px.Pond(0.3, PondCondition.Subdued).Palette);
        Assert.Same(Palette.Standard, Px.Pond(0.3).Palette);
    }

    /// <summary>Visual-regression guard: any change to the art changes these hashes on purpose (update them with the screenshots).</summary>
    [Theory]
    [MemberData(nameof(Golden))]
    public void GoldenHashes(string name, PondScene scene, string expected)
    {
        string actual = Px.Hash(scene.Render());
        Assert.True(actual == expected, $"{name}: actual hash {actual}");
    }

    public static IEnumerable<object[]> Golden() =>
    [
        ["full", Px.Pond(1.0), "F03E7B95E54EC9EC"],
        ["medium", Px.Pond(0.5), "B7A60455BD89BFFE"],
        ["low", Px.Pond(0.2), "A1FECC354F6BE3B8"],
        ["puddle", Px.Pond(0.0), "C758F427D91E7C5B"],
        ["sleeping", Px.Pond(0.55, PondCondition.Subdued, DuckPose.SleepingFloating, false), "DFF560B15582BCE8"],
        ["elapsed", Px.Pond(null, PondCondition.Unknown, DuckPose.Unknown), "4854EC4CB8CD49BB"],
        ["nodata", Px.Pond(null, PondCondition.Empty, DuckPose.Unknown, false), "491DE5330A0C1342"],
        ["error", Px.Pond(null, PondCondition.Error, DuckPose.Alert), "5793EC9F2AB6131D"],
    ];
}

public class GaugeTests
{
    [Fact]
    public void FilledWidth_FollowsRemaining_AndUnknownIsHatchedNotFull()
    {
        int Water(GaugeScene g) => Px.Count(g.Render(), Ink.Water, Ink.WaterHi, Ink.WaterDeep);
        Assert.True(Water(new GaugeScene(1.0, GaugeCondition.Normal)) > Water(new GaugeScene(0.5, GaugeCondition.Normal)));
        Assert.True(Water(new GaugeScene(0.5, GaugeCondition.Normal)) > Water(new GaugeScene(0.1, GaugeCondition.Normal)));
        Assert.Equal(0, Water(new GaugeScene(0.0, GaugeCondition.Normal)));
        IndexedImage unknown = new GaugeScene(null, GaugeCondition.Unknown).Render();
        Assert.NotEqual(Px.Hash(new GaugeScene(1.0, GaugeCondition.Normal).Render()), Px.Hash(unknown));
        Assert.Equal(0, Px.Count(unknown, Ink.WaterDeep));
        int solid = Px.Count(unknown, Ink.Water), pale = Px.Count(unknown, Ink.WaterHi);
        Assert.InRange(solid / (double)(solid + pale), 0.35, 0.65);                         // diagonal stripes, not a solid fill
        Assert.Equal(0, Px.Count(new GaugeScene(null, GaugeCondition.Empty).Render(), Ink.Water));
        Assert.Same(Palette.Subdued, new GaugeScene(0.5, GaugeCondition.Subdued).Palette);
        Assert.Equal((120, 6), (new GaugeScene(0.5, GaugeCondition.Normal).Width, new GaugeScene(0.5, GaugeCondition.Normal).Height));
    }
}

public class EncoderAndIconTests
{
    [Fact]
    public void Png_HasAValidStructure_RoundTripsThroughZlib_AndKnownCrc()
    {
        uint[] px = [0xFF112233, 0x80445566, 0x00000000, 0xFFFFFFFF];
        byte[] png = PngEncoder.Encode(px, 2, 2);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(2, ReadInt(png, 16));
        Assert.Equal(2, ReadInt(png, 20));
        Assert.Equal([0xAE, 0x42, 0x60, 0x82], png[^4..]);                               // CRC of an empty IEND chunk
        int idatLen = ReadInt(png, 33);
        using var z = new ZLibStream(new MemoryStream(png, 41, idatLen), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] r = raw.ToArray();
        Assert.Equal(2 * (2 * 4 + 1), r.Length);
        Assert.Equal([0, 0x11, 0x22, 0x33, 0xFF], r[..5]);                                // filter byte, RGBA of the first pixel
        Assert.Equal(PngEncoder.Encode(px, 2, 2), png);                                  // deterministic
    }

    private static int ReadInt(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    [Fact]
    public void TrayIco_IsAMultiResolutionDibIcon_NotPng_AndDeterministic()
    {
        byte[] ico = TrayIcon.BuildIco();
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        int count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal(3, count);
        int[] sizes = [16, 32, 48];
        for (int i = 0; i < count; i++)
        {
            int e = 6 + i * 16;
            Assert.Equal(sizes[i], ico[e]);
            Assert.Equal(sizes[i], ico[e + 1]);
            Assert.Equal(32, BitConverter.ToUInt16(ico, e + 6));
            int len = BitConverter.ToInt32(ico, e + 8), off = BitConverter.ToInt32(ico, e + 12);
            Assert.Equal(40, BitConverter.ToInt32(ico, off));                             // BITMAPINFOHEADER: a DIB, so no PNG decoder (GDI+/WIC) is involved
            Assert.Equal(sizes[i], BitConverter.ToInt32(ico, off + 4));
            Assert.Equal(sizes[i] * 2, BitConverter.ToInt32(ico, off + 8));
            Assert.NotEqual(0x474E5089, BitConverter.ToInt32(ico, off));                  // not a PNG signature
            Assert.True(off + len <= ico.Length);
        }

        Assert.Equal(ico, TrayIcon.BuildIco());
    }

    [Fact]
    public void TrayIcon_HasATransparentCornerAndADuckHead()
    {
        IndexedImage logical = TrayIcon.Logical();
        Assert.Equal(Ink.None, logical[0, 0]);
        Assert.True(Px.Count(logical, Ink.Yellow) > 60);
        Assert.True(Px.Count(logical, Ink.Orange) >= 6);
    }
}

public class ArtArchitectureTests
{
    [Fact]
    public void TheArtAssembly_IsPureBcl_NoWpfNoDrawingNoIo()
    {
        Assembly art = typeof(Ink).Assembly;
        string[] refs = art.GetReferencedAssemblies().Select(r => r.Name!).ToArray();
        Assert.All(refs, n => Assert.True(n.StartsWith("System", StringComparison.Ordinal) || n is "netstandard" or "mscorlib", n));
        string[] forbidden = ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Windows", "System.Drawing", "System.Net", "System.Diagnostics.Process", "Microsoft.Win32"];
        Assert.DoesNotContain(refs, r => forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal)));

        string dir = Path.Combine(Root(), "src", "QuotaDuck.Art");
        foreach (string f in Directory.EnumerateFiles(dir, "*.cs").Where(f => !f.Contains("\\obj\\")))
        {
            string text = File.ReadAllText(f);
            foreach (string bad in new[] { "File.", "Directory.", "FileStream", "UtcNow", "DateTime.Now", "Environment.", "Process", "DllImport", "LibraryImport", "Random" })
            {
                Assert.False(text.Contains(bad, StringComparison.Ordinal), $"{Path.GetFileName(f)} uses {bad}");
            }
        }
    }

    private static string Root()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "QuotaDuck.sln")))
        {
            d = Path.GetDirectoryName(d);
        }

        return d!;
    }
}
