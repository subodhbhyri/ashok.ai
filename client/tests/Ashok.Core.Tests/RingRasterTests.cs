using Ashok.Core.Overlay;

namespace Ashok.Core.Tests;

public class RingRasterTests
{
    private const int Width = 120, Height = 60, Thickness = 6, Radius = 10;
    private static readonly int Opaque = unchecked((int)0xFFFF00AA);

    private static readonly int[] Pixels = RingRaster.Render(Width, Height, Thickness, Radius, 0xFF00AA);

    private static int At(int x, int y) => Pixels[y * Width + x];

    [Fact]
    public void Straight_edges_are_opaque_from_the_outermost_row_through_the_band()
    {
        Assert.Equal(Opaque, At(Width / 2, 0));
        Assert.Equal(Opaque, At(Width / 2, Thickness - 1));
        Assert.Equal(Opaque, At(Width / 2, Height - 1));
        Assert.Equal(Opaque, At(0, Height / 2));
        Assert.Equal(Opaque, At(Width - 1, Height / 2));
    }

    [Fact]
    public void Inside_the_band_and_the_rounded_corners_are_transparent()
    {
        Assert.Equal(0, At(Width / 2, Thickness));
        Assert.Equal(0, At(Width / 2, Height / 2));
        Assert.Equal(0, At(0, 0));
        Assert.Equal(0, At(Width - 1, Height - 1));
    }

    [Fact]
    public void Pixels_are_premultiplied()
    {
        foreach (var p in Pixels)
        {
            var a = (p >> 24) & 0xFF;
            Assert.True(((p >> 16) & 0xFF) <= a && ((p >> 8) & 0xFF) <= a && (p & 0xFF) <= a);
        }
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void Empty_sizes_give_no_pixels(int w, int h) =>
        Assert.Empty(RingRaster.Render(w, h, Thickness, Radius, 0xFF00AA));

    [Fact]
    public void A_box_smaller_than_twice_the_band_is_filled_solid() =>
        Assert.Equal(Opaque, RingRaster.Render(8, 8, Thickness, Radius, 0xFF00AA)[4 * 8 + 4]);
}
