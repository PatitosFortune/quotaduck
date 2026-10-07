using QuotaDuck.Art;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;

namespace QuotaDuck.App.Presentation;

/// <summary>
/// AppSnapshot -> plain display models. Pure (no WPF, no clock reads: the snapshot carries its own instant), so every state is
/// string-testable. No provider logic lives here: states, ages, countdowns and the "never show an obsolete number" rule all come from Core.
/// </summary>
public static class Presenter
{
    public static ShellPresentation Present(AppSnapshot s, RefreshFeedback feedback, TimeZoneInfo zone, SetupContext? setup = null)
    {
        ProviderCardModel claude = ClaudeCard(s, zone, setup);
        ProviderCardModel codex = CodexCard(s, zone);
        string? fb = feedback == RefreshFeedback.None ? null : IssueText.ForRefreshFeedback(feedback, s.Codex.Issue);
        return new ShellPresentation(claude, codex, Tooltip(s), fb);
    }

    // ---------------- Claude (passive: no refresh control of any kind) ----------------

    private static ProviderCardModel ClaudeCard(AppSnapshot s, TimeZoneInfo zone, SetupContext? setup)
    {
        ProviderSnapshot p = s.Claude;
        List<WindowRowModel> rows = p.View.Windows.Select(w => Row("Claude", w, s.At, zone, "Last changed ")).ToList();

        string headline;
        string? note = null;
        Tone tone;
        switch (p.Issue)
        {
            case ProviderIssue.NotConfigured:
                headline = IssueText.For(p.Issue);
                tone = Tone.Muted;
                break;
            case ProviderIssue.WaitingForTerminalClaudeCode:
                headline = IssueText.For(p.Issue);
                tone = Tone.Muted;
                break;
            case ProviderIssue.ClaudeStateUnreadable:
            case ProviderIssue.ClaudeStateUnsupportedVersion:
                headline = "Showing the last known values";
                note = IssueText.For(p.Issue);
                tone = Tone.Warn;
                break;
            default:
                (headline, tone) = p.View.Reason switch
                {
                    ProviderReason.AwaitingNextClaudeReport => ("Waiting for the next Claude Code report", Tone.Muted),
                    ProviderReason.ClaudeNoRecentReport => ("No recent report from Claude Code", Tone.Muted),
                    _ => ("Reported by Claude Code", ToneOf(p.View.Rollup)),
                };
                if (p.View.Reason == ProviderReason.ClaudeNoRecentReport)
                {
                    note = "Usage elsewhere (VS Code panel, Claude Desktop, claude.ai) may not appear until a terminal Claude Code session reports.";
                }

                break;
        }

        return new ProviderCardModel(
            "Claude",
            headline,
            note,
            tone,
            rows,
            Refresh: null,                                                  // there is nothing to refresh: Claude only reports when Claude Code runs
            Footer: setup is not null && p.Issue == ProviderIssue.WaitingForTerminalClaudeCode ? null : "Claude reports only from terminal Claude Code sessions.",   // the guide says it already
            Pond: PondMapping.For(p, faceRight: true),
            PondDescription: PondMapping.Describe("Claude", p),
            // first-run guidance only while Claude has NEVER reported (no state file yet); never once data exists
            Setup: setup is not null && p.Issue == ProviderIssue.WaitingForTerminalClaudeCode ? SetupGuidePresenter.Build(setup) : null);
    }

    // ---------------- Codex (pull: manual refresh) ----------------

    private static ProviderCardModel CodexCard(AppSnapshot s, TimeZoneInfo zone)
    {
        ProviderSnapshot p = s.Codex;
        List<WindowRowModel> rows = p.View.Windows.Select(w => Row("Codex", w, s.At, zone, "Read ")).ToList();

        string headline;
        string? note = null;
        Tone tone;
        if (p.Issue == ProviderIssue.NotConfigured)
        {
            headline = IssueText.For(p.Issue);
            tone = Tone.Muted;
        }
        else if (p.Activity == ProviderActivity.Reading)
        {
            headline = "Reading…";
            tone = Tone.Neutral;
        }
        else if (p.Issue == ProviderIssue.NoData)
        {
            headline = "Not read yet";
            tone = Tone.Muted;
        }
        else if (p.Issue != ProviderIssue.None)
        {
            headline = IssueText.For(p.Issue);
            tone = p.Issue == ProviderIssue.TemporaryProviderFailure ? Tone.Warn : Tone.Error;
            if (rows.Count > 0)
            {
                note = "Showing the last successful read.";
            }
        }
        else
        {
            (headline, tone) = p.View.Reason switch
            {
                ProviderReason.AwaitingCodexRead => ("Window reset; a read is due", Tone.Muted),
                ProviderReason.CodexReadsPaused => ("Not refreshed recently", Tone.Muted),
                _ => ("Up to date", ToneOf(p.View.Rollup)),
            };
        }

        RefreshButtonModel? refresh = p.Issue == ProviderIssue.NotConfigured ? null : RefreshButton(s);
        return new ProviderCardModel("Codex", headline, note, tone, rows, refresh, Footer: null, Pond: PondMapping.For(p, faceRight: false), PondDescription: PondMapping.Describe("Codex", p));
    }

