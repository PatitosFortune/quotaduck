using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

public class ClaudeWindowMergerTests
{
    private const ClaudeWindowKind Five = ClaudeWindowKind.FiveHour;
    private static readonly DateTimeOffset R = T0 + H(3);   // a synthetic reset instant

    private static ClaudeMergeResult Merge(ClaudeWindowRecord? existing, double? pct, DateTimeOffset? reset, DateTimeOffset now, ClaudeWindowKind kind = Five) =>
        ClaudeWindowMerger.Merge(kind, existing, new ClaudeIncomingWindow(pct, reset), now, Policy);

    // ---- single-decision cases ----

    [Fact]
    public void NoIncoming_KeepsStoredRecordUntouched_NoWrite()
    {
        ClaudeWindowRecord rec = Rec(40, R, T0);
        ClaudeMergeResult r = ClaudeWindowMerger.Merge(Five, rec, null, T0 + M(5), Policy);
        Assert.Equal(ClaudeMergeDecision.NoIncoming, r.Decision);
        Assert.Same(rec, r.Record);
        Assert.False(r.ShouldPersist);
    }

    [Fact]
    public void FirstObservation_StartsChangedAndReceivedAtNow()
    {
        ClaudeMergeResult r = Merge(null, 12.5, R, T0);
        Assert.Equal(ClaudeMergeDecision.FirstObservation, r.Decision);
        Assert.Equal(new ClaudeWindowRecord(12.5, R, T0, T0), r.Record);
        Assert.True(r.ShouldPersist);
    }

    [Theory]
    [InlineData(null, ClaudeMergeDecision.RejectedMissingPercent)]
    [InlineData(-1.0, ClaudeMergeDecision.RejectedInvalidPercent)]
    [InlineData(100.5, ClaudeMergeDecision.RejectedInvalidPercent)]
    [InlineData(double.NaN, ClaudeMergeDecision.RejectedInvalidPercent)]
    [InlineData(double.PositiveInfinity, ClaudeMergeDecision.RejectedInvalidPercent)]
    public void BadPercent_IsRejected_StoredRecordKeptByReference(double? bad, ClaudeMergeDecision expected)
    {
        ClaudeWindowRecord rec = Rec(40, R, T0);
        ClaudeMergeResult r = Merge(rec, bad, R, T0 + M(1));
        Assert.Equal(expected, r.Decision);
        Assert.Same(rec, r.Record);
        Assert.False(r.ShouldPersist);
        Assert.Null(Merge(null, bad, R, T0).Record);          // and nothing is invented when nothing was stored
    }

    [Fact]
    public void ResetInThePast_IsRejected_ButWithinEpochToleranceIsAccepted()
    {
        ClaudeWindowRecord rec = Rec(40, R, T0);
        Assert.Equal(ClaudeMergeDecision.RejectedResetInPast, Merge(rec, 50, T0 - M(3), T0).Decision);
        Assert.Equal(ClaudeMergeDecision.RejectedResetInPast, Merge(rec, 50, T0 - M(2), T0).Decision);        // boundary: <= now - tolerance
        Assert.NotEqual(ClaudeMergeDecision.RejectedResetInPast, Merge(rec, 50, T0 - M(1), T0).Decision);
    }

    [Fact]
    public void ImplausibleReset_Boundary_PerWindowLength()
    {
        Assert.NotEqual(ClaudeMergeDecision.RejectedImplausibleReset, Merge(null, 10, T0 + H(5) + M(10), T0).Decision);
        Assert.Equal(ClaudeMergeDecision.RejectedImplausibleReset, Merge(null, 10, T0 + H(5) + M(10) + TimeSpan.FromSeconds(1), T0).Decision);
        // seven-day window allows a week
        Assert.NotEqual(ClaudeMergeDecision.RejectedImplausibleReset, Merge(null, 10, T0 + H(168), T0, ClaudeWindowKind.SevenDay).Decision);
        Assert.Equal(ClaudeMergeDecision.RejectedImplausibleReset, Merge(null, 10, T0 + H(169), T0, ClaudeWindowKind.SevenDay).Decision);
    }

