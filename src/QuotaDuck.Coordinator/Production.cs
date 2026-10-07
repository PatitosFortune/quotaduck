using QuotaDuck.Codex;
using QuotaDuck.Core.Claude;

namespace QuotaDuck.Coordinator;

/// <summary>One-shot timers on the thread pool.</summary>
internal sealed class ThreadPoolTimerScheduler : ITimerScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback) =>
        new Timer(static state => ((Action)state!).Invoke(), callback, delay, Timeout.InfiniteTimeSpan);
}

/// <summary>Adapts the Codex runner (the only component that can start the Codex process).</summary>
internal sealed class CodexQuotaReaderSource(CodexQuotaReader reader) : ICodexQuotaSource
{
    public Task<CodexReadOutcome> ReadAsync(CancellationToken ct) => reader.ReadAsync(ct);
}

/// <summary>Used when the state location cannot be established: nothing is read or watched and the UI says "waiting for Claude Code".</summary>
internal sealed class UnavailableClaudeSource : IClaudeStateSource, IClaudeStateWatcher
{
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public bool IsAttached => false;

    public bool TryAttach() => false;

    public ClaudeSourceRead Read() => new(ClaudeSourceStatus.Absent, ClaudeState.Empty);

    public void Dispose()
    {
    }
}

public static class QuotaCoordinatorFactory
{
    /// <summary>
    /// The production wiring: the fixed bridge state location (token-derived, not redirectable), the real Codex runner, the system clock and
    /// thread-pool timers. The caller still has to call <see cref="QuotaCoordinator.Start"/>.
    /// </summary>
    public static QuotaCoordinator CreateDefault(CoordinatorOptions? options = null)
    {
        string? dir = ClaudeStateLocation.DefaultDirectory();
        IClaudeStateSource source;
        IClaudeStateWatcher watcher;
        if (dir is null)
        {
            var none = new UnavailableClaudeSource();
            source = none;
            watcher = none;
        }
        else
        {
            source = new FileClaudeStateSource(dir);
            watcher = new FileSystemClaudeStateWatcher(dir);
        }

        return new QuotaCoordinator(
            options ?? new CoordinatorOptions(),
            source,
            watcher,
            new CodexQuotaReaderSource(new CodexQuotaReader()),
            TimeProvider.System,
            new ThreadPoolTimerScheduler());
    }
}

public static class ClaudeSetupProbeFactory
{
    /// <summary>The probe for the real user-level Claude Code settings (profile directory from the process token). Read-only.</summary>
    public static IClaudeSetupProbe CreateDefault() => new ClaudeSetupProbe(ClaudeSetupProbe.DefaultPath());
}
