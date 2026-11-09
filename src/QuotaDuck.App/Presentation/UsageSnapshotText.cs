using System.Globalization;
using System.Text;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;

namespace QuotaDuck.App.Presentation;

/// <summary>Which provider sections the "Copy" action puts in the text.</summary>
public enum ExportScope
{
    Codex = 1,
    Claude = 2,
    Both = 3,
}

/// <summary>
/// AppSnapshot -> the plain-text usage report for the clipboard. Pure: no clock reads (the snapshot's own instant is the capture time, so the
/// text agrees with the ages on screen), no I/O, no provider logic. Every number comes from the Core-derived window view, so an expired or
/// missing value is never printed (no 0%, no old number). Only enums, percentages, window labels and timestamps can reach the text: no paths,
/// account data or raw provider output. Culture-invariant, ASCII only, LF line endings.
/// </summary>
public static class UsageSnapshotText
{
    private const string Stamp = "yyyy-MM-dd HH:mm zzz";

    /// <summary>While the status line ran this recently, unchanged numbers are reported as "unchanged", not as "no report".</summary>
    private static readonly TimeSpan StatusLineRecent = TimeSpan.FromMinutes(10);

    public static string Build(AppSnapshot s, ExportScope scope, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(zone);
        ShellPresentation p = Presenter.Present(s, RefreshFeedback.None, zone);

        var sb = new StringBuilder();
        sb.Append("QuotaDuck usage snapshot\n");
        sb.Append("Captured: ").Append(Format(s.At, zone)).Append(" (when this text was copied; each provider's own data time is listed below)\n");
        sb.Append("Percentages are usage REMAINING; the percentage used is in brackets.\n");

        if (scope is ExportScope.Codex or ExportScope.Both)
        {
            sb.Append('\n');
            Section(sb, "CODEX", s, s.Codex, p.Codex, "last read", zone);
            sb.Append("Last successful read: ").Append(Moment(s.Codex.LastObservationAt, s.At, zone, "none yet")).Append('\n');
        }

        if (scope is ExportScope.Claude or ExportScope.Both)
        {
            sb.Append('\n');
            Section(sb, "CLAUDE", s, s.Claude, p.Claude, "last changed", zone);
            if (s.Claude.Issue != ProviderIssue.NotConfigured)
            {
                sb.Append("Numbers last changed: ").Append(Moment(s.Claude.LastObservationAt, s.At, zone, "never reported")).Append('\n');
                if (s.Claude.View.FreshestReceivedAge is { } received)
                {
                    sb.Append("Status line last ran: ").Append(Format(s.At - received, zone)).Append(" (about ").Append(TimeText.Humanize(received)).Append(" ago)\n");
                }

                sb.Append("Note: Claude reports only from terminal Claude Code sessions; usage elsewhere (VS Code panel, Claude Desktop, claude.ai) may not be reflected.\n");
            }
        }

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, AppSnapshot s, ProviderSnapshot p, ProviderCardModel card, string ageWord, TimeZoneInfo zone)
    {
        sb.Append(title).Append('\n');
        string status = Ascii(card.Headline);
        if (p.View.Reason == ProviderReason.ClaudeNoRecentReport && p.Issue == ProviderIssue.None && p.View.FreshestReceivedAge is { } ran && ran < StatusLineRecent)
        {
            // The status line is still running; the percentages simply have not changed. That is not evidence of a stale report.
            status = "Claude Code is still reporting; the numbers have not changed for a while";
        }

        sb.Append("Status: ").Append(status).Append('\n');
        if (card.Note is { Length: > 0 } note && p.Issue != ProviderIssue.NotConfigured && p.View.Reason != ProviderReason.ClaudeNoRecentReport)   // that note is replaced by the standing Claude note
        {
            sb.Append("Detail: ").Append(Ascii(note)).Append('\n');
        }

        if (p.Issue == ProviderIssue.NotConfigured)
        {
            return;
        }

        if (p.View.Windows.Count == 0)
        {
            // Codex's windows are only known from a successful read; none is invented before that.
            sb.Append("Windows: none known yet (Codex's windows are known only after a successful read)\n");
            return;
        }

        // Every window whose identity is known is listed, with "no data" where there is no usable value (never a 0% placeholder).
        foreach (WindowView w in p.View.Windows)
        {
            sb.Append(Line(w, s.At, zone, ageWord, claude: title == "CLAUDE")).Append('\n');
        }
    }

    private static string Line(WindowView w, DateTimeOffset now, TimeZoneInfo zone, string ageWord, bool claude)
    {
        string name = Name(w);
        switch (w.State)
        {
            case WindowState.NoData:
                return name + ": no data";
            case WindowState.Elapsed:
                DateTimeOffset? at = w.ResetsAt ?? (w.ElapsedSince is { } since ? now - since : null);
                string when = at is { } a ? ", " + Format(a, zone) + "," : string.Empty;
                return name + ": no value (the reset time" + when + " has passed; the new value has not been reported yet)";
        }

        double used = w.DisplayedUsedPercent ?? 0;
        double left = w.DisplayedRemainingPercent ?? Math.Max(0, 100 - used);
        var sb = new StringBuilder(name).Append(": ").Append(TimeText.Percent(left)).Append(" remaining (").Append(TimeText.Percent(used)).Append(" used)");
        if (w.State != WindowState.Fresh && w.EvidenceAge is { } age)
        {
            if (claude && w.ReceivedAge is { } ran && ran < StatusLineRecent)
            {
                // The status line ran recently and reported this same value: unchanged, not old.
                sb.Append(", unchanged since ").Append(Format(now - age, zone)).Append(" (").Append(TimeText.Humanize(age)).Append(" ago); status line ran ").Append(TimeText.Humanize(ran)).Append(" ago");
            }
            else
            {
                sb.Append(w.State == WindowState.Sleeping ? ", OLD reading: " : ", older reading: ").Append(ageWord).Append(' ').Append(TimeText.Humanize(age)).Append(" ago");
            }
        }

        if (w.LimitReached)
        {
            sb.Append(", limit reached");
        }

        if (w.ResetsAt is { } r)
        {
            sb.Append("; resets ").Append(Format(r, zone));
            if (w.ResetsIn is { } inn)
            {
                sb.Append(" (in ").Append(TimeText.Humanize(inn)).Append(')');
            }
        }
        else
        {
            sb.Append("; reset time not reported");
        }

        return sb.ToString();
    }

    private static string Name(WindowView w)
    {
        string label = w.Label.Text switch
        {
            "5-hour" => "5-hour session",
            var other => Sanitize(other),
        };
        // Codex's default limit bucket is just "codex": the section title already says that, so only a distinguishing bucket name is printed.
        return w.GroupName is { Length: > 0 } g && Sanitize(g) is { Length: > 0 } group && !group.Equals("codex", StringComparison.OrdinalIgnoreCase) ? group + " " + label : label;
    }

    private static string Moment(DateTimeOffset? at, DateTimeOffset now, TimeZoneInfo zone, string none) =>
        at is { } t ? Format(t, zone) + " (" + TimeText.Humanize(now - t) + " ago)" : none;

    /// <summary>Local time with its UTC offset (DST-correct for the zone at that instant): "2026-11-09 18:47 +01:00".</summary>
    internal static string Format(DateTimeOffset t, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString(Stamp, CultureInfo.InvariantCulture);

    /// <summary>Provider-supplied label text: letters, digits and a few separators only, bounded, so nothing odd can ride along.</summary>
    private static string Sanitize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (sb.Length >= 40)
            {
                break;
            }

            sb.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Ascii(string text) => text.Replace("…", "...", StringComparison.Ordinal);
}
