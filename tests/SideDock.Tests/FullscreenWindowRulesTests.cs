using SideDock;

namespace SideDock.Tests;

public sealed class FullscreenWindowRulesTests
{
    [Fact]
    public void FullscreenCoverageAllowsTolerance()
    {
        var monitor = new NativeRect(0, 0, 1920, 1080);
        var window = new NativeRect(2, 2, 1918, 1078);

        Assert.True(FullscreenWindowRules.CoversMonitor(window, monitor));
    }

    [Fact]
    public void FullscreenCoverageRejectsNonCoveringWindows()
    {
        var monitor = new NativeRect(0, 0, 1920, 1080);
        var window = new NativeRect(3, 0, 1920, 1080);

        Assert.False(FullscreenWindowRules.CoversMonitor(window, monitor));
    }

    [Fact]
    public void ScreenshotAndShellFiltersAreCaseInsensitive()
    {
        Assert.True(FullscreenWindowRules.IsScreenshotProcessName("sharex"));
        Assert.True(FullscreenWindowRules.ContainsScreenshotKeyword("Screen Clipping Overlay"));
        Assert.True(FullscreenWindowRules.ContainsScreenshotKeyword("\u622A\u56FE"));
        Assert.True(FullscreenWindowRules.IsShellWindowClassName("workerw"));
        Assert.False(FullscreenWindowRules.IsShellWindowClassName("Chrome_WidgetWin_1"));
    }
}
