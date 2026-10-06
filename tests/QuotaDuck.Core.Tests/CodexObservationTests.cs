using QuotaDuck.Core.Codex;
using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

public class CodexObservationTests
{
    [Fact]
    public void TwoWindows_BecomePolledObservations_EvidenceIsReadTime()
    {
        CodexObservationSet set = CodexObservations.FromRead(
            [new CodexBucket("synthetic", null, Reading(30, 300), Reading(10, 10_080, T0 + H(100)), false)], T0);
        Assert.Equal(0, set.SkippedWindows);
        Assert.Equal(["synthetic:primary", "synthetic:secondary"], set.Windows.Select(w => w.Key.Id));
        Assert.All(set.Windows, w =>
        {
            Assert.Equal(ObservationSource.PolledRead, w.Source);
            Assert.Equal(T0, w.EvidenceAt);
            Assert.Equal(T0, w.ReceivedAt);
            Assert.Equal("synthetic", w.GroupName);
        });
        Assert.Equal("5-hour", set.Windows[0].Label.Text);
        Assert.Equal("Weekly", set.Windows[1].Label.Text);
        Assert.All(set.Windows, w => Assert.Equal(LabelOrigin.DerivedFromDuration, w.Label.Origin));
    }

    [Fact]
    public void UnknownOrMissingDuration_GetsPositionalLabels_NoInventedMeaning()
    {
        CodexObservationSet set = CodexObservations.FromRead(
            [new CodexBucket("x", "Synthetic Bucket", Reading(1, 123), Reading(2, null), false)], T0);
        Assert.Equal(("Primary", LabelOrigin.Positional), (set.Windows[0].Label.Text, set.Windows[0].Label.Origin));
        Assert.Equal(("Secondary", LabelOrigin.Positional), (set.Windows[1].Label.Text, set.Windows[1].Label.Origin));
        Assert.Equal("Synthetic Bucket", set.Windows[0].GroupName);          // provider-supplied name preferred over the id
    }

    [Fact]
    public void NonPositiveDuration_IsTreatedAsUnknown()
    {
        CodexObservationSet set = CodexObservations.FromRead(
            [new CodexBucket("x", null, new CodexWindowReading(5, TimeSpan.Zero, T0 + H(1)), new CodexWindowReading(5, TimeSpan.FromMinutes(-5), T0 + H(1)), false)], T0);
        Assert.All(set.Windows, w =>
        {
            Assert.Null(w.Duration);
            Assert.Equal(LabelOrigin.Positional, w.Label.Origin);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("name:colon")]
    [InlineData("tab\tchar")]
    [InlineData("unicodé")]
    [InlineData("123456789012345678901234567890123456789012345678901")]   // 51 chars
    public void UnsafeLimitIds_AreSkippedAndCounted_NeverUsedAsKeys(string? id)
    {
        CodexObservationSet set = CodexObservations.FromRead([new CodexBucket(id, null, Reading(10), Reading(10), false)], T0);
        Assert.Empty(set.Windows);
        Assert.Equal(2, set.SkippedWindows);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("codex_other-2.v1")]
    [InlineData("123456789012345678901234567890123456789012345678")]      // 48 chars: allowed maximum
    public void SafeLimitIds_AreAccepted(string id)
    {
        Assert.Equal(2, CodexObservations.FromRead([new CodexBucket(id, null, Reading(10), Reading(10), false)], T0).Windows.Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(100.5)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPercent_SkipsOnlyThatWindow(double bad)
    {
        CodexObservationSet set = CodexObservations.FromRead([new CodexBucket("c", null, Reading(bad), Reading(20), false)], T0);
        Assert.Equal(["c:secondary"], set.Windows.Select(w => w.Key.Id));
        Assert.Equal(1, set.SkippedWindows);
    }

    [Fact]
    public void AbsentWindows_AreNotSkips_JustAbsent()
    {
        CodexObservationSet set = CodexObservations.FromRead([new CodexBucket("c", null, Reading(20), null, false)], T0);
        Assert.Single(set.Windows);
        Assert.Equal(0, set.SkippedWindows);
    }

    [Fact]
    public void DuplicateKeys_LegacyAndByIdView_LastWins()
    {
        CodexObservationSet set = CodexObservations.FromRead(
            [new CodexBucket("codex", null, Reading(10), null, false), new CodexBucket("codex", null, Reading(12), null, true)], T0);
        WindowObservation only = Assert.Single(set.Windows);
        Assert.Equal(12, only.UsedPercent);
        Assert.True(only.LimitReached);
    }

    [Fact]
    public void ReadTime_NonUtcIsNormalised()
    {
        CodexObservationSet set = CodexObservations.FromRead([new CodexBucket("c", null, Reading(1), null, false)], T0.ToOffset(TimeSpan.FromHours(9)));
        Assert.Equal(TimeSpan.Zero, set.Windows[0].EvidenceAt.Offset);
        Assert.Equal(T0, set.Windows[0].EvidenceAt);
    }

    [Fact]
    public void EndToEnd_FreshUntilTwoPollIntervals_ThenQuiet_ThenElapsedAtReset()
    {
        CodexObservationSet set = CodexObservations.FromRead([new CodexBucket("c", null, Reading(30, 300, T0 + H(3)), null, false)], T0);
        ProviderInput input = CodexObservations.ToProviderInput(set, new ProviderAttempt(T0, null));
        Assert.Equal(ProviderRollup.Fresh, ProviderDeriver.Derive(input, T0 + M(19), Policy).Rollup);
        Assert.Equal(ProviderRollup.Quiet, ProviderDeriver.Derive(input, T0 + M(20), Policy).Rollup);
        ProviderView atReset = ProviderDeriver.Derive(input, T0 + H(3), Policy);
        Assert.Equal(ProviderRollup.Elapsed, atReset.Rollup);
        Assert.Equal(ProviderReason.AwaitingCodexRead, atReset.Reason);
        Assert.Null(atReset.Windows.Single().DisplayedUsedPercent);
    }

    [Fact]
    public void PollIntervalPolicy_ShiftsTheFreshBound()
    {
        FreshnessPolicy p = FreshnessPolicy.ForCodexPollInterval(TimeSpan.FromMinutes(5));
        CodexObservationSet set = CodexObservations.FromRead([new CodexBucket("c", null, Reading(30), null, false)], T0);
        ProviderInput input = CodexObservations.ToProviderInput(set, null);
        Assert.Equal(ProviderRollup.Fresh, ProviderDeriver.Derive(input, T0 + M(9), p).Rollup);
        Assert.Equal(ProviderRollup.Quiet, ProviderDeriver.Derive(input, T0 + M(10), p).Rollup);
    }

    [Fact]
    public void CodexModels_CannotCarryAccountPlanCreditOrUpsellData()
    {
        // Structural privacy guard (Phase 1b §31/§35 N2): the bucket/reading types contain only allow-listed fields.
        Assert.Equal(["LimitId", "LimitName", "LimitReached", "Primary", "Secondary"],
            typeof(CodexBucket).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["Duration", "ResetsAt", "UsedPercent"],
            typeof(CodexWindowReading).GetProperties().Select(p => p.Name).Order().ToArray());
    }
}
