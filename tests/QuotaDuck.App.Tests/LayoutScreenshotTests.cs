using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;
using QuotaDuck.App.Views;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>Renders the compact flyout (the real CompactFlyoutContent bound to the real view model) in every state, at several display scales, to artifacts/screenshots/layout for review.</summary>
public class LayoutScreenshotTests
{
    private static readonly string Dir = Path.Combine(ArchitectureTests.Root(), "artifacts", "screenshots", "layout");

    private static readonly ClaudeState Fresh = new(Snap.Rec(42, Snap.T0.AddHours(3).AddMinutes(23), Snap.T0.AddMinutes(-3)), Snap.Rec(12, Snap.T0.AddDays(4).AddHours(2), Snap.T0.AddMinutes(-3)));
    private static readonly ClaudeState Elapsed = new(Snap.Rec(80, Snap.T0.AddMinutes(-20), Snap.T0.AddHours(-4)), Snap.Rec(20, Snap.T0.AddDays(4), Snap.T0.AddHours(-3)));
    private static readonly ClaudeState Sleeping = new(Snap.Rec(30, Snap.T0.AddHours(1), Snap.T0.AddHours(-2).AddMinutes(-10)), Snap.Rec(55, Snap.T0.AddDays(3), Snap.T0.AddHours(-3)));

    private static AppSnapshot BothFresh() => Snap.Make(Snap.T0, Fresh, Snap.Codex(Snap.T0.AddMinutes(-1)));

    private static (string Name, AppSnapshot S)[] Scenarios() =>
    [
        ("1-both-fresh", BothFresh()),
        ("3-claude-sleeping-codex-fresh", Snap.Make(Snap.T0, Sleeping, Snap.Codex(Snap.T0.AddMinutes(-1)))),
        ("4-claude-elapsed-codex-cooldown", Snap.Make(Snap.T0, Elapsed, Snap.Codex(Snap.T0.AddMinutes(-1)), allowed: false, allowedAt: Snap.T0.AddSeconds(41))),
        ("5-codex-error-claude-fresh", Snap.Make(Snap.T0, Fresh, codexIssue: ProviderIssue.CodexNotInstalled, attempt: new ProviderAttempt(Snap.T0, ProviderErrorKind.NotFound))),
        ("5b-codex-failed-with-last-read", Snap.Make(Snap.T0, Fresh, Snap.Codex(Snap.T0.AddMinutes(-15)), codexIssue: ProviderIssue.TemporaryProviderFailure, attempt: new ProviderAttempt(Snap.T0.AddMinutes(-1), ProviderErrorKind.Timeout))),
        ("5c-codex-reading", Snap.Make(Snap.T0, Fresh, Snap.Codex(Snap.T0.AddMinutes(-12)), reading: true, allowed: false)),
    ];

