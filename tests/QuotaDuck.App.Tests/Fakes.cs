using QuotaDuck.App.Presentation;
using QuotaDuck.App.Services;
using QuotaDuck.App.Shell;
using QuotaDuck.Coordinator;
using QuotaDuck.Core;
using QuotaDuck.Core.Claude;
using QuotaDuck.Core.Codex;

// WPF windows, STA threads and process-wide state: keep the assembly sequential.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuotaDuck.App.Tests;

internal static class Snap
{
    public static readonly DateTimeOffset T0 = new(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public static ClaudeWindowRecord Rec(double pct, DateTimeOffset reset, DateTimeOffset changed) => new(pct, reset, changed, changed);

    public static CodexObservationSet Codex(DateTimeOffset at, double used5h = 31, double usedWk = 35, DateTimeOffset? reset5h = null, DateTimeOffset? resetWk = null) =>
        CodexObservations.FromRead(
        [
            new CodexBucket("codex", null,
                new CodexWindowReading(used5h, TimeSpan.FromMinutes(300), reset5h ?? at.AddHours(3)),
                new CodexWindowReading(usedWk, TimeSpan.FromMinutes(10080), resetWk ?? at.AddDays(5)), false),
        ],
        at);

    /// <summary>Builds a snapshot exactly the way the coordinator does: Core derives each provider from its own input.</summary>
    public static AppSnapshot Make(
        DateTimeOffset now,
        ClaudeState? claude = null,
        CodexObservationSet? codex = null,
        ProviderIssue? claudeIssue = null,
        ProviderIssue codexIssue = ProviderIssue.None,
        ProviderAttempt? attempt = null,
        bool reading = false,
        bool allowed = true,
        DateTimeOffset? allowedAt = null,
        bool claudeEnabled = true,
        bool codexEnabled = true,
        bool visible = true)
    {
        claude ??= ClaudeState.Empty;
        QuotaView q = QuotaDeriver.Derive(
            ClaudeObservations.ToProviderInput(claude.FiveHour, claude.SevenDay, claudeEnabled),
            CodexObservations.ToProviderInput(codex, attempt, codexEnabled),
            now,
            FreshnessPolicy.ForCodexPollInterval(TimeSpan.FromMinutes(10)));

        ProviderIssue ci = !claudeEnabled ? ProviderIssue.NotConfigured
            : claudeIssue ?? (q.Claude.Rollup == ProviderRollup.NoData ? ProviderIssue.WaitingForTerminalClaudeCode : ProviderIssue.None);
        ProviderIssue xi = !codexEnabled ? ProviderIssue.NotConfigured
            : codexIssue != ProviderIssue.None ? codexIssue
            : q.Codex.Rollup == ProviderRollup.NoData ? ProviderIssue.NoData : ProviderIssue.None;

        return new AppSnapshot(
            1,
            now,
            visible,
            new ProviderSnapshot(q.Claude, ci, ProviderActivity.Idle, claude.FiveHour?.ChangedAt),
            new ProviderSnapshot(q.Codex, xi, reading ? ProviderActivity.Reading : ProviderActivity.Idle, codex is null ? null : codex.Windows.Max(w => w.ReceivedAt)),
            allowed,
            allowedAt,
            null,
            null);
    }
}

internal sealed class FakeService : IQuotaService
{
    public AppSnapshot Current { get; set; } = Snap.Make(Snap.T0);
    public int StartCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public int EnvironmentCalls { get; private set; }
    public int RefreshCalls { get; private set; }
    public List<bool> Visibility { get; } = [];
    public List<string> Order { get; } = [];
    public Func<Task<CodexRefreshResult>> OnRefresh { get; set; } = () => Task.FromResult(new CodexRefreshResult(CodexRefreshStatus.Succeeded, false, null));
    public Func<ValueTask>? OnDispose { get; set; }

    public event Action<AppSnapshot>? Changed;

    public AppSnapshot Capture() => Current;

    public void Start()
    {
        StartCalls++;
        Order.Add("start");
    }

    public void SetUiVisible(bool visible)
    {
        Visibility.Add(visible);
        Order.Add("visible:" + visible);
    }

    public void NotifyEnvironmentChanged() => EnvironmentCalls++;

    public Task<CodexRefreshResult> RefreshCodexAsync()
    {
        RefreshCalls++;
        return OnRefresh();
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCalls++;
        Order.Add("dispose");
        if (OnDispose is not null)
        {
            await OnDispose();
        }
    }

    public void Raise(AppSnapshot s)
    {
        Current = s;
        Changed?.Invoke(s);
    }
}

internal sealed class FakeFlyout : IFlyout
{
    public bool IsVisible { get; private set; }
    public int ShowCalls { get; private set; }
    public int HideCalls { get; private set; }

