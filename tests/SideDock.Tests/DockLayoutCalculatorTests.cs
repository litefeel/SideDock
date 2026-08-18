using SideDock;

namespace SideDock.Tests;

public sealed class DockLayoutCalculatorTests
{
    [Fact]
    public void MaxExpandedWidthLeavesRoomForCollapsedRail()
    {
        Assert.Equal(1872, DockLayoutCalculator.GetMaxExpandedWidth(360, 1920, 48));
    }

    [Fact]
    public void MaxExpandedWidthNeverDropsBelowMinimum()
    {
        Assert.Equal(360, DockLayoutCalculator.GetMaxExpandedWidth(360, 320, 48));
    }

    [Fact]
    public void ExpandedWidthClampsToMinimumAndMaximum()
    {
        Assert.Equal(360, DockLayoutCalculator.ClampExpandedWidth(200, 360, 1872));
        Assert.Equal(430, DockLayoutCalculator.ClampExpandedWidth(430, 360, 1872));
        Assert.Equal(1872, DockLayoutCalculator.ClampExpandedWidth(2000, 360, 1872));
    }

    [Fact]
    public void ReservedWidthExpandsOnlyWhenPinnedAndExcludesResizeGrip()
    {
        Assert.Equal(48, DockLayoutCalculator.GetReservedWidth(isExpanded: false, isPinned: false, windowWidth: 430, collapsedWidth: 48, resizeGripWidth: 8));
        Assert.Equal(48, DockLayoutCalculator.GetReservedWidth(isExpanded: true, isPinned: false, windowWidth: 430, collapsedWidth: 48, resizeGripWidth: 8));
        Assert.Equal(422, DockLayoutCalculator.GetReservedWidth(isExpanded: true, isPinned: true, windowWidth: 430, collapsedWidth: 48, resizeGripWidth: 8));
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(120u)]
    [InlineData(144u)]
    public void PinnedReservedWidthExcludesPhysicalResizeGripAtScaledDpi(uint dpi)
    {
        var layout = new MonitorLayout(
            new NativeRect(0, 0, 2560, 1440),
            new NativeRect(0, 0, 2560, 1400),
            dpi);
        var windowWidth = 430d;
        var resizeGripWidth = 8d;
        var reservedWidth = DockLayoutCalculator.GetReservedWidth(
            isExpanded: true,
            isPinned: true,
            windowWidth,
            collapsedWidth: 48,
            resizeGripWidth);

        Assert.Equal(
            layout.DipsToPixels(resizeGripWidth),
            layout.DipsToPixels(windowWidth) - layout.DipsToPixels(reservedWidth));
    }

    [Fact]
    public void CurrentWindowWidthFollowsExpandedState()
    {
        Assert.Equal(48, DockLayoutCalculator.GetCurrentWindowWidth(isExpanded: false, expandedWidth: 430, collapsedWidth: 48));
        Assert.Equal(430, DockLayoutCalculator.GetCurrentWindowWidth(isExpanded: true, expandedWidth: 430, collapsedWidth: 48));
    }

    [Fact]
    public void DockLeftMatchesConfiguredEdge()
    {
        Assert.Equal(0, DockLayoutCalculator.GetDockLeft(AppDockSide.Left, monitorLeft: 0, monitorRight: 1920, windowWidth: 430));
        Assert.Equal(1490, DockLayoutCalculator.GetDockLeft(AppDockSide.Right, monitorLeft: 0, monitorRight: 1920, windowWidth: 430));
    }

    [Theory]
    [InlineData(120u)]
    [InlineData(144u)]
    public void RightDockStaysOnPhysicalRightEdgeAtScaledDpi(uint dpi)
    {
        var layout = new MonitorLayout(
            new NativeRect(0, 0, 2560, 1440),
            new NativeRect(0, 0, 2560, 1400),
            dpi);
        var monitorBounds = layout.MonitorDips;
        var windowWidth = 48;

        var left = DockLayoutCalculator.GetDockLeft(AppDockSide.Right, monitorBounds.Left, monitorBounds.Right, windowWidth);

        Assert.Equal(2560, layout.DipsToPixels(left + windowWidth));
    }

    [Fact]
    public void ResizeWidthFollowsDockSideDirection()
    {
        Assert.Equal(200, DockLayoutCalculator.GetRequestedResizeWidth(AppDockSide.Left, screenXDips: 300, resizeAnchorEdge: 100));
        Assert.Equal(200, DockLayoutCalculator.GetRequestedResizeWidth(AppDockSide.Right, screenXDips: 100, resizeAnchorEdge: 300));
    }

    [Fact]
    public void ResizePreviewLayoutMatchesDockMath()
    {
        var left = DockLayoutCalculator.GetResizePreviewLayout(
            AppDockSide.Left,
            screenLeft: 0,
            resizeContentFixedEdge: 48,
            resizeAnchorEdge: 0,
            pendingResizeWidth: 430,
            gripWidth: 8);
        Assert.Equal(48, left.Left);
        Assert.Equal(374, left.Width);

        var right = DockLayoutCalculator.GetResizePreviewLayout(
            AppDockSide.Right,
            screenLeft: 0,
            resizeContentFixedEdge: 1872,
            resizeAnchorEdge: 1920,
            pendingResizeWidth: 430,
            gripWidth: 8);
        Assert.Equal(1498, right.Left);
        Assert.Equal(374, right.Width);
    }
}
