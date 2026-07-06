using Microsoft.Extensions.Logging;
using SideDock;

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
