using System.Text;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;

namespace QuotaDuck.Core.Tests;

public class ClaudeStateCodecTests
{
    private static readonly DateTimeOffset T = Epoch;

    private static ClaudeWindowRecord Rec(double pct = 23.5, int resetHours = 3, int changedMin = 0, int receivedMin = 0, double? cand = null, int streak = 0) =>
        new(pct, T + TimeSpan.FromHours(resetHours), T + TimeSpan.FromMinutes(changedMin), T + TimeSpan.FromMinutes(receivedMin), cand, streak);

    private static string Json(byte[] b) => Encoding.UTF8.GetString(b);

    private static ClaudeStateDecodeResult D(string json) => ClaudeStateCodec.Decode(Encoding.UTF8.GetBytes(json));

    private const string GoodWin = "{\"usedPercent\":10,\"resetsAt\":\"2030-01-01T15:00:00.000Z\",\"changedAt\":\"2030-01-01T12:00:00.000Z\",\"receivedAt\":\"2030-01-01T12:00:00.000Z\",\"lowerCandidate\":null,\"lowerStreak\":0}";

    // ---- round trip & exact schema ----

    [Fact]
    public void ExactSchema_IsStable()
    {
        string json = Json(ClaudeStateCodec.Encode(new ClaudeState(Rec(23.5, 3, 0, 5), Rec(41.25, 100, 2, 2, 30.5, 2))));
        Assert.Equal(
            "{\"schema\":1,"
            + "\"fiveHour\":{\"usedPercent\":23.5,\"resetsAt\":\"2030-01-01T15:00:00.000Z\",\"changedAt\":\"2030-01-01T12:00:00.000Z\",\"receivedAt\":\"2030-01-01T12:05:00.000Z\",\"lowerCandidate\":null,\"lowerStreak\":0},"
            + "\"sevenDay\":{\"usedPercent\":41.25,\"resetsAt\":\"2030-01-05T16:00:00.000Z\",\"changedAt\":\"2030-01-01T12:02:00.000Z\",\"receivedAt\":\"2030-01-01T12:02:00.000Z\",\"lowerCandidate\":30.5,\"lowerStreak\":2}}",
            json);
    }

    [Fact]
    public void RoundTrip_Both_One_None_AndNullReset()
    {
        foreach (ClaudeState s in new[]
        {
            new ClaudeState(Rec(), Rec(80, 150)),
            new ClaudeState(Rec(), null),
            new ClaudeState(null, Rec(0.001)),
            ClaudeState.Empty,
            new ClaudeState(new ClaudeWindowRecord(100, null, T, T), null),
        })
        {
            ClaudeStateDecodeResult r = ClaudeStateCodec.Decode(ClaudeStateCodec.Encode(s));
            Assert.Equal(ClaudeStateDecodeStatus.Ok, r.Status);
            Assert.Equal(s, r.State);
            Assert.Equal(0, r.DroppedWindows);
        }
    }

