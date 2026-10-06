using QuotaDuck.Codex;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.Coordinator.Tests;

/// <summary>The passive Claude side: watching, rereading and re-deriving. Nothing here can make Claude report anything.</summary>
public class ClaudeCoordinatorTests
{
    // deliberately NOT a multiple of the 5-minute safety tick, so a transition can only be noticed by its own timer
    private static readonly DateTimeOffset Reset5 = Fx.T0.AddHours(2).AddSeconds(137);
    private static readonly DateTimeOffset ResetWk = Fx.T0.AddDays(3);

    private static ClaudeState Good(double pct = 30, DateTimeOffset? changed = null) =>
        Fx.Claude(Fx.Rec(pct, Reset5, changed ?? Fx.T0.AddMinutes(-1)), Fx.Rec(10, ResetWk, changed ?? Fx.T0.AddMinutes(-1)));

    private static WindowView Five(AppSnapshot s) => s.Claude.View.Windows.First(w => w.Key.Id == "five_hour");

    // ---------------- absent / appearing / disappearing ----------------

    [Fact]
    public async Task FileAbsentAtStartup_IsWaitingForTerminalClaudeCode_NeverZeroPercent()
    {
        await using var r = new Rig();
        AppSnapshot s = r.Snap;
        Assert.Equal(ProviderIssue.WaitingForTerminalClaudeCode, s.Claude.Issue);
        Assert.Equal(ProviderRollup.NoData, s.Claude.View.Rollup);
        Assert.Equal(ProviderReason.WaitingForTerminalClaudeCode, s.Claude.View.Reason);
        Assert.All(s.Claude.View.Windows, w =>
        {
            Assert.Null(w.DisplayedUsedPercent);
            Assert.Null(w.LastKnownUsedPercent);
        });
        Assert.Null(s.Claude.LastObservationAt);
    }

    [Fact]
    public async Task FileAppearingLater_IsPickedUpAfterTheDebounce_NotBefore()
    {
        await using var r = new Rig();
        int reads = r.Claude.Reads;
        r.Claude.SetState(Good());
        r.Watcher.Raise();
        r.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Equal(reads, r.Claude.Reads);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(reads + 1, r.Claude.Reads);
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
        Assert.Equal(ProviderIssue.None, r.Snap.Claude.Issue);
        Assert.Contains(r.Published, s => Five(s).DisplayedUsedPercent == 30);        // the UI is told
    }

    [Fact]
    public async Task DuplicateWatcherEvents_CoalesceIntoOneReread()
    {
        await using var r = new Rig();
        int reads = r.Claude.Reads;
        r.Claude.SetState(Good());
        for (int i = 0; i < 25; i++)
        {
            r.Watcher.Raise();
            r.Advance(TimeSpan.FromMilliseconds(3));
        }

        r.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(reads + 1, r.Claude.Reads);
        // a later, separate burst is a separate reread
        r.Watcher.Raise();
        r.Watcher.Raise();
        r.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(reads + 2, r.Claude.Reads);
    }

