using QuotaDuck.Art;

namespace QuotaDuck.App.Presentation;

// Presentation-only reshaping of the provider card models for the compact (side-by-side or stacked) flyout.
// No provider logic, no clock: every string comes from the existing ProviderCardModel / WindowRowModel.

public enum CompactMode
{
    SideBySide = 0,
    Stacked = 1,
}

/// <summary>
/// One window row. The remaining percentage is the hero number (<see cref="Hero"/> plus a small <see cref="HeroUnit"/>); a window with no
/// usable value shows a dash and says why in words ("no value" / "no data"), never 0%. <see cref="Badge"/> spells out OLD or RESET.
/// </summary>
public sealed record CompactRowModel(
    string Label,
    string Badge,
    string Hero,
    string HeroUnit,
    string ResetLine,
    GaugeScene Gauge,
    Tone Tone,
    string AccessibleName);

public sealed record CompactCardModel(
    string Name,
    string Headline,
    string AgeText,
    string? Note,
    string StatusWord,
    string StatusGlyph,
    Tone Tone,
    IReadOnlyList<CompactRowModel> Rows,
    RefreshButtonModel? Refresh,
    PondScene Pond,
    string PondDescription,
    SetupGuideModel? Setup,
    string SetupButtonText = "Set up",
    string SetupButtonName = "Set up Claude Code: show the instructions",
    string CopyText = "Copy",
    string CopyName = "Copy usage as text to the clipboard")
{
    public bool HasRows => Rows.Count > 0 && Setup is null;

    /// <summary>The subtle header chip, e.g. "● Fresh": a glyph AND a word, so colour is never the only channel.</summary>
    public string Chip => StatusGlyph + " " + StatusWord;

    /// <summary>" - changed 3 min ago" appended to the status line, or nothing when there is no age.</summary>
    public string AgeSuffix => AgeText.Length > 0 ? " · " + AgeText.Replace(' ', ' ') : string.Empty;   // non-breaking: the age wraps as one unit, never an orphaned "ago"
}

public static class CompactCard
{
    /// <summary>"% left" is the one primary number (the pond is remaining quota); "% used" stays in the accessible name only.</summary>
    public static CompactCardModel From(ProviderCardModel c, bool setupExpanded = false)
    {
        List<CompactRowModel> rows = c.Windows.Select(w =>
        {
            string badge = w.StateText switch { "Sleeping (old)" => "OLD", "Reset" => "RESET", _ => string.Empty };
            (string hero, string unit) = w.RemainingText.Length > 0
                ? SplitRemaining(w.RemainingText)
                : ("–", w.StateText == "Reset" ? "no value" : "no data");
            string reset = w.ResetText.Replace(" (", " · ", StringComparison.Ordinal).TrimEnd(')');
            return new CompactRowModel(w.Label, badge, hero, unit, reset, w.Gauge, w.Tone, w.AccessibleName);
        }).ToList();

        (string word, string glyph) = Status(c);
        string age = c.Windows.Select(w => w.AgeText).FirstOrDefault(a => a.Length > 0) ?? string.Empty;
        return new CompactCardModel(c.Name, c.Headline, ShortAge(age), c.NoteBelongsInFooter ? null : c.Note, word, glyph, c.Tone, rows, c.Refresh, c.Pond, c.PondDescription, c.Setup,
            setupExpanded ? "Hide" : "Set up",
            setupExpanded ? "Hide the Claude Code setup instructions" : "Set up Claude Code: show the instructions",
            "Copy",
            $"Copy {c.Name} usage as text to the clipboard");
    }

    /// <summary>"58% left" -> ("58%", "left").</summary>
    private static (string Hero, string Unit) SplitRemaining(string remaining)
    {
        int i = remaining.IndexOf(' ', StringComparison.Ordinal);
        return i < 0 ? (remaining, string.Empty) : (remaining[..i], remaining[(i + 1)..]);
    }

