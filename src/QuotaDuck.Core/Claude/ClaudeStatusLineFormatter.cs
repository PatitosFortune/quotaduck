using System.Globalization;

namespace QuotaDuck.Core.Claude;

/// <summary>
/// The one-line text the bridge prints in NORMAL mode. Pure; built only from the persisted state and the Core derivation,
/// so it can never contain anything from the input payload. Rules (same honesty as the UI):
/// <code>
///  Fresh / Quiet ... "5h 24% used"            Sleeping ... "5h 24% used (old)"
///  Elapsed ......... "5h reset"               NoData ..... "5h --"
///  nothing known ... "QuotaDuck | waiting for first report"
/// </code>
/// A window is NEVER rendered as 0% unless the provider reported 0.
/// </summary>
public static class ClaudeStatusLineFormatter
{
    public const string DefaultLabel = "QuotaDuck";
    public const int MaxLabelLength = 24;

    /// <summary>Labels are restricted to a small printable-ASCII set so they can never inject terminal control sequences.</summary>
    public static bool TrySanitizeLabel(string? candidate, out string label)
    {
        label = DefaultLabel;
        if (candidate is null || candidate.Length is 0 or > MaxLabelLength)
        {
            return false;
        }

        foreach (char c in candidate)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        if (candidate.Trim().Length == 0)
        {
            return false;
        }

        label = candidate;
        return true;
    }

    public static string Format(ClaudeState state, DateTimeOffset now, FreshnessPolicy policy, string label = DefaultLabel)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);

        ProviderView view = ProviderDeriver.Derive(ClaudeObservations.ToProviderInput(state.FiveHour, state.SevenDay), now, policy);
        WindowView five = view.Windows.First(w => w.Key == WindowKey.ClaudeFiveHour);
        WindowView seven = view.Windows.First(w => w.Key == WindowKey.ClaudeSevenDay);
        if (five.State == WindowState.NoData && seven.State == WindowState.NoData)
        {
            return label + " | waiting for first report";
        }

        return label + " | " + Part("5h", five) + " | " + Part("wk", seven);
    }

    private static string Part(string name, WindowView w) => w.State switch
    {
        WindowState.Fresh or WindowState.Quiet => name + " " + Pct(w.DisplayedUsedPercent!.Value) + " used",
        WindowState.Sleeping => name + " " + Pct(w.DisplayedUsedPercent!.Value) + " used (old)",
        WindowState.Elapsed => name + " reset",
        _ => name + " --",
    };

    private static string Pct(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
