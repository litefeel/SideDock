using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SideDock;

public sealed class AppSettings
{
    public const string DefaultLogLevel = "Information";
    public const long DefaultLogFileSizeLimitBytes = 2 * 1024 * 1024;
    public const int DefaultLogRetainedFileCount = 5;
    public const long MinLogFileSizeLimitBytes = 64 * 1024;
    public const long MaxLogFileSizeLimitBytes = 50 * 1024 * 1024;
    public const int MinLogRetainedFileCount = 1;
    public const int MaxLogRetainedFileCount = 20;

    public double DefaultExpandedWidth { get; set; } = 430;
    public double MinExpandedWidth { get; set; } = 360;
    public double MaxExpandedWidth { get; set; } = 0;
    public double CollapsedWidth { get; set; } = 48;
    public int AutoHideDelayMilliseconds { get; set; } = 600;
    public bool TopmostByDefault { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string ThemeMode { get; set; } = nameof(AppThemeMode.System);
    public string DockSide { get; set; } = nameof(AppDockSide.Right);
    public string LogLevel { get; set; } = DefaultLogLevel;
    public long LogFileSizeLimitBytes { get; set; } = DefaultLogFileSizeLimitBytes;
    public int LogRetainedFileCount { get; set; } = DefaultLogRetainedFileCount;
    public List<ToolDefinition> Tools { get; set; } = [];

    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDock");

    public static string UserSettingsPath => Path.Combine(
        UserDataDirectory,
        "appsettings.json");

    public static string LogDirectory => Path.Combine(UserDataDirectory, "logs");

    public static string FailedDomainsPath => Path.Combine(UserDataDirectory, "failed-domains.txt");

    public static AppSettings Load()
    {
        var logger = AppLogging.CreateLogger<AppSettings>();
        if (TryLoadFromPath(UserSettingsPath, out var userSettings))
        {
            logger.LogInformation("Loaded user settings. Path={SettingsPath}", UserSettingsPath);
            return userSettings;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (TryLoadFromPath(path, out var appSettings))
        {
            logger.LogInformation("Loaded bundled settings. Path={SettingsPath}", path);
            return appSettings;
        }

        logger.LogWarning("No valid settings file was found. Using built-in defaults.");
        return CreateDefault();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(UserSettingsPath, json);
        AppLogging.CreateLogger<AppSettings>().LogInformation("Saved user settings. Path={SettingsPath}", UserSettingsPath);
    }

    private static bool TryLoadFromPath(string path, out AppSettings settings)
    {
        settings = null!;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (loadedSettings is null || loadedSettings.Tools.Count == 0)
            {
                return false;
            }

            loadedSettings.Normalize();
            settings = loadedSettings;

            return true;
        }
        catch (Exception ex)
        {
            AppLogging.CreateLogger<AppSettings>().LogWarning(ex, "Could not load settings. Path={SettingsPath}", path);
            return false;
        }
    }

    private static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            Tools =
            [
                new ToolDefinition("translate", "Google Translate", "https://translate.google.com/", "GT", true),
                new ToolDefinition("chatgpt", "ChatGPT", "https://chatgpt.com/", "AI", true),
                new ToolDefinition("claude", "Claude", "https://claude.ai/", "CL", true),
                new ToolDefinition("gemini", "Gemini", "https://gemini.google.com/", "GE", true)
            ]
        };
    }

    public void Normalize()
    {
        DefaultExpandedWidth = Math.Max(DefaultExpandedWidth, MinExpandedWidth);
        ThemeMode = NormalizeThemeMode(ThemeMode).ToString();
        DockSide = NormalizeDockSide(DockSide).ToString();
        LogLevel = NormalizeLogLevel(LogLevel);
        LogFileSizeLimitBytes = Math.Clamp(
            LogFileSizeLimitBytes,
            MinLogFileSizeLimitBytes,
            MaxLogFileSizeLimitBytes);
        LogRetainedFileCount = Math.Clamp(
            LogRetainedFileCount,
            MinLogRetainedFileCount,
            MaxLogRetainedFileCount);
    }

    public static AppThemeMode NormalizeThemeMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "dark" or "dork" => AppThemeMode.Dark,
            "light" => AppThemeMode.Light,
            _ => AppThemeMode.System
        };
    }

    public static AppDockSide NormalizeDockSide(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "left" => AppDockSide.Left,
            _ => AppDockSide.Right
        };
    }

    public static string NormalizeLogLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "trace" => "Trace",
            "debug" => "Debug",
            "information" or "info" => "Information",
            "warning" or "warn" => "Warning",
            "error" => "Error",
            "critical" or "fatal" => "Critical",
            "none" => "None",
            _ => DefaultLogLevel
        };
    }
}

public enum AppThemeMode
{
    Dark,
    Light,
    System
}

public enum AppDockSide
{
    Left,
    Right
}

public sealed record ToolDefinition(
    string Id,
    string Title,
    string Url,
    string IconKey,
    bool OpenExternalFallbackEnabled);
