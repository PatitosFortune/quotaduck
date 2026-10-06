using System.Text;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;

namespace QuotaDuck.Core.Tests;

public class ClaudeStatusLineParserTests
{
    private static readonly string Reset3h = Unix(Epoch + TimeSpan.FromHours(3)).ToString();
    private static readonly string Reset7d = Unix(Epoch + TimeSpan.FromDays(7)).ToString();
    private static readonly string FiveOk = Win("23.5", Reset3h);
    private static readonly string SevenOk = Win("41.25", Reset7d);

    private static ClaudeStatusLineParse P(string json) => ClaudeStatusLineParser.Parse(Bytes(json));

    // ---- which windows are present ----

    [Fact]
    public void BothWindows_AreExtracted_ExactValues()
    {
        ClaudeStatusLineParse r = P(Payload(FiveOk, SevenOk));
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.True(r.FiveHour.Usable);
        Assert.Equal(23.5, r.FiveHour.UsedPercent);
        Assert.Equal(Epoch + TimeSpan.FromHours(3), r.FiveHour.ResetsAt);
        Assert.True(r.SevenDay.Usable);
        Assert.Equal(41.25, r.SevenDay.UsedPercent);
        Assert.Equal(Epoch + TimeSpan.FromDays(7), r.SevenDay.ResetsAt);
    }

    [Fact]
    public void FiveHourOnly_WeeklyAbsent()
    {
        ClaudeStatusLineParse r = P(Payload(FiveOk, null));
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.True(r.FiveHour.Usable);
        Assert.False(r.SevenDay.Present);
        Assert.Null(r.SevenDay.ToIncoming());
    }

