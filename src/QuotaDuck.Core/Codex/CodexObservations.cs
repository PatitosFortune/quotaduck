namespace QuotaDuck.Core.Codex;

/// <summary>One Codex rate-limit window as reported (after the adapter's allow-list parse). Percent is the provider's own value.</summary>
public sealed record CodexWindowReading(double UsedPercent, TimeSpan? Duration, DateTimeOffset? ResetsAt);

/// <summary>
/// One Codex limit bucket (e.g. limit id "codex"). Contains ONLY allow-listed fields: no account id, plan, credits,
/// reset-credit grants or upsell data ever enter Core.
/// </summary>
public sealed record CodexBucket(
    string? LimitId,
    string? LimitName,
    CodexWindowReading? Primary,
    CodexWindowReading? Secondary,
    bool LimitReached);

public sealed record CodexObservationSet(IReadOnlyList<WindowObservation> Windows, int SkippedWindows);

public static class CodexObservations
{
    /// <summary>
    /// Converts ONE successful read into observations. A read replaces the previous Codex snapshot wholesale
    /// (windows absent from the new read are gone, unlike Claude's per-window merge). EvidenceAt = ReceivedAt = readAt.
    /// Windows that cannot be keyed safely or carry an invalid percentage are skipped and counted — never guessed.
    /// If the same key appears twice (legacy single-bucket view + by-id view) the last one wins.
    /// </summary>
    public static CodexObservationSet FromRead(IEnumerable<CodexBucket> buckets, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        readAt = readAt.ToUniversalTime();

        var byKey = new Dictionary<WindowKey, WindowObservation>();
        int skipped = 0;
        foreach (CodexBucket b in buckets)
        {
            foreach ((CodexSlot slot, CodexWindowReading? w) in new[] { (CodexSlot.Primary, b.Primary), (CodexSlot.Secondary, b.Secondary) })
            {
                if (w is null)
                {
                    continue;
                }

                if (!WindowKey.TryCreateCodex(b.LimitId, slot, out WindowKey key)
                    || !double.IsFinite(w.UsedPercent) || w.UsedPercent < 0 || w.UsedPercent > 100)
                {
                    skipped++;
                    continue;
                }

                TimeSpan? duration = w.Duration is { } d && d > TimeSpan.Zero ? d : null;
                byKey[key] = new WindowObservation(
                    key,
                    ObservationSource.PolledRead,
                    w.UsedPercent,
                    w.ResetsAt?.ToUniversalTime(),
                    duration,
                    EvidenceAt: readAt,
                    ReceivedAt: readAt,
                    WindowLabels.ForCodex(slot, duration),
                    GroupName: string.IsNullOrWhiteSpace(b.LimitName) ? b.LimitId : b.LimitName,
                    LimitReached: b.LimitReached);
            }
        }

        var list = new List<WindowObservation>(byKey.Values);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key.Id, b.Key.Id));
        return new CodexObservationSet(list, skipped);
    }

    public static ProviderInput ToProviderInput(
        CodexObservationSet? lastRead, ProviderAttempt? lastAttempt, bool configured = true) =>
        new(ProviderId.Codex, configured, lastRead?.Windows ?? [], ExpectedWindows: null, lastAttempt);
}
