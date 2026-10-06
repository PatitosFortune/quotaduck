namespace QuotaDuck.Core.Claude;

/// <summary>The two quota windows Claude Code's documented status line reports.</summary>
public enum ClaudeWindowKind
{
    FiveHour = 1,
    SevenDay = 2,
}

public static class ClaudeWindows
{
    public static WindowKey Key(ClaudeWindowKind kind) => kind switch
    {
        ClaudeWindowKind.FiveHour => WindowKey.ClaudeFiveHour,
        ClaudeWindowKind.SevenDay => WindowKey.ClaudeSevenDay,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static WindowLabel Label(ClaudeWindowKind kind) => kind switch
    {
        ClaudeWindowKind.FiveHour => WindowLabels.ClaudeFiveHour,
        ClaudeWindowKind.SevenDay => WindowLabels.ClaudeSevenDay,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Nominal window length (documented 5-hour / 7-day limits); used only for the reset plausibility check.</summary>
    public static TimeSpan Length(ClaudeWindowKind kind) => kind switch
    {
        ClaudeWindowKind.FiveHour => TimeSpan.FromHours(5),
        ClaudeWindowKind.SevenDay => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IReadOnlyList<ExpectedWindow> Expected { get; } =
    [
        new ExpectedWindow(WindowKey.ClaudeFiveHour, WindowLabels.ClaudeFiveHour),
        new ExpectedWindow(WindowKey.ClaudeSevenDay, WindowLabels.ClaudeSevenDay),
    ];
}

/// <summary>
/// What the bridge persists per window (Phase 1b §34 file schema). Deliberately holds NO account identity,
/// session id, path, model or any other payload field.
/// </summary>
/// <param name="ChangedAt">Last time the provider-reported (percent, reset) tuple changed, or a new epoch began. The only freshness evidence.</param>
/// <param name="ReceivedAt">Last time a payload containing this window arrived. Proves only that Claude Code ran the hook.</param>
/// <param name="LowerCandidate">Pending same-epoch lower value being counted (stale-cache suspicion), if any.</param>
/// <param name="LowerStreak">Consecutive identical lower reports seen for <paramref name="LowerCandidate"/>.</param>
public sealed record ClaudeWindowRecord(
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ChangedAt,
    DateTimeOffset ReceivedAt,
    double? LowerCandidate = null,
    int LowerStreak = 0);

/// <summary>A window as found in one status-line payload, after the parser extracted only the two allow-listed fields.</summary>
public readonly record struct ClaudeIncomingWindow(double? UsedPercent, DateTimeOffset? ResetsAt);

public static class ClaudeObservations
{
    public static WindowObservation ToObservation(ClaudeWindowKind kind, ClaudeWindowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new WindowObservation(
            ClaudeWindows.Key(kind),
            ObservationSource.PushReported,
            record.UsedPercent,
            record.ResetsAt,
            ClaudeWindows.Length(kind),
            EvidenceAt: record.ChangedAt,
            ReceivedAt: record.ReceivedAt,
            ClaudeWindows.Label(kind));
    }

    /// <summary>Builds the provider input from the persisted records (null = never reported).</summary>
    public static ProviderInput ToProviderInput(ClaudeWindowRecord? fiveHour, ClaudeWindowRecord? sevenDay, bool configured = true)
    {
        var windows = new List<WindowObservation>(2);
        if (fiveHour is not null)
        {
            windows.Add(ToObservation(ClaudeWindowKind.FiveHour, fiveHour));
        }

        if (sevenDay is not null)
        {
            windows.Add(ToObservation(ClaudeWindowKind.SevenDay, sevenDay));
        }

        return new ProviderInput(ProviderId.Claude, configured, windows, ClaudeWindows.Expected);
    }
}
