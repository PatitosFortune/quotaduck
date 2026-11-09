using System.Text.RegularExpressions;
using QuotaDuck.App.Presentation;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.App.Tests;

/// <summary>The clipboard report as a pure function of a snapshot. Exact strings: the text is the product, so it must be predictable.</summary>
public class UsageSnapshotTextTests
{
    private static readonly DateTimeOffset T = Snap.T0;                                   // 2030-01-01 10:00 UTC

    private static string Text(AppSnapshot s, ExportScope scope = ExportScope.Both, TimeZoneInfo? zone = null) =>
        UsageSnapshotText.Build(s, scope, zone ?? Snap.Utc);

    /// <summary>Claude record with the changed-at and received-at moments given separately (they differ in real life).</summary>
    private static ClaudeWindowRecord Rec(double pct, DateTimeOffset reset, DateTimeOffset changed, DateTimeOffset received) => new(pct, reset, changed, received);

    private static ClaudeState Claude(DateTimeOffset changed, DateTimeOffset received, double five = 42, double week = 12) =>
        new(Rec(five, T.AddHours(3), changed, received), Rec(week, T.AddDays(4), changed, received));

    private static AppSnapshot Fresh() => Snap.Make(T, Claude(T.AddMinutes(-3), T.AddMinutes(-1)), Snap.Codex(T.AddMinutes(-2)));

    // ---------------- scopes and exact layout ----------------

    [Fact]
    public void Both_ExactText_CodexThenClaude()
    {
        const string expected =
            "QuotaDuck usage snapshot\n" +
            "Captured: 2030-01-01 10:00 +00:00 (when this text was copied; each provider's own data time is listed below)\n" +
            "Percentages are usage REMAINING; the percentage used is in brackets.\n" +
            "\n" +
            "CODEX\n" +
            "Status: Up to date\n" +
            "5-hour session: 69% remaining (31% used); resets 2030-01-01 12:58 +00:00 (in 2 h 58 m)\n" +
            "Weekly: 65% remaining (35% used); resets 2030-01-06 09:58 +00:00 (in 4 d 23 h)\n" +
            "Last successful read: 2030-01-01 09:58 +00:00 (2 min ago)\n" +
            "\n" +
            "CLAUDE\n" +
            "Status: Reported by Claude Code\n" +
            "5-hour session: 58% remaining (42% used); resets 2030-01-01 13:00 +00:00 (in 3 h)\n" +
            "Weekly: 88% remaining (12% used); resets 2030-01-05 10:00 +00:00 (in 4 d)\n" +
            "Numbers last changed: 2030-01-01 09:57 +00:00 (3 min ago)\n" +
            "Status line last ran: 2030-01-01 09:59 +00:00 (about 1 min ago)\n" +
            "Note: Claude reports only from terminal Claude Code sessions; usage elsewhere (VS Code panel, Claude Desktop, claude.ai) may not be reflected.\n";
        Assert.Equal(expected, Text(Fresh()));
    }

    [Fact]
    public void CodexOnly_HasNoClaudeSection_AndClaudeOnly_HasNoCodexSection()
    {
        string codex = Text(Fresh(), ExportScope.Codex);
        Assert.Contains("CODEX", codex);
        Assert.DoesNotContain("CLAUDE", codex);
        Assert.DoesNotContain("Status line last ran", codex);

        string claude = Text(Fresh(), ExportScope.Claude);
        Assert.Contains("CLAUDE", claude);
        Assert.DoesNotContain("CODEX", claude);
        Assert.DoesNotContain("Last successful read", claude);
        Assert.StartsWith("QuotaDuck usage snapshot\nCaptured: ", claude);
    }

    [Fact]
    public void Output_IsDeterministic_LfOnly_AndAscii()
    {
        string a = Text(Fresh()), b = Text(Fresh());
        Assert.Equal(a, b);
        Assert.DoesNotContain('\r', a);
        Assert.All(a, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4}"));
    }

    // ---------------- time: captured vs provider, changed vs ran ----------------