    public event Action<bool>? VisibleChanged;

    public event Action? RefreshRequested;

    public event Action? CopySetupRequested;

    public PxPoint? Anchor { get; set; }

    public List<PxPoint?> AnchorsAtShow { get; } = [];

    public void ClickCopySetup() => CopySetupRequested?.Invoke();

    public void Show()
    {
        AnchorsAtShow.Add(Anchor);
        ShowCalls++;
        if (!IsVisible)
        {
            IsVisible = true;
            VisibleChanged?.Invoke(true);
        }
    }

    public void Hide()
    {
        HideCalls++;
        if (IsVisible)
        {
            IsVisible = false;
            VisibleChanged?.Invoke(false);
        }
    }

    public void ClickRefresh() => RefreshRequested?.Invoke();
}

internal sealed class FakeTray : ITray
{
    public List<string> Tooltips { get; } = [];
    public bool Disposed { get; private set; }

    public PxPoint? LastAnchor { get; set; }

    public event Action? Toggled;

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public void SetTooltip(string text)
    {
        if (Tooltips.Count == 0 || Tooltips[^1] != text)
        {
            Tooltips.Add(text);
        }
    }

    public void Dispose() => Disposed = true;

    public void LeftClick() => Toggled?.Invoke();

    public void MenuOpen() => OpenRequested?.Invoke();

    public void MenuExit() => ExitRequested?.Invoke();
}

internal sealed class FakeDispatcher : IUiDispatcher
{
    public Queue<Action> Queue { get; } = new();

    public void Post(Action action) => Queue.Enqueue(action);

    public void Pump()
    {
        while (Queue.Count > 0)
        {
            Queue.Dequeue()();
        }
    }
}

internal sealed class FakeTicker : IUiTicker
{
    private Action? _tick;
    public bool IsRunning { get; private set; }
    public int Starts { get; private set; }

    public void Start(Action tick)
    {
        _tick = tick;
        IsRunning = true;
        Starts++;
    }

    public void Stop()
    {
        IsRunning = false;
        _tick = null;
    }

    public void Tick() => _tick?.Invoke();
}

internal sealed class FakeEnvironment : IEnvironmentEvents
{
    public bool Disposed { get; private set; }

    public event Action? Resumed;

    public event Action? SessionUnlocked;

    public event Action? TimeChanged;

    public void Resume() => Resumed?.Invoke();

    public void Unlock() => SessionUnlocked?.Invoke();

    public void Time() => TimeChanged?.Invoke();

    public void Dispose() => Disposed = true;
}

internal sealed class FakeLifetime : IAppLifetime
{
    public int ShutdownCalls { get; private set; }
    public Action? OnShutdown { get; set; }

    public void Shutdown()
    {
        ShutdownCalls++;
        OnShutdown?.Invoke();
    }
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Everything wired to fakes.</summary>
internal sealed class ShellRig
{
    public FakeService Service { get; } = new();
    public FakeFlyout Flyout { get; } = new();
    public FakeTray Tray { get; } = new();
    public FakeDispatcher Dispatcher { get; } = new();
    public FakeTicker Ticker { get; } = new();
    public FakeEnvironment Environment { get; } = new();
    public FakeLifetime Lifetime { get; } = new();
    public FakeClock Clock { get; } = new(Snap.T0);
    public FakeSetupInfo SetupInfo { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public ShellViewModel Vm { get; }
    public ShellController Controller { get; }

    public ShellRig(bool start = true)
    {
        Vm = new ShellViewModel(Service, Ticker, Clock, Snap.Utc, null, null, SetupInfo, Clipboard);
        Controller = new ShellController(Service, Flyout, Tray, Environment, Dispatcher, Lifetime, Vm);
        if (start)
        {
            Controller.Start();
        }
    }
}

internal sealed class FakeSetupInfo : IClaudeSetupInfo
{
    public ClaudeSetupState State { get; set; } = ClaudeSetupState.NotConfigured;
    public string? BridgeExePath { get; set; }
    public int Checks { get; private set; }
    public bool Throw { get; set; }

    public ClaudeSetupState Check()
    {
        Checks++;
        return Throw ? throw new IOException("boom") : State;
    }
}

internal sealed class FakeClipboard : IClipboard
{
    public List<string> Texts { get; } = [];
    public bool Succeeds { get; set; } = true;
    public bool Throws { get; set; }

    public bool TrySetText(string text)
    {
        if (Throws)
        {
            throw new InvalidOperationException("clipboard locked");
        }

        if (Succeeds)
        {
            Texts.Add(text);
        }

        return Succeeds;
    }
}