    [Fact]
    public void SameEpochHigher_Advances_ChangedAtMoves()
    {
        ClaudeMergeResult r = Merge(Rec(40, R, T0), 41.5, R, T0 + M(7));
        Assert.Equal(ClaudeMergeDecision.Advanced, r.Decision);
        Assert.Equal(new ClaudeWindowRecord(41.5, R, T0 + M(7), T0 + M(7)), r.Record);
        Assert.True(r.ShouldPersist);
    }

    [Fact]
    public void SameEpochSamePercent_OnlyReceivedAtMoves_NeverChangedAt()
    {
        ClaudeMergeResult r = Merge(Rec(40, R, T0), 40, R, T0 + M(7));
        Assert.Equal(ClaudeMergeDecision.Unchanged, r.Decision);
        Assert.Equal(T0, r.Record!.ChangedAt);
        Assert.Equal(T0 + M(7), r.Record.ReceivedAt);
    }

    [Fact]
    public void EpochJitterWithinTolerance_IsTheSameEpoch()
    {
        Assert.Equal(ClaudeMergeDecision.Advanced, Merge(Rec(40, R, T0), 45, R + M(2), T0 + M(1)).Decision);
        Assert.Equal(ClaudeMergeDecision.Advanced, Merge(Rec(40, R, T0), 45, R - M(2), T0 + M(1)).Decision);
    }

    [Fact]
    public void NewerResetTime_StartsANewEpoch_EvenAtTheSamePercent_OrLower()
    {
        DateTimeOffset r2 = R + H(5);
        ClaudeMergeResult same = Merge(Rec(40, R, T0), 40, r2, T0 + H(3));
        Assert.Equal(ClaudeMergeDecision.NewEpoch, same.Decision);
        Assert.Equal(T0 + H(3), same.Record!.ChangedAt);                 // a new epoch IS new information
        ClaudeMergeResult lower = Merge(Rec(90, R, T0), 3, r2, T0 + H(3));
        Assert.Equal(ClaudeMergeDecision.NewEpoch, lower.Decision);
        Assert.Equal(3, lower.Record!.UsedPercent);                      // rollover legitimately lowers usage
    }

    [Fact]
    public void OlderResetTime_IsAStaleSessionPayload_Ignored()
    {
        ClaudeWindowRecord rec = Rec(40, R, T0);
        ClaudeMergeResult r = Merge(rec, 80, R - H(2), T0 + M(1));
        Assert.Equal(ClaudeMergeDecision.OlderEpochIgnored, r.Decision);
        Assert.Equal(40, r.Record!.UsedPercent);
        Assert.Equal(R, r.Record.ResetsAt);
        Assert.Equal(T0, r.Record.ChangedAt);
    }

    [Fact]
    public void IncomingWithoutResetTime_AssumesStoredEpoch_StoredWithoutReset_AdoptsIncoming()
    {
        ClaudeMergeResult keep = Merge(Rec(40, R, T0), 45, null, T0 + M(1));
        Assert.Equal(ClaudeMergeDecision.Advanced, keep.Decision);
        Assert.Equal(R, keep.Record!.ResetsAt);

        ClaudeMergeResult adopt = Merge(Rec(40, null, T0), 40, R, T0 + M(1));
        Assert.Equal(ClaudeMergeDecision.Unchanged, adopt.Decision);
        Assert.Equal(R, adopt.Record!.ResetsAt);
        Assert.True(adopt.ShouldPersist);                                // learned something: write immediately
    }

    // ---- stale-cache handling (§34.6) ----

