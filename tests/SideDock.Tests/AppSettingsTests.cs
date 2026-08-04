using System.Text.Json;
using SideDock;

namespace SideDock.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void MissingLogSettingsUseDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """
            {
              "Tools": [
                {
                  "Id": "tool",
                  "Title": "Tool",
                  "Url": "https://example.com/",
                  "IconKey": "EX",
                  "OpenExternalFallbackEnabled": true
                }
              ]
            }
            """)!;

        settings.Normalize();

        Assert.Equal(AppSettings.DefaultLogLevel, settings.LogLevel);
        Assert.Equal(AppSettings.DefaultLogFileSizeLimitBytes, settings.LogFileSizeLimitBytes);
        Assert.Equal(AppSettings.DefaultLogRetainedFileCount, settings.LogRetainedFileCount);
        Assert.Null(settings.PreferredDisplayId);
        Assert.Null(settings.PreferredDisplayName);
    }

    [Fact]
    public void PreferredDisplaySettingsRoundTrip()
    {
        var original = new AppSettings
        {
            PreferredDisplayId = @"pnp:MONITOR\DEL1234\instance",
            PreferredDisplayName = "DELL U2720Q",
            Tools =
            [
                new ToolDefinition("tool", "Tool", "https://example.com/", "EX", true)
            ]
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(original.PreferredDisplayId, restored.PreferredDisplayId);
        Assert.Equal(original.PreferredDisplayName, restored.PreferredDisplayName);
    }

    [Fact]
    public void InvalidLogSettingsAreNormalized()
    {
        var settings = new AppSettings
        {
            LogLevel = "not-a-level",
            LogFileSizeLimitBytes = 1,
            LogRetainedFileCount = 0,
            Tools =
            [
                new ToolDefinition("tool", "Tool", "https://example.com/", "EX", true)
            ]
        };

        settings.Normalize();

        Assert.Equal(AppSettings.DefaultLogLevel, settings.LogLevel);
        Assert.Equal(AppSettings.MinLogFileSizeLimitBytes, settings.LogFileSizeLimitBytes);
        Assert.Equal(AppSettings.MinLogRetainedFileCount, settings.LogRetainedFileCount);
    }

    [Theory]
    [InlineData("trace", "Trace")]
    [InlineData("debug", "Debug")]
    [InlineData("info", "Information")]
    [InlineData("warn", "Warning")]
    [InlineData("fatal", "Critical")]
    [InlineData("none", "None")]
    [InlineData("unknown", AppSettings.DefaultLogLevel)]
    public void LogLevelAliasesAreNormalized(string input, string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLogLevel(input));
    }
}
