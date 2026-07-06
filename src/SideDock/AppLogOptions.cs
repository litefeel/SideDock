namespace SideDock;

internal sealed record AppLogOptions(
    string DirectoryPath,
    string LogLevel,
    long FileSizeLimitBytes,
    int RetainedFileCount)
{
    public static AppLogOptions Bootstrap { get; } = new(
        AppSettings.LogDirectory,
        AppSettings.DefaultLogLevel,
        AppSettings.DefaultLogFileSizeLimitBytes,
        AppSettings.DefaultLogRetainedFileCount);

    public static AppLogOptions FromSettings(AppSettings settings)
    {
        return new AppLogOptions(
            AppSettings.LogDirectory,
            AppSettings.NormalizeLogLevel(settings.LogLevel),
            Math.Clamp(
                settings.LogFileSizeLimitBytes,
                AppSettings.MinLogFileSizeLimitBytes,
                AppSettings.MaxLogFileSizeLimitBytes),
            Math.Clamp(
                settings.LogRetainedFileCount,
                AppSettings.MinLogRetainedFileCount,
                AppSettings.MaxLogRetainedFileCount));
    }
}