    [Fact]
    public void CapturedIsTheSnapshotInstant_NotAProviderTime()
    {
        AppSnapshot s = Snap.Make(T.AddMinutes(30), Claude(T.AddMinutes(-3), T.AddMinutes(-1)), Snap.Codex(T.AddMinutes(-2)));
        string t = Text(s);
        Assert.Contains("Captured: 2030-01-01 10:30 +00:00", t);
        Assert.Contains("Last successful read: 2030-01-01 09:58 +00:00 (32 min ago)", t);   // provider time stays its own, with its age at the capture instant
    }

    [Fact]
    public void Claude_StatusLineRanRecently_ButNumbersUnchanged_IsNotCalledStale()
    {
        // numbers last changed 5 h ago (Sleeping); the status line ran 2 min ago: unchanged percentages are not a missing report
        string t = Text(Snap.Make(T, Claude(T.AddHours(-5), T.AddMinutes(-2))), ExportScope.Claude);
        Assert.Contains("Status: Claude Code is still reporting; the numbers have not changed for a while", t);
        Assert.DoesNotContain("No recent report", t);
        Assert.Contains("Numbers last changed: 2030-01-01 05:00 +00:00 (5 h ago)", t);
        Assert.Contains("Status line last ran: 2030-01-01 09:58 +00:00 (about 2 min ago)", t);
        // the per-window text agrees with the status: unchanged (the status line reported it again), not "OLD"
        Assert.Contains("5-hour session: 58% remaining (42% used), unchanged since 2030-01-01 05:00 +00:00 (5 h ago); status line ran 2 min ago; resets", t);
        Assert.Contains("Weekly: 88% remaining (12% used), unchanged since", t);
        Assert.DoesNotContain("OLD", t);
    }

    [Fact]
    public void Claude_QuietValue_WithARecentStatusLine_IsUnchanged_AndWithoutOne_IsOlder()
    {
        string ran = Text(Snap.Make(T, Claude(T.AddMinutes(-40), T.AddMinutes(-1))), ExportScope.Claude);
        Assert.Contains("Status: Reported by Claude Code", ran);
        Assert.Contains("5-hour session: 58% remaining (42% used), unchanged since 2030-01-01 09:20 +00:00 (40 min ago); status line ran 1 min ago", ran);

        string silent = Text(Snap.Make(T, Claude(T.AddMinutes(-40), T.AddMinutes(-40))), ExportScope.Claude);
        Assert.Contains("5-hour session: 58% remaining (42% used), older reading: last changed 40 min ago", silent);
        Assert.DoesNotContain("unchanged since", silent);
    }

    [Fact]
    public void Claude_ExpiredWindow_IsNoValue_EvenWhileTheStatusLineKeepsRunning()
    {
        ClaudeState st = new(Rec(97, T.AddMinutes(-20), T.AddHours(-1), T.AddMinutes(-1)), Rec(12, T.AddDays(4), T.AddHours(-1), T.AddMinutes(-1)));
        string t = Text(Snap.Make(T, st), ExportScope.Claude);
        Assert.Contains("5-hour session: no value (the reset time, 2030-01-01 09:40 +00:00, has passed; the new value has not been reported yet)", t);
        Assert.DoesNotContain("97%", t);
        Assert.Contains("Weekly: 88% remaining (12% used), unchanged since", t);
    }

    [Fact]
    public void Claude_NothingReportedForHours_SaysNoRecentReport_WithBothAges()
    {
        string t = Text(Snap.Make(T, Claude(T.AddHours(-5), T.AddHours(-4))), ExportScope.Claude);
        Assert.Contains("Status: No recent report from Claude Code", t);
        Assert.Contains("Status line last ran: 2030-01-01 06:00 +00:00 (about 4 h ago)", t);
        Assert.Contains("5-hour session: 58% remaining (42% used), OLD reading: last changed 5 h ago", t);
    }

    // ---------------- stale, expired, missing ----------------

