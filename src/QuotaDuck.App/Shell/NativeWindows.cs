using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace QuotaDuck.App.Shell;

/// <summary>Moves a window to a position in PHYSICAL pixels (so a mixed-DPI multi-monitor desktop is placed exactly, without WPF's DIP rescaling).</summary>
public interface IWindowMover
{
    void MoveTo(nint hwnd, PxPoint topLeft);
}

/// <summary>
/// The only native calls of the shell, all documented user32/shcore APIs: SetWindowPos (move, no resize/activate), MonitorFromPoint and
/// GetDpiForMonitor (the monitor's own DPI). Monitor rectangles come from WinForms' Screen (physical pixels in a per-monitor-aware process).
/// </summary>
internal sealed partial class Win32Monitors : IMonitorSource, IWindowMover
{
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static partial nint MonitorFromPoint(Point pt, uint flags);

    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    public IReadOnlyList<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        foreach (WinForms.Screen s in WinForms.Screen.AllScreens)
        {
            var bounds = new PxRect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Right, s.Bounds.Bottom);
            var work = new PxRect(s.WorkingArea.Left, s.WorkingArea.Top, s.WorkingArea.Right, s.WorkingArea.Bottom);
            list.Add(new MonitorArea(bounds, work, DpiScaleOf(bounds.Center), s.Primary));
        }

        return list;
    }

    private static double DpiScaleOf(PxPoint p)
    {
        try
        {
            nint mon = MonitorFromPoint(new Point { X = p.X, Y = p.Y }, MonitorDefaultToNearest);
            if (mon != 0 && GetDpiForMonitor(mon, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX >= 48)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return 1.0;
    }

    public void MoveTo(nint hwnd, PxPoint topLeft) =>
        SetWindowPos(hwnd, 0, topLeft.X, topLeft.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
}
