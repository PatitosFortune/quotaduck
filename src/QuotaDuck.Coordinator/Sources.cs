using QuotaDuck.Codex;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.Coordinator;

public enum ClaudeSourceStatus
{
    /// <summary>The state file (or its directory) does not exist.</summary>
    Absent = 0,

    Ok = 1,

    /// <summary>Zero bytes: nothing reported yet.</summary>
    Empty = 2,

    /// <summary>Not decodable right now (intermediate/corrupt/oversized). Transient from the UI's point of view.</summary>
    Corrupt = 3,

    /// <summary>Written by a newer schema.</summary>
    FutureSchema = 4,

    /// <summary>Exists but could not be opened/read (locked, access error).</summary>
    Unreadable = 5,
}

public sealed record ClaudeSourceRead(ClaudeSourceStatus Status, ClaudeState State);

/// <summary>Read-only access to the bridge's state. The coordinator can never write it (the bridge is the only writer).</summary>
public interface IClaudeStateSource
{
    ClaudeSourceRead Read();
}

/// <summary>Raises <see cref="Changed"/> (possibly spuriously, possibly in bursts, on any thread) when the state file may have changed.</summary>
public interface IClaudeStateWatcher : IDisposable
{
    event Action? Changed;

    /// <summary>True while change notifications are being delivered. A lost notification channel (overflow, directory removed) flips this to false.</summary>
    bool IsAttached { get; }

    /// <summary>Attempts to (re)establish notifications. A single existence check when the directory is not there. Never throws.</summary>
    bool TryAttach();
}

public interface ICodexQuotaSource
{
    Task<CodexReadOutcome> ReadAsync(CancellationToken ct);
}

/// <summary>One-shot timer abstraction so scheduling is deterministic under test.</summary>
public interface ITimerScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}