    [Fact]
    public void Claude_ElapsedWindow_PrintsNoValue_NeverTheOldNumber_NeverZero()
    {
        ClaudeState st = new(Rec(97, T.AddMinutes(-20), T.AddHours(-1), T.AddHours(-1)), Rec(12, T.AddDays(4), T.AddHours(-1), T.AddHours(-1)));
        string t = Text(Snap.Make(T, st), ExportScope.Claude);
        Assert.Contains("5-hour session: no value (the reset time, 2030-01-01 09:40 +00:00, has passed; the new value has not been reported yet)", t);
        Assert.DoesNotContain("97%", t);
        Assert.DoesNotContain("3% remaining", t);
        Assert.DoesNotContain(" 0%", t);
        Assert.Contains("Weekly: 88% remaining (12% used)", t);                                 // the sibling window keeps its own state
    }

    [Fact]
    public void Claude_AllWindowsElapsed_PrintsWaitingForNextReport_AndNoNumbers()
    {
        ClaudeState st = new(Rec(97, T.AddMinutes(-20), T.AddHours(-1), T.AddHours(-1)), Rec(60, T.AddMinutes(-5), T.AddHours(-1), T.AddHours(-1)));
        string t = Text(Snap.Make(T, st), ExportScope.Claude);
        Assert.Contains("Status: Waiting for the next Claude Code report", t);
        Assert.Contains("5-hour session: no value", t);
        Assert.Contains("Weekly: no value", t);
        Assert.DoesNotMatch(new Regex(@"\d+% remaining"), t);
    }

