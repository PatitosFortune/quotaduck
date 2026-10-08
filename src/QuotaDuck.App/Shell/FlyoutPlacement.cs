namespace QuotaDuck.App.Shell;

/// <summary>Physical (device) pixels. The placement maths is pure and works in these only; DIP conversion happens per monitor, with that monitor's DPI.</summary>
public readonly record struct PxPoint(int X, int Y);

public readonly record struct PxSize(int Width, int Height);

public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(PxPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public PxPoint Center => new((Left + Right) / 2, (Top + Bottom) / 2);
}

/// <summary>One monitor: its full bounds, its work area (bounds minus the taskbar and docked bars) and its own DPI scale (1.0 = 96 dpi).</summary>
public sealed record MonitorArea(PxRect Bounds, PxRect Work, double DpiScale, bool IsPrimary);

public enum TaskbarEdge
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 3,
    Bottom = 4,
}

public sealed record FlyoutPlacementResult(PxPoint TopLeft, PxSize Size, MonitorArea Monitor, TaskbarEdge Edge);

public interface IMonitorSource
{
    IReadOnlyList<MonitorArea> Monitors();
}

/// <summary>
/// Where the flyout goes. Uses only documented information: the monitors' bounds/work areas/DPI and the screen position of the tray
/// interaction (the click on the icon). The taskbar edge is inferred from work area vs bounds, so it works for bottom/top/left/right
/// taskbars on any monitor; with an auto-hidden taskbar (no difference) the nearest corner to the click is used. No shell hooks.
/// Without a known anchor it falls back to the corner of the primary work area where the tray normally is.
/// Always inside the chosen monitor's work area; never larger than it.
/// </summary>
public static class FlyoutPlacement
{
    public const int GapDip = 8;

    public static TaskbarEdge EdgeOf(MonitorArea m)
    {
        if (m.Work.Bottom < m.Bounds.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (m.Work.Top > m.Bounds.Top)
        {
            return TaskbarEdge.Top;
        }

        if (m.Work.Left > m.Bounds.Left)
        {
            return TaskbarEdge.Left;
        }

        if (m.Work.Right < m.Bounds.Right)
        {
            return TaskbarEdge.Right;
        }

        return TaskbarEdge.None;
    }

    /// <param name="monitors">All monitors (at least one).</param>
    /// <param name="anchor">Screen position of the tray click, or null if none is known.</param>
    /// <param name="widthDip">Flyout width in DIPs.</param>
    /// <param name="heightDip">Flyout height in DIPs (last known or an estimate).</param>
    public static FlyoutPlacementResult Place(IReadOnlyList<MonitorArea> monitors, PxPoint? anchor, double widthDip, double heightDip)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("no monitors", nameof(monitors));
        }

        MonitorArea m = MonitorFor(monitors, anchor);
        int w = Math.Max(1, (int)Math.Round(widthDip * m.DpiScale));
        int h = Math.Max(1, (int)Math.Round(heightDip * m.DpiScale));
        int gap = Math.Max(1, (int)Math.Round(GapDip * m.DpiScale));
        w = Math.Min(w, m.Work.Width);
        h = Math.Min(h, m.Work.Height);
        PxRect work = m.Work;
        TaskbarEdge edge = EdgeOf(m);

        int x, y;
        if (anchor is not { } a || !AnchorUsable(m, a))
        {
            // no usable anchor: the usual tray corner. Taskbar on the left -> bottom-left; on the top -> top-right; otherwise bottom-right.
            x = edge == TaskbarEdge.Left ? work.Left + gap : work.Right - w - gap;
            y = edge == TaskbarEdge.Top ? work.Top + gap : work.Bottom - h - gap;
        }
        else
        {
            switch (edge)
            {
                case TaskbarEdge.Bottom:
                    x = a.X - w / 2;
                    y = work.Bottom - h - gap;
                    break;
                case TaskbarEdge.Top:
                    x = a.X - w / 2;
                    y = work.Top + gap;
                    break;
                case TaskbarEdge.Right:
                    x = work.Right - w - gap;
                    y = a.Y - h / 2;
                    break;
                case TaskbarEdge.Left:
                    x = work.Left + gap;
                    y = a.Y - h / 2;
                    break;
                default:                                           // auto-hidden or no taskbar: the corner nearest the click
                    PxPoint c = m.Bounds.Center;
                    x = a.X < c.X ? work.Left + gap : work.Right - w - gap;
                    y = a.Y < c.Y ? work.Top + gap : work.Bottom - h - gap;
                    break;
            }
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        return new FlyoutPlacementResult(new PxPoint(x, y), new PxSize(w, h), m, edge);
    }

    /// <summary>The monitor containing the anchor; for an anchor outside every monitor (stale after a display change) the nearest one; with no anchor the primary.</summary>
    public static MonitorArea MonitorFor(IReadOnlyList<MonitorArea> monitors, PxPoint? anchor)
    {
        if (anchor is { } a)
        {
            MonitorArea? hit = monitors.FirstOrDefault(m => m.Bounds.Contains(a));
            if (hit is not null)
            {
                return hit;
            }

            return monitors.OrderBy(m => Distance2(m.Bounds, a)).First();
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private static bool AnchorUsable(MonitorArea m, PxPoint a) => m.Bounds.Contains(a);

    private static long Distance2(PxRect r, PxPoint p)
    {
        long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
        long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }
}
