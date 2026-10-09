using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SideDock;

namespace SideDock.Tests;

public sealed class IconImagePolicyTests
{
    [Fact]
    public void MultiFrameIcoSelects64PixelFrameInsteadOfFirst16PixelFrame()
    {
        using var stream = CreateIco(16, 32, 64);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        Assert.Equal(3, decoder.Frames.Count);
        var selected = IconImagePolicy.SelectFrame(decoder.Frames);

        Assert.NotNull(selected);
        Assert.Equal(64, selected.PixelWidth);
        Assert.Equal(64, selected.PixelHeight);
    }

    [Fact]
    public void IcoWithoutHighResolutionFrameUsesLargestAvailableFrame()
    {
        using var stream = CreateIco(16, 32);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        Assert.Equal(32, IconImagePolicy.SelectFrame(decoder.Frames)!.PixelWidth);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(32, false)]
    [InlineData(48, true)]
    [InlineData(64, true)]
    public void ExistingLowResolutionIconsStillNeedUpgrade(int size, bool preferred)
    {
        Assert.Equal(preferred, IconImagePolicy.HasPreferredSize(CreateFrame(size)));
        Assert.False(IconImagePolicy.HasPreferredSize(null));
    }

    [Theory]
    [InlineData(64, 16, true)]
    [InlineData(64, 32, true)]
    [InlineData(32, 16, true)]
    [InlineData(16, 64, false)]
    [InlineData(64, 64, false)]
    public void CachePreservesHigherResolutionAndAllowsUpgrades(int existing, int incoming, bool keep)
    {
        Assert.Equal(keep, IconImagePolicy.ShouldKeepExisting(existing, existing, incoming, incoming));
    }

    [Fact]
    public void UnsafeFramesCannotDisplaceSafeIcon()
    {
        var frames = new[] { CreateFrame(2048), CreateFrame(64) };

        Assert.Same(frames[1], IconImagePolicy.SelectFrame(frames));
        Assert.Null(IconImagePolicy.SelectFrame([frames[0]]));
    }

    private static BitmapFrame CreateFrame(int size)
    {
        var pixels = new byte[size * size * 4];
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return BitmapFrame.Create(bitmap);
    }

    private static MemoryStream CreateIco(params int[] sizes)
    {
        var images = sizes.Select(size =>
        {
            using var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(CreateFrame(size));
            encoder.Save(png);
            return png.ToArray();
        }).ToArray();

        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var index = 0; index < sizes.Length; index++)
        {
            writer.Write((byte)sizes[index]);
            writer.Write((byte)sizes[index]);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[index].Length);
            writer.Write(offset);
            offset += images[index].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }

        stream.Position = 0;
        return stream;
    }
}
