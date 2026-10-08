using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;
using QuotaDuck.App.Views;

namespace QuotaDuck.App.Tests;

/// <summary>Tray anchoring: pure placement maths over every common taskbar position, multiple monitors and DPI scales.</summary>
public class PlacementTests
{
    private const double W = 380, H = 560;

    private static MonitorArea Mon(int l, int t, int r, int b, PxRect work, double dpi = 1.0, bool primary = true) => new(new PxRect(l, t, r, b), work, dpi, primary);

    private static readonly MonitorArea BottomBar = Mon(0, 0, 1920, 1080, new PxRect(0, 0, 1920, 1032));
    private static readonly MonitorArea TopBar = Mon(0, 0, 1920, 1080, new PxRect(0, 48, 1920, 1080));
    private static readonly MonitorArea LeftBar = Mon(0, 0, 1920, 1080, new PxRect(60, 0, 1920, 1080));
    private static readonly MonitorArea RightBar = Mon(0, 0, 1920, 1080, new PxRect(0, 0, 1860, 1080));
    private static readonly MonitorArea AutoHide = Mon(0, 0, 1920, 1080, new PxRect(0, 0, 1920, 1080));

    private static FlyoutPlacementResult Place(MonitorArea m, PxPoint? anchor) => FlyoutPlacement.Place([m], anchor, W, H);

    // ---------------- taskbar edges ----------------

    [Theory]
    [InlineData(0, TaskbarEdge.Bottom)]
    [InlineData(1, TaskbarEdge.Top)]
    [InlineData(2, TaskbarEdge.Left)]
    [InlineData(3, TaskbarEdge.Right)]
    [InlineData(4, TaskbarEdge.None)]
    public void TheTaskbarEdge_IsInferredFromWorkAreaVersusBounds(int which, TaskbarEdge expected) =>
        Assert.Equal(expected, FlyoutPlacement.EdgeOf(new[] { BottomBar, TopBar, LeftBar, RightBar, AutoHide }[which]));

    // ---------------- anchored placement per edge ----------------

    [Fact]
    public void BottomTaskbar_FlyoutSitsAboveIt_CentredOnTheIcon_GapAbove()
    {
        FlyoutPlacementResult r = Place(BottomBar, new PxPoint(1000, 1050));
        Assert.Equal(new PxPoint(1000 - 190, 1032 - 560 - 8), r.TopLeft);
        Assert.Equal(TaskbarEdge.Bottom, r.Edge);
    }

    [Fact]
    public void TopTaskbar_FlyoutHangsBelowIt()
    {
        Assert.Equal(new PxPoint(1000 - 190, 48 + 8), Place(TopBar, new PxPoint(1000, 20)).TopLeft);
    }

    [Fact]
    public void LeftTaskbar_FlyoutSitsRightOfIt_CentredVerticallyOnTheIcon()
    {
        Assert.Equal(new PxPoint(60 + 8, 700 - 280), Place(LeftBar, new PxPoint(30, 700)).TopLeft);
    }

    [Fact]
    public void RightTaskbar_FlyoutSitsLeftOfIt()
    {
        Assert.Equal(new PxPoint(1860 - 380 - 8, 500 - 280), Place(RightBar, new PxPoint(1890, 500)).TopLeft);
    }

    [Fact]
    public void TheUsualTrayCorner_IsClampedIntoTheWorkArea_NotOffTheScreen()
    {
        // an icon at the far right of a bottom taskbar: centring on it would hang off the screen, so it is pulled in
        PxPoint p = Place(BottomBar, new PxPoint(1900, 1050)).TopLeft;
        Assert.Equal(1920 - 380, p.X);
        // an icon at the far left (a left-aligned taskbar's overflow): pulled in on the left
        Assert.Equal(0, Place(BottomBar, new PxPoint(5, 1050)).TopLeft.X);
        // a left-taskbar icon near the very top / bottom is clamped vertically
        Assert.Equal(0, Place(LeftBar, new PxPoint(30, 10)).TopLeft.Y);
        Assert.Equal(1080 - 560, Place(LeftBar, new PxPoint(30, 1075)).TopLeft.Y);
    }

