using System.Text.Json;
using System.Text.Unicode;

namespace QuotaDuck.Core.Codex;

public enum CodexMessageKind
{
    Malformed = 0,

    /// <summary>Server notification (method, no id). Its content is never read.</summary>
    Notification = 1,

    /// <summary>Server-initiated request (method + id). The client must never answer or act on it.</summary>
    ServerRequest = 2,

    Response = 3,

    ErrorResponse = 4,
}

public enum CodexParseIssue
{
    None = 0,
    Empty,
    TooLarge,
    InvalidUtf8,
    MalformedJson,
    TooDeep,
    NotAnObject,
    DuplicateKey,
    BadId,
    BadMethod,
    BadShape,
    TooManyBuckets,
}

/// <summary>
/// One classified JSON-RPC line. Carries ONLY: the kind, the numeric id, an error CODE (never a message), and the allow-listed
/// quota buckets. For notifications/requests nothing but the kind and id is kept.
/// </summary>
public sealed record CodexMessage(
    CodexMessageKind Kind,
    CodexParseIssue Issue,
    long? Id,
    int? ErrorCode,
    IReadOnlyList<CodexBucket> Buckets,
    int SkippedWindows);

/// <summary>
/// Pure, bounded, allow-list streaming parser for one line from <c>codex app-server</c> (the <c>account/rateLimits/read</c> reply).
/// <list type="bullet">
/// <item>Retained: message kind, numeric <c>id</c>, <c>error.code</c>, and per bucket <c>limitId</c>, <c>limitName</c>, <c>primary</c> /
/// <c>secondary</c> {<c>usedPercent</c>, <c>windowDurationMins</c>, <c>resetsAt</c>} and whether <c>rateLimitReachedType</c> is set.</item>
/// <item>Skipped token by token and never materialised: <c>accountId</c>, <c>planType</c>, <c>credits</c> (incl. balance),
/// <c>rateLimitResetCredits</c> (reset-credit grants), <c>rateLimitUpsell</c> (banner/referral/request URLs), <c>userAgent</c>,
/// <c>codexHome</c>, <c>error.message</c>/<c>data</c>, notification/request <c>params</c>, and everything unknown.</item>
/// <item>Bounded: line &lt;= 1 MiB, nesting &lt;= 32, &lt;= 16 buckets; valid UTF-8 required; duplicate allow-listed keys reject the line;
/// no BOM tolerance (a server line must be plain JSON).</item>
/// <item>An invalid window (bad percentage, non-integer or out-of-range reset) is dropped and counted; its sibling survives.</item>
/// </list>
/// </summary>
public static class CodexResponseParser
{
    public const int MaxLineBytes = 1024 * 1024;
    public const int MaxDepth = 32;
    public const int MaxBuckets = 16;
    public const string FallbackLimitId = "default";

    private const long MinResetUnix = 946_684_800L;      // 2000-01-01
    private const long MaxResetUnix = 4_102_444_800L;    // 2100-01-01
    private const long MaxDurationMins = 10_000_000L;
    private const int MaxNameLength = 64;

    private sealed class State
    {
        public int Skipped;
        public bool Duplicate;
        public int BucketCount;
        public bool TooMany;
    }

