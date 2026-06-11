using System.Runtime.InteropServices;
using System.Windows;

namespace SideDock;

internal sealed class AppBarManager
{
    private const int AbmNew = 0x00000000;
    private const int AbmRemove = 0x00000001;
    private const int AbmQueryPos = 0x00000002;
    private const int AbmSetPos = 0x00000003;
    private const int AbeLeft = 0;
    private const int AbeRight = 2;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;
    private const int MonitorDefaultToNearest = 0x00000002;

    private readonly nint _hwnd;
    private readonly uint _callbackMessage;
    private bool _isRegistered;
    private double _reservedWidthDips;
    private double _windowWidthDips;
    private double _windowHeightDips;
    private AppDockSide _dockSide = AppDockSide.Right;

    public AppBarManager(nint hwnd)
    {
        _hwnd = hwnd;
        _callbackMessage = RegisterWindowMessage("SideDock_AppBar_Callback");
    }

    public uint CallbackMessage => _callbackMessage;

    public void Register(double reservedWidthDips, double windowWidthDips, double windowHeightDips, AppDockSide dockSide)
    {
        if (_isRegistered)
        {
            Apply(reservedWidthDips, windowWidthDips, windowHeightDips, dockSide);
            return;
        }

        var data = CreateData();
        data.uCallbackMessage = _callbackMessage;
        SHAppBarMessage(AbmNew, ref data);
        _isRegistered = true;
        Apply(reservedWidthDips, windowWidthDips, windowHeightDips, dockSide);
    }

    public void Apply(double reservedWidthDips, double windowWidthDips, double windowHeightDips, AppDockSide dockSide)
    {
        _reservedWidthDips = reservedWidthDips;
        _windowWidthDips = windowWidthDips;
        _windowHeightDips = windowHeightDips;
        _dockSide = dockSide;
        if (!_isRegistered)
        {
            return;
        }

        var monitor = GetMonitorRect();
        var reservedWidthPixels = Math.Max(1, DipsToPixels(reservedWidthDips));
        var windowWidthPixels = Math.Max(reservedWidthPixels, DipsToPixels(windowWidthDips));
        var requestedWindowHeightPixels = Math.Max(1, DipsToPixels(windowHeightDips));

        var data = CreateData();
        data.uEdge = (uint)(dockSide == AppDockSide.Left ? AbeLeft : AbeRight);
        data.rc = dockSide == AppDockSide.Left
            ? new NativeRect
            {
                Left = monitor.Left,
                Top = monitor.Top,
                Right = monitor.Left + reservedWidthPixels,
                Bottom = monitor.Bottom
            }
            : new NativeRect
            {
                Left = monitor.Right - reservedWidthPixels,
                Top = monitor.Top,
                Right = monitor.Right,
                Bottom = monitor.Bottom
            };

        SHAppBarMessage(AbmQueryPos, ref data);
        if (dockSide == AppDockSide.Left)
        {
            data.rc.Right = data.rc.Left + reservedWidthPixels;
        }
        else
        {
            data.rc.Left = data.rc.Right - reservedWidthPixels;
        }

        SHAppBarMessage(AbmSetPos, ref data);

        var windowLeft = dockSide == AppDockSide.Left
            ? data.rc.Left
            : data.rc.Right - windowWidthPixels;
        var appBarHeightPixels = data.rc.Bottom - data.rc.Top;
        var windowHeightPixels = Math.Min(requestedWindowHeightPixels, appBarHeightPixels);
        var windowTop = data.rc.Top + Math.Max(0, (appBarHeightPixels - windowHeightPixels) / 2);

        SetWindowPos(
            _hwnd,
            nint.Zero,
            windowLeft,
            windowTop,
            windowWidthPixels,
            windowHeightPixels,
            SwpNoActivate | SwpNoZOrder);
    }

    public void Refresh()
    {
        Apply(_reservedWidthDips, _windowWidthDips, _windowHeightDips, _dockSide);
    }

    public void Unregister()
    {
        if (!_isRegistered)
        {
            return;
        }

        var data = CreateData();
        SHAppBarMessage(AbmRemove, ref data);
        _isRegistered = false;
    }

    private AppBarData CreateData()
    {
        return new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd
        };
    }

    private NativeRect GetMonitorRect()
    {
        var monitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo
        {
            cbSize = Marshal.SizeOf<MonitorInfo>()
        };

        return GetMonitorInfo(monitor, ref info)
            ? info.rcMonitor
            : new NativeRect
            {
                Left = 0,
                Top = 0,
                Right = DipsToPixels(SystemParameters.PrimaryScreenWidth),
                Bottom = DipsToPixels(SystemParameters.PrimaryScreenHeight)
            };
    }

    private int DipsToPixels(double dips)
    {
        return (int)Math.Round(dips * GetDpiForWindow(_hwnd) / 96.0);
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nuint SHAppBarMessage(int dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public nint lParam;
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
