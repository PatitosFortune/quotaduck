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
    private CompactPresentation _compact;
    private bool _setupExpanded;
    private RefreshFeedback _feedback;
    private DateTimeOffset _feedbackUntil;
    private bool _flyoutVisible;
    private int _frame;
    private readonly IClaudeSetupInfo? _setupInfo;
    private readonly IClipboard? _clipboard;
    private Coordinator.ClaudeSetupState? _setupState;
    private DateTimeOffset _copiedUntil;
    private DateTimeOffset _copyFailedUntil;

    public ShellViewModel(
        IQuotaService service,
        IUiTicker ticker,
        TimeProvider clock,
        TimeZoneInfo zone,
        IUiTicker? fastTicker = null,
        Func<bool>? reducedMotion = null,
        IClaudeSetupInfo? setupInfo = null,
        IClipboard? clipboard = null)
    {
        _setupInfo = setupInfo;
        _clipboard = clipboard;
        _service = service;
        _ticker = ticker;
        _clock = clock;
        _zone = zone;
        _fastTicker = fastTicker;
        _reducedMotion = reducedMotion ?? (() => false);
        ReducedMotion = _reducedMotion();
        _current = Presenter.Present(service.Capture(), RefreshFeedback.None, zone);
        _compact = BuildCompact();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShellPresentation Current => _current;

    /// <summary>What the compact flyout binds to: the two cards, and the setup guide only while it is expanded.</summary>
    public CompactPresentation Compact => _compact;

    /// <summary>The first-run guide is open below the cards. Purely a view choice: it never touches provider state, and it closes by itself once Claude has reported.</summary>
    public bool SetupExpanded => _setupExpanded;

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

        SetupContext? setup = _setupInfo is not null && _setupState is { } st
            ? new SetupContext(st, _setupInfo.BridgeExePath, now < _copiedUntil, now < _copyFailedUntil)
            : null;
        ShellPresentation raw = Presenter.Present(_service.Capture(), _feedback, _zone, setup);
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
            if (next.Claude.Setup is null)
            {
                _setupExpanded = false;                                     // Claude has reported (or never needed the guide): nothing to keep open
            }

            _compact = BuildCompact();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Compact)));
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
            CheckSetupIfNeeded();
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

    /// <summary>
    /// Only when the flyout opens AND Claude has never reported: one narrow look at the user-level status-line setting (the coordinator's
    /// probe returns a four-value state; nothing else is read). Nothing is checked while hidden, while Claude has data, or on a timer.
    /// </summary>
    private void CheckSetupIfNeeded()
    {
        if (_setupInfo is null)
        {
            return;
        }

        if (_service.Capture().Claude.Issue != ProviderIssue.WaitingForTerminalClaudeCode)
        {
            _setupState = null;
            return;
        }

        try
        {
            _setupState = _setupInfo.Check();
        }
        catch (Exception)
        {
            _setupState = Coordinator.ClaudeSetupState.Unknown;
        }
    }

    /// <summary>The "Set up" / "Hide" button in the Claude card.</summary>
    public void ToggleSetup()
    {
        if (_current.Claude.Setup is null)
        {
            return;
        }

        _setupExpanded = !_setupExpanded;
        _compact = BuildCompact();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Compact)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SetupExpanded)));
    }

    private CompactPresentation BuildCompact() => new(
        CompactCard.From(_current.Claude, _setupExpanded),
        CompactCard.From(_current.Codex),
        _setupExpanded ? _current.Claude.Setup : null,
        _current.RefreshFeedback);

    /// <summary>The Copy button of the setup guide: the snippet goes to the clipboard (the user pastes it into their own settings).</summary>
    public void CopySetup()
    {
        if (_setupInfo is null || _clipboard is null || _setupState is not { } st)
        {
            return;
        }

        string snippet = SetupGuidePresenter.SnippetFor(new SetupContext(st, _setupInfo.BridgeExePath, false));
        if (snippet.Length == 0)
        {
            return;
        }

        bool ok;
        try
        {
            ok = _clipboard.TrySetText(snippet);
        }
        catch (Exception)
        {
            ok = false;
        }

        DateTimeOffset now = _clock.GetUtcNow();
        _copiedUntil = ok ? now + TimeSpan.FromSeconds(3) : default;
        _copyFailedUntil = ok ? default : now + TimeSpan.FromSeconds(6);
        Refresh();
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
        && a.Refresh == b.Refresh && a.Pond == b.Pond && a.PondDescription == b.PondDescription && a.Setup == b.Setup && a.Windows.SequenceEqual(b.Windows);
}
