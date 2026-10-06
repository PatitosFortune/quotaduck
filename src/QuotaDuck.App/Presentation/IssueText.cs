using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Presentation;

/// <summary>
/// The ONLY place bounded <see cref="ProviderIssue"/> values become user-facing words. The input is an enum, so nothing a provider,
/// process or exception said can ever reach the screen: no raw messages, paths, protocol data, account ids or process error output.
/// </summary>
public static class IssueText
{
    public static string For(ProviderIssue issue) => issue switch
    {
        ProviderIssue.None => string.Empty,
        ProviderIssue.NotConfigured => "Turned off",
        ProviderIssue.WaitingForTerminalClaudeCode => "Waiting for Claude Code in a terminal",
        ProviderIssue.NoData => "No data yet",
        ProviderIssue.ClaudeStateUnreadable => "The Claude state file is temporarily unreadable; showing the last known values",
        ProviderIssue.ClaudeStateUnsupportedVersion => "The Claude state was written by a newer QuotaDuck; showing the last known values",
        ProviderIssue.CodexNotInstalled => "Codex is not installed",
        ProviderIssue.CodexNotTrusted => "The installed Codex could not be verified, so it was not started",
        ProviderIssue.CodexRequestRejected => "Codex declined the request (you may need to sign in to Codex)",
        ProviderIssue.CodexProtocolUnsupported => "Codex answered in an unexpected way (a newer version?)",
        ProviderIssue.ProviderUnavailable => "Codex could not be started",
        ProviderIssue.TemporaryProviderFailure => "Codex did not answer in time; will try again",
        _ => "Something went wrong",
    };

    /// <summary>The short sentence shown under a manual refresh that failed.</summary>
    public static string ForRefreshFeedback(RefreshFeedback feedback, ProviderIssue issue) => feedback switch
    {
        Presentation.RefreshFeedback.TooSoon => "Codex can be refreshed at most once a minute.",
        Presentation.RefreshFeedback.Failed => issue is ProviderIssue.None or ProviderIssue.NoData
            ? "The refresh did not work."
            : "The refresh did not work: " + For(issue) + ".",
        _ => string.Empty,
    };
}