    public static CodexMessage Parse(ReadOnlySpan<byte> line)
    {
        if (line.Length > MaxLineBytes)
        {
            return Fail(CodexParseIssue.TooLarge);
        }

        if (line.Length == 0)
        {
            return Fail(CodexParseIssue.Empty);
        }

        if (!Utf8.IsValid(line))
        {
            return Fail(CodexParseIssue.InvalidUtf8);
        }

        var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = MaxDepth });
        try
        {
            return ParseRoot(ref reader);
        }
        catch (JsonException)
        {
            return Fail(JsonDepthScan.Exceeds(line, MaxDepth) ? CodexParseIssue.TooDeep : CodexParseIssue.MalformedJson);
        }
        catch (InvalidOperationException)
        {
            return Fail(CodexParseIssue.MalformedJson);
        }
    }

    private static CodexMessage Fail(CodexParseIssue issue) =>
        new(CodexMessageKind.Malformed, issue, null, null, [], 0);

    private static CodexMessage ParseRoot(ref Utf8JsonReader r)
    {
        if (!r.Read())
        {
            return Fail(CodexParseIssue.MalformedJson);
        }

        if (r.TokenType != JsonTokenType.StartObject)
        {
            return Fail(CodexParseIssue.NotAnObject);
        }

        long? id = null;
        int? errorCode = null;
        bool seenId = false, seenMethod = false, seenResult = false, seenError = false;
        CodexBucket? legacy = null;
        var byId = new List<CodexBucket>();
        var st = new State();

        while (true)
        {
            if (!r.Read())
            {
                return Fail(CodexParseIssue.MalformedJson);
            }

            if (r.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (r.ValueTextEquals("id"u8))
            {
                if (seenId)
                {
                    return Fail(CodexParseIssue.DuplicateKey);
                }

                seenId = true;
                r.Read();
                if (r.TokenType != JsonTokenType.Number || !r.TryGetInt64(out long v))
                {
                    r.Skip();
                    return Fail(CodexParseIssue.BadId);        // we only ever send numeric ids, so only numeric ids may come back
                }

                id = v;
            }
            else if (r.ValueTextEquals("method"u8))
            {
                if (seenMethod)
                {
                    return Fail(CodexParseIssue.DuplicateKey);
                }

                seenMethod = true;
                r.Read();
                if (r.TokenType != JsonTokenType.String)
                {
                    r.Skip();
                    return Fail(CodexParseIssue.BadMethod);    // the method name itself is never materialised or retained
                }
            }
            else if (r.ValueTextEquals("result"u8))
            {
                if (seenResult)
                {
                    return Fail(CodexParseIssue.DuplicateKey);
                }

                seenResult = true;
                r.Read();
                if (r.TokenType != JsonTokenType.StartObject)
                {
                    r.Skip();
                    return Fail(CodexParseIssue.BadShape);
                }

                ParseResult(ref r, st, ref legacy, byId);
                if (st.Duplicate)
                {
                    return Fail(CodexParseIssue.DuplicateKey);
                }

                if (st.TooMany)
                {
                    return Fail(CodexParseIssue.TooManyBuckets);
                }
            }
            else if (r.ValueTextEquals("error"u8))
            {
                if (seenError)
                {
                    return Fail(CodexParseIssue.DuplicateKey);
                }

                seenError = true;
                r.Read();
                if (r.TokenType != JsonTokenType.StartObject)
                {
                    r.Skip();
                    return Fail(CodexParseIssue.BadShape);
                }

                errorCode = ParseErrorCode(ref r);
            }
            else
            {
                r.Read();
                r.Skip();                                       // params, jsonrpc, anything else: skipped unread
            }
        }

        if (r.Read())
        {
            return Fail(CodexParseIssue.MalformedJson);
        }

        if (seenMethod)
        {
            return new CodexMessage(seenId ? CodexMessageKind.ServerRequest : CodexMessageKind.Notification, CodexParseIssue.None, id, null, [], 0);
        }

        if (!seenId || (seenError && seenResult) || (!seenError && !seenResult))
        {
            return Fail(CodexParseIssue.BadShape);
        }

        if (seenError)
        {
            return new CodexMessage(CodexMessageKind.ErrorResponse, CodexParseIssue.None, id, errorCode, [], 0);
        }

        var buckets = new List<CodexBucket>(byId.Count + 1);
        if (legacy is not null)
        {
            // An id-less legacy bucket is only usable when no per-limit view exists (otherwise it would duplicate it).
            if (!string.IsNullOrEmpty(legacy.LimitId))
            {
                buckets.Add(legacy);
            }
            else if (byId.Count == 0)
            {
                buckets.Add(legacy with { LimitId = FallbackLimitId });
            }
        }

        buckets.AddRange(byId);                                 // later entries win in CodexObservations.FromRead
        return new CodexMessage(CodexMessageKind.Response, CodexParseIssue.None, id, null, buckets, st.Skipped);
    }

    private static int? ParseErrorCode(ref Utf8JsonReader r)
    {
        int? code = null;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("code"u8))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out int c))
                {
                    code = c;
                }
                else
                {
                    r.Skip();
                }
            }
            else
            {
                r.Read();
                r.Skip();                                       // error.message / error.data are never read
            }
        }

        return code;
    }

    private static void ParseResult(ref Utf8JsonReader r, State st, ref CodexBucket? legacy, List<CodexBucket> byId)
    {
        bool seenLegacy = false, seenMap = false;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("rateLimits"u8))
            {
                if (seenLegacy)
                {
                    st.Duplicate = true;
                    return;
                }

                seenLegacy = true;
                r.Read();
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    legacy = ParseBucket(ref r, st, null);
                    if (st.Duplicate)
                    {
                        return;
                    }
                }
                else
                {
                    r.Skip();
                }
            }
            else if (r.ValueTextEquals("rateLimitsByLimitId"u8))
            {
                if (seenMap)
                {
                    st.Duplicate = true;
                    return;
                }

                seenMap = true;
                r.Read();
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        string? key = ReadSafeName(ref r, isPropertyName: true);
                        r.Read();
                        if (r.TokenType == JsonTokenType.StartObject)
                        {
                            if (++st.BucketCount > MaxBuckets)
                            {
                                st.TooMany = true;
                                return;
                            }

                            byId.Add(ParseBucket(ref r, st, key));
                            if (st.Duplicate)
                            {
                                return;
                            }
                        }
                        else
                        {
                            r.Skip();
                        }
                    }
                }
                else
                {
                    r.Skip();
                }
            }
            else
            {
                r.Read();
                r.Skip();   // accountId, rateLimitResetCredits, rateLimitUpsell, ordinaryUsageAllowed, ... : never materialised
            }
        }
    }

    private static CodexBucket ParseBucket(ref Utf8JsonReader r, State st, string? keyId)
    {
        string? limitId = null, limitName = null;
        CodexWindowReading? primary = null, secondary = null;
        bool reached = false;
        bool seenId = false, seenName = false, seenPrimary = false, seenSecondary = false, seenReached = false;

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("limitId"u8))
            {
                if (seenId) { st.Duplicate = true; return Empty(); }
                seenId = true;
                r.Read();
                limitId = ReadSafeName(ref r, isPropertyName: false);
            }
            else if (r.ValueTextEquals("limitName"u8))
            {
                if (seenName) { st.Duplicate = true; return Empty(); }
                seenName = true;
                r.Read();
                limitName = ReadSafeName(ref r, isPropertyName: false);
            }
            else if (r.ValueTextEquals("primary"u8))
            {
                if (seenPrimary) { st.Duplicate = true; return Empty(); }
                seenPrimary = true;
                r.Read();
                primary = ParseWindow(ref r, st);
                if (st.Duplicate) { return Empty(); }
            }
            else if (r.ValueTextEquals("secondary"u8))
            {
                if (seenSecondary) { st.Duplicate = true; return Empty(); }
                seenSecondary = true;
                r.Read();
                secondary = ParseWindow(ref r, st);
                if (st.Duplicate) { return Empty(); }
            }
            else if (r.ValueTextEquals("rateLimitReachedType"u8))
            {
                if (seenReached) { st.Duplicate = true; return Empty(); }
                seenReached = true;
                r.Read();
                // only WHETHER a reached-type is set matters (null/absent = not reached); its text is never materialised
                reached = r.TokenType switch
                {
                    JsonTokenType.String => r.ValueSpan.Length > 0,
                    JsonTokenType.True or JsonTokenType.StartObject => true,
                    _ => false,
                };
                r.Skip();
            }
            else
            {
                r.Read();
                r.Skip();   // planType, credits, accountId-like fields, spendControlReached, individualLimit, normalModelSlug, ...
            }
        }

        // The per-limit map key is the identity of a by-id bucket; the bucket's own limitId is the fallback.
        return new CodexBucket(keyId ?? limitId, limitName, primary, secondary, reached);

        static CodexBucket Empty() => new(null, null, null, null, false);
    }

    private static CodexWindowReading? ParseWindow(ref Utf8JsonReader r, State st)
    {
        if (r.TokenType == JsonTokenType.Null)
        {
            return null;                                        // absent window: not a skip
        }

        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
            st.Skipped++;
            return null;
        }

        double? pct = null;
        long? duration = null, reset = null;
        bool pctBad = false, resetBad = false;
        bool seenPct = false, seenDur = false, seenReset = false;

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("usedPercent"u8))
            {
                if (seenPct) { st.Duplicate = true; return null; }
                seenPct = true;
                r.Read();
                if (r.TokenType == JsonTokenType.Number && r.TryGetDouble(out double d) && double.IsFinite(d) && d >= 0 && d <= 100)
                {
                    pct = d;
                }
                else
                {
                    pctBad = true;
                    r.Skip();
                }
            }
            else if (r.ValueTextEquals("windowDurationMins"u8))
            {
                if (seenDur) { st.Duplicate = true; return null; }
                seenDur = true;
                r.Read();
                if (r.TokenType == JsonTokenType.Number && r.TryGetInt64(out long m) && m > 0 && m <= MaxDurationMins)
                {
                    duration = m;                               // an unusable duration just means "unknown duration", not a bad window
                }
                else
                {
                    r.Skip();
                }
            }
            else if (r.ValueTextEquals("resetsAt"u8))
            {
                if (seenReset) { st.Duplicate = true; return null; }
                seenReset = true;
                r.Read();
                if (r.TokenType == JsonTokenType.Null)
                {
                    // reset unknown
                }
                else if (r.TokenType == JsonTokenType.Number && r.TryGetInt64(out long s) && s >= MinResetUnix && s <= MaxResetUnix)
                {
                    reset = s;
                }
                else
                {
                    resetBad = true;
                    r.Skip();
                }
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        if (pct is null || pctBad || resetBad)
        {
            st.Skipped++;
            return null;
        }

        return new CodexWindowReading(
            pct.Value,
            duration is { } mins ? TimeSpan.FromMinutes(mins) : null,
            reset is { } secs ? DateTimeOffset.FromUnixTimeSeconds(secs) : null);
    }

    /// <summary>A short, printable-ASCII-or-Unicode name (no control characters), or null if absent/too long/not a string. The
    /// value is skipped, not read, in every other case.</summary>
    private static string? ReadSafeName(ref Utf8JsonReader r, bool isPropertyName)
    {
        if (!isPropertyName && r.TokenType != JsonTokenType.String)
        {
            r.Skip();
            return null;
        }

        if (r.ValueSpan.Length > MaxNameLength * 4 && !r.ValueIsEscaped)
        {
            return null;
        }

        string? s = r.GetString();
        if (s is null || s.Length == 0 || s.Length > MaxNameLength)
        {
            return null;
        }

        foreach (char c in s)
        {
            if (char.IsControl(c))
            {
                return null;
            }
        }

        return s;
    }
}