    [Fact]
    public void AnAutoHiddenTaskbar_UsesTheCornerNearestTheClick()
    {
        Assert.Equal(new PxPoint(1920 - 380 - 8, 1080 - 560 - 8), Place(AutoHide, new PxPoint(1800, 1075)).TopLeft);      // bottom-right
        Assert.Equal(new PxPoint(8, 8), Place(AutoHide, new PxPoint(100, 5)).TopLeft);                                       // top-left
        Assert.Equal(new PxPoint(8, 1080 - 560 - 8), Place(AutoHide, new PxPoint(5, 900)).TopLeft);                          // bottom-left
        Assert.Equal(new PxPoint(1920 - 380 - 8, 8), Place(AutoHide, new PxPoint(1915, 100)).TopLeft);                       // top-right
    }

    // ---------------- fallbacks ----------------

    [Fact]
    public void WithoutAnAnchor_ItFallsBackToTheTrayCornerOfThePrimaryWorkArea()
    {
        Assert.Equal(new PxPoint(1920 - 380 - 8, 1032 - 560 - 8), Place(BottomBar, null).TopLeft);       // the previous behaviour
        Assert.Equal(new PxPoint(1920 - 380 - 8, 48 + 8), Place(TopBar, null).TopLeft);
        Assert.Equal(new PxPoint(60 + 8, 1080 - 560 - 8), Place(LeftBar, null).TopLeft);
        Assert.Equal(new PxPoint(1860 - 380 - 8, 1080 - 560 - 8), Place(RightBar, null).TopLeft);
    }

    [Fact]
    public void AStaleAnchor_PicksTheNearestMonitor_NotJustThePrimary()
    {
        MonitorArea primary = Mon(0, 0, 1920, 1080, new PxRect(0, 0, 1920, 1032), 1.0, primary: true);
        MonitorArea right = Mon(1920, 0, 3840, 1080, new PxRect(1920, 0, 3840, 1032), 1.0, primary: false);
        FlyoutPlacementResult r = FlyoutPlacement.Place([primary, right], new PxPoint(4200, 500), W, H);      // just off the right monitor's edge
        Assert.False(r.Monitor.IsPrimary);
        Assert.Equal(new PxPoint(3840 - 380 - 8, 1032 - 560 - 8), r.TopLeft);
    }

    [Fact]
    public void AStaleAnchorOutsideEveryMonitor_UsesTheNearestMonitorsCorner_StillInsideItsWorkArea()
    {
        FlyoutPlacementResult r = Place(BottomBar, new PxPoint(5000, 3000));                              // the display layout changed since the click
        Assert.Equal(new PxPoint(1920 - 380 - 8, 1032 - 560 - 8), r.TopLeft);
    }

    [Fact]
    public void AFlyoutTallerThanTheWorkArea_IsShrunkToFit_AtTheTop()
    {
        var small = Mon(0, 0, 1280, 600, new PxRect(0, 0, 1280, 552));
        FlyoutPlacementResult r = FlyoutPlacement.Place([small], new PxPoint(640, 580), W, 900);
        Assert.Equal(552, r.Size.Height);
        Assert.Equal(0, r.TopLeft.Y);
    }

    [Fact]
    public void NoMonitors_IsRejected() => Assert.Throws<ArgumentException>(() => FlyoutPlacement.Place([], null, W, H));

    // ---------------- DPI and multiple monitors ----------------

    [Fact]
    public void AHighDpiMonitor_ScalesTheSizeAndTheGap_InPhysicalPixels()
    {
        var m = Mon(0, 0, 2880, 1620, new PxRect(0, 0, 2880, 1566), dpi: 1.5);
        FlyoutPlacementResult r = FlyoutPlacement.Place([m], new PxPoint(2000, 1590), W, H);
        Assert.Equal(new PxSize(570, 840), r.Size);
        Assert.Equal(new PxPoint(2000 - 285, 1566 - 840 - 12), r.TopLeft);                                // gap 8 DIP = 12 px
    }

