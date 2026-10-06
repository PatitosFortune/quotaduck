using QuotaDuck.Codex;
using QuotaDuck.Core;

namespace QuotaDuck.Coordinator.Tests;

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int ms = 3000)
    {
        for (int waited = 0; waited < ms && !condition(); waited += 2)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }
}

/// <summary>The Codex pull side of the coordinator against a fake reader, fake clock and fake timers.</summary>
public class CodexCoordinatorTests
{
    private static async Task CompleteOk(Rig r, double used = 40, DateTimeOffset? reset5h = null, DateTimeOffset? resetWk = null)
    {
        int calls = r.Codex.Calls;
        DateTimeOffset at = r.Clock.Now;
        r.Codex.Complete(Fx.Ok(at, used, reset5h, resetWk));
        await Wait.Until(() => r.Snap.Codex.Activity == ProviderActivity.Idle);
        Assert.Equal(calls, r.Codex.Calls);
    }

    private static async Task CompleteFail(Rig r, CodexReadFailure f = CodexReadFailure.Timeout)
    {
        r.Codex.Complete(Fx.Fail(r.Clock.Now, f));
        await Wait.Until(() => r.Snap.Codex.Activity == ProviderActivity.Idle);
    }

    /// <summary>A manual refresh that wrongly starts (and so waits for) a read must fail the test within seconds, never hang it.</summary>
    private static Task<CodexRefreshResult> Refresh(Rig r) => r.C.RefreshCodexAsync().WaitAsync(TimeSpan.FromSeconds(3));

    // ---------------- initial / visible / hidden ----------------

