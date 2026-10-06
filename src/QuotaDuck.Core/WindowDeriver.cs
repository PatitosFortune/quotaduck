namespace QuotaDuck.Core;

/// <summary>Per-window freshness state. Numeric order is "best evidence first" and is relied on by provider roll-up.</summary>
public enum WindowState
{
    /// <summary>No usable reading (never reported, or the stored reading is invalid). No number is ever shown.</summary>
    NoData = 0,

    /// <summary>Recent evidence that the provider reported this (Push: data CHANGED recently; Polled: read recently).</summary>
    Fresh = 1,

    /// <summary>Older evidence, still before the reset time. Drawn at the last value with its age; not an error.</summary>
    Quiet = 2,

    /// <summary>Evidence is old (hours). Drawn at the last value, de-emphasised, age prominent.</summary>
    Sleeping = 3,

    /// <summary>The reset time has passed. The old reading is obsolete; the new one is unknown. No number is shown.</summary>
    Elapsed = 4,
}

[Flags]
public enum WindowAnomalies
{
    None = 0,

    /// <summary>Evidence timestamp is in the future beyond tolerance (clock change or tampered state). Caps state at Quiet.</summary>
    ClockSkew = 1,

    /// <summary>Reset time is implausibly far ahead for this window length. Caps state at Quiet.</summary>
    ImplausibleReset = 2,

    /// <summary>The provider gave no reset time, so the window cannot expire by time (only by age).</summary>
    MissingResetTime = 4,

    /// <summary>The stored reading is not a valid percentage; treated as no data.</summary>
    InvalidObservation = 8,
}

/// <summary>Everything a UI needs about one window. Numbers are non-null ONLY when they are truthful to show.</summary>
public sealed record WindowView(
    WindowKey Key,
    WindowLabel Label,
    string? GroupName,
    WindowState State,
    WindowAnomalies Anomalies,
    double? DisplayedUsedPercent,
    double? DisplayedRemainingPercent,
    double? LastKnownUsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? ResetsIn,
    TimeSpan? ElapsedSince,
    TimeSpan? EvidenceAge,
    TimeSpan? ReceivedAge,
    DateTimeOffset? NextTransitionAt,
    bool LimitReached,
    ObservationSource? Source);

/// <summary>
/// Pure, clock-injected derivation of a window's state. THE STATE TABLE (first matching rule wins):
/// <code>
///  1. observation missing or invalid ............................. NoData
///  2. ResetsAt known and now &gt;= ResetsAt ......................... Elapsed   (overrides every age rule)
///  3. evidence age &lt; fresh bound ................................. Fresh     (capped to Quiet if ClockSkew or ImplausibleReset)
///  4. evidence age &lt; quiet bound ................................. Quiet
///  5. otherwise ................................................... Sleeping
/// bounds: Push  fresh=10 min, quiet=2 h  (age measured from ChangedAt, never from "bridge ran")
///         Polled fresh=20 min, quiet=2 h (age measured from the last successful read)
/// Numbers shown: Fresh/Quiet/Sleeping => the reading; NoData/Elapsed => none (never 0%, never the stale value).
/// </code>
/// </summary>
public static class WindowDeriver
{
    public static WindowView NoData(WindowKey key, WindowLabel label, WindowAnomalies anomalies = WindowAnomalies.None) =>
        new(key, label, null, WindowState.NoData, anomalies, null, null, null, null, null, null, null, null, null, false, null);

    public static WindowView Derive(WindowObservation obs, DateTimeOffset now, FreshnessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(obs);
        ArgumentNullException.ThrowIfNull(policy);

        if (!double.IsFinite(obs.UsedPercent) || obs.UsedPercent < 0 || obs.UsedPercent > 100)
        {
            return NoData(obs.Key, obs.Label, WindowAnomalies.InvalidObservation) with { GroupName = obs.GroupName, Source = obs.Source };
        }

        now = now.ToUniversalTime();
        DateTimeOffset evidence = obs.EvidenceAt.ToUniversalTime();
        DateTimeOffset received = obs.ReceivedAt.ToUniversalTime();
        DateTimeOffset? resetsAt = obs.ResetsAt?.ToUniversalTime();

        var anomalies = WindowAnomalies.None;
        if (evidence - now > policy.FutureTolerance)
        {
            anomalies |= WindowAnomalies.ClockSkew;
        }

        if (resetsAt is null)
        {
            anomalies |= WindowAnomalies.MissingResetTime;
        }
        else
        {
            TimeSpan horizon = obs.Duration is { } d && d > TimeSpan.Zero ? d : policy.MaxPlausibleWindow;
            if (resetsAt.Value > now + horizon + policy.ResetPlausibilityTolerance)
            {
                anomalies |= WindowAnomalies.ImplausibleReset;
            }
        }

        TimeSpan evidenceAge = Clamp(now - evidence);
        TimeSpan receivedAge = Clamp(now - received);
        (TimeSpan fresh, TimeSpan quiet) = obs.Source == ObservationSource.PushReported
            ? (policy.PushFreshWithin, policy.PushQuietWithin)
            : (policy.PolledFreshWithin, policy.PolledQuietWithin);

        bool elapsed = resetsAt is { } r && now >= r;
        WindowState state;
        if (elapsed)
        {
            state = WindowState.Elapsed;
        }
        else
        {
            state = evidenceAge < fresh ? WindowState.Fresh
                  : evidenceAge < quiet ? WindowState.Quiet
                  : WindowState.Sleeping;
            if (state == WindowState.Fresh && (anomalies & (WindowAnomalies.ClockSkew | WindowAnomalies.ImplausibleReset)) != 0)
            {
                state = WindowState.Quiet;
            }
        }

        // The first instant at which the state will differ, purely from the passage of time.
        DateTimeOffset? next = null;
        if (!elapsed)
        {
            if (state == WindowState.Fresh)
            {
                next = evidence + fresh;
            }
            else if (state == WindowState.Quiet)
            {
                next = evidence + quiet;
            }

            if (resetsAt is { } rr && (next is null || rr < next))
            {
                next = rr;
            }
        }

        bool displayable = state is WindowState.Fresh or WindowState.Quiet or WindowState.Sleeping;
        double? shown = displayable ? obs.UsedPercent : null;

        return new WindowView(
            obs.Key,
            obs.Label,
            obs.GroupName,
            state,
            anomalies,
            shown,
            shown is { } s ? 100d - s : null,
            obs.UsedPercent,
            resetsAt,
            displayable && resetsAt is { } ra ? ra - now : null,
            elapsed && resetsAt is { } re ? now - re : null,
            evidenceAge,
            receivedAge,
            next,
            displayable && obs.LimitReached,
            obs.Source);
    }

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
