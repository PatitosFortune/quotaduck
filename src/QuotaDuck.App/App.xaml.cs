using System.Windows;
using QuotaDuck.App.Services;
using QuotaDuck.App.Shell;
using QuotaDuck.App.Views;
using QuotaDuck.Coordinator;

namespace QuotaDuck.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private ShellController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(() => _controller?.Show()));
        if (_single is null)
        {
            Shutdown();                                                         // another instance is running; it has been asked to open
            return;
        }

        var service = new CoordinatorService(QuotaCoordinatorFactory.CreateDefault());
        var vm = new ShellViewModel(
            service,
            new DispatcherTicker(),
            TimeProvider.System,
            TimeZoneInfo.Local,
            new DispatcherTicker(TimeSpan.FromMilliseconds(80)),
            () => !SystemParameters.ClientAreaAnimation);                       // reduced motion = Windows "Show animations" off

        // The flyout window is created lazily on first Show: a tray-only start pays nothing for WPF layout.
        _controller = new ShellController(
            service,
            new LazyFlyout(() => new FlyoutWindow(vm)),
            new WinFormsTray("QuotaDuck"),
            new SystemEventsEnvironment(),
            new WpfDispatcher(Dispatcher),
            new WpfLifetime(),
            vm);
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Defers constructing the real window until it is first shown; forwards its events to the controller.</summary>
    private sealed class LazyFlyout(Func<FlyoutWindow> create) : IFlyout
    {
        private FlyoutWindow? _w;

        public bool IsVisible => _w?.IsVisible ?? false;

        public event Action<bool>? VisibleChanged;

        public event Action? RefreshRequested;

        public void Show()
        {
            if (_w is null)
            {
                _w = create();
                _w.VisibleChanged += v => VisibleChanged?.Invoke(v);
                _w.RefreshRequested += () => RefreshRequested?.Invoke();
            }

            ((IFlyout)_w).Show();
        }

        public void Hide() => _w?.Hide();
    }
}
