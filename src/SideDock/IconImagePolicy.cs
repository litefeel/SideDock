using System.Windows.Media.Imaging;

namespace SideDock;

internal static class IconImagePolicy
{
    internal const int PreferredSize = 48;
    internal const int MaxSourceDimension = 1024;
    internal const long MaxSourcePixels = 1024 * 1024;

    internal static bool HasPreferredSize(BitmapSource? icon)
    {
        return icon is not null && icon.PixelWidth >= PreferredSize && icon.PixelHeight >= PreferredSize;
    }

    internal static bool IsSafeFrame(BitmapSource frame)
    {
        return frame.PixelWidth > 0 && frame.PixelHeight > 0
            && frame.PixelWidth <= MaxSourceDimension && frame.PixelHeight <= MaxSourceDimension
            && (long)frame.PixelWidth * frame.PixelHeight <= MaxSourcePixels;
    }

    internal static BitmapFrame? SelectFrame(IEnumerable<BitmapFrame> frames)
    {
        var safeFrames = frames.Where(IsSafeFrame).ToArray();
        return safeFrames.Where(HasPreferredSize)
            .OrderBy(frame => Math.Max(frame.PixelWidth, frame.PixelHeight))
            .ThenByDescending(frame => frame.Format.BitsPerPixel)
            .FirstOrDefault()
            ?? safeFrames.OrderByDescending(frame => Math.Min(frame.PixelWidth, frame.PixelHeight))
                .ThenByDescending(frame => frame.Format.BitsPerPixel)
                .FirstOrDefault();
    }

    internal static bool ShouldKeepExisting(int width, int height, int newWidth, int newHeight)
    {
        // Keep the best available detail even when neither image reaches the preferred size.
        return Math.Min(width, height) > Math.Min(newWidth, newHeight);
    }
}
