using System.Globalization;
using QuotaDuck.Art;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;

namespace QuotaDuck.App.Presentation;

/// <summary>
/// Provider state -> pond and duck. Pure and exhaustively testable. The water level is only ever drawn from a number Core says is truthful to
/// show (<c>DisplayedUsedPercent</c>): an elapsed window is a HATCHED full bowl with a hollow "?" duck, never a solid 100%; no data is a dry
/// hatched bowl. The exact numbers are always printed beside the art; the art never replaces them.
/// <code>
///  tightest numeric window  -> Normal  (Fresh / Quiet)      awake duck: floating, or standing when the water is shallow
///                            Subdued (Sleeping)           sleeping duck, quiet colours
///  no number, window elapsed -> Unknown                     hatched water, hollow duck with "?"
///  no number, no failure     -> Empty                       dry hatched bowl, hollow duck with "?"
///  no number, failure        -> Error                       dry hatched bowl, standing duck with "!"
///  number AND a failure      -> as above, plus a "!" beside the duck
///  Codex reading             -> "..." dots beside the duck
/// </code>
/// </summary>
public static class PondMapping
{
    public static bool IsFailure(ProviderIssue issue) => issue is
        ProviderIssue.CodexNotInstalled or ProviderIssue.CodexNotTrusted or ProviderIssue.CodexRequestRejected
        or ProviderIssue.CodexProtocolUnsupported or ProviderIssue.ProviderUnavailable or ProviderIssue.TemporaryProviderFailure
        or ProviderIssue.ClaudeStateUnreadable or ProviderIssue.ClaudeStateUnsupportedVersion;

    public static PondScene For(ProviderSnapshot p, bool faceRight)
    {
        bool failure = IsFailure(p.Issue);
        bool busy = p.Activity == ProviderActivity.Reading;
        WindowView? tight = p.View.Windows
            .Where(w => w.DisplayedUsedPercent is not null)
            .OrderByDescending(w => w.DisplayedUsedPercent)
            .FirstOrDefault();

        if (tight is not null)
        {
            double remaining = Math.Clamp(1 - tight.DisplayedUsedPercent!.Value / 100.0, 0, 1);
            bool shallow = PondGeometry.IsShallow(remaining);
            if (tight.State == WindowState.Sleeping)
            {
                return new PondScene(remaining, PondCondition.Subdued, shallow ? DuckPose.SleepingStanding : DuckPose.SleepingFloating, faceRight, failure, busy);
            }

            return new PondScene(remaining, PondCondition.Normal, shallow ? DuckPose.Standing : DuckPose.Floating, faceRight, failure, busy);
        }

        if (p.Issue == ProviderIssue.NotConfigured)
        {
            return new PondScene(null, PondCondition.Empty, DuckPose.Unknown, faceRight);
        }

        if (p.View.Windows.Any(w => w.State == WindowState.Elapsed))
        {
            return new PondScene(null, PondCondition.Unknown, DuckPose.Unknown, faceRight, Alert: failure, Busy: busy);
        }

        if (failure)
        {
            return new PondScene(null, PondCondition.Error, DuckPose.Alert, faceRight, Busy: busy);
        }

        return new PondScene(null, PondCondition.Empty, DuckPose.Unknown, faceRight, Busy: busy);
    }

    /// <summary>The text equivalent of the pond (automation name): what the picture shows, in words.</summary>
    public static string Describe(string provider, ProviderSnapshot p)
    {
        PondScene s = For(p, true);
        WindowView? tight = p.View.Windows.Where(w => w.DisplayedUsedPercent is not null).OrderByDescending(w => w.DisplayedUsedPercent).FirstOrDefault();
        string extra = (s.Alert ? " A warning mark is shown." : string.Empty) + (s.Busy ? " Reading now." : string.Empty);
        switch (s.Condition)
        {
            case PondCondition.Normal:
            case PondCondition.Subdued:
                string pct = ((int)Math.Round(s.Remaining!.Value * 100)).ToString(CultureInfo.InvariantCulture);
                string duck = s.Duck switch
                {
                    DuckPose.SleepingFloating or DuckPose.SleepingStanding => "the duck is sleeping because the data is old",
                    DuckPose.Standing => "the duck is standing in shallow water",
                    _ => "the duck is swimming",
                };
                return $"{provider} pond: {pct} percent of the water is left in the tightest window ({tight!.Label.Text}); {duck}.{extra}";
            case PondCondition.Unknown:
                return $"{provider} pond: the quota window has reset and the new level is not known yet, shown as hatched water; the duck is unsure.{extra}";
            case PondCondition.Error:
                return $"{provider} pond: dry, because the quota could not be read; the duck shows a warning.{extra}";
            default:
                return $"{provider} pond: dry, there is no data yet.{extra}";
        }
    }

    public static GaugeScene GaugeFor(WindowView w) => w.State switch
    {
        WindowState.Elapsed => new GaugeScene(null, GaugeCondition.Unknown),
        WindowState.NoData => new GaugeScene(null, GaugeCondition.Empty),
        _ => w.DisplayedUsedPercent is { } used
            ? new GaugeScene(Math.Clamp(1 - used / 100.0, 0, 1), w.State == WindowState.Sleeping ? GaugeCondition.Subdued : GaugeCondition.Normal)
            : new GaugeScene(null, GaugeCondition.Empty),
    };
}
