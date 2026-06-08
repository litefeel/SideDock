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
    public List<ToolDefinition> Tools { get; set; } = [];

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return CreateDefault();
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (settings is null || settings.Tools.Count == 0)
            {
                return CreateDefault();
            }

            settings.DefaultExpandedWidth = Math.Max(settings.DefaultExpandedWidth, settings.MinExpandedWidth);

            return settings;
        }
        catch
        {
            return CreateDefault();
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
}

public sealed record ToolDefinition(
    string Id,
    string Title,
    string Url,
    string IconKey,
    bool OpenExternalFallbackEnabled);
