using System.Windows;

namespace SideDock;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\SideDock.SingleInstance";

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message,
                "SideDock",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }
}
