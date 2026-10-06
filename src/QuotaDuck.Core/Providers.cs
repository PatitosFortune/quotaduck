namespace QuotaDuck.Core;

/// <summary>Coarse failure classes for a provider read attempt (Codex). Details stay in the adapter, never raw payloads.</summary>
public enum ProviderErrorKind
{
    /// <summary>Provider tool not installed / package not found.</summary>
    NotFound = 1,

    /// <summary>Executable failed path/signature/signer validation (never launched).</summary>
    ValidationFailed = 2,

    /// <summary>Process could not be started.</summary>
    StartFailed = 3,

    /// <summary>No handshake or read response within the deadline.</summary>
    Timeout = 4,

    /// <summary>Provider answered with an error (e.g. not signed in).</summary>
    Rejected = 5,

    /// <summary>Response did not match the expected shape.</summary>
    Malformed = 6,

    Unknown = 99,
}

public sealed record ProviderAttempt(DateTimeOffset At, ProviderErrorKind? Error);

/// <summary>A window a provider is expected to report, so the UI can show "not reported yet" instead of omitting it.</summary>
public sealed record ExpectedWindow(WindowKey Key, WindowLabel Label);

public sealed record ProviderInput(
    ProviderId Provider,
    bool Configured,
    IReadOnlyList<WindowObservation> Windows,
    IReadOnlyList<ExpectedWindow>? ExpectedWindows = null,
    ProviderAttempt? LastAttempt = null);

/// <summary>
/// Best window state across the provider. Values mirror <see cref="WindowState"/> shifted by one.
/// <b>For a single tray/status indicator ONLY.</b> Individual window states (<see cref="WindowView.State"/>) are
/// independent and authoritative: a Fresh window never makes a Sleeping sibling look Fresh, and an Elapsed window
/// stays Elapsed (no number) however fresh its siblings are. Nothing numeric is ever rolled up.
/// </summary>
public enum ProviderRollup
{
    NotConfigured = 0,
    NoData = 1,
    Fresh = 2,
    Quiet = 3,
    Sleeping = 4,
    Elapsed = 5,
}

/// <summary>Why the provider looks the way it does. Machine-readable; the UI maps these to honest copy.</summary>
public enum ProviderReason
{
    None = 0,
    NotConfigured = 1,

    /// <summary>Claude has never reported. Must read "Waiting for Claude Code in a terminal" — never 0%.</summary>
    WaitingForTerminalClaudeCode = 2,

    /// <summary>Claude data exists but nothing has changed for hours (idle terminal, or usage happening elsewhere).</summary>
    ClaudeNoRecentReport = 3,

    /// <summary>Every Claude window has reached its reset time; the next terminal report will refill the pond.</summary>
    AwaitingNextClaudeReport = 4,

    CodexNotReadYet = 5,

    /// <summary>Codex reads are old without a recorded failure (app hidden / paused).</summary>
    CodexReadsPaused = 6,

    /// <summary>Every Codex window has reached its reset time; a read is due.</summary>
    AwaitingCodexRead = 7,

    /// <summary>The last read failed and there is no earlier data at all.</summary>
    ReadFailedNoData = 8,

    /// <summary>The last read failed; older data (aging normally) is still shown.</summary>
    LastReadFailed = 9,
}

public sealed record ProviderView(
    ProviderId Provider,
    ProviderRollup Rollup,
    ProviderReason Reason,
    ProviderErrorKind? Error,
    IReadOnlyList<WindowView> Windows,
    WindowKey? MostConstrained,
    bool LimitReached,
    TimeSpan? FreshestEvidenceAge,
    TimeSpan? FreshestReceivedAge,
    DateTimeOffset? NextTransitionAt);

public sealed record QuotaView(ProviderView Claude, ProviderView Codex, DateTimeOffset? NextTransitionAt);

/// <summary>
/// Provider-independent state derivation. Each provider is derived from its OWN input only, so one provider's
/// staleness can never leak into the other (Phase 1 §8 requirement).
/// <code>
///  WINDOWS are derived one by one and are never influenced by siblings or by the roll-up (authoritative per-window state).
///  ROLLUP  = tray/status-indicator summary ONLY: NotConfigured if !Configured; else NoData if no window has data; else best (lowest) of the window states.
///  REASON  | Claude                         | Codex
///  NotConfigured | NotConfigured            | NotConfigured
///  NoData  | WaitingForTerminalClaudeCode   | error ? ReadFailedNoData : CodexNotReadYet
///  Fresh   | None                           | error ? LastReadFailed : None
///  Quiet   | None                           | error ? LastReadFailed : None
///  Sleeping| ClaudeNoRecentReport           | error ? LastReadFailed : CodexReadsPaused
///  Elapsed | AwaitingNextClaudeReport       | error ? LastReadFailed : AwaitingCodexRead
/// </code>
/// </summary>
public static class ProviderDeriver
{
    public static ProviderView Derive(ProviderInput input, DateTimeOffset now, FreshnessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);

