using System.Windows;
using System.Windows.Input;
using QuotaDuck.App.Shell;

namespace QuotaDuck.App.Views;

/// <summary>The flyout: a plain window that hides instead of closing. All logic is in <see cref="ShellController"/> / <see cref="ShellViewModel"/>.</summary>
public partial class FlyoutWindow : Window, IFlyout
{
    private bool _allowClose;

    public FlyoutWindow(ShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        IsVisibleChanged += (_, _) => VisibleChanged?.Invoke(base.IsVisible);
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

    public new bool IsVisible => base.IsVisible;

    public void AllowClose() => _allowClose = true;

    void IFlyout.Show()
    {
        PlaceNearTray();
        base.Show();
        Activate();
    }

    void IFlyout.Hide() => base.Hide();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    /// <summary>Bottom-right of the work area (DIPs, so correct at any DPI/scale). Placeholder positioning.</summary>
    private void PlaceNearTray()
    {
        Rect wa = SystemParameters.WorkArea;
        UpdateLayout();
        double h = ActualHeight > 0 ? ActualHeight : 480;
        Left = wa.Right - Width - 12;
        Top = Math.Max(wa.Top + 8, wa.Bottom - h - 12);
    }
}
