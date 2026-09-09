using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// Adding a panel used to be a modal over the whole window that asked what to open
/// and then put it wherever the layout appended things. Placing first and choosing
/// second means the panel lands where the user pointed.
/// </summary>
public sealed class PanelPlacementTests
{
    private static RuntimeTabViewModel Tab(int panels = 1)
    {
        var tab = new RuntimeTabViewModel(new TabInstanceId("tab"), "Tab", "source");
        for (var index = 0; index < panels; index++)
        {
            tab.AddPanel(new UnavailableRuntimePanelViewModel(
                new PanelInstanceId($"panel-{index}"),
                PanelKind.Terminal,
                $"Panel {index}",
                "LOCAL",
                "unavailable"));
        }

        return tab;
    }

    [Theory]
    [InlineData(PanelSide.Left, 0)]
    [InlineData(PanelSide.Right, 1)]
    public void A_panel_added_to_a_side_takes_a_new_column_there(PanelSide side, int column)
    {
        var tab = Tab();

        var placeholder = tab.AddPlaceholder(side);

        Assert.Equal(2, tab.Columns);
        Assert.Equal(column, placeholder.LayoutColumn);
        Assert.Equal(column == 0 ? 1 : 0, tab.Panels[0].LayoutColumn);
    }

    [Theory]
    [InlineData(PanelSide.Top, 0)]
    [InlineData(PanelSide.Bottom, 1)]
    public void A_panel_added_above_or_below_takes_a_new_row(PanelSide side, int row)
    {
        var tab = Tab();

        var placeholder = tab.AddPlaceholder(side);

        Assert.Equal(2, tab.Rows);
        Assert.Equal(row, placeholder.LayoutRow);
    }

    [Fact]
    public void Splitting_a_panel_puts_the_new_one_beside_it()
    {
        var tab = Tab();
        var original = tab.Panels[0];

        var placeholder = tab.SplitWithPlaceholder(original.Id, PanelSplitOrientation.LeftRight);

        Assert.NotNull(placeholder);
        Assert.Equal(2, tab.Columns);
        Assert.Equal(original.LayoutColumn + 1, placeholder!.LayoutColumn);
        Assert.Equal(original.LayoutRow, placeholder.LayoutRow);
    }

