using System.IO;
using System.Text.Json;

namespace SideDock;

public sealed class AppSettings
{
    public double DefaultExpandedWidth { get; set; } = 430;
    public double MinExpandedWidth { get; set; } = 360;
    public double MaxExpandedWidth { get; set; } = 0;
    public double CollapsedWidth { get; set; } = 48;
    public int AutoHideDelayMilliseconds { get; set; } = 600;
    public bool TopmostByDefault { get; set; } = true;
    public string ThemeMode { get; set; } = nameof(AppThemeMode.System);
    public string DockSide { get; set; } = nameof(AppDockSide.Right);
    public List<ToolDefinition> Tools { get; set; } = [];

    public static string UserSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideDock",
        "appsettings.json");

    public static AppSettings Load()
    {
        if (TryLoadFromPath(UserSettingsPath, out var userSettings))
        {
            return userSettings;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (TryLoadFromPath(path, out var appSettings))
        {
            return appSettings;
        }

        return CreateDefault();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(UserSettingsPath, json);
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
        catch
        {
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
