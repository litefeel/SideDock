using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SideDock;

public partial class MainWindow : Window
{
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int AbnPosChanged = 0x00000001;
    private const int AbnFullscreenApp = 0x00000002;
    private const int DisplayIconSize = 24;
    private const int PreferredIconFrameSize = 48;
    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;
    private const int MonitorDefaultToNull = 0x00000000;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const int GaRoot = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int FullscreenEdgeTolerancePixels = 2;
    private const int MaxWindowTextLength = 512;
    private const string ProjectHomeUrl = "https://github.com/litefeel/SideDock";
    private const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValueName = "SideDock";

    private static readonly HttpClient IconHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] ScreenshotProcessNames =
    [
        "FSCapture",
        "Greenshot",
        "Lightshot",
        "PicPick",
        "ScreenClippingHost",
        "ShareX",
        "Snipaste",
        "SnippingTool"
    ];

    private static readonly string[] ScreenshotWindowKeywords =
    [
        "capture",
        "screen clipping",
        "screenshot",
        "screenclip",
        "snip",
        "\u622A\u56FE",
        "\u622A\u5C4F"
    ];

    private static readonly string[] ShellWindowClassNames =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    ];

    private const string ExternalBlankLinkMessageType = "sideDock.openExternalBlankLink";
    private const string ExternalBlankLinkScript = """
        (() => {
            if (window.__sideDockExternalBlankLinkHandlerInstalled) {
                return;
            }

            window.__sideDockExternalBlankLinkHandlerInstalled = true;

            document.addEventListener('click', event => {
                if (event.defaultPrevented || event.button !== 0) {
                    return;
                }

                const element = event.target instanceof Element ? event.target : event.target?.parentElement;
                const anchor = element?.closest?.('a[target]');
                const target = anchor?.getAttribute('target')?.trim().toLowerCase();
                if (!anchor || (target !== '_blank' && target !== '_new') || !anchor.href) {
                    return;
                }

                window.chrome?.webview?.postMessage({
                    type: 'sideDock.openExternalBlankLink',
                    href: anchor.href
                });
                event.preventDefault();
            });
        })();
        """;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _cursorTimer;
    private readonly ObservableCollection<ToolItem> _toolItems;
    private readonly Dictionary<string, WebView2> _browsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolStatuses = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _cursorLeftAt;
    private ToolItem? _currentItem;
    private CoreWebView2Environment? _webViewEnvironment;
    private double _expandedWidth;
    private bool _isExpanded = true;
    private bool _isPinned;
    private bool _isResizing;
    private double _resizeAnchorEdge;
    private double _resizeContentFixedEdge;
    private double _pendingResizeWidth;
    private Window? _resizePreviewWindow;
    private Canvas? _resizePreviewCanvas;
    private Border? _resizePreviewPanel;
    private WebView2? _resizePreviewBrowser;
    private bool _isContentHiddenForResize;
    private bool _areWebViewsReady;
    private bool _isAutoHiddenForFullscreen;
    private HwndSource? _hwndSource;
    private AppBarManager? _appBarManager;

    public MainWindow()
    {
        InitializeComponent();

        ApplyTheme();
        ApplyStartupSetting();
        ApplyDockSideLayout();
        _expandedWidth = ClampExpandedWidth(_settings.DefaultExpandedWidth);
        Width = _expandedWidth;
        MinWidth = _settings.CollapsedWidth;
        Topmost = true;

        _toolItems = new ObservableCollection<ToolItem>(_settings.Tools.Select(tool => new ToolItem(tool)));
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
        DockToConfiguredEdge(_expandedWidth);
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
        _appBarManager.Register(GetReservedWidth(Width), Width, GetDockSide());
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _cursorTimer.Stop();
        _hwndSource?.RemoveHook(WndProc);
        _appBarManager?.Unregister();
        MoveResizePreviewBrowserBack();
        CloseResizePreview();

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
        ApplyBrowserTheme(browser);
        await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ExternalBlankLinkScript);
        browser.CoreWebView2.WebMessageReceived += (_, args) => OnBrowserWebMessageReceived(args);
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

        await using var memoryStream = new MemoryStream();
        await iconStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        if (!TrySelectIconFrame(memoryStream, out var frame))
        {
            return false;
        }

        if (TryGetIconSize(cachePath, out var cachedWidth, out var cachedHeight)
            && IsPreferredIconSize(cachedWidth, cachedHeight)
            && !IsPreferredIconSize(frame.PixelWidth, frame.PixelHeight))
        {
            if (!TryLoadIcon(cachePath, out var cachedIcon))
            {
                return false;
            }

            item.Icon = cachedIcon;
            return true;
        }

        await using (var fileStream = File.Create(cachePath))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            encoder.Save(fileStream);
        }

        if (!TryLoadIcon(cachePath, out var icon))
        {
            return false;
        }

        item.Icon = icon;
        return true;
    }

    private static bool TrySelectIconFrame(Stream iconStream, out BitmapSource frame)
    {
        frame = null!;

        try
        {
            var decoder = BitmapDecoder.Create(
                iconStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var selectedFrame = decoder.Frames
                .Where(candidate => IsPreferredIconSize(candidate.PixelWidth, candidate.PixelHeight))
                .OrderBy(candidate => Math.Max(candidate.PixelWidth, candidate.PixelHeight))
                .ThenBy(candidate => Math.Min(candidate.PixelWidth, candidate.PixelHeight))
                .FirstOrDefault()
                ?? decoder.Frames
                    .OrderByDescending(candidate => Math.Max(candidate.PixelWidth, candidate.PixelHeight))
                    .ThenByDescending(candidate => Math.Min(candidate.PixelWidth, candidate.PixelHeight))
                    .FirstOrDefault();

            if (selectedFrame is null)
            {
                return false;
            }

            frame = selectedFrame;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetIconSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();

            if (frame is null)
            {
                return false;
            }

            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPreferredIconSize(int width, int height)
    {
        return width >= PreferredIconFrameSize && height >= PreferredIconFrameSize;
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
    }

    private void OnToolListMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null
            && !TryGetToolIconHit(e, out _))
        {
            e.Handled = true;
        }
    }

    private async void OnToolListMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetToolIconHit(e, out var item))
        {
            e.Handled = true;
            return;
        }

        ToolList.SelectedItem = item;
        await ShowSelectedToolAsync();
        Expand();
        e.Handled = true;
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

    private void OnBrowserWebMessageReceived(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (TryGetExternalBlankLinkUrl(e.WebMessageAsJson, out var url))
        {
            OpenExternal(url);
        }
    }

    private static bool TryGetExternalBlankLinkUrl(string json, out string url)
    {
        url = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !string.Equals(typeElement.GetString(), ExternalBlankLinkMessageType, StringComparison.Ordinal)
                || !root.TryGetProperty("href", out var hrefElement)
                || hrefElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var href = hrefElement.GetString();
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            url = uri.AbsoluteUri;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SettingsButton.ContextMenu is null)
        {
            return;
        }

        UpdateSettingsMenuChecks();
        SettingsButton.ContextMenu.PlacementTarget = SettingsButton;
        SettingsButton.ContextMenu.Placement = GetDockSide() == AppDockSide.Left
            ? System.Windows.Controls.Primitives.PlacementMode.Right
            : System.Windows.Controls.Primitives.PlacementMode.Left;
        SettingsButton.ContextMenu.IsOpen = true;
    }

    private void OnThemeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string themeMode })
        {
            return;
        }

        _settings.ThemeMode = AppSettings.NormalizeThemeMode(themeMode).ToString();
        AppSettings.Save(_settings);
        ApplyTheme();
        ApplyBrowserThemes();
        UpdateSettingsMenuChecks();
    }

    private void OnDockSideMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string dockSide })
        {
            return;
        }

        _settings.DockSide = AppSettings.NormalizeDockSide(dockSide).ToString();
        AppSettings.Save(_settings);
        ApplyDockSideLayout();
        DockToConfiguredEdge(_isExpanded ? _expandedWidth : _settings.CollapsedWidth);
        UpdateSettingsMenuChecks();
    }

    private void UpdateSettingsMenuChecks()
    {
        var themeMode = GetThemeMode();
        ThemeDarkMenuItem.IsChecked = themeMode == AppThemeMode.Dark;
        ThemeLightMenuItem.IsChecked = themeMode == AppThemeMode.Light;
        ThemeSystemMenuItem.IsChecked = themeMode == AppThemeMode.System;

        var dockSide = GetDockSide();
        DockLeftMenuItem.IsChecked = dockSide == AppDockSide.Left;
        DockRightMenuItem.IsChecked = dockSide == AppDockSide.Right;

        StartWithWindowsMenuItem.IsChecked = _settings.StartWithWindows;
        CurrentVersionMenuItem.Header = $"Version {GetCurrentVersion()}";
    }

    private AppThemeMode GetThemeMode()
    {
        return AppSettings.NormalizeThemeMode(_settings.ThemeMode);
    }

    private AppDockSide GetDockSide()
    {
        return AppSettings.NormalizeDockSide(_settings.DockSide);
    }

    private void ApplyDockSideLayout()
    {
        var dockSide = GetDockSide();
        var railWidth = _settings.CollapsedWidth;
        var resizeWidth = _isExpanded ? 18 : 0;

        if (dockSide == AppDockSide.Left)
        {
            ResizeColumn.Width = new GridLength(railWidth);
            RailColumn.Width = new GridLength(resizeWidth);
            Grid.SetColumn(RailBorder, 0);
            Grid.SetColumn(ContentPanel, 1);
            Grid.SetColumn(ResizeGrip, 2);
            RailBorder.BorderThickness = new Thickness(0, 0, 1, 0);
        }
        else
        {
            ResizeColumn.Width = new GridLength(resizeWidth);
            RailColumn.Width = new GridLength(railWidth);
            Grid.SetColumn(ResizeGrip, 0);
            Grid.SetColumn(ContentPanel, 1);
            Grid.SetColumn(RailBorder, 2);
            RailBorder.BorderThickness = new Thickness(1, 0, 0, 0);
        }
    }

    private void ApplyTheme()
    {
        var themeMode = GetThemeMode();
        var useLightTheme = themeMode == AppThemeMode.Light
            || (themeMode == AppThemeMode.System && IsSystemLightTheme());

        if (useLightTheme)
        {
            SetThemeBrush("HeaderButtonForeground", Color.FromRgb(33, 38, 48));
            SetThemeBrush("HeaderButtonHoverBackground", Color.FromRgb(220, 228, 240));
            SetThemeBrush("HeaderButtonHoverBorder", Color.FromRgb(164, 177, 198));
            SetThemeBrush("HeaderButtonCheckedBackground", Color.FromRgb(44, 125, 250));
            SetThemeBrush("HeaderButtonCheckedHoverBackground", Color.FromRgb(65, 142, 255));
            SetThemeBrush("HeaderButtonCheckedBorder", Color.FromRgb(37, 99, 235));
            SetThemeBrush("RailBackground", Color.FromArgb(236, 245, 247, 251));
            SetThemeBrush("RailBorderBrush", Color.FromArgb(220, 202, 211, 224));
            SetThemeBrush("ToolListForeground", Color.FromRgb(33, 38, 48));
            SetThemeBrush("ToolItemHoverBackground", Color.FromRgb(220, 228, 240));
            SetThemeBrush("ToolItemHoverBorder", Color.FromRgb(164, 177, 198));
            SetThemeBrush("ToolItemSelectedBackground", Color.FromRgb(44, 125, 250));
            SetThemeBrush("ToolItemSelectedHoverBackground", Color.FromRgb(65, 142, 255));
            SetThemeBrush("DefaultIconBackground", Color.FromRgb(230, 235, 243));
            SetThemeBrush("DefaultIconForeground", Color.FromRgb(61, 70, 86));
            SetThemeBrush("ContentBackground", Color.FromRgb(250, 251, 253));
            SetThemeBrush("HeaderBackground", Color.FromRgb(241, 244, 248));
            SetThemeBrush("HeaderBorderBrush", Color.FromRgb(215, 222, 232));
            SetThemeBrush("TitleForeground", Color.FromRgb(16, 24, 39));
            SetThemeBrush("UrlForeground", Color.FromRgb(96, 108, 128));
            SetThemeBrush("ResizePreviewBackground", Color.FromRgb(250, 251, 253));
            return;
        }

        SetThemeBrush("HeaderButtonForeground", Color.FromRgb(216, 222, 233));
        SetThemeBrush("HeaderButtonHoverBackground", Color.FromRgb(35, 42, 54));
        SetThemeBrush("HeaderButtonHoverBorder", Color.FromRgb(54, 65, 83));
        SetThemeBrush("HeaderButtonCheckedBackground", Color.FromRgb(44, 125, 250));
        SetThemeBrush("HeaderButtonCheckedHoverBackground", Color.FromRgb(73, 148, 255));
        SetThemeBrush("HeaderButtonCheckedBorder", Color.FromRgb(74, 145, 255));
        SetThemeBrush("RailBackground", Color.FromArgb(51, 255, 255, 255));
        SetThemeBrush("RailBorderBrush", Color.FromArgb(85, 255, 255, 255));
        SetThemeBrush("ToolListForeground", Color.FromRgb(232, 237, 245));
        SetThemeBrush("ToolItemHoverBackground", Color.FromArgb(96, 255, 255, 255));
        SetThemeBrush("ToolItemHoverBorder", Color.FromArgb(140, 255, 255, 255));
        SetThemeBrush("ToolItemSelectedBackground", Color.FromRgb(44, 125, 250));
        SetThemeBrush("ToolItemSelectedHoverBackground", Color.FromRgb(73, 148, 255));
        SetThemeBrush("DefaultIconBackground", Color.FromRgb(34, 42, 54));
        SetThemeBrush("DefaultIconForeground", Color.FromRgb(216, 222, 233));
        SetThemeBrush("ContentBackground", Color.FromRgb(11, 13, 17));
        SetThemeBrush("HeaderBackground", Color.FromRgb(17, 23, 34));
        SetThemeBrush("HeaderBorderBrush", Color.FromRgb(38, 45, 58));
        SetThemeBrush("TitleForeground", Color.FromRgb(244, 247, 251));
        SetThemeBrush("UrlForeground", Color.FromRgb(141, 152, 170));
        SetThemeBrush("ResizePreviewBackground", Color.FromRgb(11, 13, 17));
    }

    private void ApplyBrowserThemes()
    {
        foreach (var browser in _browsers.Values)
        {
            ApplyBrowserTheme(browser);
        }
    }

    private void ApplyBrowserTheme(WebView2 browser)
    {
        if (browser.CoreWebView2 is null)
        {
            return;
        }

        browser.CoreWebView2.Profile.PreferredColorScheme = GetThemeMode() switch
        {
            AppThemeMode.Dark => CoreWebView2PreferredColorScheme.Dark,
            AppThemeMode.Light => CoreWebView2PreferredColorScheme.Light,
            _ => CoreWebView2PreferredColorScheme.Auto
        };
    }

    private void SetThemeBrush(string key, Color color)
    {
        Resources[key] = new SolidColorBrush(color);
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return !Equals(key?.GetValue("AppsUseLightTheme"), 0);
        }
        catch
        {
            return true;
        }
    }

    private void SaveExpandedWidth()
    {
        var width = ClampExpandedWidth(_expandedWidth);
        if (Math.Abs(_settings.DefaultExpandedWidth - width) < 0.5)
        {
            return;
        }

        _settings.DefaultExpandedWidth = width;
        AppSettings.Save(_settings);
    }

    private void OnStartWithWindowsClick(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        AppSettings.Save(_settings);
        ApplyStartupSetting();
        UpdateSettingsMenuChecks();
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryKeyPath);
            if (key is null)
            {
                SetStatus("Could not update startup setting.");
                return;
            }

            if (_settings.StartWithWindows && TryGetStartupCommand(out var command))
            {
                key.SetValue(RunRegistryValueName, command, RegistryValueKind.String);
                return;
            }

            key.DeleteValue(RunRegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not update startup setting: {ex.Message}");
        }
    }

    private static bool TryGetStartupCommand(out string command)
    {
        command = string.Empty;

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                executablePath = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                executablePath = null;
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        command = $"\"{executablePath}\"";
        return true;
    }

    private async void OnAddUrlClick(object sender, RoutedEventArgs e)
    {
        if (!TryPromptForToolDefinition(out var tool))
        {
            return;
        }

        _settings.Tools.Add(tool);
        AppSettings.Save(_settings);

        var item = new ToolItem(tool);
        _toolItems.Add(item);
        await CreateBrowserAsync(item);
        ToolList.SelectedItem = item;
        ShowSelectedTool();
        Expand();
    }

    private void OnRemoveUrlClick(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItem item)
        {
            return;
        }

        if (_toolItems.Count <= 1)
        {
            MessageBox.Show(
                "At least one URL is required.",
                "SideDock",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Remove {item.Tool.Title}?",
            "SideDock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var removedIndex = ToolList.SelectedIndex;
        if (_browsers.Remove(item.Tool.Id, out var browser))
        {
            BrowserHost.Children.Remove(browser);
            browser.Dispose();
        }

        _toolStatuses.Remove(item.Tool.Id);
        _settings.Tools.RemoveAll(tool => tool.Id.Equals(item.Tool.Id, StringComparison.OrdinalIgnoreCase));
        AppSettings.Save(_settings);
        _toolItems.Remove(item);

        ToolList.SelectedIndex = Math.Min(removedIndex, _toolItems.Count - 1);
        ShowSelectedTool();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var version = GetCurrentVersion();

        SetStatus($"Opening SideDock {version} project page...");
        OpenExternal(ProjectHomeUrl);
    }

    private static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
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
        DockToConfiguredEdge(_isExpanded ? _expandedWidth : _settings.CollapsedWidth);
    }

    private void OnCursorTimerTick(object? sender, EventArgs e)
    {
        if (!_isResizing)
        {
            UpdateFullscreenAutoHideState();
        }

        if (_isAutoHiddenForFullscreen)
        {
            return;
        }

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

    private void UpdateFullscreenAutoHideState()
    {
        if (IsAnotherWindowFullscreenOnCurrentMonitor())
        {
            HideForFullscreenApp();
            return;
        }

        RestoreAfterFullscreenApp();
    }

    private void HideForFullscreenApp()
    {
        if (_isAutoHiddenForFullscreen)
        {
            return;
        }

        _cursorLeftAt = null;
        _isAutoHiddenForFullscreen = true;
        Hide();
    }

    private void RestoreAfterFullscreenApp()
    {
        if (!_isAutoHiddenForFullscreen)
        {
            return;
        }

        _isAutoHiddenForFullscreen = false;
        ShowActivated = false;
        Show();
        ApplyTopmostState();

        var currentWidth = _isExpanded ? _expandedWidth : _settings.CollapsedWidth;
        _appBarManager?.Register(GetReservedWidth(currentWidth), currentWidth, GetDockSide());
        DockToConfiguredEdge(currentWidth);
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
        ContentPanel.Visibility = Visibility.Visible;
        ResizeGrip.Visibility = Visibility.Visible;
        ApplyDockSideLayout();
        ApplyTopmostState();
        DockToConfiguredEdge(_expandedWidth);
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
        ApplyDockSideLayout();
        ApplyTopmostState();
        DockToConfiguredEdge(_settings.CollapsedWidth);
    }

    private void OnResizeGripMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isExpanded)
        {
            return;
        }

        _isResizing = true;
        if (GetDockSide() == AppDockSide.Left)
        {
            _resizeContentFixedEdge = GetElementLeftDips(ContentPanel);
            _resizeAnchorEdge = _resizeContentFixedEdge - GetRailWidth();
        }
        else
        {
            _resizeContentFixedEdge = GetElementRightDips(ContentPanel);
            _resizeAnchorEdge = _resizeContentFixedEdge + GetRailWidth();
        }

        _pendingResizeWidth = _expandedWidth;
        ShowResizePreview();
        MoveCurrentBrowserToResizePreview();
        HideContentForResize();
        ResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        var screenX = GetScreenXDips(e.GetPosition(this));
        UpdatePendingResizeWidth(screenX);
        e.Handled = true;
    }

    private void OnResizeGripMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteResize();
        e.Handled = true;
    }

    private void UpdatePendingResizeWidth(double screenXDips)
    {
        var requestedWidth = GetDockSide() == AppDockSide.Left
            ? screenXDips - _resizeAnchorEdge
            : _resizeAnchorEdge - screenXDips;
        _pendingResizeWidth = ClampExpandedWidth(requestedWidth);
        UpdateResizePreview();
    }

    private void CompleteResize()
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        _expandedWidth = _pendingResizeWidth;
        SaveExpandedWidth();
        MoveResizePreviewBrowserBack();
        CloseResizePreview();
        RestoreContentAfterResize();
        if (ResizeGrip.IsMouseCaptured)
        {
            ResizeGrip.ReleaseMouseCapture();
        }

        DockToConfiguredEdge(_expandedWidth);
    }

    private void ShowResizePreview()
    {
        _resizePreviewCanvas = new Canvas
        {
            Background = Brushes.Transparent
        };

        _resizePreviewPanel = new Border
        {
            Background = Resources["ResizePreviewBackground"] as Brush ?? new SolidColorBrush(Color.FromRgb(11, 13, 17)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 44, 125, 250)),
            BorderThickness = new Thickness(1, 0, 1, 0),
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        _resizePreviewCanvas.Children.Add(_resizePreviewPanel);

        _resizePreviewWindow ??= new Window
        {
            Left = 0,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight,
            Top = 0,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(36, 0, 0, 0)),
            Content = _resizePreviewCanvas,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true
        };

        _resizePreviewWindow.MouseMove += OnResizePreviewMouseMove;
        _resizePreviewWindow.MouseLeftButtonUp += OnResizePreviewMouseLeftButtonUp;

        UpdateResizePreview();
        if (!_resizePreviewWindow.IsVisible)
        {
            _resizePreviewWindow.Show();
        }
    }

    private void OnResizePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing || _resizePreviewWindow is null)
        {
            return;
        }

        UpdatePendingResizeWidth(_resizePreviewWindow.Left + e.GetPosition(_resizePreviewWindow).X);
        e.Handled = true;
    }

    private void OnResizePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteResize();
        e.Handled = true;
    }

    private void UpdateResizePreview()
    {
        if (_resizePreviewWindow is null || _resizePreviewPanel is null)
        {
            return;
        }

        var screenBounds = GetPrimaryScreenBoundsDips();
        var gripWidth = GetResizeGripWidth();
        var previewLeft = GetDockSide() == AppDockSide.Left
            ? Math.Round(_resizeContentFixedEdge - screenBounds.Left)
            : Math.Round(_resizeAnchorEdge - _pendingResizeWidth + gripWidth - screenBounds.Left);
        var previewRight = GetDockSide() == AppDockSide.Left
            ? Math.Round(_resizeAnchorEdge + _pendingResizeWidth - gripWidth - screenBounds.Left)
            : Math.Round(_resizeContentFixedEdge - screenBounds.Left);
        var contentPreviewWidth = Math.Max(1, previewRight - previewLeft);

        Canvas.SetLeft(_resizePreviewPanel, previewLeft);
        Canvas.SetTop(_resizePreviewPanel, 0);
        _resizePreviewPanel.Width = contentPreviewWidth;
        _resizePreviewPanel.Height = screenBounds.Height;

        _resizePreviewWindow.Left = screenBounds.Left;
        _resizePreviewWindow.Top = screenBounds.Top;
        _resizePreviewWindow.Width = screenBounds.Width;
        _resizePreviewWindow.Height = screenBounds.Height;
    }

    private void CloseResizePreview()
    {
        if (_resizePreviewWindow is not null)
        {
            _resizePreviewWindow.MouseMove -= OnResizePreviewMouseMove;
            _resizePreviewWindow.MouseLeftButtonUp -= OnResizePreviewMouseLeftButtonUp;
        }

        _resizePreviewWindow?.Close();
        _resizePreviewWindow = null;
        _resizePreviewCanvas = null;
        _resizePreviewPanel = null;
        _resizePreviewBrowser = null;
    }

    private void MoveCurrentBrowserToResizePreview()
    {
        var browser = GetCurrentBrowser();
        if (browser is null || _resizePreviewPanel is null)
        {
            return;
        }

        BrowserHost.Children.Remove(browser);
        browser.Visibility = Visibility.Visible;
        _resizePreviewPanel.Child = browser;
        _resizePreviewBrowser = browser;

        if (_resizePreviewWindow is not null)
        {
            _resizePreviewWindow.Topmost = false;
            _resizePreviewWindow.Topmost = true;
        }
    }

    private void MoveResizePreviewBrowserBack()
    {
        if (_resizePreviewBrowser is null)
        {
            return;
        }

        if (_resizePreviewPanel is not null)
        {
            _resizePreviewPanel.Child = null;
        }

        if (!BrowserHost.Children.Contains(_resizePreviewBrowser))
        {
            BrowserHost.Children.Add(_resizePreviewBrowser);
        }

        foreach (var (toolId, browser) in _browsers)
        {
            browser.Visibility = _currentItem is not null && toolId.Equals(_currentItem.Tool.Id, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Hidden;
        }

        _resizePreviewBrowser = null;
    }

    private void HideContentForResize()
    {
        if (_isContentHiddenForResize)
        {
            return;
        }

        ContentPanel.Visibility = Visibility.Hidden;
        _isContentHiddenForResize = true;
    }

    private void RestoreContentAfterResize()
    {
        if (!_isContentHiddenForResize)
        {
            return;
        }

        ContentPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        _isContentHiddenForResize = false;
    }

    private double GetScreenXDips(Point windowPoint)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return Left + windowPoint.X;
        }

        var screenPixels = PointToScreen(windowPoint);
        var screenDips = source.CompositionTarget.TransformFromDevice.Transform(screenPixels);
        return screenDips.X;
    }

    private double GetElementRightDips(FrameworkElement element)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return Left + element.TranslatePoint(new Point(element.ActualWidth, 0), this).X;
        }

        var rightPixels = element.PointToScreen(new Point(element.ActualWidth, 0));
        var rightDips = source.CompositionTarget.TransformFromDevice.Transform(rightPixels);
        return rightDips.X;
    }

    private double GetElementLeftDips(FrameworkElement element)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return Left + element.TranslatePoint(new Point(0, 0), this).X;
        }

        var leftPixels = element.PointToScreen(new Point(0, 0));
        var leftDips = source.CompositionTarget.TransformFromDevice.Transform(leftPixels);
        return leftDips.X;
    }

    private double GetRailWidth()
    {
        return RailBorder.ActualWidth > 0
            ? RailBorder.ActualWidth
            : _settings.CollapsedWidth;
    }

    private double GetResizeGripWidth()
    {
        return ResizeGrip.ActualWidth > 0
            ? ResizeGrip.ActualWidth
            : 18;
    }

    private Rect GetPrimaryScreenBoundsDips()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        var topLeft = source.CompositionTarget.TransformFromDevice.Transform(new Point(0, 0));
        var bottomRight = source.CompositionTarget.TransformFromDevice.Transform(
            new Point(GetSystemMetrics(SystemMetricCxScreen), GetSystemMetrics(SystemMetricCyScreen)));

        return new Rect(topLeft, bottomRight);
    }

    private void DockToConfiguredEdge(double width)
    {
        var clampedWidth = _isExpanded
            ? ClampExpandedWidth(width)
            : _settings.CollapsedWidth;
        Width = clampedWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        if (_isAutoHiddenForFullscreen)
        {
            return;
        }

        if (_appBarManager is not null)
        {
            _appBarManager.Apply(GetReservedWidth(clampedWidth), clampedWidth, GetDockSide());
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = GetDockSide() == AppDockSide.Left
            ? workArea.Left
            : workArea.Right - clampedWidth;
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

    private bool TryPromptForToolDefinition(out ToolDefinition tool)
    {
        tool = null!;
        ToolDefinition? createdTool = null;

        var titleBox = new TextBox
        {
            MinWidth = 280,
            Margin = new Thickness(0, 4, 0, 12)
        };
        var urlBox = new TextBox
        {
            MinWidth = 280,
            Margin = new Thickness(0, 4, 0, 8)
        };
        var errorText = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var addButton = new Button
        {
            Content = "Add",
            IsDefault = true,
            MinWidth = 78,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 78
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(addButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel
        {
            Margin = new Thickness(18)
        };
        content.Children.Add(new TextBlock { Text = "Title" });
        content.Children.Add(titleBox);
        content.Children.Add(new TextBlock { Text = "URL" });
        content.Children.Add(urlBox);
        content.Children.Add(errorText);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Add URL",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = content
        };

        addButton.Click += (_, _) =>
        {
            if (!TryCreateToolDefinition(titleBox.Text, urlBox.Text, out var candidate, out var error))
            {
                errorText.Text = error;
                return;
            }

            createdTool = candidate;
            dialog.DialogResult = true;
        };

        if (dialog.ShowDialog() == true && createdTool is not null)
        {
            tool = createdTool;
            return true;
        }

        return false;
    }

    private bool TryCreateToolDefinition(string title, string url, out ToolDefinition tool, out string error)
    {
        tool = null!;
        error = string.Empty;

        if (!TryNormalizeUrl(url, out var uri, out error))
        {
            return false;
        }

        var displayTitle = string.IsNullOrWhiteSpace(title)
            ? uri.Host
            : title.Trim();
        tool = new ToolDefinition(
            CreateUniqueToolId(displayTitle, uri),
            displayTitle,
            uri.AbsoluteUri,
            CreateIconKey(displayTitle),
            true);
        return true;
    }

    private static bool TryNormalizeUrl(string input, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;

        var candidate = input.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "URL is required.";
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter a valid http or https URL.";
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private string CreateUniqueToolId(string title, Uri uri)
    {
        var baseId = ToSafeId(title);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = ToSafeId(uri.Host);
        }

        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "tool";
        }

        var existingIds = _settings.Tools
            .Select(tool => tool.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = baseId;
        var suffix = 2;
        while (existingIds.Contains(id))
        {
            id = $"{baseId}-{suffix}";
            suffix++;
        }

        return id;
    }

    private static string ToSafeId(string value)
    {
        var chars = new List<char>();
        var previousSeparator = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                chars.Add(ch);
                previousSeparator = false;
                continue;
            }

            if (!previousSeparator)
            {
                chars.Add('-');
                previousSeparator = true;
            }
        }

        return new string(chars.ToArray()).Trim('-');
    }

    private static string CreateIconKey(string title)
    {
        var key = new string(title
            .Where(char.IsLetterOrDigit)
            .Take(2)
            .Select(char.ToUpperInvariant)
            .ToArray());

        return string.IsNullOrWhiteSpace(key) ? "UR" : key;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool TryGetToolIconHit(MouseButtonEventArgs e, out ToolItem? item)
    {
        item = null;

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not ToolItem toolItem)
        {
            return false;
        }

        var pointer = e.GetPosition(container);
        var iconBounds = new Rect(
            (container.ActualWidth - DisplayIconSize) / 2,
            (container.ActualHeight - DisplayIconSize) / 2,
            DisplayIconSize,
            DisplayIconSize);

        if (!iconBounds.Contains(pointer))
        {
            return false;
        }

        item = toolItem;
        return true;
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

    private bool IsAnotherWindowFullscreenOnCurrentMonitor()
    {
        var ownerHandle = _hwndSource?.Handle ?? IntPtr.Zero;
        if (ownerHandle == IntPtr.Zero)
        {
            return false;
        }

        var foregroundWindow = GetAncestor(GetForegroundWindow(), GaRoot);
        if (foregroundWindow == IntPtr.Zero
            || IsSideDockWindow(foregroundWindow)
            || IsShellWindow(foregroundWindow)
            || !IsWindowVisible(foregroundWindow)
            || IsIconic(foregroundWindow))
        {
            return false;
        }

        var sideDockMonitor = MonitorFromWindow(ownerHandle, MonitorDefaultToNearest);
        var foregroundMonitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToNull);
        if (sideDockMonitor == IntPtr.Zero
            || foregroundMonitor == IntPtr.Zero
            || sideDockMonitor != foregroundMonitor)
        {
            return false;
        }

        if (!TryGetWindowFrameRect(foregroundWindow, out var windowRect))
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            cbSize = Marshal.SizeOf<MonitorInfo>()
        };

        return GetMonitorInfo(foregroundMonitor, ref monitorInfo)
            && CoversMonitor(windowRect, monitorInfo.rcMonitor)
            && !IsScreenshotCaptureWindow(foregroundWindow);
    }

    private static bool IsScreenshotCaptureWindow(IntPtr hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out var processId) != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (ScreenshotProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return ContainsScreenshotKeyword(GetWindowClassName(hwnd))
            || ContainsScreenshotKeyword(GetWindowTitle(hwnd));
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        if (hwnd == GetShellWindow())
        {
            return true;
        }

        var className = GetWindowClassName(hwnd);
        return ShellWindowClassNames.Contains(className, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsScreenshotKeyword(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && ScreenshotWindowKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(MaxWindowTextLength);
        return GetClassName(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var builder = new StringBuilder(MaxWindowTextLength);
        return GetWindowText(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private bool IsSideDockWindow(IntPtr hwnd)
    {
        if (hwnd == (_hwndSource?.Handle ?? IntPtr.Zero))
        {
            return true;
        }

        return _resizePreviewWindow is not null
            && hwnd == new WindowInteropHelper(_resizePreviewWindow).Handle;
    }

    private static bool TryGetWindowFrameRect(IntPtr hwnd, out NativeRect rect)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<NativeRect>()) == 0
            && rect.Right > rect.Left
            && rect.Bottom > rect.Top)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static bool CoversMonitor(NativeRect windowRect, NativeRect monitorRect)
    {
        return windowRect.Left <= monitorRect.Left + FullscreenEdgeTolerancePixels
            && windowRect.Top <= monitorRect.Top + FullscreenEdgeTolerancePixels
            && windowRect.Right >= monitorRect.Right - FullscreenEdgeTolerancePixels
            && windowRect.Bottom >= monitorRect.Bottom - FullscreenEdgeTolerancePixels;
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
            else if (wParam.ToInt32() == AbnFullscreenApp)
            {
                Dispatcher.BeginInvoke(UpdateFullscreenAutoHideState);
            }

            handled = true;
            return IntPtr.Zero;
        }

        if (msg is WmDisplayChange or WmSettingChange or WmDpiChanged)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ApplyTheme();
                ApplyBrowserThemes();
                DockToConfiguredEdge(_isExpanded ? _expandedWidth : _settings.CollapsedWidth);
            });
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

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record IconCandidate(string Href, string Rel, string Sizes);
}
