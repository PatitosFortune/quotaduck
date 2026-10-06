using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;
using QuotaDuck.Art;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>Provider state -> pond and duck (and gauges). Each provider is mapped from its own snapshot only.</summary>
public class PondMappingTests
{
    private static readonly DateTimeOffset T = Snap.T0;

    private static ClaudeState Claude(double five, DateTimeOffset changed, DateTimeOffset? reset5 = null, double week = 10) =>
        new(Snap.Rec(five, reset5 ?? T.AddHours(3), changed), Snap.Rec(week, T.AddDays(4), changed));

    private static ShellPresentation P(AppSnapshot s) => Presenter.Present(s, RefreshFeedback.None, Snap.Utc);

    // ---------------- water level and duck for real numbers ----------------

    [Theory]
    [InlineData(0, 1.0, DuckPose.Floating)]          // nothing used: a full pond
    [InlineData(20, 0.8, DuckPose.Floating)]
    [InlineData(42, 0.58, DuckPose.Floating)]        // medium: duck still floats
    [InlineData(60, 0.4, DuckPose.Floating)]
    [InlineData(75, 0.25, DuckPose.Standing)]        // low: shallow, the duck stands
    [InlineData(90, 0.1, DuckPose.Standing)]
    [InlineData(100, 0.0, DuckPose.Standing)]        // limit reached: a tiny puddle
    public void FreshClaude_WaterIsWhatIsLeft_DuckFloatsOrStandsByDepth(double used, double remaining, DuckPose duck)
    {
        PondScene pond = P(Snap.Make(T, Claude(used, T.AddMinutes(-1), week: 0))).Claude.Pond;
        Assert.Equal(PondCondition.Normal, pond.Condition);
        Assert.Equal(remaining, pond.Remaining!.Value, 6);
        Assert.Equal(duck, pond.Duck);
        Assert.True(pond.FaceRight);                                                    // Claude's duck faces right, Codex's left
        Assert.False(pond.Alert);
        Assert.False(pond.Busy);
    }

    [Fact]
    public void ThePondShowsTheTightestWindow_NotTheAverage_NotTheFirst()
    {
        ClaudeState s = new(Snap.Rec(10, T.AddHours(3), T.AddMinutes(-1)), Snap.Rec(80, T.AddDays(4), T.AddMinutes(-1)));
        PondScene pond = P(Snap.Make(T, s)).Claude.Pond;
        Assert.Equal(0.2, pond.Remaining!.Value, 6);                                    // the weekly window (80% used) is tighter than the 5-hour one
        Assert.Contains("Weekly", PondMapping.Describe("Claude", Snap.Make(T, s).Claude));
    }

    [Fact]
    public void Quiet_StaysAwake_Sleeping_SleepsWithSubduedColours()
    {
        PondScene quiet = P(Snap.Make(T, Claude(30, T.AddMinutes(-40)))).Claude.Pond;
        Assert.Equal((PondCondition.Normal, DuckPose.Floating), (quiet.Condition, quiet.Duck));

        PondScene asleep = P(Snap.Make(T, Claude(30, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1)))).Claude.Pond;
        Assert.Equal((PondCondition.Subdued, DuckPose.SleepingFloating), (asleep.Condition, asleep.Duck));
        Assert.Equal(0.7, asleep.Remaining!.Value, 6);                                  // the last known level is still the water level

