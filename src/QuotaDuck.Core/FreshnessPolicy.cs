namespace QuotaDuck.Core;

/// <summary>
/// All time thresholds in one place (tunable, validated). Defaults follow Phase 1b §34.
/// Boundaries are EXCLUSIVE on the older side: a window is Fresh while age &lt; FreshWithin, and the new state
/// begins exactly at evidence + threshold. That makes <see cref="WindowView.NextTransitionAt"/> an exact instant.
/// </summary>
public sealed record FreshnessPolicy
{
    /// <summary>Push sources: "Fresh" while the last CHANGE is younger than this.</summary>
    public TimeSpan PushFreshWithin { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Push sources: "Quiet" until the last change is this old, then "Sleeping".</summary>
    public TimeSpan PushQuietWithin { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Polled sources: "Fresh" while the last successful read is younger than this (default 2 x 10-min poll).</summary>
    public TimeSpan PolledFreshWithin { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Polled sources: "Quiet" until the last successful read is this old, then "Sleeping".</summary>
    public TimeSpan PolledQuietWithin { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Evidence timestamps further in the future than this are a clock anomaly (never "Fresh").</summary>
    public TimeSpan FutureTolerance { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Two reset times within this distance are the same window epoch (provider jitter).</summary>
    public TimeSpan EpochTolerance { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A reset time later than now + window length + this is implausible.</summary>
    public TimeSpan ResetPlausibilityTolerance { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Window length assumed for the plausibility check when a window has no known duration.</summary>
    public TimeSpan MaxPlausibleWindow { get; init; } = TimeSpan.FromDays(8);

    /// <summary>Coalescing: the bridge rewrites its state file for an unchanged payload at most this often.</summary>
    public TimeSpan PersistReceivedAtEvery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// v1 default for <see cref="LowerAcceptCount"/>. A CONSERVATIVE HEURISTIC, not a measured provider behaviour:
    /// Anthropic's downward usage corrections within a window are not yet characterised (Phase 1b §34.6 UNKNOWN).
    /// Three identical consecutive lower reports is "enough to stop believing it is one stale cache" while still
    /// making an alternating live/stale pair of sessions unable to drag the number down.
    /// </summary>
    public const int DefaultLowerAcceptCount = 3;

    /// <summary>
    /// A same-epoch LOWER percentage is treated as another session's stale cache and ignored, unless the same lower
    /// value is reported this many times IN A ROW (then accepted as a genuine downward adjustment). Any higher or equal
    /// report, a different lower value, or a new reset epoch restarts the sequence. Minimum 2. See
    /// <see cref="DefaultLowerAcceptCount"/> for why the default is a conservative v1 heuristic.
    /// </summary>
    public int LowerAcceptCount { get; init; } = DefaultLowerAcceptCount;

    public static FreshnessPolicy Default { get; } = new FreshnessPolicy().Validated();

    /// <summary>Policy whose polled "Fresh" bound is two poll intervals (one missed poll is still fresh).</summary>
    public static FreshnessPolicy ForCodexPollInterval(TimeSpan interval) =>
        new FreshnessPolicy { PolledFreshWithin = interval * 2 }.Validated();

    public FreshnessPolicy Validated()
    {
        static void Positive(TimeSpan t, string name)
        {
            if (t <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, t, "must be positive");
            }
        }

        Positive(PushFreshWithin, nameof(PushFreshWithin));
        Positive(PushQuietWithin, nameof(PushQuietWithin));
        Positive(PolledFreshWithin, nameof(PolledFreshWithin));
        Positive(PolledQuietWithin, nameof(PolledQuietWithin));
        Positive(FutureTolerance, nameof(FutureTolerance));
        Positive(EpochTolerance, nameof(EpochTolerance));
        Positive(ResetPlausibilityTolerance, nameof(ResetPlausibilityTolerance));
        Positive(MaxPlausibleWindow, nameof(MaxPlausibleWindow));
        Positive(PersistReceivedAtEvery, nameof(PersistReceivedAtEvery));
        if (PushFreshWithin >= PushQuietWithin)
        {
            throw new ArgumentException("PushFreshWithin must be < PushQuietWithin");
        }

        if (PolledFreshWithin >= PolledQuietWithin)
        {
            throw new ArgumentException("PolledFreshWithin must be < PolledQuietWithin");
        }

        if (LowerAcceptCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(LowerAcceptCount), LowerAcceptCount, "must be >= 2");
        }

        return this;
    }
}