    [Fact]
    public void LowerSameEpoch_IsIgnored_UntilTheSameLowerValueRepeatsThreeTimes()
    {
        ClaudeWindowRecord rec = Rec(60, R, T0);
        ClaudeMergeResult r1 = Merge(rec, 50, R, T0 + M(1));
        Assert.Equal(ClaudeMergeDecision.LowerIgnored, r1.Decision);
        Assert.Equal(60, r1.Record!.UsedPercent);
        Assert.Equal(T0, r1.Record.ChangedAt);                           // not new information
        ClaudeMergeResult r2 = Merge(r1.Record, 50, R, T0 + M(2));
        Assert.Equal(ClaudeMergeDecision.LowerIgnored, r2.Decision);
        Assert.Equal(2, r2.Record!.LowerStreak);
        ClaudeMergeResult r3 = Merge(r2.Record, 50, R, T0 + M(3));
        Assert.Equal(ClaudeMergeDecision.AdjustedDown, r3.Decision);
        Assert.Equal(50, r3.Record!.UsedPercent);
        Assert.Equal(T0 + M(3), r3.Record.ChangedAt);
        Assert.Equal(0, r3.Record.LowerStreak);
    }

    // ---- the lower-reading repeat rule: a conservative v1 heuristic with an explicit, named threshold ----