    [Fact]
    public void WithSeveralMonitors_TheOneClickedWins_EachWithItsOwnDpi()
    {
        MonitorArea primary = Mon(0, 0, 1920, 1080, new PxRect(0, 0, 1920, 1032), 1.0, primary: true);
        MonitorArea left = Mon(-3840, 0, 0, 2160, new PxRect(-3840, 0, 0, 2112), 2.0, primary: false);       // a 4K screen to the left at 200%
        MonitorArea above = Mon(0, -1080, 1920, 0, new PxRect(0, -1080, 1920, 0), 1.0, primary: false);
        MonitorArea[] all = [primary, left, above];

        FlyoutPlacementResult onLeft = FlyoutPlacement.Place(all, new PxPoint(-200, 2140), W, H);
        Assert.Same(left, onLeft.Monitor);
        Assert.Equal(new PxSize(760, 1120), onLeft.Size);                                                 // 200% => twice the physical size
        Assert.Equal(new PxPoint(-760, 2112 - 1120 - 16), onLeft.TopLeft);                       // centring on the icon would overhang the right edge: clamped to it
        Assert.True(left.Work.Contains(onLeft.TopLeft));

        FlyoutPlacementResult onPrimary = FlyoutPlacement.Place(all, new PxPoint(1000, 1050), W, H);
        Assert.Same(primary, onPrimary.Monitor);
        Assert.Equal(new PxSize(380, 560), onPrimary.Size);

        FlyoutPlacementResult onAbove = FlyoutPlacement.Place(all, new PxPoint(900, -10), W, H);
        Assert.Same(above, onAbove.Monitor);
        Assert.InRange(onAbove.TopLeft.Y, -1080, -560);                                                   // above the primary, in negative coordinates

        Assert.Same(primary, FlyoutPlacement.Place(all, null, W, H).Monitor);                              // no anchor: the primary
    }

    [Fact]
    public void WhateverTheLayout_TheFlyoutIsAlwaysInsideTheChosenWorkArea()
    {
        var rnd = new Random(7);
        MonitorArea[] layouts = [BottomBar, TopBar, LeftBar, RightBar, AutoHide, Mon(0, 0, 800, 600, new PxRect(0, 0, 800, 560), 1.25), Mon(-1280, 0, 0, 1024, new PxRect(-1280, 0, 0, 984), 1.0, false)];
        for (int i = 0; i < 4000; i++)
        {
            MonitorArea[] ms = [BottomBar, layouts[rnd.Next(layouts.Length)] with { IsPrimary = false, Bounds = new PxRect(2000, 0, 3920, 1080), Work = new PxRect(2000, 0, 3920, 1032) }];
            PxPoint? a = rnd.Next(5) == 0 ? null : new PxPoint(rnd.Next(-500, 4500), rnd.Next(-500, 1500));
            double h = rnd.Next(100, 1400);
            FlyoutPlacementResult r = FlyoutPlacement.Place(ms, a, W, h);
            PxRect work = r.Monitor.Work;
            Assert.True(r.TopLeft.X >= work.Left && r.TopLeft.Y >= work.Top, $"{a} {h}");
            Assert.True(r.TopLeft.X + r.Size.Width <= work.Right && r.TopLeft.Y + r.Size.Height <= work.Bottom, $"{a} {h}");
        }
    }
}

public class WindowPlacementTests
{
    private sealed class FakeMonitors(params MonitorArea[] areas) : IMonitorSource
    {
        public bool Throw { get; set; }

        public IReadOnlyList<MonitorArea> Monitors() => Throw ? throw new InvalidOperationException("no geometry") : areas;
    }

    private sealed class FakeMover : IWindowMover
    {
        public List<(nint Hwnd, PxPoint At)> Moves { get; } = [];

        public void MoveTo(nint hwnd, PxPoint topLeft) => Moves.Add((hwnd, topLeft));
    }

