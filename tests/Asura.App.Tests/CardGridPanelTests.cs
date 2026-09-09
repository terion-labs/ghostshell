using Asura.App.Controls;
using Avalonia;
using Avalonia.Controls;

namespace Asura.App.Tests;

/// <summary>
/// The launcher's card grid. Its job is that every card is the same size
/// regardless of how many there are — a profile with one saved screen should not
/// get a card shaped differently from a profile with eight.
/// </summary>
public sealed class CardGridPanelTests
{
    private static CardGridPanel Grid(int cards, double width)
    {
        var panel = new CardGridPanel { MinItemWidth = 256, Spacing = 14 };
        for (var index = 0; index < cards; index++)
        {
            panel.Children.Add(new Border { Height = 100 });
        }

        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        return panel;
    }

    private static double CardWidth(CardGridPanel panel) => panel.Children[0].Bounds.Width;

    /// <summary>
    /// The column count used to be clamped to the number of cards, so a single
    /// card was given the whole row — a metre-wide bar where a card belonged.
    /// </summary>
    [Fact]
    public void One_card_is_not_stretched_across_the_whole_row()
    {
        var single = CardWidth(Grid(cards: 1, width: 1132));

        Assert.True(
            single < 400,
            $"A lone card took {single:0} px of a 1132 px row.");
    }

    [Fact]
    public void Card_width_does_not_depend_on_how_many_cards_there_are()
    {
        var one = CardWidth(Grid(cards: 1, width: 1132));
        var four = CardWidth(Grid(cards: 4, width: 1132));
        var nine = CardWidth(Grid(cards: 9, width: 1132));

        Assert.Equal(four, one, precision: 3);
        Assert.Equal(four, nine, precision: 3);
    }

    [Fact]
    public void Cards_never_go_narrower_than_the_minimum()
    {
        Assert.True(CardWidth(Grid(cards: 6, width: 1132)) >= 256);
        Assert.True(CardWidth(Grid(cards: 6, width: 600)) >= 256);
    }

    /// <summary>
    /// A row narrower than one card still shows the card, filling the row rather
    /// than overflowing it.
    /// </summary>
    [Fact]
    public void A_row_too_narrow_for_the_minimum_still_fits_one_card()
    {
        Assert.Equal(200, CardWidth(Grid(cards: 3, width: 200)), precision: 3);
    }

    /// <summary>
    /// The row ends flush, which is the reason this panel exists rather than a
    /// wrap panel. Layout rounds to device pixels, so "flush" is within a pixel.
    /// </summary>
    [Fact]
    public void Cards_fill_the_row_edge_to_edge()
    {
        const double width = 1132;
        var panel = Grid(cards: 4, width: width);
        var columns = (int)Math.Floor((width + 14) / (256d + 14));
        var last = panel.Children[columns - 1];

        Assert.True(
            Math.Abs(last.Bounds.Right - width) <= 1,
            $"The last card in the row ended at {last.Bounds.Right:0.##}, not {width}.");
    }
}
