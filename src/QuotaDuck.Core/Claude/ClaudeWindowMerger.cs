namespace QuotaDuck.Core.Claude;

public enum ClaudeMergeDecision
{
    /// <summary>The payload did not contain this window; the stored record is untouched.</summary>
    NoIncoming = 0,

    RejectedMissingPercent,
    RejectedInvalidPercent,

    /// <summary>Reset time is already in the past beyond tolerance: Claude Code drops windows after reset, so this is a stale payload.</summary>
    RejectedResetInPast,

    /// <summary>Reset time is further ahead than the window length allows.</summary>
    RejectedImplausibleReset,

    FirstObservation,

    /// <summary>Same epoch, same percent: only <c>ReceivedAt</c> advances. NOT freshness evidence.</summary>
    Unchanged,

    /// <summary>Same epoch, higher percent: genuine new information.</summary>
    Advanced,

    /// <summary>Later reset time: a new window began (counts as new information even at the same percent).</summary>
    NewEpoch,

    /// <summary>Earlier reset time than stored: a stale session's cached payload. Ignored.</summary>
    OlderEpochIgnored,

    /// <summary>Same epoch, lower percent: another session's stale cache. Ignored (streak counted).</summary>
    LowerIgnored,

    /// <summary>The same lower value repeated <see cref="FreshnessPolicy.LowerAcceptCount"/> times: accepted as a real adjustment.</summary>
    AdjustedDown,
}

public sealed record ClaudeMergeResult(ClaudeWindowRecord? Record, ClaudeMergeDecision Decision, bool ShouldPersist);

/// <summary>
/// Pure merge of one incoming status-line window into the stored record (Phase 1b §34).
/// <code>
///  incoming absent ............................ keep, no write
///  percent missing / not in [0,100] ........... reject (keep)
///  reset in past (beyond tolerance) ........... reject (keep)         [Claude drops windows after reset]
///  reset &gt; now + length + tolerance ............ reject (keep)
///  nothing stored ............................. FirstObservation   changedAt = receivedAt = now
///  reset later  (&gt; tolerance) ................. NewEpoch           replace, changedAt = receivedAt = now
///  reset earlier (&gt; tolerance) ................ OlderEpochIgnored  receivedAt = now only
///  same epoch, percent equal .................. Unchanged          receivedAt = now only
///  same epoch, percent higher ................. Advanced           changedAt = receivedAt = now
///  same epoch, percent lower .................. LowerIgnored (streak++), or AdjustedDown after LowerAcceptCount identical reports
/// </code>
/// ShouldPersist coalesces disk writes: true on any material change, or when only ReceivedAt moved and the stored
/// ReceivedAt is at least <see cref="FreshnessPolicy.PersistReceivedAtEvery"/> old.
/// </summary>
public static class ClaudeWindowMerger
{
    private const double Epsilon = 1e-6;

    public static ClaudeMergeResult Merge(
        ClaudeWindowKind kind,
        ClaudeWindowRecord? existing,
        ClaudeIncomingWindow? incoming,
        DateTimeOffset now,
        FreshnessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        now = now.ToUniversalTime();

        // Stored timestamps never go backwards: if the system clock stepped back (NTP, resume) or another process's clock
        // reading interleaves, the record is stamped with the previous ReceivedAt instead, so ChangedAt <= ReceivedAt always
        // holds and the state file is never rejected by the strict decoder. Validation below still uses the true 'now'.
        DateTimeOffset stamp = existing is not null && existing.ReceivedAt > now ? existing.ReceivedAt : now;

        if (incoming is not { } inc)
        {
            return new ClaudeMergeResult(existing, ClaudeMergeDecision.NoIncoming, false);
        }

        if (inc.UsedPercent is not { } pct)
        {
            return Rejected(existing, ClaudeMergeDecision.RejectedMissingPercent);
        }

        if (!double.IsFinite(pct) || pct < 0 || pct > 100)
        {
            return Rejected(existing, ClaudeMergeDecision.RejectedInvalidPercent);
        }

        DateTimeOffset? reset = inc.ResetsAt?.ToUniversalTime();
        if (reset is { } r)
        {
            if (r <= now - policy.EpochTolerance)
            {
                return Rejected(existing, ClaudeMergeDecision.RejectedResetInPast);
            }

            if (r > now + ClaudeWindows.Length(kind) + policy.ResetPlausibilityTolerance)
            {
                return Rejected(existing, ClaudeMergeDecision.RejectedImplausibleReset);
            }
        }

        if (existing is null)
        {
            return new ClaudeMergeResult(
                new ClaudeWindowRecord(pct, reset, stamp, stamp), ClaudeMergeDecision.FirstObservation, true);
        }

        // Epoch relation. Unknown incoming reset => assume the stored epoch; unknown stored reset => adopt the incoming one.
        int epoch = (reset, existing.ResetsAt) switch
        {
            (null, _) => 0,
            (_, null) => 0,
            ({ } a, { } b) => a - b > policy.EpochTolerance ? 1 : b - a > policy.EpochTolerance ? -1 : 0,
        };
        DateTimeOffset? mergedReset = existing.ResetsAt ?? reset;

        if (epoch > 0)
        {
            return new ClaudeMergeResult(
                new ClaudeWindowRecord(pct, reset, stamp, stamp), ClaudeMergeDecision.NewEpoch, true);
        }

        if (epoch < 0)
        {
            return Touched(existing, ClaudeMergeDecision.OlderEpochIgnored, now, stamp, policy);
        }

        if (Math.Abs(pct - existing.UsedPercent) < Epsilon)
        {
            return Touched(
                existing with { ResetsAt = mergedReset, LowerCandidate = null, LowerStreak = 0 },
                ClaudeMergeDecision.Unchanged, now, stamp, policy, forceWrite: existing.ResetsAt != mergedReset || existing.LowerStreak != 0);
        }

        if (pct > existing.UsedPercent)
        {
            return new ClaudeMergeResult(
                new ClaudeWindowRecord(pct, mergedReset, stamp, stamp), ClaudeMergeDecision.Advanced, true);
        }

        // Same epoch, lower percentage: suspect a stale cache from another session.
        int streak = existing.LowerCandidate is { } c && Math.Abs(c - pct) < Epsilon ? existing.LowerStreak + 1 : 1;
        if (streak >= policy.LowerAcceptCount)
        {
            return new ClaudeMergeResult(
                new ClaudeWindowRecord(pct, mergedReset, stamp, stamp), ClaudeMergeDecision.AdjustedDown, true);
        }

        return new ClaudeMergeResult(
            existing with { ResetsAt = mergedReset, ReceivedAt = stamp, LowerCandidate = pct, LowerStreak = streak },
            ClaudeMergeDecision.LowerIgnored, true);
    }

    private static ClaudeMergeResult Rejected(ClaudeWindowRecord? existing, ClaudeMergeDecision d) => new(existing, d, false);

    /// <summary>Only ReceivedAt moves; write at most once per coalescing interval (judged against the true clock).</summary>
    private static ClaudeMergeResult Touched(
        ClaudeWindowRecord existing, ClaudeMergeDecision decision, DateTimeOffset now, DateTimeOffset stamp, FreshnessPolicy policy, bool forceWrite = false)
    {
        bool due = now - existing.ReceivedAt >= policy.PersistReceivedAtEvery;
        return new ClaudeMergeResult(existing with { ReceivedAt = stamp }, decision, forceWrite || due);
    }
}
