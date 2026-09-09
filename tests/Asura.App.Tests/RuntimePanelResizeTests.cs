using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// The canvas divided itself evenly and had no way to say otherwise, so panels
/// could not be resized at all — the split was fixed when the layout was chosen
/// and never again. These pin the track weights that replaced the even division.
/// </summary>
public sealed class RuntimePanelResizeTests
{
    private static RuntimeTabViewModel Tab(int panels)
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

    [Fact]
    public void Tracks_start_evenly_divided()
    {
        var tab = Tab(2);

        Assert.All(tab.ColumnWeights, weight => Assert.Equal(1d / tab.ColumnWeights.Count, weight, 6));
        Assert.Equal(1, tab.ColumnWeights.Sum(), 6);
    }

    [Fact]
    public void Moving_a_boundary_takes_from_one_track_and_gives_to_the_other()
    {
        var tab = Tab(2);
        if (tab.ColumnWeights.Count < 2)
        {
            return;
        }

        var before = tab.ColumnWeights.ToArray();
        Assert.True(tab.MoveColumnSplit(0, 100, 1_000));

        Assert.Equal(before[0] + 0.1, tab.ColumnWeights[0], 6);
        Assert.Equal(before[1] - 0.1, tab.ColumnWeights[1], 6);
        Assert.Equal(1, tab.ColumnWeights.Sum(), 6);
    }

    /// <summary>
    /// A drag past the limit stops at it rather than being refused, which is what
    /// makes dragging feel like it is against a wall instead of broken.
    /// </summary>
    [Fact]
    public void A_boundary_stops_at_the_minimum_instead_of_collapsing_a_track()
    {
        var tab = Tab(2);
        if (tab.ColumnWeights.Count < 2)
        {
            return;
        }

        Assert.True(tab.MoveColumnSplit(0, 5_000, 1_000));

        Assert.Equal(0.78, tab.ColumnWeights[0], 6);
        Assert.Equal(0.22, tab.ColumnWeights[1], 6);
    }

    [Fact]
    public void Window_resize_does_not_make_the_next_drag_jump()
    {
        var tab = Tab(2);
        Assert.True(tab.MoveColumnSplit(0, 5_000, 1_000));
        var atLargeViewport = tab.ColumnWeights.ToArray();

        // At 300 px the requested panel minimums no longer fit. A drag farther
        // into the invalid side is refused without rewriting the stored split.
        Assert.False(tab.MoveColumnSplit(0, 10, 300));
        Assert.Equal(atLargeViewport, tab.ColumnWeights);

        // Reversing direction moves by exactly the new pointer delta; it does not
        // jump to a limit derived from the resized window.
        Assert.True(tab.MoveColumnSplit(0, -10, 300));
        Assert.Equal(atLargeViewport[0] - (10d / 300), tab.ColumnWeights[0], 6);
        Assert.Equal(atLargeViewport[1] + (10d / 300), tab.ColumnWeights[1], 6);
    }

