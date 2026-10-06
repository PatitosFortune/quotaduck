using QuotaDuck.Codex;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;
using QuotaDuck.Core.Codex;

namespace QuotaDuck.Coordinator;

/// <summary>
/// Turns the two provider sources into one trustworthy live <see cref="AppSnapshot"/>.
/// <para><b>Claude is passive.</b> The bridge state file is watched (plus a low-frequency safety reread and re-evaluation at every Core
/// transition instant). Rereading the file never refreshes anything: Core derives freshness from the stored ChangedAt, not from when we
/// looked. There is no way to ask Claude for a refresh.</para>
/// <para><b>Codex is pull.</b> Scheduling is the pure <see cref="CodexSchedule"/>; the coordinator adds single-flight (one app-server read
/// at a time, concurrent triggers join it), manual refresh with a minimum interval between real reads, and honest failure handling
/// (a failure records an attempt only; it never creates, replaces or ages observations).</para>
/// <para><b>Independence.</b> Each provider's view is derived from its own input only; nothing here merges their quota semantics.</para>
/// All times come from the injected <see cref="TimeProvider"/> and <see cref="ITimerScheduler"/>. The class never touches a process,
/// network, credential, environment variable or provider file for writing.
/// </summary>
public sealed class QuotaCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CoordinatorOptions _options;
    private readonly CoordinatorPolicy _policy;
    private readonly FreshnessPolicy _freshness;
    private readonly IClaudeStateSource _claudeSource;
    private readonly IClaudeStateWatcher _watcher;
    private readonly ICodexQuotaSource _codex;
    private readonly TimeProvider _clock;
    private readonly ITimerScheduler _scheduler;

    // ---- Claude (passive) ----
    private ClaudeState _claudeState = ClaudeState.Empty;
    private ProviderIssue _claudeIssue = ProviderIssue.None;
    private DateTimeOffset? _claudeRereadDue;
    private DateTimeOffset? _claudeSafetyDue;
    private DateTimeOffset? _claudeRetryDue;
    private DateTimeOffset? _reattachDue;
    private int _claudeRetryCount;

    // ---- Codex (pull) ----
    private CodexObservationSet? _codexSet;
    private DateTimeOffset? _codexLastSuccessAt;
    private ProviderAttempt? _codexAttempt;
    private ProviderIssue _codexIssue = ProviderIssue.None;
    private int _failures;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private bool _inFlight;
    private TaskCompletionSource<bool>? _done;
    private CancellationTokenSource? _cts;
    private ScheduledCodexRead? _codexNext;

    // ---- shared ----
    private bool _visible;
    private bool _started;
    private bool _disposed;
    private long _version;
    private bool _dirty = true;
    private AppSnapshot? _current;
    private DateTimeOffset? _nextTransition;
    private IDisposable? _timer;
    private long _timerGeneration;

    public QuotaCoordinator(
        CoordinatorOptions options,
        IClaudeStateSource claudeSource,
        IClaudeStateWatcher claudeWatcher,
        ICodexQuotaSource codexSource,
        TimeProvider clock,
        ITimerScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _policy = options.Policy.Validated();
        _freshness = options.Freshness ?? FreshnessPolicy.ForCodexPollInterval(_policy.CodexVisibleInterval);
        _claudeSource = claudeSource;
        _watcher = claudeWatcher;
        _codex = codexSource;
        _clock = clock;
        _scheduler = scheduler;
    }

    /// <summary>Raised (possibly on a background thread) whenever the snapshot content changes. Handler exceptions are swallowed.</summary>
    public event Action<AppSnapshot>? Changed;

    /// <summary>The last published snapshot (null before <see cref="Start"/>).</summary>
    public AppSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>A snapshot derived NOW (ages and elapsed windows are exact at this instant). Does not publish and does not read any provider.</summary>
    public AppSnapshot Capture()
    {
        lock (_gate)
        {
            return BuildLocked(_clock.GetUtcNow(), out _);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            DateTimeOffset now = _clock.GetUtcNow();
            if (_options.ClaudeEnabled)
            {
                _watcher.Changed += OnClaudeFileChanged;
                _watcher.TryAttach();
                _claudeSafetyDue = now + _policy.ClaudeSafetyReread;
                _reattachDue = _watcher.IsAttached ? null : now + _policy.WatcherReattach;
                ReadClaudeLocked(now);
            }

            _dirty = true;
        }

        Evaluate();
    }

    /// <summary>The quota UI (window/flyout) is shown or hidden. Hidden = no ordinary Codex polling (reset reads still happen).</summary>
    public void SetUiVisible(bool visible)
    {
        lock (_gate)
        {
            if (_visible == visible)
            {
                return;
            }

            _visible = visible;
            _dirty = true;
        }

        Evaluate();
    }

    /// <summary>Rereads the local Claude state file now. The result is still the last Claude Code observation, honestly labelled — never "fresh".</summary>
    public void RereadClaudeState()
    {
        lock (_gate)
        {
            if (_started && !_disposed && _options.ClaudeEnabled)
            {
                ReadClaudeLocked(_clock.GetUtcNow());
            }
        }

        Evaluate();
    }

    /// <summary>The shell reports a system resume, session unlock, or wall-clock change. Everything is re-derived from the clock.</summary>
    public void NotifyEnvironmentChanged()
    {
        lock (_gate)
        {
            if (_started && !_disposed && _options.ClaudeEnabled)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                _watcher.TryAttach();                                   // a watcher may have gone silent across sleep
                _reattachDue = _watcher.IsAttached ? null : now + _policy.WatcherReattach;
                _claudeSafetyDue = now + _policy.ClaudeSafetyReread;
                ReadClaudeLocked(now);
            }

            _dirty = true;
        }

        Evaluate();
    }

    /// <summary>
    /// User-initiated Codex refresh. Joins a read already in flight; otherwise starts one if at least
    /// <see cref="CoordinatorPolicy.CodexMinInterval"/> has passed since the previous real read started; otherwise starts nothing.
    /// </summary>
    public async Task<CodexRefreshResult> RefreshCodexAsync(CancellationToken ct = default)
    {
        Task<bool> wait;
        bool joined;
        CancellationToken? token = null;
        lock (_gate)
        {
            if (_disposed || !_started)
            {
                return new CodexRefreshResult(CodexRefreshStatus.ShuttingDown, false, null);
            }

            if (!_options.CodexEnabled)
            {
                return new CodexRefreshResult(CodexRefreshStatus.Disabled, false, null);
            }

            DateTimeOffset now = _clock.GetUtcNow();
            if (_inFlight)
            {
                wait = _done!.Task;
                joined = true;
            }
            else
            {
                DateTimeOffset allowedAt = AllowedAtLocked(now);
                if (now < allowedAt)
                {
                    return new CodexRefreshResult(CodexRefreshStatus.CooldownActive, false, allowedAt);
                }

                token = BeginReadLocked(now);
                wait = _done!.Task;
                joined = false;
            }
        }

        Evaluate();
        if (token is { } t)
        {
            _ = RunReadAsync(t);
        }

        bool ok = await wait.WaitAsync(ct).ConfigureAwait(false);
        return new CodexRefreshResult(ok ? CodexRefreshStatus.Succeeded : CodexRefreshStatus.Failed, joined, null);
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timerGeneration++;
            _timer?.Dispose();
            _timer = null;
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            pending = _done?.Task;
        }

        _watcher.Changed -= OnClaudeFileChanged;
        _watcher.Dispose();
        if (pending is not null)
        {
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    // =====================================================================================================================
    // evaluation
    // =====================================================================================================================

    private void Evaluate()
    {
        AppSnapshot? publish;
        CancellationToken? start = null;
        lock (_gate)
        {
            if (_disposed || !_started)
            {
                return;
            }

            DateTimeOffset now = _clock.GetUtcNow();
            if (_startedAt > now || _completedAt > now)
            {
                // The wall clock moved BACKWARDS past our stored read times: rebase them permanently (not just per evaluation), otherwise
                // every later evaluation would re-clamp to "now" and the next read could never become due.
                _startedAt = _startedAt > now ? now : _startedAt;
                _completedAt = _completedAt > now ? now : _completedAt;
                _dirty = true;
            }

            if (_options.ClaudeEnabled)
            {
                ClaudeHousekeepingLocked(now);
            }

            _codexNext = NextCodexLocked(now);
            if (_codexNext is { } due && due.At <= now)
            {
                start = BeginReadLocked(now);
                _codexNext = NextCodexLocked(now);                      // in flight now => null
            }

            publish = PublishLocked(now);
            ArmLocked(now);
        }

        Publish(publish);
        if (start is { } t)
        {
            _ = RunReadAsync(t);
        }
    }

    private void OnTimer(long generation)
    {
        lock (_gate)
        {
            if (generation != _timerGeneration)
            {
                return;                                                 // superseded by a newer arm
            }
        }

        Evaluate();
    }

    private void OnClaudeFileChanged()
    {
        lock (_gate)
        {
            if (_disposed || !_started)
            {
                return;
            }

            DateTimeOffset now = _clock.GetUtcNow();
            _claudeRereadDue ??= now + _policy.ClaudeDebounce;          // a burst of duplicate events coalesces into one reread
            ArmLocked(now);
        }
    }

    // =====================================================================================================================
    // Claude (passive)
    // =====================================================================================================================

    private void ClaudeHousekeepingLocked(DateTimeOffset now)
    {
        // a wall clock that moved backwards must never postpone a reread: absolute due times are clamped to "interval from now"
        _claudeRereadDue = Cap(_claudeRereadDue, now, _policy.ClaudeDebounce);
        _claudeSafetyDue = Cap(_claudeSafetyDue, now, _policy.ClaudeSafetyReread);
        _claudeRetryDue = Cap(_claudeRetryDue, now, _policy.ClaudeSafetyReread);
        _reattachDue = Cap(_reattachDue, now, _policy.WatcherReattach);

        bool reread = false;
        if (_claudeRereadDue is { } d && d <= now)
        {
            _claudeRereadDue = null;
            reread = true;
        }

        if (_claudeRetryDue is { } r && r <= now)
        {
            _claudeRetryDue = null;
            reread = true;
        }

        if (_claudeSafetyDue is { } s && s <= now)
        {
            _claudeSafetyDue = now + _policy.ClaudeSafetyReread;
            reread = true;
            _watcher.TryAttach();                                       // also verifies an attached watcher is still valid
            _reattachDue = _watcher.IsAttached ? null : (_reattachDue ?? now + _policy.WatcherReattach);
        }

        if (!_watcher.IsAttached && _reattachDue is { } a && a <= now)
        {
            _reattachDue = _watcher.TryAttach() ? null : now + _policy.WatcherReattach;
            reread |= _watcher.IsAttached;                              // the file may have appeared before we could watch it
        }

        if (reread)
        {
            ReadClaudeLocked(now);
        }
    }

    private static DateTimeOffset? Cap(DateTimeOffset? due, DateTimeOffset now, TimeSpan interval) =>
        due is { } x && x > now + interval ? now + interval : due;

    private void ReadClaudeLocked(DateTimeOffset now)
    {
        ClaudeSourceRead r = _claudeSource.Read();
        ClaudeState oldState = _claudeState;
        ProviderIssue oldIssue = _claudeIssue;
        switch (r.Status)
        {
            case ClaudeSourceStatus.Ok:
                _claudeState = r.State;
                _claudeIssue = ProviderIssue.None;
                _claudeRetryCount = 0;
                _claudeRetryDue = null;
                break;

            case ClaudeSourceStatus.Absent:
            case ClaudeSourceStatus.Empty:
                _claudeState = ClaudeState.Empty;                       // the source of truth is gone/empty: say so, never show stale numbers
                _claudeIssue = ProviderIssue.WaitingForTerminalClaudeCode;
                _claudeRetryCount = 0;
                _claudeRetryDue = null;
                break;

            case ClaudeSourceStatus.FutureSchema:
                _claudeIssue = ProviderIssue.ClaudeStateUnsupportedVersion;     // last good state is kept; no fast retry
                _claudeRetryDue = null;
                break;

            default:                                                    // Corrupt / Unreadable: keep the last good state, retry soon
                _claudeIssue = ProviderIssue.ClaudeStateUnreadable;
                double factor = Math.Pow(2, Math.Min(_claudeRetryCount, 20));
                TimeSpan delay = TimeSpan.FromTicks((long)Math.Min(_policy.ClaudeRetryBase.Ticks * factor, _policy.ClaudeSafetyReread.Ticks));
                _claudeRetryDue = now + delay;
                _claudeRetryCount++;
                break;
        }

        if (oldState != _claudeState || oldIssue != _claudeIssue)
        {
            _dirty = true;
        }
    }

    // =====================================================================================================================
    // Codex (pull)
    // =====================================================================================================================

    private ScheduledCodexRead? NextCodexLocked(DateTimeOffset now) =>
        CodexSchedule.Next(
            new CodexScheduleInput(
                now,
                _options.CodexEnabled,
                _visible,
                _inFlight,
                _startedAt,
                _completedAt,
                _failures,
                _codexSet?.Windows.Where(w => w.ResetsAt is not null).Select(w => w.ResetsAt!.Value).Distinct().ToArray() ?? []),
            _policy);

    private DateTimeOffset AllowedAtLocked(DateTimeOffset now) =>
        _startedAt is { } s ? (s > now ? now : s) + _policy.CodexMinInterval : now;

    /// <summary>Marks a read in flight (single-flight is enforced by the caller holding the lock and checking <c>_inFlight</c>).</summary>
    private CancellationToken BeginReadLocked(DateTimeOffset now)
    {
        _inFlight = true;
        _startedAt = now;
        _cts = new CancellationTokenSource();
        _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dirty = true;
        return _cts.Token;
    }

    private async Task RunReadAsync(CancellationToken token)
    {
        CodexReadOutcome? outcome = null;
        bool cancelled = false;
        try
        {
            outcome = await _codex.ReadAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            outcome = null;                                             // unexpected adapter failure: bounded issue only, message dropped
        }

        TaskCompletionSource<bool>? done;
        bool success = false;
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            _inFlight = false;
            done = _done;
            _done = null;
            _cts?.Dispose();
            _cts = null;
            if (cancelled || outcome?.Failure == CodexReadFailure.Cancelled)
            {
                // we cancelled it (shutdown): not a provider failure, nothing is recorded
            }
            else if (outcome is { Failure: null, Observations: { } set })
            {
                _completedAt = now;
                _codexSet = set;
                _codexLastSuccessAt = outcome.At;
                _codexAttempt = new ProviderAttempt(now, null);
                _codexIssue = ProviderIssue.None;
                _failures = 0;
                success = true;
            }
            else
            {
                // FAILURE: attempt bookkeeping ONLY. _codexSet and _codexLastSuccessAt are untouched, so nothing looks fresher.
                _completedAt = now;
                _failures++;
                _codexAttempt = new ProviderAttempt(now, outcome?.Attempt?.Error ?? ProviderErrorKind.Unknown);
                _codexIssue = MapFailure(outcome?.Failure);
            }

            _dirty = true;
        }

        done?.TrySetResult(success);
        Evaluate();
    }

    internal static ProviderIssue MapFailure(CodexReadFailure? f) => f switch
    {
        CodexReadFailure.NotFound => ProviderIssue.CodexNotInstalled,
        CodexReadFailure.ValidationFailed => ProviderIssue.CodexNotTrusted,
        CodexReadFailure.StartFailed => ProviderIssue.ProviderUnavailable,
        CodexReadFailure.Rejected => ProviderIssue.CodexRequestRejected,
        CodexReadFailure.Malformed or CodexReadFailure.ProtocolViolation or CodexReadFailure.NoQuotaData => ProviderIssue.CodexProtocolUnsupported,
        _ => ProviderIssue.TemporaryProviderFailure,                    // Timeout, ProcessExited, anything unexpected
    };

    // =====================================================================================================================
    // snapshot, publication, timer
    // =====================================================================================================================

    private AppSnapshot BuildLocked(DateTimeOffset now, out DateTimeOffset? nextTransition)
    {
        ProviderInput claudeIn = ClaudeObservations.ToProviderInput(_claudeState.FiveHour, _claudeState.SevenDay, _options.ClaudeEnabled);
        ProviderInput codexIn = CodexObservations.ToProviderInput(_codexSet, _codexAttempt, _options.CodexEnabled);
        QuotaView q = QuotaDeriver.Derive(claudeIn, codexIn, now, _freshness);
        nextTransition = q.NextTransitionAt;

        ProviderIssue claudeIssue = !_options.ClaudeEnabled ? ProviderIssue.NotConfigured
            : _claudeIssue != ProviderIssue.None ? _claudeIssue
            : q.Claude.Rollup == ProviderRollup.NoData ? ProviderIssue.WaitingForTerminalClaudeCode
            : ProviderIssue.None;
        ProviderIssue codexIssue = !_options.CodexEnabled ? ProviderIssue.NotConfigured
            : _codexIssue != ProviderIssue.None ? _codexIssue
            : q.Codex.Rollup == ProviderRollup.NoData ? ProviderIssue.NoData
            : ProviderIssue.None;

        DateTimeOffset? claudeLast = new[] { _claudeState.FiveHour?.ChangedAt, _claudeState.SevenDay?.ChangedAt }.Max();
        DateTimeOffset allowedAt = AllowedAtLocked(now);
        bool allowed = _options.CodexEnabled && !_disposed && _started && !_inFlight && now >= allowedAt;
        DateTimeOffset? claudeNext = _options.ClaudeEnabled
            ? new[] { _claudeRereadDue, _claudeRetryDue, _claudeSafetyDue }.Min()
            : null;

        return new AppSnapshot(
            _version,
            now,
            _visible,
            new ProviderSnapshot(q.Claude, claudeIssue, ProviderActivity.Idle, claudeLast),
            new ProviderSnapshot(q.Codex, codexIssue, _inFlight ? ProviderActivity.Reading : ProviderActivity.Idle, _codexLastSuccessAt),
            allowed,
            _options.CodexEnabled && !_inFlight && now < allowedAt ? allowedAt : null,
            _codexNext,
            claudeNext);
    }

    private AppSnapshot? PublishLocked(DateTimeOffset now)
    {
        AppSnapshot candidate = BuildLocked(now, out DateTimeOffset? transition);
        AppSnapshot? prev = _current;
        bool crossedTransition = _nextTransition is { } t && t <= now;
        bool refreshFlip = prev is not null && prev.CodexRefreshAllowed != candidate.CodexRefreshAllowed;
        _nextTransition = transition;
        if (prev is not null && !_dirty && !crossedTransition && !refreshFlip)
        {
            return null;
        }

        _dirty = false;
        _version++;
        AppSnapshot published = candidate with { Version = _version };
        Volatile.Write(ref _current, published);
        return published;
    }

    private void Publish(AppSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception)
        {
            // a misbehaving UI handler must never break coordination
        }
    }

    private void ArmLocked(DateTimeOffset now)
    {
        _timer?.Dispose();
        _timer = null;
        if (_disposed || !_started)
        {
            return;
        }

        DateTimeOffset? wake = null;
        void Consider(DateTimeOffset? t)
        {
            if (t is { } x && (wake is null || x < wake))
            {
                wake = x;
            }
        }

        if (_nextTransition is { } nt && nt > now)
        {
            Consider(nt);
        }

        if (_options.ClaudeEnabled)
        {
            Consider(_claudeRereadDue);
            Consider(_claudeSafetyDue);
            Consider(_claudeRetryDue);
            if (!_watcher.IsAttached)
            {
                Consider(_reattachDue);
            }
        }

        if (_options.CodexEnabled)
        {
            Consider(_codexNext?.At);
            if (!_inFlight)
            {
                DateTimeOffset allowedAt = AllowedAtLocked(now);
                if (allowedAt > now)
                {
                    Consider(allowedAt);                                // the manual-refresh button re-enables exactly then
                }
            }
        }

        TimeSpan delay = wake is { } w ? w - now : _policy.MaxTimerDelay;
        if (delay > _policy.MaxTimerDelay)
        {
            delay = _policy.MaxTimerDelay;
        }

        if (delay < TimeSpan.FromMilliseconds(1))
        {
            delay = TimeSpan.FromMilliseconds(1);
        }

        long generation = ++_timerGeneration;
        _timer = _scheduler.Schedule(delay, () => OnTimer(generation));
    }
}
