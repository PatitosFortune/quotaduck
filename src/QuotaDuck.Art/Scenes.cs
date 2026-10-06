namespace QuotaDuck.Art;

public enum DuckPose
{
    /// <summary>Awake, floating on the water.</summary>
    Floating = 0,

    /// <summary>Awake, standing on the bottom of a shallow pond.</summary>
    Standing = 1,

    /// <summary>Asleep (eyes closed, a "z" beside the head), floating.</summary>
    SleepingFloating = 2,

    /// <summary>Asleep, standing.</summary>
    SleepingStanding = 3,

    /// <summary>Hollow outline with a "?": the state is not known.</summary>
    Unknown = 4,

    /// <summary>Standing, with a red "!": something is wrong (restrained: no other change).</summary>
    Alert = 5,
}

public enum PondCondition
{
    /// <summary>Real water level, regular colours.</summary>
    Normal = 0,

    /// <summary>Real water level (last known), quiet colours: the data is old.</summary>
    Subdued = 1,

    /// <summary>The window elapsed: a full bowl of HATCHED water — refilled in principle, value unknown, never drawn as solid 100%.</summary>
    Unknown = 2,

    /// <summary>No data yet: a dry hatched bowl.</summary>
    Empty = 3,

    /// <summary>Unavailable / error and no data: a dry hatched bowl.</summary>
    Error = 4,
}

public abstract record ArtScene
{
    public abstract int Width { get; }

    public abstract int Height { get; }

    public abstract Palette Palette { get; }

    public abstract IndexedImage Render();
}

/// <summary>Procedural duck sprites (facing right; mirror when blitting). 20 px wide.</summary>
public static class DuckSprites
{
    public const int Width = 20;
    public const int FloatingHeight = 12;     // rows 0..11, the last row sits on the water surface
    public const int StandingHeight = 16;     // rows 0..15, the feet are on the last row

    public static IndexedImage Build(DuckPose pose)
    {
        bool standing = pose is DuckPose.Standing or DuckPose.SleepingStanding or DuckPose.Alert or DuckPose.Unknown;
        bool sleeping = pose is DuckPose.SleepingFloating or DuckPose.SleepingStanding;
        bool hollow = pose == DuckPose.Unknown;
        var img = new IndexedImage(Width, standing ? StandingHeight : FloatingHeight);
        Ink fill = hollow ? Ink.Sky : Ink.Yellow;
        Ink beak = hollow ? Ink.Outline : Ink.Orange;

        img.OutlineEllipse(2, 7, 2, 2, Ink.Outline, fill);                                 // tail nub
        img.OutlineEllipse(9, 9, 7, 4, Ink.Outline, fill);                                 // body
        if (!hollow)
        {
            img.FillEllipse(8, 10, 3, 1, Ink.YellowShade);                                 // wing
        }

        int hy = sleeping ? 5 : 4;
        img.OutlineEllipse(14, hy, 3, 3, Ink.Outline, fill);                               // head
        img[18, hy] = beak;
        img[19, hy] = beak;
        img[18, hy + 1] = beak;
        if (sleeping)
        {
            img[14, hy] = Ink.Outline;                                                     // closed eye
            img[15, hy] = Ink.Outline;
        }
        else
        {
            img[15, hy - 1] = Ink.Outline;                                                 // open eye
        }

        if (standing)
        {
            Ink leg = hollow ? Ink.Outline : Ink.Orange;
            foreach (int x in new[] { 7, 11 })
            {
                img[x, 14] = leg;
                img[x, 15] = leg;
                img[x + 1, 15] = leg;
            }
        }

        return img;
    }
}

public static class Glyphs
{
    public static IndexedImage Z { get; } = IndexedImage.FromRows("#####", "...#.", "..#..", ".#...", "#####");

    public static IndexedImage Question { get; } = IndexedImage.FromRows(".###.", "#...#", "....#", "..##.", "..#..", ".....", "..#..");

    public static IndexedImage Alert { get; } = IndexedImage.FromRows("!!", "!!", "!!", "!!", "..", "!!");
}

public static class PondGeometry
{
    public const int Width = 144;
    public const int Height = 32;
    public const int GroundY = 12;
    public const int MaxDepth = 16;                 // water rows when the pond is full
    public const int Cx = 72;
    public const int RimHalf = 50;
    public const int FloatingMinDepth = 6;          // below this the duck stands on the bottom

    /// <summary>Half-width of the bowl at depth row d (0 = rim). An ellipse: narrower the deeper, so a falling level also narrows the water.</summary>
    public static int HalfWidth(int d) => (int)Math.Floor(RimHalf * Math.Sqrt(Math.Max(0, 1 - Math.Pow(d / 17.0, 2))));

