using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using QuotaDuck.App.Shell;

namespace QuotaDuck.App.Views;

/// <summary>The flyout: a plain window that hides instead of closing. All logic is in <see cref="ShellController"/> / <see cref="ShellViewModel"/>.</summary>
public partial class FlyoutWindow : Window, IFlyout
{
    private readonly IMonitorSource _monitors;
    private readonly IWindowMover _mover;
    private bool _allowClose;
    private bool _replacePending;

    public FlyoutWindow(ShellViewModel vm, IMonitorSource? monitors = null, IWindowMover? mover = null)
    {
        var win32 = new Win32Monitors();
        _monitors = monitors ?? win32;
        _mover = mover ?? win32;
        InitializeComponent();
        DataContext = vm;
        IsVisibleChanged += (_, _) =>
        {
            VisibleChanged?.Invoke(base.IsVisible);
            if (base.IsVisible)
            {
                ReplaceLater();                                                  // second pass once the real size is known
            }
        };
        // content changes while open (a Codex read adds rows, the guide's details expand): keep the flyout fully on screen, next to the icon
        SizeChanged += (_, _) =>
        {
            if (base.IsVisible)
            {
                ReplaceLater();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                base.Hide();
            }
        };
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;                                                // the close button hides; only Exit really closes
                base.Hide();
            }
        };
    }

    public event Action<bool>? VisibleChanged;

    public event Action? RefreshRequested;

    public event Action? CopySetupRequested;

    public new bool IsVisible => base.IsVisible;

    public PxPoint? Anchor { get; set; }

    /// <summary>The last placement computed (for tests and diagnostics): where, on which monitor, next to which taskbar edge.</summary>
    public FlyoutPlacementResult? LastPlacement { get; private set; }

    public void AllowClose() => _allowClose = true;

    void IFlyout.Show()
    {
        ApplyPlacement();
        base.Show();
        Activate();
    }

    void IFlyout.Hide() => base.Hide();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void OnCopySetupClick(object sender, RoutedEventArgs e) => CopySetupRequested?.Invoke();

    private void ReplaceLater()
    {
        if (_replacePending)
        {
            return;
        }

        _replacePending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _replacePending = false;
                if (base.IsVisible)
                {
                    ApplyPlacement();
                }
            });
    }

    /// <summary>
    /// Next to the tray icon that was clicked (see <see cref="FlyoutPlacement"/>), in physical pixels on that monitor, kept inside its work
    /// area; the window's MaxHeight is that work area (minus the gaps) so tall content scrolls rather than leaving the screen.
    /// Anything unexpected falls back to the bottom-right corner of the primary work area.
    /// </summary>
    private void ApplyPlacement()
    {
        try
        {
            IReadOnlyList<MonitorArea> monitors = _monitors.Monitors();
            nint hwnd = new WindowInteropHelper(this).EnsureHandle();
            double heightDip = ActualHeight > 0 ? ActualHeight : 560;
            FlyoutPlacementResult r = FlyoutPlacement.Place(monitors, Anchor, Width, heightDip);
            LastPlacement = r;
            MaxHeight = Math.Max(240, (r.Monitor.Work.Height - 2 * Math.Round(FlyoutPlacement.GapDip * r.Monitor.DpiScale)) / r.Monitor.DpiScale);
            _mover.MoveTo(hwnd, r.TopLeft);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Rect wa = SystemParameters.WorkArea;                               // reliable-geometry-unavailable fallback: the old placement
            MaxHeight = Math.Max(240, wa.Height - 24);
            Left = wa.Right - Width - 12;
            Top = Math.Max(wa.Top + 8, wa.Bottom - (ActualHeight > 0 ? ActualHeight : 560) - 12);
        }
    }
}
