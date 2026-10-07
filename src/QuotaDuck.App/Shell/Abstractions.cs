namespace QuotaDuck.App.Shell;

/// <summary>The flyout window as the controller sees it. <see cref="VisibleChanged"/> is the single source of truth for visibility.</summary>
public interface IFlyout
{
    bool IsVisible { get; }

    void Show();

    void Hide();

    event Action<bool>? VisibleChanged;

    event Action? RefreshRequested;

    /// <summary>The Copy button of the first-run Claude setup guide was pressed.</summary>
    event Action? CopySetupRequested;

    /// <summary>Where the tray was last clicked (screen pixels), used to place the flyout next to it; null = unknown (fall back to the work-area corner).</summary>
    PxPoint? Anchor { get; set; }
}

public interface ITray : IDisposable
{
    event Action? Toggled;

    event Action? OpenRequested;

    event Action? ExitRequested;

    /// <summary>Sets the tooltip; a no-op when unchanged (the tray icon is never redrawn on a timer).</summary>
    void SetTooltip(string text);

    /// <summary>Screen position of the most recent click on the icon (left or right), or null if it was never clicked.</summary>
    PxPoint? LastAnchor { get; }
}

/// <summary>Marshals work onto the UI thread (the coordinator raises Changed on background threads).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>A 1 s repaint tick that runs ONLY while the flyout is visible (countdowns and ages); stopped when hidden.</summary>
public interface IUiTicker
{
    void Start(Action tick);

    void Stop();

    bool IsRunning { get; }
}

public interface IEnvironmentEvents : IDisposable
{
    event Action? Resumed;

    event Action? SessionUnlocked;

    event Action? TimeChanged;
}

public interface IAppLifetime
{
    void Shutdown();
}
