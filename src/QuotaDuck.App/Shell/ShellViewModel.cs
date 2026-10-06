using System.ComponentModel;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Services;
using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Shell;

/// <summary>
/// Holds the current <see cref="ShellPresentation"/> and the transient manual-refresh feedback. It derives everything from
/// <see cref="IQuotaService.Capture"/>; it never reads a provider and never schedules anything.
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan FeedbackLifetime = TimeSpan.FromSeconds(8);

    private readonly IQuotaService _service;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly IUiTicker _ticker;
    private ShellPresentation _current;
    private RefreshFeedback _feedback;
    private DateTimeOffset _feedbackUntil;
    private bool _flyoutVisible;

    public ShellViewModel(IQuotaService service, IUiTicker ticker, TimeProvider clock, TimeZoneInfo zone)
    {
        _service = service;
        _ticker = ticker;
        _clock = clock;
        _zone = zone;
        _current = Presenter.Present(service.Capture(), RefreshFeedback.None, zone);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShellPresentation Current => _current;

    public bool FlyoutVisible => _flyoutVisible;

    /// <summary>Honours the Windows "show animations" setting. The placeholder UI has no animation at all; this is for the art to come.</summary>
    public bool ReducedMotion { get; init; }

    /// <summary>Re-derives the presentation now. Cheap (pure); called on snapshot changes and, while visible, once a second.</summary>
    public void Refresh()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_feedback != RefreshFeedback.None && now >= _feedbackUntil)
        {
            _feedback = RefreshFeedback.None;
        }

        ShellPresentation next = Presenter.Present(_service.Capture(), _feedback, _zone);
        if (next != _current)
        {
            _current = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        }
    }

    public void SetFlyoutVisible(bool visible)
    {
        if (_flyoutVisible == visible)
        {
            return;
        }

        _flyoutVisible = visible;
        if (visible)
        {
            Refresh();
            _ticker.Start(Refresh);
        }
        else
        {
            _ticker.Stop();                                                 // nothing ticks while hidden
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlyoutVisible)));
    }

    /// <summary>The user pressed Refresh (Codex only).</summary>
    public async Task RefreshCodexAsync()
    {
        CodexRefreshResult r;
        try
        {
            r = await _service.RefreshCodexAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            r = new CodexRefreshResult(CodexRefreshStatus.Failed, false, null);     // never surface the exception
        }

        _feedback = r.Status switch
        {
            CodexRefreshStatus.CooldownActive => RefreshFeedback.TooSoon,
            CodexRefreshStatus.Failed => RefreshFeedback.Failed,
            _ => RefreshFeedback.None,
        };
        _feedbackUntil = _clock.GetUtcNow() + FeedbackLifetime;
        Refresh();
    }
}