    [Fact]
    public void Claude_NeverReported_ListsNoWindows_AndNoZero()
    {
        string t = Text(Snap.Make(T), ExportScope.Claude);
        Assert.Contains("Status: Waiting for Claude Code in a terminal", t);
        Assert.Contains("5-hour session: no data\nWeekly: no data\n", t);                      // the two windows Claude really has are listed, as "no data"
        Assert.Contains("Numbers last changed: never reported", t);
        Assert.DoesNotContain("Status line last ran", t);
        Assert.DoesNotContain("remaining", t.Replace("usage REMAINING", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("0%", t);
        Assert.Contains("only from terminal Claude Code sessions", t);
    }

    [Fact]
    public void Claude_PartialData_OneWindowMissing_SaysNoDataForIt()
    {
        ClaudeState st = new(Rec(42, T.AddHours(3), T.AddMinutes(-3), T.AddMinutes(-3)), null);
        string t = Text(Snap.Make(T, st), ExportScope.Claude);
        Assert.Contains("5-hour session: 58% remaining (42% used)", t);
        Assert.Contains("Weekly: no data", t);
        Assert.DoesNotContain("Weekly: 100%", t);
    }

    [Fact]
    public void Codex_NotReadYet_ShowsStatus_NoNumbers_AndNoneYet()
    {
        string t = Text(Snap.Make(T), ExportScope.Codex);
        Assert.Contains("Status: Not read yet", t);
        Assert.Contains("Windows: none known yet (Codex's windows are known only after a successful read)", t);     // nothing invented before a read
        Assert.Contains("Last successful read: none yet", t);
        Assert.DoesNotContain("remaining (", t);
    }

    [Fact]
    public void Codex_NotInstalled_UsesTheBoundedIssueText()
    {
        string t = Text(Snap.Make(T, codexIssue: ProviderIssue.CodexNotInstalled, attempt: new ProviderAttempt(T, ProviderErrorKind.NotFound)), ExportScope.Codex);
        Assert.Contains("Status: Codex is not installed", t);
        Assert.Contains("Windows: none known yet", t);
        Assert.Contains("Last successful read: none yet", t);
    }

    [Fact]
    public void Codex_FailedAfterAGoodRead_KeepsTheOldValues_LabelledWithTheirRealAge_AndSaysSo()
    {
        AppSnapshot s = Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-30)), codexIssue: ProviderIssue.TemporaryProviderFailure,
            attempt: new ProviderAttempt(T.AddMinutes(-1), ProviderErrorKind.Timeout));
        string t = Text(s, ExportScope.Codex);
        Assert.Contains("Status: Codex did not answer in time; will try again", t);
        Assert.Contains("Detail: Showing the last successful read.", t);
        Assert.Contains("69% remaining (31% used), older reading: last read 30 min ago", t);
        Assert.Contains("Last successful read: 2030-01-01 09:30 +00:00 (30 min ago)", t);      // the failed attempt did not move it
    }

    [Fact]
    public void Codex_Reading_IsStated_AndStillReportsTheLastRead()
    {
        string t = Text(Snap.Make(T, codex: Snap.Codex(T.AddMinutes(-12)), reading: true, allowed: false), ExportScope.Codex);
        Assert.Contains("Status: Reading...", t);
        Assert.Contains("Last successful read: 2030-01-01 09:48 +00:00 (12 min ago)", t);
    }

    [Fact]
    public void ProviderTurnedOff_SaysSo_AndPrintsNothingElseAboutIt()
    {
        string t = Text(Snap.Make(T, claudeEnabled: false, codexEnabled: false));
        Assert.Contains("CODEX\nStatus: Turned off\n", t);
        Assert.Contains("CLAUDE\nStatus: Turned off\n", t);
        Assert.DoesNotContain("remaining (", t);
        Assert.DoesNotContain("Numbers last changed", t);
        Assert.DoesNotContain("Note:", t);
    }

    [Fact]
    public void Codex_WindowWithoutAResetTime_SaysSo_InsteadOfInventingOne()
    {
        AppSnapshot s = Fresh();
        WindowView w = s.Codex.View.Windows[0] with { ResetsAt = null, ResetsIn = null };
        AppSnapshot edited = s with { Codex = s.Codex with { View = s.Codex.View with { Windows = [w, s.Codex.View.Windows[1]] } } };
        Assert.Contains("5-hour session: 69% remaining (31% used); reset time not reported", Text(edited, ExportScope.Codex));
    }

    [Fact]
    public void LimitReached_IsStated()
    {
        AppSnapshot s = Fresh();
        WindowView w = s.Codex.View.Windows[0] with { LimitReached = true, DisplayedUsedPercent = 100, DisplayedRemainingPercent = 0 };
        AppSnapshot edited = s with { Codex = s.Codex with { View = s.Codex.View with { Windows = [w, s.Codex.View.Windows[1]] } } };
        Assert.Contains("5-hour session: 0% remaining (100% used), limit reached;", Text(edited, ExportScope.Codex));   // a real 0% remaining, from a real reading
    }

    // ---------------- time zones ----------------

    [Fact]
    public void TimeZone_OffsetIsCorrectAtEachInstant_AcrossTheSpringForwardTransition()
    {
        TimeZoneInfo berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        DateTimeOffset now = new(2030, 3, 30, 23, 30, 0, TimeSpan.Zero);                    // 00:30 +01:00 on Sun 31 Mar; clocks go forward at 01:00 UTC
        DateTimeOffset reset = new(2030, 3, 31, 3, 0, 0, TimeSpan.Zero);                    // 05:00 +02:00 (after the change)
        ClaudeState st = new(Rec(10, reset, now.AddMinutes(-1), now.AddMinutes(-1)), Rec(20, now.AddDays(3), now.AddMinutes(-1), now.AddMinutes(-1)));
        string t = Text(Snap.Make(now, st), ExportScope.Claude, berlin);
        Assert.Contains("Captured: 2030-03-31 00:30 +01:00", t);
        Assert.Contains("5-hour session: 90% remaining (10% used); resets 2030-03-31 05:00 +02:00 (in 3 h 30 m)", t);   // wall-clock gap is 4 h 30 m; the true duration is 3 h 30 m
        Assert.Contains("Numbers last changed: 2030-03-31 00:29 +01:00 (1 min ago)", t);
    }

    [Fact]
    public void TimeZone_NegativeAndHalfHourOffsets_AreFormatted()
    {
        DateTimeOffset i = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal("2030-01-01 05:00 -05:00", UsageSnapshotText.Format(i, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));
        Assert.Equal("2030-01-01 15:30 +05:30", UsageSnapshotText.Format(i, TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")));
    }

    [Fact]
    public void TimeZone_AResetOnTheNextLocalDay_ShowsItsDate()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");     // +09:00: 10:00 UTC is 19:00; a 20:00 UTC reset is the next local day
        ClaudeState st = new(Rec(10, T.AddHours(10), T.AddMinutes(-1), T.AddMinutes(-1)), null);
        string t = Text(Snap.Make(T, st), ExportScope.Claude, tokyo);
        Assert.Contains("Captured: 2030-01-01 19:00 +09:00", t);
        Assert.Contains("resets 2030-01-02 05:00 +09:00", t);
    }

    // ---------------- nothing but the allowed content ----------------

    [Fact]
    public void NoPaths_Credentials_Accounts_OrUrls_InAnyState()
    {
        var snapshots = new List<AppSnapshot>
        {
            Fresh(),
            Snap.Make(T),
            Snap.Make(T, claudeEnabled: false, codexEnabled: false),
            Snap.Make(T, Claude(T.AddHours(-5), T.AddMinutes(-2)), Snap.Codex(T.AddMinutes(-30)), claudeIssue: ProviderIssue.ClaudeStateUnreadable,
                codexIssue: ProviderIssue.CodexRequestRejected, attempt: new ProviderAttempt(T, ProviderErrorKind.Rejected)),
            Snap.Make(T, codexIssue: ProviderIssue.CodexNotTrusted, attempt: new ProviderAttempt(T, ProviderErrorKind.ValidationFailed)),
        };
        foreach (AppSnapshot s in snapshots)
        {
            foreach (ExportScope scope in Enum.GetValues<ExportScope>())
            {
                string t = Text(s, scope);
                foreach (string bad in new[] { "\\", "://", "@", "token", "password", "credential", "cookie", "auth", "account", "email", "C:", "Users", ".json", ".exe" })
                {
                    Assert.False(t.Contains(bad, StringComparison.OrdinalIgnoreCase), $"\"{bad}\" found in:\n{t}");
                }
            }
        }
    }

    [Fact]
    public void ProviderSuppliedWindowNames_AreSanitised_SoNothingOddCanRideAlong()
    {
        AppSnapshot s = Fresh();
        string canary = "Spark\r\nC:\\Users\\someone\\secret@example.test " + new string('x', 200);
        WindowView w = s.Codex.View.Windows[0] with { GroupName = canary };
        AppSnapshot edited = s with { Codex = s.Codex with { View = s.Codex.View with { Windows = [w, s.Codex.View.Windows[1]] } } };
        string t = Text(edited, ExportScope.Codex);
        Assert.DoesNotContain("\\", t);
        Assert.DoesNotContain("@", t);
        Assert.DoesNotContain('\r', t);
        string line = t.Split('\n').Single(l => l.Contains("5-hour session", StringComparison.Ordinal));
        Assert.True(line.IndexOf(" 5-hour session:", StringComparison.Ordinal) <= 40, "group name is capped at 40 characters");
        Assert.All(t.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.DoesNotContain('\t', l));
    }

    [Fact]
    public void EveryLine_IsOneOfTheKnownShapes()
    {
        var known = new[]
        {
            "QuotaDuck usage snapshot", "Captured: ", "Percentages are usage REMAINING", "CODEX", "CLAUDE", "Status: ", "Detail: ", "Last successful read: ",
            "Numbers last changed: ", "Status line last ran: ", "Note: ", "5-hour session: ", "Weekly: ", "Windows: ",
        };
        foreach (AppSnapshot s in new[] { Fresh(), Snap.Make(T), Snap.Make(T, Claude(T.AddHours(-5), T.AddMinutes(-2)), Snap.Codex(T.AddMinutes(-30))) })
        {
            foreach (string line in Text(s).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.True(known.Any(k => line.StartsWith(k, StringComparison.Ordinal)), $"unexpected line: {line}");
            }
        }
    }
}
