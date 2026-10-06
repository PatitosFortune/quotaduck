using System.Text;
using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.PayloadFx;

namespace QuotaDuck.Core.Tests;

/// <summary>Deterministic fuzzing: the parser is total (never throws) and anything it accepts satisfies the documented bounds.</summary>
public class ClaudeParserFuzzTests
{
    private static void AssertSound(ClaudeStatusLineParse r, string context)
    {
        foreach (ClaudeParsedWindow w in new[] { r.FiveHour, r.SevenDay })
        {
            if (r.Status != ClaudeParseStatus.Ok)
            {
                Assert.False(w.Present, context);          // a rejected payload yields nothing, not even partial windows
                continue;
            }

            Assert.Equal(w.Present && w.Issues == ClaudeWindowIssues.None && w.UsedPercent is not null, w.Usable);
            if (w.UsedPercent is { } p)
            {
                Assert.True(double.IsFinite(p) && p >= 0 && p <= 100, context);
            }

            if (w.ResetsAt is { } t)
            {
                Assert.True(t.Year is >= 2000 and <= 2100 && t.Offset == TimeSpan.Zero, context);
            }

            if (w.Usable)
            {
                Assert.Equal(ClaudeWindowIssues.None, w.Issues);
            }
        }
    }

    [Fact]
    public void ByteMutatedValidPayloads_NeverThrow_AndResultsAreAlwaysSound()
    {
        var rng = new Random(20300103);
        byte[][] seeds =
        [
            Bytes(Payload(Win(23.5, Epoch.AddHours(3)), Win(41.25, Epoch.AddDays(7)))),
            Bytes(Payload(Win(1, Epoch.AddHours(3)), null, extras: false)),
            Bytes(Payload(null, Win("100", "null"), extras: true)),
        ];

        int ok = 0, usable = 0;
        for (int i = 0; i < 40_000; i++)
        {
            byte[] m = ClaudeStateCodecTests.Mutate(seeds[i % seeds.Length], rng);
            ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(m);              // must not throw
            Assert.Equal(r, ClaudeStatusLineParser.Parse(m));                       // deterministic
            AssertSound(r, $"iteration {i}");
            if (r.Status == ClaudeParseStatus.Ok)
            {
                ok++;
                if (r.FiveHour.Usable || r.SevenDay.Usable)
                {
                    usable++;
                }
            }
        }

        Assert.True(ok > 500 && usable > 500, $"fuzz corpus must exercise the accept path (ok={ok}, usable={usable})");
    }

    [Fact]
    public void RandomStructuredJson_WithAllowlistedKeysSprinkledIn_NeverThrows_AndStaysSound()
    {
        var rng = new Random(20300104);
        string[] keys = ["rate_limits", "five_hour", "seven_day", "used_percentage", "resets_at", "x", "context_window", "cwd"];
        for (int i = 0; i < 20_000; i++)
        {
            var sb = new StringBuilder();
            Gen(sb, rng, 0, keys);
            ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(Encoding.UTF8.GetBytes(sb.ToString()));
            AssertSound(r, sb.ToString());
        }
    }

    [Fact]
    public void RandomBytes_NeverThrow()
    {
        var rng = new Random(20300105);
        for (int i = 0; i < 20_000; i++)
        {
            byte[] b = new byte[rng.Next(0, 400)];
            rng.NextBytes(b);
            ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(b);
            AssertSound(r, "random bytes");
        }
    }

    [Fact]
    public void LargeButValidPayloads_ParseInTimeProportionalToSize_NoQuadraticBlowup()
    {
        // ~200 KB of skipped unknown content with the allow-listed window at the very end.
        var sb = new StringBuilder("{\"junk\":[");
        for (int i = 0; i < 4_000; i++)
        {
            sb.Append("{\"k\":\"").Append(Canary).Append("\",\"n\":").Append(i).Append("},");
        }

        sb.Append("0],\"rate_limits\":{\"five_hour\":").Append(Win(7, Epoch.AddHours(3))).Append("}}");
        byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.True(b.Length < ClaudeStatusLineParser.MaxPayloadBytes, b.Length.ToString());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ClaudeStatusLineParse r = ClaudeStatusLineParser.Parse(b);
        sw.Stop();
        Assert.True(r.FiveHour.Usable);
        Assert.Equal(7, r.FiveHour.UsedPercent);
        Assert.True(sw.ElapsedMilliseconds < 500, $"parse took {sw.ElapsedMilliseconds} ms");   // generous: guards against pathological behaviour only
    }

    private static void Gen(StringBuilder sb, Random rng, int depth, string[] keys)
    {
        int kind = depth > 38 ? rng.Next(0, 5) : rng.Next(0, 8);
        switch (kind)
        {
            case 0: sb.Append(rng.Next(-5, 200)); break;
            case 1: sb.Append((rng.NextDouble() * 150 - 20).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
            case 2: sb.Append("\"").Append(Canary).Append(rng.Next(100)).Append("\""); break;
            case 3: sb.Append(rng.Next(2) == 0 ? "true" : "null"); break;
            case 4: sb.Append(rng.Next(900_000_000, 2_000_000_000)); break;
            case 5:
            case 6:
                sb.Append('{');
                int n = rng.Next(0, 5);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) { sb.Append(','); }
                    sb.Append('"').Append(keys[rng.Next(keys.Length)]).Append("\":");
                    Gen(sb, rng, depth + 1, keys);
                }

                sb.Append('}');
                break;
            default:
                sb.Append('[');
                int m = rng.Next(0, 4);
                for (int i = 0; i < m; i++)
                {
                    if (i > 0) { sb.Append(','); }
                    Gen(sb, rng, depth + 1, keys);
                }

                sb.Append(']');
                break;
        }
    }
}
