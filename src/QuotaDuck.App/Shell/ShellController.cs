using QuotaDuck.App.Presentation;
using QuotaDuck.App.Services;
using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Shell;

/// <summary>
/// The shell's lifecycle glue and nothing else: start/stop the coordinator, forward visibility and environment events, route tray
/// gestures. No provider or scheduling logic — those stay in the coordinator.
/// </summary>
public sealed class ShellController
{
    private readonly IQuotaService _service;
    private readonly IFlyout _flyout;
    private readonly ITray _tray;
    private readonly IEnvironmentEvents _environment;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLifetime _lifetime;
    private readonly ShellViewModel _vm;
    private bool _started;
    private bool _exiting;

    public ShellController(
        IQuotaService service,
        IFlyout flyout,
        ITray tray,
        IEnvironmentEvents environment,
        IUiDispatcher dispatcher,
        IAppLifetime lifetime,
        ShellViewModel viewModel)
    {
        _service = service;
        _flyout = flyout;
        _tray = tray;
        _environment = environment;
        _dispatcher = dispatcher;
        _lifetime = lifetime;
        _vm = viewModel;
    }

    public ShellViewModel ViewModel => _vm;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _tray.Toggled += Toggle;
        _tray.OpenRequested += Show;
        _tray.ExitRequested += OnExitRequested;
        _flyout.VisibleChanged += OnFlyoutVisibleChanged;
        _flyout.RefreshRequested += OnRefreshRequested;
        _environment.Resumed += _service.NotifyEnvironmentChanged;               // the coordinator's single entry point; no logic duplicated here
        _environment.SessionUnlocked += _service.NotifyEnvironmentChanged;
        _environment.TimeChanged += _service.NotifyEnvironmentChanged;
        _service.Changed += OnSnapshot;
        _service.Start();
        _tray.SetTooltip(Presenter.Tooltip(_service.Capture()));
    }

    public void Show()
    {
        if (!_exiting)
        {
            _flyout.Show();
        }
    }

    public void Hide() => _flyout.Hide();

    public void Toggle()
    {
        if (_exiting)
        {
            return;
        }

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
        }
        else
        {
            _flyout.Show();
        }
    }

    /// <summary>The window's visibility is the single source of truth: tray toggle, Esc, the close button all end up here.</summary>
    private void OnFlyoutVisibleChanged(bool visible)
    {
        if (_exiting && visible)
        {
            return;
        }

        _service.SetUiVisible(visible);
        _vm.SetFlyoutVisible(visible);
    }

    private void OnRefreshRequested() => _ = _vm.RefreshCodexAsync();

    private void OnSnapshot(AppSnapshot snapshot) =>
        _dispatcher.Post(() =>
        {
            if (_exiting)
            {
                return;
            }

            _tray.SetTooltip(Presenter.Tooltip(snapshot));                        // only on real changes, never on a timer
            _vm.Refresh();
        });

    private void OnExitRequested() => _ = ExitAsync();

    /// <summary>Tray icon first (it disappears at once), then hide, then stop the coordinator (cancels an in-flight read), then shut down.</summary>
    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _tray.Dispose();
        _environment.Dispose();
        _flyout.Hide();
        _service.Changed -= OnSnapshot;
        try
        {
            await _service.DisposeAsync().ConfigureAwait(true);
        }
        finally
        {
            _lifetime.Shutdown();
        }
    }
}
