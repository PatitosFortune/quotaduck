using QuotaDuck.Core.Claude;
using static QuotaDuck.Core.Tests.Fx;

namespace QuotaDuck.Core.Tests;

public class ClaudeStatusLineFormatterTests
{
    private static string F(ClaudeState s, DateTimeOffset? now = null, string label = "QuotaDuck") =>
        ClaudeStatusLineFormatter.Format(s, now ?? T0, Policy, label);

    private static ClaudeWindowRecord R(double pct, TimeSpan changedAgo, TimeSpan resetsIn) =>
        new(pct, T0 + resetsIn, T0 - changedAgo, T0 - changedAgo);

    [Fact]
    public void NothingKnown_SaysWaiting_NeverZeroPercent()
    {
        string s = F(ClaudeState.Empty);
        Assert.Equal("QuotaDuck | waiting for first report", s);
        Assert.DoesNotContain("0%", s);
    }

    [Fact]
    public void BothFresh_ShowsUsedPercentages_WithTrimmedDecimals()
    {
        Assert.Equal("QuotaDuck | 5h 23.5% used | wk 41% used",
            F(new ClaudeState(R(23.5, M(1), H(3)), R(41.0, M(1), H(100)))));
        Assert.Equal("QuotaDuck | 5h 0% used | wk 100% used",
            F(new ClaudeState(R(0, M(1), H(3)), R(100, M(1), H(100)))));                  // a REPORTED 0 is shown as 0
        Assert.Equal("QuotaDuck | 5h 33.3% used | wk -- ", F(new ClaudeState(R(33.33333, M(1), H(3)), null)).TrimEnd() + " ");
    }

    [Fact]
    public void QuietIsShownLikeFresh_SleepingIsMarkedOld_ElapsedHidesTheNumber_MissingIsDashes()
    {
        string s = F(new ClaudeState(R(10, M(30), H(3)), R(20, H(5), H(100))));
        Assert.Equal("QuotaDuck | 5h 10% used | wk 20% used (old)", s);

        string elapsed = F(new ClaudeState(R(90, M(1), -M(1)), R(20, M(1), H(100))));
        Assert.Equal("QuotaDuck | 5h reset | wk 20% used", elapsed);
        Assert.DoesNotContain("90", elapsed);

        Assert.Equal("QuotaDuck | 5h 10% used | wk --", F(new ClaudeState(R(10, M(1), H(3)), null)));
    }

    [Fact]
    public void ElapsedEverywhere_NeverShowsAnyNumber()
    {
        string s = F(new ClaudeState(R(90, M(1), -M(1)), R(80, M(1), -M(5))));
        Assert.Equal("QuotaDuck | 5h reset | wk reset", s);
    }

    [Fact]
    public void CustomLabel_IsUsed()
    {
        Assert.StartsWith("My-Duck_1.0 | ", F(new ClaudeState(R(1, M(1), H(3)), null), label: "My-Duck_1.0"));
    }

    [Theory]
    [InlineData("QuotaDuck", true)]
    [InlineData("a", true)]
    [InlineData("Claude 5h", true)]
    [InlineData("v1.2_x-y", true)]
    [InlineData("123456789012345678901234", true)]     // 24
    [InlineData("1234567890123456789012345", false)]   // 25
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("a|b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\tb", false)]
    [InlineData("\u001b[31mred", false)]               // ANSI escape injection
    [InlineData("a;b", false)]
    [InlineData("é", false)]
    [InlineData("🦆", false)]
    [InlineData("<script>", false)]
    public void LabelSanitiser_AllowsOnlyASmallPrintableSet(string label, bool allowed)
    {
        Assert.Equal(allowed, ClaudeStatusLineFormatter.TrySanitizeLabel(label, out string result));
        Assert.Equal(allowed ? label : ClaudeStatusLineFormatter.DefaultLabel, result);
    }

    [Fact]
    public void NullLabel_IsRejected() => Assert.False(ClaudeStatusLineFormatter.TrySanitizeLabel(null, out _));

    [Fact]
    public void Output_IsPlainPrintableAscii_NoControlCharacters()
    {
        foreach (ClaudeState s in new[]
        {
            ClaudeState.Empty,
            new ClaudeState(R(1.25, M(1), H(3)), R(2, H(9), H(100))),
            new ClaudeState(R(99, M(1), -M(1)), null),
        })
        {
            string line = F(s);
            Assert.All(line, c => Assert.InRange(c, ' ', '~'));
        }
    }
}