        PondScene asleepShallow = P(Snap.Make(T, Claude(85, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1)))).Claude.Pond;
        Assert.Equal((PondCondition.Subdued, DuckPose.SleepingStanding), (asleepShallow.Condition, asleepShallow.Duck));
    }

    // ---------------- no number: hatched / dry, never a faked level ----------------

    [Fact]
    public void ElapsedWindows_AreAHatchedBowlWithAHollowDuck_NotAFullPond()
    {
        ClaudeState s = new(Snap.Rec(80, T.AddMinutes(-20), T.AddHours(-4)), Snap.Rec(20, T.AddMinutes(-5), T.AddDays(-6)));
        PondScene pond = P(Snap.Make(T, s)).Claude.Pond;
        Assert.Equal((PondCondition.Unknown, DuckPose.Unknown), (pond.Condition, pond.Duck));
        Assert.Null(pond.Remaining);                                                    // never "100%"
        Assert.All(P(Snap.Make(T, s)).Claude.Windows, w => Assert.Equal(GaugeCondition.Unknown, w.Gauge.Condition));
    }

    [Fact]
    public void OneElapsedAndOneLive_DrawsTheLiveWater_AndTheElapsedGaugeStaysHatched()
    {
        ClaudeState s = new(Snap.Rec(80, T.AddMinutes(-20), T.AddHours(-4)), Snap.Rec(20, T.AddDays(4), T.AddMinutes(-1)));
        ProviderCardModel claude = P(Snap.Make(T, s)).Claude;
        Assert.Equal(PondCondition.Normal, claude.Pond.Condition);
        Assert.Equal(0.8, claude.Pond.Remaining!.Value, 6);
        Assert.Equal(GaugeCondition.Unknown, claude.Windows.Single(w => w.Label == "5-hour").Gauge.Condition);
        Assert.Equal(GaugeCondition.Normal, claude.Windows.Single(w => w.Label == "Weekly").Gauge.Condition);
    }

    [Fact]
    public void WaitingForClaude_IsADryHatchedBowl_AndHollowDuck_NotASleepingOne()
    {
        ShellPresentation p = P(Snap.Make(T));
        Assert.Equal((PondCondition.Empty, DuckPose.Unknown), (p.Claude.Pond.Condition, p.Claude.Pond.Duck));
        Assert.Null(p.Claude.Pond.Remaining);
        Assert.All(p.Claude.Windows, w => Assert.Equal(GaugeCondition.Empty, w.Gauge.Condition));
        Assert.Contains("dry", p.Claude.PondDescription);
        Assert.Contains("no data", p.Claude.PondDescription);
    }

    [Fact]
    public void CodexFailure_WithNoData_IsAnErrorPond_WithAnAlertDuck()
    {
        PondScene pond = P(Snap.Make(T, codexIssue: ProviderIssue.CodexNotInstalled, attempt: new ProviderAttempt(T, ProviderErrorKind.NotFound))).Codex.Pond;
        Assert.Equal((PondCondition.Error, DuckPose.Alert), (pond.Condition, pond.Duck));
        Assert.False(pond.FaceRight);
    }

    [Fact]
    public void CodexFailure_WithOldData_KeepsTheLastWater_AndAddsAWarningMark()
    {
        AppSnapshot s = Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-15)), codexIssue: ProviderIssue.TemporaryProviderFailure, attempt: new ProviderAttempt(T.AddMinutes(-1), ProviderErrorKind.Timeout));
        PondScene pond = P(s).Codex.Pond;
        Assert.Equal(PondCondition.Normal, pond.Condition);
        Assert.Equal(0.65, pond.Remaining!.Value, 6);                                  // Codex's weekly window (35% used) is the tighter one
        Assert.True(pond.Alert);
        Assert.Contains("warning", PondMapping.Describe("Codex", s.Codex));
    }

    [Fact]
    public void ClaudeStateFileProblem_AlsoShowsTheWarningMark_OverTheLastKnownWater()
    {
        PondScene pond = P(Snap.Make(T, Claude(42, T.AddMinutes(-3)), claudeIssue: ProviderIssue.ClaudeStateUnreadable)).Claude.Pond;
        Assert.True(pond.Alert);
        Assert.Equal(0.58, pond.Remaining!.Value, 6);
    }

    [Fact]
    public void CodexReading_ShowsBusyDots_AndKeepsTheOldWater()
    {
        PondScene pond = P(Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-12)), reading: true, allowed: false)).Codex.Pond;
        Assert.True(pond.Busy);
        Assert.Equal(PondCondition.Normal, pond.Condition);
        Assert.Contains("Reading now", PondMapping.Describe("Codex", Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-12)), reading: true, allowed: false).Codex));
    }

    [Fact]
    public void ProvidersDisabled_AreDryHollow_AndIndependent()
    {
        ShellPresentation p = P(Snap.Make(T, Claude(42, T.AddMinutes(-3)), claudeEnabled: true, codexEnabled: false));
        Assert.Equal(PondCondition.Normal, p.Claude.Pond.Condition);
        Assert.Equal((PondCondition.Empty, DuckPose.Unknown), (p.Codex.Pond.Condition, p.Codex.Pond.Duck));
    }

    [Fact]
    public void TheTwoDucks_AreIndependent_OneSleepsWhileTheOtherSwims()
    {
        AppSnapshot s = Snap.Make(T, Claude(30, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1)), Snap.Codex(T.AddMinutes(-1), used5h: 90, usedWk: 10));
        ShellPresentation p = P(s);
        Assert.Equal(DuckPose.SleepingFloating, p.Claude.Pond.Duck);
        Assert.Equal(DuckPose.Standing, p.Codex.Pond.Duck);                              // Codex is fresh but at 90% used: shallow
        Assert.Equal(PondCondition.Subdued, p.Claude.Pond.Condition);
        Assert.Equal(PondCondition.Normal, p.Codex.Pond.Condition);
    }

    // ---------------- text equivalents ----------------

    [Fact]
    public void PondDescriptions_SpellOutLevelWindowAndDuck_InWords()
    {
        string fresh = P(Snap.Make(T, Claude(42, T.AddMinutes(-1)))).Claude.PondDescription;
        Assert.Equal("Claude pond: 58 percent of the water is left in the tightest window (5-hour); the duck is swimming.", fresh);
        Assert.Equal(
            "Claude pond: 15 percent of the water is left in the tightest window (5-hour); the duck is standing in shallow water.",
            P(Snap.Make(T, Claude(85, T.AddMinutes(-1)))).Claude.PondDescription);
        Assert.Contains("sleeping because the data is old", P(Snap.Make(T, Claude(30, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1)))).Claude.PondDescription);
        ClaudeState elapsed = new(Snap.Rec(80, T.AddMinutes(-20), T.AddHours(-4)), null);
        Assert.Contains("has reset and the new level is not known", P(Snap.Make(T, elapsed)).Claude.PondDescription);
        Assert.Contains("could not be read", P(Snap.Make(T, codexIssue: ProviderIssue.CodexNotTrusted)).Codex.PondDescription);
    }

    [Fact]
    public void NoDescriptionOrScene_ContainsAnythingSensitive_ForAnyIssue()
    {
        foreach (ProviderIssue issue in Enum.GetValues<ProviderIssue>())
        {
            ShellPresentation p = P(Snap.Make(T, Claude(42, T.AddMinutes(-3)), Snap.Codex(T.AddMinutes(-3)), claudeIssue: issue, codexIssue: issue));
            foreach (string text in new[] { p.Claude.PondDescription, p.Codex.PondDescription })
            {
                foreach (string bad in new[] { "Exception", "C:\\", ".exe", "accountId", "token", "stderr" })
                {
                    Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    // ---------------- gauges ----------------

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(25, 0.75)]
    [InlineData(100, 0.0)]
    public void GaugeWater_IsTheRemainingFraction(double used, double remaining)
    {
        GaugeScene g = P(Snap.Make(T, Claude(used, T.AddMinutes(-1)))).Claude.Windows.Single(w => w.Label == "5-hour").Gauge;
        Assert.Equal(GaugeCondition.Normal, g.Condition);
        Assert.Equal(remaining, g.Remaining!.Value, 6);
    }

    [Fact]
    public void SleepingGauge_IsSubdued()
    {
        GaugeScene g = P(Snap.Make(T, Claude(25, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1)))).Claude.Windows.Single(w => w.Label == "5-hour").Gauge;
        Assert.Equal(GaugeCondition.Subdued, g.Condition);
    }
}

public class PondAnimatorTests
{
    private static PondScene Water(double r) => new(r, PondCondition.Normal, DuckPose.Floating, true);

    private static PondScene Hatched => new(null, PondCondition.Unknown, DuckPose.Unknown, true);

    [Fact]
    public void FirstObservation_NeverAnimates()
    {
        var a = new PondAnimator();
        Assert.False(a.Observe(Water(0.9), allowAnimation: true));
        Assert.False(a.Active);
    }

    [Fact]
    public void ARefillFromElapsed_RunsEightStepsFromEmpty_ToTheTarget()
    {
        var a = new PondAnimator();
        a.Observe(Hatched, true);
        Assert.True(a.Observe(Water(0.8), true));
        Assert.Equal(0.0, a.Current(Water(0.8)).Remaining!.Value, 6);                  // starts dry
        double last = 0;
        for (int i = 0; i < PondAnimator.Steps - 1; i++)
        {
            Assert.True(a.Step());
            double v = a.Current(Water(0.8)).Remaining!.Value;
            Assert.True(v > last && v < 0.8);
            last = v;
        }

        Assert.False(a.Step());                                                         // the 8th step ends it
        Assert.Equal(0.8, a.Current(Water(0.8)).Remaining!.Value, 6);
        Assert.False(a.Active);
        Assert.False(a.Step());                                                         // idle steps are harmless
    }

    [Fact]
    public void ABigRiseInWater_Animates_SmallRisesAndAllFalls_DoNot()
    {
        var a = new PondAnimator();
        a.Observe(Water(0.2), true);
        Assert.False(a.Observe(Water(0.4), true));                                      // +0.2 < 0.25
        Assert.False(a.Observe(Water(0.1), true));                                      // falling water just moves
        Assert.True(a.Observe(Water(0.9), true));                                       // +0.8: a reset/refill
    }

    [Fact]
    public void ReducedMotion_OrHidden_NeverStartsOne_AndCancelsARunningOne()
    {
        var a = new PondAnimator();
        a.Observe(Hatched, true);
        Assert.False(a.Observe(Water(0.8), allowAnimation: false));
        Assert.Equal(0.8, a.Current(Water(0.8)).Remaining!.Value, 6);

        var b = new PondAnimator();
        b.Observe(Hatched, true);
        Assert.True(b.Observe(Water(0.8), true));
        Assert.False(b.Observe(Water(0.8), allowAnimation: false));                     // the user turned animations off / the flyout hid
        Assert.False(b.Active);
    }

    [Fact]
    public void ARunningRefill_IsRetargeted_NotRestarted_AndNonWaterScenesAreNeverAnimated()
    {
        var a = new PondAnimator();
        a.Observe(Hatched, true);
        a.Observe(Water(0.5), true);
        a.Step();
        a.Step();
        Assert.True(a.Observe(Water(0.9), true));
        for (int i = 0; i < 6; i++) { a.Step(); }
        Assert.False(a.Active);
        Assert.Equal(0.9, a.Current(Water(0.9)).Remaining!.Value, 6);

        PondScene dry = new(null, PondCondition.Empty, DuckPose.Unknown, true);
        Assert.Same(dry, new PondAnimator().Current(dry));
        Assert.False(a.Observe(dry, true));
    }
}

public class ViewModelAnimationTests
{
    private sealed class Rig
    {
        public FakeService Service { get; } = new();
        public FakeTicker Slow { get; } = new();
        public FakeTicker Fast { get; } = new();
        public bool Reduced { get; set; }
        public ShellViewModel Vm { get; }

        public Rig() => Vm = new ShellViewModel(Service, Slow, new FakeClock(Snap.T0), Snap.Utc, Fast, () => Reduced);
    }

    [Fact]
    public void TheFrame_TogglesOnlyWhileVisible_AndResetsOnHide()
    {
        var r = new Rig();
        Assert.Equal(0, r.Vm.Frame);
        r.Vm.SetFlyoutVisible(true);
        Assert.Equal(0, r.Vm.Frame);
        r.Slow.Tick();
        Assert.Equal(1, r.Vm.Frame);
        Assert.Equal(1, r.Vm.Current.Claude.Pond.Frame);
        r.Slow.Tick();
        Assert.Equal(0, r.Vm.Frame);
        r.Slow.Tick();
        r.Vm.SetFlyoutVisible(false);
        Assert.Equal(0, r.Vm.Frame);
        Assert.False(r.Slow.IsRunning);                                                 // no animation loop while hidden
        Assert.False(r.Fast.IsRunning);
        r.Slow.Tick();                                                                  // a stray tick after hiding is a no-op
        Assert.Equal(0, r.Vm.Frame);
    }

    [Fact]
    public void ReducedMotion_FreezesTheFrame_AndIsReReadEveryTimeTheFlyoutOpens()
    {
        var r = new Rig { Reduced = true };
        r.Vm.SetFlyoutVisible(true);
        Assert.True(r.Vm.ReducedMotion);
        r.Slow.Tick();
        r.Slow.Tick();
        Assert.Equal(0, r.Vm.Frame);
        Assert.Equal(0, r.Vm.Current.Codex.Pond.Frame);
        r.Vm.SetFlyoutVisible(false);

        r.Reduced = false;                                                              // the user re-enabled Windows animations
        r.Vm.SetFlyoutVisible(true);
        Assert.False(r.Vm.ReducedMotion);
        r.Slow.Tick();
        Assert.Equal(1, r.Vm.Frame);
    }

    [Fact]
    public void ARefill_RunsOnTheShortTickerOnly_ThenStopsIt()
    {
        var r = new Rig();
        ClaudeState elapsed = new(Snap.Rec(80, Snap.T0.AddMinutes(-20), Snap.T0.AddHours(-4)), null);
        r.Service.Current = Snap.Make(Snap.T0, elapsed);
        r.Vm.SetFlyoutVisible(true);
        Assert.False(r.Fast.IsRunning);

        ClaudeState refilled = new(Snap.Rec(10, Snap.T0.AddHours(3), Snap.T0), null);   // the next terminal report: a new window, 90% left
        r.Service.Current = Snap.Make(Snap.T0, refilled);
        r.Vm.Refresh();
        Assert.True(r.Fast.IsRunning);
        Assert.Equal(0.0, r.Vm.Current.Claude.Pond.Remaining!.Value, 6);
        for (int i = 0; i < PondAnimator.Steps; i++)
        {
            r.Fast.Tick();
        }

        Assert.False(r.Fast.IsRunning);
        Assert.Equal(0.9, r.Vm.Current.Claude.Pond.Remaining!.Value, 6);
    }

    [Fact]
    public void HidingTheFlyout_StopsARunningRefill_NoTimerSurvivesWhileHidden()
    {
        var r = new Rig();
        r.Service.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(80, Snap.T0.AddMinutes(-20), Snap.T0.AddHours(-4)), null));
        r.Vm.SetFlyoutVisible(true);
        r.Service.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(10, Snap.T0.AddHours(3), Snap.T0), null));
        r.Vm.Refresh();
        Assert.True(r.Fast.IsRunning);
        r.Vm.SetFlyoutVisible(false);
        Assert.False(r.Fast.IsRunning);
        Assert.False(r.Slow.IsRunning);
    }

    [Fact]
    public void NoRefillAnimation_WhileHidden_OrUnderReducedMotion()
    {
        foreach (bool reduced in new[] { false, true })
        {
            var r = new Rig { Reduced = reduced };
            ClaudeState elapsed = new(Snap.Rec(80, Snap.T0.AddMinutes(-20), Snap.T0.AddHours(-4)), null);
            r.Service.Current = Snap.Make(Snap.T0, elapsed);
            if (reduced)
            {
                r.Vm.SetFlyoutVisible(true);
            }

            r.Vm.Refresh();
            r.Service.Current = Snap.Make(Snap.T0, new ClaudeState(Snap.Rec(10, Snap.T0.AddHours(3), Snap.T0), null));
            r.Vm.Refresh();
            Assert.False(r.Fast.IsRunning);                                              // hidden (reduced=false case) or reduced: jumps straight to the new level
            if (reduced)
            {
                Assert.Equal(0.9, r.Vm.Current.Claude.Pond.Remaining!.Value, 6);
            }
        }
    }

    [Fact]
    public void AnUnchangedSnapshot_DoesNotRaiseAPropertyChange()
    {
        var r = new Rig { Reduced = true };
        r.Vm.SetFlyoutVisible(true);
        int changes = 0;
        r.Vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(ShellViewModel.Current) ? 1 : 0;
        r.Slow.Tick();
        r.Slow.Tick();
        r.Vm.Refresh();
        Assert.Equal(0, changes);                                                       // value comparison, not reference: no needless re-render
    }
}

