using System.Text.Json;
using QuotaDuck.App.Presentation;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>Snapshot rendering for every provider state. Pure strings: no WPF involved.</summary>
public class PresenterTests
{
    private static readonly DateTimeOffset T = Snap.T0;

    private static ShellPresentation P(AppSnapshot s, RefreshFeedback f = RefreshFeedback.None) => Presenter.Present(s, f, Snap.Utc);

    private static ClaudeState Claude(double five, DateTimeOffset changed, DateTimeOffset? reset5 = null, double week = 12) =>
        new(Snap.Rec(five, reset5 ?? T.AddHours(3), changed), Snap.Rec(week, T.AddDays(4), changed));

    private static WindowRowModel Row(ProviderCardModel c, string label) => c.Windows.Single(w => w.Label == label);

    // ---------------- Claude ----------------

    [Fact]
    public void Claude_WaitingForTerminal_NoNumbers_NoZero_AndTheLimitationIsStated()
    {
        ShellPresentation p = P(Snap.Make(T));
        Assert.Equal("Claude", p.Claude.Name);
        Assert.Equal("Waiting for Claude Code in a terminal", p.Claude.Headline);
        Assert.Equal(Tone.Muted, p.Claude.Tone);
        Assert.Equal(2, p.Claude.Windows.Count);                                         // the two windows Claude really reports, shown as "no data"
        Assert.All(p.Claude.Windows, w =>
        {
            Assert.Null(w.Fraction);
            Assert.Equal("No data", w.PercentText);
            Assert.Equal("No data", w.StateText);
            Assert.DoesNotContain("0%", w.PercentText);
        });
        Assert.Contains("terminal sessions only", p.Claude.Footer);
        Assert.Null(p.Claude.Refresh);                                                    // Claude has nothing to refresh
    }

    [Fact]
    public void Claude_Fresh_ShowsUsedRemainingResetCountdownAndAge()
    {
        ShellPresentation p = P(Snap.Make(T, Claude(42, T.AddMinutes(-3))));
        WindowRowModel five = Row(p.Claude, "5-hour");
        Assert.Equal("Reported by Claude Code", p.Claude.Headline);
        Assert.Equal(0.42, five.Fraction);
        Assert.Equal("42% used", five.PercentText);
        Assert.Equal("58% left", five.RemainingText);
        Assert.Equal("Fresh", five.StateText);
        Assert.Equal("Resets in 3 h (13:00)", five.ResetText);
        Assert.Equal("Last changed 3 min ago", five.AgeText);
        Assert.Equal("Resets in 4 d (Sat 10:00)", Row(p.Claude, "Weekly").ResetText);
        Assert.Contains("42% used", five.AccessibleName);
        Assert.Contains("Fresh", five.AccessibleName);
    }

