using System.IO;
using QuotaDuck.App.Presentation;
using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Services;

/// <summary>What the shell may ask about Claude Code setup: one four-value answer and where the bridge exe sits (if beside the app).</summary>
public interface IClaudeSetupInfo
{
    ClaudeSetupState Check();

    /// <summary>Full path of <c>quotaduck-statusline.exe</c> if it is in the app's own folder; otherwise null (the guide shows a placeholder path).</summary>
    string? BridgeExePath { get; }
}

public interface IClipboard
{
    /// <summary>Puts plain text on the clipboard. False if it could not (the text stays selectable in the guide).</summary>
    bool TrySetText(string text);
}

internal sealed class ClaudeSetupInfo(IClaudeSetupProbe probe, string appDirectory) : IClaudeSetupInfo
{
    public ClaudeSetupState Check() => probe.Check();

    public string? BridgeExePath
    {
        get
        {
            string p = Path.Combine(appDirectory, SetupGuidePresenter.BridgeFileName);
            return File.Exists(p) ? p : null;
        }
    }
}
