using Ashok.Core.Overlay;

namespace Ashok.Core.Tests;

public class OverlayTests
{
    [Fact]
    public void FromBounds_maps_x_y_width_height() =>
        Assert.Equal(new ScreenRect(24, 310, 260, 40), ScreenRect.FromBounds([24, 310, 260, 40]));

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void FromBounds_rejects_the_wrong_length(int length) =>
        Assert.Throws<ArgumentException>(() => ScreenRect.FromBounds(new int[length]));

    [Fact]
    public void Right_and_bottom_edges()
    {
        var r = new ScreenRect(10, 20, 100, 50);

        Assert.Equal(110, r.Right);
        Assert.Equal(70, r.Bottom);
    }

    [Fact]
    public void Inflate_grows_every_side_and_allows_negative_origins() =>
        Assert.Equal(new ScreenRect(-1930, -4, 112, 62), new ScreenRect(-1920, 6, 92, 42).Inflate(10));

    [Fact]
    public void Outer_box_is_the_target_inflated_by_padding_plus_thickness()
    {
        var target = new ScreenRect(100, 200, 300, 80);

        var outer = OverlayGeometry.OuterBox(target);

        var d = OverlayGeometry.RingPaddingPx + OverlayGeometry.RingThicknessPx;
        Assert.Equal(new ScreenRect(100 - d, 200 - d, 300 + 2 * d, 80 + 2 * d), outer);
    }
}
