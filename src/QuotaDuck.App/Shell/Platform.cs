using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

/// <summary>1 s ticker that exists only while started (the flyout is visible).</summary>
public sealed class DispatcherTicker : IUiTicker
{
    private DispatcherTimer? _timer;

    public bool IsRunning => _timer is { IsEnabled: true };

    public void Start(Action tick)
    {
        Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}

/// <summary>Tray icon via WinForms NotifyIcon: left click toggles, menu has Open and Exit, tooltip only changes when its text changes.</summary>
public sealed class WinFormsTray : ITray
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Icon _image;
    private string _tooltip = string.Empty;

    public WinFormsTray(string initialTooltip)
    {
        _image = PlaceholderIcon.Create();
        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon = new WinForms.NotifyIcon { Icon = _image, ContextMenuStrip = _menu, Visible = true };
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
        _menu.Dispose();
        _image.Dispose();
    }
}

/// <summary>A placeholder tray icon (yellow disc), rendered once at 16 and 32 px into an in-memory ICO — no native handles to leak.</summary>
public static class PlaceholderIcon
{
    public static Icon Create()
    {
        byte[] p16 = Png(16), p32 = Png(32);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);                                                      // reserved
        w.Write((short)1);                                                      // type: icon
        w.Write((short)2);                                                      // two images
        int offset = 6 + 2 * 16;
        foreach ((int size, byte[] data) in new[] { (16, p16), (32, p32) })
        {
            w.Write((byte)size);
            w.Write((byte)size);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }

        w.Write(p16);
        w.Write(p32);
        w.Flush();
        ms.Position = 0;
        return new Icon(ms);
    }

    private static byte[] Png(int size)
    {
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            double r = size / 2.0;
            dc.DrawEllipse(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xC1, 0x2E)), new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0x5B, 0x00)), size / 16.0), new System.Windows.Point(r, r), r - size / 10.0, r - size / 10.0);
            dc.DrawEllipse(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x7D, 0x1E)), null, new System.Windows.Point(r * 1.55, r * 1.05), size / 8.0, size / 12.0);
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
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
