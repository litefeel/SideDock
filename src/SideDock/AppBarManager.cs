using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SideDock;

internal sealed class AppBarManager
{
    private const int EdgeTolerancePixels = 1;
    private const int AbmNew = 0x00000000;
    private const int AbmRemove = 0x00000001;
    private const int AbmQueryPos = 0x00000002;
    private const int AbmSetPos = 0x00000003;
    private const int AbeLeft = 0;
    private const int AbeRight = 2;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoZOrder = 0x0004;

    private readonly nint _hwnd;
    private readonly uint _callbackMessage;
    private readonly ILogger<AppBarManager> _logger;
    private bool _isRegistered;
    private double _reservedWidthDips;
    private double _windowWidthDips;
    private double _windowHeightDips;
    private AppDockSide _dockSide = AppDockSide.Right;

    public AppBarManager(nint hwnd, ILogger<AppBarManager>? logger = null)
    {
        _hwnd = hwnd;
        _logger = logger ?? AppLogging.CreateLogger<AppBarManager>();
        _callbackMessage = RegisterWindowMessage("SideDock_AppBar_Callback");
    }

    public uint CallbackMessage => _callbackMessage;

    public void Register(double reservedWidthDips, double windowWidthDips, double windowHeightDips, AppDockSide dockSide, string reason)
    {
        if (_isRegistered)
        {
            _logger.LogInformation("Appbar already registered. Applying current layout. Reason={Reason}", reason);
            Apply(reservedWidthDips, windowWidthDips, windowHeightDips, dockSide, reason);
            return;
        }

        var data = CreateData();
        data.uCallbackMessage = _callbackMessage;
        var result = SHAppBarMessage(AbmNew, ref data);
        _isRegistered = true;
        _logger.LogInformation(
            "Appbar registered. Reason={Reason} Hwnd=0x{Hwnd:X} CallbackMessage={CallbackMessage} Result={Result}",
            reason,
            _hwnd,
            _callbackMessage,
            (ulong)result);
        Apply(reservedWidthDips, windowWidthDips, windowHeightDips, dockSide, reason);
    }

