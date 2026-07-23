using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Windows.Threading;

namespace SideDock;

internal sealed class FailedDomainNotificationService : IDisposable
{
    private const string ActionArgumentName = "action";
    private const string OpenFailedDomainsFileAction = "openFailedDomainsFile";

    private readonly Dispatcher _dispatcher;
    private readonly Action _openFailedDomainsFile;
    private readonly ILogger _logger;
    private bool _isRegistered;
    private bool _isDisposed;

    public FailedDomainNotificationService(
        Dispatcher dispatcher,
        Action openFailedDomainsFile,
        ILogger logger)
    {
        _dispatcher = dispatcher;
        _openFailedDomainsFile = openFailedDomainsFile;
        _logger = logger;

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            _isRegistered = true;
            _logger.LogDebug("Failed domain notification activation registered.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register failed domain notification activation.");
        }
    }

    public void ShowNewDomain(string endpoint)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument(ActionArgumentName, OpenFailedDomainsFileAction)
                .AddText("New failed domain")
                .AddText(endpoint)
                .AddText("Click to open the failed domains file.")
                .Show();
            _logger.LogInformation("Showed new failed domain notification. Endpoint={Endpoint}", endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show new failed domain notification. Endpoint={Endpoint}", endpoint);
        }
    }

    public static void Uninstall(ILogger logger)
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
            logger.LogInformation("Cleaned up app notification resources.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clean up app notification resources.");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_isRegistered)
        {
            ToastNotificationManagerCompat.OnActivated -= OnActivated;
            _isRegistered = false;
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat eventArgs)
    {
        try
        {
            var arguments = ToastArguments.Parse(eventArgs.Argument);
            if (!arguments.TryGetValue(ActionArgumentName, out var action)
                || !string.Equals(action, OpenFailedDomainsFileAction, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation("Failed domain notification activated.");
            _dispatcher.BeginInvoke(_openFailedDomainsFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not handle failed domain notification activation.");
        }
    }
}
