using QuotaDuck.Core;

namespace QuotaDuck.Coordinator;

/// <summary>
/// Bounded, UI-safe reason a provider is not simply "fine". Never carries text, exceptions, paths or protocol content.
/// The two providers use their own subsets; a value for one provider never implies anything about the other.
/// </summary>
public enum ProviderIssue
{
    None = 0,

    /// <summary>The provider is switched off in the app's preferences.</summary>
    NotConfigured = 1,

    /// <summary>Claude: the bridge has never reported (no state file / empty). Must read "Waiting for Claude Code in a terminal" — never 0%.</summary>
    WaitingForTerminalClaudeCode = 2,

    /// <summary>No observation exists yet and no failure is known (e.g. Codex before its first read).</summary>
    NoData = 3,

    /// <summary>Claude: the state file exists but could not be read/decoded right now; the last good state (if any) is kept.</summary>
    ClaudeStateUnreadable = 4,

    /// <summary>Claude: the state file was written by a newer schema this build does not understand.</summary>
    ClaudeStateUnsupportedVersion = 5,

    /// <summary>Codex is not installed for this user.</summary>
    CodexNotInstalled = 6,

    /// <summary>The installed Codex executable failed path/signature/signer validation and was never launched.</summary>
    CodexNotTrusted = 7,

    /// <summary>Codex answered with a protocol error (typically "not signed in"; the exact code is not mapped without evidence).</summary>
    CodexRequestRejected = 8,

    /// <summary>Codex's reply did not have the expected shape/protocol (possibly a newer, unsupported version).</summary>
    CodexProtocolUnsupported = 9,

    /// <summary>Codex could not be started.</summary>
    ProviderUnavailable = 10,

    /// <summary>Timeout, crash or early exit: expected to be transient; retried with backoff.</summary>
    TemporaryProviderFailure = 11,
}

public enum ProviderActivity
{
    Idle = 0,

    /// <summary>A real provider read is in flight (Codex only; Claude never has one).</summary>
    Reading = 1,
}

/// <summary>Why a Codex read happens (or is scheduled).</summary>
public enum CodexTrigger
{
    /// <summary>First read once the quota UI is visible.</summary>
    InitialVisible = 1,

    /// <summary>Periodic refresh while the quota UI is visible.</summary>
    VisiblePoll = 2,

    /// <summary>Retry after a failure, with exponential backoff.</summary>
    Retry = 3,

    /// <summary>One read shortly after a known reset time.</summary>
    ResetRead = 4,

    /// <summary>User-initiated.</summary>
    Manual = 5,
}

public sealed record ScheduledCodexRead(DateTimeOffset At, CodexTrigger Trigger);

/// <param name="LastObservationAt">Claude: when the provider's reported numbers last CHANGED (a file reread never moves this). Codex: when the last SUCCESSFUL read was received. A failed attempt never moves it.</param>
public sealed record ProviderSnapshot(
    ProviderView View,
    ProviderIssue Issue,
    ProviderActivity Activity,
    DateTimeOffset? LastObservationAt);

/// <summary>Everything the UI needs; nothing else. Each provider is derived from its own inputs only.</summary>
public sealed record AppSnapshot(
    long Version,
    DateTimeOffset At,
    bool UiVisible,
    ProviderSnapshot Claude,
    ProviderSnapshot Codex,
    bool CodexRefreshAllowed,
    DateTimeOffset? CodexRefreshAllowedAt,
    ScheduledCodexRead? CodexNextRead,
    DateTimeOffset? ClaudeNextLocalReread);

public enum CodexRefreshStatus
{
    Succeeded = 1,
    Failed = 2,

    /// <summary>Inside the minimum interval between real provider reads: nothing was started.</summary>
    CooldownActive = 3,

    /// <summary>Codex is switched off in the preferences.</summary>
    Disabled = 4,

    ShuttingDown = 5,
}

public sealed record CodexRefreshResult(CodexRefreshStatus Status, bool JoinedInFlight, DateTimeOffset? NextAllowedAt);

/// <summary>All coordination timings in one validated place. Defaults follow the approved audit policy.</summary>
public sealed record CoordinatorPolicy
{
    /// <summary>Refresh interval while the quota UI is visible (measured from the previous read's completion).</summary>
    public TimeSpan CodexVisibleInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Minimum time between the STARTS of two real Codex reads, whatever the trigger.</summary>
    public TimeSpan CodexMinInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The reset read happens this long after a known reset time.</summary>
    public TimeSpan CodexResetReadDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CodexBackoffBase { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan CodexBackoffCap { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Coalesces bursts of file-system events into one reread.</summary>
    public TimeSpan ClaudeDebounce { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Low-frequency local reread in case a file-system notification was missed.</summary>
    public TimeSpan ClaudeSafetyReread { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan ClaudeRetryBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>While the watcher cannot attach (directory not there yet), retry attaching this often (a single existence check).</summary>
    public TimeSpan WatcherReattach { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>No timer sleeps longer than this, so a missed sleep/clock event self-heals.</summary>
    public TimeSpan MaxTimerDelay { get; init; } = TimeSpan.FromMinutes(5);

    public CoordinatorPolicy Validated()
    {
        static void Positive(TimeSpan t, string n)
        {
            if (t <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(n, "must be positive");
            }
        }

        Positive(CodexVisibleInterval, nameof(CodexVisibleInterval));
        Positive(CodexMinInterval, nameof(CodexMinInterval));
        Positive(CodexResetReadDelay, nameof(CodexResetReadDelay));
        Positive(CodexBackoffBase, nameof(CodexBackoffBase));
        Positive(CodexBackoffCap, nameof(CodexBackoffCap));
        Positive(ClaudeDebounce, nameof(ClaudeDebounce));
        Positive(ClaudeSafetyReread, nameof(ClaudeSafetyReread));
        Positive(ClaudeRetryBase, nameof(ClaudeRetryBase));
        Positive(WatcherReattach, nameof(WatcherReattach));
        Positive(MaxTimerDelay, nameof(MaxTimerDelay));
        if (CodexBackoffCap < CodexBackoffBase)
        {
            throw new ArgumentOutOfRangeException(nameof(CodexBackoffCap), "cap must be >= base");
        }

        return this;
    }

    public static CoordinatorPolicy Default { get; } = new CoordinatorPolicy().Validated();
}

public sealed record CoordinatorOptions
{
    public bool ClaudeEnabled { get; init; } = true;

    public bool CodexEnabled { get; init; } = true;

    public CoordinatorPolicy Policy { get; init; } = CoordinatorPolicy.Default;

    /// <summary>Freshness thresholds for the Core derivation. Null = Core default for Claude and a 10-minute-poll policy for Codex.</summary>
    public FreshnessPolicy? Freshness { get; init; }
}