    /// <summary>"Last changed 3 min ago" -> "changed 3 min ago"; "Read 1 min ago" -> "read 1 min ago" (the status line already says what it is).</summary>
    private static string ShortAge(string age)
    {
        if (age.StartsWith("Last ", StringComparison.Ordinal))
        {
            age = age[5..];
        }

        return age.Length == 0 ? age : char.ToLowerInvariant(age[0]) + age[1..];
    }

    /// <summary>The header chip: Fresh / Quiet / Old / Waiting / Off / Reading / Problem / Unavailable. Pure; derived from what the card already says.</summary>
    private static (string Word, string Glyph) Status(ProviderCardModel c)
    {
        if (c.Setup is not null)
        {
            return ("Waiting", "○");
        }

        bool anyValue = c.Windows.Any(w => w.Fraction is not null);
        return c.Tone switch
        {
            Tone.Error => ("Unavailable", "!"),
            Tone.Warn => ("Problem", "!"),
            Tone.Good => ("Fresh", "●"),
            Tone.Neutral => c.Windows.Count > 0 ? ("Quiet", "●") : ("Reading", "○"),
            _ when c.Headline == IssueText.For(QuotaDuck.Coordinator.ProviderIssue.NotConfigured) => ("Off", "○"),
            _ when anyValue || c.Windows.Any(w => w.StateText == "Reset") => ("Old", "◐"),
            _ => ("Waiting", "○"),
        };
    }
}

/// <summary>
/// The compact flyout content. <see cref="FooterNote"/> is the one shared, compact line beside "Copy both" (Claude's terminal-only note).
/// </summary>
public sealed record CompactPresentation(CompactCardModel Claude, CompactCardModel Codex, SetupGuideModel? Guide, string? RefreshFeedback, string CopyBothText = "Copy both", string? FooterNote = null);

/// <summary>
/// Side-by-side only when it can be done without squeezing: enough work-area width for two full cards, no expanded help, and no
/// accessibility request for larger text. Anything else stacks the same cards. Pure.
/// </summary>
public static class CompactLayoutPolicy
{
    /// <summary>The widest a pond / gauge may be, in DIP. The integer art scale is the largest that fits (see <see cref="PixelScale"/>).</summary>
    public const double ArtTargetDip = 288;

    /// <summary>Card padding (left and right each), card margin and the flyout's own margin, in DIP. Must match CompactFlyoutContent.xaml.</summary>
    public const double CardPaddingDip = 12, CardMarginDip = 4, RootMarginDip = 6;

    /// <summary>
    /// The pond and the gauge are the same logical width and share one integer scale, so at any DPI they are exactly as wide as each
    /// other and as the card's content. 288 DIP at 100/150/200%, 230.4 DIP at 125% (a whole-number scale cannot reach 288 there).
    /// </summary>
    public static double ArtWidthDip(double dpiScale) => QuotaDuck.Art.PondGeometry.Width * QuotaDuck.Art.PixelScale.Choose(QuotaDuck.Art.PondGeometry.Width, ArtTargetDip, dpiScale) / dpiScale;

    /// <summary>Flyout content width in DIP: the cards are exactly as wide as their art (plus chrome), rounded UP to whole device pixels.</summary>
    public static double ContentWidthDip(CompactMode mode, double dpiScale, double cardBorderDip = 1)
    {
        double column = ArtWidthDip(dpiScale) + 2 * CardPaddingDip + 2 * cardBorderDip + 2 * CardMarginDip;
        double width = 2 * RootMarginDip + (mode == CompactMode.SideBySide ? 2 * column : column);
        return Math.Ceiling(width * dpiScale - 1e-6) / dpiScale;
    }

    /// <summary>Side-by-side window (content + frame) with some air; narrower work areas stack.</summary>
    public const double SideBySideMinWorkWidthDip = 700;

    public static CompactMode Choose(double workAreaWidthDip, bool helpExpandedFullHeight, double textScale = 1.0) =>
        helpExpandedFullHeight || workAreaWidthDip < SideBySideMinWorkWidthDip || textScale > 1.25
            ? CompactMode.Stacked
            : CompactMode.SideBySide;
}