    private static readonly MonitorArea Bottom = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0, true);

    [Fact]
    public void ShowingWithAnAnchor_MovesTheWindowNextToTheIcon_ThenCorrectsOnceTheRealSizeIsKnown()
    {
        Sta.Run(() =>
        {
            var service = new FakeService();
            var vm = new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
            var mover = new FakeMover();
            var w = new FlyoutWindow(vm, new FakeMonitors(Bottom), mover) { Anchor = new PxPoint(1700, 1050) };
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.NotEmpty(mover.Moves);
            Assert.All(mover.Moves, m => Assert.NotEqual(0, m.Hwnd));
            (nint _, PxPoint first) = mover.Moves[0];
            Assert.True(first.X + 664 <= 1920 && first.X >= 1920 - 664 - 1, $"the wide flyout is clamped to the right edge of the work area, x={first.X}");   // centred on 1700 would run off the screen
            Assert.True(mover.Moves.Count >= 2, "second pass after layout");
            PxPoint last = mover.Moves[^1].At;
            Assert.Equal(1920 - (int)Math.Round(w.ActualWidth), last.X);
            Assert.True(last.Y + w.ActualHeight <= 1032 - 8 + 1 && last.Y + w.ActualHeight >= 1032 - 8 - 1, $"bottom edge {last.Y + w.ActualHeight} sits one gap above the taskbar");
            Assert.Equal(TaskbarEdge.Bottom, w.LastPlacement!.Edge);
        });
    }

    [Fact]
    public void WithoutAnAnchor_TheWindowGoesToTheTrayCorner()
    {
        Sta.Run(() =>
        {
            var vm = new ShellViewModel(new FakeService(), new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
            var mover = new FakeMover();
            var w = new FlyoutWindow(vm, new FakeMonitors(Bottom), mover);
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.Equal(1920 - (int)Math.Round(w.ActualWidth) - 8, mover.Moves[^1].At.X);
            Assert.Equal(CompactMode.SideBySide, w.LastMode);
        });
    }

    [Fact]
    public void AnIconInTheMiddle_CentresTheWideFlyoutOnIt_AndANarrowWorkAreaStacks()
    {
        Sta.Run(() =>
        {
            var vm = new ShellViewModel(new FakeService(), new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
            var mover = new FakeMover();
            var w = new FlyoutWindow(vm, new FakeMonitors(Bottom), mover) { Anchor = new PxPoint(960, 1050) };
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.InRange(mover.Moves[^1].At.X, 960 - (int)Math.Round(w.ActualWidth / 2) - 1, 960 - (int)Math.Round(w.ActualWidth / 2) + 1);
            w.Hide();

            var narrow = new MonitorArea(new PxRect(0, 0, 1280, 720), new PxRect(0, 0, 1280, 672), 2.0, true);         // 640 DIP wide work area (1280 px at 200%)
            var vm2 = new ShellViewModel(new FakeService(), new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
            var w2 = new FlyoutWindow(vm2, new FakeMonitors(narrow), new FakeMover()) { Anchor = new PxPoint(1200, 700) };
            ((IFlyout)w2).Show();
            Sta.Flush();
            Assert.Equal(CompactMode.Stacked, w2.LastMode);
            Assert.True(w2.ActualWidth < 400);
        });
    }

    [Fact]
    public void WhenMonitorGeometryIsUnavailable_ItFallsBackToTheWorkAreaCorner_NeverThrows()
    {
        Sta.Run(() =>
        {
            var vm = new ShellViewModel(new FakeService(), new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
            var mover = new FakeMover();
            var w = new FlyoutWindow(vm, new FakeMonitors(Bottom) { Throw = true }, mover);
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.True(w.IsVisible);
            Assert.Empty(mover.Moves);                                                      // nothing was moved natively
            System.Windows.Rect wa = System.Windows.SystemParameters.WorkArea;
            Assert.InRange(w.Left, wa.Left - 1, wa.Right);                                  // the old placement: still on the work area
        });
    }

    [Fact]
    public void TheTrayAnchor_ReachesTheFlyout_ForClickOpenAndToggle_AndIsNullWhenUnknown()
    {
        var r = new ShellRig();
        r.Controller.Toggle();                                                              // never clicked: no anchor
        r.Controller.Hide();
        r.Tray.LastAnchor = new PxPoint(1800, 1060);
        r.Tray.LeftClick();
        r.Tray.LeftClick();
        r.Tray.MenuOpen();
        Assert.Equal([null, new PxPoint(1800, 1060), new PxPoint(1800, 1060)], r.Flyout.AnchorsAtShow.ToArray());
    }
}
