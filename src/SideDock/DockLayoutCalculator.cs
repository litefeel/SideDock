namespace SideDock;

internal static class DockLayoutCalculator
{
    public static double GetMaxExpandedWidth(double minExpandedWidth, double screenWidth, double collapsedWidth)
    {
        return Math.Max(minExpandedWidth, screenWidth - collapsedWidth);
    }

    public static double ClampExpandedWidth(double width, double minExpandedWidth, double maxExpandedWidth)
    {
        return Math.Clamp(width, minExpandedWidth, maxExpandedWidth);
    }

    public static double GetReservedWidth(bool isExpanded, bool isPinned, double windowWidth, double collapsedWidth)
    {
        return isExpanded && isPinned
            ? windowWidth
            : collapsedWidth;
    }

    public static double GetCurrentWindowWidth(bool isExpanded, double expandedWidth, double collapsedWidth)
    {
        return isExpanded ? expandedWidth : collapsedWidth;
    }

    public static double GetDockLeft(AppDockSide dockSide, double monitorLeft, double monitorRight, double windowWidth)
    {
        return dockSide == AppDockSide.Left
            ? monitorLeft
            : monitorRight - windowWidth;
    }

    public static double GetRequestedResizeWidth(AppDockSide dockSide, double screenXDips, double resizeAnchorEdge)
    {
        return dockSide == AppDockSide.Left
            ? screenXDips - resizeAnchorEdge
            : resizeAnchorEdge - screenXDips;
    }

    public static ResizePreviewLayout GetResizePreviewLayout(
        AppDockSide dockSide,
        double screenLeft,
        double resizeContentFixedEdge,
        double resizeAnchorEdge,
        double pendingResizeWidth,
        double gripWidth)
    {
        var previewLeft = dockSide == AppDockSide.Left
            ? Math.Round(resizeContentFixedEdge - screenLeft)
            : Math.Round(resizeAnchorEdge - pendingResizeWidth + gripWidth - screenLeft);
        var previewRight = dockSide == AppDockSide.Left
            ? Math.Round(resizeAnchorEdge + pendingResizeWidth - gripWidth - screenLeft)
            : Math.Round(resizeContentFixedEdge - screenLeft);

        return new ResizePreviewLayout(previewLeft, Math.Max(1, previewRight - previewLeft));
    }
}

internal readonly record struct ResizePreviewLayout(double Left, double Width);
