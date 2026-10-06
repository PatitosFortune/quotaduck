using System.Text;
using System.Text.Json;
using QuotaDuck.Core.Codex;

namespace QuotaDuck.Core.Tests;

/// <summary>The allow-list parser for `account/rateLimits/read` replies. Synthetic payloads only (fictional 2030 epoch, CANARY_ values).</summary>
public class CodexResponseParserTests
{
    private const long Reset5h = 1893456000;      // 2030-01-01T00:00:00Z
    private const long ResetWk = 1893974400;

    private static string Window(string pct, string dur = "300", long reset = Reset5h) =>
        $"{{\"usedPercent\":{pct},\"windowDurationMins\":{dur},\"resetsAt\":{reset}}}";

    private static string Bucket(string id = "codex", string name = "null", string? primary = null, string? secondary = null, string reached = "null") =>
        "{\"limitId\":" + (id == "null" ? "null" : $"\"{id}\"") + ",\"limitName\":" + name +
        ",\"primary\":" + (primary ?? Window("42")) + ",\"secondary\":" + (secondary ?? Window("16", "10080", ResetWk)) +
        ",\"credits\":{\"balance\":\"CANARY_BALANCE\"},\"planType\":\"CANARY_PLAN\",\"rateLimitReachedType\":" + reached + "}";

    private static string Reply(string? legacy = null, string? byId = null, string extra = "") =>
        "{\"id\":2,\"result\":{" +
        (legacy is null ? "" : "\"rateLimits\":" + legacy + ",") +
        (byId is null ? "" : "\"rateLimitsByLimitId\":" + byId + ",") +
        "\"accountId\":\"CANARY_ACCOUNT\",\"rateLimitResetCredits\":{\"g\":[\"CANARY_GRANT\"]},\"rateLimitUpsell\":{\"url\":\"CANARY_URL\"}" + extra + "}}";

    private static CodexMessage P(string json) => CodexResponseParser.Parse(Encoding.UTF8.GetBytes(json));

    private static CodexMessage Good() => P(Reply(Bucket(), "{\"codex\":" + Bucket() + "}"));

    // ---- happy path ----

