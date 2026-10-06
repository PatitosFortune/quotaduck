using QuotaDuck.Codex;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;
using QuotaDuck.Core.Codex;

// Real temp files / watchers in some classes: keep the assembly sequential.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuotaDuck.Coordinator.Tests;

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Deterministic timers. <see cref="Advance"/> moves the clock and fires due callbacks in order, advancing the clock to each due
/// instant first. <see cref="Jump"/> moves the clock WITHOUT firing anything (system sleep / wall-clock change).
/// </summary>
internal sealed class FakeScheduler(FakeClock clock) : ITimerScheduler
{
    private sealed class Entry(DateTimeOffset due, Action cb) : IDisposable
    {
        public DateTimeOffset Due { get; } = due;

        public Action Callback { get; } = cb;

        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }

    private readonly List<Entry> _entries = [];

    public int Scheduled { get; private set; }

    public int Pending => _entries.Count(e => !e.Cancelled);

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var e = new Entry(clock.Now + delay, callback);
        _entries.Add(e);
        Scheduled++;
        return e;
    }

    public void Advance(TimeSpan span)
    {
        DateTimeOffset target = clock.Now + span;
        while (true)
        {
            Entry? next = _entries.Where(e => !e.Cancelled && e.Due <= target).MinBy(e => e.Due);
            if (next is null)
            {
                break;
            }

            _entries.Remove(next);
            if (next.Due > clock.Now)
            {
                clock.Now = next.Due;
            }

            next.Callback();
        }

        clock.Now = target;
        _entries.RemoveAll(e => e.Cancelled);
    }

    /// <summary>Wall clock changes (forward or backward) without any timer firing.</summary>
    public void Jump(DateTimeOffset to) => clock.Now = to;
}

internal sealed class FakeClaudeSource : IClaudeStateSource
{
    public ClaudeSourceRead Next { get; set; } = new(ClaudeSourceStatus.Absent, ClaudeState.Empty);

    public int Reads { get; private set; }

    public ClaudeSourceRead Read()
    {
        Reads++;
        return Next;
    }

    public void SetState(ClaudeState s) => Next = new ClaudeSourceRead(ClaudeSourceStatus.Ok, s);
}

internal sealed class FakeWatcher : IClaudeStateWatcher
{
    public event Action? Changed;

    public bool IsAttached { get; set; }

    public bool CanAttach { get; set; } = true;

    public int AttachCalls { get; private set; }

    public bool Disposed { get; private set; }

    public bool TryAttach()
    {
        AttachCalls++;
        if (CanAttach)
        {
            IsAttached = true;
        }

        return IsAttached;
    }

    public void Raise() => Changed?.Invoke();

    public void Dispose() => Disposed = true;
}

internal sealed class FakeCodex : ICodexQuotaSource
{
    private readonly Queue<TaskCompletionSource<CodexReadOutcome>> _pending = new();
    private int _active;

    public int Calls { get; private set; }

    public int MaxConcurrent { get; private set; }

    public CancellationToken LastToken { get; private set; }

    /// <summary>When set, reads complete immediately with this outcome instead of waiting for <see cref="Complete"/>.</summary>
    public Func<CodexReadOutcome>? Immediate { get; set; }

    public int Waiting => _pending.Count;

    public Task<CodexReadOutcome> ReadAsync(CancellationToken ct)
    {
        Calls++;
        LastToken = ct;
        int now = Interlocked.Increment(ref _active);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        if (Immediate is not null)
        {
            Interlocked.Decrement(ref _active);
            return Task.FromResult(Immediate());
        }

        var tcs = new TaskCompletionSource<CodexReadOutcome>(TaskCreationOptions.None);   // continuations run synchronously inside Complete(): deterministic
        ct.Register(() => tcs.TrySetCanceled(ct));
        _pending.Enqueue(tcs);
        return tcs.Task.ContinueWith(
            t =>
            {
                Interlocked.Decrement(ref _active);
                return t.GetAwaiter().GetResult();
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Complete(CodexReadOutcome outcome) => _pending.Dequeue().SetResult(outcome);
}

internal static class Fx
{
    public static readonly DateTimeOffset T0 = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public static CodexObservationSet Obs(DateTimeOffset at, double used5h = 40, DateTimeOffset? reset5h = null, double usedWk = 20, DateTimeOffset? resetWk = null) =>
        CodexObservations.FromRead(
        [
            new CodexBucket(
                "codex",
                null,
                new CodexWindowReading(used5h, TimeSpan.FromMinutes(300), reset5h ?? at.AddHours(3)),
                new CodexWindowReading(usedWk, TimeSpan.FromMinutes(10080), resetWk ?? at.AddDays(5)),
                false),
        ],
        at);

    public static CodexReadOutcome Ok(DateTimeOffset at, double used5h = 40, DateTimeOffset? reset5h = null, DateTimeOffset? resetWk = null) =>
        new(null, Obs(at, used5h, reset5h, 20, resetWk), at, null, true);

    public static CodexReadOutcome Fail(DateTimeOffset at, CodexReadFailure f) => new(f, null, at, null, true);

    public static ClaudeWindowRecord Rec(double pct, DateTimeOffset reset, DateTimeOffset changed, DateTimeOffset? received = null) =>
        new(pct, reset, changed, received ?? changed);

    public static ClaudeState Claude(ClaudeWindowRecord? five, ClaudeWindowRecord? seven = null) => new(five, seven);
}

/// <summary>A coordinator wired to fakes, started at <see cref="Fx.T0"/>.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public FakeClock Clock { get; } = new(Fx.T0);

    public FakeScheduler Scheduler { get; }

    public FakeClaudeSource Claude { get; } = new();

    public FakeWatcher Watcher { get; } = new() { IsAttached = true };

    public FakeCodex Codex { get; } = new();

    public QuotaCoordinator C { get; }

    public List<AppSnapshot> Published { get; } = [];

    public Rig(CoordinatorOptions? options = null, bool start = true)
    {
        Scheduler = new FakeScheduler(Clock);
        C = new QuotaCoordinator(options ?? new CoordinatorOptions(), Claude, Watcher, Codex, Clock, Scheduler);
        C.Changed += Published.Add;
        if (start)
        {
            C.Start();
        }
    }

    public AppSnapshot Snap => C.Capture();

    public void Advance(TimeSpan t) => Scheduler.Advance(t);

    public void Advance(double seconds) => Scheduler.Advance(TimeSpan.FromSeconds(seconds));

    public ValueTask DisposeAsync() => C.DisposeAsync();
}