    [Fact]
    public void LowerRule_Threshold_IsTheNamedPolicySetting_DefaultThree()
    {
        Assert.Equal(3, FreshnessPolicy.DefaultLowerAcceptCount);            // pinned: changing the default is a deliberate, reviewed act
        Assert.Equal(FreshnessPolicy.DefaultLowerAcceptCount, Policy.LowerAcceptCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void LowerRule_AcceptsExactlyOnThePolicyThreshold_NotOnAHardCodedNumber(int threshold)
    {
        FreshnessPolicy p = (FreshnessPolicy.Default with { LowerAcceptCount = threshold }).Validated();
        ClaudeWindowRecord? rec = Rec(60, R, T0);
        for (int i = 1; i < threshold; i++)
        {
            ClaudeMergeResult r = ClaudeWindowMerger.Merge(Five, rec, new ClaudeIncomingWindow(50, R), T0 + M(i), p);
            Assert.Equal(ClaudeMergeDecision.LowerIgnored, r.Decision);        // reports 1 .. threshold-1 are ignored
            Assert.Equal(60, r.Record!.UsedPercent);
            rec = r.Record;
        }

        ClaudeMergeResult accepted = ClaudeWindowMerger.Merge(Five, rec, new ClaudeIncomingWindow(50, R), T0 + M(threshold), p);
        Assert.Equal(ClaudeMergeDecision.AdjustedDown, accepted.Decision);     // report number `threshold` is accepted
        Assert.Equal(50, accepted.Record!.UsedPercent);
    }

    [Fact]
    public void LowerRule_DifferingLowerValues_ResetTheCandidateSequence()
    {
        // 50, 50, then 45: the 45 starts a new sequence (it must NOT inherit the 50-streak) and needs its own full run.
        ClaudeWindowRecord? rec = Rec(60, R, T0);
        var decisions = new List<ClaudeMergeDecision>();
        int minute = 1;
        foreach (double v in new[] { 50.0, 50, 45, 45, 45 })
        {
            ClaudeMergeResult r = Merge(rec, v, R, T0 + M(minute++));
            decisions.Add(r.Decision);
            rec = r.Record;
        }

        Assert.Equal(
            [ClaudeMergeDecision.LowerIgnored, ClaudeMergeDecision.LowerIgnored, ClaudeMergeDecision.LowerIgnored,
             ClaudeMergeDecision.LowerIgnored, ClaudeMergeDecision.AdjustedDown],
            decisions);
        Assert.Equal(45, rec!.UsedPercent);
    }

    [Fact]
    public void LowerRule_NewResetsAtEpoch_BypassesTheRule_AndClearsTheSequence()
    {
        ClaudeWindowRecord? rec = Rec(60, R, T0);
        rec = Merge(rec, 50, R, T0 + M(1)).Record;
        rec = Merge(rec, 50, R, T0 + M(2)).Record;
        Assert.Equal(2, rec!.LowerStreak);                                       // one report away from acceptance

        // a later reset time starts a new epoch: a lower percentage is accepted IMMEDIATELY, no streak needed
        DateTimeOffset r2 = R + H(5);
        ClaudeMergeResult roll = Merge(rec, 5, r2, T0 + H(3) + M(1));
        Assert.Equal(ClaudeMergeDecision.NewEpoch, roll.Decision);
        Assert.Equal(5, roll.Record!.UsedPercent);
        Assert.Equal(0, roll.Record.LowerStreak);
        Assert.Null(roll.Record.LowerCandidate);
    }

    [Fact]
    public void LowerRule_AnOlderEpoch_NeverFeedsTheStreak()
    {
        ClaudeWindowRecord? rec = Rec(60, R, T0);
        for (int i = 1; i <= 10; i++)
        {
            ClaudeMergeResult r = Merge(rec, 10, R - H(2), T0 + M(i));          // stale session from the previous window
            Assert.Equal(ClaudeMergeDecision.OlderEpochIgnored, r.Decision);
            rec = r.Record;
        }

        Assert.Equal(60, rec!.UsedPercent);
        Assert.Equal(0, rec.LowerStreak);
    }

    [Fact]
    public void AlternatingSessions_StaleLowerNeverGetsAccepted()
    {
        // Session A (live) and session B (stale cache) report alternately; B must never drag the number down.
        ClaudeWindowRecord? rec = Merge(null, 60, R, T0).Record;
        for (int i = 1; i <= 50; i++)
        {
            DateTimeOffset now = T0 + M(i);
            rec = Merge(rec, 50, R, now).Record;             // B (stale)
            ClaudeMergeResult a = Merge(rec, 60 + i * 0.1, R, now);
            Assert.Equal(ClaudeMergeDecision.Advanced, a.Decision);
            rec = a.Record;
            Assert.Equal(0, rec!.LowerStreak);               // a higher report resets the streak
        }

        Assert.Equal(65, rec!.UsedPercent, 6);
    }

    [Fact]
    public void DifferentLowerValues_RestartTheStreak()
    {
        ClaudeWindowRecord? rec = Rec(60, R, T0);
        rec = Merge(rec, 50, R, T0 + M(1)).Record;
        rec = Merge(rec, 45, R, T0 + M(2)).Record;
        Assert.Equal(1, rec!.LowerStreak);
        Assert.Equal(45, rec.LowerCandidate);
        Assert.Equal(60, rec.UsedPercent);
    }

    [Fact]
    public void UnchangedReport_ResetsALowerStreak()
    {
        ClaudeWindowRecord? rec = Rec(60, R, T0);
        rec = Merge(rec, 50, R, T0 + M(1)).Record;
        ClaudeMergeResult r = Merge(rec, 60, R, T0 + M(2));
        Assert.Equal(ClaudeMergeDecision.Unchanged, r.Decision);
        Assert.Equal(0, r.Record!.LowerStreak);
        Assert.Null(r.Record.LowerCandidate);
        Assert.True(r.ShouldPersist);                                    // streak state changed => must be written
    }

    // ---- write coalescing ----

    [Theory]
    [InlineData(0, false)]
    [InlineData(30, false)]
    [InlineData(59.999, false)]
    [InlineData(60, true)]
    [InlineData(600, true)]
    public void UnchangedPayload_WritesAtMostOncePerCoalescingInterval(double secondsSinceReceived, bool expectWrite)
    {
        ClaudeWindowRecord rec = Rec(40, R, T0);
        ClaudeMergeResult r = Merge(rec, 40, R, T0 + TimeSpan.FromSeconds(secondsSinceReceived));
        Assert.Equal(expectWrite, r.ShouldPersist);
    }

    // ---- the central §34 scenarios, end to end through the freshness engine ----

    [Fact]
    public void BridgeKeepsRunning_DataNeverChanges_StateDecaysInsteadOfStayingFresh()
    {
        // Claude Code re-runs the hook every minute with the SAME cached numbers (e.g. refreshInterval).
        DateTimeOffset reset = T0 + H(5);
        ClaudeWindowRecord? rec = Merge(null, 40, reset, T0).Record;
        var states = new List<WindowState>();
        for (int minute = 1; minute <= 240; minute++)
        {
            DateTimeOffset now = T0 + M(minute);
            rec = Merge(rec, 40, reset, now).Record;
            Assert.Equal(now, rec!.ReceivedAt);                          // "the bridge ran just now" ...
            Assert.Equal(T0, rec.ChangedAt);                             // ... but nothing changed since T0
            states.Add(WindowDeriver.Derive(ClaudeObservations.ToObservation(Five, rec), now, Policy).State);
        }

        Assert.Equal(WindowState.Fresh, states[0]);                      // 1 min after the change
        Assert.Equal(WindowState.Fresh, states[8]);                      // 9 min
        Assert.Equal(WindowState.Quiet, states[9]);                      // 10 min: no longer fresh, despite 9 more hook runs
        Assert.Equal(WindowState.Quiet, states[118]);                    // 119 min
        Assert.Equal(WindowState.Sleeping, states[119]);                 // 120 min
        Assert.Equal(WindowState.Sleeping, states[239]);                 // 240 min, bridge STILL running every minute
    }

    [Fact]
    public void ChangingData_KeepsItFresh_ThenResetExpiresIt_ThenNewEpochRefillsIt()
    {
        DateTimeOffset reset = T0 + H(5);
        ClaudeWindowRecord? rec = Merge(null, 10, reset, T0).Record;
        rec = Merge(rec, 20, reset, T0 + H(1)).Record;
        WindowView live = View(rec!, T0 + H(1) + M(5));
        Assert.Equal(WindowState.Fresh, live.State);
        Assert.Equal(20, live.DisplayedUsedPercent);

        // time passes with no payloads; the reset instant arrives
        WindowView before = View(rec!, reset - TimeSpan.FromTicks(1));
        Assert.NotEqual(WindowState.Elapsed, before.State);
        WindowView at = View(rec!, reset);
        Assert.Equal(WindowState.Elapsed, at.State);
        Assert.Null(at.DisplayedUsedPercent);                            // not 0%, not 20%
        Assert.Equal(20, at.LastKnownUsedPercent);

        // Claude Code reports again in the new window
        DateTimeOffset later = reset + M(7);
        ClaudeMergeResult roll = Merge(rec, 20, reset + H(5), later);    // even an identical percent is a new epoch
        Assert.Equal(ClaudeMergeDecision.NewEpoch, roll.Decision);
        WindowView after = View(roll.Record!, later);
        Assert.Equal(WindowState.Fresh, after.State);
        Assert.Equal(20, after.DisplayedUsedPercent);
    }

    [Fact]
    public void ElapsedWindow_StaleSessionRepeatsOldEpochPayload_StaysElapsed()
    {
        DateTimeOffset reset = T0 + H(1);
        ClaudeWindowRecord rec = Rec(80, reset, T0);
        DateTimeOffset now = reset + M(30);
        // another terminal that has not refreshed still emits the old epoch (reset in the past) => rejected
        ClaudeMergeResult r = Merge(rec, 80, reset, now);
        Assert.Equal(ClaudeMergeDecision.RejectedResetInPast, r.Decision);
        Assert.Equal(WindowState.Elapsed, View(r.Record!, now).State);
    }

    [Fact]
    public void ProviderInput_OnlyOneWindowReported_OtherIsNotReported_NeverZero()
    {
        ProviderInput input = ClaudeObservations.ToProviderInput(Rec(30, T0 + H(3), T0), null);
        ProviderView v = ProviderDeriver.Derive(input, T0 + M(1), Policy);
        WindowView weekly = v.Windows.Single(w => w.Key == WindowKey.ClaudeSevenDay);
        Assert.Equal(WindowState.NoData, weekly.State);
        Assert.Null(weekly.DisplayedUsedPercent);
    }

    [Fact]
    public void PersistedRecord_HoldsNoIdentityOrPayloadFields()
    {
        // Structural privacy guard: the record type can only carry the §34 schema.
        string[] props = typeof(ClaudeWindowRecord).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["ChangedAt", "LowerCandidate", "LowerStreak", "ReceivedAt", "ResetsAt", "UsedPercent"], props);
    }

    private static WindowView View(ClaudeWindowRecord rec, DateTimeOffset now) =>
        WindowDeriver.Derive(ClaudeObservations.ToObservation(Five, rec), now, Policy);
}
