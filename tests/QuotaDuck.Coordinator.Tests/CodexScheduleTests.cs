namespace QuotaDuck.Coordinator.Tests;

/// <summary>The pure Codex pull policy, boundary by boundary.</summary>
public class CodexScheduleTests
{
    private static readonly CoordinatorPolicy P = CoordinatorPolicy.Default;
    private static readonly DateTimeOffset N = Fx.T0;

    private static CodexScheduleInput In(
        bool visible = true, bool enabled = true, bool inFlight = false,
        DateTimeOffset? started = null, DateTimeOffset? completed = null, int failures = 0, params DateTimeOffset[] resets) =>
        new(N, enabled, visible, inFlight, started, completed, failures, resets);

    private static ScheduledCodexRead? Next(CodexScheduleInput i) => CodexSchedule.Next(i, P);

    [Fact]
    public void Disabled_OrInFlight_SchedulesNothing()
    {
        Assert.Null(Next(In(enabled: false)));
        Assert.Null(Next(In(inFlight: true)));
        Assert.Null(Next(In(inFlight: true, started: N.AddMinutes(-30), completed: N.AddMinutes(-30), resets: N.AddMinutes(-1))));
    }

    [Fact]
    public void VisibleAndNeverRead_ReadsImmediately_AsInitial()
    {
        ScheduledCodexRead r = Next(In())!;
        Assert.Equal(new ScheduledCodexRead(N, CodexTrigger.InitialVisible), r);
    }

    [Fact]
    public void HiddenAndNeverRead_SchedulesNothing_NoInitialReadWithoutAUi()
    {
        Assert.Null(Next(In(visible: false)));
    }

    [Fact]
    public void VisiblePoll_IsTenMinutesAfterTheLastCompletion_ExactBoundary()
    {
        DateTimeOffset done = N.AddMinutes(-4);
        ScheduledCodexRead r = Next(In(started: done.AddSeconds(-1), completed: done))!;
        Assert.Equal(CodexTrigger.VisiblePoll, r.Trigger);
        Assert.Equal(done.AddMinutes(10), r.At);
    }

    [Fact]
    public void VisiblePoll_WhenOverdue_IsReportedAtItsDueTime_NotAtNow()
    {
        DateTimeOffset done = N.AddHours(-3);
        ScheduledCodexRead r = Next(In(started: done.AddSeconds(-1), completed: done))!;
        Assert.Equal(done.AddMinutes(10), r.At);                                   // already in the past => due immediately
        Assert.True(r.At <= N);
    }

    [Fact]
    public void Hidden_AfterASuccess_SchedulesNothingWithoutKnownResets()
    {
        Assert.Null(Next(In(visible: false, started: N.AddMinutes(-20), completed: N.AddMinutes(-20))));
    }

    [Fact]
    public void Hidden_ResetRead_IsAtResetPlusDelay_AndOnlyWhileNoReadStartedAfterIt()
    {
        DateTimeOffset reset = N.AddHours(1);
        DateTimeOffset readAt = N.AddMinutes(-5);
        ScheduledCodexRead r = Next(In(visible: false, started: readAt, completed: readAt, resets: reset))!;
        Assert.Equal(new ScheduledCodexRead(reset.AddSeconds(30), CodexTrigger.ResetRead), r);

        // a read that STARTED at/after the post-reset moment satisfies it (evaluated at a "now" after that moment)
        DateTimeOffset target = reset.AddSeconds(30);
        ScheduledCodexRead? At(DateTimeOffset started, DateTimeOffset completed) =>
            Next(new CodexScheduleInput(target.AddMinutes(10), true, false, false, started, completed, 0, [reset]));
        Assert.Null(At(target, target.AddSeconds(2)));
        Assert.Null(At(target.AddMinutes(3), target.AddMinutes(3)));
        // one tick earlier does not
        Assert.NotNull(At(target.AddTicks(-1), target.AddSeconds(2)));
    }

    [Fact]
    public void ResetRead_FailedAttemptWhileHidden_IsNotRetriedUntilVisible()
    {
        DateTimeOffset reset = N.AddMinutes(-10);
        DateTimeOffset attempt = reset.AddSeconds(31);                              // the reset read ran (and failed)
        Assert.Null(Next(In(visible: false, started: attempt, completed: attempt.AddSeconds(1), failures: 1, resets: reset)));
        Assert.NotNull(Next(In(visible: true, started: attempt, completed: attempt.AddSeconds(1), failures: 1, resets: reset)));
    }

