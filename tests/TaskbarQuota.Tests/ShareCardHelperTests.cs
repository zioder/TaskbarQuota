using TaskbarQuota.Services;
using Windows.UI;

namespace TaskbarQuota.Tests;

public class ShareCardHelperTests
{
    [Fact]
    public void CompositeOverBackground_FullyTransparentPixel_BecomesBackground()
    {
        // BGRA, premultiplied: fully transparent black.
        byte[] pixels = { 0, 0, 0, 0 };

        ShareCardHelper.CompositeOverBackground(pixels, Color.FromArgb(255, 32, 32, 32));

        Assert.Equal(new byte[] { 32, 32, 32, 255 }, pixels);
    }

    [Fact]
    public void CompositeOverBackground_FullyOpaquePixel_IsUnchanged()
    {
        byte[] pixels = { 10, 20, 30, 255 };

        ShareCardHelper.CompositeOverBackground(pixels, Color.FromArgb(255, 255, 255, 255));

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, pixels);
    }

    [Fact]
    public void CompositeOverBackground_HalfTransparentWhite_OverDarkBackground_Blends()
    {
        // Premultiplied white at 50% alpha: channels = 255 * 128/255 = 128.
        byte[] pixels = { 128, 128, 128, 128 };

        ShareCardHelper.CompositeOverBackground(pixels, Color.FromArgb(255, 0, 0, 0));

        // out = 128 + 0 * 127/255 = 128, alpha forced opaque.
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, pixels);
    }

    [Fact]
    public void CompositeOverBackground_SemiTransparentPixel_AccumulatesBackground()
    {
        byte[] pixels = { 0, 0, 0, 0 };

        ShareCardHelper.CompositeOverBackground(pixels, Color.FromArgb(255, 250, 250, 250));

        Assert.Equal(new byte[] { 250, 250, 250, 255 }, pixels);
    }
}
