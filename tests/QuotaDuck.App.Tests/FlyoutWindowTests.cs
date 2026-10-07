using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaDuck.App.Shell;
using QuotaDuck.Art;
using QuotaDuck.App.Views;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "STA test timed out");
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public static void Flush() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(root, i);
            if (c is T t)
            {
                yield return t;
            }

            foreach (T d in Descendants<T>(c))
            {
                yield return d;
            }
        }
    }
}

/// <summary>The real WPF flyout window (STA) driven by fake snapshots: lifecycle, binding, keyboard and accessibility.</summary>
public class FlyoutWindowTests
{
    private static (FlyoutWindow Window, ShellViewModel Vm, FakeService Service, List<bool> Visibility) Make()
    {
        var service = new FakeService();
        var vm = new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
        var w = new FlyoutWindow(vm);
        var vis = new List<bool>();
        w.VisibleChanged += vis.Add;
        return (w, vm, service, vis);
    }

    private static string[] Texts(DependencyObject root) => Sta.Descendants<TextBlock>(root).Select(t => t.Text).ToArray();

    /// <summary>The template instantiates a collapsed button for Claude too; only VISIBLE buttons exist for the user.</summary>
    private static Button[] VisibleButtons(FlyoutWindow w) => Sta.Descendants<Button>(w).Where(b => b.IsVisible).ToArray();

    private static Button RefreshButton(FlyoutWindow w) => Assert.Single(VisibleButtons(w));

