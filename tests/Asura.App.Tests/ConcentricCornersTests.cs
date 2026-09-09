using Asura.App.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class ConcentricCornersTests
{
    /// <summary>
    /// Corners that changed as the page scrolled: the surface outside the
    /// scroll area was still being measured against, and the distance to it is
    /// the scroll position.
    /// </summary>
    [Fact]
    public void A_scroll_boundary_ends_the_search()
    {
        Assert.True(ConcentricCorners.StopsTheSearch(new ScrollViewer()));
        Assert.True(ConcentricCorners.StopsTheSearch(new ScrollContentPresenter()));
        Assert.False(ConcentricCorners.StopsTheSearch(new Grid()));
        Assert.False(ConcentricCorners.StopsTheSearch(new Border()));
    }

    [Fact]
    public void An_evenly_inset_child_steps_in_by_its_inset()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(8, 8),
            size: new Size(384, 284),
            minimumRadius: 2);

        Assert.NotNull(derived);
        Assert.Equal(new CornerRadius(18), derived!.Value);
    }

    [Fact]
    public void A_child_flush_against_its_container_keeps_the_whole_radius()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: default,
            size: new Size(400, 300),
            minimumRadius: 2);

        Assert.Equal(new CornerRadius(26), derived!.Value);
    }

    /// <summary>
    /// One surface one distance inside another takes one radius. Corner by
    /// corner gave a tall element two tight corners and two round ones, which
    /// makes a single shape read as two.
    /// </summary>
    [Fact]
    public void The_closest_edge_decides_every_corner()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(4, 10),
            size: new Size(380, 268),
            minimumRadius: 2);

        // Left 4, top 10, right 16, bottom 22: the nearest edge is 4 away.
        Assert.Equal(new CornerRadius(22), derived!.Value);
    }

    /// <summary>
    /// A sidebar under a tab strip: far from the window's top, hard against
    /// its left and bottom. The distance that counts is the one it actually
    /// stands off by, not the one the tab strip put above it.
    /// </summary>
    [Fact]
    public void A_sidebar_below_the_chrome_follows_the_edge_it_hugs()
    {
        var derived = ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(1400, 900),
            offsetInContainer: new Point(8, 120),
            size: new Size(200, 772),
            minimumRadius: 2);

        Assert.Equal(new CornerRadius(18), derived!.Value);
    }

    [Fact]
    public void An_element_standing_off_further_than_the_radius_is_left_alone() =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 10,
            containerSize: new Size(400, 300),
            offsetInContainer: new Point(40, 40),
            size: new Size(320, 220),
            minimumRadius: 3));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_square_container_has_nothing_to_derive_from(double outerRadius) =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius,
            new Size(400, 300),
            default,
            new Size(100, 100),
            minimumRadius: 2));

    [Fact]
    public void An_unarranged_element_is_left_alone() =>
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 26,
            containerSize: new Size(400, 300),
            offsetInContainer: default,
            size: default,
            minimumRadius: 2));

    /// <summary>
    /// A gap just short of the container's own radius earns a corner too tight
    /// to draw — a card eight points inside a nine-point panel would take a
    /// one-point corner and read square beside the seven-point controls it
    /// holds. The rule stands aside there rather than clamping to a floor
    /// nothing else in the interface uses.
    /// </summary>
    [Fact]
    public void ACornerTooTightToDrawKeepsWhatTheThemeGaveIt()
    {
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 9,
            containerSize: new Size(1200, 700),
            offsetInContainer: new Point(8, 8),
            size: new Size(400, 200),
            minimumRadius: 2));

        // A gap that still leaves a corner keeps deriving one.
        var derived = ConcentricCorners.Derive(
            outerRadius: 14,
            containerSize: new Size(1200, 700),
            offsetInContainer: new Point(8, 8),
            size: new Size(400, 200),
            minimumRadius: 2);
        Assert.Equal(new CornerRadius(6), derived);
    }

    /// <summary>
    /// A card whose content outgrew the panel hangs outside it. Clamping that
    /// overflow to zero read it as flush and handed it the container's whole
    /// radius, so it came out rounder than the cards beside it — which is how a
    /// row of identical cards stopped agreeing on their corners.
    /// </summary>
    [Fact]
    public void SomethingHangingOutsideItsContainerIsLeftAlone()
    {
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 9,
            containerSize: new Size(1200, 700),
            offsetInContainer: new Point(336, 640),
            size: new Size(509, 196),
            minimumRadius: 2));

        // The same card, inside the panel, derives from the edge it is
        // nearest — five points off the bottom here.
        Assert.Equal(
            new CornerRadius(21),
            ConcentricCorners.Derive(
                outerRadius: 26,
                containerSize: new Size(1200, 841),
                offsetInContainer: new Point(336, 640),
                size: new Size(509, 196),
                minimumRadius: 2));
    }

    /// <summary>
    /// A card cannot be tighter than the controls it holds. Eight points inside
    /// a thirteen-point panel earns a five-point corner — against the ten its
    /// own inputs are drawn with — and an input rounder than the card around it
    /// is the thing that reads as broken. The floor is the control corner, so
    /// the rule stands aside there and the card keeps the theme's.
    /// </summary>
    [Fact]
    public void ACardIsNeverTighterThanTheControlsItHolds()
    {
        // The profile whose controls are drawn at ten.
        Assert.Null(ConcentricCorners.Derive(
            outerRadius: 13,
            containerSize: new Size(1200, 700),
            offsetInContainer: new Point(8, 8),
            size: new Size(400, 200),
            minimumRadius: 10));

        // Nearer the container than its controls are round, it still derives.
        Assert.Equal(
            new CornerRadius(11),
            ConcentricCorners.Derive(
                outerRadius: 13,
                containerSize: new Size(1200, 700),
                offsetInContainer: new Point(2, 2),
                size: new Size(400, 200),
                minimumRadius: 10));
    }
}
