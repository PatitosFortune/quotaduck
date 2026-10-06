using System.Text.Json;
using System.Text.Unicode;

namespace QuotaDuck.Core.Claude;

public enum ClaudeParseStatus
{
    /// <summary>Syntactically valid JSON object; per-window results say what (if anything) is usable.</summary>
    Ok = 0,

    Empty,
    TooLarge,
    InvalidUtf8,
    MalformedJson,

    /// <summary>Nesting deeper than <see cref="ClaudeStatusLineParser.MaxDepth"/> anywhere in the payload (even in skipped fields).</summary>
    TooDeep,

    NotAnObject,

    /// <summary>An allow-listed key appeared twice (ambiguous payload; rejected rather than guessing which one wins).</summary>
    DuplicateKey,
}

[Flags]
public enum ClaudeWindowIssues
{
    None = 0,
    MissingPercent = 1,
    InvalidPercent = 2,
    InvalidReset = 4,
    WrongShape = 8,
}

/// <summary>
/// One window as extracted from a payload. Only <see cref="Usable"/> windows may be merged; everything else is
/// reported (flags only — never raw text) so callers can count rejections without ever holding payload content.
/// </summary>
public readonly record struct ClaudeParsedWindow(bool Present, double? UsedPercent, DateTimeOffset? ResetsAt, ClaudeWindowIssues Issues)
{
    public bool Usable => Present && Issues == ClaudeWindowIssues.None && UsedPercent is not null;

    public ClaudeIncomingWindow? ToIncoming() => Usable ? new ClaudeIncomingWindow(UsedPercent, ResetsAt) : null;
}

public readonly record struct ClaudeStatusLineParse(ClaudeParseStatus Status, ClaudeParsedWindow FiveHour, ClaudeParsedWindow SevenDay);

/// <summary>
/// Pure, bounded, allow-list streaming parser for Claude Code's status-line JSON (Phase 1b §4 / bridge gate).
/// <list type="bullet">
/// <item>Reads ONLY <c>rate_limits.five_hour|seven_day.used_percentage|resets_at</c>; every other token is skipped while
/// streaming. No JsonDocument/JsonElement/DOM, no strings are materialised, nothing is retained after the call.</item>
/// <item>Bounded: input &lt;= <see cref="MaxPayloadBytes"/>, nesting &lt;= <see cref="MaxDepth"/>.</item>
/// <item>UTF-8 must be valid for the whole payload (a leading BOM is tolerated and ignored).</item>
/// <item>Division of validation: the parser rejects what is syntactically or representably invalid (wrong types, no
/// finite 0..100 percentage, non-integer or non-2000..2100 reset seconds). SEMANTIC plausibility against the clock
/// (reset in the past / further ahead than the window length) is the merge's job
/// (<see cref="ClaudeWindowMerger"/>), because the parser is clock-free.</item>
/// <item>Each window is independent: one absent/invalid window never affects the other.</item>
/// <item>No type coercion: a percentage given as a string is invalid, not "40".</item>
/// </list>
/// </summary>
public static class ClaudeStatusLineParser
{
    public const int MaxPayloadBytes = 256 * 1024;
    public const int MaxDepth = 32;

    // Reset instants must be whole seconds between 2000-01-01 and 2100-01-01 (UTC) to be representable/sane.
    private const long MinResetUnix = 946_684_800L;
    private const long MaxResetUnix = 4_102_444_800L;

