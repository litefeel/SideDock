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

    private const int MonitorInfoPrimary = 0x00000001;
    private const int DisplayDeviceActive = 0x00000001;
    private const int DisplayDeviceMirroringDriver = 0x00000008;
    private const int MonitorDpiTypeEffective = 0;

    public static MonitorLayout FromWindow(nint hwnd)
    {
        var monitor = hwnd != nint.Zero
            ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
            : MonitorFromPoint(new NativePoint(0, 0), MonitorDefaultToNearest);

        return TryGetFromMonitor(monitor, hwnd, out var layout)
            ? layout
            : GetFallbackLayout(hwnd);
    }

    public static IReadOnlyList<DisplayMonitor> GetActiveDisplays(nint dpiWindow)
    {
        var displays = new List<DisplayMonitor>();
        EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (nint monitor, nint monitorDc, ref NativeRect monitorRect, nint data) =>
            {
                if (TryGetDisplayMonitor(monitor, dpiWindow, out var display))
                {
                    displays.Add(display);
                }

                return true;
            },
            nint.Zero);

        return displays
            .OrderBy(display => display.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static nint GetMonitorFromWindow(nint hwnd, int defaultTo)
    {
        return MonitorFromWindow(hwnd, defaultTo);
    }

    public static bool TryGetFromMonitor(nint monitor, nint dpiWindow, out MonitorLayout layout)
    {
        if (TryGetMonitorInfo(monitor, out var info))
        {
            layout = new MonitorLayout(
                info.rcMonitor,
                info.rcWork,
                GetDpi(monitor, dpiWindow));
            return true;
        }

        layout = default;
        return false;
    }

    private static bool TryGetDisplayMonitor(
        nint monitor,
        nint dpiWindow,
        out DisplayMonitor display)
    {
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            display = null!;
            return false;
        }

        var monitorDevice = GetMonitorDevice(info.szDevice);
        var deviceId = monitorDevice?.DeviceID?.Trim();
        var displayId = string.IsNullOrWhiteSpace(deviceId)
            ? "gdi:" + info.szDevice.Trim()
            : "pnp:" + deviceId;
        var friendlyName = monitorDevice?.DeviceString?.Trim();
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            friendlyName = info.szDevice.Trim();
        }

        var layout = new MonitorLayout(
            info.rcMonitor,
            info.rcWork,
            GetDpi(monitor, dpiWindow));
        display = new DisplayMonitor(
            monitor,
            displayId,
            info.szDevice.Trim(),
            friendlyName,
            (info.dwFlags & MonitorInfoPrimary) != 0,
            layout);
        return true;
    }

    private static DisplayDevice? GetMonitorDevice(string deviceName)
    {
        DisplayDevice? firstDevice = null;
        for (uint index = 0; ; index++)
        {
            var device = CreateDisplayDevice();
            if (!EnumDisplayDevices(deviceName, index, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & DisplayDeviceMirroringDriver) != 0)
            {
                continue;
            }

            firstDevice ??= device;
            if ((device.StateFlags & DisplayDeviceActive) != 0
                && !string.IsNullOrWhiteSpace(device.DeviceID))
            {
                return device;
            }
        }

        return firstDevice;
    }

    private static bool TryGetMonitorInfo(nint monitor, out MonitorInfoEx info)
    {
        info = new MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<MonitorInfoEx>(),
            szDevice = string.Empty
        };
        return monitor != nint.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static DisplayDevice CreateDisplayDevice()
    {
        return new DisplayDevice
        {
            cb = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty
        };
    }

    private static MonitorLayout GetFallbackLayout(nint dpiWindow)
    {
        var width = Math.Max(1, GetSystemMetrics(SystemMetricCxScreen));
        var height = Math.Max(1, GetSystemMetrics(SystemMetricCyScreen));
        var bounds = new NativeRect(0, 0, width, height);
        return new MonitorLayout(bounds, bounds, GetDpi(nint.Zero, dpiWindow));
    }

    private static uint GetDpi(nint monitor, nint hwnd)
    {
        if (monitor != nint.Zero
            && GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            return dpiX;
        }

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

    private delegate bool MonitorEnumProc(
        nint monitor,
        nint monitorDc,
        ref NativeRect monitorRect,
        nint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string lpDevice,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