    [Fact]
    public async Task FileDisappearing_ClearsTheNumbers_ItNeverKeepsShowingOldOnes()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
        r.Claude.Next = new ClaudeSourceRead(ClaudeSourceStatus.Absent, ClaudeState.Empty);
        r.Watcher.Raise();
        r.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Null(Five(r.Snap).DisplayedUsedPercent);
        Assert.Equal(ProviderIssue.WaitingForTerminalClaudeCode, r.Snap.Claude.Issue);
    }

    [Fact]
    public async Task StartingAfterTheWindowAlreadyElapsed_ShowsElapsed_WithNoNumber()
    {
        var r = new Rig(start: false);
        r.Claude.SetState(Fx.Claude(Fx.Rec(80, Fx.T0.AddMinutes(-30), Fx.T0.AddHours(-4))));
        r.C.Start();
        WindowView w = Five(r.Snap);
        Assert.Equal(WindowState.Elapsed, w.State);
        Assert.Null(w.DisplayedUsedPercent);
        Assert.Equal(ProviderReason.AwaitingNextClaudeReport, r.Snap.Claude.View.Reason);
        await r.DisposeAsync();
    }

    // ---------------- corrupt / unreadable / future schema ----------------

    [Fact]
    public async Task CorruptOrUnreadableFile_KeepsTheLastGoodState_FlagsIt_AndRetriesWithBackoff()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();

        r.Claude.Next = new ClaudeSourceRead(ClaudeSourceStatus.Corrupt, ClaudeState.Empty);
        r.Watcher.Raise();
        r.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(ProviderIssue.ClaudeStateUnreadable, r.Snap.Claude.Issue);
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);                           // intermediate/corrupt states never blank the UI

        int reads = r.Claude.Reads;
        r.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(reads, r.Claude.Reads);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(reads + 1, r.Claude.Reads);                                       // retry after 1 s
        r.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(reads + 2, r.Claude.Reads);                                       // then 2 s
        r.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(reads + 3, r.Claude.Reads);                                       // then 4 s

        r.Claude.SetState(Good(35, Fx.T0.AddMinutes(5)));
        r.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(ProviderIssue.None, r.Snap.Claude.Issue);
        Assert.Equal(35, Five(r.Snap).DisplayedUsedPercent);
        int after = r.Claude.Reads;
        r.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(after, r.Claude.Reads);                                           // retries stopped
    }

    [Fact]
    public async Task UnreadableFile_WithNoPriorState_StaysNoData_NotZero()
    {
        await using var r = new Rig();
        r.Claude.Next = new ClaudeSourceRead(ClaudeSourceStatus.Unreadable, ClaudeState.Empty);
        r.C.RereadClaudeState();
        Assert.Equal(ProviderIssue.ClaudeStateUnreadable, r.Snap.Claude.Issue);
        Assert.Null(Five(r.Snap).DisplayedUsedPercent);
    }

    [Fact]
    public async Task UnsupportedSchema_IsFlagged_KeepsLastGood_AndDoesNotRetryFast()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();
        r.Claude.Next = new ClaudeSourceRead(ClaudeSourceStatus.FutureSchema, ClaudeState.Empty);
        r.C.RereadClaudeState();
        Assert.Equal(ProviderIssue.ClaudeStateUnsupportedVersion, r.Snap.Claude.Issue);
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
        int reads = r.Claude.Reads;
        r.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(reads, r.Claude.Reads);
    }

    // ---------------- missed events, safety reread, watcher health ----------------

    [Fact]
    public async Task MissedEvents_AreCaughtBy_TheFiveMinuteSafetyReread()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());                                                       // changed on disk, no event delivered
        r.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        Assert.Null(Five(r.Snap).DisplayedUsedPercent);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
    }

    [Fact]
    public async Task DetachedWatcher_IsReattachedEveryThirtySeconds_AndAReadFollowsTheAttach()
    {
        var r = new Rig(start: false);
        r.Watcher.IsAttached = false;
        r.Watcher.CanAttach = false;
        r.C.Start();
        int attaches = r.Watcher.AttachCalls;
        r.Advance(TimeSpan.FromSeconds(29.999));
        Assert.Equal(attaches, r.Watcher.AttachCalls);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(attaches + 1, r.Watcher.AttachCalls);
        r.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(attaches + 2, r.Watcher.AttachCalls);

        r.Claude.SetState(Good());                                                       // the directory appears and the file is already there
        r.Watcher.CanAttach = true;
        r.Advance(TimeSpan.FromSeconds(30));
        Assert.True(r.Watcher.IsAttached);
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
        int stable = r.Watcher.AttachCalls;
        r.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(stable, r.Watcher.AttachCalls);                                    // attached: no more reattach polling (safety verify aside)
        await r.DisposeAsync();
    }

    [Fact]
    public async Task EnvironmentNotifications_RereadTheFile_AndReverifyTheWatcher()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        int attaches = r.Watcher.AttachCalls;
        r.C.NotifyEnvironmentChanged();                                                  // resume / unlock / clock change
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
        Assert.True(r.Watcher.AttachCalls > attaches);
    }

    // ---------------- re-evaluation without any reread ----------------

    [Fact]
    public async Task WindowElapses_ExactlyAtResetsAt_WithoutAnyFileEventOrReread()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();
        int reads = r.Claude.Reads;
        r.Advance(Reset5 - Fx.T0 - TimeSpan.FromMilliseconds(1));
        Assert.NotEqual(WindowState.Elapsed, Five(r.Snap).State);
        r.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(WindowState.Elapsed, Five(r.Snap).State);
        Assert.Null(Five(r.Snap).DisplayedUsedPercent);
        Assert.Contains(r.Published, s => s.At == Reset5 && Five(s).State == WindowState.Elapsed);     // the UI is told AT the instant
        Assert.Equal(24, r.Claude.Reads - reads);                                      // only the 24 five-minute safety rereads: the transition itself needed none
    }

    [Fact]
    public async Task FreshQuietSleeping_TransitionAtExactInstants_EvenWhenTheFileIsRereadUnchanged()
    {
        await using var r = new Rig();
        r.Claude.SetState(Fx.Claude(Fx.Rec(30, Fx.T0.AddHours(4), Fx.T0.AddSeconds(-3), Fx.T0.AddSeconds(-3))));
        r.C.RereadClaudeState();
        Assert.Equal(WindowState.Fresh, Five(r.Snap).State);

        for (int i = 0; i < 12; i++)                                                   // repeated rereads of the SAME state
        {
            r.Advance(TimeSpan.FromMinutes(1));
            r.C.RereadClaudeState();
        }

        Assert.Equal(WindowState.Quiet, Five(r.Snap).State);                          // a reread is not an observation: Fresh ended at +10:00
        Assert.Contains(r.Published, s => s.At == Fx.T0.AddMinutes(10).AddSeconds(-3) && Five(s).State == WindowState.Quiet);
        r.Advance(TimeSpan.FromHours(2));
        Assert.Equal(WindowState.Sleeping, Five(r.Snap).State);
        Assert.Equal(Fx.T0.AddSeconds(-3), r.Snap.Claude.LastObservationAt);                          // the observation time never moved
    }

    [Fact]
    public async Task OnlyARealChange_MakesClaudeFreshAgain()
    {
        await using var r = new Rig();
        r.Claude.SetState(Fx.Claude(Fx.Rec(30, Fx.T0.AddHours(4), Fx.T0)));
        r.C.RereadClaudeState();
        r.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(WindowState.Quiet, Five(r.Snap).State);
        r.Claude.SetState(Fx.Claude(Fx.Rec(31, Fx.T0.AddHours(4), r.Clock.Now)));       // the bridge recorded a change
        r.C.RereadClaudeState();
        Assert.Equal(WindowState.Fresh, Five(r.Snap).State);
        Assert.Equal(r.Clock.Now, r.Snap.Claude.LastObservationAt);
    }

    [Fact]
    public async Task ClockMovingBackwards_DoesNotDelayTheSafetyReread()
    {
        await using var r = new Rig();
        r.Advance(TimeSpan.FromMinutes(4));
        r.Scheduler.Jump(Fx.T0.AddHours(-3));
        r.C.NotifyEnvironmentChanged();                                                  // schedules the next safety reread from "now"
        r.Claude.SetState(Good());
        r.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
    }

    [Fact]
    public async Task ClaudeDisabled_NeverReadsOrWatches_AndIsNotConfigured()
    {
        await using var r = new Rig(new CoordinatorOptions { ClaudeEnabled = false });
        r.Advance(TimeSpan.FromHours(1));
        r.Watcher.Raise();
        r.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, r.Claude.Reads);
        Assert.Equal(ProviderIssue.NotConfigured, r.Snap.Claude.Issue);
        Assert.Equal(ProviderRollup.NotConfigured, r.Snap.Claude.View.Rollup);
    }

    [Fact]
    public async Task AThrowingChangedHandler_DoesNotBreakCoordination()
    {
        await using var r = new Rig();
        r.C.Changed += _ => throw new InvalidOperationException("ui bug");
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();
        r.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(30, Five(r.Snap).DisplayedUsedPercent);
    }

    // ---------------- provider independence ----------------

    [Fact]
    public async Task CodexFailure_NeverTouchesClaude_AndClaudeNeverTouchesCodex()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();
        r.C.SetUiVisible(true);
        r.Codex.Complete(Fx.Ok(r.Clock.Now));
        await Wait.Until(() => r.Snap.Codex.Activity == ProviderActivity.Idle);
        ProviderSnapshot claudeBefore = r.Snap.Claude;
        ProviderSnapshot codexBefore = r.Snap.Codex;

        // Codex fails repeatedly
        r.Advance(TimeSpan.FromMinutes(10));
        r.Codex.Complete(Fx.Fail(r.Clock.Now, CodexReadFailure.ValidationFailed));
        await Wait.Until(() => r.Snap.Codex.Issue == ProviderIssue.CodexNotTrusted);
        Assert.Equal(claudeBefore.Issue, r.Snap.Claude.Issue);
        Assert.Equal(claudeBefore.LastObservationAt, r.Snap.Claude.LastObservationAt);
        Assert.Equal(Five(r.Snap).DisplayedUsedPercent, claudeBefore.View.Windows.First(w => w.Key.Id == "five_hour").DisplayedUsedPercent);

        // Claude goes stale / corrupt / disappears; Codex keeps its own state
        int codexCalls = r.Codex.Calls;
        r.Claude.Next = new ClaudeSourceRead(ClaudeSourceStatus.Absent, ClaudeState.Empty);
        r.C.RereadClaudeState();
        Assert.Equal(codexCalls, r.Codex.Calls);                                         // a Claude change never triggers a Codex read
        Assert.Equal(ProviderIssue.CodexNotTrusted, r.Snap.Codex.Issue);
        Assert.Equal(codexBefore.LastObservationAt, r.Snap.Codex.LastObservationAt);
        Assert.Equal(2, r.Snap.Codex.View.Windows.Count(w => w.LastKnownUsedPercent is not null));
    }

    [Fact]
    public async Task OneProviderBeingStale_NeverMakesTheOtherStale()
    {
        await using var r = new Rig();
        r.Claude.SetState(Fx.Claude(Fx.Rec(30, Fx.T0.AddHours(4), Fx.T0)));
        r.C.RereadClaudeState();
        r.C.SetUiVisible(true);
        r.Codex.Complete(Fx.Ok(r.Clock.Now));
        await Wait.Until(() => r.Snap.Codex.Activity == ProviderActivity.Idle);

        r.Advance(TimeSpan.FromHours(1));                                                // Claude (push) is Quiet; Codex polling kept it fresh
        Assert.Equal(WindowState.Quiet, Five(r.Snap).State);
        r.Codex.Complete(Fx.Ok(r.Clock.Now));                                            // the 10-minute polls: complete them as they come
        await Wait.Until(() => r.Codex.Waiting == 0);
        Assert.Equal(WindowState.Fresh, r.Snap.Codex.View.Windows.First(w => w.Key.Id == "codex:primary").State);
    }

    [Fact]
    public async Task SnapshotsSerialiseWithoutAnySensitiveOrInternalContent()
    {
        await using var r = new Rig();
        r.Claude.SetState(Good());
        r.C.RereadClaudeState();
        r.C.SetUiVisible(true);
        r.Codex.Complete(Fx.Ok(r.Clock.Now));
        await Wait.Until(() => r.Snap.Codex.Activity == ProviderActivity.Idle);
        string json = System.Text.Json.JsonSerializer.Serialize(r.Snap);
        foreach (string bad in new[] { "CANARY", "\\\\", "codex.exe", ".json", "Exception", "stderr", "account", "token", "Bearer" })
        {
            Assert.DoesNotContain(bad, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