    /// <summary>Water rows for a remaining fraction: 16 when full, 1 for a puddle (a limit reached still leaves a tiny puddle), 0 for unknown.</summary>
    public static int DepthRows(double? remaining) =>
        remaining is not { } r ? 0 : Math.Clamp((int)Math.Round(MaxDepth * Math.Clamp(r, 0, 1)), 1, MaxDepth);

    public static bool IsShallow(double? remaining) => DepthRows(remaining) < FloatingMinDepth;
}

/// <summary>One provider's pond. Remaining quota = remaining water; the exact numbers are always shown as text next to it.</summary>
public sealed record PondScene(
    double? Remaining,
    PondCondition Condition,
    DuckPose Duck,
    bool FaceRight,
    bool Alert = false,
    bool Busy = false,
    int Frame = 0) : ArtScene
{
    public override int Width => PondGeometry.Width;

    public override int Height => PondGeometry.Height;

    public override Palette Palette => Condition == PondCondition.Subdued ? Palette.Subdued : Palette.Standard;

    public override IndexedImage Render()
    {
        const int W = PondGeometry.Width, H = PondGeometry.Height, G = PondGeometry.GroundY, Cx = PondGeometry.Cx;
        var img = new IndexedImage(W, H);
        img.Fill(Ink.Sky);

        // ground (a speckled sand bank) with a darker surface line
        img.FillRect(0, G, W, H - G, Ink.Sand);
        for (int y = G + 1; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if ((x * 7 + y * 13) % 23 == 0)
                {
                    img[x, y] = Ink.SandDark;
                }
            }
        }

        img.FillRect(0, G, W, 1, Ink.SandDark);

        bool real = Condition is PondCondition.Normal or PondCondition.Subdued;
        int n = real ? PondGeometry.DepthRows(Remaining) : 0;
        int surface = PondGeometry.MaxDepth - n;                      // first water row (depth index); MaxDepth = dry
        int frame = Frame & 1;

        // the bowl: wall pixels, exposed (wet) sand above the water, hatched fill for unknown / empty
        for (int d = 0; d < PondGeometry.MaxDepth; d++)
        {
            int y = G + d, hw = PondGeometry.HalfWidth(d);
            int x0 = Cx - hw + 1, x1 = Cx + hw - 1;
            img[Cx - hw, y] = Ink.SandDark;
            img[Cx + hw, y] = Ink.SandDark;
            for (int x = x0; x <= x1; x++)
            {
                bool diag = ((x + y) / 2) % 2 == 0;
                img[x, y] = Condition switch
                {
                    PondCondition.Unknown => diag ? Ink.Water : Ink.WaterHi,         // hatched water: refilled in principle, value unknown
                    PondCondition.Empty or PondCondition.Error => diag ? Ink.WetSand : Ink.Sand,
                    _ => d >= surface && (n > 1 || Math.Abs(x - Cx) <= 6)
                        ? (d >= surface + 3 ? Ink.WaterDeep : Ink.Water)
                        : Ink.WetSand,
                };
            }
        }

        int surfaceY = G + Math.Min(surface, PondGeometry.MaxDepth - 1);
        if (real)
        {
            // surface highlight, 2-frame ripple (frame 0 when animation is off)
            int hw = PondGeometry.HalfWidth(Math.Min(surface, PondGeometry.MaxDepth - 1));
            int half = n > 1 ? hw - 1 : 6;
            for (int x = Cx - half; x <= Cx + half; x++)
            {
                if ((x + frame * 2) % 4 == 0)
                {
                    img[x, surfaceY] = Ink.WaterHi;
                }
            }
        }

        // stones on the walls: visible only while above the water (they appear as the pond recedes)
        if (Condition != PondCondition.Unknown)
        {
            foreach ((int d, bool left) in new[] { (5, true), (9, false), (12, true) })
            {
                if (d < surface - 1)
                {
                    int x = left ? Cx - PondGeometry.HalfWidth(d) + 2 : Cx + PondGeometry.HalfWidth(d) - 4;
                    img.FillRect(x, G + d, 3, 1, Ink.Stone);
                    img.FillRect(x, G + d + 1, 3, 1, Ink.StoneDark);
                }
            }

            // reeds growing out of the walls become visible as the water drops
            foreach ((int d, bool left) in new[] { (2, false), (3, true) })
            {
                if (d < surface - 1)
                {
                    int x = left ? Cx - PondGeometry.HalfWidth(d) + 2 : Cx + PondGeometry.HalfWidth(d) - 3;
                    Reed(img, x, G + d, 6);
                }
            }
        }

        // bank reeds (decoration, always there)
        foreach ((int x, int h) in new[] { (8, 9), (13, 12), (18, 7), (123, 8), (128, 12), (134, 9) })
        {
            Reed(img, x, G - 1, h);
        }

        DrawDuck(img, surface, surfaceY, n);
        return img;
    }

    private static void Reed(IndexedImage img, int x, int baseY, int height)
    {
        for (int i = 0; i < height; i++)
        {
            img[x, baseY - i] = Ink.ReedDark;
        }

        img[x - 1, baseY - height + 1] = Ink.Reed;
        img[x, baseY - height] = Ink.Reed;
        img[x, baseY - height - 1] = Ink.Reed;
    }

    private void DrawDuck(IndexedImage img, int surface, int surfaceY, int n)
    {
        const int G = PondGeometry.GroundY, Cx = PondGeometry.Cx;
        DuckPose pose = Duck;
        bool real = Condition is PondCondition.Normal or PondCondition.Subdued;
        bool floatingPose = pose is DuckPose.Floating or DuckPose.SleepingFloating;
        if (floatingPose && (!real || n < PondGeometry.FloatingMinDepth))
        {
            pose = pose == DuckPose.SleepingFloating ? DuckPose.SleepingStanding : DuckPose.Standing;      // never float on nothing
        }

        IndexedImage sprite = DuckSprites.Build(pose);
        int left = Cx - 10 + (FaceRight ? -14 : 14);
        bool standing = sprite.Height == DuckSprites.StandingHeight;
        int floorY = G + PondGeometry.MaxDepth - 1;
        int top = standing ? floorY - (sprite.Height - 1) : surfaceY - (sprite.Height - 1);
        img.Blit(sprite, left, top, mirror: !FaceRight);
        if (!standing)
        {
            // ripples either side of the floating duck
            img[left - 2, surfaceY] = Ink.WaterHi;
            img[left - 1, surfaceY] = Ink.WaterHi;
            img[left + DuckSprites.Width, surfaceY] = Ink.WaterHi;
            img[left + DuckSprites.Width + 1, surfaceY] = Ink.WaterHi;
        }

        // marks beside the head (facing side): z (animated), ?, !, and the busy dots
        int mx = FaceRight ? left + DuckSprites.Width + 2 : left - 7;
        int my = top + 1;
        if (pose is DuckPose.SleepingFloating or DuckPose.SleepingStanding)
        {
            img.Blit(Glyphs.Z, mx, my - (Frame & 1));
        }
        else if (pose == DuckPose.Unknown)
        {
            img.Blit(Glyphs.Question, mx, my);
        }

        if (Alert || pose == DuckPose.Alert)
        {
            img.Blit(Glyphs.Alert, FaceRight ? mx + 8 : mx - 5, my);
        }

        if (Busy)
        {
            int dots = 2 + (Frame & 1);
            for (int i = 0; i < dots; i++)
            {
                img[mx + (FaceRight ? 0 : 2) + i * 2, my + 8] = Ink.Outline;
            }
        }
    }
}

