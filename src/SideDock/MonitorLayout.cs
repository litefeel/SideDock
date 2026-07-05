using System.Runtime.InteropServices;
using System.Windows;

namespace SideDock;

internal readonly record struct MonitorLayout(NativeRect MonitorPixels, NativeRect WorkPixels, uint Dpi)
{
    public const uint DefaultDpi = 96;

    public double Scale => Dpi > 0 ? Dpi / (double)DefaultDpi : 1.0;

    public Rect MonitorDips => ToDips(MonitorPixels);

    public Rect WorkDips => ToDips(WorkPixels);

    public double MonitorWidthDips => PixelsToDips(MonitorPixels.Width);

    public double MonitorHeightDips => PixelsToDips(MonitorPixels.Height);

    public int DipsToPixels(double dips)
    {
        return (int)Math.Round(dips * Scale);
    }

    public double PixelsToDips(double pixels)
    {
        return pixels / Scale;
    }

    private Rect ToDips(NativeRect rect)
    {
        return new Rect(
            PixelsToDips(rect.Left),
            PixelsToDips(rect.Top),
            PixelsToDips(rect.Width),
            PixelsToDips(rect.Height));
    }
}

internal static class MonitorLayoutProvider
{
    public const int MonitorDefaultToNull = 0x00000000;
    public const int MonitorDefaultToNearest = 0x00000002;

    public static MonitorLayout FromWindow(nint hwnd)
    {
        var monitor = hwnd != nint.Zero
            ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
            : MonitorFromPoint(new NativePoint(0, 0), MonitorDefaultToNearest);

        return TryGetFromMonitor(monitor, hwnd, out var layout)
            ? layout
            : GetFallbackLayout(hwnd);
    }

    public static nint GetMonitorFromWindow(nint hwnd, int defaultTo)
    {
        return MonitorFromWindow(hwnd, defaultTo);
    }

    public static bool TryGetFromMonitor(nint monitor, nint dpiWindow, out MonitorLayout layout)
    {
        var info = new MonitorInfo
        {
            cbSize = Marshal.SizeOf<MonitorInfo>()
        };

        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info))
        {
            layout = new MonitorLayout(info.rcMonitor, info.rcWork, GetDpi(dpiWindow));
            return true;
        }

        layout = default;
        return false;
    }

    private static MonitorLayout GetFallbackLayout(nint dpiWindow)
    {
        var width = Math.Max(1, GetSystemMetrics(SystemMetricCxScreen));
        var height = Math.Max(1, GetSystemMetrics(SystemMetricCyScreen));
        var bounds = new NativeRect(0, 0, width, height);
        return new MonitorLayout(bounds, bounds, GetDpi(dpiWindow));
    }

    private static uint GetDpi(nint hwnd)
    {
        if (hwnd != nint.Zero)
        {
            var windowDpi = GetDpiForWindow(hwnd);
            if (windowDpi > 0)
            {
                return windowDpi;
            }
        }

        var systemDpi = GetDpiForSystem();
        return systemDpi > 0 ? systemDpi : MonitorLayout.DefaultDpi;
    }

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }
}
