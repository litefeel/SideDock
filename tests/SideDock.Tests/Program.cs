using SideDock;

var tests = new (string Name, Action Run)[]
{
    ("Max expanded width leaves room for collapsed rail", MaxExpandedWidthLeavesRoomForCollapsedRail),
    ("Max expanded width never drops below minimum", MaxExpandedWidthNeverDropsBelowMinimum),
    ("Expanded width clamps to minimum and maximum", ExpandedWidthClampsToMinimumAndMaximum),
    ("Reserved width expands only when pinned", ReservedWidthExpandsOnlyWhenPinned),
    ("Resize width follows dock side direction", ResizeWidthFollowsDockSideDirection),
    ("Resize preview layout matches left and right dock math", ResizePreviewLayoutMatchesDockMath),
    ("Fullscreen coverage allows two pixel tolerance", FullscreenCoverageAllowsTolerance),
    ("Fullscreen coverage rejects non-covering windows", FullscreenCoverageRejectsNonCoveringWindows),
    ("Screenshot and shell window filters are case-insensitive", ScreenshotAndShellFiltersAreCaseInsensitive)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} test(s) failed.");
    return 1;
}

Console.WriteLine($"{tests.Length} test(s) passed.");
return 0;

static void MaxExpandedWidthLeavesRoomForCollapsedRail()
{
    AssertEqual(1872, DockLayoutCalculator.GetMaxExpandedWidth(360, 1920, 48));
}

static void MaxExpandedWidthNeverDropsBelowMinimum()
{
    AssertEqual(360, DockLayoutCalculator.GetMaxExpandedWidth(360, 320, 48));
}

static void ExpandedWidthClampsToMinimumAndMaximum()
{
    AssertEqual(360, DockLayoutCalculator.ClampExpandedWidth(200, 360, 1872));
    AssertEqual(430, DockLayoutCalculator.ClampExpandedWidth(430, 360, 1872));
    AssertEqual(1872, DockLayoutCalculator.ClampExpandedWidth(2000, 360, 1872));
}

static void ReservedWidthExpandsOnlyWhenPinned()
{
    AssertEqual(48, DockLayoutCalculator.GetReservedWidth(isExpanded: false, isPinned: false, windowWidth: 430, collapsedWidth: 48));
    AssertEqual(48, DockLayoutCalculator.GetReservedWidth(isExpanded: true, isPinned: false, windowWidth: 430, collapsedWidth: 48));
    AssertEqual(430, DockLayoutCalculator.GetReservedWidth(isExpanded: true, isPinned: true, windowWidth: 430, collapsedWidth: 48));
}

static void ResizeWidthFollowsDockSideDirection()
{
    AssertEqual(200, DockLayoutCalculator.GetRequestedResizeWidth(AppDockSide.Left, screenXDips: 300, resizeAnchorEdge: 100));
    AssertEqual(200, DockLayoutCalculator.GetRequestedResizeWidth(AppDockSide.Right, screenXDips: 100, resizeAnchorEdge: 300));
}

static void ResizePreviewLayoutMatchesDockMath()
{
    var left = DockLayoutCalculator.GetResizePreviewLayout(
        AppDockSide.Left,
        screenLeft: 0,
        resizeContentFixedEdge: 48,
        resizeAnchorEdge: 0,
        pendingResizeWidth: 430,
        gripWidth: 18);
    AssertEqual(48, left.Left);
    AssertEqual(364, left.Width);

    var right = DockLayoutCalculator.GetResizePreviewLayout(
        AppDockSide.Right,
        screenLeft: 0,
        resizeContentFixedEdge: 1872,
        resizeAnchorEdge: 1920,
        pendingResizeWidth: 430,
        gripWidth: 18);
    AssertEqual(1508, right.Left);
    AssertEqual(364, right.Width);
}

static void FullscreenCoverageAllowsTolerance()
{
    var monitor = new NativeRect(0, 0, 1920, 1080);
    var window = new NativeRect(2, 2, 1918, 1078);
    AssertTrue(FullscreenWindowRules.CoversMonitor(window, monitor));
}

static void FullscreenCoverageRejectsNonCoveringWindows()
{
    var monitor = new NativeRect(0, 0, 1920, 1080);
    var window = new NativeRect(3, 0, 1920, 1080);
    AssertFalse(FullscreenWindowRules.CoversMonitor(window, monitor));
}

static void ScreenshotAndShellFiltersAreCaseInsensitive()
{
    AssertTrue(FullscreenWindowRules.IsScreenshotProcessName("sharex"));
    AssertTrue(FullscreenWindowRules.ContainsScreenshotKeyword("Screen Clipping Overlay"));
    AssertTrue(FullscreenWindowRules.ContainsScreenshotKeyword("\u622A\u56FE"));
    AssertTrue(FullscreenWindowRules.IsShellWindowClassName("workerw"));
    AssertFalse(FullscreenWindowRules.IsShellWindowClassName("Chrome_WidgetWin_1"));
}

static void AssertTrue(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Expected true.");
    }
}

static void AssertFalse(bool condition)
{
    if (condition)
    {
        throw new InvalidOperationException("Expected false.");
    }
}

static void AssertEqual(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.0001)
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