public class TrayIconTests
{
    [Fact]
    public void TheEmbeddedIcon_IsTheGeneratorsOutput_Byte_For_Byte()
    {
        byte[] embedded = TrayIconResource.Bytes();
        Assert.Equal(TrayIcon.BuildIco(), embedded);                                    // regenerate with tools/QuotaDuck.AssetGen if the art changed
        string onDisk = Path.Combine(ArchitectureTests.Root(), "src", "QuotaDuck.App", "Assets", "quotaduck.ico");
        Assert.Equal(File.ReadAllBytes(onDisk), embedded);
    }

    [Fact]
    public void TheTrayPath_NeverTouchesWpfImaging()
    {
        string text = File.ReadAllText(Path.Combine(ArchitectureTests.Root(), "src", "QuotaDuck.App", "Shell", "Platform.cs"));
        foreach (string bad in new[] { "RenderTargetBitmap", "System.Windows.Media.Imaging", "DrawingVisual", "BitmapEncoder", "PlaceholderIcon", "System.Windows.Media;" })
        {
            Assert.DoesNotContain(bad, text);
        }

        foreach (string f in Directory.EnumerateFiles(Path.Combine(ArchitectureTests.Root(), "src", "QuotaDuck.App"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\")))
        {
            if (!f.EndsWith("PixelImage.cs"))
            {
                Assert.DoesNotContain("RenderTargetBitmap", File.ReadAllText(f));      // only the flyout's pixel images use WPF bitmaps (WriteableBitmap)
            }
        }
    }

    [Fact]
    public void TheIconLoads_AsASmallSystemIcon_FromTheEmbeddedResource()
    {
        using System.Drawing.Icon icon = TrayIconResource.Load();
        Assert.True(icon.Width >= 16);
        Assert.Equal(icon.Width, icon.Height);
    }

    [Fact]
    public void TheTrayMenu_IsCreatedLazily_OnTheFirstRightClick_WithOpenAndExit()
    {
        Sta.Run(() =>
        {
            using var tray = new WinFormsTray("t");
            var icon = (System.Windows.Forms.NotifyIcon)typeof(WinFormsTray).GetField("_icon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tray)!;
            Assert.Null(icon.ContextMenuStrip);                                         // nothing built (and no GDI+ loaded) until needed
            // WinForms routes the shell's right-button-down to NotifyIcon.MouseDown; raise it the way NotifyIcon does
            typeof(System.Windows.Forms.NotifyIcon).GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(icon, [new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Right, 1, 0, 0, 0)]);
            System.Windows.Forms.ContextMenuStrip menu = Assert.IsType<System.Windows.Forms.ContextMenuStrip>(icon.ContextMenuStrip);
            Assert.Equal("Open,Exit", string.Join(",", menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(i => i.Text)));
            bool opened = false, exited = false;
            tray.OpenRequested += () => opened = true;
            tray.ExitRequested += () => exited = true;
            menu.Items[0].PerformClick();
            menu.Items[1].PerformClick();
            Assert.True(opened);
            Assert.True(exited);
        });
    }
}
