using Asura.App.Views.Components;
using Avalonia;

namespace Asura.App.Tests;

/// <summary>
/// The zoom and pan arithmetic on its own, away from a rendering platform: the
/// gestures are only as good as these numbers.
/// </summary>
public sealed class ZoomableImageGeometryTests
{
    [Fact]
    public void A_picture_larger_than_the_view_is_scaled_down_to_fit()
    {
        var scale = ZoomableImageGeometry.FitScale(
            new Size(2000, 1000), new Size(400, 400), angle: 0);

        Assert.Equal(0.2, scale, 3);
    }

    [Fact]
    public void A_picture_smaller_than_the_view_is_left_at_its_own_size()
    {
        var scale = ZoomableImageGeometry.FitScale(
            new Size(120, 80), new Size(400, 400), angle: 0);

        Assert.Equal(1d, scale, 3);
    }

    [Fact]
    public void Turning_a_picture_a_quarter_fits_it_to_the_other_side()
    {
        // Upright the wide picture is limited by width; on its side it is
        // limited by what was its width and is now its height.
        var upright = ZoomableImageGeometry.FitScale(
            new Size(1000, 500), new Size(500, 1000), angle: 0);
        var turned = ZoomableImageGeometry.FitScale(
            new Size(1000, 500), new Size(500, 1000), angle: 90);

        Assert.Equal(0.5, upright, 3);
        Assert.Equal(1d, turned, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void A_half_turn_fits_the_same_way_as_none(int angle) =>
        Assert.Equal(
            ZoomableImageGeometry.FitScale(new Size(800, 400), new Size(200, 200), 0),
            ZoomableImageGeometry.FitScale(new Size(800, 400), new Size(200, 200), angle),
            3);

    [Fact]
    public void An_empty_picture_or_view_has_nothing_to_fit()
    {
        Assert.Equal(1d, ZoomableImageGeometry.FitScale(default, new Size(400, 400), 0));
        Assert.Equal(1d, ZoomableImageGeometry.FitScale(new Size(400, 400), default, 0));
    }

    [Fact]
    public void Zooming_keeps_the_point_under_the_pointer_under_the_pointer()
    {
        var centre = new Point(200, 150);
        var pointer = new Point(320, 90);
        var offset = new Vector(10, -20);
        const double factor = 2d;

        var zoomed = ZoomableImageGeometry.AnchoredOffset(offset, pointer, centre, factor);

        // Where the pointer sat, in picture space, before and after: the same
        // spot, or the wheel magnifies the middle instead of the detail.
        var before = (pointer - centre - offset) / 1d;
        var after = (pointer - centre - zoomed) / factor;
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void Zooming_about_the_middle_leaves_a_centred_picture_centred()
    {
        var offset = ZoomableImageGeometry.AnchoredOffset(
            default, new Point(200, 150), new Point(200, 150), 1.25);

        Assert.Equal(0d, offset.X, 6);
        Assert.Equal(0d, offset.Y, 6);
    }

    [Fact]
    public void A_picture_cannot_be_dragged_out_of_sight()
    {
        var clamped = ZoomableImageGeometry.ClampOffset(
            new Vector(10_000, -10_000),
            new Size(400, 200),
            new Size(300, 300),
            angle: 0,
            scale: 1d);

        // Half the picture plus half the view: an edge reaches the middle and
        // stops, so there is always something left to grab.
        Assert.Equal(350, clamped.X, 3);
        Assert.Equal(-250, clamped.Y, 3);
    }

    [Fact]
    public void A_modest_pan_is_left_alone()
    {
        var offset = new Vector(24, -12);

        Assert.Equal(
            offset,
            ZoomableImageGeometry.ClampOffset(
                offset, new Size(400, 200), new Size(300, 300), 0, 1d));
    }

    [Fact]
    public void Panning_a_turned_picture_is_bounded_by_its_turned_shape()
    {
        var clamped = ZoomableImageGeometry.ClampOffset(
            new Vector(10_000, 10_000),
            new Size(400, 200),
            new Size(300, 300),
            angle: 90,
            scale: 1d);

        Assert.Equal(250, clamped.X, 3);
        Assert.Equal(350, clamped.Y, 3);
    }
}