    /// <summary>
    /// The point of placing first: the created panel inherits the cell rather than
    /// being appended somewhere else.
    /// </summary>
    [Fact]
    public void Choosing_what_to_open_puts_the_panel_in_the_placeholder_cell()
    {
        var tab = Tab();
        var placeholder = tab.AddPlaceholder(PanelSide.Right);
        var column = placeholder.LayoutColumn;
        var row = placeholder.LayoutRow;

        tab.ReplaceTarget = placeholder.Id;
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            new PanelInstanceId("chosen"),
            PanelKind.Terminal,
            "Chosen",
            "LOCAL",
            "unavailable"));

        Assert.DoesNotContain(tab.Panels, panel => panel is PanelPlaceholderViewModel);
        var chosen = Assert.Single(tab.Panels, panel => string.Equals(panel.Id.Value, "chosen", StringComparison.Ordinal));
        Assert.Equal(column, chosen.LayoutColumn);
        Assert.Equal(row, chosen.LayoutRow);
        Assert.Null(tab.ReplaceTarget);

        // The panel takes the placeholder's cell through its layout, but it is
        // appended like the session host appends it. The two lists are compared
        // index by index, so slotting it into the placeholder's position would put
        // them out of step and every later receipt would be rejected.
        Assert.Same(chosen, tab.Panels[^1]);
    }

    /// <summary>
    /// A split divides the panel's own cell. Everything else keeps the area it had,
    /// which for a panel that shared the split panel's columns means stretching
    /// across the new track. Opening a track without that left a full-height column
    /// running through the whole grid and a hole beside the panels above it.
    /// </summary>
    [Theory]
    [InlineData(PanelSplitOrientation.LeftRight)]
    [InlineData(PanelSplitOrientation.TopBottom)]
    public void Splitting_a_panel_leaves_every_cell_covered_exactly_once(
        PanelSplitOrientation orientation)
    {
        var tab = Tab(2);
        var target = tab.Panels[1];

        Assert.NotNull(tab.SplitWithPlaceholder(target.Id, orientation));

        AssertEveryCellCoveredOnce(tab);
    }

    [Theory]
    [InlineData(PanelSide.Left)]
    [InlineData(PanelSide.Right)]
    [InlineData(PanelSide.Top)]
    [InlineData(PanelSide.Bottom)]
    public void Placing_a_panel_against_an_edge_leaves_every_cell_covered_once(
        PanelSide side)
    {
        var tab = Tab(2);

        tab.AddPlaceholder(side);

        AssertEveryCellCoveredOnce(tab);
    }

    /// <summary>
    /// The layout is a grid, so every cell must belong to exactly one panel: a cell
    /// covered by none is a hole, and one covered by two is an overlap.
    /// </summary>
    private static void AssertEveryCellCoveredOnce(RuntimeTabViewModel tab)
    {
        var columns = tab.Panels.Max(panel => panel.LayoutColumns);
        var rows = tab.Panels.Max(panel => panel.LayoutRows);
        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                var cell = (column, row);
                var covering = tab.Panels.Count(panel =>
                    panel.LayoutColumn <= cell.column
                    && cell.column < panel.LayoutColumn + Math.Max(1, panel.LayoutColumnSpan)
                    && panel.LayoutRow <= cell.row
                    && cell.row < panel.LayoutRow + Math.Max(1, panel.LayoutRowSpan));
                Assert.True(
                    covering == 1,
                    $"Cell ({cell.column},{cell.row}) is covered by {covering} panels.");
            }
        }
    }

    /// <summary>
    /// The reported sequence: three columns, split the middle one across, then split
    /// the first one across twice. Splitting the first used to append a row to the
    /// whole grid, which pushed the second column's two panels into overlapping each
    /// other, and left the first column as a half and two quarters instead of thirds.
    /// </summary>
    [Fact]
    public void Splitting_a_column_repeatedly_keeps_the_other_columns_intact()
    {
        var tab = Tab();
        var first = tab.Panels[0];
        var second = tab.SplitWithPlaceholder(first.Id, PanelSplitOrientation.LeftRight);
        Assert.NotNull(second);
        var third = tab.SplitWithPlaceholder(second!.Id, PanelSplitOrientation.LeftRight);
        Assert.NotNull(third);
        AssertEveryCellCoveredOnce(tab);

        // The middle column is divided top from bottom.
        Assert.NotNull(tab.SplitWithPlaceholder(second.Id, PanelSplitOrientation.TopBottom));
        AssertEveryCellCoveredOnce(tab);

        // Now the first column, twice. Neither may disturb the other columns.
        Assert.NotNull(tab.SplitWithPlaceholder(first.Id, PanelSplitOrientation.TopBottom));
        AssertEveryCellCoveredOnce(tab);
        Assert.NotNull(tab.SplitWithPlaceholder(first.Id, PanelSplitOrientation.TopBottom));
        AssertEveryCellCoveredOnce(tab);

        // Three panels stacked in the first column, each one row tall: thirds, not a
        // half and two quarters.
        var stacked = tab.Panels
            .Where(panel => panel.LayoutColumn == first.LayoutColumn)
            .ToArray();
        Assert.Equal(3, stacked.Length);
        Assert.Equal(3, tab.Rows);
        Assert.All(stacked, panel => Assert.Equal(1, Math.Max(1, panel.LayoutRowSpan)));
        Assert.All(tab.RowWeights, weight => Assert.Equal(1d / 3d, weight, 6));
    }

    /// <summary>
    /// Closing a panel has to give its cell back. Dropping empty tracks does not do
    /// it on its own — a cell freed in the middle of the grid sits in rows and
    /// columns other panels still occupy, so nothing is empty and the hole stays.
    /// </summary>
    [Theory]
    [InlineData(PanelSplitOrientation.LeftRight)]
    [InlineData(PanelSplitOrientation.TopBottom)]
    public void Closing_a_split_panel_gives_its_cell_back(
        PanelSplitOrientation orientation)
    {
        var tab = Tab();
        var kept = tab.Panels[0];
        var added = tab.SplitWithPlaceholder(kept.Id, orientation);
        Assert.NotNull(added);

        Assert.True(tab.RemovePanel(added!.Id));

        var survivor = Assert.Single(tab.Panels);
        Assert.Same(kept, survivor);

        // The survivor covers the whole canvas again. Whether the track boundary is
        // still there does not matter and is deliberately preserved elsewhere, so
        // this asserts the area rather than the track count.
        Assert.Equal(0, survivor.LayoutColumn);
        Assert.Equal(0, survivor.LayoutRow);
        Assert.Equal(tab.Columns, Math.Max(1, survivor.LayoutColumnSpan));
        Assert.Equal(tab.Rows, Math.Max(1, survivor.LayoutRowSpan));
        AssertEveryCellCoveredOnce(tab);
    }

    /// <summary>
    /// The same, but with the freed cell inside the grid rather than at its edge:
    /// the neighbouring column still occupies both rows, so no track becomes empty
    /// and only a neighbour growing into the cell can close the gap.
    /// </summary>
    [Fact]
    public void Closing_a_panel_beside_a_taller_column_gives_its_cell_back()
    {
        var tab = Tab();
        var left = tab.Panels[0];
        var right = tab.SplitWithPlaceholder(left.Id, PanelSplitOrientation.LeftRight);
        Assert.NotNull(right);
        var leftBottom = tab.SplitWithPlaceholder(left.Id, PanelSplitOrientation.TopBottom);
        Assert.NotNull(leftBottom);
        AssertEveryCellCoveredOnce(tab);

        Assert.True(tab.RemovePanel(leftBottom!.Id));

        Assert.Equal(2, tab.Panels.Count);
        AssertEveryCellCoveredOnce(tab);
    }

    /// <summary>
    /// A placed cell is a panel, and selecting it selects it — on both sides. It
    /// was once withheld from the host, which meant the two had to agree on when
    /// they were allowed to disagree; selection then snapped back to the cell
    /// after the user had clicked past it.
    /// </summary>
    [Fact]
    public void A_placed_cell_becomes_the_active_panel()
    {
        var tab = Tab();
        var panel = tab.Panels[0];
        Assert.True(tab.ActivatePanel(panel.Id));

        var placeholder = tab.AddPlaceholder(PanelSide.Right);

        Assert.Same(placeholder, tab.ActivePanel);
        Assert.Equal(placeholder.Id, tab.ActivePanelId);
    }

    [Fact]
    public void Splitting_a_panel_that_is_not_there_does_nothing()
    {
        var tab = Tab();

        Assert.Null(tab.SplitWithPlaceholder(new PanelInstanceId("missing"), PanelSplitOrientation.TopBottom));
        Assert.Single(tab.Panels);
    }

    /// <summary>
    /// A placeholder is a cell the user has placed but not yet filled, so it exists
    /// only on this side of the session host. Activating and discarding one has to
    /// work without the host, because the host has never been told the panel exists
    /// — sending it there fails as not_found, and that failure used to cost the
    /// workspace its keyboard authority.
    /// </summary>
    [Fact]
    public void A_placeholder_can_be_activated_without_the_session_host()
    {
        var tab = Tab();
        var placeholder = tab.AddPlaceholder(PanelSide.Right);

        Assert.True(tab.ActivatePanel(placeholder.Id));
        Assert.Same(placeholder, tab.ActivePanel);
    }

    /// <summary>
    /// The session host's graph does not contain placeholders, so a projection that
    /// omits one is correct and must validate. Counting the placeholder made the
    /// client's tab look a panel wider than the host's, and after a few splits every
    /// activation came back reading as an invalid receipt.
    /// </summary>
    [Fact]
    public void A_host_projection_carries_the_placed_cell()
    {
        var tab = Tab();
        var placeholder = tab.AddPlaceholder(PanelSide.Right);
        Assert.Same(placeholder, tab.ActivePanel);

        // The cell is a panel the host holds, so a projection that leaves it out
        // describes a different tab and must be refused rather than absorbed.
        Assert.Throws<InvalidOperationException>(() => tab.ValidateHostProjection(
            new TabInstance(
                new TabInstanceId("tab"),
                "Tab",
                [new PanelInstance(new PanelInstanceId("panel-0"), PanelKind.Terminal, "Panel 0")],
                new PanelInstanceId("panel-0"))));

        var projection = new TabInstance(
            new TabInstanceId("tab"),
            "Tab",
            [
                new PanelInstance(new PanelInstanceId("panel-0"), PanelKind.Terminal, "Panel 0"),
                new PanelInstance(placeholder.Id, PanelKind.Placeholder, placeholder.Title),
            ],
            placeholder.Id);

        tab.ValidateHostProjection(projection);
        tab.ApplyHostProjection(projection);

        Assert.Same(placeholder, tab.ActivePanel);
    }

    [Fact]
    public void A_placeholder_can_be_discarded_without_the_session_host()
    {
        var tab = Tab();
        var placeholder = tab.AddPlaceholder(PanelSide.Bottom);

        Assert.True(tab.RemovePanel(placeholder.Id));
        Assert.DoesNotContain(tab.Panels, panel => panel is PanelPlaceholderViewModel);
    }
}
