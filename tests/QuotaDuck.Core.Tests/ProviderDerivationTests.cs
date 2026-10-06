using QuotaDuck.Core.Claude;
using QuotaDuck.Core.Codex;
using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

public class ProviderDerivationTests
{
    private static ProviderInput ClaudeIn(params WindowObservation[] w) => new(ProviderId.Claude, true, w, ClaudeWindows.Expected);

    private static ProviderInput CodexIn(ProviderErrorKind? error = null, params WindowObservation[] w) =>
        new(ProviderId.Codex, true, w, null, error is null ? null : new ProviderAttempt(T0, error));

    // ---- the reason/rollup table (docs/core/FRESHNESS-STATE-TABLE.md), one case per cell ----

    [Fact]
    public void Claude_NeverReported_WaitsForTerminal_AndShowsNoNumbersAnywhere()
    {
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(), T0, Policy);
        Assert.Equal(ProviderRollup.NoData, v.Rollup);
        Assert.Equal(ProviderReason.WaitingForTerminalClaudeCode, v.Reason);
        Assert.Equal(2, v.Windows.Count);                       // both expected windows are present...
        Assert.All(v.Windows, w =>
        {
            Assert.Equal(WindowState.NoData, w.State);          // ...as "not reported", never as 0%
            Assert.Null(w.DisplayedUsedPercent);
            Assert.Null(w.DisplayedRemainingPercent);
        });
        Assert.Null(v.MostConstrained);
        Assert.Null(v.FreshestEvidenceAge);
        Assert.Null(v.NextTransitionAt);
    }

    [Fact]
    public void Claude_RollupAndReason_PerState()
    {
        Assert.Equal((ProviderRollup.Fresh, ProviderReason.None), Cell(Obs(evidenceAge: M(1))));
        Assert.Equal((ProviderRollup.Quiet, ProviderReason.None), Cell(Obs(evidenceAge: M(30))));
        Assert.Equal((ProviderRollup.Sleeping, ProviderReason.ClaudeNoRecentReport), Cell(Obs(evidenceAge: H(5), resetsIn: H(1))));
        Assert.Equal((ProviderRollup.Elapsed, ProviderReason.AwaitingNextClaudeReport), Cell(Obs(resetsIn: -M(1))));

        static (ProviderRollup, ProviderReason) Cell(WindowObservation o)
        {
            ProviderView v = ProviderDeriver.Derive(ClaudeIn(o), T0, Policy);
            return (v.Rollup, v.Reason);
        }
    }

    [Fact]
    public void Codex_RollupAndReason_PerState_WithAndWithoutErrorOverlay()
    {
        WindowObservation fresh = Fx.Polled(40, M(1)), quiet = Fx.Polled(40, M(30)), sleeping = Fx.Polled(40, H(3), H(1)), elapsed = Fx.Polled(40, M(1), -M(1));

        Assert.Equal((ProviderRollup.NoData, ProviderReason.CodexNotReadYet), Cell(null));
        Assert.Equal((ProviderRollup.NoData, ProviderReason.ReadFailedNoData), Cell(ProviderErrorKind.Timeout));
        Assert.Equal((ProviderRollup.Fresh, ProviderReason.None), Cell(null, fresh));
        Assert.Equal((ProviderRollup.Fresh, ProviderReason.LastReadFailed), Cell(ProviderErrorKind.Rejected, fresh));
        Assert.Equal((ProviderRollup.Quiet, ProviderReason.None), Cell(null, quiet));
        Assert.Equal((ProviderRollup.Quiet, ProviderReason.LastReadFailed), Cell(ProviderErrorKind.Malformed, quiet));
        Assert.Equal((ProviderRollup.Sleeping, ProviderReason.CodexReadsPaused), Cell(null, sleeping));
        Assert.Equal((ProviderRollup.Sleeping, ProviderReason.LastReadFailed), Cell(ProviderErrorKind.StartFailed, sleeping));
        Assert.Equal((ProviderRollup.Elapsed, ProviderReason.AwaitingCodexRead), Cell(null, elapsed));
        Assert.Equal((ProviderRollup.Elapsed, ProviderReason.LastReadFailed), Cell(ProviderErrorKind.NotFound, elapsed));

        static (ProviderRollup, ProviderReason) Cell(ProviderErrorKind? err, params WindowObservation[] w)
        {
            ProviderView v = ProviderDeriver.Derive(CodexIn(err, w), T0, Policy);
            return (v.Rollup, v.Reason);
        }
    }

    [Fact]
    public void FailedRead_DoesNotRefreshAnything_DataKeepsAgingNormally()
    {
        // A failed attempt "now" must not change the age of the older successful read.
        ProviderView v = ProviderDeriver.Derive(CodexIn(ProviderErrorKind.Timeout, Fx.Polled(40, M(45))), T0, Policy);
        Assert.Equal(WindowState.Quiet, v.Windows.Single().State);
        Assert.Equal(M(45), v.FreshestEvidenceAge);
        Assert.Equal(ProviderErrorKind.Timeout, v.Error);
    }

    [Fact]
    public void NotConfigured_OverridesEverything_AndShowsNothingActionable()
    {
        var input = new ProviderInput(ProviderId.Claude, false, [Obs(limitReached: true)], ClaudeWindows.Expected);
        ProviderView v = ProviderDeriver.Derive(input, T0, Policy);
        Assert.Equal(ProviderRollup.NotConfigured, v.Rollup);
        Assert.Equal(ProviderReason.NotConfigured, v.Reason);
        Assert.Null(v.MostConstrained);
        Assert.False(v.LimitReached);
        Assert.Null(v.NextTransitionAt);
    }

    // ---- window selection / composition ----

    [Fact]
    public void ExpectedWindow_MissingOne_IsNoData_OtherStillDerived()
    {
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(Obs(pct: 30, evidenceAge: M(1))), T0, Policy);   // only five_hour reported
        Assert.Equal(ProviderRollup.Fresh, v.Rollup);
        Assert.Equal(WindowState.Fresh, v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour).State);
        WindowView weekly = v.Windows.Single(w => w.Key == WindowKey.ClaudeSevenDay);
        Assert.Equal(WindowState.NoData, weekly.State);
        Assert.Null(weekly.DisplayedUsedPercent);
        Assert.Equal(["five_hour", "seven_day"], v.Windows.Select(w => w.Key.Id));
    }

    [Fact]
    public void Rollup_IsTheBestWindow_NotTheWorst()
    {
        WindowObservation five = Obs(evidenceAge: H(5), resetsIn: H(1));                             // Sleeping
        WindowObservation seven = Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: M(2), resetsIn: H(100), duration: H(168));   // Fresh
        Assert.Equal(ProviderRollup.Fresh, ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy).Rollup);
    }

    [Fact]
    public void OneWindowElapsed_OtherLive_ProviderStaysLive_ButElapsedWindowShowsNoNumber()
    {
        WindowObservation five = Obs(pct: 90, resetsIn: -M(1));
        WindowObservation seven = Obs(pct: 20, key: WindowKey.ClaudeSevenDay, evidenceAge: M(1), resetsIn: H(100), duration: H(168));
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy);
        Assert.Equal(ProviderRollup.Fresh, v.Rollup);
        Assert.Null(v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour).DisplayedUsedPercent);
        Assert.Equal(WindowKey.ClaudeSevenDay, v.MostConstrained);        // the elapsed 90% must not win
    }

    [Fact]
    public void MostConstrained_HighestUsed_TieBreaksOnEarlierReset_ThenKey()
    {
        WindowObservation a = Obs(pct: 70, key: WindowKey.ClaudeFiveHour, resetsIn: H(2));
        WindowObservation b = Obs(pct: 70, key: WindowKey.ClaudeSevenDay, resetsIn: H(1), duration: H(168));
        Assert.Equal(WindowKey.ClaudeSevenDay, ProviderDeriver.Derive(ClaudeIn(a, b), T0, Policy).MostConstrained);
        WindowObservation c = Obs(pct: 71, key: WindowKey.ClaudeFiveHour, resetsIn: H(4));
        Assert.Equal(WindowKey.ClaudeFiveHour, ProviderDeriver.Derive(ClaudeIn(c, b), T0, Policy).MostConstrained);
    }

    [Fact]
    public void ForeignProviderWindows_AreIgnored_DuplicateKeysLastWins()
    {
        ProviderView v = ProviderDeriver.Derive(
            ClaudeIn(Fx.Polled(99, M(1)), Obs(pct: 10), Obs(pct: 20)), T0, Policy);
        WindowView five = v.Windows.Single(w => w.Key == WindowKey.ClaudeFiveHour);
        Assert.Equal(20, five.DisplayedUsedPercent);
        Assert.DoesNotContain(v.Windows, w => w.Key.Provider == ProviderId.Codex);
    }

    [Fact]
    public void LimitReached_PropagatesOnlyFromDisplayableWindows()
    {
        Assert.True(ProviderDeriver.Derive(CodexIn(null, Fx.Polled(100, M(1), limitReached: true)), T0, Policy).LimitReached);
        Assert.False(ProviderDeriver.Derive(CodexIn(null, Fx.Polled(100, M(1), -M(1), limitReached: true)), T0, Policy).LimitReached);
    }

    [Fact]
    public void NextTransition_IsTheEarliestAcrossWindows()
    {
        WindowObservation five = Obs(evidenceAge: M(1), resetsIn: H(3));                                    // next = +9 min
        WindowObservation seven = Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: M(1), resetsIn: M(4), duration: H(168));   // next = reset +4 min
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy);
        Assert.Equal(T0 + M(4), v.NextTransitionAt);
    }

    [Fact]
    public void Ages_ProviderHeadlineUsesTheFreshestWindow_AndReceivedAgeIsSeparate()
    {
        WindowObservation five = Obs(evidenceAge: H(3), receivedAge: M(1), resetsIn: H(2));
        WindowObservation seven = Obs(key: WindowKey.ClaudeSevenDay, evidenceAge: M(40), receivedAge: M(1), resetsIn: H(100), duration: H(168));
        ProviderView v = ProviderDeriver.Derive(ClaudeIn(five, seven), T0, Policy);
        Assert.Equal(M(40), v.FreshestEvidenceAge);
        Assert.Equal(M(1), v.FreshestReceivedAge);
    }

    // ---- independence (Phase 1 §8): one provider must never affect the other ----

    [Fact]
    public void Providers_AreDerivedIndependently()
    {
        ProviderInput staleClaude = ClaudeIn(Obs(evidenceAge: H(5), resetsIn: H(1)));
        ProviderInput freshCodex = CodexIn(null, Fx.Polled(10, M(1)));
        QuotaView q = QuotaDeriver.Derive(staleClaude, freshCodex, T0, Policy);
        Assert.Equal(ProviderRollup.Sleeping, q.Claude.Rollup);
        Assert.Equal(ProviderRollup.Fresh, q.Codex.Rollup);

        // swap which one is stale: results mirror, nothing bleeds across
        QuotaView r = QuotaDeriver.Derive(ClaudeIn(Obs(evidenceAge: M(1))), CodexIn(ProviderErrorKind.Timeout), T0, Policy);
        Assert.Equal(ProviderRollup.Fresh, r.Claude.Rollup);
        Assert.Equal(ProviderRollup.NoData, r.Codex.Rollup);
        Assert.Equal(ProviderReason.ReadFailedNoData, r.Codex.Reason);
        Assert.Equal(ProviderReason.None, r.Claude.Reason);
    }

    [Fact]
    public void QuotaDeriver_RejectsSwappedInputs_AndPicksEarliestTransition()
    {
        Assert.Throws<ArgumentException>(() => QuotaDeriver.Derive(CodexIn(), CodexIn(), T0, Policy));
        Assert.Throws<ArgumentException>(() => QuotaDeriver.Derive(ClaudeIn(), ClaudeIn(), T0, Policy));
        QuotaView q = QuotaDeriver.Derive(ClaudeIn(Obs(evidenceAge: M(9))), CodexIn(null, Fx.Polled(10, M(1))), T0, Policy);
        Assert.Equal(T0 + M(1), q.NextTransitionAt);      // Claude fresh bound ends in 1 min; Codex in 19 min
    }

    [Fact]
    public void Codex_ReadReplacesPreviousSnapshotWholesale_Claude_MergesPerWindow()
    {
        var first = CodexObservations.FromRead(
            [new CodexBucket("codex", null, Reading(10), Reading(5, 10_080), false)], T0);
        var second = CodexObservations.FromRead(
            [new CodexBucket("codex", null, Reading(12), null, false)], T0 + M(10));
        ProviderView v1 = ProviderDeriver.Derive(CodexObservations.ToProviderInput(first, null), T0, Policy);
        ProviderView v2 = ProviderDeriver.Derive(CodexObservations.ToProviderInput(second, null), T0 + M(10), Policy);
        Assert.Equal(2, v1.Windows.Count);
        Assert.Single(v2.Windows);     // the secondary window vanished with the new read; nothing stale lingers
    }
}
