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
    public void ReservedWidthExpandsOnlyWhenPinned()
    {
        Assert.Equal(48, DockLayoutCalculator.GetReservedWidth(isExpanded: false, isPinned: false, windowWidth: 430, collapsedWidth: 48));
        Assert.Equal(48, DockLayoutCalculator.GetReservedWidth(isExpanded: true, isPinned: false, windowWidth: 430, collapsedWidth: 48));
        Assert.Equal(430, DockLayoutCalculator.GetReservedWidth(isExpanded: true, isPinned: true, windowWidth: 430, collapsedWidth: 48));
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
            gripWidth: 18);
        Assert.Equal(48, left.Left);
        Assert.Equal(364, left.Width);

        var right = DockLayoutCalculator.GetResizePreviewLayout(
            AppDockSide.Right,
            screenLeft: 0,
            resizeContentFixedEdge: 1872,
            resizeAnchorEdge: 1920,
            pendingResizeWidth: 430,
            gripWidth: 18);
        Assert.Equal(1508, right.Left);
        Assert.Equal(364, right.Width);
    }
}