    [Fact]
    public void Times_AreCanonicalisedToUtcMilliseconds()
    {
        DateTimeOffset odd = new DateTimeOffset(2030, 1, 1, 14, 0, 0, TimeSpan.FromHours(2)) + TimeSpan.FromTicks(1234567);
        var rec = new ClaudeWindowRecord(5, odd, odd, odd);
        ClaudeWindowRecord back = ClaudeStateCodec.Decode(ClaudeStateCodec.Encode(new ClaudeState(rec, null))).State.FiveHour!;
        Assert.Equal(TimeSpan.Zero, back.ChangedAt.Offset);
        Assert.Equal(ClaudeStateCodec.Canonicalize(odd), back.ChangedAt);
        Assert.Equal(0, back.ChangedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 12, 0, 0, 123, TimeSpan.Zero), back.ChangedAt);
    }

    [Fact]
    public void EncodedSize_IsFarBelowTheCap_EvenForTheLongestPossibleContent()
    {
        var longest = new ClaudeWindowRecord(99.99999999999999, T + TimeSpan.FromDays(7), T, T, 12.345678901234567, 1000);
        byte[] b = ClaudeStateCodec.Encode(new ClaudeState(longest, longest));
        Assert.True(b.Length < ClaudeStateCodec.MaxBytes / 3, $"{b.Length} bytes");
    }

    // ---- empty / corrupt / oversized / future ----

    [Fact]
    public void Empty_Bom_AreEmpty()
    {
        Assert.Equal(ClaudeStateDecodeStatus.Empty, ClaudeStateCodec.Decode([]).Status);
        Assert.Equal(ClaudeStateDecodeStatus.Empty, ClaudeStateCodec.Decode([0xEF, 0xBB, 0xBF]).Status);
        Assert.Equal(ClaudeState.Empty, ClaudeStateCodec.Decode([]).State);
    }

    [Fact]
    public void Oversized_IsRejectedWithoutParsing()
    {
        byte[] big = new byte[ClaudeStateCodec.MaxBytes + 1];
        Array.Fill(big, (byte)' ');
        Assert.Equal(ClaudeStateDecodeStatus.TooLarge, ClaudeStateCodec.Decode(big).Status);
        big = new byte[ClaudeStateCodec.MaxBytes];
        Encoding.UTF8.GetBytes("{\"schema\":1}").CopyTo(big, 0);
        Array.Fill(big, (byte)' ', 12, big.Length - 12);
        Assert.Equal(ClaudeStateDecodeStatus.Ok, ClaudeStateCodec.Decode(big).Status);          // exactly at the cap is fine
    }

    public static TheoryData<string> CorruptDocs => new()
    {
        "not json",
        "{",
        "{\"schema\":1",
        "[]",
        "null",
        "\"x\"",
        "{}",                                                  // schema missing
        "{\"schema\":0}",
        "{\"schema\":-1}",
        "{\"schema\":\"1\"}",
        "{\"schema\":1.5}",
        "{\"schema\":1,\"schema\":1}",
        "{\"schema\":1,\"fiveHour\":null,\"fiveHour\":null}",
        "{\"schema\":1,\"sevenDay\":null,\"sevenDay\":null}",
        "{\"schema\":1}trailing",
        "{\"schema\":1}{\"schema\":1}",
        "{\"schema\":1,\"fiveHour\":}",
        "   ",
    };

    [Theory]
    [MemberData(nameof(CorruptDocs))]
    public void CorruptState_IsCorrupt_AndYieldsAnEmptyStateNeverPartial(string json)
    {
        ClaudeStateDecodeResult r = D(json);
        Assert.Equal(ClaudeStateDecodeStatus.Corrupt, r.Status);
        Assert.Equal(ClaudeState.Empty, r.State);
    }

    [Fact]
    public void InvalidUtf8_IsCorrupt()
    {
        byte[] b = [.. Encoding.UTF8.GetBytes("{\"schema\":1,\"x\":\""), 0xFF, .. Encoding.UTF8.GetBytes("\"}")];
        Assert.Equal(ClaudeStateDecodeStatus.Corrupt, ClaudeStateCodec.Decode(b).Status);
    }

    [Fact]
    public void ExcessiveNesting_IsCorrupt()
    {
        Assert.Equal(ClaudeStateDecodeStatus.Corrupt, D("{\"schema\":1,\"x\":" + Nested(50) + "}").Status);
    }

    [Fact]
    public void FutureSchema_IsReportedAsSuch_EvenIfTheRestIsUnreadable()
    {
        Assert.Equal(ClaudeStateDecodeStatus.FutureSchema, D("{\"schema\":2,\"fiveHour\":{\"whatever\":true}}").Status);
        Assert.Equal(ClaudeStateDecodeStatus.FutureSchema, D("{\"schema\":99}").Status);
        Assert.Equal(ClaudeStateDecodeStatus.FutureSchema, D("{\"newThing\":1,\"schema\":3}").Status);     // schema position-independent
    }

    // ---- per-window validation: drop only the bad window ----

    [Theory]
    [InlineData("usedPercent", "101")]
    [InlineData("usedPercent", "-0.1")]
    [InlineData("usedPercent", "\"10\"")]
    [InlineData("usedPercent", "null")]
    [InlineData("usedPercent", "{\"x\":1}")]
    [InlineData("changedAt", "\"2030-01-01T12:00:00Z\"")]               // wrong format (no ms)
    [InlineData("changedAt", "\"2030-01-01T12:00:00.000+02:00\"")]      // offset not allowed
    [InlineData("changedAt", "\"1999-12-31T23:59:59.999Z\"")]           // before 2000
    [InlineData("changedAt", "\"2100-01-01T00:00:00.000Z\"")]           // at/after 2100
    [InlineData("changedAt", "12345")]
    [InlineData("changedAt", "\"2030-01-01T13:00:00.000Z\"")]           // changedAt > receivedAt
    [InlineData("receivedAt", "null")]
    [InlineData("resetsAt", "\"garbage\"")]
    [InlineData("resetsAt", "5")]
    [InlineData("lowerCandidate", "101")]
    [InlineData("lowerCandidate", "\"x\"")]
    [InlineData("lowerStreak", "1001")]
    [InlineData("lowerStreak", "-1")]
    [InlineData("lowerStreak", "1.5")]
    [InlineData("lowerStreak", "[1]")]
    public void InvalidField_DropsThatWindowOnly_TheOtherSurvives(string field, string badValue)
    {
        string bad = ReplaceField(GoodWin, field, badValue);
        ClaudeStateDecodeResult r = D("{\"schema\":1,\"fiveHour\":" + bad + ",\"sevenDay\":" + GoodWin + "}");
        Assert.Equal(ClaudeStateDecodeStatus.Ok, r.Status);
        Assert.Null(r.State.FiveHour);
        Assert.NotNull(r.State.SevenDay);
        Assert.Equal(1, r.DroppedWindows);
    }

    [Fact]
    public void StreakWithoutACandidate_IsInvalid() =>
        Assert.Null(D("{\"schema\":1,\"fiveHour\":" + ReplaceField(GoodWin, "lowerStreak", "2") + "}").State.FiveHour);

    [Fact]
    public void MissingRequiredField_DropsTheWindow()
    {
        foreach (string f in new[] { "usedPercent", "changedAt", "receivedAt" })
        {
            string noField = RemoveField(GoodWin, f);
            Assert.Null(D("{\"schema\":1,\"fiveHour\":" + noField + "}").State.FiveHour);
        }
    }

    [Fact]
    public void DuplicateFieldInsideAWindow_DropsTheWindow() =>
        Assert.Null(D("{\"schema\":1,\"fiveHour\":" + GoodWin.Replace("{\"usedPercent\":10,", "{\"usedPercent\":10,\"usedPercent\":11,") + "}").State.FiveHour);

    [Fact]
    public void WindowThatIsNotAnObject_IsDropped()
    {
        ClaudeStateDecodeResult r = D("{\"schema\":1,\"fiveHour\":[1,2],\"sevenDay\":" + GoodWin + "}");
        Assert.Null(r.State.FiveHour);
        Assert.NotNull(r.State.SevenDay);
        Assert.Equal(1, r.DroppedWindows);
    }

    [Fact]
    public void UnknownFields_AreIgnoredAndNeverRoundTripped()
    {
        string withExtras = "{\"schema\":1,\"zzz\":{\"a\":[1,2,{\"b\":\"" + Canary + "\"}]},\"fiveHour\":"
            + GoodWin.Replace("\"lowerStreak\":0", "\"lowerStreak\":0,\"note\":\"" + Canary + "\"") + "}";
        ClaudeStateDecodeResult r = D(withExtras);
        Assert.Equal(ClaudeStateDecodeStatus.Ok, r.Status);
        Assert.NotNull(r.State.FiveHour);
        Assert.DoesNotContain(Canary, Json(ClaudeStateCodec.Encode(r.State)));
    }

    // ---- fuzz: whatever bytes arrive, decoding is total and only ever yields valid records ----

    [Fact]
    public void FuzzedBytes_NeverThrow_AndAcceptedRecordsAreAlwaysValid()
    {
        var rng = new Random(20300102);
        byte[] seed = ClaudeStateCodec.Encode(new ClaudeState(Rec(23.5, 3, 0, 5, 20, 2), Rec(41, 100)));
        int accepted = 0;
        for (int i = 0; i < 20_000; i++)
        {
            byte[] m = Mutate(seed, rng);
            ClaudeStateDecodeResult r = ClaudeStateCodec.Decode(m);          // must not throw
            if (r.Status != ClaudeStateDecodeStatus.Ok)
            {
                Assert.Equal(ClaudeState.Empty, r.State);
                continue;
            }

            accepted++;
            foreach (ClaudeWindowRecord? w in new[] { r.State.FiveHour, r.State.SevenDay })
            {
                if (w is null)
                {
                    continue;
                }

                Assert.InRange(w.UsedPercent, 0, 100);
                Assert.True(w.ChangedAt <= w.ReceivedAt);
                Assert.InRange(w.LowerStreak, 0, 1000);
                Assert.True(w.LowerStreak == 0 || w.LowerCandidate is not null);
                Assert.True(w.ChangedAt.Year is >= 2000 and < 2100);
            }
        }

        Assert.True(accepted > 100, "fuzz corpus should still exercise the accept path");
    }

    internal static byte[] Mutate(byte[] seed, Random rng)
    {
        var b = new List<byte>(seed);
        int ops = rng.Next(1, 5);
        for (int k = 0; k < ops && b.Count > 0; k++)
        {
            int pos = rng.Next(b.Count);
            switch (rng.Next(7))
            {
                case 0: b[pos] ^= (byte)(1 << rng.Next(8)); break;
                case 1: b.RemoveAt(pos); break;
                case 2: b.Insert(pos, (byte)rng.Next(256)); break;
                case 3: b.RemoveRange(pos, Math.Min(rng.Next(1, 20), b.Count - pos)); break;
                case 4: b.Insert(pos, new byte[] { (byte)'"', (byte)'\\', (byte)'{', (byte)'[', (byte)',', (byte)'}', (byte)']', 0xFF, 0x00 }[rng.Next(9)]); break;
                case 5: b.RemoveRange(pos, b.Count - pos); break;
                default:
                    int len = Math.Min(rng.Next(1, 30), b.Count - pos);
                    b.InsertRange(rng.Next(b.Count), b.GetRange(pos, len));
                    break;
            }
        }

        return b.ToArray();
    }

    private static string ReplaceField(string win, string field, string raw)
    {
        // naive but sufficient for the fixed GoodWin layout
        var keys = new[] { "usedPercent", "resetsAt", "changedAt", "receivedAt", "lowerCandidate", "lowerStreak" };
        int start = win.IndexOf("\"" + field + "\":", StringComparison.Ordinal) + field.Length + 3;
        int end = start;
        int depth = 0;
        while (end < win.Length)
        {
            char c = win[end];
            if (c is '{' or '[') { depth++; }
            else if (c is '}' or ']') { if (depth == 0) { break; } depth--; }
            else if (c == ',' && depth == 0) { break; }
            end++;
        }

        _ = keys;
        return win[..start] + raw + win[end..];
    }

    private static string RemoveField(string win, string field)
    {
        int s = win.IndexOf("\"" + field + "\":", StringComparison.Ordinal);
        int e = win.IndexOf(',', s);
        return e < 0 ? win[..s].TrimEnd(',') + "}" : win[..s] + win[(e + 1)..];
    }
}
