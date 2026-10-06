using System.ComponentModel;
using QuotaDuck.App.Presentation;
using QuotaDuck.App.Services;
using QuotaDuck.Coordinator;

namespace QuotaDuck.App.Shell;

/// <summary>
/// Holds the current <see cref="ShellPresentation"/>, the transient manual-refresh feedback and the (visible-only) animation state. It
/// derives everything from <see cref="IQuotaService.Capture"/>; it never reads a provider and never schedules provider work.
/// <para>Animation is driven by two tickers that exist ONLY while the flyout is visible: the 1 Hz ticker (countdowns, ripple/zzz frame) and a
/// short-lived 80 ms ticker during a refill. Reduced motion (re-read every time the flyout opens) disables both frames and refills.</para>
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan FeedbackLifetime = TimeSpan.FromSeconds(8);

    private readonly IQuotaService _service;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly IUiTicker _ticker;
    private readonly IUiTicker? _fastTicker;
    private readonly Func<bool> _reducedMotion;
    private readonly PondAnimator _claudeAnimator = new();
    private readonly PondAnimator _codexAnimator = new();
    private ShellPresentation _current;
    private RefreshFeedback _feedback;
    private DateTimeOffset _feedbackUntil;
    private bool _flyoutVisible;
    private int _frame;

    public ShellViewModel(
        IQuotaService service,
        IUiTicker ticker,
        TimeProvider clock,
        TimeZoneInfo zone,
        IUiTicker? fastTicker = null,
        Func<bool>? reducedMotion = null)
    {
        _service = service;
        _ticker = ticker;
        _clock = clock;
        _zone = zone;
        _fastTicker = fastTicker;
        _reducedMotion = reducedMotion ?? (() => false);
        ReducedMotion = _reducedMotion();
        _current = Presenter.Present(service.Capture(), RefreshFeedback.None, zone);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShellPresentation Current => _current;

    public bool FlyoutVisible => _flyoutVisible;

    /// <summary>Windows "show animations" setting, re-read whenever the flyout opens. When true nothing animates.</summary>
    public bool ReducedMotion { get; private set; }

    /// <summary>The ripple / zzz / busy-dots frame (0 or 1). Always 0 under reduced motion or while hidden.</summary>
    public int Frame => _frame;

    /// <summary>Re-derives the presentation now. Cheap (pure); called on snapshot changes and, while visible, once a second.</summary>
    public void Refresh()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_feedback != RefreshFeedback.None && now >= _feedbackUntil)
        {
            _feedback = RefreshFeedback.None;
        }

        ShellPresentation raw = Presenter.Present(_service.Capture(), _feedback, _zone);
        bool allow = _flyoutVisible && !ReducedMotion;
        bool tween = _claudeAnimator.Observe(raw.Claude.Pond, allow) | _codexAnimator.Observe(raw.Codex.Pond, allow);
        if (tween && _fastTicker is { IsRunning: false })
        {
            _fastTicker.Start(OnFastTick);
        }

        ShellPresentation next = raw with
        {
            Claude = raw.Claude with { Pond = _claudeAnimator.Current(raw.Claude.Pond) with { Frame = _frame } },
            Codex = raw.Codex with { Pond = _codexAnimator.Current(raw.Codex.Pond) with { Frame = _frame } },
        };
        if (!Same(next, _current))
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
            ReducedMotion = _reducedMotion();
            _frame = 0;
            Refresh();
            _ticker.Start(OnTick);
        }
        else
        {
            _ticker.Stop();                                                 // nothing ticks while hidden
            _fastTicker?.Stop();
            _frame = 0;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlyoutVisible)));
    }

    private void OnTick()
    {
        if (!ReducedMotion)
        {
            _frame ^= 1;
        }

        Refresh();
    }

    private void OnFastTick()
    {
        bool running = _claudeAnimator.Step() | _codexAnimator.Step();
        if (!running)
        {
            _fastTicker?.Stop();
        }

        Refresh();
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

    // Value comparison (the models hold list instances, which compare by reference): avoids re-binding the whole flyout on every tick.
    private static bool Same(ShellPresentation a, ShellPresentation b) =>
        a.TrayTooltip == b.TrayTooltip && a.RefreshFeedback == b.RefreshFeedback && Same(a.Claude, b.Claude) && Same(a.Codex, b.Codex);

    private static bool Same(ProviderCardModel a, ProviderCardModel b) =>
        a.Name == b.Name && a.Headline == b.Headline && a.Note == b.Note && a.Tone == b.Tone && a.Footer == b.Footer
        && a.Refresh == b.Refresh && a.Pond == b.Pond && a.PondDescription == b.PondDescription && a.Windows.SequenceEqual(b.Windows);
}
