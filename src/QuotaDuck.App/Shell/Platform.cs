using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace QuotaDuck.App.Shell;

/// <summary>Maps raw Windows notifications to "the environment changed". Pure so the mapping is testable without a message pump.</summary>
public static class EnvironmentMapping
{
    public static bool IsResume(PowerModes mode) => mode == PowerModes.Resume;

    /// <summary>Unlock of this session, or reconnecting to it (remote/console): the machine may have slept or the clock may have jumped.</summary>
    public static bool IsUnlock(SessionSwitchReason reason) =>
        reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect;
}

/// <summary>Resume from sleep, session unlock and system-time changes (the only three the coordinator cares about).</summary>
public sealed class SystemEventsEnvironment : IEnvironmentEvents
{
    public SystemEventsEnvironment()
    {
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.TimeChanged += OnTime;
    }

    public event Action? Resumed;

    public event Action? SessionUnlocked;

    public event Action? TimeChanged;

    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (EnvironmentMapping.IsResume(e.Mode))
        {
            Resumed?.Invoke();
        }
    }

    private void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (EnvironmentMapping.IsUnlock(e.Reason))
        {
            SessionUnlocked?.Invoke();
        }
    }

    private void OnTime(object? sender, EventArgs e) => TimeChanged?.Invoke();

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPower;                               // static events: never leak a subscription
        SystemEvents.SessionSwitch -= OnSession;
        SystemEvents.TimeChanged -= OnTime;
    }
}

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

public sealed class WpfLifetime : IAppLifetime
{
    public void Shutdown() => System.Windows.Application.Current?.Shutdown();
}

/// <summary>A ticker that exists only while started (the flyout is visible): 1 s by default, 80 ms for the short refill tween.</summary>
public sealed class DispatcherTicker(TimeSpan? interval = null) : IUiTicker
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(1);
    private DispatcherTimer? _timer;

    public bool IsRunning => _timer is { IsEnabled: true };

    public void Start(Action tick)
    {
        Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _interval };
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}

/// <summary>
/// Tray icon via WinForms NotifyIcon: left click toggles, the menu has Open and Exit, the tooltip only changes when its text changes.
/// The context menu is created on the first right-click (building a ToolStrip loads GDI+ and costs ~11 MB of working set that a tray that is
/// never right-clicked should not pay).
/// </summary>
public sealed class WinFormsTray : ITray
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _image;
    private WinForms.ContextMenuStrip? _menu;
    private string _tooltip = string.Empty;

    public WinFormsTray(string initialTooltip)
    {
        _image = TrayIconResource.Load();
        _icon = new WinForms.NotifyIcon { Icon = _image, Visible = true };
        _icon.MouseDown += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right && _menu is null)
            {
                _menu = new WinForms.ContextMenuStrip();
                _menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
                _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
                _icon.ContextMenuStrip = _menu;                                  // assigned before the button-up that shows it
            }
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                Toggled?.Invoke();
            }
        };
        SetTooltip(initialTooltip);
    }

    public event Action? Toggled;

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public void SetTooltip(string text)
    {
        text = text.Length > 127 ? text[..127] : text;                          // NotifyIcon.Text limit
        if (text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        _icon.Text = text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu?.Dispose();
        _image.Dispose();
    }
}

/// <summary>
/// The static tray icon: an .ico generated at development time by tools/QuotaDuck.AssetGen (deterministic) and embedded in this assembly.
/// It never touches WPF imaging/rendering, so no Direct3D or GPU driver is pulled in.
/// </summary>
public static class TrayIconResource
{
    public const string ResourceName = "quotaduck.ico";

    /// <summary>The raw embedded .ico (compared byte for byte with the generator's output in the tests).</summary>
    public static byte[] Bytes()
    {
        using Stream s = typeof(TrayIconResource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("tray icon resource missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static Icon Load()
    {
        using Stream s = typeof(TrayIconResource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("tray icon resource missing");
        return new Icon(s, WinForms.SystemInformation.SmallIconSize);
    }
}

/// <summary>One instance per user session: a second launch asks the running one to open its flyout, then exits.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\QuotaDuck.App.v1.Instance";
    private const string EventName = @"Local\QuotaDuck.App.v1.Open";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _open;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle open, Action? onOpenRequested)
    {
        _mutex = mutex;
        _open = open;
        if (onOpenRequested is not null)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(open, (_, _) => onOpenRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    /// <summary>Null if another instance is already running (it has been asked to open).</summary>
    public static SingleInstance? TryAcquire(Action onOpenRequested)
    {
        var open = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            open.Set();
            mutex.Dispose();
            open.Dispose();
            return null;
        }

        return new SingleInstance(mutex, open, onOpenRequested);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _open.Dispose();
    }
}
