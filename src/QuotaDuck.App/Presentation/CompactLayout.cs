using QuotaDuck.Art;

namespace QuotaDuck.App.Presentation;

// Presentation-only reshaping of the provider card models for the compact (side-by-side or stacked) flyout.
// No provider logic, no clock: every string comes from the existing ProviderCardModel / WindowRowModel.

public enum CompactMode
{
    SideBySide = 0,
    Stacked = 1,
}

public sealed record CompactRowModel(
    string Label,
    string StateTag,
    string Primary,
    string ResetLine,
    GaugeScene Gauge,
    Tone Tone,
    string AccessibleName);

public sealed record CompactCardModel(
    string Name,
    string Headline,
    string Freshness,
    string? Note,
    string? Footer,
    Tone Tone,
    IReadOnlyList<CompactRowModel> Rows,
    RefreshButtonModel? Refresh,
    PondScene Pond,
    string PondDescription,
    SetupGuideModel? Setup,
    string SetupButtonText = "Set up",
    string SetupButtonName = "Set up Claude Code: show the instructions")
{
    public bool HasRows => Rows.Count > 0 && Setup is null;
}

public static class CompactCard
{
    /// <summary>"% left" is the one primary number (the pond is remaining quota); "% used" stays in the accessible name only.</summary>
    public static CompactCardModel From(ProviderCardModel c, bool setupExpanded = false)
    {
        List<CompactRowModel> rows = c.Windows.Select(w =>
        {
            string primary = w.RemainingText.Length > 0 ? w.RemainingText : w.StateText == "Reset" ? "Reset" : "No data";
            string tag = w.StateText is "Sleeping (old)" or "Reset" or "No data" ? w.StateText : string.Empty;
            if (tag == primary)
            {
                tag = string.Empty;                                                  // never say "Reset" twice
            }

            string reset = w.ResetText.Replace(" (", " · ", StringComparison.Ordinal).TrimEnd(')');
            return new CompactRowModel(w.Label, tag.Length > 0 ? " · " + tag : tag, primary, reset, w.Gauge, w.Tone, w.AccessibleName);
        }).ToList();

        string freshness = c.Windows.Select(w => w.AgeText).FirstOrDefault(a => a.Length > 0) ?? string.Empty;
        return new CompactCardModel(c.Name, c.Headline, freshness, c.Note, c.Footer, c.Tone, rows, c.Refresh, c.Pond, c.PondDescription, c.Setup,
            setupExpanded ? "Hide" : "Set up",
            setupExpanded ? "Hide the Claude Code setup instructions" : "Set up Claude Code: show the instructions");
    }
}

/// <summary>
/// Side-by-side only when it can be done without squeezing: enough work-area width for two full cards, no expanded help, and no
/// accessibility request for larger text. Anything else stacks the same cards. Pure.
/// </summary>
public sealed record CompactPresentation(CompactCardModel Claude, CompactCardModel Codex, SetupGuideModel? Guide, string? RefreshFeedback);

public static class CompactLayoutPolicy
{
    /// <summary>Content widths in DIP: two cards (the 288-DIP pond at an integer art scale + padding) + gutters; or one card when stacked.</summary>
    public const double SideBySideContentWidthDip = 648, StackedContentWidthDip = 332;

    /// <summary>Side-by-side window (content + frame) with some air; narrower work areas stack.</summary>
    public const double SideBySideMinWorkWidthDip = 700;

    public static CompactMode Choose(double workAreaWidthDip, bool helpExpandedFullHeight, double textScale = 1.0) =>
        helpExpandedFullHeight || workAreaWidthDip < SideBySideMinWorkWidthDip || textScale > 1.25
            ? CompactMode.Stacked
            : CompactMode.SideBySide;
}
