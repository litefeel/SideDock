using Microsoft.Extensions.Logging;
using SideDock;
using System.Text.Json;

namespace SideDock.Tests;

public sealed class AppLoggingTests
{
    [Fact]
    public void LoggerCreatesDirectoryAndWritesCompactJson()
    {
        var directory = CreateTempLogDirectory();
        try
        {
            using var factory = AppLogging.CreateLoggerFactory(
                new AppLogOptions(directory, "Information", AppSettings.DefaultLogFileSizeLimitBytes, 5),
                out var serilogLogger);
            using (serilogLogger)
            {
                var logger = factory.CreateLogger("SideDock.Tests.Logging");
                logger.LogInformation("Structured log test {Value}", 42);
            }

            var log = ReadAllLogText(directory);

            Assert.Contains("Structured log test", log);
            Assert.Contains("\"Value\":42", log);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task LoggerPreservesIconScopesAcrossConcurrentAsyncRefreshes()
    {
        var directory = CreateTempLogDirectory();
        try
        {
            using var factory = AppLogging.CreateLoggerFactory(
                new AppLogOptions(directory, "Information", AppSettings.DefaultLogFileSizeLimitBytes, 5),
                out var serilogLogger);
            using (serilogLogger)
            {
                var logger = factory.CreateLogger("SideDock.Tests.IconScopes");
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                async Task WriteRefreshAsync(string refreshId, string trigger, string iconUri)
                {
                    using var refreshScope = logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["ToolId"] = "chatgpt",
                        ["IconTrigger"] = trigger,
                        ["IconRefreshId"] = refreshId
                    });
                    await resume.Task;
                    using var uriScope = logger.BeginScope(new Dictionary<string, object?> { ["IconUri"] = iconUri });
                    logger.LogInformation("Icon scope test. Event={Event}", "Refresh");
                }

                var first = WriteRefreshAsync("refresh-1", "NavigationCompleted", "https://example.com/page-icon.png");
                var second = WriteRefreshAsync("refresh-2", "FaviconChanged", "https://example.com/favicon.ico");
                resume.SetResult();
                await Task.WhenAll(first, second);
                logger.LogInformation("Icon scope test. Event={Event}", "OutsideScope");
            }

            var lines = ReadAllLogText(directory).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            var refreshIds = new HashSet<string>();
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                if (entry.GetProperty("Event").GetString() == "OutsideScope")
                {
                    Assert.False(entry.TryGetProperty("IconRefreshId", out _));
                    Assert.False(entry.TryGetProperty("ToolId", out _));
                    Assert.False(entry.TryGetProperty("IconUri", out _));
                    continue;
                }

                Assert.Equal("chatgpt", entry.GetProperty("ToolId").GetString());
                var refreshId = entry.GetProperty("IconRefreshId").GetString()!;
                Assert.Contains(refreshId, new[] { "refresh-1", "refresh-2" });
                Assert.True(refreshIds.Add(refreshId));
                var isFirst = refreshId == "refresh-1";
                Assert.Equal(isFirst ? "NavigationCompleted" : "FaviconChanged", entry.GetProperty("IconTrigger").GetString());
                Assert.Equal(isFirst ? "https://example.com/page-icon.png" : "https://example.com/favicon.ico", entry.GetProperty("IconUri").GetString());
            }

            Assert.Equal(2, refreshIds.Count);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void LoggerFiltersEventsBelowConfiguredLevel()
    {
        var directory = CreateTempLogDirectory();
        try
        {
            using var factory = AppLogging.CreateLoggerFactory(
                new AppLogOptions(directory, "Warning", AppSettings.DefaultLogFileSizeLimitBytes, 5),
                out var serilogLogger);
            using (serilogLogger)
            {
                var logger = factory.CreateLogger("SideDock.Tests.Filtering");
                logger.LogInformation("Hidden information event");
                logger.LogWarning("Visible warning event");
            }

            var log = ReadAllLogText(directory);

            Assert.DoesNotContain("Hidden information event", log);
            Assert.Contains("Visible warning event", log);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void LoggerRollsFilesAndHonorsRetentionLimit()
    {
        var directory = CreateTempLogDirectory();
        try
        {
            using var factory = AppLogging.CreateLoggerFactory(
                new AppLogOptions(directory, "Information", 512, 2),
                out var serilogLogger);
            using (serilogLogger)
            {
                var logger = factory.CreateLogger("SideDock.Tests.Rolling");
                for (var i = 0; i < 40; i++)
                {
                    logger.LogInformation("Rolling log event {Index} {Payload}", i, new string('x', 256));
                }
            }

            var files = Directory.GetFiles(directory, "*.clef");

            Assert.InRange(files.Length, 1, 2);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempLogDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "SideDock.Tests", Guid.NewGuid().ToString("N"));
    }

    private static string ReadAllLogText(string directory)
    {
        return string.Join(Environment.NewLine, Directory.GetFiles(directory, "*.clef").Select(File.ReadAllText));
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
