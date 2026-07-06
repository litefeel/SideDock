using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Extensions.Logging;

namespace SideDock;

internal static class AppLogging
{
    private static readonly object SyncRoot = new();
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private static Logger? _serilogLogger;

    public static void InitializeBootstrap()
    {
        Configure(AppLogOptions.Bootstrap);
    }

    public static void Configure(AppSettings settings)
    {
        Configure(AppLogOptions.FromSettings(settings));
    }

    public static void Configure(AppLogOptions options)
    {
        try
        {
            var serilogLogger = CreateSerilogLogger(options);
            var loggerFactory = CreateLoggerFactory(serilogLogger, options);

            lock (SyncRoot)
            {
                var oldFactory = _loggerFactory;
                var oldLogger = _serilogLogger;

                _loggerFactory = loggerFactory;
                _serilogLogger = serilogLogger;
                Log.Logger = serilogLogger;

                oldFactory.Dispose();
                oldLogger?.Dispose();
            }

            CreateLogger(typeof(AppLogging).FullName!).LogInformation(
                "Logging configured. LogDirectory={LogDirectory} LogLevel={LogLevel} FileSizeLimitBytes={FileSizeLimitBytes} RetainedFileCount={RetainedFileCount}",
                options.DirectoryPath,
                options.LogLevel,
                options.FileSizeLimitBytes,
                options.RetainedFileCount);
        }
        catch
        {
            lock (SyncRoot)
            {
                _loggerFactory.Dispose();
                _serilogLogger?.Dispose();
                _loggerFactory = NullLoggerFactory.Instance;
                _serilogLogger = null;
                Log.Logger = Serilog.Core.Logger.None;
            }
        }
    }

    public static Microsoft.Extensions.Logging.ILogger<T> CreateLogger<T>()
    {
        return _loggerFactory.CreateLogger<T>();
    }

    public static Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        return _loggerFactory.CreateLogger(categoryName);
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            _loggerFactory.Dispose();
            _serilogLogger?.Dispose();
            _loggerFactory = NullLoggerFactory.Instance;
            _serilogLogger = null;
            Log.CloseAndFlush();
            Log.Logger = Serilog.Core.Logger.None;
        }
    }

    internal static ILoggerFactory CreateLoggerFactory(AppLogOptions options, out IDisposable serilogLogger)
    {
        var logger = CreateSerilogLogger(options);
        serilogLogger = logger;
        return CreateLoggerFactory(logger, options);
    }

    private static Logger CreateSerilogLogger(AppLogOptions options)
    {
        Directory.CreateDirectory(options.DirectoryPath);

        return new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(options.LogLevel))
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "SideDock")
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(options.DirectoryPath, "sidedock-.clef"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                retainedFileCountLimit: options.RetainedFileCount,
                shared: true)
            .CreateLogger();
    }

    private static ILoggerFactory CreateLoggerFactory(Logger serilogLogger, AppLogOptions options)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(ToMicrosoftLevel(options.LogLevel));
            builder.AddSerilog(serilogLogger, dispose: false);
        });
    }

    private static LogEventLevel ToSerilogLevel(string logLevel)
    {
        return AppSettings.NormalizeLogLevel(logLevel) switch
        {
            "Trace" => LogEventLevel.Verbose,
            "Debug" => LogEventLevel.Debug,
            "Warning" => LogEventLevel.Warning,
            "Error" => LogEventLevel.Error,
            "Critical" => LogEventLevel.Fatal,
            "None" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }

    private static Microsoft.Extensions.Logging.LogLevel ToMicrosoftLevel(string logLevel)
    {
        return AppSettings.NormalizeLogLevel(logLevel) switch
        {
            "Trace" => Microsoft.Extensions.Logging.LogLevel.Trace,
            "Debug" => Microsoft.Extensions.Logging.LogLevel.Debug,
            "Warning" => Microsoft.Extensions.Logging.LogLevel.Warning,
            "Error" => Microsoft.Extensions.Logging.LogLevel.Error,
            "Critical" => Microsoft.Extensions.Logging.LogLevel.Critical,
            "None" => Microsoft.Extensions.Logging.LogLevel.None,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };
    }
}
