using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;

namespace SideDock;

public partial class MainWindow : Window
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int AbnPosChanged = 0x00000001;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _cursorTimer;
    private readonly Dictionary<string, WebView2> _browsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolStatuses = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _cursorLeftAt;
    private ToolDefinition? _currentTool;
    private double _expandedWidth;
    private bool _isExpanded = true;
    private bool _isPinned;
    private bool _isResizing;
    private bool _areWebViewsReady;
    private HwndSource? _hwndSource;
    private AppBarManager? _appBarManager;

    public MainWindow()
    {
        InitializeComponent();

        _expandedWidth = ClampExpandedWidth(_settings.DefaultExpandedWidth);
        Width = _expandedWidth;
        MinWidth = _settings.CollapsedWidth;
        Topmost = _settings.TopmostByDefault;
        TopmostButton.IsChecked = Topmost;

        ToolList.ItemsSource = _settings.Tools;
        ToolList.SelectedIndex = 0;

        _cursorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _cursorTimer.Tick += OnCursorTimerTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DockToLeftEdge(_expandedWidth);
        Collapse();
        _cursorTimer.Start();
        await InitializeWebViewsAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        _appBarManager = new AppBarManager(handle);
        _appBarManager.Register(GetReservedWidth(Width), Width);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _cursorTimer.Stop();
        _hwndSource?.RemoveHook(WndProc);
        _appBarManager?.Unregister();

        foreach (var browser in _browsers.Values)
        {
            browser.Dispose();
        }
    }

    private async Task InitializeWebViewsAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SideDock",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);

            foreach (var tool in _settings.Tools)
            {
                await CreateBrowserAsync(environment, tool);
            }

            _areWebViewsReady = true;
            ShowSelectedTool();
        }
        catch (Exception ex)
        {
            var runtimeHint = ex.GetType().Name.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                ? "WebView2 Runtime is missing or unavailable. Install Microsoft Edge WebView2 Runtime, then run SideDock again."
                : "WebView2 could not start.";

            SetStatus(runtimeHint);
            MessageBox.Show(
                $"{runtimeHint}\n\n{ex.Message}",
                "SideDock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CreateBrowserAsync(CoreWebView2Environment environment, ToolDefinition tool)
    {
        var browser = new WebView2
        {
            Visibility = Visibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _browsers[tool.Id] = browser;
        _toolStatuses[tool.Id] = "Loading...";
        BrowserHost.Children.Add(browser);

        await browser.EnsureCoreWebView2Async(environment);
        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
        browser.CoreWebView2.NavigationStarting += (_, _) => OnBrowserNavigationStarting(tool);
        browser.CoreWebView2.NavigationCompleted += (_, args) => OnBrowserNavigationCompleted(tool, browser, args);
        browser.CoreWebView2.NewWindowRequested += (_, args) => OnNewWindowRequested(browser, args);
        browser.CoreWebView2.SourceChanged += (_, _) =>
        {
            if (IsCurrentTool(tool))
            {
                UpdateNavigationState();
            }
        };

        browser.CoreWebView2.Navigate(tool.Url);

        if (IsCurrentTool(tool))
        {
            ShowSelectedTool();
        }
    }

    private void OnToolSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ShowSelectedTool();
        Expand();
    }

    private void OnToolListMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Expand();
    }

    private void ShowSelectedTool()
    {
        if (ToolList.SelectedItem is not ToolDefinition tool)
        {
            return;
        }

        _currentTool = tool;
        TitleText.Text = tool.Title;
        UrlText.Text = GetCurrentUrl();

        foreach (var (toolId, browser) in _browsers)
        {
            browser.Visibility = toolId.Equals(tool.Id, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Hidden;
        }

        SetStatus(_toolStatuses.TryGetValue(tool.Id, out var status) ? status : "Waiting for WebView2...");
        UpdateNavigationState();
    }

    private void OnBrowserNavigationStarting(ToolDefinition tool)
    {
        _toolStatuses[tool.Id] = "Loading...";
        if (IsCurrentTool(tool))
        {
            SetStatus("Loading...");
        }
    }

    private void OnBrowserNavigationCompleted(ToolDefinition tool, WebView2 browser, CoreWebView2NavigationCompletedEventArgs e)
    {
        _toolStatuses[tool.Id] = e.IsSuccess ? "Ready" : $"Load failed: {e.WebErrorStatus}";

        if (!IsCurrentTool(tool))
        {
            return;
        }

        UpdateNavigationState();
        SetStatus(_toolStatuses[tool.Id]);
    }

    private static void OnNewWindowRequested(WebView2 browser, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            browser.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        var browser = GetCurrentBrowser();
        if (browser?.CoreWebView2.CanGoBack == true)
        {
            browser.CoreWebView2.GoBack();
        }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        var browser = GetCurrentBrowser();
        if (browser?.CoreWebView2.CanGoForward == true)
        {
            browser.CoreWebView2.GoForward();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        GetCurrentBrowser()?.CoreWebView2.Reload();
    }

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        OpenExternal(GetCurrentUrl());
    }

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        _isPinned = PinButton.IsChecked == true;
        if (_isPinned)
        {
            Expand();
        }

        ApplyTopmostState();
        DockToLeftEdge(_isExpanded ? _expandedWidth : _settings.CollapsedWidth);
    }

    private void OnTopmostChanged(object sender, RoutedEventArgs e)
    {
        ApplyTopmostState();
    }

    private void OnCursorTimerTick(object? sender, EventArgs e)
    {
        if (!_isExpanded || _isResizing || !TryGetCursorPosition(out var cursor))
        {
            return;
        }

        var windowRect = GetWindowScreenRect();
        var cursorInside = windowRect.Contains(cursor);

        if (_isPinned || cursorInside)
        {
            _cursorLeftAt = null;
            return;
        }

        _cursorLeftAt ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _cursorLeftAt.Value >= TimeSpan.FromMilliseconds(_settings.AutoHideDelayMilliseconds))
        {
            Collapse();
        }
    }

    private void Expand()
    {
        if (_isExpanded)
        {
            return;
        }

        _isExpanded = true;
        _cursorLeftAt = null;
        ContentColumn.Width = new GridLength(1, GridUnitType.Star);
        ResizeColumn.Width = new GridLength(18);
        ContentPanel.Visibility = Visibility.Visible;
        ResizeGrip.Visibility = Visibility.Visible;
        ApplyTopmostState();
        DockToLeftEdge(_expandedWidth);
    }

    private void Collapse()
    {
        if (_isPinned)
        {
            return;
        }

        _isExpanded = false;
        ContentPanel.Visibility = Visibility.Collapsed;
        ResizeGrip.Visibility = Visibility.Collapsed;
        ContentColumn.Width = new GridLength(0);
        ResizeColumn.Width = new GridLength(0);
        ApplyTopmostState();
        DockToLeftEdge(_settings.CollapsedWidth);
    }

    private void OnResizeGripMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isExpanded)
        {
            return;
        }

        _isResizing = true;
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        var requestedWidth = e.GetPosition(this).X;
        _expandedWidth = ClampExpandedWidth(requestedWidth);
        ResizeWindowOnly(_expandedWidth);
        e.Handled = true;
    }

    private void OnResizeGripMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        ResizeGrip.ReleaseMouseCapture();
        DockToLeftEdge(_expandedWidth);
        e.Handled = true;
    }

    private void ResizeWindowOnly(double width)
    {
        Width = ClampExpandedWidth(width);
        Height = SystemParameters.PrimaryScreenHeight;
    }

    private void DockToLeftEdge(double width)
    {
        var clampedWidth = _isExpanded
            ? ClampExpandedWidth(width)
            : _settings.CollapsedWidth;
        Width = clampedWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        if (_appBarManager is not null)
        {
            _appBarManager.Apply(GetReservedWidth(clampedWidth), clampedWidth);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left;
        Top = workArea.Top;
        Height = workArea.Height;
    }

    private Rect GetWindowScreenRect()
    {
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height)));
        return new Rect(topLeft, bottomRight);
    }

    private void UpdateNavigationState()
    {
        if (!_areWebViewsReady || _currentTool is null)
        {
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            return;
        }

        var browser = GetCurrentBrowser();
        UrlText.Text = GetCurrentUrl();
        BackButton.IsEnabled = browser?.CoreWebView2.CanGoBack == true;
        ForwardButton.IsEnabled = browser?.CoreWebView2.CanGoForward == true;
    }

    private string GetCurrentUrl()
    {
        var browser = GetCurrentBrowser();
        if (!string.IsNullOrWhiteSpace(browser?.Source?.AbsoluteUri))
        {
            return browser.Source.AbsoluteUri;
        }

        return _currentTool?.Url ?? "about:blank";
    }

    private WebView2? GetCurrentBrowser()
    {
        return _currentTool is not null && _browsers.TryGetValue(_currentTool.Id, out var browser)
            ? browser
            : null;
    }

    private bool IsCurrentTool(ToolDefinition tool)
    {
        return _currentTool?.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase) == true;
    }

    private double GetReservedWidth(double windowWidth)
    {
        return _isExpanded && _isPinned
            ? windowWidth
            : _settings.CollapsedWidth;
    }

    private double ClampExpandedWidth(double width)
    {
        return Math.Clamp(width, _settings.MinExpandedWidth, GetMaxExpandedWidth());
    }

    private double GetMaxExpandedWidth()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        return Math.Max(_settings.MinExpandedWidth, screenWidth - _settings.CollapsedWidth);
    }

    private void ApplyTopmostState()
    {
        Topmost = TopmostButton.IsChecked == true || (_isExpanded && !_isPinned);
    }

    private void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open external browser: {ex.Message}");
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBarManager is not null && unchecked((uint)msg) == _appBarManager.CallbackMessage)
        {
            if (wParam.ToInt32() == AbnPosChanged)
            {
                Dispatcher.BeginInvoke(() => _appBarManager.Refresh());
            }

            handled = true;
            return IntPtr.Zero;
        }

        if (msg is WmDisplayChange or WmSettingChange or WmDpiChanged)
        {
            Dispatcher.BeginInvoke(() => DockToLeftEdge(_isExpanded ? _expandedWidth : _settings.CollapsedWidth));
        }

        return IntPtr.Zero;
    }

    private static bool TryGetCursorPosition(out Point point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new Point(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }
}
