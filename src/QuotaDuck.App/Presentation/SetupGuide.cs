using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Presentation;

public enum SetupVariant
{
    /// <summary>No status line is configured: show the entry to add.</summary>
    Connect = 0,

    /// <summary>Another status line exists: never replace it; show the bridge's silent tap line.</summary>
    TapExisting = 1,

    /// <summary>QuotaDuck's bridge is already wired: just waiting for the first Claude Code message.</summary>
    ConnectedWaiting = 2,
}

/// <summary>What the shell knows when it builds the guide: the probe's answer, where the bridge exe sits (if beside the app) and the copy feedback.</summary>
public sealed record SetupContext(ClaudeSetupState State, string? BridgePath, bool Copied, bool CopyFailed = false);

public sealed record SetupGuideModel(
    SetupVariant Variant,
    string Title,
    string Intro,
    string Limitation,
    string StepsBefore,
    string Snippet,
    bool HasSnippet,
    string PathNote,
    string StepsAfter,
    string RemoveText,
    string CopyLabel,
    string AccessibleName);

/// <summary>
/// The first-run text for the Claude card (display text only: the shell never reads or writes Claude settings; the probe in the coordinator
/// reports one four-value state). QuotaDuck never edits Claude's configuration: the user pastes the snippet themselves and removes it the same way.
/// </summary>
public static class SetupGuidePresenter
{
    public const string PlaceholderExe = "C:/Path/To/quotaduck-statusline.exe";
    public const string BridgeFileName = "quotaduck-statusline.exe";

    public static SetupGuideModel Build(SetupContext c)
    {
        string exe = ExeText(c.BridgePath);
        string pathNote = c.BridgePath is null ? "Replace C:/Path/To/ with the folder where " + BridgeFileName + " is saved." : string.Empty;
        string copy = c.CopyFailed ? "Couldn't copy: select the text" : c.Copied ? "Copied" : "Copy";
        const string intro = "QuotaDuck reads Claude usage from Claude Code terminal sessions.";
        const string limitation = "In our testing, the VS Code Claude panel and the Claude Desktop Code tab did not trigger the status line, so their usage cannot be counted. Only terminal sessions report.";
        const string location = "%USERPROFILE%\\.claude\\settings.json";

        switch (c.State)
        {
            case ClaudeSetupState.OtherStatusLine:
                return Make(
                    SetupVariant.TapExisting,
                    "Add QuotaDuck to your existing status line",
                    "You already use a Claude Code status line, so QuotaDuck will not replace it. The bridge has a silent \"tap\" mode that listens without changing your output.",
                    limitation,
                    "Add this line to your status-line script, where it reads Claude's data from stdin:",
                    TapLine(exe),
                    pathNote,
                    "Then start Claude Code in a terminal and send a message; numbers appear after its first reply.",
                    "Your script's own output is unchanged. QuotaDuck never edits your Claude settings. To undo, delete that line again.",
                    copy);

            case ClaudeSetupState.BridgeConfigured:
                return Make(
                    SetupVariant.ConnectedWaiting,
                    "Waiting for your first Claude Code message",
                    "QuotaDuck's status line is set up. Start Claude Code in a terminal and send a message; numbers appear after its first reply.",
                    limitation,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "To undo, delete the \"statusLine\" entry in " + location + ". QuotaDuck never edits your Claude settings.",
                    copy);

            default:
                string unknown = c.State == ClaudeSetupState.Unknown ? " (QuotaDuck could not check your settings file, so it cannot tell whether this is already set up.)" : string.Empty;
                return Make(
                    SetupVariant.Connect,
                    "Connect Claude Code (one-time setup)",
                    intro + unknown,
                    limitation,
                    "1. Open " + location + " (create it if it does not exist).\n2. Add this entry, merging it with anything already there:",
                    StatusLineEntry(exe),
                    pathNote,
                    "3. Start Claude Code in a terminal and send a message; numbers appear after its first reply.",
                    "QuotaDuck never edits your Claude settings. To undo, delete the \"statusLine\" entry again.",
                    copy);
        }
    }

    /// <summary>The text the Copy button puts on the clipboard (empty for the already-connected variant).</summary>
    public static string SnippetFor(SetupContext c) => Build(c).Snippet;

    public static string StatusLineEntry(string exe) =>
        "{\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"" + exe + "\"\n  }\n}";

    public static string TapLine(string exe) => "echo \"$input\" | \"" + exe + "\" --tap";

    /// <summary>Forward slashes keep the JSON valid without escaping; the placeholder when the bridge is not beside the app.</summary>
    public static string ExeText(string? bridgePath) =>
        string.IsNullOrWhiteSpace(bridgePath) ? PlaceholderExe : bridgePath.Replace('\\', '/').Replace("\"", string.Empty);

    private static SetupGuideModel Make(SetupVariant v, string title, string intro, string limitation, string before, string snippet, string pathNote, string after, string remove, string copy) =>
        new(v, title, intro, limitation, before, snippet, snippet.Length > 0, pathNote, after, remove, copy,
            title + ". " + intro + " " + limitation + (before.Length > 0 ? " " + before.Replace("\n", " ") : string.Empty) + " " + remove);
}