    public static ClaudeStatusLineParse Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxPayloadBytes)
        {
            return Fail(ClaudeParseStatus.TooLarge);
        }

        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        if (utf8.Length == 0)
        {
            return Fail(ClaudeParseStatus.Empty);
        }

        if (!Utf8.IsValid(utf8))
        {
            return Fail(ClaudeParseStatus.InvalidUtf8);
        }

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxDepth });
        try
        {
            return ParseRoot(ref reader);
        }
        catch (JsonException)
        {
            // Error path only: classify depth overflow with a cheap O(n) structural scan (the reader's own message is not
            // reliable under resource-key trimming).
            return Fail(ExceedsDepth(utf8) ? ClaudeParseStatus.TooDeep : ClaudeParseStatus.MalformedJson);
        }
        catch (InvalidOperationException)
        {
            return Fail(ClaudeParseStatus.MalformedJson);
        }
    }

    private static ClaudeStatusLineParse Fail(ClaudeParseStatus status) => new(status, default, default);

    /// <summary>True if brackets nest deeper than <see cref="MaxDepth"/> outside of string literals.</summary>
    private static bool ExceedsDepth(ReadOnlySpan<byte> utf8)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        foreach (byte b in utf8)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == (byte)'\\')
                {
                    escaped = true;
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            switch (b)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'{':
                case (byte)'[':
                    if (++depth > MaxDepth)
                    {
                        return true;
                    }

                    break;
                case (byte)'}':
                case (byte)']':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
            }
        }

        return false;
    }

    private static ClaudeStatusLineParse ParseRoot(ref Utf8JsonReader r)
    {
        if (!r.Read())
        {
            return Fail(ClaudeParseStatus.MalformedJson);
        }

        if (r.TokenType != JsonTokenType.StartObject)
        {
            return Fail(ClaudeParseStatus.NotAnObject);
        }

        ClaudeParsedWindow five = default, seven = default;
        bool seenRate = false;
        while (true)
        {
            if (!r.Read())
            {
                return Fail(ClaudeParseStatus.MalformedJson);
            }

            if (r.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (r.ValueTextEquals("rate_limits"u8))
            {
                if (seenRate)
                {
                    return Fail(ClaudeParseStatus.DuplicateKey);
                }

                seenRate = true;
                if (!r.Read())
                {
                    return Fail(ClaudeParseStatus.MalformedJson);
                }

                ClaudeParseStatus st = ParseRateLimits(ref r, ref five, ref seven);
                if (st != ClaudeParseStatus.Ok)
                {
                    return Fail(st);
                }
            }
            else
            {
                if (!r.Read())
                {
                    return Fail(ClaudeParseStatus.MalformedJson);
                }

                r.Skip();
            }
        }

        // Anything after the closing brace (a second document, stray tokens) is malformed.
        if (r.Read())
        {
            return Fail(ClaudeParseStatus.MalformedJson);
        }

        return new ClaudeStatusLineParse(ClaudeParseStatus.Ok, five, seven);
    }

    private static ClaudeParseStatus ParseRateLimits(ref Utf8JsonReader r, ref ClaudeParsedWindow five, ref ClaudeParsedWindow seven)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.StartObject:
                break;
            case JsonTokenType.Null:
                return ClaudeParseStatus.Ok;
            default:
                r.Skip();                      // wrong shape: treat both windows as absent
                return ClaudeParseStatus.Ok;
        }

        bool seenFive = false, seenSeven = false;
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject)
            {
                return ClaudeParseStatus.Ok;
            }

            bool isFive = r.ValueTextEquals("five_hour"u8);
            bool isSeven = !isFive && r.ValueTextEquals("seven_day"u8);
            if (!isFive && !isSeven)
            {
                if (!r.Read())
                {
                    return ClaudeParseStatus.MalformedJson;
                }

                r.Skip();                      // e.g. spend_limit and any future/unknown window
                continue;
            }

            if (isFive ? seenFive : seenSeven)
            {
                return ClaudeParseStatus.DuplicateKey;
            }

            if (isFive)
            {
                seenFive = true;
            }
            else
            {
                seenSeven = true;
            }

            if (!r.Read())
            {
                return ClaudeParseStatus.MalformedJson;
            }

            ClaudeParsedWindow w = ParseWindow(ref r, out bool duplicate);
            if (duplicate)
            {
                return ClaudeParseStatus.DuplicateKey;
            }

            if (isFive)
            {
                five = w;
            }
            else
            {
                seven = w;
            }
        }

        return ClaudeParseStatus.MalformedJson;
    }

    private static ClaudeParsedWindow ParseWindow(ref Utf8JsonReader r, out bool duplicate)
    {
        duplicate = false;
        if (r.TokenType == JsonTokenType.Null)
        {
            return default;                    // explicitly absent
        }

        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            return new ClaudeParsedWindow(true, null, null, ClaudeWindowIssues.WrongShape);
        }

        double? pct = null;
        DateTimeOffset? reset = null;
        var issues = ClaudeWindowIssues.None;
        bool seenPct = false, seenReset = false;

        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (r.ValueTextEquals("used_percentage"u8))
            {
                if (seenPct)
                {
                    duplicate = true;
                    return default;
                }

                seenPct = true;
                r.Read();
                (pct, ClaudeWindowIssues pi) = ReadPercent(ref r);
                issues |= pi;
            }
            else if (r.ValueTextEquals("resets_at"u8))
            {
                if (seenReset)
                {
                    duplicate = true;
                    return default;
                }

                seenReset = true;
                r.Read();
                (reset, ClaudeWindowIssues ri) = ReadReset(ref r);
                issues |= ri;
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        if (pct is null && (issues & ClaudeWindowIssues.InvalidPercent) == 0)
        {
            issues |= ClaudeWindowIssues.MissingPercent;
        }

        return new ClaudeParsedWindow(true, pct, reset, issues);
    }

    private static (double?, ClaudeWindowIssues) ReadPercent(ref Utf8JsonReader r)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Null:
                return (null, ClaudeWindowIssues.MissingPercent);
            case JsonTokenType.Number:
                return r.TryGetDouble(out double d) && double.IsFinite(d) && d >= 0 && d <= 100
                    ? (d, ClaudeWindowIssues.None)
                    : (null, ClaudeWindowIssues.InvalidPercent);
            default:
                r.Skip();                      // string/bool/array/object: no coercion
                return (null, ClaudeWindowIssues.InvalidPercent);
        }
    }

    private static (DateTimeOffset?, ClaudeWindowIssues) ReadReset(ref Utf8JsonReader r)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Null:
                return (null, ClaudeWindowIssues.None);
            case JsonTokenType.Number:
                return r.TryGetInt64(out long s) && s >= MinResetUnix && s <= MaxResetUnix
                    ? (DateTimeOffset.FromUnixTimeSeconds(s), ClaudeWindowIssues.None)
                    : (null, ClaudeWindowIssues.InvalidReset);
            default:
                r.Skip();
                return (null, ClaudeWindowIssues.InvalidReset);
        }
    }
}
