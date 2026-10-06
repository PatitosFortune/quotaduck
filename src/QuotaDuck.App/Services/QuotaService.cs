using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Services;

/// <summary>
/// Everything the shell may ask of the coordinator — and nothing more. There is deliberately no "refresh Claude", no raw provider access and
/// no scheduling here: the UI signals visibility and environment changes and renders snapshots.
/// </summary>
public interface IQuotaService : IAsyncDisposable
{
    event Action<AppSnapshot>? Changed;

    /// <summary>A snapshot derived at this instant (exact ages and countdowns); does not read any provider.</summary>
    AppSnapshot Capture();

    void Start();

    void SetUiVisible(bool visible);

    /// <summary>Resume from sleep, session unlock or system-time change.</summary>
    void NotifyEnvironmentChanged();

    Task<CodexRefreshResult> RefreshCodexAsync();
}

internal sealed class CoordinatorService(QuotaCoordinator coordinator) : IQuotaService
{
    public event Action<AppSnapshot>? Changed
    {
        add => coordinator.Changed += value;
        remove => coordinator.Changed -= value;
    }

    public AppSnapshot Capture() => coordinator.Capture();

    public void Start() => coordinator.Start();

    public void SetUiVisible(bool visible) => coordinator.SetUiVisible(visible);

    public void NotifyEnvironmentChanged() => coordinator.NotifyEnvironmentChanged();

    public Task<CodexRefreshResult> RefreshCodexAsync() => coordinator.RefreshCodexAsync();

    public ValueTask DisposeAsync() => coordinator.DisposeAsync();
}
