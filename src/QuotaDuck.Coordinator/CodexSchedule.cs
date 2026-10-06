namespace QuotaDuck.Coordinator;

internal sealed record CodexScheduleInput(
    DateTimeOffset Now,
    bool Enabled,
    bool Visible,
    bool InFlight,
    DateTimeOffset? LastAttemptStartedAt,
    DateTimeOffset? LastAttemptCompletedAt,
    int ConsecutiveFailures,
    IReadOnlyList<DateTimeOffset> KnownResets);

/// <summary>
/// The Codex pull policy as ONE pure function (no clock, no I/O), so every boundary is table-testable.
/// <list type="bullet">
/// <item>Nothing is scheduled while a read is in flight (re-evaluated on completion) or Codex is disabled.</item>
/// <item>Visible UI: first read immediately; then <see cref="CoordinatorPolicy.CodexVisibleInterval"/> after the previous read completed;
/// after failures, an exponential backoff from the previous completion instead.</item>
/// <item>Hidden UI: nothing — except ONE read per known reset, at reset + delay (a failed reset read is not retried while hidden).</item>
/// <item>Every read, whatever its trigger, starts at least <see cref="CoordinatorPolicy.CodexMinInterval"/> after the previous start.</item>
/// <item>Anchors in the future (the clock moved backwards) are clamped to now, so a backwards clock can never postpone a read.</item>
/// </list>
/// </summary>
internal static class CodexSchedule
{
    public static ScheduledCodexRead? Next(CodexScheduleInput i, CoordinatorPolicy p)
    {
        if (!i.Enabled || i.InFlight)
        {
            return null;
        }

        DateTimeOffset? started = Clamp(i.LastAttemptStartedAt, i.Now);
        DateTimeOffset? completed = Clamp(i.LastAttemptCompletedAt, i.Now);

        ScheduledCodexRead? best = null;
        void Offer(DateTimeOffset at, CodexTrigger t)
        {
            if (best is null || at < best.At)
            {
                best = new ScheduledCodexRead(at, t);
            }
        }

        if (i.Visible)
        {
            if (completed is null)
            {
                Offer(i.Now, CodexTrigger.InitialVisible);
            }
            else if (i.ConsecutiveFailures > 0)
            {
                Offer(completed.Value + Backoff(i.ConsecutiveFailures, p), CodexTrigger.Retry);
            }
            else
            {
                Offer(completed.Value + p.CodexVisibleInterval, CodexTrigger.VisiblePoll);
            }
        }

        foreach (DateTimeOffset reset in i.KnownResets)
        {
            DateTimeOffset target = reset + p.CodexResetReadDelay;
            if (started is null || started.Value < target)           // no read has STARTED at/after the post-reset moment yet
            {
                Offer(target, CodexTrigger.ResetRead);
            }
        }

        if (best is null)
        {
            return null;
        }

        return started is { } s && best.At < s + p.CodexMinInterval ? best with { At = s + p.CodexMinInterval } : best;
    }

    /// <summary>Delay after the n-th consecutive failure (n &gt;= 1): base * 2^(n-1), capped.</summary>
    public static TimeSpan Backoff(int consecutiveFailures, CoordinatorPolicy p)
    {
        double factor = Math.Pow(2, Math.Min(consecutiveFailures - 1, 30));
        double ticks = p.CodexBackoffBase.Ticks * factor;
        return ticks >= p.CodexBackoffCap.Ticks ? p.CodexBackoffCap : TimeSpan.FromTicks((long)ticks);
    }

    private static DateTimeOffset? Clamp(DateTimeOffset? anchor, DateTimeOffset now) =>
        anchor is { } a && a > now ? now : anchor;
}
