using System.Windows;
using System.Windows.Controls;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Shell;

namespace QuotaDuck.App.Views;

/// <summary>
/// The flyout body: two provider cards (Claude left, Codex right) that stack into one column when the work area is too narrow, and the
/// first-run setup guide below them while it is expanded. Everything it shows comes from <see cref="ShellViewModel.Compact"/>.
/// </summary>
public partial class CompactFlyoutContent : UserControl
{
    /// <summary>2 DIP in high contrast so side-by-side cards stay distinct (no hairline separators), otherwise 1.</summary>
    public static readonly Thickness CardBorder = new(SystemParameters.HighContrast ? 2 : 1);

    private CompactMode _mode = CompactMode.SideBySide;

    public CompactFlyoutContent()
    {
        InitializeComponent();
        Width = CompactLayoutPolicy.SideBySideContentWidthDip;
    }

    public event Action? RefreshRequested;

    public event Action? CopySetupRequested;

    /// <summary>The arrangement. The width is fixed per mode: text wraps at the card width instead of widening the flyout.</summary>
    public CompactMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Cards.Columns = value == CompactMode.SideBySide ? 2 : 1;
            Width = value == CompactMode.SideBySide ? CompactLayoutPolicy.SideBySideContentWidthDip : CompactLayoutPolicy.StackedContentWidthDip;
        }
    }

    private void OnSetUpClick(object sender, RoutedEventArgs e) => (DataContext as ShellViewModel)?.ToggleSetup();

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySetupRequested?.Invoke();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
