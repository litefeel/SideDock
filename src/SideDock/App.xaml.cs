using System.Windows;

namespace SideDock;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
    }
}