    [Fact]
    public void Claude_Quiet_And_Sleeping_KeepTheNumberButSayHowOldItIs()
    {
        WindowRowModel quiet = Row(P(Snap.Make(T, Claude(42, T.AddMinutes(-40)))).Claude, "5-hour");
        Assert.Equal("Quiet", quiet.StateText);
        Assert.Equal("42% used", quiet.PercentText);
        Assert.Equal("Last changed 40 min ago", quiet.AgeText);

        ShellPresentation sleepy = P(Snap.Make(T, Claude(42, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1))));
        WindowRowModel sleeping = Row(sleepy.Claude, "5-hour");
        Assert.Equal("Sleeping (old)", sleeping.StateText);
        Assert.Equal("42% used", sleeping.PercentText);
        Assert.Equal("Last changed 2 h 10 m ago", sleeping.AgeText);
        Assert.Equal("No recent report from Claude Code", sleepy.Claude.Headline);
        Assert.Contains("VS Code panel", sleepy.Claude.Note);                             // the surface limitation, stated plainly
    }

    [Fact]
    public void Claude_Elapsed_ShowsNoNumber_NeverTheOldValue_NeverZero()
    {
        ShellPresentation p = P(Snap.Make(T, Claude(80, T.AddHours(-4), reset5: T.AddMinutes(-20), week: 20)));
        WindowRowModel five = Row(p.Claude, "5-hour");
        Assert.Equal("Reset", five.StateText);
        Assert.Null(five.Fraction);
        Assert.Equal("Reset; new value unknown", five.PercentText);
        Assert.Equal(string.Empty, five.RemainingText);
        Assert.Equal("Reset 20 min ago", five.ResetText);
        Assert.DoesNotContain("80", five.PercentText + five.RemainingText + five.AccessibleName);
        Assert.DoesNotContain("0%", five.PercentText);
        Assert.Equal("20% used", Row(p.Claude, "Weekly").PercentText);                   // the sibling window is independent
    }

    [Fact]
    public void Claude_AllWindowsElapsed_SaysWaitingForTheNextReport()
    {
        ClaudeState s = new(Snap.Rec(80, T.AddMinutes(-20), T.AddHours(-4)), Snap.Rec(20, T.AddMinutes(-5), T.AddDays(-6)));
        Assert.Equal("Waiting for the next Claude Code report", P(Snap.Make(T, s)).Claude.Headline);
    }

    [Theory]
    [InlineData(ProviderIssue.ClaudeStateUnreadable)]
    [InlineData(ProviderIssue.ClaudeStateUnsupportedVersion)]
    public void Claude_StateFileProblems_KeepTheLastKnownValues_WithASafeNote(ProviderIssue issue)
    {
        ShellPresentation p = P(Snap.Make(T, Claude(42, T.AddMinutes(-3)), claudeIssue: issue));
        Assert.Equal("Showing the last known values", p.Claude.Headline);
        Assert.Equal(IssueText.For(issue), p.Claude.Note);
        Assert.Equal("42% used", Row(p.Claude, "5-hour").PercentText);
        Assert.Equal(Tone.Warn, p.Claude.Tone);
    }

    [Fact]
    public void Claude_Disabled_IsTurnedOff()
    {
        ShellPresentation p = P(Snap.Make(T, claudeEnabled: false));
        Assert.Equal("Turned off", p.Claude.Headline);
    }

    // ---------------- Codex ----------------

    [Fact]
    public void Codex_BeforeTheFirstRead_IsNotReadYet_WithAnEnabledRefresh_AndNoWindowsInvented()
    {
        ShellPresentation p = P(Snap.Make(T));
        Assert.Equal("Not read yet", p.Codex.Headline);
        Assert.Empty(p.Codex.Windows);
        Assert.Equal(new RefreshButtonModel(true, "Refresh", null), p.Codex.Refresh);
    }

    [Fact]
    public void Codex_Reading_ShowsReading_AndDisablesRefresh_WhileKeepingOlderWindows()
    {
        ShellPresentation p = P(Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-12)), reading: true, allowed: false));
        Assert.Equal("Reading…", p.Codex.Headline);
        Assert.Equal(new RefreshButtonModel(false, "Reading…", null), p.Codex.Refresh);
        Assert.Equal(2, p.Codex.Windows.Count);
    }

    [Fact]
    public void Codex_Fresh_ShowsBothWindows_WithCountdowns()
    {
        ShellPresentation p = P(Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-1))));
        Assert.Equal("Up to date", p.Codex.Headline);
        WindowRowModel five = Row(p.Codex, "5-hour");
        Assert.Equal("31% used", five.PercentText);
        Assert.Equal("69% left", five.RemainingText);
        Assert.Equal("Fresh", five.StateText);
        Assert.Equal("Read 1 min ago", five.AgeText);
        Assert.StartsWith("Resets in 2 h 59 m", five.ResetText);
        Assert.Equal("35% used", Row(p.Codex, "Weekly").PercentText);
        Assert.Null(p.Codex.Footer);
    }

    [Fact]
    public void Codex_OnlyThePrimaryWindow_IsNotPaddedToLookSymmetric()
    {
        var onlyOne = Core.Codex.CodexObservations.FromRead(
            [new Core.Codex.CodexBucket("codex", null, new Core.Codex.CodexWindowReading(10, TimeSpan.FromMinutes(300), T.AddHours(2)), null, false)], T);
        ShellPresentation p = P(Snap.Make(T, codex: onlyOne));
        Assert.Single(p.Codex.Windows);
        Assert.Equal(2, p.Claude.Windows.Count);                                          // asymmetric on purpose: provider truth over symmetry
    }

    [Fact]
    public void Codex_Failure_ShowsASafeReason_AndTheLastSuccessfulRead_Aged()
    {
        ShellPresentation p = P(Snap.Make(
            T,
            codex: Snap.Codex(T.AddMinutes(-15)),
            codexIssue: ProviderIssue.TemporaryProviderFailure,
            attempt: new ProviderAttempt(T.AddMinutes(-1), ProviderErrorKind.Timeout)));
        Assert.Equal("Codex did not answer in time; will try again", p.Codex.Headline);
        Assert.Equal("Showing the last successful read.", p.Codex.Note);
        Assert.Equal(Tone.Warn, p.Codex.Tone);
        Assert.Equal("Read 15 min ago", Row(p.Codex, "5-hour").AgeText);                  // the failed attempt did not make it younger
    }

    [Fact]
    public void Codex_FailureWithNoData_ShowsNoWindows()
    {
        ShellPresentation p = P(Snap.Make(T, codexIssue: ProviderIssue.CodexNotInstalled, attempt: new ProviderAttempt(T, ProviderErrorKind.NotFound)));
        Assert.Equal("Codex is not installed", p.Codex.Headline);
        Assert.Empty(p.Codex.Windows);
        Assert.Null(p.Codex.Note);
        Assert.Equal(Tone.Error, p.Codex.Tone);
    }

    [Fact]
    public void Codex_ElapsedWindow_ShowsNoNumber_AndAReadIsDue()
    {
        ShellPresentation p = P(Snap.Make(T, codex: Snap.Codex(T.AddHours(-4), reset5h: T.AddMinutes(-30), resetWk: T.AddMinutes(-10))));
        Assert.Equal("Window reset; a read is due", p.Codex.Headline);
        WindowRowModel five = Row(p.Codex, "5-hour");
        Assert.Equal("Reset", five.StateText);
        Assert.Null(five.Fraction);
        Assert.Equal("Reset 30 min ago", five.ResetText);
    }

    [Fact]
    public void Codex_Disabled_HasNoRefreshButton()
    {
        ShellPresentation p = P(Snap.Make(T, codexEnabled: false));
        Assert.Equal("Turned off", p.Codex.Headline);
        Assert.Null(p.Codex.Refresh);
    }

    // ---------------- refresh button rules ----------------

    [Fact]
    public void RefreshButton_Allowed_Cooldown_Reading_Rules()
    {
        Assert.Equal(new RefreshButtonModel(true, "Refresh", null), P(Snap.Make(T, allowed: true)).Codex.Refresh);

        RefreshButtonModel cd = P(Snap.Make(T, allowed: false, allowedAt: T.AddSeconds(41.2))).Codex.Refresh!;
        Assert.False(cd.Enabled);
        Assert.Equal("Refresh in 42 s", cd.Text);                                         // rounded UP: never enabled earlier than shown
        Assert.Contains("once a minute", cd.Hint);

        Assert.False(P(Snap.Make(T, allowed: false, allowedAt: T.AddSeconds(0.2))).Codex.Refresh!.Enabled);
        Assert.Equal("Refresh in 1 s", P(Snap.Make(T, allowed: false, allowedAt: T.AddSeconds(0.2))).Codex.Refresh!.Text);

        RefreshButtonModel reading = P(Snap.Make(T, reading: true, allowed: false)).Codex.Refresh!;
        Assert.False(reading.Enabled);
        Assert.Equal("Reading…", reading.Text);

        // cooldown AND reading: reading wins
        Assert.Equal("Reading…", P(Snap.Make(T, reading: true, allowed: false, allowedAt: T.AddSeconds(30))).Codex.Refresh!.Text);
    }

    [Fact]
    public void ManualRefreshFeedback_IsSafeText()
    {
        Assert.Equal("Codex can be refreshed at most once a minute.", P(Snap.Make(T), RefreshFeedback.TooSoon).RefreshFeedback);
        Assert.Equal("The refresh did not work.", P(Snap.Make(T), RefreshFeedback.Failed).RefreshFeedback);
        string withReason = P(Snap.Make(T, codexIssue: ProviderIssue.CodexRequestRejected), RefreshFeedback.Failed).RefreshFeedback!;
        Assert.Contains("sign in to Codex", withReason);
        Assert.Null(P(Snap.Make(T)).RefreshFeedback);
    }

    // ---------------- tray tooltip ----------------

    [Fact]
    public void Tooltip_IsCompact_PlainText_AndWithinTheTrayLimit()
    {
        string t = P(Snap.Make(T, Claude(42, T.AddMinutes(-3)), Snap.Codex(T.AddMinutes(-1)))).TrayTooltip;
        Assert.Equal("QuotaDuck | Claude 5h 42% wk 12% | Codex 5h 31% wk 35%", t);
        Assert.True(t.Length <= 127);

        Assert.Equal("QuotaDuck | Claude waiting | Codex –", P(Snap.Make(T)).TrayTooltip);
    }

    [Fact]
    public void Tooltip_ReflectsStates_WithoutInventingNumbers()
    {
        string elapsed = Presenter.Tooltip(Snap.Make(T, Claude(80, T.AddHours(-4), reset5: T.AddMinutes(-20), week: 20)));
        Assert.Contains("5h reset", elapsed);
        Assert.DoesNotContain("80", elapsed);
        Assert.Contains("wk 20%", elapsed);
        Assert.Contains("(old)", Presenter.Tooltip(Snap.Make(T, Claude(42, T.AddHours(-2).AddMinutes(-10), reset5: T.AddHours(1)))));
        Assert.Contains("Claude waiting", Presenter.Tooltip(Snap.Make(T)));
        Assert.Contains("Codex reading", Presenter.Tooltip(Snap.Make(T, reading: true, allowed: false)));
        Assert.Contains("Codex off", Presenter.Tooltip(Snap.Make(T, codexEnabled: false)));
        Assert.All(new[] { Snap.Make(T), Snap.Make(T, Claude(1, T)), Snap.Make(T, codex: Snap.Codex(T)) }, s => Assert.True(Presenter.Tooltip(s).Length <= 127));
    }

    // ---------------- error mapping / no leakage ----------------

    [Fact]
    public void EveryIssue_HasSafeNonEmptyText_NoPathsExceptionsOrProtocolWords()
    {
        foreach (ProviderIssue issue in Enum.GetValues<ProviderIssue>())
        {
            string text = IssueText.For(issue);
            if (issue == ProviderIssue.None)
            {
                Assert.Equal(string.Empty, text);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(text), issue.ToString());
            foreach (string bad in new[] { "\\", "/", "Exception", "stack", "stderr", "json", "rpc", "token", "password", "account", ".exe", "app-server", "0x" })
            {
                Assert.DoesNotContain(bad, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal("Something went wrong", IssueText.For((ProviderIssue)12345));        // unknown values still map to a safe string
    }

    [Fact]
    public void WholePresentation_ForEveryIssue_ContainsNothingSensitive()
    {
        foreach (ProviderIssue issue in Enum.GetValues<ProviderIssue>())
        {
            AppSnapshot s = Snap.Make(T, Claude(42, T.AddMinutes(-3)), Snap.Codex(T.AddMinutes(-3)), claudeIssue: issue, codexIssue: issue);
            string json = JsonSerializer.Serialize(Presenter.Present(s, RefreshFeedback.Failed, Snap.Utc));
            foreach (string bad in new[] { "Exception", "C:\\\\", "codex.exe", "accountId", "Bearer", "stderr", "CANARY" })
            {
                Assert.DoesNotContain(bad, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Claude_HasNoRefreshOrFetchAffordance_InAnyState()
    {
        foreach (AppSnapshot s in new[]
                 {
                     Snap.Make(T), Snap.Make(T, Claude(42, T.AddMinutes(-3))), Snap.Make(T, Claude(42, T.AddHours(-3), reset5: T.AddHours(1))),
                     Snap.Make(T, Claude(80, T.AddHours(-4), reset5: T.AddMinutes(-20))), Snap.Make(T, claudeIssue: ProviderIssue.ClaudeStateUnreadable),
                 })
        {
            ProviderCardModel c = P(s).Claude;
            Assert.Null(c.Refresh);
            string all = string.Join("|", new[] { c.Name, c.Headline, c.Note, c.Footer }.Concat(c.Windows.SelectMany(w => new[] { w.Label, w.PercentText, w.RemainingText, w.StateText, w.ResetText, w.AgeText, w.AccessibleName })));
            Assert.DoesNotContain("Refresh", all);
            Assert.DoesNotContain("fetch", all, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------- time text ----------------

    [Theory]
    [InlineData(0, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3660, "1 h 1 m")]
    [InlineData(86399, "23 h 59 m")]
    [InlineData(86400, "1 d")]
    [InlineData(90000, "1 d 1 h")]
    [InlineData(-5, "less than a minute")]
    public void Humanize_RoundsDown_AndNeverGoesNegative(int seconds, string expected) =>
        Assert.Equal(expected, TimeText.Humanize(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void LocalReset_UsesTheGivenZone_AndAddsTheDayWhenFarAway()
    {
        TimeZoneInfo plus2 = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(2), "t", "t");
        Assert.Equal("12:30", TimeText.LocalReset(T.AddMinutes(30), T, plus2));
        Assert.Equal("Thu 12:00", TimeText.LocalReset(T.AddDays(2), T, plus2));
    }
}
