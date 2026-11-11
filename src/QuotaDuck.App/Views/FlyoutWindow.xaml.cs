using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;

namespace QuotaDuck.App.Views;

/// <summary>The flyout: a plain window that hides instead of closing. All logic is in <see cref="ShellController"/> / <see cref="ShellViewModel"/>.</summary>
public partial class FlyoutWindow : Window, IFlyout
{
    /// <summary>The tool window's frame, in DIPs, used only before the first real measurement.</summary>
    private const double FrameDip = 16;

    private readonly IMonitorSource _monitors;
    private readonly IWindowMover _mover;
    private const int WmEnterSizeMove = 0x0231;

    private bool _allowClose;
    private bool _replacePending;
    private bool _userMoved;       // the user dragged it since it was last opened: stop pulling it back to the tray icon

    public FlyoutWindow(ShellViewModel vm, IMonitorSource? monitors = null, IWindowMover? mover = null)
    {
        var win32 = new Win32Monitors();
        _monitors = monitors ?? win32;
        _mover = mover ?? win32;
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowMessageHook);
        Body.RefreshRequested += () => RefreshRequested?.Invoke();
        Body.CopySetupRequested += () => CopySetupRequested?.Invoke();
        Body.CopyUsageRequested += scope => CopyUsageRequested?.Invoke(scope);
        IsVisibleChanged += (_, _) =>
        {
            VisibleChanged?.Invoke(base.IsVisible);
            if (base.IsVisible)
            {
                ReplaceLater();                                                  // second pass once the real size is known
            }
            else
            {
                _userMoved = false;                                              // hidden: the next open anchors to the tray icon again
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

    public event Action<ExportScope>? CopyUsageRequested;

    public new bool IsVisible => base.IsVisible;

    public PxPoint? Anchor { get; set; }

    /// <summary>The last placement computed (for tests and diagnostics): where, on which monitor, next to which taskbar edge.</summary>
    public FlyoutPlacementResult? LastPlacement { get; private set; }

    /// <summary>Side by side or stacked, as last chosen from the target monitor's work-area width (for tests and diagnostics).</summary>
    public CompactMode LastMode => Body.Mode;

    public void AllowClose() => _allowClose = true;

    /// <summary>
    /// Tells a drag by the user from every programmatic move or DPI-induced size change: only the OS move loop (WM_ENTERSIZEMOVE, sent when
    /// the user starts dragging the title bar or uses Alt+Space, Move) counts as the user. Our own SetWindowPos and the resizes WPF does when
    /// the window crosses a monitor with another DPI never send it. From then until the flyout is hidden or explicitly opened again the
    /// tray-anchored re-placement is suppressed (otherwise a DPI-induced resize moved the window back to the tray corner while it was being
    /// dragged, repeatedly: the cross-monitor flicker).
    /// </summary>
    internal nint WindowMessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            _userMoved = true;
        }

        return 0;
    }

    void IFlyout.Show()
    {
        _userMoved = false;                                                      // an explicit open anchors to the tray icon
        ApplyPlacement();
        base.Show();
        Activate();
    }

    void IFlyout.Hide() => base.Hide();

    private void ReplaceLater()
    {
        if (_replacePending || _userMoved)
        {
            return;
        }

        _replacePending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _replacePending = false;
                if (base.IsVisible && !_userMoved)
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
            MonitorArea target = FlyoutPlacement.MonitorFor(monitors, Anchor);
            CompactMode mode = CompactLayoutPolicy.Choose(target.Work.Width / target.DpiScale, false);       // two cards only where they fit comfortably
            if (Body.Mode != mode)
            {
                Body.Mode = mode;
                UpdateLayout();
            }

            double widthDip = ActualWidth > 0 ? ActualWidth : Body.ContentWidthDip + FrameDip;
            double heightDip = ActualHeight > 0 ? ActualHeight : 360;
            FlyoutPlacementResult r = FlyoutPlacement.Place(monitors, Anchor, widthDip, heightDip);
            LastPlacement = r;
            MaxHeight = Math.Max(240, (r.Monitor.Work.Height - 2 * Math.Round(FlyoutPlacement.GapDip * r.Monitor.DpiScale)) / r.Monitor.DpiScale);
            _mover.MoveTo(hwnd, r.TopLeft);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Rect wa = SystemParameters.WorkArea;                               // reliable-geometry-unavailable fallback: the old placement
            MaxHeight = Math.Max(240, wa.Height - 24);
            Left = wa.Right - (ActualWidth > 0 ? ActualWidth : Body.ContentWidthDip + FrameDip) - 12;
            Top = Math.Max(wa.Top + 8, wa.Bottom - (ActualHeight > 0 ? ActualHeight : 360) - 12);
        }
    }
}