    private static ShellViewModel Vm(AppSnapshot s, ClaudeSetupState? setup = null, string? exe = null)
    {
        var svc = new FakeService { Current = s };
        var vm = setup is { } st
            ? new ShellViewModel(svc, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc, null, null, new FakeSetupInfo { State = st, BridgeExePath = exe }, new FakeClipboard())
            : new ShellViewModel(svc, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
        vm.SetFlyoutVisible(true);
        return vm;
    }

    private static readonly List<string> Metrics = [];

    /// <summary>Hosts the compact content in a root with the given DPI scale, lays it out, renders it at that DPI and saves a PNG; records the DIP size.</summary>
    private static (double W, double H) Render(string name, ShellViewModel vm, CompactMode mode, bool expanded, double scale, bool hc = false)
    {
        (double W, double H) size = (0, 0);
        Sta.Run(() =>
        {
            SetHighContrast(hc);
            var content = new CompactFlyoutContent { Mode = mode };
            var host = new Border { Child = content, Background = SystemColors.WindowBrush };
            VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
            content.DataContext = vm;
            if (expanded)
            {
                vm.ToggleSetup();
            }

            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            Sta.Flush();
            foreach (PixelImage img in Sta.Descendants<PixelImage>(host))
            {
                typeof(PixelImage).GetMethod("Rebuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(img, null);   // pick the integer scale for THIS dpi
            }

            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            Sta.Flush();
            size = (host.ActualWidth, host.ActualHeight);
            int pw = (int)Math.Ceiling(size.W * scale), ph = (int)Math.Ceiling(size.H * scale);
            var bmp = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(host);
            Directory.CreateDirectory(Dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using FileStream fs = File.Create(Path.Combine(Dir, name + ".png"));
            enc.Save(fs);
            SetHighContrast(false);
        });
        lock (Metrics)
        {
            Metrics.Add($"{name,-52} {size.W,6:0} x {size.H,4:0} DIP   ({Math.Ceiling(size.W * scale):0} x {Math.Ceiling(size.H * scale):0} px @ {scale * 100:0}%)");
        }

        return size;
    }

    /// <summary>Test-only: WPF caches SystemParameters.HighContrast; flip the cached value so the palette / border logic can be previewed.</summary>
    private static void SetHighContrast(bool on)
    {
        foreach (System.Reflection.FieldInfo f in typeof(SystemParameters).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
        {
            if (f.FieldType == typeof(bool) && f.Name.Contains("ighContrast", StringComparison.OrdinalIgnoreCase))
            {
                f.SetValue(null, on);
            }
        }
    }

    [Fact]
    public void RenderLayoutScreenshots()
    {
        // the five operating states, side by side at 100%
        foreach ((string name, AppSnapshot s) in Scenarios())
        {
            Render(name + "@100", Vm(s), CompactMode.SideBySide, false, 1.0);
        }

        // 125% / 150% / stacked fallback / high contrast for the headline state and the hard one
        AppSnapshot fresh = BothFresh();
        Render("1-both-fresh@125", Vm(fresh), CompactMode.SideBySide, false, 1.25);
        Render("1-both-fresh@150", Vm(fresh), CompactMode.SideBySide, false, 1.5);
        Render("1-both-fresh@100-stacked-fallback", Vm(fresh), CompactMode.Stacked, false, 1.0);
        Render("1-both-fresh@150-stacked-fallback", Vm(fresh), CompactMode.Stacked, false, 1.5);
        Render("4-claude-elapsed-codex-cooldown@150", Vm(Scenarios()[2].S), CompactMode.SideBySide, false, 1.5);
        Render("1-both-fresh@100-highcontrast", Vm(fresh), CompactMode.SideBySide, false, 1.0, hc: true);
        Render("4-claude-elapsed-codex-cooldown@100-highcontrast", Vm(Scenarios()[2].S), CompactMode.SideBySide, false, 1.0, hc: true);

        // first run: Claude never reported, Codex fresh (setup collapsed = the normal first-run view; expanded = the guide below both cards)
        AppSnapshot first = Snap.Make(Snap.T0, null, Snap.Codex(Snap.T0.AddMinutes(-1)));
        Render("6-setup-collapsed@100", Vm(first, ClaudeSetupState.NotConfigured), CompactMode.SideBySide, false, 1.0);
        Render("7-setup-expanded@100", Vm(first, ClaudeSetupState.NotConfigured), CompactMode.SideBySide, true, 1.0);
        Render("7-setup-expanded@150", Vm(first, ClaudeSetupState.NotConfigured), CompactMode.SideBySide, true, 1.5);
        Render("7-setup-expanded-existing-statusline@100", Vm(first, ClaudeSetupState.OtherStatusLine, @"C:\Apps\QuotaDuck\quotaduck-statusline.exe"), CompactMode.SideBySide, true, 1.0);
        Render("7-setup-expanded-stacked@100", Vm(first, ClaudeSetupState.NotConfigured), CompactMode.Stacked, true, 1.0);
        Render("2-claude-waiting-bridge-wired-codex-fresh@100", Vm(first, ClaudeSetupState.BridgeConfigured), CompactMode.SideBySide, false, 1.0);

        Directory.CreateDirectory(Dir);
        File.WriteAllLines(Path.Combine(Dir, "metrics.txt"), Metrics.OrderBy(m => m, StringComparer.Ordinal));
        Assert.True(Metrics.Count >= 15);
    }

    private sealed class OneMonitor(MonitorArea m) : IMonitorSource
    {
        public IReadOnlyList<MonitorArea> Monitors() => [m];
    }

    private sealed class NoMove : IWindowMover
    {
        public void MoveTo(nint hwnd, PxPoint topLeft)
        {
        }
    }

    /// <summary>The eight review screenshots, rendered from the REAL FlyoutWindow (so the responsive policy and window sizing are the real ones).</summary>
    [Fact]
    public void RenderRealWindowScreenshots()
    {
        var wide = new MonitorArea(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0, true);
        var narrow = new MonitorArea(new PxRect(0, 0, 1280, 720), new PxRect(0, 0, 1280, 672), 2.0, true);        // 640 DIP wide work area
        AppSnapshot first = Snap.Make(Snap.T0, null, Snap.Codex(Snap.T0.AddMinutes(-1)));
        (string Name, AppSnapshot S, ClaudeSetupState? Setup, bool Expand, MonitorArea Mon)[] cases =
        [
            ("R1-both-fresh", BothFresh(), null, false, wide),
            ("R2-claude-waiting-codex-fresh", first, ClaudeSetupState.BridgeConfigured, false, wide),
            ("R3-claude-sleeping-codex-fresh", Scenarios()[1].S, null, false, wide),
            ("R4-claude-elapsed-codex-cooldown", Scenarios()[2].S, null, false, wide),
            ("R5-codex-error-claude-fresh", Scenarios()[3].S, null, false, wide),
            ("R6-setup-collapsed", first, ClaudeSetupState.NotConfigured, false, wide),
            ("R7-setup-expanded", first, ClaudeSetupState.NotConfigured, true, wide),
            ("R8-stacked-fallback-narrow-work-area", BothFresh(), null, false, narrow),
        ];
        foreach ((string name, AppSnapshot s, ClaudeSetupState? setup, bool expand, MonitorArea mon) in cases)
        {
            Sta.Run(() =>
            {
                ShellViewModel vm = Vm(s, setup);
                var w = new FlyoutWindow(vm, new OneMonitor(mon), new NoMove()) { Anchor = new PxPoint(mon.Work.Width - 40, mon.Work.Bottom + 10) };
                ((IFlyout)w).Show();
                if (expand)
                {
                    vm.ToggleSetup();
                }

                Sta.Flush();
                Sta.Flush();
                var content = (FrameworkElement)w.Content;
                int pw = (int)Math.Ceiling(content.ActualWidth), ph = (int)Math.Ceiling(content.ActualHeight);
                var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                var host = new Border { Width = pw, Height = ph, Background = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, pw, ph) } };
                host.Measure(new Size(pw, ph));
                host.Arrange(new Rect(0, 0, pw, ph));
                bmp.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Dir);
                using FileStream fs = File.Create(Path.Combine(Dir, name + ".png"));
                enc.Save(fs);
                var scroll = Sta.Descendants<ScrollViewer>(w).First();
                lock (Metrics)
                {
                    Metrics.Add($"{name,-52} window {w.ActualWidth,5:0} x {w.ActualHeight,4:0} DIP, mode {w.LastMode}, scrolls: {scroll.ScrollableHeight > 0.5}");
                }

                w.Hide();
            });
        }

        File.AppendAllLines(Path.Combine(Dir, "metrics.txt"), Metrics.Where(m => m.StartsWith("R", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1920, false, 1.0, CompactMode.SideBySide)]
    [InlineData(640, false, 1.0, CompactMode.Stacked)]
    [InlineData(1920, true, 1.0, CompactMode.Stacked)]
    [InlineData(1920, false, 1.5, CompactMode.Stacked)]
    [InlineData(1280, false, 1.0, CompactMode.SideBySide)]
    public void PolicyPrototype(double workWidthDip, bool helpFullHeight, double textScale, CompactMode expected) =>
        Assert.Equal(expected, CompactLayoutPolicy.Choose(workWidthDip, helpFullHeight, textScale));

    [Fact]
    public void AWiderFlyout_StillClampsInsideEveryWorkArea_ForAnyAnchor()
    {
        MonitorArea[][] layouts =
        [
            [new MonitorArea(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0, true)],
            [new MonitorArea(new PxRect(0, 0, 1920, 1080), new PxRect(60, 0, 1920, 1080), 1.5, true)],
            [new MonitorArea(new PxRect(0, 0, 1366, 768), new PxRect(0, 0, 1860 - 500, 720), 1.0, true)],
            [new MonitorArea(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0, true), new MonitorArea(new PxRect(-1280, 0, 0, 1024), new PxRect(-1280, 0, 0, 984), 1.25, false)],
        ];
        foreach (MonitorArea[] ms in layouts)
        {
            foreach (double widthDip in new[] { 648 + 16, 332 + 16 })
            {
                foreach (PxPoint a in new[] { new PxPoint(2, 1060), new PxPoint(1915, 1060), new PxPoint(-1270, 1000), new PxPoint(960, 1060), new PxPoint(1900, 10) })
                {
                    FlyoutPlacementResult r = FlyoutPlacement.Place(ms, a, widthDip, 400);
                    PxRect w = r.Monitor.Work;
                    Assert.True(r.TopLeft.X >= w.Left && r.TopLeft.X + r.Size.Width <= w.Right, $"x out of work area {r.TopLeft} {r.Size} {w}");
                    Assert.True(r.TopLeft.Y >= w.Top && r.TopLeft.Y + r.Size.Height <= w.Bottom);
                }
            }
        }
    }
}