    [Fact]
    public async Task NotVisible_NoReadHappens_AndCodexIsNoData()
    {
        await using var r = new Rig();
        r.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, r.Codex.Calls);
        Assert.Equal(ProviderIssue.NoData, r.Snap.Codex.Issue);
        Assert.DoesNotContain(r.Snap.Codex.View.Windows, w => w.DisplayedUsedPercent is not null);
    }

    [Fact]
    public async Task BecomingVisible_StartsOneInitialRead_ThatShowsReading_ThenTheWindows()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        Assert.Equal(1, r.Codex.Calls);
        Assert.Equal(ProviderActivity.Reading, r.Snap.Codex.Activity);
        Assert.False(r.Snap.CodexRefreshAllowed);

        await CompleteOk(r);
        AppSnapshot s = r.Snap;
        Assert.Equal(2, s.Codex.View.Windows.Count(w => w.DisplayedUsedPercent is not null));
        Assert.Equal(Fx.T0, s.Codex.LastObservationAt);
        Assert.Equal(ProviderIssue.None, s.Codex.Issue);
        Assert.Equal(new ScheduledCodexRead(Fx.T0.AddMinutes(10), CodexTrigger.VisiblePoll), r.C.Current!.CodexNextRead);
    }

    [Fact]
    public async Task VisibleRefresh_HappensExactlyTenMinutesAfterCompletion()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r);
        r.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, r.Codex.Calls);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, r.Codex.Calls);                                              // exactly at +10:00.000
        await CompleteOk(r);
        r.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(3, r.Codex.Calls);
    }

    [Fact]
    public async Task HiddenUi_SuppressesOrdinaryPolling_ForHours()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r);
        r.C.SetUiVisible(false);
        r.Advance(TimeSpan.FromHours(2));                                           // no reset inside the window (reset is +3 h)
        Assert.Equal(1, r.Codex.Calls);
        r.C.SetUiVisible(true);                                                      // visible again, long overdue: reads once, now
        Assert.Equal(2, r.Codex.Calls);
    }

    [Fact]
    public async Task ResetReadWhileHidden_HappensOnce_AtResetPlusThirtySeconds()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r, reset5h: Fx.T0.AddHours(3), resetWk: Fx.T0.AddDays(5));
        r.C.SetUiVisible(false);

        r.Advance(new TimeSpan(3, 0, 0) + TimeSpan.FromSeconds(29.999));
        Assert.Equal(1, r.Codex.Calls);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, r.Codex.Calls);                                              // the one hidden read, at reset + 30 s exactly
        await CompleteOk(r, reset5h: Fx.T0.AddHours(8), resetWk: Fx.T0.AddDays(5));
        r.Advance(TimeSpan.FromHours(4));                                            // hidden: nothing until the next reset
        Assert.Equal(2, r.Codex.Calls);
    }

    [Fact]
    public async Task FailedResetReadWhileHidden_IsNotRetried_UntilTheUiIsVisible()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r, reset5h: Fx.T0.AddHours(1), resetWk: Fx.T0.AddDays(5));
        r.C.SetUiVisible(false);
        r.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(30));
        Assert.Equal(2, r.Codex.Calls);
        await CompleteFail(r);
        r.Advance(TimeSpan.FromHours(5));
        Assert.Equal(2, r.Codex.Calls);
        r.C.SetUiVisible(true);
        Assert.Equal(3, r.Codex.Calls);                                              // retry only now
    }

    [Fact]
    public async Task ResetWhileAppSuspended_ReadsOnceOnResume_NotOncePerMissedTimer()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r, reset5h: Fx.T0.AddHours(3), resetWk: Fx.T0.AddHours(4));
        r.C.SetUiVisible(false);
        r.Scheduler.Jump(Fx.T0.AddHours(30));                                       // slept through both resets and many polls
        r.C.NotifyEnvironmentChanged();
        r.C.NotifyEnvironmentChanged();
        r.C.NotifyEnvironmentChanged();
        Assert.Equal(2, r.Codex.Calls);
        await CompleteOk(r, reset5h: Fx.T0.AddHours(33), resetWk: Fx.T0.AddDays(6));
        r.C.NotifyEnvironmentChanged();
        r.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, r.Codex.Calls);
    }

    [Fact]
    public async Task ResetOccurringDuringAnInFlightRead_IsFollowedByOneMoreRead()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r, reset5h: Fx.T0.AddMinutes(11), resetWk: Fx.T0.AddDays(5));
        r.Advance(TimeSpan.FromMinutes(10));                                          // poll read starts at +10:00 and stays in flight
        Assert.Equal(2, r.Codex.Calls);
        r.Advance(TimeSpan.FromMinutes(2));                                           // reset (+11:00) and its read target (+11:30) pass meanwhile
        Assert.Equal(2, r.Codex.Calls);                                              // never a second concurrent read
        Assert.Equal(1, r.Codex.MaxConcurrent);
        r.Codex.Complete(Fx.Ok(r.Clock.Now, 40, Fx.T0.AddMinutes(11), Fx.T0.AddDays(5)));   // the in-flight read still reports the OLD reset
        await Wait.Until(() => r.Codex.Calls == 3);
        Assert.Equal(3, r.Codex.Calls);                                              // follow-up: the post-reset read that the in-flight one could not be
        Assert.Equal(1, r.Codex.MaxConcurrent);
    }

    // ---------------- manual refresh ----------------

    [Fact]
    public async Task ManualRefresh_Works_ThenIsRefusedInsideTheCooldown_AndAllowedExactlyAtSixtySeconds()
    {
        await using var r = new Rig();
        Task<CodexRefreshResult> first = r.C.RefreshCodexAsync();
        Assert.Equal(1, r.Codex.Calls);
        await CompleteOk(r);
        Assert.Equal(CodexRefreshStatus.Succeeded, (await first).Status);

        r.Advance(TimeSpan.FromSeconds(59.999));
        Assert.False(r.Snap.CodexRefreshAllowed);
        CodexRefreshResult denied = await Refresh(r);
        Assert.Equal(CodexRefreshStatus.CooldownActive, denied.Status);
        Assert.Equal(Fx.T0.AddSeconds(60), denied.NextAllowedAt);
        Assert.Equal(1, r.Codex.Calls);                                              // nothing was started

        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(r.Snap.CodexRefreshAllowed);
        Assert.Contains(r.Published, s => s.CodexRefreshAllowed && s.At == Fx.T0.AddSeconds(60));   // the button re-enables at that instant (published)
        Task<CodexRefreshResult> second = r.C.RefreshCodexAsync();
        Assert.Equal(2, r.Codex.Calls);
        await CompleteOk(r);
        Assert.Equal(CodexRefreshStatus.Succeeded, (await second).Status);
    }

    [Fact]
    public async Task ManualRefresh_CooldownCountsFailedReadsToo()
    {
        await using var r = new Rig();
        Task<CodexRefreshResult> t = r.C.RefreshCodexAsync();
        await CompleteFail(r);
        Assert.Equal(CodexRefreshStatus.Failed, (await t).Status);
        Assert.Equal(CodexRefreshStatus.CooldownActive, (await Refresh(r)).Status);
        Assert.Equal(1, r.Codex.Calls);
    }

    [Fact]
    public async Task TwoSimultaneousManualRefreshes_ShareOneRead()
    {
        await using var r = new Rig();
        Task<CodexRefreshResult> a = r.C.RefreshCodexAsync();
        Task<CodexRefreshResult> b = r.C.RefreshCodexAsync();
        Assert.Equal(1, r.Codex.Calls);
        await CompleteOk(r);
        CodexRefreshResult[] results = await Task.WhenAll(a, b);
        Assert.All(results, x => Assert.Equal(CodexRefreshStatus.Succeeded, x.Status));
        Assert.Equal(1, results.Count(x => x.JoinedInFlight));
        Assert.Equal(1, r.Codex.Calls);
        Assert.Equal(1, r.Codex.MaxConcurrent);
    }

    [Fact]
    public async Task ManualRefresh_RacingTheVisibleTrigger_AndTheTimer_StillSingleFlight()
    {
        await using var r = new Rig();
        Task<CodexRefreshResult> manual = r.C.RefreshCodexAsync();
        r.C.SetUiVisible(true);                                                      // would start the initial read
        r.Advance(TimeSpan.FromMinutes(30));                                         // timers fire while in flight
        r.C.NotifyEnvironmentChanged();
        Assert.Equal(1, r.Codex.Calls);
        Assert.Equal(1, r.Codex.MaxConcurrent);
        await CompleteOk(r);
        Assert.Equal(CodexRefreshStatus.Succeeded, (await manual).Status);
    }

    [Fact]
    public async Task ManualRefresh_IsAllowedDuringBackoff_ButOnlyOutsideTheCooldown()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteFail(r);                                                       // backoff: next scheduled retry in 60 s
        r.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CodexRefreshStatus.CooldownActive, (await Refresh(r)).Status);
        r.Scheduler.Jump(r.Clock.Now);                                               // (no-op) keep the explicit clock handling visible
        r.Advance(TimeSpan.FromSeconds(30));                                         // retry fires at exactly +60 s
        Assert.Equal(2, r.Codex.Calls);
    }

    [Fact]
    public async Task ManualRefresh_WhenCodexDisabled_OrBeforeStart_OrAfterDispose()
    {
        await using var disabled = new Rig(new CoordinatorOptions { CodexEnabled = false });
        Assert.Equal(CodexRefreshStatus.Disabled, (await Refresh(disabled)).Status);
        Assert.Equal(ProviderIssue.NotConfigured, disabled.Snap.Codex.Issue);
        Assert.Equal(ProviderRollup.NotConfigured, disabled.Snap.Codex.View.Rollup);
        disabled.C.SetUiVisible(true);
        disabled.Advance(TimeSpan.FromHours(5));
        Assert.Equal(0, disabled.Codex.Calls);

        await using var notStarted = new Rig(start: false);
        Assert.Equal(CodexRefreshStatus.ShuttingDown, (await Refresh(notStarted)).Status);

        var disposed = new Rig();
        await disposed.DisposeAsync();
        Assert.Equal(CodexRefreshStatus.ShuttingDown, (await Refresh(disposed)).Status);
        Assert.Equal(0, disposed.Codex.Calls);
    }

    // ---------------- failure, backoff, recovery ----------------

    [Fact]
    public async Task Failures_BackOffExponentially_ThenRecoveryReturnsToTheTenMinuteCycle()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);                                                      // read 1 at T0
        await CompleteFail(r);
        r.Advance(TimeSpan.FromSeconds(59.999));
        Assert.Equal(1, r.Codex.Calls);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, r.Codex.Calls);                                              // retry after 60 s
        await CompleteFail(r);
        r.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(2, r.Codex.Calls);
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, r.Codex.Calls);                                              // then 120 s
        await CompleteFail(r);
        r.Advance(TimeSpan.FromSeconds(240));
        Assert.Equal(4, r.Codex.Calls);                                              // then 240 s
        await CompleteOk(r);
        Assert.Equal(ProviderIssue.None, r.Snap.Codex.Issue);
        r.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(4, r.Codex.Calls);
        r.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, r.Codex.Calls);                                              // back to 10 minutes
        await CompleteFail(r);
        r.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(6, r.Codex.Calls);                                              // the failure streak restarted at 60 s
    }

    [Fact]
    public async Task RepeatedFailures_SaturateAtTheCap()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        foreach (int expected in new[] { 60, 120, 240, 480, 900, 900, 900 })
        {
            int calls = r.Codex.Calls;
            await CompleteFail(r);
            r.Advance(TimeSpan.FromSeconds(expected - 1));
            Assert.Equal(calls, r.Codex.Calls);
            r.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(calls + 1, r.Codex.Calls);
        }
    }

    [Fact]
    public async Task FailureAfterASuccess_NeverRefreshesTheObservation_NorHidesIt()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r, used: 55);
        AppSnapshot good = r.Snap;
        DateTimeOffset firstRead = good.Codex.LastObservationAt!.Value;

        r.Advance(TimeSpan.FromMinutes(10));                                         // poll
        await CompleteFail(r, CodexReadFailure.ProcessExited);
        AppSnapshot after = r.Snap;
        Assert.Equal(firstRead, after.Codex.LastObservationAt);                       // success time unchanged by the failed attempt
        Assert.Equal(ProviderIssue.TemporaryProviderFailure, after.Codex.Issue);
        Assert.Equal(ProviderReason.LastReadFailed, after.Codex.View.Reason);
        WindowView w = after.Codex.View.Windows.First(x => x.DisplayedUsedPercent is not null);
        Assert.Equal(55, w.DisplayedUsedPercent);                                     // older data still shown, honestly aged
        Assert.Equal(TimeSpan.FromMinutes(10), w.EvidenceAge);                        // 10 min old: the failure did not make it younger
        Assert.True(after.Codex.View.FreshestEvidenceAge >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task StaleSuccess_ThenFailure_AgesIntoSleeping_NeverBackToFresh()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r);
        r.C.SetUiVisible(false);
        r.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));                  // hidden, aged
        Assert.Equal(WindowState.Sleeping, r.Snap.Codex.View.Windows.First(x => x.LastKnownUsedPercent is not null).State);
        r.C.SetUiVisible(true);
        await CompleteFail(r);
        Assert.Equal(WindowState.Sleeping, r.Snap.Codex.View.Windows.First(x => x.LastKnownUsedPercent is not null).State);
    }

    [Theory]
    [InlineData(CodexReadFailure.NotFound, ProviderIssue.CodexNotInstalled)]
    [InlineData(CodexReadFailure.ValidationFailed, ProviderIssue.CodexNotTrusted)]
    [InlineData(CodexReadFailure.StartFailed, ProviderIssue.ProviderUnavailable)]
    [InlineData(CodexReadFailure.Timeout, ProviderIssue.TemporaryProviderFailure)]
    [InlineData(CodexReadFailure.ProcessExited, ProviderIssue.TemporaryProviderFailure)]
    [InlineData(CodexReadFailure.Rejected, ProviderIssue.CodexRequestRejected)]
    [InlineData(CodexReadFailure.Malformed, ProviderIssue.CodexProtocolUnsupported)]
    [InlineData(CodexReadFailure.ProtocolViolation, ProviderIssue.CodexProtocolUnsupported)]
    [InlineData(CodexReadFailure.NoQuotaData, ProviderIssue.CodexProtocolUnsupported)]
    public async Task EveryRunnerFailure_MapsToABoundedIssue_AndNoObservations(CodexReadFailure failure, ProviderIssue expected)
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteFail(r, failure);
        Assert.Equal(expected, r.Snap.Codex.Issue);
        Assert.Null(r.Snap.Codex.LastObservationAt);
        Assert.Equal(ProviderReason.ReadFailedNoData, r.Snap.Codex.View.Reason);
        Assert.DoesNotContain(r.Snap.Codex.View.Windows, w => w.DisplayedUsedPercent is not null);
    }

    [Fact]
    public async Task AnUnexpectedExceptionFromTheSource_BecomesABoundedFailure_WithoutLeakingItsMessage()
    {
        await using var r = new Rig();
        r.Codex.Immediate = () => throw new InvalidOperationException("CANARY_SECRET_MESSAGE C:\\Users\\x\\auth");
        r.C.SetUiVisible(true);
        await Wait.Until(() => r.Snap.Codex.Issue == ProviderIssue.TemporaryProviderFailure);
        Assert.DoesNotContain("CANARY", System.Text.Json.JsonSerializer.Serialize(r.Snap), StringComparison.Ordinal);
    }

    // ---------------- visibility / shutdown during a read ----------------

    [Fact]
    public async Task HidingTheUiDuringAnInFlightRead_LetsItFinish_ThenNoFurtherPolling()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        r.C.SetUiVisible(false);
        Assert.Equal(ProviderActivity.Reading, r.Snap.Codex.Activity);
        await CompleteOk(r);
        Assert.Equal(2, r.Snap.Codex.View.Windows.Count(w => w.DisplayedUsedPercent is not null));
        r.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, r.Codex.Calls);
    }

    [Fact]
    public async Task ShowingTheUiAgainDuringAnInFlightRead_DoesNotStartASecond()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        r.C.SetUiVisible(false);
        r.C.SetUiVisible(true);
        r.C.SetUiVisible(false);
        r.C.SetUiVisible(true);
        Assert.Equal(1, r.Codex.Calls);
        await CompleteOk(r);
        Assert.Equal(1, r.Codex.Calls);
    }

    [Fact]
    public async Task Shutdown_CancelsTheInFlightRead_RecordsNoFailure_AndReleasesWaiters()
    {
        var r = new Rig();
        Task<CodexRefreshResult> waiting = r.C.RefreshCodexAsync();
        Assert.False(r.Codex.LastToken.IsCancellationRequested);
        await r.DisposeAsync();
        Assert.True(r.Codex.LastToken.IsCancellationRequested);
        Assert.Equal(CodexRefreshStatus.Failed, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.True(r.Watcher.Disposed);
        Assert.Equal(1, r.Codex.Calls);
        r.Advance(TimeSpan.FromHours(1));                                            // nothing fires after disposal
        Assert.Equal(1, r.Codex.Calls);
        Assert.Equal(0, r.Scheduler.Pending);
    }

    [Fact]
    public async Task ACancelledRunnerOutcome_IsNotAProviderFailure()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        r.Codex.Complete(new CodexReadOutcome(CodexReadFailure.Cancelled, null, r.Clock.Now, null, true));
        await Wait.Until(() => r.Snap.Codex.Activity == ProviderActivity.Idle);
        Assert.Equal(ProviderIssue.NoData, r.Snap.Codex.Issue);
        Assert.Equal(ProviderReason.CodexNotReadYet, r.Snap.Codex.View.Reason);
    }

    // ---------------- clock changes ----------------

    [Fact]
    public async Task ClockMovingBackwards_DoesNotPostponeTheNextRead_OrTheCooldown()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r);
        r.Scheduler.Jump(Fx.T0.AddHours(-2));                                        // wall clock set back two hours
        r.C.NotifyEnvironmentChanged();
        Assert.Equal(1, r.Codex.Calls);
        AppSnapshot s = r.Snap;
        Assert.Equal(r.Clock.Now.AddSeconds(60), s.CodexRefreshAllowedAt);           // cooldown measured from the clamped anchor, not T0 + 60 s
        r.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(2, r.Codex.Calls);                                              // poll is 10 min after "now", not 2 h 10 min
    }

    [Fact]
    public async Task ClockMovingForward_ByHours_ReadsOnceAndRederivesAges()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r);
        r.Scheduler.Jump(Fx.T0.AddHours(6));                                         // past both ... the 5-hour reset (T0+3h)
        r.C.NotifyEnvironmentChanged();
        Assert.Equal(2, r.Codex.Calls);
        Assert.Equal(WindowState.Elapsed, r.Snap.Codex.View.Windows.First(w => w.Key.Id == "codex:primary").State);
    }

    [Fact]
    public async Task NoHotLoop_TheTimerCountStaysBounded_WhileNothingHappens()
    {
        await using var r = new Rig();
        r.C.SetUiVisible(true);
        await CompleteOk(r);
        int before = r.Scheduler.Scheduled;
        r.Advance(TimeSpan.FromHours(1));
        Assert.True(r.Scheduler.Scheduled - before < 40, $"{r.Scheduler.Scheduled - before} timers in an idle hour");
    }
}
