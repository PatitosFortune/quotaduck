namespace QuotaDuck.Core;

/// <summary>How trustworthy a window's human label is. Positional labels carry no provider semantics.</summary>
public enum LabelOrigin
{
    /// <summary>Fixed by a documented provider contract (Claude's five_hour / seven_day).</summary>
    Fixed = 1,

    /// <summary>Derived from a provider-supplied window duration (e.g. 300 min =&gt; "5-hour").</summary>
    DerivedFromDuration = 2,

    /// <summary>Only "Primary"/"Secondary": the provider gave no duration. Do not infer a meaning.</summary>
    Positional = 3,
}

public readonly record struct WindowLabel(string Text, LabelOrigin Origin);

public static class WindowLabels
{
    public static WindowLabel ClaudeFiveHour { get; } = new("5-hour", LabelOrigin.Fixed);

    public static WindowLabel ClaudeSevenDay { get; } = new("Weekly", LabelOrigin.Fixed);

    /// <summary>
    /// Exact-match duration mapping only (no fuzzy tolerance): an unknown duration must not be given a
    /// confident name. Falls back to Primary/Secondary.
    /// </summary>
    public static WindowLabel ForCodex(CodexSlot slot, TimeSpan? duration)
    {
        if (duration is { } d && d > TimeSpan.Zero)
        {
            string? text = null;
            foreach (var (minutes, name) in KnownDurations)
            {
                if (d == TimeSpan.FromMinutes(minutes))
                {
                    text = name;
                    break;
                }
            }

            if (text is not null)
            {
                return new WindowLabel(text, LabelOrigin.DerivedFromDuration);
            }
        }

        return new WindowLabel(slot == CodexSlot.Primary ? "Primary" : "Secondary", LabelOrigin.Positional);
    }

    private static readonly (long Minutes, string Name)[] KnownDurations =
    [
        (300, "5-hour"),
        (1_440, "Daily"),
        (10_080, "Weekly"),
        (43_200, "Monthly"),
        (525_600, "Annual"),
    ];
}