    private static RefreshButtonModel RefreshButton(AppSnapshot s)
    {
        if (s.Codex.Activity == ProviderActivity.Reading)
        {
            return new RefreshButtonModel(false, "Reading…", null);
        }

        if (s.CodexRefreshAllowed)
        {
            return new RefreshButtonModel(true, "Refresh", null);
        }

        if (s.CodexRefreshAllowedAt is { } at)
        {
            int secs = (int)Math.Ceiling(Math.Max(0, (at - s.At).TotalSeconds));
            return new RefreshButtonModel(false, $"Refresh in {secs} s", "Codex can be read at most once a minute.");
        }

        return new RefreshButtonModel(false, "Refresh", null);
    }

    // ---------------- windows ----------------

    private static WindowRowModel Row(string provider, WindowView w, DateTimeOffset now, TimeZoneInfo zone, string agePrefix)
    {
        string label = w.Label.Text;
        string stateText = w.State switch
        {
            WindowState.Fresh => "Fresh",
            WindowState.Quiet => "Quiet",
            WindowState.Sleeping => "Sleeping (old)",
            WindowState.Elapsed => "Reset",
            _ => "No data",
        };

        double? fraction = null;
        string percent, remaining = string.Empty;
        if (w.DisplayedUsedPercent is { } used)
        {
            fraction = Math.Clamp(used / 100.0, 0, 1);
            percent = TimeText.Percent(used) + " used";
            remaining = TimeText.Percent(Math.Max(0, 100 - used)) + " left";
        }
        else
        {
            percent = w.State == WindowState.Elapsed ? "Reset; new value unknown" : "No data";       // never a stale number, never 0%
        }

        string reset = string.Empty;
        if (w.State == WindowState.Elapsed && w.ElapsedSince is { } since)
        {
            reset = "Reset " + TimeText.Humanize(since) + " ago";
        }
        else if (w.ResetsIn is { } left && w.ResetsAt is { } at)
        {
            reset = "Resets in " + TimeText.Humanize(left) + " (" + TimeText.LocalReset(at, now, zone) + ")";
        }

        string age = w.EvidenceAge is { } a ? agePrefix + TimeText.Humanize(a) + " ago" : string.Empty;
        string accessible = $"{provider} {label}: {percent}" + (remaining.Length > 0 ? ", " + remaining : string.Empty) + $", {stateText}" + (reset.Length > 0 ? ", " + reset : string.Empty);
        return new WindowRowModel(label, fraction, percent, remaining, stateText, reset, age, accessible, w.State switch
        {
            WindowState.Fresh => Tone.Good,
            WindowState.Quiet => Tone.Neutral,
            _ => Tone.Muted,
        }, PondMapping.GaugeFor(w));
    }

    private static Tone ToneOf(ProviderRollup r) => r switch
    {
        ProviderRollup.Fresh => Tone.Good,
        ProviderRollup.Quiet => Tone.Neutral,
        _ => Tone.Muted,
    };

    // ---------------- tray tooltip (<= 127 chars, plain text) ----------------

    public static string Tooltip(AppSnapshot s)
    {
        string text = "QuotaDuck | Claude " + Compact(s.Claude, "–")
                      + " | Codex " + Compact(s.Codex, s.Codex.Activity == ProviderActivity.Reading ? "reading" : "–");
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    private static string Compact(ProviderSnapshot p, string none)
    {
        if (p.Issue == ProviderIssue.NotConfigured)
        {
            return "off";
        }

        if (p.Issue == ProviderIssue.WaitingForTerminalClaudeCode)
        {
            return "waiting";
        }

        var parts = new List<string>();
        foreach (WindowView w in p.View.Windows)
        {
            string tag = ShortLabel(w.Label.Text);
            parts.Add(w.State switch
            {
                WindowState.Elapsed => tag + " reset",
                WindowState.NoData => tag + " –",
                WindowState.Sleeping => tag + " " + TimeText.Percent(w.DisplayedUsedPercent ?? 0) + " (old)",
                _ => tag + " " + TimeText.Percent(w.DisplayedUsedPercent ?? 0),
            });
        }

        return parts.Count == 0 ? none : string.Join(" ", parts);
    }

    private static string ShortLabel(string label) => label switch
    {
        "5-hour" => "5h",
        "Weekly" => "wk",
        _ => label.Length > 8 ? label[..8] : label,
    };
}