    [Fact]
    public void WeeklyOnly_FiveHourAbsent()
    {
        ClaudeStatusLineParse r = P(Payload(null, SevenOk));
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.False(r.FiveHour.Present);
        Assert.True(r.SevenDay.Usable);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rate_limits\":null}")]
    [InlineData("{\"rate_limits\":{}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":null,\"seven_day\":null}}")]
    [InlineData("{\"rate_limits\":[1,2,3]}")]
    [InlineData("{\"rate_limits\":\"nope\"}")]
    [InlineData("{\"rate_limits\":7}")]
    public void NeitherWindow_IsOk_ButNothingUsable(string json)
    {
        ClaudeStatusLineParse r = P(json);
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.False(r.FiveHour.Usable);
        Assert.False(r.SevenDay.Usable);
        Assert.False(r.FiveHour.Present);
        Assert.False(r.SevenDay.Present);
    }

    [Fact]
    public void WindowsAreIndependent_OneInvalidDoesNotAffectTheOther()
    {
        ClaudeStatusLineParse r = P(Payload(Win("999", Reset3h), SevenOk));
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.False(r.FiveHour.Usable);
        Assert.True(r.FiveHour.Issues.HasFlag(ClaudeWindowIssues.InvalidPercent));
        Assert.True(r.SevenDay.Usable);
        Assert.Equal(41.25, r.SevenDay.UsedPercent);
    }

    // ---- allow-list soundness ----

    [Fact]
    public void UnknownExtraFields_AreSkipped_ResultIdenticalWithAndWithoutThem()
    {
        ClaudeStatusLineParse with = P(Payload(FiveOk, SevenOk, extras: true));
        ClaudeStatusLineParse without = P(Payload(FiveOk, SevenOk, extras: false));
        Assert.Equal(without, with);
        Assert.Equal(ClaudeParseStatus.Ok, with.Status);
    }

    [Fact]
    public void DecoyKeysAtOtherPaths_AreNeverRead()
    {
        // context_window.used_percentage (8) and spend_limit.used_percentage (1) must not leak into the result.
        ClaudeStatusLineParse r = P(Payload(null, null, extras: true));
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.False(r.FiveHour.Present);
        Assert.False(r.SevenDay.Present);
        // and the same key names nested inside a window object but under another property are ignored too
        ClaudeStatusLineParse nested = P("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":10,\"resets_at\":" + Reset3h + ",\"inner\":{\"used_percentage\":99,\"resets_at\":1}}}}");
        Assert.Equal(10, nested.FiveHour.UsedPercent);
        Assert.Equal(Epoch + TimeSpan.FromHours(3), nested.FiveHour.ResetsAt);
    }

    [Fact]
    public void UnknownWindowNames_AreIgnored()
    {
        ClaudeStatusLineParse r = P("{\"rate_limits\":{\"one_hour\":{\"used_percentage\":50,\"resets_at\":" + Reset3h + "},\"seven_day_opus\":{\"used_percentage\":60}}}");
        Assert.False(r.FiveHour.Present);
        Assert.False(r.SevenDay.Present);
    }

    [Fact]
    public void EscapedPropertyNames_AreMatchedByTheirDecodedValue()
    {
        string escaped = "{\"rate\\u005flimits\":{\"five\\u005fhour\":{\"used\\u005fpercentage\":12.5,\"resets\\u005fat\":" + Reset3h + "}}}";
        ClaudeStatusLineParse r = P(escaped);
        Assert.Equal(12.5, r.FiveHour.UsedPercent);
    }

    [Fact]
    public void ParsedWindow_HoldsNoStrings_StructuralPrivacyGuard()
    {
        // The parse result can only carry numbers/flags: no payload text can escape via the return value.
        Type[] types = [typeof(ClaudeParsedWindow), typeof(ClaudeStatusLineParse)];
        foreach (Type t in types)
        {
            Assert.DoesNotContain(t.GetProperties(), p => p.PropertyType == typeof(string) || p.PropertyType == typeof(byte[]) || p.PropertyType == typeof(object));
        }
    }

    // ---- bounded: size, depth ----

    [Fact]
    public void Oversized_IsRejected_ExactBoundaryAccepted()
    {
        string valid = Payload(FiveOk, null, extras: false);
        byte[] ok = new byte[ClaudeStatusLineParser.MaxPayloadBytes];
        Encoding.UTF8.GetBytes(valid).CopyTo(ok, 0);
        Array.Fill(ok, (byte)' ', valid.Length, ok.Length - valid.Length);            // whitespace padding to exactly the limit
        Assert.Equal(ClaudeParseStatus.Ok, ClaudeStatusLineParser.Parse(ok).Status);

        byte[] big = new byte[ClaudeStatusLineParser.MaxPayloadBytes + 1];
        Array.Fill(big, (byte)' ');
        ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(big);
        Assert.Equal(ClaudeParseStatus.TooLarge, r.Status);
        Assert.False(r.FiveHour.Present);
    }

    [Fact]
    public void Nesting_WithinTheLimit_InSkippedFields_IsFine()
    {
        // root object (1) + 31 nested levels = 32 = MaxDepth
        string json = Payload(FiveOk, SevenOk, rootExtras: "\"deep\":" + Nested(30));
        ClaudeStatusLineParse r = P(json);
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.True(r.FiveHour.Usable);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(200)]
    [InlineData(5000)]
    public void Nesting_BeyondTheLimit_RejectsThePayload_NothingExtracted(int levels)
    {
        ClaudeStatusLineParse r = P(Payload(FiveOk, SevenOk, rootExtras: "\"deep\":" + Nested(levels)));
        Assert.Equal(ClaudeParseStatus.TooDeep, r.Status);
        Assert.False(r.FiveHour.Present);
        Assert.False(r.SevenDay.Present);

        ClaudeStatusLineParse arrays = P(Payload(FiveOk, SevenOk, rootExtras: "\"deep\":" + NestedArrays(levels)));
        Assert.Equal(ClaudeParseStatus.TooDeep, arrays.Status);
    }

    [Fact]
    public void DeepNestingInsideAnAllowlistedWindow_IsAlsoBounded()
    {
        string w = "{\"used_percentage\":10,\"resets_at\":" + Reset3h + ",\"x\":" + Nested(60) + "}";
        Assert.Equal(ClaudeParseStatus.TooDeep, P(Payload(w, null)).Status);
    }

    // ---- malformed / truncated ----

    public static TheoryData<string> MalformedDocs => new()
    {
        "{",
        "{\"rate_limits\":",
        "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1,}}}",       // trailing comma
        "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1}}",         // missing brace
        "{\"a\":}",
        "{\"a\" 1}",
        "{'a':1}",
        "{\"a\":01}",                                                          // leading zero
        "{\"a\":1}{\"b\":2}",                                                  // two documents
        "{\"a\":1} garbage",
        "{\"a\":1}x",
        "{/*c*/\"a\":1}",                                                      // comments not allowed
        "{\"a\":NaN}",
        "{\"a\":+1}",
        "{\"a\":\"unterminated}",
        "{\"a\":\"bad \\x escape\"}",
        "   ",
        "\n\n",
        "{\"a\":[1,2,}",
    };

    [Theory]
    [MemberData(nameof(MalformedDocs))]
    public void MalformedJson_IsRejected_NothingExtracted(string json)
    {
        ClaudeStatusLineParse r = P(json);
        Assert.NotEqual(ClaudeParseStatus.Ok, r.Status);
        Assert.False(r.FiveHour.Present);
        Assert.False(r.SevenDay.Present);
    }

    [Fact]
    public void EveryTruncationOfAValidPayload_IsRejected_NeverThrows_NeverPartiallyAccepted()
    {
        byte[] full = Bytes(Payload(FiveOk, SevenOk));
        for (int len = 0; len < full.Length; len++)
        {
            ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(full.AsSpan(0, len));
            Assert.NotEqual(ClaudeParseStatus.Ok, r.Status);
            Assert.False(r.FiveHour.Present || r.SevenDay.Present, $"prefix length {len} leaked a window");
        }

        Assert.Equal(ClaudeParseStatus.Ok, ClaudeStatusLineParser.Parse(full).Status);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void NonObjectRoot_IsRejected(string json) => Assert.Equal(ClaudeParseStatus.NotAnObject, P(json).Status);

    [Fact]
    public void Empty_And_BomOnly_AreEmpty()
    {
        Assert.Equal(ClaudeParseStatus.Empty, ClaudeStatusLineParser.Parse([]).Status);
        Assert.Equal(ClaudeParseStatus.Empty, ClaudeStatusLineParser.Parse([0xEF, 0xBB, 0xBF]).Status);
    }

    [Fact]
    public void LeadingUtf8Bom_IsTolerated()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Bytes(Payload(FiveOk, null, extras: false))];
        Assert.True(ClaudeStatusLineParser.Parse(withBom).FiveHour.Usable);
    }

    // ---- invalid UTF-8 (anywhere, even inside skipped strings) ----

    public static TheoryData<string, byte[]> BadUtf8 => new()
    {
        { "lone 0xFF", [0xFF] },
        { "truncated 2-byte", [0xC3] },
        { "truncated 3-byte", [0xE2, 0x82] },
        { "overlong NUL", [0xC0, 0x80] },
        { "overlong slash", [0xC0, 0xAF] },
        { "UTF-16 surrogate half", [0xED, 0xA0, 0x80] },
        { "above U+10FFFF", [0xF4, 0x90, 0x80, 0x80] },
        { "stray continuation", [0x80] },
    };

    [Theory]
    [MemberData(nameof(BadUtf8))]
    public void InvalidUtf8_InASkippedString_RejectsTheWholePayload(string _, byte[] bad)
    {
        byte[] head = Bytes("{\"note\":\"");
        byte[] tail = Bytes("\",\"rate_limits\":{\"five_hour\":" + FiveOk + "}}");
        byte[] all = [.. head, .. bad, .. tail];
        ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(all);
        Assert.Equal(ClaudeParseStatus.InvalidUtf8, r.Status);
        Assert.False(r.FiveHour.Present);
    }

    [Fact]
    public void InvalidUtf8_InAKeyName_IsRejected()
    {
        byte[] all = [.. Bytes("{\"k"), 0xFF, .. Bytes("\":1,\"rate_limits\":{\"five_hour\":" + FiveOk + "}}")];
        Assert.Equal(ClaudeParseStatus.InvalidUtf8, ClaudeStatusLineParser.Parse(all).Status);
    }

    [Fact]
    public void ValidMultibyteUtf8_InSkippedFields_IsFine()
    {
        string json = Payload(FiveOk, null, rootExtras: "\"note\":\"héllo 日本語 🦆 \\u00e9\"");
        Assert.True(P(json).FiveHour.Usable);
    }

    // ---- percentages ----

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("100", 100.0)]
    [InlineData("0.0", 0.0)]
    [InlineData("99.999", 99.999)]
    [InlineData("2.35e1", 23.5)]
    [InlineData("1E1", 10.0)]
    public void ValidPercentages_AreAccepted(string raw, double expected)
    {
        ClaudeParsedWindow w = P(Payload(Win(raw, Reset3h), null)).FiveHour;
        Assert.True(w.Usable);
        Assert.Equal(expected, w.UsedPercent!.Value, 9);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("-1")]
    [InlineData("100.0001")]
    [InlineData("101")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("\"40\"")]            // strings are never coerced
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[40]")]
    public void InvalidPercentages_AreRejected(string raw)
    {
        ClaudeParsedWindow w = P(Payload(Win(raw, Reset3h), SevenOk)).FiveHour;
        Assert.False(w.Usable);
        Assert.True(w.Issues.HasFlag(ClaudeWindowIssues.InvalidPercent));
        Assert.Null(w.UsedPercent);
        Assert.Null(w.ToIncoming());
    }

    [Theory]
    [InlineData(null)]          // key omitted
    [InlineData("null")]
    public void MissingPercent_IsRejected_NotTreatedAsZero(string? raw)
    {
        ClaudeParsedWindow w = P(Payload(Win(raw, Reset3h), null)).FiveHour;
        Assert.True(w.Present);
        Assert.False(w.Usable);
        Assert.True(w.Issues.HasFlag(ClaudeWindowIssues.MissingPercent));
        Assert.Null(w.UsedPercent);
    }

    // ---- reset timestamps ----

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void MissingOrNullReset_IsAllowed_ResetIsSimplyUnknown(string? raw)
    {
        ClaudeParsedWindow w = P(Payload(Win("10", raw), null)).FiveHour;
        Assert.True(w.Usable);
        Assert.Null(w.ResetsAt);
    }

    [Theory]
    [InlineData("\"1893456000\"")]      // string
    [InlineData("1893456000.5")]        // fraction
    [InlineData("1.893456e9")]          // exponent form is not an integer token
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("946684799")]           // 1999-12-31
    [InlineData("4102444801")]          // just past 2100-01-01
    [InlineData("99999999999999999999")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void InvalidResetTimestamps_RejectTheWholeWindow(string raw)
    {
        ClaudeParsedWindow w = P(Payload(Win("10", raw), SevenOk)).FiveHour;
        Assert.False(w.Usable);
        Assert.True(w.Issues.HasFlag(ClaudeWindowIssues.InvalidReset));
        Assert.Null(w.ToIncoming());                  // never half-trusted: a window with a bad reset is not merged at all
    }

    [Theory]
    [InlineData("946684800", 2000)]
    [InlineData("4102444800", 2100)]
    public void ResetBoundaries_AreInclusive(string raw, int year)
    {
        ClaudeParsedWindow w = P(Payload(Win("10", raw), null)).FiveHour;
        Assert.True(w.Usable);
        Assert.Equal(year, w.ResetsAt!.Value.Year);
    }

    // ---- shape / duplicates ----

    [Theory]
    [InlineData("5")]
    [InlineData("\"x\"")]
    [InlineData("[1]")]
    [InlineData("true")]
    public void WrongWindowShape_IsPresentButRejected_SiblingUnaffected(string raw)
    {
        ClaudeStatusLineParse r = P(Payload(raw, SevenOk));
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.True(r.FiveHour.Present);
        Assert.True(r.FiveHour.Issues.HasFlag(ClaudeWindowIssues.WrongShape));
        Assert.False(r.FiveHour.Usable);
        Assert.True(r.SevenDay.Usable);
    }

    [Theory]
    [InlineData("{\"rate_limits\":{},\"rate_limits\":{}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1},\"five_hour\":{\"used_percentage\":2}}}")]
    [InlineData("{\"rate_limits\":{\"seven_day\":{\"used_percentage\":1},\"seven_day\":null}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1,\"used_percentage\":2}}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"resets_at\":1893456000,\"resets_at\":1893456001}}}")]
    public void DuplicateAllowlistedKeys_RejectThePayload(string json)
    {
        ClaudeStatusLineParse r = P(json);
        Assert.Equal(ClaudeParseStatus.DuplicateKey, r.Status);
        Assert.False(r.FiveHour.Present);
        Assert.False(r.SevenDay.Present);
    }

    [Fact]
    public void DuplicateNonAllowlistedKeys_AreHarmless()
    {
        ClaudeStatusLineParse r = P("{\"x\":1,\"x\":2,\"rate_limits\":{\"five_hour\":" + FiveOk + "}}");
        Assert.Equal(ClaudeParseStatus.Ok, r.Status);
        Assert.True(r.FiveHour.Usable);
    }

    [Fact]
    public void ToIncoming_FeedsTheMerge_EndToEnd()
    {
        ClaudeParsedWindow w = P(Payload(FiveOk, null)).FiveHour;
        ClaudeMergeResult m = ClaudeWindowMerger.Merge(ClaudeWindowKind.FiveHour, null, w.ToIncoming(), Epoch, FreshnessPolicy.Default);
        Assert.Equal(ClaudeMergeDecision.FirstObservation, m.Decision);
        Assert.Equal(23.5, m.Record!.UsedPercent);
    }
}
