using System.Windows;
using Microsoft.Extensions.Logging;

namespace SideDock;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\SideDock.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private ILogger<App> _logger = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            MessageBox.Show(
                "SideDock requires Windows 11 (build 22000 or later). Windows 10 is not supported.",
                "SideDock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        AppLogging.InitializeBootstrap();
        _logger = AppLogging.CreateLogger<App>();
        _logger.LogInformation("SideDock startup requested. ArgsCount={ArgsCount}", e.Args.Length);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            _logger.LogInformation("Another SideDock instance is already running. Shutting down duplicate instance.");
            AppLogging.Shutdown();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var settings = AppSettings.Load();
        AppLogging.Configure(settings);
        _logger = AppLogging.CreateLogger<App>();
        _logger.LogInformation(
            "SideDock startup continuing. SettingsPath={SettingsPath} LogDirectory={LogDirectory} LogLevel={LogLevel}",
            AppSettings.UserSettingsPath,
            AppSettings.LogDirectory,
            settings.LogLevel);

        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unhandled UI exception.");
            MessageBox.Show(
                args.Exception.Message,
                "SideDock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        MainWindow = new MainWindow(settings);
        MainWindow.Show();
        _logger.LogInformation("Main window shown.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("SideDock exiting. ExitCode={ExitCode}", e.ApplicationExitCode);

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
        AppLogging.Shutdown();
    }
}
