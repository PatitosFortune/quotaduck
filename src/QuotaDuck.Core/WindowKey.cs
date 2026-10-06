namespace QuotaDuck.Core;

public enum ProviderId
{
    Claude = 1,
    Codex = 2,
}

/// <summary>Which Codex rate-limit slot a window came from. Slots are positional, not semantic.</summary>
public enum CodexSlot
{
    Primary = 1,
    Secondary = 2,
}

/// <summary>
/// Stable identity of one quota window. Ids are restricted to a safe character set because Codex-supplied
/// limit ids end up in keys, logs and UI automation names (path/log-injection hygiene).
/// Ordinal ordering of <see cref="Id"/> yields the intended display order
/// (five_hour &lt; seven_day; "{limit}:primary" &lt; "{limit}:secondary").
/// </summary>
public readonly record struct WindowKey
{
    private WindowKey(ProviderId provider, string id)
    {
        Provider = provider;
        Id = id;
    }

    public ProviderId Provider { get; }

    public string Id { get; }

    public static WindowKey ClaudeFiveHour { get; } = new(ProviderId.Claude, "five_hour");

    public static WindowKey ClaudeSevenDay { get; } = new(ProviderId.Claude, "seven_day");

    public static bool TryCreateCodex(string? limitId, CodexSlot slot, out WindowKey key)
    {
        key = default;
        if (!IsSafeToken(limitId) || !Enum.IsDefined(slot))
        {
            return false;
        }

        key = new WindowKey(ProviderId.Codex, limitId + ":" + (slot == CodexSlot.Primary ? "primary" : "secondary"));
        return true;
    }

    public override string ToString() => Id is null ? "(default)" : $"{Provider}/{Id}";

    private static bool IsSafeToken(string? s)
    {
        if (s is null || s.Length is 0 or > 48)
        {
            return false;
        }

        // Must start alphanumeric: rejects "..", ".hidden", "-flag" style ids outright.
        if (!char.IsAsciiLetterOrDigit(s[0]))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