    [Fact]
    public void A_spanning_panel_minimum_constrains_its_whole_span()
    {
        var leftSlot = new LayoutSlotDefinition(
            new LayoutSlotId("left"),
            new LayoutGridBounds(0, 0, 2, 1),
            new LayoutMinimumSize(300, 140));
        var rightSlot = new LayoutSlotDefinition(
            new LayoutSlotId("right"),
            new LayoutGridBounds(2, 0, 1, 1),
            new LayoutMinimumSize(150, 140));
        var tab = new RuntimeTabViewModel(
            new TabInstanceId("spanning-tab"),
            "Spanning",
            "source",
            new LayoutDefinition(
                new LayoutId("spanning-layout"),
                LayoutDefinition.CurrentSchemaVersion,
                "Spanning",
                new LayoutGrid(3, 1),
                [leftSlot, rightSlot]));
        tab.AddPanel(
            new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("left-panel"),
                PanelKind.Terminal,
                "Left",
                "LOCAL",
                "unavailable"),
            leftSlot.Id);
        tab.AddPanel(
            new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("right-panel"),
                PanelKind.Terminal,
                "Right",
                "LOCAL",
                "unavailable"),
            rightSlot.Id);

        Assert.True(tab.MoveColumnSplit(1, -5_000, 600));
        Assert.Equal(0.5, tab.ColumnWeights.Take(2).Sum(), 6);

        Assert.True(tab.MoveColumnSplit(1, 5_000, 600));
        Assert.Equal(0.75, tab.ColumnWeights.Take(2).Sum(), 6);
    }

    [Fact]
    public void Uneven_panel_minimums_use_the_exact_required_canvas()
    {
        var leftSlot = new LayoutSlotDefinition(
            new LayoutSlotId("small"),
            new LayoutGridBounds(0, 0, 1, 1),
            new LayoutMinimumSize(100, 140));
        var rightSlot = new LayoutSlotDefinition(
            new LayoutSlotId("large"),
            new LayoutGridBounds(1, 0, 1, 1),
            new LayoutMinimumSize(500, 140));
        var tab = new RuntimeTabViewModel(
            new TabInstanceId("uneven-tab"),
            "Uneven",
            "source",
            new LayoutDefinition(
                new LayoutId("uneven-layout"),
                LayoutDefinition.CurrentSchemaVersion,
                "Uneven",
                new LayoutGrid(2, 1),
                [leftSlot, rightSlot]));
        tab.AddPanel(
            new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("small-panel"),
                PanelKind.Terminal,
                "Small",
                "LOCAL",
                "unavailable"),
            leftSlot.Id);
        tab.AddPanel(
            new UnavailableRuntimePanelViewModel(
                new PanelInstanceId("large-panel"),
                PanelKind.Terminal,
                "Large",
                "LOCAL",
                "unavailable"),
            rightSlot.Id);

        Assert.Equal(600, tab.MinimumCanvasWidth);
        Assert.True(tab.MoveColumnSplit(0, -5_000, 600));
        Assert.Equal(1d / 6, tab.ColumnWeights[0], 6);
        Assert.Equal(5d / 6, tab.ColumnWeights[1], 6);
    }

    /// <summary>
    /// Closing a panel used to leave its track behind, so the survivor kept half
    /// the canvas and the next panel was appended past the hole — which is why
    /// adding one after closing one divided the canvas into three with a gap.
    /// </summary>
    [Fact]
    public void Closing_a_panel_gives_its_space_to_the_ones_that_remain()
    {
        var tab = Tab(2);
        var first = tab.Panels[0].Id;

        Assert.True(tab.RemovePanel(first));

        var survivor = Assert.Single(tab.Panels);
        Assert.Equal(1, tab.Columns);
        Assert.Equal(1, tab.Rows);
        Assert.Equal(0, survivor.LayoutColumn);
        Assert.Equal(0, survivor.LayoutRow);
        Assert.Equal(1, survivor.LayoutColumnSpan);
        Assert.Equal(1, survivor.LayoutRowSpan);
    }

    [Fact]
    public void A_panel_added_after_a_close_lands_beside_the_survivor()
    {
        var tab = Tab(2);
        Assert.True(tab.RemovePanel(tab.Panels[0].Id));

        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            new PanelInstanceId("panel-added"),
            PanelKind.Terminal,
            "Added",
            "LOCAL",
            "unavailable"));

        // Two panels, two tracks, no empty one between them.
        Assert.Equal(2, tab.Panels.Count);
        Assert.Equal(2, tab.Rows * tab.Columns);
        Assert.Equal(
            [0, 1],
            [.. tab.Panels.Select(panel => panel.LayoutRow * tab.Columns + panel.LayoutColumn).Order()]);
    }

    [Fact]
    public void A_boundary_that_does_not_exist_is_refused()
    {
        var tab = Tab(2);

        Assert.False(tab.MoveColumnSplit(-1, 10, 500));
        Assert.False(tab.MoveColumnSplit(99, 10, 500));
        Assert.False(tab.MoveColumnSplit(0, double.NaN, 500));
        Assert.False(tab.MoveColumnSplit(0, 10, double.NaN));
        Assert.False(tab.MoveColumnSplit(0, 10, 0));
    }
}