    [Fact]
    public void GoodReply_YieldsTheAllowlistedWindows()
    {
        CodexMessage m = Good();
        Assert.Equal(CodexMessageKind.Response, m.Kind);
        Assert.Equal(2, m.Id);
        Assert.Equal(CodexParseIssue.None, m.Issue);
        Assert.All(m.Buckets, b => Assert.Equal("codex", b.LimitId));
        CodexBucket b = m.Buckets[^1];
        Assert.Equal(42, b.Primary!.UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(300), b.Primary.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Reset5h), b.Primary.ResetsAt);
        Assert.Equal(16, b.Secondary!.UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(10080), b.Secondary.Duration);
        Assert.False(b.LimitReached);
    }

    [Fact]
    public void GoodReply_FlowsIntoCoreObservations_WithOneWindowPerSlot()
    {
        var set = CodexObservations.FromRead(Good().Buckets, DateTimeOffset.FromUnixTimeSeconds(Reset5h - 3600));
        Assert.Equal(2, set.Windows.Count);
        Assert.Equal(0, set.SkippedWindows);
    }

    [Fact]
    public void SensitiveExtras_NeverEnterTheParsedModel_NotEvenInTheObjectGraph()
    {
        CodexMessage m = P(Reply(Bucket(), "{\"codex\":" + Bucket() + "}", ",\"spendControlReached\":{\"x\":\"CANARY_SPEND\"},\"normalModelSlug\":\"CANARY_MODEL\",\"individualLimit\":\"CANARY_LIMIT\""));
        string dump = JsonSerializer.Serialize(m) + m + string.Join("|", m.Buckets);
        Assert.DoesNotContain("CANARY", dump, StringComparison.Ordinal);
        // and the type itself has nowhere to put them: only these members exist
        Assert.Equal(
            ["Buckets", "ErrorCode", "Id", "Issue", "Kind", "SkippedWindows"],
            typeof(CodexMessage).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["LimitId", "LimitName", "LimitReached", "Primary", "Secondary"],
            typeof(CodexBucket).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["Duration", "ResetsAt", "UsedPercent"], typeof(CodexWindowReading).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void LegacyBucketWithoutAnId_IsUsedAsDefault_OnlyWhenNoPerLimitViewExists()
    {
        CodexMessage alone = P(Reply(Bucket("null")));
        Assert.Equal("default", Assert.Single(alone.Buckets).LimitId);
        CodexMessage both = P(Reply(Bucket("null"), "{\"codex\":" + Bucket() + "}"));
        Assert.Equal("codex", Assert.Single(both.Buckets).LimitId);
    }

    [Fact]
    public void PerLimitMapKey_IsTheIdentity_AndMultipleBucketsSurvive()
    {
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket("codex") + ",\"codex_other\":" + Bucket("something-else") + "}"));
        Assert.Equal("codex,codex_other", string.Join(",", m.Buckets.Select(b => b.LimitId)));
    }

    [Theory]
    [InlineData("\"rate_limit_reached\"", true)]
    [InlineData("{\"k\":1}", true)]
    [InlineData("true", true)]
    [InlineData("null", false)]
    [InlineData("\"\"", false)]
    [InlineData("false", false)]
    [InlineData("3", false)]
    public void RateLimitReached_IsAFlagOnly(string raw, bool expected)
    {
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket(reached: raw) + "}"));
        Assert.Equal(expected, Assert.Single(m.Buckets).LimitReached);
    }

    [Fact]
    public void HugeSkippedFields_AreSkippedNotMaterialised()
    {
        string huge = new('x', 900_000);
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket() + "}", ",\"accountId2\":\"" + huge + "\""));
        Assert.Equal(CodexMessageKind.Response, m.Kind);
        Assert.Single(m.Buckets);
    }

    // ---- message classes ----

    [Fact]
    public void Notification_HasNoContentRetained()
    {
        CodexMessage m = P("{\"method\":\"thread/started\",\"params\":{\"secret\":\"CANARY_PARAMS\"}}");
        Assert.Equal(CodexMessageKind.Notification, m.Kind);
        Assert.Null(m.Id);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(m), StringComparison.Ordinal);
    }

    [Fact]
    public void ServerRequest_IsClassified_AndItsContentIgnored()
    {
        CodexMessage m = P("{\"id\":99,\"method\":\"item/tool/call\",\"params\":{\"cmd\":\"CANARY_CMD\"}}");
        Assert.Equal(CodexMessageKind.ServerRequest, m.Kind);
        Assert.Equal(99, m.Id);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(m), StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorResponse_KeepsOnlyTheNumericCode()
    {
        CodexMessage m = P("{\"id\":2,\"error\":{\"code\":-32603,\"message\":\"CANARY_MESSAGE\",\"data\":{\"t\":\"CANARY_DATA\"}}}");
        Assert.Equal(CodexMessageKind.ErrorResponse, m.Kind);
        Assert.Equal(-32603, m.ErrorCode);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(m), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"id\":2}")]                                                      // neither result nor error
    [InlineData("{\"id\":2,\"result\":{},\"error\":{\"code\":1}}")]                 // both
    [InlineData("{\"result\":{}}")]                                                 // no id
    [InlineData("{\"id\":2,\"result\":[1,2]}")]                                      // result not an object
    [InlineData("{\"id\":2,\"error\":\"nope\"}")]                                    // error not an object
    public void WrongShape_IsMalformed(string json) => Assert.Equal(CodexMessageKind.Malformed, P(json).Kind);

    [Theory]
    [InlineData("{\"id\":\"2\",\"result\":{}}")]
    [InlineData("{\"id\":2.5,\"result\":{}}")]
    [InlineData("{\"id\":null,\"result\":{}}")]
    [InlineData("{\"id\":true,\"result\":{}}")]
    [InlineData("{\"id\":[2],\"result\":{}}")]
    public void NonIntegerIds_AreRejected_WeOnlyEverSendIntegers(string json) =>
        Assert.Equal(CodexParseIssue.BadId, P(json).Issue);

    // ---- bounds and hostile input ----

    [Fact]
    public void Empty_TooLarge_InvalidUtf8_Bom_NotObject_Trailing_AreRejected()
    {
        Assert.Equal(CodexParseIssue.Empty, CodexResponseParser.Parse([]).Issue);
        Assert.Equal(CodexParseIssue.TooLarge, CodexResponseParser.Parse(new byte[CodexResponseParser.MaxLineBytes + 1]).Issue);
        Assert.Equal(CodexParseIssue.InvalidUtf8, CodexResponseParser.Parse([0x7B, 0xC3, 0x28, 0x7D]).Issue);
        Assert.Equal(CodexMessageKind.Malformed, CodexResponseParser.Parse([0xEF, 0xBB, 0xBF, .. "{\"id\":2,\"result\":{}}"u8]).Kind);
        Assert.Equal(CodexParseIssue.NotAnObject, P("[1,2]").Issue);
        Assert.Equal(CodexParseIssue.NotAnObject, P("42").Issue);
        Assert.Equal(CodexMessageKind.Malformed, P("{\"id\":2,\"result\":{}} {\"id\":3}").Kind);
        Assert.Equal(CodexMessageKind.Malformed, P("{\"id\":2,\"result\":{}").Kind);
        Assert.Equal(CodexMessageKind.Malformed, P("not json").Kind);
    }

    [Fact]
    public void Nesting_AtTheLimit_Parses_BeyondIt_IsTooDeep_EvenInsideSkippedFields()
    {
        string Nest(int n) => new string('[', n) + new string(']', n);
        // root object = depth 1, "params" value starts at depth 2
        Assert.Equal(CodexMessageKind.Notification, P("{\"method\":\"m\",\"params\":" + Nest(CodexResponseParser.MaxDepth - 1) + "}").Kind);
        CodexMessage deep = P("{\"method\":\"m\",\"params\":" + Nest(CodexResponseParser.MaxDepth) + "}");
        Assert.Equal(CodexMessageKind.Malformed, deep.Kind);
        Assert.Equal(CodexParseIssue.TooDeep, deep.Issue);
        Assert.Equal(CodexParseIssue.TooDeep, P("{\"method\":\"m\",\"params\":" + Nest(5000) + "}").Issue);
    }

    [Theory]
    [InlineData("{\"id\":2,\"id\":3,\"result\":{}}")]
    [InlineData("{\"id\":2,\"result\":{},\"result\":{}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{},\"rateLimits\":{}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{},\"rateLimitsByLimitId\":{}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"a\",\"limitId\":\"b\"}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":null,\"primary\":null}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"usedPercent\":2}}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"windowDurationMins\":1,\"windowDurationMins\":2}}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"resetsAt\":null,\"resetsAt\":null}}}}")]
    [InlineData("{\"id\":2,\"result\":{\"rateLimits\":{\"rateLimitReachedType\":null,\"rateLimitReachedType\":null}}}")]
    [InlineData("{\"method\":\"a\",\"method\":\"b\"}")]
    public void DuplicateAllowlistedKeys_RejectTheWholeLine(string json)
    {
        CodexMessage m = P(json);
        Assert.Equal(CodexMessageKind.Malformed, m.Kind);
        Assert.Equal(CodexParseIssue.DuplicateKey, m.Issue);
    }

    [Fact]
    public void DuplicateNonAllowlistedKeys_AreHarmless()
    {
        CodexMessage m = P("{\"id\":2,\"result\":{\"x\":1,\"x\":2,\"rateLimitsByLimitId\":{\"codex\":" + Bucket() + "}}}");
        Assert.Equal(CodexMessageKind.Response, m.Kind);
    }

    [Fact]
    public void MoreThanSixteenBuckets_IsRejected()
    {
        string map = "{" + string.Join(",", Enumerable.Range(0, 17).Select(i => $"\"b{i}\":{Bucket($"b{i}")}")) + "}";
        Assert.Equal(CodexParseIssue.TooManyBuckets, P(Reply(null, map)).Issue);
        string ok = "{" + string.Join(",", Enumerable.Range(0, 16).Select(i => $"\"b{i}\":{Bucket($"b{i}")}")) + "}";
        Assert.Equal(16, P(Reply(null, ok)).Buckets.Count);
    }

    // ---- per-window validation (one bad window never poisons its sibling) ----

    [Theory]
    [InlineData("101")]
    [InlineData("-0.5")]
    [InlineData("\"40\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1e999")]
    [InlineData("[]")]
    public void InvalidPercent_DropsThatWindowOnly_AndCountsIt(string pct)
    {
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket(primary: Window(pct)) + "}"));
        CodexBucket b = Assert.Single(m.Buckets);
        Assert.Null(b.Primary);
        Assert.NotNull(b.Secondary);
        Assert.Equal(1, m.SkippedWindows);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("42.5", 42.5)]
    [InlineData("1E1", 10)]
    public void ValidPercentForms_AreAccepted(string pct, double expected)
    {
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket(primary: Window(pct)) + "}"));
        Assert.Equal(expected, Assert.Single(m.Buckets).Primary!.UsedPercent);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"1893456000\"")]
    [InlineData("100")]
    [InlineData("99999999999")]
    [InlineData("-1")]
    [InlineData("true")]
    public void InvalidResetsAt_DropsThatWindow_NotTheWholeReply(string reset)
    {
        string w = "{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":" + reset + "}";
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket(primary: w) + "}"));
        Assert.Null(Assert.Single(m.Buckets).Primary);
        Assert.Equal(1, m.SkippedWindows);
    }

    [Fact]
    public void NullResetsAt_AndMissingDuration_AreUnknownNotInvalid()
    {
        string w = "{\"usedPercent\":10,\"resetsAt\":null}";
        CodexWindowReading r = Assert.Single(P(Reply(null, "{\"codex\":" + Bucket(primary: w) + "}")).Buckets).Primary!;
        Assert.Null(r.ResetsAt);
        Assert.Null(r.Duration);
        string w2 = "{\"usedPercent\":10,\"windowDurationMins\":-5,\"resetsAt\":null}";
        Assert.Null(Assert.Single(P(Reply(null, "{\"codex\":" + Bucket(primary: w2) + "}")).Buckets).Primary!.Duration);
    }

    [Fact]
    public void NullOrWrongShapeWindow_IsAbsentOrSkipped_NeverAZeroReading()
    {
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket(primary: "null", secondary: "7") + "}"));
        CodexBucket b = Assert.Single(m.Buckets);
        Assert.Null(b.Primary);
        Assert.Null(b.Secondary);
        Assert.Equal(1, m.SkippedWindows);
        Assert.Empty(CodexObservations.FromRead(m.Buckets, DateTimeOffset.UnixEpoch).Windows);
    }

    [Theory]
    [InlineData("\"has\\u0001control\"")]
    [InlineData("12")]
    [InlineData("null")]
    public void UnsafeOrNonStringNames_AreDropped(string name)
    {
        CodexMessage m = P(Reply(null, "{\"codex\":" + Bucket(name: name) + "}"));
        Assert.Null(Assert.Single(m.Buckets).LimitName);
    }

    [Fact]
    public void OverlongName_IsDropped_AndOverlongKey_YieldsNoKeyedWindow()
    {
        string longName = "\"" + new string('n', 65) + "\"";
        Assert.Null(Assert.Single(P(Reply(null, "{\"codex\":" + Bucket(name: longName) + "}")).Buckets).LimitName);
        string longKey = new('k', 65);
        CodexMessage m = P(Reply(null, "{\"" + longKey + "\":" + Bucket(id: "null") + "}"));
        Assert.Empty(CodexObservations.FromRead(m.Buckets, DateTimeOffset.UnixEpoch).Windows);   // un-keyable: skipped, never guessed
    }

    // ---- fuzz: never throws, never emits an out-of-range reading ----

    [Fact]
    public void Fuzz_MutatedReplies_NeverThrow_AndNeverEmitInvalidReadings()
    {
        byte[] seed = Encoding.UTF8.GetBytes(Reply(Bucket(), "{\"codex\":" + Bucket() + ",\"x\":" + Bucket("x") + "}"));
        var rnd = new Random(20301);
        int responses = 0;
        for (int i = 0; i < 6000; i++)
        {
            byte[] b = (byte[])seed.Clone();
            int edits = 1 + rnd.Next(4);
            for (int e = 0; e < edits; e++)
            {
                switch (rnd.Next(4))
                {
                    case 0: b[rnd.Next(b.Length)] = (byte)rnd.Next(256); break;
                    case 1: b[rnd.Next(b.Length)] = (byte)"{}[]\",:0-.e"[rnd.Next(11)]; break;
                    case 2: b = b[..rnd.Next(b.Length)]; break;
                    default:
                        int at = rnd.Next(b.Length);
                        int cut = Math.Min(rnd.Next(1, 6), b.Length - at);
                        b = [.. b[..at], .. b[(at + cut)..]];
                        break;
                }

                if (b.Length == 0) break;
            }

            CodexMessage m = CodexResponseParser.Parse(b);
            if (m.Kind != CodexMessageKind.Response) continue;
            responses++;
            foreach (CodexBucket bk in m.Buckets)
            {
                foreach (CodexWindowReading? w in new[] { bk.Primary, bk.Secondary })
                {
                    if (w is null) continue;
                    Assert.InRange(w.UsedPercent, 0, 100);
                    Assert.True(w.ResetsAt is null || (w.ResetsAt >= DateTimeOffset.FromUnixTimeSeconds(946_684_800) && w.ResetsAt <= DateTimeOffset.FromUnixTimeSeconds(4_102_444_800)));
                    Assert.True(w.Duration is null || w.Duration > TimeSpan.Zero);
                }
            }
        }

        Assert.True(responses > 50, "the fuzz corpus should still reach the success path sometimes");
    }
}
