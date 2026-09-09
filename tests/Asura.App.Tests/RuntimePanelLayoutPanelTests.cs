using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class RuntimePanelLayoutPanelTests
{
    [Fact]
    public void Saved_screen_preview_uses_the_definition_geometry()
    {
        var left = new LauncherScreenPanelPreviewViewModel(3, 2, 0, 0, 1, 2, true);
        var upperRight = new LauncherScreenPanelPreviewViewModel(3, 2, 1, 0, 2, 1, false);
        var lowerRight = new LauncherScreenPanelPreviewViewModel(3, 2, 1, 1, 2, 1, false);
        var leftControl = new Border { DataContext = left };
        var upperControl = new Border { DataContext = upperRight };
        var lowerControl = new Border { DataContext = lowerRight };
        var preview = new ScreenLayoutPreviewPanel
        {
            Children =
            {
                leftControl,
                upperControl,
                lowerControl,
            },
        };

        preview.Measure(new Size(300, 120));
        preview.Arrange(new Rect(0, 0, 300, 120));

        Assert.Equal(new Rect(0, 0, 100, 120), leftControl.Bounds);
        Assert.Equal(new Rect(100, 0, 200, 60), upperControl.Bounds);
        Assert.Equal(new Rect(100, 60, 200, 60), lowerControl.Bounds);
    }

    [Fact]
    public void Durable_layout_preserves_slot_spans_and_minimum_canvas_size()
    {
        var firstSlot = new LayoutSlotDefinition(
            new LayoutSlotId("left"),
            new LayoutGridBounds(0, 0, 2, 1),
            new LayoutMinimumSize(300, 100));
        var secondSlot = new LayoutSlotDefinition(
            new LayoutSlotId("right"),
            new LayoutGridBounds(2, 0, 1, 2),
            new LayoutMinimumSize(150, 250));
        var layout = new LayoutDefinition(
            new LayoutId("layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Operations",
            new LayoutGrid(3, 2),
            [firstSlot, secondSlot]);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Screen",
            "SAVED SCREEN",
            layout);
        var first = new TestRuntimePanel("first");
        var second = new TestRuntimePanel("second");
        tab.AddPanel(first, firstSlot.Id);
        tab.AddPanel(second, secondSlot.Id);

        var firstControl = new Border { DataContext = first };
        var secondControl = new Border { DataContext = second };
        var panel = new RuntimePanelLayoutPanel
        {
            Children =
            {
                firstControl,
                secondControl,
            },
        };
        panel.Measure(new Size(900, 400));
        panel.Arrange(new Rect(0, 0, 900, 400));

        Assert.Equal(3, tab.Columns);
        Assert.Equal(2, tab.Rows);
        Assert.Equal(450, tab.MinimumCanvasWidth);
        Assert.Equal(250, tab.MinimumCanvasHeight);
        Assert.Equal(new Rect(0, 0, 600, 200), firstControl.Bounds);
        Assert.Equal(new Rect(600, 0, 300, 400), secondControl.Bounds);
    }

    [Fact]
    public void Constrained_canvas_compresses_instead_of_exceeding_the_window()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Compact", "WORKSPACE TAB");
        var first = new TestRuntimePanel("first");
        var second = new TestRuntimePanel("second");
        tab.AddPanel(first);
        tab.AddPanel(second);
        var firstControl = new Border { DataContext = first };
        var secondControl = new Border { DataContext = second };
        var layout = new RuntimePanelLayoutPanel
        {
            Tab = tab,
            Children =
            {
                firstControl,
                secondControl,
            },
        };

        layout.Measure(new Size(300, 100));
        layout.Arrange(new Rect(0, 0, 300, 100));

        Assert.Equal(new Size(300, 100), layout.DesiredSize);
        Assert.Equal(new Rect(0, 0, 150, 100), firstControl.Bounds);
        Assert.Equal(new Rect(150, 0, 150, 100), secondControl.Bounds);
    }

    [Fact]
    public void Ad_hoc_tabs_reflow_and_saved_tabs_append_without_mutating_existing_geometry()
    {
        var automatic = new RuntimeTabViewModel(TabInstanceId.New(), "Ad hoc", "WORKSPACE TAB");
        automatic.AddPanel(new TestRuntimePanel("one"));
        automatic.AddPanel(new TestRuntimePanel("two"));
        automatic.AddPanel(new TestRuntimePanel("three"));

        Assert.Equal((2, 2), (automatic.Columns, automatic.Rows));
        Assert.Equal((0, 0),
            (automatic.Panels[0].LayoutColumn, automatic.Panels[0].LayoutRow));
        Assert.Equal((0, 1),
            (automatic.Panels[2].LayoutColumn, automatic.Panels[2].LayoutRow));

        var slot = new LayoutSlotDefinition(
            new LayoutSlotId("whole"),
            new LayoutGridBounds(0, 0, 2, 1),
            new LayoutMinimumSize(440, 140));
        var saved = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Saved",
            "SAVED SCREEN",
            new LayoutDefinition(
                new LayoutId("saved-layout"),
                LayoutDefinition.CurrentSchemaVersion,
                "Saved",
                new LayoutGrid(2, 1),
                [slot]));
        var original = new TestRuntimePanel("original");
        saved.AddPanel(original, slot.Id);
        saved.AddPanel(new TestRuntimePanel("extra"));

        Assert.Equal((0, 0, 2, 1),
            (original.LayoutColumn, original.LayoutRow, original.LayoutColumnSpan, original.LayoutRowSpan));
        Assert.Equal(2, saved.Rows);
        Assert.Equal((0, 1, 2, 1),
            (saved.Panels[1].LayoutColumn,
                saved.Panels[1].LayoutRow,
                saved.Panels[1].LayoutColumnSpan,
                saved.Panels[1].LayoutRowSpan));
    }

    [Fact]
    public void Activating_a_panel_keeps_exactly_one_panel_selected()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Split", "SAVED SCREEN");
        var first = new TestRuntimePanel("first");
        var second = new TestRuntimePanel("second");
        tab.AddPanel(first);
        tab.AddPanel(second);

        Assert.Equal(first.Id, tab.ActivePanelId);
        Assert.Same(first, tab.ActivePanel);
        Assert.True(first.IsActive);
        Assert.False(second.IsActive);

        Assert.True(tab.ActivatePanel(second.Id));
        Assert.Equal(second.Id, tab.ActivePanelId);
        Assert.Same(second, tab.ActivePanel);
        Assert.False(first.IsActive);
        Assert.True(second.IsActive);

        Assert.False(tab.ActivatePanel(new PanelInstanceId("missing-panel")));
        Assert.Equal(second.Id, tab.ActivePanelId);
    }

    [Fact]
    public void Closing_the_selected_panel_in_a_split_removes_that_panel_only()
    {
        var leftSlot = new LayoutSlotDefinition(
            new LayoutSlotId("left"),
            new LayoutGridBounds(0, 0, 1, 1),
            new LayoutMinimumSize(220, 140));
        var rightSlot = new LayoutSlotDefinition(
            new LayoutSlotId("right"),
            new LayoutGridBounds(1, 0, 1, 1),
            new LayoutMinimumSize(220, 140));
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Split",
            "SAVED SCREEN",
            new LayoutDefinition(
                new LayoutId("split-layout"),
                LayoutDefinition.CurrentSchemaVersion,
                "Split",
                new LayoutGrid(2, 1),
                [leftSlot, rightSlot]));
        var left = new TestRuntimePanel("left");
        var right = new TestRuntimePanel("right");
        tab.AddPanel(left, leftSlot.Id);
        tab.AddPanel(right, rightSlot.Id);
        tab.ActivatePanel(right.Id);

        var closeTarget = Assert.IsType<TestRuntimePanel>(tab.ActivePanel);
        Assert.True(tab.RemovePanel(closeTarget.Id));

        Assert.True(right.IsDisposed);
        Assert.False(left.IsDisposed);
        Assert.Equal([left], tab.Panels);
        Assert.Equal(left.Id, tab.ActivePanelId);
        Assert.True(left.IsActive);
    }

    [Fact]
    public void ClosingOneHalfOfAManualSplitCollapsesTheRecursiveSplit()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Split", "WORKSPACE TAB");
        var original = new TestRuntimePanel("original");
        var split = new TestRuntimePanel("split");
        tab.AddPanel(original);

        Assert.True(tab.SplitActivePanel(split, PanelSplitOrientation.LeftRight));
        Assert.Equal(2, Dockables(tab.DockLayout).OfType<IDocument>().Count());

        Assert.True(tab.RemovePanel(split.Id));

        var survivor = Assert.Single(Dockables(tab.DockLayout).OfType<IDocument>());
        Assert.Equal(original.Id.Value, survivor.Id);
        Assert.Empty(Dockables(tab.DockLayout).OfType<ProportionalDockSplitter>());
    }

    [Fact]
    public void ClosingATemporarySplitRestoresTheSavedRecursiveShape()
    {
        var leftSlot = new LayoutSlotDefinition(
            new LayoutSlotId("left"),
            new LayoutGridBounds(0, 0, 1, 1),
            new LayoutMinimumSize(220, 140));
        var rightSlot = new LayoutSlotDefinition(
            new LayoutSlotId("right"),
            new LayoutGridBounds(1, 0, 1, 1),
            new LayoutMinimumSize(220, 140));
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Saved",
            "SAVED SCREEN",
            new LayoutDefinition(
                new LayoutId("saved-split"),
                LayoutDefinition.CurrentSchemaVersion,
                "Saved",
                new LayoutGrid(2, 1),
                [leftSlot, rightSlot]));
        var left = new TestRuntimePanel("left");
        var right = new TestRuntimePanel("right");
        var temporary = new TestRuntimePanel("temporary");
        tab.AddPanel(left, leftSlot.Id);
        tab.AddPanel(right, rightSlot.Id);
        tab.ActivatePanel(left.Id);

        tab.SplitActivePanel(temporary, PanelSplitOrientation.LeftRight);
        tab.RemovePanel(temporary.Id);

        Assert.Equal(
            new[] { left.Id.Value, right.Id.Value }.Order(StringComparer.Ordinal),
            Dockables(tab.DockLayout).OfType<IDocument>().Select(item => item.Id).Order(StringComparer.Ordinal));
        var savedSplit = Assert.Single(Dockables(tab.DockLayout).OfType<ProportionalDock>());
        Assert.Equal(Orientation.Horizontal, savedSplit.Orientation);
    }

    [Fact]
    public void ClosingBesideANestedSplitKeepsTheSiblingSubtree()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Nested", "WORKSPACE TAB");
        var left = new TestRuntimePanel("left");
        var upperRight = new TestRuntimePanel("upper-right");
        var lowerRight = new TestRuntimePanel("lower-right");
        tab.AddPanel(left);
        tab.SplitActivePanel(upperRight, PanelSplitOrientation.LeftRight);
        tab.SplitActivePanel(lowerRight, PanelSplitOrientation.TopBottom);

        Assert.True(tab.RemovePanel(left.Id));

        Assert.Equal(
            new[] { upperRight.Id.Value, lowerRight.Id.Value }.Order(StringComparer.Ordinal),
            Dockables(tab.DockLayout).OfType<IDocument>().Select(item => item.Id).Order(StringComparer.Ordinal));
        var survivingSplit = Assert.Single(
            Dockables(tab.DockLayout).OfType<ProportionalDock>(),
            dock => dock.Orientation == Orientation.Vertical
                && dock.VisibleDockables?.Count(
                    item => item is not ProportionalDockSplitter) == 2);
        Assert.Equal(Orientation.Vertical, survivingSplit.Orientation);
    }

    [Fact]
    public void DirectionalAndNextFocusSelectTheExpectedPanel()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Focus", "WORKSPACE TAB");
        var left = new TestRuntimePanel("left");
        var upperRight = new TestRuntimePanel("upper-right");
        var lowerRight = new TestRuntimePanel("lower-right");
        tab.AddPanel(left);
        tab.SplitActivePanel(upperRight, PanelSplitOrientation.LeftRight);
        tab.SplitActivePanel(lowerRight, PanelSplitOrientation.TopBottom);

        Assert.True(tab.FocusPanel(PanelFocusDirection.Up));
        Assert.Same(upperRight, tab.ActivePanel);
        Assert.True(tab.FocusPanel(PanelFocusDirection.Left));
        Assert.Same(left, tab.ActivePanel);
        Assert.True(tab.FocusPanel(PanelFocusDirection.Right));
        Assert.Same(upperRight, tab.ActivePanel);
        Assert.True(tab.FocusPanel(PanelFocusDirection.Down));
        Assert.Same(lowerRight, tab.ActivePanel);
        Assert.True(tab.FocusPanel(PanelFocusDirection.Next));
        Assert.Same(left, tab.ActivePanel);
    }

    [Fact]
    public void ZoomMakesOnlyTheActivePanelVisibleAndArrangesItAcrossTheCanvas()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Zoom", "WORKSPACE TAB");
        var first = new TestRuntimePanel("first");
        var second = new TestRuntimePanel("second");
        tab.AddPanel(first);
        tab.AddPanel(second);
        var firstControl = new Border { DataContext = first };
        var secondControl = new Border { DataContext = second };
        var layout = new RuntimePanelLayoutPanel
        {
            Children =
            {
                firstControl,
                secondControl,
            },
        };

        Assert.True(tab.ToggleActivePanelZoom());
        layout.Measure(new Size(800, 400));
        layout.Arrange(new Rect(0, 0, 800, 400));

        Assert.True(first.IsZoomed);
        Assert.True(first.IsVisibleInLayout);
        Assert.False(second.IsVisibleInLayout);
        Assert.Equal(new Rect(0, 0, 800, 400), firstControl.Bounds);

        Assert.True(tab.ToggleActivePanelZoom());
        Assert.True(first.IsVisibleInLayout);
        Assert.True(second.IsVisibleInLayout);
    }

    [Fact]
    public void RuntimeTabRenameTrimsTheTitleAndRejectsBlankNames()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Before", "WORKSPACE TAB");

        Assert.True(tab.Rename("  After  "));
        Assert.Equal("After", tab.Title);
        Assert.False(tab.Rename("   "));
        Assert.Equal("After", tab.Title);
    }

    private static IEnumerable<IDockable> Dockables(IRootDock root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is not IDock { VisibleDockables: { } children })
            {
                continue;
            }

            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }

    private sealed class TestRuntimePanel(string title)
        : RuntimePanelViewModel(PanelInstanceId.New(), PanelKind.Terminal, title, "TEST")
    {
        public bool IsDisposed { get; private set; }

        public override void Dispose() => IsDisposed = true;
    }
}
