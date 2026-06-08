using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SideDock;

public partial class MainWindow : Window
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int AbnPosChanged = 0x00000001;
    private const int DisplayIconSize = 24;

    private static readonly HttpClient IconHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _cursorTimer;
    private readonly List<ToolItem> _toolItems;
    private readonly Dictionary<string, WebView2> _browsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolStatuses = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _cursorLeftAt;
    private ToolItem? _currentItem;
    private CoreWebView2Environment? _webViewEnvironment;
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
        Topmost = true;

        _toolItems = _settings.Tools.Select(tool => new ToolItem(tool)).ToList();
        LoadCachedIcons();
        ToolList.ItemsSource = _toolItems;
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
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);

            foreach (var item in _toolItems)
            {
                await CreateBrowserAsync(item);
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

    private async Task CreateBrowserAsync(ToolItem item)
    {
        if (_webViewEnvironment is null || _browsers.ContainsKey(item.Tool.Id))
        {
            return;
        }

        var browser = new WebView2
        {
            Visibility = Visibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _browsers[item.Tool.Id] = browser;
        _toolStatuses[item.Tool.Id] = "Loading...";
        BrowserHost.Children.Add(browser);

        await browser.EnsureCoreWebView2Async(_webViewEnvironment);
        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
        browser.CoreWebView2.NavigationStarting += (_, _) => OnBrowserNavigationStarting(item.Tool);
        browser.CoreWebView2.NavigationCompleted += async (_, args) => await OnBrowserNavigationCompletedAsync(item, browser, args);
        browser.CoreWebView2.NewWindowRequested += (_, args) => OnNewWindowRequested(browser, args);
        browser.CoreWebView2.FaviconChanged += async (_, _) => await CacheFallbackFaviconAsync(item, browser);
        browser.CoreWebView2.SourceChanged += (_, _) =>
        {
            if (IsCurrentTool(item.Tool))
            {
                UpdateNavigationState();
            }
        };

        browser.CoreWebView2.Navigate(item.Tool.Url);

        if (IsCurrentTool(item.Tool))
        {
            ShowSelectedTool();
        }
    }

    private void LoadCachedIcons()
    {
        foreach (var item in _toolItems)
        {
            var cachePath = GetIconCachePath(item.Tool);
            if (TryLoadIcon(cachePath, out var icon))
            {
                item.Icon = icon;
            }
        }
    }

    private async Task CacheBestIconAsync(ToolItem item, WebView2 browser)
    {
        if (browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var script = """
                (() => Array.from(document.querySelectorAll('link[rel]'))
                    .filter(link => /icon/i.test(link.rel))
                    .map(link => ({
                        href: link.href,
                        rel: link.rel,
                        sizes: link.sizes ? link.sizes.value : ''
                    })))()
                """;
            var json = await browser.CoreWebView2.ExecuteScriptAsync(script);
            var candidates = JsonSerializer.Deserialize<List<IconCandidate>>(json, JsonOptions) ?? [];

            foreach (var candidate in candidates
                         .Where(candidate => IsSupportedIconCandidate(candidate.Href))
                         .OrderByDescending(GetIconScore))
            {
                if (await TryDownloadAndCacheIconAsync(item, candidate.Href))
                {
                    return;
                }
            }

            await CacheFallbackFaviconAsync(item, browser);
        }
        catch
        {
            await CacheFallbackFaviconAsync(item, browser);
        }
    }

    private async Task CacheFallbackFaviconAsync(ToolItem item, WebView2 browser)
    {
        if (browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(browser.CoreWebView2.FaviconUri))
        {
            return;
        }

        try
        {
            using var faviconStream = await browser.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            await CacheIconStreamAsync(item, faviconStream);
        }
        catch
        {
            // Keep the default icon when a site does not expose a usable favicon.
        }
    }

    private static async Task<bool> TryDownloadAndCacheIconAsync(ToolItem item, string href)
    {
        try
        {
            using var response = await IconHttpClient.GetAsync(href);
            if (!response.IsSuccessStatusCode || !IsSupportedContentType(response.Content.Headers.ContentType?.MediaType))
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            return await CacheIconStreamAsync(item, stream);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CacheIconStreamAsync(ToolItem item, Stream iconStream)
    {
        var cachePath = GetIconCachePath(item.Tool);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        await using (var fileStream = File.Create(cachePath))
        {
            await iconStream.CopyToAsync(fileStream);
        }

        if (!TryLoadIcon(cachePath, out var icon))
        {
            return false;
        }

        item.Icon = icon;
        return true;
    }

    private static bool TryLoadIcon(string path, out BitmapImage? icon)
    {
        icon = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            icon = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedIconCandidate(string href)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedContentType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        return mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("image/x-icon", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("image/vnd.microsoft.icon", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetIconScore(IconCandidate candidate)
    {
        var score = GetLargestDeclaredSize(candidate.Sizes);

        if (candidate.Rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }

        if (score >= DisplayIconSize)
        {
            score += 500;
        }

        return score;
    }

    private static int GetLargestDeclaredSize(string sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes))
        {
            return 0;
        }

        if (sizes.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return 512;
        }

        var largest = 0;
        foreach (var part in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dimensions = part.Split('x', 'X');
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], out var width))
            {
                largest = Math.Max(largest, width);
            }
        }

        return largest;
    }

    private static string GetIconCachePath(ToolDefinition tool)
    {
        var safeId = string.Concat(tool.Id.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "Icons",
            $"{safeId}.icon");
    }

    private async void OnToolSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        await ShowSelectedToolAsync();
        Expand();
    }

    private async void OnToolListMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await ShowSelectedToolAsync();
        Expand();
    }

    private async Task ShowSelectedToolAsync()
    {
        if (ToolList.SelectedItem is ToolItem item && !_browsers.ContainsKey(item.Tool.Id))
        {
            await CreateBrowserAsync(item);
        }

        ShowSelectedTool();
    }

    private void ShowSelectedTool()
    {
        if (ToolList.SelectedItem is not ToolItem item)
        {
            return;
        }

        _currentItem = item;
        TitleText.Text = item.Tool.Title;
        UrlText.Text = GetCurrentUrl();

        foreach (var (toolId, browser) in _browsers)
        {
            browser.Visibility = toolId.Equals(item.Tool.Id, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Hidden;
        }

        SetStatus(_toolStatuses.TryGetValue(item.Tool.Id, out var status) ? status : "Waiting for WebView2...");
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

    private async Task OnBrowserNavigationCompletedAsync(ToolItem item, WebView2 browser, CoreWebView2NavigationCompletedEventArgs e)
    {
        _toolStatuses[item.Tool.Id] = e.IsSuccess ? "Ready" : $"Load failed: {e.WebErrorStatus}";

        if (e.IsSuccess)
        {
            await CacheBestIconAsync(item, browser);
        }

        if (!IsCurrentTool(item.Tool))
        {
            return;
        }

        UpdateNavigationState();
        SetStatus(_toolStatuses[item.Tool.Id]);
    }

    private static void OnNewWindowRequested(WebView2 browser, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri))
        {
            browser.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        OpenExternal(GetCurrentUrl());
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        Collapse(force: true);
    }

    private void OnClosePageClick(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            Collapse(force: true);
            return;
        }

        if (_browsers.Remove(_currentItem.Tool.Id, out var browser))
        {
            BrowserHost.Children.Remove(browser);
            browser.Dispose();
        }

        _toolStatuses[_currentItem.Tool.Id] = "Closed";
        SetStatus("Closed");
        Collapse(force: true);
    }

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        _isPinned = PinButton.IsChecked == true;
        PinIconText.Text = _isPinned ? "\uE718" : "\uE77A";
        if (_isPinned)
        {
            Expand();
        }

        ApplyTopmostState();
        DockToLeftEdge(_isExpanded ? _expandedWidth : _settings.CollapsedWidth);
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

    private void Collapse(bool force = false)
    {
        if (_isPinned && !force)
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
        if (!_areWebViewsReady || _currentItem is null)
        {
            return;
        }

        UrlText.Text = GetCurrentUrl();
    }

    private string GetCurrentUrl()
    {
        var browser = GetCurrentBrowser();
        if (!string.IsNullOrWhiteSpace(browser?.Source?.AbsoluteUri))
        {
            return browser.Source.AbsoluteUri;
        }

        return _currentItem?.Tool.Url ?? "about:blank";
    }

    private WebView2? GetCurrentBrowser()
    {
        return _currentItem is not null && _browsers.TryGetValue(_currentItem.Tool.Id, out var browser)
            ? browser
            : null;
    }

    private bool IsCurrentTool(ToolDefinition tool)
    {
        return _currentItem?.Tool.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase) == true;
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
        Topmost = true;
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
        if (string.IsNullOrWhiteSpace(UrlText.Text) || UrlText.Text == "about:blank")
        {
            UrlText.Text = message;
        }
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

    private sealed record IconCandidate(string Href, string Rel, string Sizes);
}
