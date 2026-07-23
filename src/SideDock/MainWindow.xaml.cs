using Microsoft.Extensions.Logging;
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
    private const int MaxCachedIconDimension = 256;
    private const int MaxIconSourceDimension = 1024;
    private const long MaxIconSourcePixels = 1024 * 1024;
    private const int MaxIconDownloadBytes = 2 * 1024 * 1024;
    private const int GaRoot = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int MaxWindowTextLength = 512;
    private const string ProjectHomeUrl = "https://github.com/litefeel/SideDock";
    private const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryValueName = "SideDock";

    private static readonly HttpClient IconHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly SemaphoreSlim IconDownloadGate = new(3, 3);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    private readonly AppSettings _settings;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherTimer _cursorTimer;
    private readonly ObservableCollection<ToolItem> _toolItems;
    private readonly Dictionary<string, WebView2> _browsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<WebView2?>> _browserCreationTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _browserCreationCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _iconCacheLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _iconRefreshCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CoreWebView2DevToolsProtocolEventReceiver>> _networkEventReceivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly FailedDomainStore _failedDomainStore;
    private readonly FailedDomainNotificationService _failedDomainNotificationService;
    private DateTime? _cursorLeftAt;
    private ToolItem? _currentItem;
    private CoreWebView2Environment? _webViewEnvironment;
    private Task<CoreWebView2Environment>? _webViewEnvironmentTask;
    private double _expandedWidth;
    private bool _isExpanded;
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
    private bool _isAutoHiddenForFullscreen;
    private bool _isClosing;
    private HwndSource? _hwndSource;
    private AppBarManager? _appBarManager;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _logger = AppLogging.CreateLogger<MainWindow>();
        _failedDomainStore = new FailedDomainStore(AppSettings.FailedDomainsPath, _logger);

        InitializeComponent();
        _failedDomainNotificationService = new FailedDomainNotificationService(Dispatcher, OpenFailedDomainsFile, _logger);

        ApplyTheme();
        ApplyStartupSetting();
        _expandedWidth = ClampExpandedWidth(_settings.DefaultExpandedWidth);
        ContentPanel.Visibility = Visibility.Collapsed;
        ResizeGrip.Visibility = Visibility.Collapsed;
        ContentColumn.Width = new GridLength(0);
        ApplyDockSideLayout();
        Width = GetCurrentDockWidth();
        Height = GetDockWindowHeight();
        MinWidth = _settings.CollapsedWidth;
        MinHeight = _settings.CollapsedWidth;
        Topmost = true;

        _toolItems = new ObservableCollection<ToolItem>(_settings.Tools.Select(tool => new ToolItem(tool)));
        LoadCachedIcons();
        ToolList.ItemsSource = _toolItems;
        ToolList.SelectedIndex = -1;
        _logger.LogInformation(
            "Main window initialized. DockSide={DockSide} ThemeMode={ThemeMode} ToolCount={ToolCount} Tools={@Tools}",
            _settings.DockSide,
            _settings.ThemeMode,
            _settings.Tools.Count,
            _settings.Tools.Select(tool => new
            {
                tool.Id,
                tool.Title,
                tool.Url
            }).ToArray());

        _cursorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _cursorTimer.Tick += OnCursorTimerTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Main window loaded.");
        DockToConfiguredEdge(GetCurrentDockWidth(), "Loaded");
        _cursorTimer.Start();
        _logger.LogInformation("WebView2 initialization deferred until a tool is activated.");
        await RefreshMissingIconsAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        _logger.LogInformation("Window source initialized. Hwnd=0x{Hwnd:X}", handle);
        _appBarManager = new AppBarManager(handle, AppLogging.CreateLogger<AppBarManager>());
        var currentWidth = GetCurrentDockWidth();
        _appBarManager.Register(GetReservedWidth(currentWidth), currentWidth, GetDockWindowHeight(), GetDockSide(), "SourceInitialized");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _logger.LogInformation("Main window closing.");
        _isClosing = true;
        _failedDomainNotificationService.Dispose();
        _lifetimeCancellation.Cancel();
        foreach (var cancellation in _browserCreationCancellations.Values.ToArray())
        {
            cancellation.Cancel();
        }

        foreach (var cancellation in _iconRefreshCancellations.Values.ToArray())
        {
            cancellation.Cancel();
        }

        _cursorTimer.Stop();
        _hwndSource?.RemoveHook(WndProc);
        _appBarManager?.Unregister("Closing");
        MoveResizePreviewBrowserBack();
        CloseResizePreview();

        foreach (var browser in _browsers.Values)
        {
            browser.Dispose();
        }

        _browsers.Clear();
    }

    private async Task<CoreWebView2Environment> EnsureWebViewEnvironmentAsync()
    {
        if (_webViewEnvironment is not null)
        {
            return _webViewEnvironment;
        }

        var environmentTask = _webViewEnvironmentTask;
        if (environmentTask is null)
        {
            environmentTask = CreateWebViewEnvironmentAsync();
            _webViewEnvironmentTask = environmentTask;
        }

        try
        {
            return await environmentTask;
        }
        catch
        {
            if (ReferenceEquals(_webViewEnvironmentTask, environmentTask))
            {
                _webViewEnvironmentTask = null;
            }

            throw;
        }
    }

    private async Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync()
    {
        if (_isClosing)
        {
            throw new OperationCanceledException();
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SideDock",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);
        _logger.LogInformation("Initializing WebView2 environment. UserDataFolder={UserDataFolder}", userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        if (_isClosing)
        {
            throw new OperationCanceledException();
        }

        _webViewEnvironment = environment;
        _logger.LogInformation("WebView2 environment initialized.");
        return environment;
    }

    private async Task<WebView2?> EnsureBrowserAsync(ToolItem item)
    {
        if (_browsers.TryGetValue(item.Tool.Id, out var existingBrowser))
        {
            return existingBrowser;
        }

        if (_browserCreationTasks.TryGetValue(item.Tool.Id, out var existingTask))
        {
            return await existingTask;
        }

        var cancellation = new CancellationTokenSource();
        var creationTask = CreateBrowserAsync(item, cancellation.Token);
        _browserCreationTasks[item.Tool.Id] = creationTask;
        _browserCreationCancellations[item.Tool.Id] = cancellation;

        try
        {
            return await creationTask;
        }
        finally
        {
            if (_browserCreationTasks.TryGetValue(item.Tool.Id, out var registeredTask)
                && ReferenceEquals(registeredTask, creationTask))
            {
                _browserCreationTasks.Remove(item.Tool.Id);
            }

            if (_browserCreationCancellations.TryGetValue(item.Tool.Id, out var registeredCancellation)
                && ReferenceEquals(registeredCancellation, cancellation))
            {
                _browserCreationCancellations.Remove(item.Tool.Id);
            }

            cancellation.Dispose();
        }
    }

    private async Task<WebView2?> CreateBrowserAsync(ToolItem item, CancellationToken cancellationToken)
    {
        WebView2? browser = null;

        try
        {
            var environment = await EnsureWebViewEnvironmentAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (_isClosing || !_toolItems.Contains(item))
            {
                return null;
            }

            if (_browsers.TryGetValue(item.Tool.Id, out var existingBrowser))
            {
                return existingBrowser;
            }

            _logger.LogInformation(
                "Creating WebView2 browser. ToolId={ToolId} ToolTitle={ToolTitle} ToolUrl={ToolUrl}",
                item.Tool.Id,
                item.Tool.Title,
                item.Tool.Url);
            browser = new WebView2
            {
                Visibility = Visibility.Hidden,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _browsers[item.Tool.Id] = browser;
            _toolStatuses[item.Tool.Id] = "Loading...";
            BrowserHost.Children.Add(browser);

            await browser.EnsureCoreWebView2Async(environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (_isClosing || !_toolItems.Contains(item))
            {
                RemoveAndDisposeBrowser(item.Tool.Id, browser);
                return null;
            }

            _logger.LogInformation("WebView2 browser ready. ToolId={ToolId}", item.Tool.Id);
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
            ApplyBrowserTheme(browser);
            await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ExternalBlankLinkScript);
            cancellationToken.ThrowIfCancellationRequested();
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

            await ConfigureNetworkFailureTrackingAsync(item.Tool, browser.CoreWebView2);
            cancellationToken.ThrowIfCancellationRequested();

            browser.CoreWebView2.Navigate(item.Tool.Url);
            _logger.LogInformation("WebView2 initial navigation requested. ToolId={ToolId} ToolUrl={ToolUrl}", item.Tool.Id, item.Tool.Url);
            UpdateBrowserPresentation();

            if (IsCurrentTool(item.Tool))
            {
                ShowSelectedTool();
            }

            return browser;
        }
        catch
        {
            if (browser is not null)
            {
                RemoveAndDisposeBrowser(item.Tool.Id, browser);
            }

            throw;
        }
    }

    private void CancelBrowserCreation(string toolId)
    {
        if (_browserCreationCancellations.Remove(toolId, out var cancellation))
        {
            cancellation.Cancel();
        }

        _browserCreationTasks.Remove(toolId);
    }

    private void RemoveAndDisposeBrowser(string toolId, WebView2 browser)
    {
        if (_browsers.TryGetValue(toolId, out var registeredBrowser)
            && ReferenceEquals(registeredBrowser, browser))
        {
            _browsers.Remove(toolId);
        }

        BrowserHost.Children.Remove(browser);
        if (ReferenceEquals(_resizePreviewBrowser, browser))
        {
            if (_resizePreviewPanel is not null)
            {
                _resizePreviewPanel.Child = null;
            }

            _resizePreviewBrowser = null;
        }

        _networkEventReceivers.Remove(toolId);

        browser.Dispose();
    }

    private async Task ConfigureNetworkFailureTrackingAsync(ToolDefinition tool, CoreWebView2 coreWebView)
    {
        var requestUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        var requestReceiver = coreWebView.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        var finishedReceiver = coreWebView.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        var failedReceiver = coreWebView.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
        var webSocketCreatedReceiver = coreWebView.GetDevToolsProtocolEventReceiver("Network.webSocketCreated");
        var webSocketFrameErrorReceiver = coreWebView.GetDevToolsProtocolEventReceiver("Network.webSocketFrameError");
        var webSocketClosedReceiver = coreWebView.GetDevToolsProtocolEventReceiver("Network.webSocketClosed");

        requestReceiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
                var root = document.RootElement;
                if (root.TryGetProperty("requestId", out var requestIdElement)
                    && root.TryGetProperty("request", out var requestElement)
                    && requestElement.TryGetProperty("url", out var urlElement))
                {
                    var requestId = requestIdElement.GetString();
                    var url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(url))
                    {
                        requestUrls[requestId] = url;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Could not parse WebView2 request event. ToolId={ToolId}", tool.Id);
            }
        };

        finishedReceiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            if (TryGetDevToolsString(args.ParameterObjectAsJson, "requestId", out var requestId))
            {
                requestUrls.Remove(requestId);
            }
        };

        failedReceiver.DevToolsProtocolEventReceived += (_, args) =>
            OnNetworkLoadingFailed(tool, requestUrls, args.ParameterObjectAsJson);

        webSocketCreatedReceiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            try
            {
                using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
                var root = document.RootElement;
                if (root.TryGetProperty("requestId", out var requestIdElement)
                    && root.TryGetProperty("url", out var urlElement))
                {
                    var requestId = requestIdElement.GetString();
                    var url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(url))
                    {
                        requestUrls[requestId] = url;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Could not parse WebView2 WebSocket creation event. ToolId={ToolId}", tool.Id);
            }
        };

        webSocketFrameErrorReceiver.DevToolsProtocolEventReceived += (_, args) =>
            OnWebSocketFrameError(tool, requestUrls, args.ParameterObjectAsJson);

        webSocketClosedReceiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            if (TryGetDevToolsString(args.ParameterObjectAsJson, "requestId", out var requestId))
            {
                requestUrls.Remove(requestId);
            }
        };

        _networkEventReceivers[tool.Id] =
        [
            requestReceiver,
            finishedReceiver,
            failedReceiver,
            webSocketCreatedReceiver,
            webSocketFrameErrorReceiver,
            webSocketClosedReceiver
        ];
        await coreWebView.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        _logger.LogDebug("WebView2 network failure tracking enabled. ToolId={ToolId}", tool.Id);
    }

    private void OnNetworkLoadingFailed(ToolDefinition tool, Dictionary<string, string> requestUrls, string eventJson)
    {
        try
        {
            using var document = JsonDocument.Parse(eventJson);
            var root = document.RootElement;
            var requestId = root.TryGetProperty("requestId", out var requestIdElement) ? requestIdElement.GetString() : null;
            var errorText = root.TryGetProperty("errorText", out var errorElement) ? errorElement.GetString() : null;
            var canceled = root.TryGetProperty("canceled", out var canceledElement) && canceledElement.ValueKind == JsonValueKind.True;
            var blockedReason = root.TryGetProperty("blockedReason", out var blockedElement) ? blockedElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(requestId) || !requestUrls.Remove(requestId, out var url))
            {
                return;
            }

            if (!FailedDomainStore.IsConnectionFailure(errorText, canceled, blockedReason))
            {
                return;
            }

            RecordNetworkFailure(tool, url, errorText);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse WebView2 loading failure event. ToolId={ToolId}", tool.Id);
        }
    }

    private void OnWebSocketFrameError(ToolDefinition tool, Dictionary<string, string> requestUrls, string eventJson)
    {
        try
        {
            using var document = JsonDocument.Parse(eventJson);
            var root = document.RootElement;
            var requestId = root.TryGetProperty("requestId", out var requestIdElement) ? requestIdElement.GetString() : null;
            var errorMessage = root.TryGetProperty("errorMessage", out var errorElement) ? errorElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(requestId)
                || !FailedDomainStore.IsConnectionFailure(errorMessage, canceled: false)
                || !requestUrls.Remove(requestId, out var url))
            {
                return;
            }

            RecordNetworkFailure(tool, url, errorMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse WebView2 WebSocket error event. ToolId={ToolId}", tool.Id);
        }
    }

    private void RecordNetworkFailure(ToolDefinition tool, string url, string? errorText)
    {
        if (!FailedDomainStore.TryNormalizeEndpoint(url, out var endpoint))
        {
            return;
        }

        var recordResult = _failedDomainStore.Record(endpoint);
        _logger.LogWarning(
            "WebView2 network request failed. ToolId={ToolId} Endpoint={Endpoint} Url={Url} Error={NetworkError} FailureCount={FailureCount}",
            tool.Id,
            endpoint,
            url,
            errorText,
            recordResult.FailureCount);

        if (recordResult.IsNew)
        {
            _failedDomainNotificationService.ShowNewDomain(endpoint);
        }
    }

    private static bool TryGetDevToolsString(string eventJson, string propertyName, out string value)
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(eventJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var element))
            {
                return false;
            }

            value = element.GetString() ?? string.Empty;
            return value.Length > 0;
        }
        catch (JsonException)
        {
            return false;
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

    private async Task RefreshMissingIconsAsync()
    {
        var refreshTasks = _toolItems
            .Where(item => item.Icon is null)
            .Select(RefreshMissingIconAsync)
            .ToArray();

        if (refreshTasks.Length == 0)
        {
            _logger.LogDebug("All tool icons loaded from cache.");
            return;
        }

        _logger.LogInformation("Refreshing missing tool icons. MissingIconCount={MissingIconCount}", refreshTasks.Length);
        await Task.WhenAll(refreshTasks);
    }

    private async Task RefreshMissingIconAsync(ToolItem item)
    {
        if (item.Icon is not null || !TryGetRootFaviconUri(item.Tool, out var faviconUri))
        {
            return;
        }

        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        if (_iconRefreshCancellations.Remove(item.Tool.Id, out var previousCancellation))
        {
            previousCancellation.Cancel();
        }

        _iconRefreshCancellations[item.Tool.Id] = refreshCancellation;
        var gateEntered = false;
        try
        {
            await IconDownloadGate.WaitAsync(refreshCancellation.Token);
            gateEntered = true;
            if (item.Icon is not null || _isClosing || !_toolItems.Contains(item))
            {
                return;
            }

            var cached = await TryDownloadAndCacheIconAsync(
                item,
                faviconUri.AbsoluteUri,
                refreshCancellation.Token);
            _logger.LogDebug(
                "Missing tool icon refresh completed. ToolId={ToolId} IsCached={IsCached}",
                item.Tool.Id,
                cached);
        }
        catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
        {
        }
        catch (OutOfMemoryException ex)
        {
            _logger.LogWarning(ex, "Rejected tool icon because decoding exhausted memory. ToolId={ToolId}", item.Tool.Id);
        }
        finally
        {
            if (gateEntered)
            {
                IconDownloadGate.Release();
            }

            if (_iconRefreshCancellations.TryGetValue(item.Tool.Id, out var registeredCancellation)
                && ReferenceEquals(registeredCancellation, refreshCancellation))
            {
                _iconRefreshCancellations.Remove(item.Tool.Id);
            }

            refreshCancellation.Dispose();
        }
    }

    private void CancelIconRefresh(string toolId)
    {
        if (_iconRefreshCancellations.Remove(toolId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    private static bool TryGetRootFaviconUri(ToolDefinition tool, out Uri faviconUri)
    {
        faviconUri = null!;
        if (!Uri.TryCreate(tool.Url, UriKind.Absolute, out var pageUri)
            || (pageUri.Scheme != Uri.UriSchemeHttp && pageUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        faviconUri = new Uri(pageUri, "/favicon.ico");
        return true;
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
                if (await TryDownloadAndCacheIconAsync(item, candidate.Href, _lifetimeCancellation.Token))
                {
                    return;
                }
            }

            await CacheFallbackFaviconAsync(item, browser);
        }
        catch (OperationCanceledException) when (_isClosing)
        {
        }
        catch (OutOfMemoryException ex)
        {
            _logger.LogWarning(ex, "Rejected page icon because decoding exhausted memory. ToolId={ToolId}", item.Tool.Id);
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
            await CacheIconStreamAsync(item, faviconStream, _lifetimeCancellation.Token);
        }
        catch (OutOfMemoryException ex)
        {
            _logger.LogWarning(ex, "Rejected WebView2 favicon because decoding exhausted memory. ToolId={ToolId}", item.Tool.Id);
        }
        catch
        {
            // Keep the default icon when a site does not expose a usable favicon.
        }
    }

    private async Task<bool> TryDownloadAndCacheIconAsync(
        ToolItem item,
        string href,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await IconHttpClient.GetAsync(
                href,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode
                || !IsSupportedContentType(response.Content.Headers.ContentType?.MediaType)
                || response.Content.Headers.ContentLength > MaxIconDownloadBytes)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await CacheIconStreamAsync(item, stream, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CacheIconStreamAsync(
        ToolItem item,
        Stream iconStream,
        CancellationToken cancellationToken)
    {
        var cacheLock = GetIconCacheLock(item.Tool.Id);
        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_isClosing || !_toolItems.Contains(item))
            {
                return false;
            }

            var cachePath = GetIconCachePath(item.Tool);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            await using var memoryStream = new MemoryStream();
            if (!await CopyIconStreamAsync(iconStream, memoryStream, cancellationToken))
            {
                return false;
            }

            memoryStream.Position = 0;
            if (!TrySelectIconFrame(memoryStream, out var frame))
            {
                return false;
            }

            frame = ScaleIconFrameForCache(frame);

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
        finally
        {
            cacheLock.Release();
        }
    }

    private SemaphoreSlim GetIconCacheLock(string toolId)
    {
        if (!_iconCacheLocks.TryGetValue(toolId, out var cacheLock))
        {
            cacheLock = new SemaphoreSlim(1, 1);
            _iconCacheLocks[toolId] = cacheLock;
        }

        return cacheLock;
    }

    private static async Task<bool> CopyIconStreamAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
            {
                return true;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaxIconDownloadBytes)
            {
                return false;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private static bool TrySelectIconFrame(Stream iconStream, out BitmapSource frame)
    {
        frame = null!;

        try
        {
            var decoder = BitmapDecoder.Create(
                iconStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);

            var selectedFrame = decoder.Frames
                .Where(IsSafeIconFrame)
                .Where(candidate => IsPreferredIconSize(candidate.PixelWidth, candidate.PixelHeight))
                .OrderBy(candidate => Math.Max(candidate.PixelWidth, candidate.PixelHeight))
                .ThenBy(candidate => Math.Min(candidate.PixelWidth, candidate.PixelHeight))
                .FirstOrDefault()
                ?? decoder.Frames
                    .Where(IsSafeIconFrame)
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
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeIconFrame(BitmapSource frame)
    {
        return frame.PixelWidth > 0
            && frame.PixelHeight > 0
            && frame.PixelWidth <= MaxIconSourceDimension
            && frame.PixelHeight <= MaxIconSourceDimension
            && (long)frame.PixelWidth * frame.PixelHeight <= MaxIconSourcePixels;
    }

    private static BitmapSource ScaleIconFrameForCache(BitmapSource frame)
    {
        var largestDimension = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (largestDimension <= MaxCachedIconDimension)
        {
            return frame;
        }

        var scale = (double)MaxCachedIconDimension / largestDimension;
        var scaledFrame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        scaledFrame.Freeze();
        return scaledFrame;
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
                BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault();

            if (frame is null || !IsSafeIconFrame(frame))
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
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || !IsSafeIconFrame(frame))
            {
                return false;
            }

            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth > PreferredIconFrameSize)
            {
                bitmap.DecodePixelWidth = PreferredIconFrameSize;
            }

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

    private void OnToolListMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null)
        {
            e.Handled = true;
        }
    }

    private async void OnToolListMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetToolIconHit(e, out var item) || item is null)
        {
            e.Handled = true;
            return;
        }

        await ActivateToolAsync(item);
        e.Handled = true;
    }

    private async Task ActivateToolAsync(ToolItem item)
    {
        if (_isClosing || !_toolItems.Contains(item))
        {
            return;
        }

        ToolList.SelectedItem = item;
        _currentItem = item;
        if (!_browsers.ContainsKey(item.Tool.Id))
        {
            _toolStatuses[item.Tool.Id] = "Starting WebView2...";
        }

        ShowSelectedTool();
        Expand();

        try
        {
            await EnsureBrowserAsync(item);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebView2 initialization failed. ToolId={ToolId}", item.Tool.Id);
            _toolStatuses[item.Tool.Id] = "WebView2 could not start.";
            if (!IsCurrentTool(item.Tool) || _isClosing)
            {
                return;
            }

            var runtimeHint = ex.GetType().Name.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
                ? "WebView2 Runtime is missing or unavailable. Install Microsoft Edge WebView2 Runtime, then try again."
                : "WebView2 could not start.";
            UrlText.Text = runtimeHint;
            MessageBox.Show(
                $"{runtimeHint}\n\n{ex.Message}",
                "SideDock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
        _logger.LogDebug("Selected tool shown. ToolId={ToolId}", item.Tool.Id);

        UpdateBrowserPresentation();

        SetStatus(_toolStatuses.TryGetValue(item.Tool.Id, out var status) ? status : "Waiting for WebView2...");
        UpdateNavigationState();
    }

    private void ClearToolSelection()
    {
        ToolList.SelectedIndex = -1;
        _currentItem = null;
        TitleText.Text = "SideDock";
        UrlText.Text = string.Empty;
        UpdateBrowserPresentation();
    }

    private void UpdateBrowserPresentation()
    {
        foreach (var (toolId, browser) in _browsers)
        {
            if (!ReferenceEquals(browser, _resizePreviewBrowser))
            {
                browser.Visibility = _currentItem is not null
                    && toolId.Equals(_currentItem.Tool.Id, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Hidden;
            }
        }

        ApplyBrowserMemoryTargets();
    }

    private void ApplyBrowserMemoryTargets()
    {
        foreach (var (toolId, browser) in _browsers)
        {
            if (browser.CoreWebView2 is null)
            {
                continue;
            }

            var isResizePreview = ReferenceEquals(browser, _resizePreviewBrowser);
            var isContentVisible = !_isContentHiddenForResize || isResizePreview;
            var isActive = !_isClosing
                && !_isAutoHiddenForFullscreen
                && _isExpanded
                && isContentVisible
                && _currentItem is not null
                && toolId.Equals(_currentItem.Tool.Id, StringComparison.OrdinalIgnoreCase);
            var target = isActive
                ? CoreWebView2MemoryUsageTargetLevel.Normal
                : CoreWebView2MemoryUsageTargetLevel.Low;

            try
            {
                if (browser.CoreWebView2.MemoryUsageTargetLevel != target)
                {
                    browser.CoreWebView2.MemoryUsageTargetLevel = target;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not update WebView2 memory target. ToolId={ToolId} Target={Target}",
                    toolId,
                    target);
            }
        }
    }

    private void OnBrowserNavigationStarting(ToolDefinition tool)
    {
        _logger.LogDebug("WebView2 navigation starting. ToolId={ToolId}", tool.Id);
        _toolStatuses[tool.Id] = "Loading...";
        if (IsCurrentTool(tool))
        {
            SetStatus("Loading...");
        }
    }

    private async Task OnBrowserNavigationCompletedAsync(ToolItem item, WebView2 browser, CoreWebView2NavigationCompletedEventArgs e)
    {
        _toolStatuses[item.Tool.Id] = e.IsSuccess ? "Ready" : $"Load failed: {e.WebErrorStatus}";
        _logger.LogInformation(
            "WebView2 navigation completed. ToolId={ToolId} IsSuccess={IsSuccess} WebErrorStatus={WebErrorStatus}",
            item.Tool.Id,
            e.IsSuccess,
            e.WebErrorStatus);

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
        _logger.LogInformation("Theme mode changed. ThemeMode={ThemeMode}", _settings.ThemeMode);
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
        _logger.LogInformation("Dock side changed. DockSide={DockSide}", _settings.DockSide);
        ApplyDockSideLayout();
        DockToConfiguredEdge(GetCurrentDockWidth(), "DockSideChanged");
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

        ExpandedRailPanel.Visibility = Visibility.Visible;
        RailBorder.CornerRadius = new CornerRadius(0);

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
        _logger.LogInformation("Start with Windows setting changed. StartWithWindows={StartWithWindows}", _settings.StartWithWindows);
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
                _logger.LogWarning("Could not open startup registry key.");
                SetStatus("Could not update startup setting.");
                return;
            }

            if (_settings.StartWithWindows && TryGetStartupCommand(out var command))
            {
                key.SetValue(RunRegistryValueName, command, RegistryValueKind.String);
                _logger.LogInformation("Startup registry value set. ValueName={ValueName}", RunRegistryValueName);
                return;
            }

            key.DeleteValue(RunRegistryValueName, throwOnMissingValue: false);
            _logger.LogInformation("Startup registry value removed. ValueName={ValueName}", RunRegistryValueName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update startup setting.");
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
        _logger.LogInformation(
            "Tool added. ToolId={ToolId} ToolTitle={ToolTitle} ToolUrl={ToolUrl}",
            tool.Id,
            tool.Title,
            tool.Url);

        var item = new ToolItem(tool);
        _toolItems.Add(item);
        await RefreshMissingIconAsync(item);
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

        CancelBrowserCreation(item.Tool.Id);
        CancelIconRefresh(item.Tool.Id);
        if (_browsers.TryGetValue(item.Tool.Id, out var browser))
        {
            RemoveAndDisposeBrowser(item.Tool.Id, browser);
        }

        _toolStatuses.Remove(item.Tool.Id);
        _settings.Tools.RemoveAll(tool => tool.Id.Equals(item.Tool.Id, StringComparison.OrdinalIgnoreCase));
        AppSettings.Save(_settings);
        _logger.LogInformation(
            "Tool removed. ToolId={ToolId} ToolTitle={ToolTitle} ToolUrl={ToolUrl}",
            item.Tool.Id,
            item.Tool.Title,
            item.Tool.Url);
        _toolItems.Remove(item);
        ClearToolSelection();
        Collapse(force: true, reason: "ToolRemoved");
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
        _logger.LogInformation("Exit requested from settings menu.");
        Application.Current.Shutdown();
    }

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogDirectory);
            Process.Start(new ProcessStartInfo(AppSettings.LogDirectory)
            {
                UseShellExecute = true
            });
            _logger.LogInformation("Opened logs folder. LogDirectory={LogDirectory}", AppSettings.LogDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open logs folder. LogDirectory={LogDirectory}", AppSettings.LogDirectory);
            SetStatus($"Could not open logs folder: {ex.Message}");
        }
    }

    private void OnOpenFailedDomainsFileClick(object sender, RoutedEventArgs e)
    {
        OpenFailedDomainsFile();
    }

    private void OpenFailedDomainsFile()
    {
        try
        {
            if (!_failedDomainStore.EnsureFileExists())
            {
                SetStatus("Could not create the failed domains file. See logs for details.");
                return;
            }
            Process.Start(new ProcessStartInfo(_failedDomainStore.Path)
            {
                UseShellExecute = true
            });
            _logger.LogInformation("Opened failed domains file. Path={FailedDomainsPath}", _failedDomainStore.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open failed domains file. Path={FailedDomainsPath}", _failedDomainStore.Path);
            SetStatus($"Could not open failed domains file: {ex.Message}");
        }
    }

    private void OnClearFailedDomainsClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Clear all recorded failed domains and their failure counts?",
            "Clear failed domains",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_failedDomainStore.Clear())
        {
            SetStatus("Failed domains were cleared in memory, but the file could not be updated.");
            return;
        }

        _logger.LogInformation("Cleared failed domains. Path={FailedDomainsPath}", _failedDomainStore.Path);
        SetStatus("Failed domains cleared.");
    }

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        OpenExternal(GetCurrentUrl());
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        Collapse(force: true, reason: "HideButton");
    }

    private void OnClosePageClick(object sender, RoutedEventArgs e)
    {
        if (_currentItem is null)
        {
            Collapse(force: true, reason: "ClosePage");
            return;
        }

        CancelBrowserCreation(_currentItem.Tool.Id);
        if (_browsers.TryGetValue(_currentItem.Tool.Id, out var browser))
        {
            RemoveAndDisposeBrowser(_currentItem.Tool.Id, browser);
            _logger.LogInformation("Closed WebView2 browser. ToolId={ToolId}", _currentItem.Tool.Id);
        }

        _toolStatuses[_currentItem.Tool.Id] = "Closed";
        SetStatus("Closed");
        Collapse(force: true, reason: "ClosePage");
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
        _logger.LogInformation("Pin state changed. IsPinned={IsPinned}", _isPinned);
        DockToConfiguredEdge(GetCurrentDockWidth(), "PinChanged");
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
            Collapse(reason: "AutoHide");
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

    private void HandleAppbarFullscreenNotification()
    {
        var wasAutoHiddenForFullscreen = _isAutoHiddenForFullscreen;
        UpdateFullscreenAutoHideState();

        if (!_isAutoHiddenForFullscreen && !wasAutoHiddenForFullscreen && !_isResizing)
        {
            _logger.LogInformation("Reapplying dock position after fullscreen appbar notification.");
            DockToConfiguredEdge(GetCurrentDockWidth(), "AppBarFullscreenNotification");
        }
    }

    private void HideForFullscreenApp()
    {
        if (_isAutoHiddenForFullscreen)
        {
            return;
        }

        _cursorLeftAt = null;
        _isAutoHiddenForFullscreen = true;
        ApplyBrowserMemoryTargets();
        _logger.LogInformation("Hiding SideDock for fullscreen app.");
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
        ApplyBrowserMemoryTargets();
        ApplyTopmostState();

        var currentWidth = GetCurrentDockWidth();
        _logger.LogInformation("Restoring SideDock after fullscreen app.");
        _appBarManager?.Register(GetReservedWidth(currentWidth), currentWidth, GetDockWindowHeight(), GetDockSide(), "FullscreenRestore");
        DockToConfiguredEdge(currentWidth, "FullscreenRestore");
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
        ApplyBrowserMemoryTargets();
        ApplyDockSideLayout();
        ApplyTopmostState();
        _logger.LogInformation("SideDock expanded. Width={Width}", GetCurrentDockWidth());
        DockToConfiguredEdge(GetCurrentDockWidth(), "Expand");
    }

    private void Collapse(bool force = false, string reason = "Collapse")
    {
        if (_isPinned && !force)
        {
            _logger.LogDebug("Collapse skipped because SideDock is pinned. Reason={Reason}", reason);
            return;
        }

        _isExpanded = false;
        ContentPanel.Visibility = Visibility.Collapsed;
        ResizeGrip.Visibility = Visibility.Collapsed;
        ContentColumn.Width = new GridLength(0);
        ApplyBrowserMemoryTargets();
        ApplyDockSideLayout();
        ApplyTopmostState();
        _logger.LogInformation("SideDock collapsed. Reason={Reason} Width={Width}", reason, GetCurrentDockWidth());
        DockToConfiguredEdge(GetCurrentDockWidth(), reason);
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
        var requestedWidth = DockLayoutCalculator.GetRequestedResizeWidth(
            GetDockSide(),
            screenXDips,
            _resizeAnchorEdge);
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

        _logger.LogInformation("Resize completed. ExpandedWidth={ExpandedWidth}", _expandedWidth);
        DockToConfiguredEdge(GetCurrentDockWidth(), "ResizeComplete");
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

        var screenBounds = GetCurrentMonitorBoundsDips();

        _resizePreviewWindow ??= new Window
        {
            Left = screenBounds.Left,
            Width = screenBounds.Width,
            Height = screenBounds.Height,
            Top = screenBounds.Top,
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

        var screenBounds = GetCurrentMonitorBoundsDips();
        var gripWidth = GetResizeGripWidth();
        var previewLayout = DockLayoutCalculator.GetResizePreviewLayout(
            GetDockSide(),
            screenBounds.Left,
            _resizeContentFixedEdge,
            _resizeAnchorEdge,
            _pendingResizeWidth,
            gripWidth);

        Canvas.SetLeft(_resizePreviewPanel, previewLayout.Left);
        Canvas.SetTop(_resizePreviewPanel, 0);
        _resizePreviewPanel.Width = previewLayout.Width;
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
        ApplyBrowserMemoryTargets();

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

        _resizePreviewBrowser = null;
        UpdateBrowserPresentation();
    }

    private void HideContentForResize()
    {
        if (_isContentHiddenForResize)
        {
            return;
        }

        ContentPanel.Visibility = Visibility.Hidden;
        _isContentHiddenForResize = true;
        ApplyBrowserMemoryTargets();
    }

    private void RestoreContentAfterResize()
    {
        if (!_isContentHiddenForResize)
        {
            return;
        }

        ContentPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        _isContentHiddenForResize = false;
        ApplyBrowserMemoryTargets();
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

    private Rect GetCurrentMonitorBoundsDips()
    {
        return GetCurrentMonitorLayout().MonitorDips;
    }

    private void DockToConfiguredEdge(double width, string reason)
    {
        var clampedWidth = _isExpanded
            ? ClampExpandedWidth(width)
            : _settings.CollapsedWidth;
        var layout = GetCurrentMonitorLayout();
        var windowHeight = GetDockWindowHeight(layout);
        _logger.LogInformation(
            "Docking requested. Reason={Reason} DockSide={DockSide} IsExpanded={IsExpanded} IsPinned={IsPinned} RequestedWidth={RequestedWidth} ClampedWidth={ClampedWidth} WindowHeight={WindowHeight} Monitor={@Monitor} WorkArea={@WorkArea} Dpi={Dpi}",
            reason,
            GetDockSide(),
            _isExpanded,
            _isPinned,
            width,
            clampedWidth,
            windowHeight,
            ToLogRect(layout.MonitorPixels),
            ToLogRect(layout.WorkPixels),
            layout.Dpi);
        Width = clampedWidth;
        Height = windowHeight;

        if (_isAutoHiddenForFullscreen)
        {
            _logger.LogInformation("Docking skipped because SideDock is hidden for fullscreen app. Reason={Reason}", reason);
            return;
        }

        if (_appBarManager is not null)
        {
            _appBarManager.Apply(GetReservedWidth(clampedWidth), clampedWidth, windowHeight, GetDockSide(), reason);
            return;
        }

        var monitorBounds = layout.MonitorDips;
        Left = DockLayoutCalculator.GetDockLeft(GetDockSide(), monitorBounds.Left, monitorBounds.Right, clampedWidth);
        Top = monitorBounds.Top + Math.Max(0, (monitorBounds.Height - windowHeight) / 2);
        Height = windowHeight;
        _logger.LogInformation(
            "Docked without appbar manager. Reason={Reason} Left={Left} Top={Top} Width={Width} Height={Height}",
            reason,
            Left,
            Top,
            Width,
            Height);
    }

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

    private double GetDockWindowHeight()
    {
        return GetDockWindowHeight(GetCurrentMonitorLayout());
    }

    private static double GetDockWindowHeight(MonitorLayout layout)
    {
        return layout.MonitorHeightDips;
    }

    private MonitorLayout GetCurrentMonitorLayout()
    {
        return MonitorLayoutProvider.FromWindow(_hwndSource?.Handle ?? nint.Zero);
    }

    private Rect GetWindowScreenRect()
    {
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height)));
        return new Rect(topLeft, bottomRight);
    }

    private void UpdateNavigationState()
    {
        if (_currentItem is null)
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
        return DockLayoutCalculator.GetReservedWidth(
            _isExpanded,
            _isPinned,
            windowWidth,
            _settings.CollapsedWidth);
    }

    private double GetCurrentDockWidth()
    {
        return DockLayoutCalculator.GetCurrentWindowWidth(_isExpanded, _expandedWidth, _settings.CollapsedWidth);
    }

    private double ClampExpandedWidth(double width)
    {
        return DockLayoutCalculator.ClampExpandedWidth(width, _settings.MinExpandedWidth, GetMaxExpandedWidth());
    }

    private double GetMaxExpandedWidth()
    {
        var screenWidth = GetCurrentMonitorLayout().MonitorWidthDips;
        return DockLayoutCalculator.GetMaxExpandedWidth(_settings.MinExpandedWidth, screenWidth, _settings.CollapsedWidth);
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

        var sideDockMonitor = MonitorLayoutProvider.GetMonitorFromWindow(ownerHandle, MonitorLayoutProvider.MonitorDefaultToNearest);
        var foregroundMonitor = MonitorLayoutProvider.GetMonitorFromWindow(foregroundWindow, MonitorLayoutProvider.MonitorDefaultToNull);
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

        return MonitorLayoutProvider.TryGetFromMonitor(foregroundMonitor, ownerHandle, out var monitorLayout)
            && FullscreenWindowRules.CoversMonitor(windowRect, monitorLayout.MonitorPixels)
            && !IsScreenshotCaptureWindow(foregroundWindow);
    }

    private static bool IsScreenshotCaptureWindow(IntPtr hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out var processId) != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (FullscreenWindowRules.IsScreenshotProcessName(process.ProcessName))
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

        return FullscreenWindowRules.ContainsScreenshotKeyword(GetWindowClassName(hwnd))
            || FullscreenWindowRules.ContainsScreenshotKeyword(GetWindowTitle(hwnd));
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        if (hwnd == GetShellWindow())
        {
            return true;
        }

        var className = GetWindowClassName(hwnd);
        return FullscreenWindowRules.IsShellWindowClassName(className);
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
            _logger.LogWarning(ex, "Could not open external browser. HasUrl={HasUrl}", !string.IsNullOrWhiteSpace(url));
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
                _logger.LogInformation("Received appbar position changed notification.");
                Dispatcher.BeginInvoke(() => _appBarManager.Refresh("AppBarPosChanged"));
            }
            else if (wParam.ToInt32() == AbnFullscreenApp)
            {
                _logger.LogInformation("Received appbar fullscreen notification.");
                Dispatcher.BeginInvoke(HandleAppbarFullscreenNotification);
            }

            handled = true;
            return IntPtr.Zero;
        }

        if (msg is WmDisplayChange or WmSettingChange or WmDpiChanged)
        {
            _logger.LogInformation("Received display, setting, or DPI message. Message=0x{Message:X}", msg);
            Dispatcher.BeginInvoke(() =>
            {
                ApplyTheme();
                ApplyBrowserThemes();
                DockToConfiguredEdge(GetCurrentDockWidth(), "DisplayOrDpiChanged");
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
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    private sealed record IconCandidate(string Href, string Rel, string Sizes);
}