public enum GaugeCondition
{
    Normal = 0,
    Subdued = 1,
    Unknown = 2,     // elapsed: hatched, not a number
    Empty = 3,       // no data: dry hatched trough
}

/// <summary>A small pixel trough replacing the generic progress bar: water = remaining quota. The numbers are always printed next to it.</summary>
public sealed record GaugeScene(double? Remaining, GaugeCondition Condition) : ArtScene
{
    public const int GaugeWidth = 120;
    public const int GaugeHeight = 6;

    public override int Width => GaugeWidth;

    public override int Height => GaugeHeight;

    public override Palette Palette => Condition == GaugeCondition.Subdued ? Palette.Subdued : Palette.Standard;

    public override IndexedImage Render()
    {
        var img = new IndexedImage(GaugeWidth, GaugeHeight);
        img.FillRect(0, 0, GaugeWidth, GaugeHeight, Ink.SandDark);                            // frame
        int inner = GaugeWidth - 2;
        int filled = Condition is GaugeCondition.Normal or GaugeCondition.Subdued && Remaining is { } r
            ? (int)Math.Round(inner * Math.Clamp(r, 0, 1))
            : 0;
        for (int y = 1; y < GaugeHeight - 1; y++)
        {
            for (int x = 1; x <= inner; x++)
            {
                bool diag = ((x + y) / 2) % 2 == 0;
                img[x, y] = Condition switch
                {
                    GaugeCondition.Unknown => diag ? Ink.Water : Ink.WaterHi,
                    GaugeCondition.Empty => diag ? Ink.WetSand : Ink.Sand,
                    _ => x <= filled ? (y == 1 ? Ink.WaterHi : (y == GaugeHeight - 2 ? Ink.WaterDeep : Ink.Water)) : Ink.Sand,
                };
            }
        }

        return img;
    }
}

/// <summary>Crisp integer scaling: the largest whole number k such that the scene is at most <c>targetDip</c> DIPs wide on this display.</summary>
public static class PixelScale
{
    public static int Choose(int logicalWidth, double targetDip, double dpiScale) =>
        Math.Max(1, (int)Math.Floor(targetDip * Math.Max(0.5, dpiScale) / logicalWidth + 1e-9));
}