    [Fact]
    public void ShowHide_RaisesVisibilityEvents_CloseHidesInsteadOfClosing_AndItCanBeShownAgain()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, _, _, List<bool> vis) = Make();
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.True(w.IsVisible);
            Assert.Equal([true], vis);

            ((IFlyout)w).Hide();
            Sta.Flush();
            Assert.False(w.IsVisible);
            Assert.Equal([true, false], vis);

            ((IFlyout)w).Show();
            w.Close();                                                                  // the X button / Alt+F4: must hide, never destroy
            Sta.Flush();
            Assert.False(w.IsVisible);
            Assert.True(w.IsLoaded);
            Assert.Equal([true, false, true, false], vis);

            ((IFlyout)w).Show();                                                        // still usable
            Assert.True(w.IsVisible);

            w.AllowClose();                                                             // only Exit really closes
            w.Close();
            Sta.Flush();
            Assert.False(w.IsLoaded);
        });
    }

    [Fact]
    public void EscapeHidesTheFlyout()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, _, _, List<bool> vis) = Make();
            ((IFlyout)w).Show();
            Sta.Flush();
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent };
            w.RaiseEvent(e);
            Sta.Flush();
            Assert.True(e.Handled);
            Assert.False(w.IsVisible);
            Assert.Equal([true, false], vis);
            ((IFlyout)w).Show();                                                        // other keys do not hide it
            w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, Key.A) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            Assert.True(w.IsVisible);
        });
    }

    [Fact]
    public void RefreshButton_ReflectsAllowed_Cooldown_Reading_AndClaudeHasNoButton()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, FakeService svc, _) = Make();
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.Single(VisibleButtons(w));                                          // exactly one visible button in the whole window: Codex's
            Button b = RefreshButton(w);
            Assert.Equal("Refresh", b.Content);
            Assert.True(b.IsEnabled);

            svc.Current = Snap.Make(Snap.T0, allowed: false, allowedAt: Snap.T0.AddSeconds(30));
            vm.Refresh();
            Sta.Flush();
            Assert.Equal("Refresh in 30 s", RefreshButton(w).Content);
            Assert.False(RefreshButton(w).IsEnabled);

            svc.Current = Snap.Make(Snap.T0, codex: Snap.Codex(Snap.T0), reading: true, allowed: false);
            vm.Refresh();
            Sta.Flush();
            Assert.Equal("Reading…", RefreshButton(w).Content);
            Assert.False(RefreshButton(w).IsEnabled);

            svc.Current = Snap.Make(Snap.T0.AddMinutes(2), codex: Snap.Codex(Snap.T0));
            vm.Refresh();
            Sta.Flush();
            Assert.True(RefreshButton(w).IsEnabled);
            Assert.True(RefreshButton(w).Focusable);                                    // keyboard reachable (Tab / Space / Enter)
            Assert.True(RefreshButton(w).IsTabStop);
        });
    }

    [Fact]
    public void ClickingRefresh_RaisesRefreshRequested()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, _, _, _) = Make();
            int clicks = 0;
            w.RefreshRequested += () => clicks++;
            ((IFlyout)w).Show();
            Sta.Flush();
            RefreshButton(w).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, clicks);
        });
    }

    [Fact]
    public void BothProviderSectionsAreRendered_WithTheirOwnStates()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, FakeService svc, _) = Make();
            ((IFlyout)w).Show();
            Sta.Flush();
            string[] waiting = Texts(w);
            Assert.Contains("Claude", waiting);
            Assert.Contains("Codex", waiting);
            Assert.Contains("Waiting for Claude Code in a terminal", waiting);
            Assert.Contains("Not read yet", waiting);

            ClaudeState claude = new(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), Snap.Rec(12, Snap.T0.AddDays(4), Snap.T0.AddMinutes(-3)));
            svc.Current = Snap.Make(Snap.T0, claude, Snap.Codex(Snap.T0.AddMinutes(-1)));
            vm.Refresh();
            Sta.Flush();
            string[] live = Texts(w);
            Assert.Contains("42% used", live);
            Assert.Contains("58% left", live);
            Assert.Contains("31% used", live);
            Assert.Contains(live, x => x == "Resets in 3 h (13:00) · Last changed 3 min ago");
            Assert.Contains("Reported by Claude Code", live);
            Assert.Contains("Up to date", live);
            Assert.Contains("Claude reports only from terminal Claude Code sessions.", live);
        });
    }

    [Fact]
    public void Accessibility_NamesOnTheWindowPondsAndButton_GaugesAreDecorative()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, FakeService svc, _) = Make();
            svc.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), null), Snap.Codex(Snap.T0));
            vm.Refresh();
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.Equal("QuotaDuck quota", AutomationProperties.GetName(w));
            PixelImage[] images = Sta.Descendants<PixelImage>(w).ToArray();
            Assert.Equal(6, images.Length);                                             // 2 ponds + 4 gauges (Claude 5h + weekly, Codex 5h + weekly)
            PixelImage[] ponds = images.Where(i => !i.Decorative).ToArray();
            PixelImage[] gauges = images.Where(i => i.Decorative).ToArray();
            Assert.Equal(2, ponds.Length);
            Assert.Equal(4, gauges.Length);
            Assert.All(ponds, p => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(p))));
            Assert.Contains(ponds, p => AutomationProperties.GetName(p).StartsWith("Claude pond: 58 percent of the water is left", StringComparison.Ordinal));
            Assert.Contains(ponds, p => AutomationProperties.GetName(p).StartsWith("Codex pond: 65 percent", StringComparison.Ordinal));
            // decorative gauges are not exposed to assistive technology; the row text beside them carries the numbers and has an automation name
            Assert.All(gauges, g => Assert.False(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(g).IsContentElement()));
            Assert.All(ponds, g => Assert.True(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(g).IsContentElement()));
            Assert.Equal("Refresh Codex quota", AutomationProperties.GetName(RefreshButton(w)));
            Assert.True(KeyboardNavigation.GetTabNavigation(w) == KeyboardNavigationMode.Cycle);
        });
    }

    [Fact]
    public void PixelArt_IsDrawnCrisply_IntegerScaled_FrozenBitmaps_NearestNeighbour()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, FakeService svc, _) = Make();
            svc.Current = Snap.Make(Snap.T0, codex: Snap.Codex(Snap.T0));
            vm.Refresh();
            ((IFlyout)w).Show();
            Sta.Flush();
            double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
            foreach (PixelImage img in Sta.Descendants<PixelImage>(w))
            {
                var bmp = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(img.Source);
                Assert.True(bmp.IsFrozen);
                Assert.Equal(System.Windows.Media.BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(img));
                int logical = img.Scene!.Width;
                Assert.Equal(0, bmp.PixelWidth % logical);                                // a whole number of device pixels per art pixel
                Assert.Equal(PixelScale.Choose(logical, img.TargetWidth, dpi) * logical, bmp.PixelWidth);
                Assert.Equal(bmp.PixelWidth / dpi, img.ActualWidth, 0.6);                 // shown 1:1 in device pixels
            }
        });
    }

    [Fact]
    public void ARealPond_IsPaintedInTheBitmap_WaterWhereThePresenterSaidItWas()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, FakeService svc, _) = Make();
            svc.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(25, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-1)), null));
            vm.Refresh();
            ((IFlyout)w).Show();
            Sta.Flush();
            PixelImage pond = Sta.Descendants<PixelImage>(w).First(i => !i.Decorative);
            var bmp = (System.Windows.Media.Imaging.BitmapSource)pond.Source;
            var px = new uint[bmp.PixelWidth * bmp.PixelHeight];
            bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
            Assert.Contains(Palette.Standard[Ink.Water], px);
            Assert.Contains(Palette.Standard[Ink.Yellow], px);                          // the duck
        });
    }

    [Fact]
    public void Layout_IsDpiFriendly_AndPlacedInsideTheWorkArea()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, _, _, _) = Make();
            Assert.True(w.UseLayoutRounding);
            Assert.Equal(SizeToContent.Height, w.SizeToContent);
            Assert.False(w.Topmost);                                                    // no always-on-top widget in this gate
            Assert.False(w.ShowInTaskbar);
            ((IFlyout)w).Show();
            Sta.Flush();
            Rect wa = SystemParameters.WorkArea;
            Assert.InRange(w.Left, wa.Left - 1, wa.Right);
            Assert.True(w.Left + w.ActualWidth <= wa.Right + 1);
            Assert.True(w.Top >= wa.Top - 1);
        });
    }

    [Fact]
    public void ThePlaceholderUi_HasNoAnimations_SoReducedMotionIsTriviallyHonoured()
    {
        string xaml = File.ReadAllText(Path.Combine(ArchitectureTests.Root(), "src", "QuotaDuck.App", "Views", "FlyoutWindow.xaml"));
        foreach (string anim in new[] { "Storyboard", "Animation", "BeginStoryboard", "EventTrigger", "Transition" })
        {
            Assert.DoesNotContain(anim, xaml);
        }

        string app = File.ReadAllText(Path.Combine(ArchitectureTests.Root(), "src", "QuotaDuck.App", "App.xaml.cs"));
        Assert.Contains("SystemParameters.ClientAreaAnimation", app);                   // the ViewModel carries the OS setting for the art to come
    }

    private static (FlyoutWindow Window, ShellViewModel Vm, FakeClipboard Clip) MakeWithSetup(ClaudeSetupState state, string? exe = null)
    {
        var service = new FakeService();
        var clip = new FakeClipboard();
        var vm = new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc, null, null, new FakeSetupInfo { State = state, BridgeExePath = exe }, clip);
        return (new FlyoutWindow(vm), vm, clip);
    }

    [Fact]
    public void TheSetupGuide_ShowsTheSnippetAndACopyButton_InPlaceOfTheEmptyRows_AndCopyRaisesTheEvent()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, _) = MakeWithSetup(ClaudeSetupState.NotConfigured);
            int copies = 0;
            w.CopySetupRequested += () => copies++;
            vm.SetFlyoutVisible(true);
            ((IFlyout)w).Show();
            Sta.Flush();
            string[] texts = Texts(w);
            Assert.Contains("Connect Claude Code (one-time setup)", texts);
            Assert.Contains("Why only terminal sessions?", Sta.Descendants<System.Windows.Controls.Expander>(w).Select(x => (string)x.Header));
            System.Windows.Controls.Expander more = Sta.Descendants<System.Windows.Controls.Expander>(w).Single(x => x.IsVisible);
            Assert.False(more.IsExpanded);                                                 // the details are one keyboard action away, collapsed to keep the flyout short
            more.IsExpanded = true;
            Sta.Flush();
            Assert.Contains(Texts(w), t => t.Contains("did not trigger the status line", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("never edits your Claude settings", StringComparison.Ordinal));
            Assert.DoesNotContain("No data", Sta.Descendants<TextBlock>(w).Where(x => x.IsVisible).Select(x => x.Text));   // the two "No data" rows are replaced by the guide

            TextBox snippet = Sta.Descendants<TextBox>(w).Single(t => t.IsVisible);
            Assert.True(snippet.IsReadOnly);
            Assert.Contains("\"statusLine\"", snippet.Text);
            Assert.True(snippet.Focusable);                                                // selectable by keyboard too

            Button[] buttons = VisibleButtons(w);
            Assert.Equal(2, buttons.Length);                                               // Copy (Claude's guide) and Refresh (Codex)
            Button copy = buttons.Single(b => AutomationProperties.GetName(b).StartsWith("Copy", StringComparison.Ordinal));
            Assert.Equal("Copy", copy.Content);
            copy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, copies);
            Assert.Equal("Refresh Codex quota", AutomationProperties.GetName(buttons.Single(b => b != copy)));
        });
    }

    [Fact]
    public void TheSetupGuide_IsGone_OnceClaudeHasData_AndOnlyRefreshRemains()
    {
        Sta.Run(() =>
        {
            (FlyoutWindow w, ShellViewModel vm, _) = MakeWithSetup(ClaudeSetupState.NotConfigured);
            var svc = (FakeService)typeof(ShellViewModel).GetField("_service", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
            svc.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), null));
            vm.SetFlyoutVisible(true);
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.DoesNotContain(Sta.Descendants<TextBox>(w), t => t.IsVisible);
            Assert.Equal("Refresh Codex quota", AutomationProperties.GetName(Assert.Single(VisibleButtons(w))));
            Assert.Contains("42% used", Texts(w));
        });
    }

    [Fact]
    public void ATallFlyout_IsCappedToTheWorkArea_AndScrolls_NeverRunningOffTheScreen()
    {
        Sta.Run(() =>
        {
            var tiny = new MonitorArea(new PxRect(0, 0, 1280, 500), new PxRect(0, 0, 1280, 452), 1.0, true);     // a very small screen: content cannot fit
            var service = new FakeService { Current = Snap.Make(Snap.T0, codex: Snap.Codex(Snap.T0)) };
            var vm = new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc, null, null, new FakeSetupInfo { State = ClaudeSetupState.NotConfigured }, new FakeClipboard());
            var mover = new PlacementMover();
            var w = new FlyoutWindow(vm, new PlacementMonitors(tiny), mover);
            vm.SetFlyoutVisible(true);
            ((IFlyout)w).Show();
            Sta.Flush();
            Assert.True(w.MaxHeight <= 452 - 16 + 0.5, $"MaxHeight {w.MaxHeight}");
            Assert.True(w.ActualHeight <= w.MaxHeight + 0.5, $"{w.ActualHeight} > {w.MaxHeight}");
            var scroll = Sta.Descendants<System.Windows.Controls.ScrollViewer>(w).First();
            Assert.True(scroll.ScrollableHeight > 0, "content taller than the screen must scroll");
            Assert.True(mover.Last.Y >= 0 && mover.Last.Y + w.ActualHeight <= 452 + 1);
        });
    }

    [Fact]
    public void WhenTheContentGrowsWhileOpen_TheFlyoutIsPlacedAgain_BottomEdgeStaysAboveTheTaskbar()
    {
        Sta.Run(() =>
        {
            var m = new MonitorArea(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0, true);
            var service = new FakeService { Current = Snap.Make(Snap.T0) };
            var vm = new ShellViewModel(service, new FakeTicker(), new FakeClock(Snap.T0), Snap.Utc);
            var mover = new PlacementMover();
            var w = new FlyoutWindow(vm, new PlacementMonitors(m), mover) { Anchor = new PxPoint(1700, 1050) };
            vm.SetFlyoutVisible(true);
            ((IFlyout)w).Show();
            Sta.Flush();
            double before = mover.Last.Y + w.ActualHeight;
            service.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), Snap.Rec(12, Snap.T0.AddDays(4), Snap.T0.AddMinutes(-3))), Snap.Codex(Snap.T0.AddMinutes(-1)));
            vm.Refresh();                                                                  // rows appear: the window grows
            Sta.Flush();
            Sta.Flush();
            double after = mover.Last.Y + w.ActualHeight;
            Assert.InRange(before, 1032 - 8 - 1, 1032 - 8 + 1);
            Assert.InRange(after, 1032 - 8 - 1, 1032 - 8 + 1);                              // still one gap above the taskbar after growing
        });
    }

    private sealed class PlacementMonitors(MonitorArea m) : IMonitorSource
    {
        public IReadOnlyList<MonitorArea> Monitors() => [m];
    }

    private sealed class PlacementMover : IWindowMover
    {
        public PxPoint Last { get; private set; }

        public void MoveTo(nint hwnd, PxPoint topLeft) => Last = topLeft;
    }

    [Fact]
    public void SetupGuideScreenshots_RenderedForReview()
    {
        string dir = Path.Combine(ArchitectureTests.Root(), "artifacts", "screenshots");
        Directory.CreateDirectory(dir);
        foreach ((string name, ClaudeSetupState state, string? exe) in new (string, ClaudeSetupState, string?)[]
                 {
                     ("8-setup-connect", ClaudeSetupState.NotConfigured, null),
                     ("9-setup-existing-statusline", ClaudeSetupState.OtherStatusLine, @"C:\Apps\QuotaDuck\quotaduck-statusline.exe"),
                     ("10-setup-connected-waiting", ClaudeSetupState.BridgeConfigured, null),
                 })
        {
            Sta.Run(() =>
            {
                (FlyoutWindow w, ShellViewModel vm, _) = MakeWithSetup(state, exe);
                vm.SetFlyoutVisible(true);
                ((IFlyout)w).Show();
                Sta.Flush();
                var content = (FrameworkElement)w.Content;
                content.UpdateLayout();
                int pw = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
                int ph = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
                var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                var host = new Border { Width = pw, Height = ph, Background = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(-content.Margin.Left, -content.Margin.Top, pw, ph) } };
                host.Measure(new Size(pw, ph));
                host.Arrange(new Rect(0, 0, pw, ph));
                bmp.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using FileStream fs = File.Create(Path.Combine(dir, name + ".png"));
                enc.Save(fs);
            });
        }
    }

    [Fact]
    public void Screenshots_RenderedForReview()
    {
        string dir = Path.Combine(ArchitectureTests.Root(), "artifacts", "screenshots");
        Directory.CreateDirectory(dir);
        ClaudeState fresh = new(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), Snap.Rec(12, Snap.T0.AddDays(4), Snap.T0.AddMinutes(-3)));
        ClaudeState elapsed = new(Snap.Rec(80, Snap.T0.AddMinutes(-20), Snap.T0.AddHours(-4)), Snap.Rec(20, Snap.T0.AddDays(4), Snap.T0.AddHours(-3)));
        (string Name, AppSnapshot Snapshot)[] cases =
        [
            ("1-waiting-and-not-read", Snap.Make(Snap.T0)),
            ("2-both-fresh", Snap.Make(Snap.T0, fresh, Snap.Codex(Snap.T0.AddMinutes(-1)))),
            ("3-claude-elapsed-codex-cooldown", Snap.Make(Snap.T0, elapsed, Snap.Codex(Snap.T0.AddMinutes(-1)), allowed: false, allowedAt: Snap.T0.AddSeconds(41))),
            ("5-claude-sleeping-codex-shallow", Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(30, Snap.T0.AddHours(1), Snap.T0.AddHours(-2).AddMinutes(-10)), Snap.Rec(55, Snap.T0.AddDays(3), Snap.T0.AddHours(-3))), Snap.Codex(Snap.T0.AddMinutes(-2), used5h: 88, usedWk: 40))),
            ("6-codex-error-no-data-claude-reading", Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(12, Snap.T0.AddHours(4), Snap.T0.AddMinutes(-2)), Snap.Rec(60, Snap.T0.AddDays(3), Snap.T0.AddMinutes(-2))), codexIssue: ProviderIssue.CodexNotInstalled, attempt: new ProviderAttempt(Snap.T0, ProviderErrorKind.NotFound))),
            ("7-codex-reading", Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(3), Snap.T0.AddMinutes(-3)), Snap.Rec(12, Snap.T0.AddDays(4), Snap.T0.AddMinutes(-3))), Snap.Codex(Snap.T0.AddMinutes(-12)), reading: true, allowed: false)),
            ("4-codex-failed-claude-quiet", Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(42, Snap.T0.AddHours(1), Snap.T0.AddHours(-2).AddMinutes(-10)), null), Snap.Codex(Snap.T0.AddMinutes(-15)), codexIssue: ProviderIssue.TemporaryProviderFailure, attempt: new ProviderAttempt(Snap.T0.AddMinutes(-1), ProviderErrorKind.Timeout))),
        ];
        foreach ((string name, AppSnapshot snapshot) in cases)
        {
            Sta.Run(() =>
            {
                (FlyoutWindow w, ShellViewModel vm, FakeService svc, _) = Make();
                svc.Current = snapshot;
                vm.Refresh();
                ((IFlyout)w).Show();
                Sta.Flush();
                var content = (FrameworkElement)w.Content;
                int pw = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
                int ph = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
                var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                var host = new Border { Background = SystemColors.WindowBrush, Width = pw, Height = ph };
                var vb = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(-content.Margin.Left, -content.Margin.Top, pw, ph) };
                host.Background = vb;
                host.Measure(new Size(pw, ph));
                host.Arrange(new Rect(0, 0, pw, ph));
                bmp.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using FileStream fs = File.Create(Path.Combine(dir, name + ".png"));
                enc.Save(fs);
                Assert.True(pw > 100 && ph > 100);
            });
        }
    }
}