        // Windows belonging to another provider are ignored; duplicate keys: the last one wins.
        var byKey = new Dictionary<WindowKey, WindowObservation>();
        foreach (WindowObservation o in input.Windows)
        {
            if (o.Key.Provider == input.Provider)
            {
                byKey[o.Key] = o;
            }
        }

        var views = new List<WindowView>(byKey.Count);
        foreach (WindowObservation o in byKey.Values)
        {
            views.Add(WindowDeriver.Derive(o, now, policy));
        }

        if (input.ExpectedWindows is { } expected)
        {
            foreach (ExpectedWindow e in expected)
            {
                if (e.Key.Provider == input.Provider && !byKey.ContainsKey(e.Key))
                {
                    views.Add(WindowDeriver.NoData(e.Key, e.Label));
                }
            }
        }

        views.Sort(static (a, b) => string.CompareOrdinal(a.Key.Id, b.Key.Id));

        ProviderErrorKind? error = input.LastAttempt?.Error;
        ProviderRollup rollup = RollUp(input.Configured, views);
        ProviderReason reason = ReasonFor(input.Provider, rollup, error);

        TimeSpan? freshestEvidence = null, freshestReceived = null;
        DateTimeOffset? next = null;
        bool limitReached = false;
        foreach (WindowView v in views)
        {
            if (v.State != WindowState.NoData)
            {
                freshestEvidence = Min(freshestEvidence, v.EvidenceAge);
                freshestReceived = Min(freshestReceived, v.ReceivedAge);
            }

            if (v.NextTransitionAt is { } t && (next is null || t < next))
            {
                next = t;
            }

            limitReached |= v.LimitReached;
        }

        return new ProviderView(
            input.Provider, rollup, reason, error, views, input.Configured ? MostConstrained(views) : null,
            input.Configured && limitReached, freshestEvidence, freshestReceived, input.Configured ? next : null);
    }

    /// <summary>The displayable window with the highest used percentage (ties: earliest reset, then key order).</summary>
    public static WindowKey? MostConstrained(IReadOnlyList<WindowView> views)
    {
        WindowView? best = null;
        foreach (WindowView v in views)
        {
            if (v.DisplayedUsedPercent is not { } p)
            {
                continue;
            }

            if (best is null || p > best.DisplayedUsedPercent
                || (p == best.DisplayedUsedPercent && Earlier(v.ResetsAt, best.ResetsAt)))
            {
                best = v;
            }
        }

        return best?.Key;
    }

    private static bool Earlier(DateTimeOffset? a, DateTimeOffset? b) => (a, b) switch
    {
        (null, _) => false,
        (_, null) => true,
        _ => a < b,
    };

    private static ProviderRollup RollUp(bool configured, List<WindowView> views)
    {
        if (!configured)
        {
            return ProviderRollup.NotConfigured;
        }

        int best = int.MaxValue;
        foreach (WindowView v in views)
        {
            if (v.State != WindowState.NoData && (int)v.State < best)
            {
                best = (int)v.State;
            }
        }

        return best == int.MaxValue ? ProviderRollup.NoData : (ProviderRollup)(best + 1);
    }

    private static ProviderReason ReasonFor(ProviderId provider, ProviderRollup rollup, ProviderErrorKind? error)
    {
        if (rollup == ProviderRollup.NotConfigured)
        {
            return ProviderReason.NotConfigured;
        }

        if (provider == ProviderId.Claude)
        {
            return rollup switch
            {
                ProviderRollup.NoData => ProviderReason.WaitingForTerminalClaudeCode,
                ProviderRollup.Sleeping => ProviderReason.ClaudeNoRecentReport,
                ProviderRollup.Elapsed => ProviderReason.AwaitingNextClaudeReport,
                _ => ProviderReason.None,
            };
        }

        return rollup switch
        {
            ProviderRollup.NoData => error is null ? ProviderReason.CodexNotReadYet : ProviderReason.ReadFailedNoData,
            _ when error is not null => ProviderReason.LastReadFailed,
            ProviderRollup.Sleeping => ProviderReason.CodexReadsPaused,
            ProviderRollup.Elapsed => ProviderReason.AwaitingCodexRead,
            _ => ProviderReason.None,
        };
    }

    private static TimeSpan? Min(TimeSpan? a, TimeSpan? b) => (a, b) switch
    {
        (null, _) => b,
        (_, null) => a,
        _ => a < b ? a : b,
    };
}

public static class QuotaDeriver
{
    public static QuotaView Derive(ProviderInput claude, ProviderInput codex, DateTimeOffset now, FreshnessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(codex);
        if (claude.Provider != ProviderId.Claude)
        {
            throw new ArgumentException("expected Claude input", nameof(claude));
        }

        if (codex.Provider != ProviderId.Codex)
        {
            throw new ArgumentException("expected Codex input", nameof(codex));
        }

        ProviderView c = ProviderDeriver.Derive(claude, now, policy);
        ProviderView x = ProviderDeriver.Derive(codex, now, policy);
        DateTimeOffset? next = (c.NextTransitionAt, x.NextTransitionAt) switch
        {
            (null, var b) => b,
            (var a, null) => a,
            (var a, var b) => a < b ? a : b,
        };
        return new QuotaView(c, x, next);
    }
}
