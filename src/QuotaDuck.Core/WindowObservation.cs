namespace QuotaDuck.Core;

/// <summary>
/// How a reading reached us, which decides what counts as "evidence of freshness".
/// <list type="bullet">
/// <item><see cref="PushReported"/>: the provider tool pushed it (Claude status line). Only a CHANGE in the
/// reported data is evidence; a re-run of the hook proves nothing.</item>
/// <item><see cref="PolledRead"/>: we asked and the provider answered (Codex app-server). Every successful
/// read is a genuine observation, so evidence time = read time.</item>
/// </list>
/// </summary>
public enum ObservationSource
{
    PushReported = 1,
    PolledRead = 2,
}

/// <summary>
/// One provider-reported quota window, already validated/normalised by a provider adapter.
/// Plain data: the derivation code defends against bad values instead of throwing, so the UI path
/// can never crash on a corrupt record.
/// </summary>
/// <param name="EvidenceAt">PushReported: when the reported data last CHANGED (ChangedAt). PolledRead: read time.</param>
/// <param name="ReceivedAt">Last time any payload containing this window reached us (never evidence of freshness by itself).</param>
public sealed record WindowObservation(
    WindowKey Key,
    ObservationSource Source,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Duration,
    DateTimeOffset EvidenceAt,
    DateTimeOffset ReceivedAt,
    WindowLabel Label,
    string? GroupName = null,
    bool LimitReached = false);
