using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

/// <summary>
/// Invariant: each window's state is independent and authoritative. The provider roll-up exists for a single
/// tray/status indicator only and must never influence, aggregate into, or override an individual window.
/// </summary>
public class WindowIndependenceTests
{
    private static ProviderInput ClaudeIn(params WindowObservation[] w) => new(ProviderId.Claude, true, w, ClaudeWindows.Expected);

    private static readonly TimeSpan Weekly = H(168);

    /// <summary>30 deliberately varied observations covering every state, anomaly and source for a given window.</summary>
    private static List<WindowObservation> Variants(WindowKey key, TimeSpan duration)
    {
        var list = new List<WindowObservation>();
        foreach (double pct in new[] { 10.0, 80.0 })
        foreach (double ageMin in new[] { 1.0, 15.0, 180.0 })
        foreach (double? resetMin in new double?[] { -1, 30, 120, null })
        {
            list.Add(Obs(pct: pct, evidenceAge: M(ageMin), resetsIn: resetMin is { } r ? M(r) : null, hasReset: resetMin is not null, duration: duration, key: key));
        }

        list.Add(Obs(pct: double.NaN, key: key, duration: duration));                                   // invalid
        list.Add(Obs(pct: 50, evidenceAge: -M(30), key: key, duration: duration));                      // clock skew
        list.Add(Obs(pct: 50, resetsIn: H(24 * 60), key: key, duration: duration));                     // implausible reset
        list.Add(Obs(ObservationSource.PolledRead, 50, M(5), key: key, duration: duration));            // polled fresh
        list.Add(Obs(ObservationSource.PolledRead, 50, H(3), resetsIn: H(1), key: key, duration: duration)); // polled sleeping
        list.Add(Obs(pct: 100, limitReached: true, key: key, duration: duration));
        Assert.Equal(30, list.Count);
        return list;
    }

    [Fact]
    public void EveryWindowView_IsIdenticalAloneOrBesideAnySibling_ForAll900Pairs()
    {
        List<WindowObservation> fives = Variants(WindowKey.ClaudeFiveHour, H(5));
        List<WindowObservation> sevens = Variants(WindowKey.ClaudeSevenDay, Weekly);
        int pairs = 0;
        foreach (WindowObservation a in fives)
        {
            WindowView aloneA = WindowDeriver.Derive(a, T0, Policy);
            foreach (WindowObservation b in sevens)
            {
                pairs++;
                ProviderView pv = ProviderDeriver.Derive(ClaudeIn(a, b), T0, Policy);
                Assert.Equal(aloneA, pv.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour));
                Assert.Equal(WindowDeriver.Derive(b, T0, Policy), pv.Windows.Single(w => w.Key == WindowKey.ClaudeSevenDay));
            }
        }

        Assert.Equal(900, pairs);
    }

    [Fact]
    public void FreshFiveHour_DoesNotMakeASleepingWeekly_LookFresh()
    {
        WindowObservation five = Obs(evidenceAge: M(1), resetsIn: H(3));                                                     // Fresh
        WindowObservation seven = Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: H(30), resetsIn: H(100), duration: Weekly); // Sleeping
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy);

        Assert.Equal(WindowState.Fresh, v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour).State);
        Assert.Equal(WindowState.Sleeping, v.Windows.Single(w => w.Key == WindowKey.ClaudeSevenDay).State);   // authoritative
        Assert.Equal(ProviderRollup.Fresh, v.Rollup);                                                          // tray summary only
    }

    [Fact]
    public void ElapsedWindow_StaysElapsed_EvenWithAFreshSibling_AndShowsNoNumber()
    {
        WindowObservation five = Obs(pct: 90, evidenceAge: M(1), resetsIn: -M(1));                                            // Elapsed
        WindowObservation seven = Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: M(1), resetsIn: H(100), duration: Weekly);  // Fresh
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy);

        WindowView f = v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour);
        Assert.Equal(WindowState.Elapsed, f.State);
        Assert.Null(f.DisplayedUsedPercent);
        Assert.Null(f.DisplayedRemainingPercent);
        Assert.Equal(ProviderRollup.Fresh, v.Rollup);   // the roll-up being Fresh must not rescue the elapsed window
    }

    [Fact]
    public void SleepingFiveHour_NextToFreshWeekly_StaysSleeping()
    {
        WindowObservation five = Obs(evidenceAge: H(5), resetsIn: H(1));
        WindowObservation seven = Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: M(1), resetsIn: H(100), duration: Weekly);
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy);
        Assert.Equal(WindowState.Sleeping, v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour).State);
    }

    [Fact]
    public void ChangingASibling_ChangesTheRollupMaybe_ButNeverTheOtherWindow()
    {
        WindowObservation five = Obs(evidenceAge: M(30), resetsIn: H(3));   // Quiet
        WindowView baseline = WindowDeriver.Derive(five, T0, Policy);
        foreach (WindowObservation sibling in Variants(WindowKey.ClaudeSevenDay, Weekly))
        {
            ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, sibling), T0, Policy);
            Assert.Equal(baseline, v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour));
        }
    }

    [Fact]
    public void RollupMovesWithTheBestWindow_WhileWindowsStayPut()
    {
        WindowObservation five = Obs(evidenceAge: H(5), resetsIn: H(1));                                                      // Sleeping
        var withSleepingWeekly = ProviderDeriver.Derive(ClaudeIn(five, Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: H(6), resetsIn: H(100), duration: Weekly)), T0, Policy);
        var withFreshWeekly = ProviderDeriver.Derive(ClaudeIn(five, Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: M(1), resetsIn: H(100), duration: Weekly)), T0, Policy);
        Assert.Equal(ProviderRollup.Sleeping, withSleepingWeekly.Rollup);
        Assert.Equal(ProviderRollup.Fresh, withFreshWeekly.Rollup);                                                           // summary changed...
        Assert.Equal(withSleepingWeekly.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour),
                     withFreshWeekly.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour));                                // ...the window did not
    }

    [Fact]
    public void ProviderView_CarriesNoAggregatedNumber()
    {
        // Structural guard: nothing numeric is rolled up at provider level. Percentages live only on WindowView.
        string[] names = typeof(ProviderView).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("Percent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Used", StringComparison.OrdinalIgnoreCase) || n.Contains("Remaining", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(QuotaView).GetProperties().Select(p => p.Name), n => n.Contains("Percent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowDeriver_HasNoInputThroughWhichTheRollupOrASiblingCouldLeakIn()
    {
        // WindowDeriver's public API takes only the observation, now and policy.
        var m = typeof(WindowDeriver).GetMethod(nameof(WindowDeriver.Derive))!;
        Assert.Equal(["obs", "now", "policy"], m.GetParameters().Select(p => p.Name!));
    }
}
