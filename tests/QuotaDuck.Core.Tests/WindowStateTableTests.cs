using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

/// <summary>Encodes the freshness/reset state table of <see cref="WindowDeriver"/> rule by rule (docs/core/FRESHNESS-STATE-TABLE.md).</summary>
public class WindowStateTableTests
{
    private const ObservationSource Push = ObservationSource.PushReported;
    private const ObservationSource Polled = ObservationSource.PolledRead;
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    public static TheoryData<ObservationSource, double, WindowState> AgeBoundaries => new()
    {
        // Push: fresh < 10 min, quiet < 2 h (exclusive upper bounds => new state begins exactly at the bound)
        { Push, 0, WindowState.Fresh },
        { Push, 9.99, WindowState.Fresh },
        { Push, 10, WindowState.Quiet },
        { Push, 119.99, WindowState.Quiet },
        { Push, 120, WindowState.Sleeping },
        { Push, 60 * 30, WindowState.Sleeping },
        // Polled: fresh < 20 min, quiet < 2 h
        { Polled, 0, WindowState.Fresh },
        { Polled, 19.99, WindowState.Fresh },
        { Polled, 20, WindowState.Quiet },
        { Polled, 119.99, WindowState.Quiet },
        { Polled, 120, WindowState.Sleeping },
    };

    [Theory]
    [MemberData(nameof(AgeBoundaries))]
    public void Rule3to5_AgeBoundaries(ObservationSource source, double ageMinutes, WindowState expected)
    {
        WindowView v = WindowDeriver.Derive(Obs(source, evidenceAge: M(ageMinutes), resetsIn: H(40), duration: H(48)), T0, Policy);
        Assert.Equal(expected, v.State);
    }