    [Fact]
    public void EarliestKnownResetWins_AndSatisfiedOnesAreSkipped()
    {
        DateTimeOffset r1 = N.AddHours(1), r2 = N.AddHours(3);
        Assert.Equal(r1.AddSeconds(30), Next(In(visible: false, started: N.AddMinutes(-5), completed: N.AddMinutes(-5), resets: [r2, r1]))!.At);
        DateTimeOffset after1 = r1.AddMinutes(2);
        Assert.Equal(r2.AddSeconds(30), Next(new CodexScheduleInput(after1.AddMinutes(1), true, false, false, after1, after1, 0, [r1, r2]))!.At);
    }

    [Fact]
    public void VisibleAndAResetEarlierThanThePoll_ChoosesTheReset()
    {
        DateTimeOffset reset = N.AddMinutes(2);
        ScheduledCodexRead r = Next(In(started: N.AddMinutes(-1), completed: N.AddMinutes(-1), resets: reset))!;
        Assert.Equal(CodexTrigger.ResetRead, r.Trigger);
        Assert.Equal(reset.AddSeconds(30), r.At);
    }

    [Fact]
    public void MinimumIntervalBetweenReadStarts_AppliesToEveryTrigger()
    {
        // a reset whose read target (reset + 30 s) is at now + 20 s, but a read started 20 s ago: wait until 60 s after THAT start
        DateTimeOffset started = N.AddSeconds(-20);
        ScheduledCodexRead r = Next(In(visible: false, started: started, completed: started.AddSeconds(5), resets: N.AddSeconds(-10)))!;
        Assert.Equal(started.AddSeconds(60), r.At);
        // exactly 60 s after the previous start the constraint no longer binds (target now = N, previous start N-60 s)
        DateTimeOffset s2 = N.AddSeconds(-60);
        Assert.Equal(N, Next(In(visible: false, started: s2, completed: s2, resets: N.AddSeconds(-30)))!.At);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 480)]
    [InlineData(5, 900)]
    [InlineData(6, 900)]
    [InlineData(40, 900)]
    [InlineData(100000, 900)]
    public void Backoff_IsExponentialFromTheLastCompletion_AndCapped(int failures, int expectedSeconds)
    {
        DateTimeOffset done = N.AddMinutes(-1);
        ScheduledCodexRead r = Next(In(started: done.AddSeconds(-1), completed: done, failures: failures))!;
        Assert.Equal(CodexTrigger.Retry, r.Trigger);
        Assert.Equal(done.AddSeconds(expectedSeconds), r.At);
    }

    [Fact]
    public void ABackwardsClock_NeverPostponesAnything()
    {
        // anchors "in the future" (clock moved back 3 h) are clamped to now
        DateTimeOffset future = N.AddHours(3);
        ScheduledCodexRead r = Next(In(started: future, completed: future))!;
        Assert.Equal(N.AddMinutes(10), r.At);                                       // not future + 10 min
        ScheduledCodexRead r2 = Next(In(visible: false, started: future, completed: future, resets: N.AddSeconds(-10)))!;
        Assert.Equal(N.AddSeconds(60), r2.At);                                      // min interval from NOW, not from the future start
    }

    [Fact]
    public void Backoff_Function_NeverOverflowsOrGoesNegative()
    {
        for (int n = 1; n < 5000; n += 7)
        {
            TimeSpan d = CodexSchedule.Backoff(n, P);
            Assert.InRange(d, P.CodexBackoffBase, P.CodexBackoffCap);
        }
    }

    [Fact]
    public void Policy_Validation_RejectsNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinatorPolicy { CodexMinInterval = TimeSpan.Zero }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinatorPolicy { CodexBackoffCap = TimeSpan.FromSeconds(1) }.Validated());
        Assert.Equal(TimeSpan.FromMinutes(10), CoordinatorPolicy.Default.CodexVisibleInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), CoordinatorPolicy.Default.CodexMinInterval);
    }
}
