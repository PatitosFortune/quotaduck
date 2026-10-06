namespace QuotaDuck.App.Presentation;

/// <summary>Visual emphasis only. Every state is ALSO spelled out in text, so colour is never the only channel.</summary>
public enum Tone
{
    Neutral = 0,
    Good = 1,
    Muted = 2,
    Warn = 3,
    Error = 4,
}

public sealed record WindowRowModel(
    string Label,
    double? Fraction,
    string PercentText,
    string RemainingText,
    string StateText,
    string ResetText,
    string AgeText,
    string AccessibleName,
    Tone Tone);

public sealed record RefreshButtonModel(bool Enabled, string Text, string? Hint);

public sealed record ProviderCardModel(
    string Name,
    string Headline,
    string? Note,
    Tone Tone,
    IReadOnlyList<WindowRowModel> Windows,
    RefreshButtonModel? Refresh,
    string? Footer);

public sealed record ShellPresentation(
    ProviderCardModel Claude,
    ProviderCardModel Codex,
    string TrayTooltip,
    string? RefreshFeedback);

/// <summary>What happened to the last manual Codex refresh the user asked for (shown briefly; never raw error text).</summary>
public enum RefreshFeedback
{
    None = 0,
    TooSoon = 1,
    Failed = 2,
}