    [Fact]
    public void Rule1_NoObservation_NoDataShowsNothing()
    {
        WindowView v = WindowDeriver.NoData(WindowKey.ClaudeSevenDay, WindowLabels.ClaudeSevenDay);
        Assert.Equal(WindowState.NoData, v.State);
        Assert.Null(v.DisplayedUsedPercent);
        Assert.Null(v.DisplayedRemainingPercent);
        Assert.Null(v.LastKnownUsedPercent);
        Assert.Null(v.ResetsIn);
        Assert.Null(v.NextTransitionAt);
        Assert.False(v.LimitReached);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    public void Rule1_InvalidPercent_IsNoDataWithAnomaly_NeverShown(double bad)
    {
        WindowView v = WindowDeriver.Derive(Obs(pct: bad), T0, Policy);
        Assert.Equal(WindowState.NoData, v.State);
        Assert.True(v.Anomalies.HasFlag(WindowAnomalies.InvalidObservation));
        Assert.Null(v.DisplayedUsedPercent);
        Assert.Null(v.LastKnownUsedPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(23.5)]
    public void ValidPercentEdges_AreShownExactly_ZeroFromProviderIsNotTheSameAsUnknown(double pct)
    {
        WindowView v = WindowDeriver.Derive(Obs(pct: pct), T0, Policy);
        Assert.Equal(WindowState.Fresh, v.State);
        Assert.Equal(pct, v.DisplayedUsedPercent);
        Assert.Equal(100 - pct, v.DisplayedRemainingPercent);
    }

    [Fact]
    public void Rule2_ResetInstant_ElapsedStartsExactlyAtResetsAt()
    {
        Assert.NotEqual(WindowState.Elapsed, WindowDeriver.Derive(Obs(resetsIn: Tick), T0, Policy).State);
        Assert.Equal(WindowState.Elapsed, WindowDeriver.Derive(Obs(resetsIn: TimeSpan.Zero), T0, Policy).State);
        Assert.Equal(WindowState.Elapsed, WindowDeriver.Derive(Obs(resetsIn: -Tick), T0, Policy).State);
    }

    [Theory]
    [InlineData(0)]      // would be Fresh by age
    [InlineData(30)]     // would be Quiet
    [InlineData(600)]    // would be Sleeping
    public void Rule2_ElapsedOverridesEveryAgeRule_AndHidesTheObsoleteNumber(double ageMinutes)
    {
        WindowView v = WindowDeriver.Derive(Obs(pct: 62, evidenceAge: M(ageMinutes), resetsIn: -M(1)), T0, Policy);
        Assert.Equal(WindowState.Elapsed, v.State);
        Assert.Null(v.DisplayedUsedPercent);           // never the old value
        Assert.Null(v.DisplayedRemainingPercent);      // never "0%" or "100% left" either
        Assert.Equal(62, v.LastKnownUsedPercent);      // kept for the expanded "was 62% at ..." view only
        Assert.Equal(M(1), v.ElapsedSince);
        Assert.Null(v.ResetsIn);
        Assert.Null(v.NextTransitionAt);
    }

    [Fact]
    public void ElapsedWindowStaysElapsed_ForeverUntilNewData()
    {
        WindowObservation o = Obs(resetsIn: M(5));
        foreach (double minutes in new[] { 5.0, 6, 60, 600, 60 * 24 * 30 })
        {
            Assert.Equal(WindowState.Elapsed, WindowDeriver.Derive(o, T0 + M(minutes), Policy).State);
        }
    }

    [Fact]
    public void MissingResetTime_CannotExpireByTime_OnlyByAge()
    {
        WindowView fresh = WindowDeriver.Derive(Obs(hasReset: false), T0, Policy);
        Assert.Equal(WindowState.Fresh, fresh.State);
        Assert.True(fresh.Anomalies.HasFlag(WindowAnomalies.MissingResetTime));
        Assert.Null(fresh.ResetsIn);
        Assert.Equal(T0 + M(10), fresh.NextTransitionAt);

        WindowView old = WindowDeriver.Derive(Obs(hasReset: false, evidenceAge: H(30)), T0, Policy);
        Assert.Equal(WindowState.Sleeping, old.State);
        Assert.Null(old.NextTransitionAt);
    }

    [Fact]
    public void ClockSkew_SmallFutureEvidenceIsTolerated_LargeOneIsNeverFresh()
    {
        WindowView small = WindowDeriver.Derive(Obs(evidenceAge: -M(1)), T0, Policy);
        Assert.Equal(WindowState.Fresh, small.State);
        Assert.Equal(WindowAnomalies.None, small.Anomalies);
        Assert.Equal(TimeSpan.Zero, small.EvidenceAge);

        WindowView atLimit = WindowDeriver.Derive(Obs(evidenceAge: -M(2)), T0, Policy);   // exactly the tolerance: still benign
        Assert.Equal(WindowState.Fresh, atLimit.State);

        WindowView big = WindowDeriver.Derive(Obs(evidenceAge: -M(3)), T0, Policy);
        Assert.Equal(WindowState.Quiet, big.State);
        Assert.True(big.Anomalies.HasFlag(WindowAnomalies.ClockSkew));
        Assert.Equal(T0 + M(3) + H(2), big.NextTransitionAt);
    }

    [Fact]
    public void ImplausibleReset_BoundaryAndCap()
    {
        TimeSpan edge = H(5) + M(10);   // window length + tolerance: still plausible (strictly greater is implausible)
        WindowView ok = WindowDeriver.Derive(Obs(resetsIn: edge, duration: H(5)), T0, Policy);
        Assert.Equal(WindowState.Fresh, ok.State);
        Assert.Equal(WindowAnomalies.None, ok.Anomalies);

        WindowView bad = WindowDeriver.Derive(Obs(resetsIn: edge + TimeSpan.FromSeconds(1), duration: H(5)), T0, Policy);
        Assert.True(bad.Anomalies.HasFlag(WindowAnomalies.ImplausibleReset));
        Assert.Equal(WindowState.Quiet, bad.State);          // never Fresh
        Assert.NotNull(bad.DisplayedUsedPercent);            // but still shown, with its age
    }

    [Fact]
    public void UnknownDuration_UsesMaxPlausibleWindow()
    {
        WindowObservation o = Obs(resetsIn: H(24 * 9)) with { Duration = null };
        Assert.True(WindowDeriver.Derive(o, T0, Policy).Anomalies.HasFlag(WindowAnomalies.ImplausibleReset));
        WindowObservation fine = Obs(resetsIn: H(24 * 7)) with { Duration = null };
        Assert.Equal(WindowAnomalies.None, WindowDeriver.Derive(fine, T0, Policy).Anomalies);
    }

    [Fact]
    public void BridgeRunningAgain_DoesNotMakeAnythingFresh()
    {
        // §34: ReceivedAt is "now" (hook just ran) but the data last CHANGED 3 hours ago.
        WindowView v = WindowDeriver.Derive(Obs(Push, evidenceAge: H(3), receivedAge: TimeSpan.Zero, resetsIn: H(2)), T0, Policy);
        Assert.Equal(WindowState.Sleeping, v.State);
        Assert.Equal(H(3), v.EvidenceAge);
        Assert.Equal(TimeSpan.Zero, v.ReceivedAge);   // surfaced separately as "Claude Code last ran just now"
    }

    [Fact]
    public void PolledRead_EveryReadIsEvidence_ReceivedEqualsEvidence()
    {
        WindowView v = WindowDeriver.Derive(Polled_(age: M(1)), T0, Policy);
        Assert.Equal(WindowState.Fresh, v.State);
        Assert.Equal(v.EvidenceAge, v.ReceivedAge);

        static WindowObservation Polled_(TimeSpan age) => Fx.Polled(40, age);
    }

    [Fact]
    public void NonUtcInputs_AreNormalised()
    {
        var plus7 = TimeSpan.FromHours(7);
        WindowObservation o = Obs() with
        {
            EvidenceAt = T0.ToOffset(plus7) - M(30),
            ReceivedAt = T0.ToOffset(plus7),
            ResetsAt = (T0 + H(1)).ToOffset(plus7),
        };
        WindowView v = WindowDeriver.Derive(o, T0.ToOffset(-plus7), Policy);
        Assert.Equal(WindowState.Quiet, v.State);
        Assert.Equal(M(30), v.EvidenceAge);
        Assert.Equal(H(1), v.ResetsIn);
        Assert.Equal(TimeSpan.Zero, v.ResetsAt!.Value.Offset);
    }

    [Fact]
    public void LimitReached_OnlyReportedWhileTheNumberIsTruthful()
    {
        Assert.True(WindowDeriver.Derive(Obs(limitReached: true), T0, Policy).LimitReached);
        Assert.False(WindowDeriver.Derive(Obs(limitReached: true, resetsIn: -M(1)), T0, Policy).LimitReached);   // elapsed
        Assert.False(WindowDeriver.Derive(Obs(limitReached: true, pct: double.NaN), T0, Policy).LimitReached);  // invalid
    }

    [Fact]
    public void Derive_IsDeterministic()
    {
        WindowObservation o = Obs(evidenceAge: M(45));
        Assert.Equal(WindowDeriver.Derive(o, T0, Policy), WindowDeriver.Derive(o, T0, Policy));
    }
}