    public void Apply(double reservedWidthDips, double windowWidthDips, double windowHeightDips, AppDockSide dockSide, string reason)
    {
        _reservedWidthDips = reservedWidthDips;
        _windowWidthDips = windowWidthDips;
        _windowHeightDips = windowHeightDips;
        _dockSide = dockSide;
        if (!_isRegistered)
        {
            _logger.LogDebug("Appbar apply skipped because appbar is not registered. Reason={Reason}", reason);
            return;
        }

        var layout = MonitorLayoutProvider.FromWindow(_hwnd);
        var monitor = layout.MonitorPixels;
        var reservedWidthPixels = Math.Max(1, layout.DipsToPixels(reservedWidthDips));
        var windowWidthPixels = Math.Max(reservedWidthPixels, layout.DipsToPixels(windowWidthDips));
        var requestedWindowHeightPixels = Math.Max(1, layout.DipsToPixels(windowHeightDips));
        GetWindowRect(_hwnd, out var windowBefore);

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

        var queryBefore = data.rc;
        _logger.LogInformation(
            "Appbar apply started. Reason={Reason} DockSide={DockSide} ReservedWidthDips={ReservedWidthDips} WindowWidthDips={WindowWidthDips} WindowHeightDips={WindowHeightDips} ReservedWidthPixels={ReservedWidthPixels} WindowWidthPixels={WindowWidthPixels} RequestedWindowHeightPixels={RequestedWindowHeightPixels} Dpi={Dpi} Monitor={@Monitor} WorkArea={@WorkArea} WindowBefore={@WindowBefore} QueryBefore={@QueryBefore}",
            reason,
            dockSide,
            reservedWidthDips,
            windowWidthDips,
            windowHeightDips,
            reservedWidthPixels,
            windowWidthPixels,
            requestedWindowHeightPixels,
            layout.Dpi,
            ToLogRect(monitor),
            ToLogRect(layout.WorkPixels),
            ToLogRect(windowBefore),
            ToLogRect(queryBefore));

        var queryResult = SHAppBarMessage(AbmQueryPos, ref data);
        var queryAfter = data.rc;
        if (dockSide == AppDockSide.Left)
        {
            data.rc.Right = data.rc.Left + reservedWidthPixels;
        }
        else
        {
            data.rc.Left = data.rc.Right - reservedWidthPixels;
        }

        var setBefore = data.rc;
        var setResult = SHAppBarMessage(AbmSetPos, ref data);
        var setAfter = data.rc;

        var windowLeft = dockSide == AppDockSide.Left
            ? data.rc.Left
            : data.rc.Right - windowWidthPixels;
        var appBarHeightPixels = data.rc.Bottom - data.rc.Top;
        var windowHeightPixels = Math.Min(requestedWindowHeightPixels, appBarHeightPixels);
        var windowTop = data.rc.Top + Math.Max(0, (appBarHeightPixels - windowHeightPixels) / 2);

        var setWindowResult = SetWindowPos(
            _hwnd,
            nint.Zero,
            windowLeft,
            windowTop,
            windowWidthPixels,
            windowHeightPixels,
            SwpNoActivate | SwpNoZOrder);
        var setWindowError = setWindowResult ? 0 : Marshal.GetLastPInvokeError();
        GetWindowRect(_hwnd, out var windowAfter);

        _logger.LogInformation(
            "Appbar apply completed. Reason={Reason} QueryResult={QueryResult} QueryAfter={@QueryAfter} SetBefore={@SetBefore} SetResult={SetResult} SetAfter={@SetAfter} SetWindowResult={SetWindowResult} SetWindowError={SetWindowError} WindowAfter={@WindowAfter}",
            reason,
            (ulong)queryResult,
            ToLogRect(queryAfter),
            ToLogRect(setBefore),
            (ulong)setResult,
            ToLogRect(setAfter),
            setWindowResult,
            setWindowError,
            ToLogRect(windowAfter));

        if (dockSide == AppDockSide.Right && Math.Abs(windowAfter.Right - monitor.Right) > EdgeTolerancePixels)
        {
            _logger.LogWarning(
                "Right dock final window edge does not match monitor edge. Reason={Reason} WindowRight={WindowRight} MonitorRight={MonitorRight} Difference={Difference} WindowAfter={@WindowAfter} Monitor={@Monitor}",
                reason,
                windowAfter.Right,
                monitor.Right,
                monitor.Right - windowAfter.Right,
                ToLogRect(windowAfter),
                ToLogRect(monitor));
        }
        else if (dockSide == AppDockSide.Left && Math.Abs(windowAfter.Left - monitor.Left) > EdgeTolerancePixels)
        {
            _logger.LogWarning(
                "Left dock final window edge does not match monitor edge. Reason={Reason} WindowLeft={WindowLeft} MonitorLeft={MonitorLeft} Difference={Difference} WindowAfter={@WindowAfter} Monitor={@Monitor}",
                reason,
                windowAfter.Left,
                monitor.Left,
                windowAfter.Left - monitor.Left,
                ToLogRect(windowAfter),
                ToLogRect(monitor));
        }
    }

    public void Refresh(string reason)
    {
        _logger.LogInformation("Refreshing appbar. Reason={Reason}", reason);
        Apply(_reservedWidthDips, _windowWidthDips, _windowHeightDips, _dockSide, reason);
    }

    public void Unregister(string reason)
    {
        if (!_isRegistered)
        {
            _logger.LogDebug("Appbar unregister skipped because appbar is not registered. Reason={Reason}", reason);
            return;
        }

        var data = CreateData();
        var result = SHAppBarMessage(AbmRemove, ref data);
        _isRegistered = false;
        _logger.LogInformation("Appbar unregistered. Reason={Reason} Result={Result}", reason, (ulong)result);
    }

    private AppBarData CreateData()
    {
        return new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd
        };
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nuint SHAppBarMessage(int dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    private static object ToLogRect(NativeRect rect)
    {
        return new
        {
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            rect.Width,
            rect.Height
        };
    }

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

}
