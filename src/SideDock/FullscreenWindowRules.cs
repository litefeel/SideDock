namespace SideDock;

internal static class FullscreenWindowRules
{
    private const int FullscreenEdgeTolerancePixels = 2;

    private static readonly string[] ScreenshotProcessNames =
    [
        "FSCapture",
        "Greenshot",
        "Lightshot",
        "PicPick",
        "ScreenClippingHost",
        "ShareX",
        "Snipaste",
        "SnippingTool"
    ];

    private static readonly string[] ScreenshotWindowKeywords =
    [
        "capture",
        "screen clipping",
        "screenshot",
        "screenclip",
        "snip",
        "\u622A\u56FE",
        "\u622A\u5C4F"
    ];

    private static readonly string[] ShellWindowClassNames =
    [
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    ];

    public static bool CoversMonitor(NativeRect windowRect, NativeRect monitorRect)
    {
        return windowRect.Left <= monitorRect.Left + FullscreenEdgeTolerancePixels
            && windowRect.Top <= monitorRect.Top + FullscreenEdgeTolerancePixels
            && windowRect.Right >= monitorRect.Right - FullscreenEdgeTolerancePixels
            && windowRect.Bottom >= monitorRect.Bottom - FullscreenEdgeTolerancePixels;
    }

    public static bool IsScreenshotProcessName(string? processName)
    {
        return !string.IsNullOrWhiteSpace(processName)
            && ScreenshotProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);
    }

    public static bool ContainsScreenshotKeyword(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && ScreenshotWindowKeywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsShellWindowClassName(string? className)
    {
        return !string.IsNullOrWhiteSpace(className)
            && ShellWindowClassNames.Contains(className, StringComparer.OrdinalIgnoreCase);
    }
}
