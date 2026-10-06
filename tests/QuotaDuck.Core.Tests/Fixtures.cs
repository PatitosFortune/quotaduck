using QuotaDuck.Core.Claude;
using QuotaDuck.Core.Codex;

namespace QuotaDuck.Core.Tests;

/// <summary>
/// Strictly synthetic fixtures. All instants are relative to <see cref="T0"/> (a fictional 2030-01-01 noon UTC);
/// all percentages are round invented numbers. No real provider data, account ids or payloads appear anywhere.
/// </summary>
internal static class Fx
{
    public static readonly DateTimeOffset T0 = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly FreshnessPolicy Policy = FreshnessPolicy.Default;

    public static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);

    public static TimeSpan H(double hours) => TimeSpan.FromHours(hours);

    /// <summary>A window observation as of <paramref name="now"/> (default T0), described by ages/offsets relative to it.</summary>
    public static WindowObservation Obs(
        ObservationSource source = ObservationSource.PushReported,
        double pct = 40,
        TimeSpan? evidenceAge = null,
        TimeSpan? receivedAge = null,
        TimeSpan? resetsIn = null,
        bool hasReset = true,
        TimeSpan? duration = null,
        WindowKey? key = null,
        bool limitReached = false,
        DateTimeOffset? now = null)
    {
        DateTimeOffset n = now ?? T0;
        TimeSpan ea = evidenceAge ?? TimeSpan.Zero;
        return new WindowObservation(
            key ?? WindowKey.ClaudeFiveHour,
            source,
            pct,
            hasReset ? n + (resetsIn ?? H(3)) : null,
            duration ?? H(5),
            n - ea,
            n - (receivedAge ?? ea),
            WindowLabels.ClaudeFiveHour,
            LimitReached: limitReached);
    }

    public static WindowObservation Polled(double pct = 40, TimeSpan? age = null, TimeSpan? resetsIn = null, string limit = "synthetic", CodexSlot slot = CodexSlot.Primary, bool limitReached = false)
    {
        Assert.True(WindowKey.TryCreateCodex(limit, slot, out WindowKey key));
        return Obs(ObservationSource.PolledRead, pct, age, null, resetsIn, true, H(5), key, limitReached);
    }

    public static ClaudeWindowRecord Rec(double pct, DateTimeOffset? resetsAt, DateTimeOffset changedAt, DateTimeOffset? receivedAt = null) =>
        new(pct, resetsAt, changedAt, receivedAt ?? changedAt);

    public static CodexWindowReading Reading(double pct, double? durationMinutes = 300, DateTimeOffset? resetsAt = null) =>
        new(pct, durationMinutes is { } m ? TimeSpan.FromMinutes(m) : null, resetsAt ?? T0 + H(3));
}
