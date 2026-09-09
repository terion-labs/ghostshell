using System.ComponentModel;
using System.Globalization;
using Asura.App.ViewModels;
using Asura.Core;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;

namespace Asura.App.Tests;

public sealed class RuntimeDockLayoutPersistenceTests
{
    [Theory]
    [InlineData(PanelSide.Left, Orientation.Horizontal)]
    [InlineData(PanelSide.Right, Orientation.Horizontal)]
    [InlineData(PanelSide.Top, Orientation.Vertical)]
    [InlineData(PanelSide.Bottom, Orientation.Vertical)]
    public void Adding_a_placeholder_to_an_edge_inserts_it_into_the_dock_tree(
        PanelSide side,
        Orientation orientation)
    {
        var tab = NewTab("edge-add");
        tab.AddPanel(Panel("original"));

        var placeholder = tab.AddPlaceholder(side);

        Assert.Contains(
            Enumerate(tab.DockLayout).OfType<IDocument>(),
            document => string.Equals(document.Id, placeholder.Id.Value
, StringComparison.Ordinal) && ReferenceEquals(document.Context, placeholder));
        Assert.Contains(
            Enumerate(tab.DockLayout).OfType<ProportionalDock>(),
            dock => dock.Orientation == orientation);
    }

    [Theory]
    [InlineData(PanelSplitOrientation.LeftRight, Orientation.Horizontal)]
    [InlineData(PanelSplitOrientation.TopBottom, Orientation.Vertical)]
    public void Splitting_with_a_placeholder_inserts_it_beside_the_target_document(
        PanelSplitOrientation split,
        Orientation orientation)
    {
        var tab = NewTab("panel-split");
        var original = Panel("original");
        tab.AddPanel(original);

        var placeholder = tab.SplitWithPlaceholder(original.Id, split);

        Assert.NotNull(placeholder);
        Assert.Contains(
            Enumerate(tab.DockLayout).OfType<IDocument>(),
            document => string.Equals(document.Id, placeholder!.Id.Value
, StringComparison.Ordinal) && ReferenceEquals(document.Context, placeholder));
        Assert.Contains(
            Enumerate(tab.DockLayout).OfType<ProportionalDock>(),
            dock => dock.Orientation == orientation);
    }

    [Fact]
    public void Removing_a_document_collapses_its_empty_layout_leaf()
    {
        var tab = NewTab("collapsing-leaf");
        var original = Panel("original-panel");
        tab.AddPanel(original);
        _ = tab.SplitWithPlaceholder(
            original.Id,
            PanelSplitOrientation.LeftRight);

        var document = Assert.Single(
            Enumerate(tab.DockLayout).OfType<IDocument>(),
            candidate => string.Equals(candidate.Id, original.Id.Value, StringComparison.Ordinal));

        tab.DockFactory.RemoveDockable(document, collapse: true);

        var remainingLeaf = Assert.Single(
            Enumerate(tab.DockLayout).OfType<IDocumentDock>());
        Assert.Single(remainingLeaf.VisibleDockables!);
        Assert.DoesNotContain(
            Enumerate(tab.DockLayout).OfType<IDocumentDock>(),
            leaf => leaf.VisibleDockables is not { Count: > 0 });
    }

    [Fact]
    public void Closing_a_branch_normalizes_the_surviving_dock_before_presentation()
    {
        var tab = NewTab("nested-close-reflow");
        var upperLeft = Panel("nested-close-upper-left");
        var right = Panel("nested-close-right");
        var lowerLeft = Panel("nested-close-lower-left");
        tab.AddPanel(upperLeft);
        Assert.True(tab.SplitActivePanel(right, PanelSplitOrientation.LeftRight));
        Assert.True(tab.ActivatePanel(upperLeft.Id));
        Assert.True(tab.SplitActivePanel(lowerLeft, PanelSplitOrientation.TopBottom));

        Assert.True(tab.RemovePanel(right.Id));

        var singletonDocks = Enumerate(tab.DockLayout)
            .OfType<IProportionalDock>()
            .Select(dock => (dock.VisibleDockables ?? [])
                .Where(dockable => dockable is not IProportionalDockSplitter)
                .ToArray())
            .Where(panes => panes.Length == 1)
            .ToArray();
        Assert.NotEmpty(singletonDocks);
        Assert.All(singletonDocks, panes =>
        {
            Assert.Equal(1d, panes[0].Proportion);
            Assert.Equal(1d, panes[0].CollapsedProportion);
        });
    }

    [Fact]
    public void Closing_a_nested_right_column_then_adding_right_leaves_no_empty_dock()
    {
        var tab = NewTab("nested-column-close-reflow");
        var upperLeft = Panel("nested-column-upper-left");
        tab.AddPanel(upperLeft);
        var lowerLeft = tab.SplitWithPlaceholder(
            upperLeft.Id,
            PanelSplitOrientation.TopBottom);
        Assert.NotNull(lowerLeft);

        var upperRight = tab.AddPlaceholder(PanelSide.Right);
        var lowerRight = tab.SplitWithPlaceholder(
            upperRight.Id,
            PanelSplitOrientation.TopBottom);
        Assert.NotNull(lowerRight);
        // Dock restores saved proportional containers as non-collapsible.
        foreach (var dock in Enumerate(tab.DockLayout).OfType<IProportionalDock>())
        {
            dock.IsCollapsable = false;
        }

        var publishedEmptyBranch = false;
        var publishedRevisionCount = 0;
        tab.PropertyChanged += (_, eventArgs) =>
        {
            if (string.Equals(
                    eventArgs.PropertyName,
                    nameof(RuntimeTabViewModel.DockLayoutRevision),
                    StringComparison.Ordinal))
            {
                publishedRevisionCount++;
                publishedEmptyBranch |= Enumerate(tab.DockLayout)
                    .OfType<IProportionalDock>()
                    .Any(dock => !(dock.VisibleDockables ?? [])
                        .Any(dockable => dockable is not IProportionalDockSplitter));
            }
        };

        Assert.True(tab.RemovePanel(lowerRight!.Id));
        Assert.True(tab.RemovePanel(upperRight.Id));
        var replacementRight = tab.AddPlaceholder(PanelSide.Right);

        Assert.False(publishedEmptyBranch);
        Assert.True(publishedRevisionCount >= 3);
        Assert.DoesNotContain(
            Enumerate(tab.DockLayout).OfType<IProportionalDock>(),
            dock => !(dock.VisibleDockables ?? [])
                .Any(dockable => dockable is not IProportionalDockSplitter));
        Assert.Equal(
            [upperLeft.Id, lowerLeft!.Id, replacementRight.Id],
            Enumerate(tab.DockLayout)
                .OfType<IDocument>()
                .Select(document => new PanelInstanceId(document.Id!)));
        AssertSelectionsBelongToLayout(tab.DockLayout);
    }

    [Fact]
    public void Restoring_a_layout_prunes_an_empty_noncollapsible_panel_branch()
    {
        var source = NewTab("empty-branch-recovery-source");
        var upperLeft = Panel("empty-branch-upper-left");
        source.AddPanel(upperLeft);
        var lowerLeft = source.SplitWithPlaceholder(
            upperLeft.Id,
            PanelSplitOrientation.TopBottom);
        Assert.NotNull(lowerLeft);
        var upperRight = source.AddPlaceholder(PanelSide.Right);
        var lowerRight = source.SplitWithPlaceholder(
            upperRight.Id,
            PanelSplitOrientation.TopBottom);
        Assert.NotNull(lowerRight);

        // Model the stale shape written by builds without topology repair.
        foreach (var dock in Enumerate(source.DockLayout).OfType<IProportionalDock>())
        {
            dock.IsCollapsable = false;
        }

        RemoveLeafWithoutNormalizing(source, lowerRight!.Id);
        RemoveLeafWithoutNormalizing(source, upperRight.Id);
        Assert.Contains(
            Enumerate(source.DockLayout).OfType<IProportionalDock>(),
            dock => !(dock.VisibleDockables ?? [])
                .Any(dockable => dockable is not IProportionalDockSplitter));

        var restored = NewTab(
            "empty-branch-recovery-target",
            source.SerializeDockLayout());

        Assert.DoesNotContain(
            Enumerate(restored.DockLayout).OfType<IProportionalDock>(),
            dock => !(dock.VisibleDockables ?? [])
                .Any(dockable => dockable is not IProportionalDockSplitter));
        Assert.Equal(
            [upperLeft.Id, lowerLeft!.Id],
            Enumerate(restored.DockLayout)
                .OfType<IDocument>()
                .Select(document => new PanelInstanceId(document.Id!)));
        AssertSelectionsBelongToLayout(restored.DockLayout);
    }

    [Fact]
    public void Adding_to_the_workspace_edge_wraps_an_existing_recursive_split()
    {
        var tab = NewTab("nested-edge-add");
        var first = Panel("first");
        var second = Panel("second");
        tab.AddPanel(first);
        Assert.True(tab.SplitActivePanel(second, PanelSplitOrientation.LeftRight));

        var placeholder = tab.AddPlaceholder(PanelSide.Right);

        var documents = Enumerate(tab.DockLayout).OfType<IDocument>().ToArray();
        Assert.Equal(3, documents.Length);
        Assert.Contains(documents, document => string.Equals(document.Id, first.Id.Value, StringComparison.Ordinal));
        Assert.Contains(documents, document => string.Equals(document.Id, second.Id.Value, StringComparison.Ordinal));
        Assert.Contains(documents, document => string.Equals(document.Id, placeholder.Id.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void Center_drop_between_occupied_leaves_swaps_panels_without_creating_tabs()
    {
        var tab = NewTab("center-swap");
        var leftPanel = Panel("left");
        var rightPanel = Panel("right");
        tab.AddPanel(leftPanel);
        Assert.True(tab.SplitActivePanel(rightPanel, PanelSplitOrientation.LeftRight));

        var documents = Enumerate(tab.DockLayout)
            .OfType<IDocument>()
            .ToDictionary(document => document.Id!, StringComparer.Ordinal);
        var leftDocument = documents[leftPanel.Id.Value];
        var rightDocument = documents[rightPanel.Id.Value];
        var leftLeaf = Assert.IsAssignableFrom<IDock>(leftDocument.Owner);
        var rightLeaf = Assert.IsAssignableFrom<IDock>(rightDocument.Owner);

        tab.DockFactory.MoveDockable(
            leftLeaf,
            rightLeaf,
            leftDocument,
            rightDocument);

        Assert.Same(rightLeaf, leftDocument.Owner);
        Assert.Same(leftLeaf, rightDocument.Owner);
        Assert.Same(leftDocument, Assert.Single(rightLeaf.VisibleDockables!));
        Assert.Same(rightDocument, Assert.Single(leftLeaf.VisibleDockables!));
        Assert.All(
            Enumerate(tab.DockLayout).OfType<IDocumentDock>(),
            leaf => Assert.Single(leaf.VisibleDockables!));
    }

    [Fact]
    public void Recovery_serializes_a_workspace_while_a_new_dock_panel_is_being_chosen()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Dock recovery",
            "Bronze",
            []);
        var tab = NewTab("recovery-edge-add");
        tab.AddPanel(Panel("original"));
        tab.AddPlaceholder(PanelSide.Right);
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;

        var json = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);

        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    [Fact]
    public void Serializing_a_live_layout_does_not_mutate_dock_ownership()
    {
        var tab = NewTab("owner-stability");
        var first = Panel("first");
        var second = Panel("second");
        tab.AddPanel(first);
        Assert.True(tab.SplitActivePanel(second, PanelSplitOrientation.LeftRight));

        var dockables = Enumerate(tab.DockLayout).ToArray();
        var owners = dockables.ToDictionary(dockable => dockable, dockable => dockable.Owner);
        var ownerChangeNotifications = 0;
        PropertyChangedEventHandler handler = (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.PropertyName, nameof(IDockable.Owner), StringComparison.Ordinal))
            {
                ownerChangeNotifications++;
            }
        };
        foreach (var dockable in dockables.OfType<INotifyPropertyChanged>())
        {
            dockable.PropertyChanged += handler;
        }

        try
        {
            var json = tab.SerializeDockLayout();

            Assert.False(string.IsNullOrWhiteSpace(json));
            Assert.Equal(0, ownerChangeNotifications);
            Assert.All(
                dockables,
                dockable => Assert.Same(owners[dockable], dockable.Owner));
        }
        finally
        {
            foreach (var dockable in dockables.OfType<INotifyPropertyChanged>())
            {
                dockable.PropertyChanged -= handler;
            }
        }
    }

    [Fact]
    public void Restored_layout_can_split_while_recovery_serializes_the_change()
    {
        var source = NewTab("restored-live-source");
        var original = Panel("original");
        source.AddPanel(original);

        var restored = NewTab("restored-live-target", source.SerializeDockLayout());
        var livePanel = Panel("live-panel");
        restored.AddPanel(livePanel, savedDockableId: original.Id.Value);
        restored.PropertyChanged += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.PropertyName, nameof(RuntimeTabViewModel.DockLayoutRevision), StringComparison.Ordinal))
            {
                _ = restored.SerializeDockLayout();
            }
        };

        var placeholder = restored.SplitWithPlaceholder(
            livePanel.Id,
            PanelSplitOrientation.LeftRight);

        Assert.NotNull(placeholder);
        var documents = Enumerate(restored.DockLayout).OfType<IDocument>().ToArray();
        Assert.Equal(2, documents.Length);
        Assert.All(documents, document => Assert.NotNull(document.Owner));
        Assert.Contains(
            documents,
            document => ReferenceEquals(document.Context, livePanel));
        Assert.Contains(
            documents,
            document => ReferenceEquals(document.Context, placeholder));
    }

    [Fact]
    public void Nested_splits_round_trip_and_rebind_to_new_runtime_panels()
    {
        var tab = NewTab("source-tab");
        var left = Panel("left");
        var upperRight = Panel("upper-right");
        var lowerRight = Panel("lower-right");

        tab.AddPanel(left);
        Assert.True(tab.SplitActivePanel(upperRight, PanelSplitOrientation.LeftRight));
        Assert.True(tab.SplitActivePanel(lowerRight, PanelSplitOrientation.TopBottom));

        var splitDocks = Enumerate(tab.DockLayout).OfType<ProportionalDock>().ToArray();
        Assert.Equal(2, splitDocks.Length);
        SetPaneProportions(splitDocks[0], 0.37, 0.63);
        SetPaneProportions(splitDocks[1], 0.58, 0.42);
        var expected = Signature(tab.DockLayout);

        var json = tab.SerializeDockLayout();
        var restored = NewTab("restored-tab", json);

        Assert.Equal(expected, Signature(restored.DockLayout));
        Assert.All(
            Enumerate(restored.DockLayout).OfType<IDocument>(),
            document => Assert.Null(document.Context));

        var restoredLeft = Panel("restored-left");
        var restoredUpperRight = Panel("restored-upper-right");
        var restoredLowerRight = Panel("restored-lower-right");
        restored.AddPanel(restoredLeft, savedDockableId: left.Id.Value);
        restored.AddPanel(restoredUpperRight, savedDockableId: upperRight.Id.Value);
        restored.AddPanel(restoredLowerRight, savedDockableId: lowerRight.Id.Value);

        var documents = Enumerate(restored.DockLayout).OfType<IDocument>().ToArray();
        Assert.Contains(documents, document => ReferenceEquals(document.Context, restoredLeft));
        Assert.Contains(documents, document => ReferenceEquals(document.Context, restoredUpperRight));
        Assert.Contains(documents, document => ReferenceEquals(document.Context, restoredLowerRight));
        Assert.All(documents, document =>
        {
            Assert.True(document.CanDrag);
            Assert.True(document.CanDrop);
            // Not Dock's floating: a panel holding an operating-system view
            // cannot change window without that view being destroyed, so the
            // shell floats panels inside its own window instead.
            Assert.False(document.CanFloat);
        });
    }

    [Fact]
    public void Recursive_layout_persistence_stays_compact_and_round_trips()
    {
        var tab = NewTab("compact-recursive-layout");
        tab.AddPanel(Panel("panel-00"));
        for (var index = 1; index < 16; index++)
        {
            Assert.True(tab.SplitActivePanel(
                Panel($"panel-{index:D2}"),
                index % 2 == 0
                    ? PanelSplitOrientation.LeftRight
                    : PanelSplitOrientation.TopBottom));
        }

        var encoded = tab.SerializeDockLayout();
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Compact recovery",
            "Bronze",
            []);
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        var recoveryPayload = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
        var restored = NewTab("compact-recursive-layout-restored", encoded);

        Assert.StartsWith("dock.br.1:", encoded, StringComparison.Ordinal);
        Assert.True(encoded.Length < 64 * 1024);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(recoveryPayload) < 256 * 1024);
        Assert.Equal(
            16,
            Enumerate(restored.DockLayout).OfType<IDocument>().Count());
    }

    [Fact]
    public void Floating_window_geometry_round_trips()
    {
        var tab = NewTab("floating-source");
        var panel = Panel("floating-panel");
        tab.AddPanel(panel);

        var document = Assert.Single(Enumerate(tab.DockLayout).OfType<IDocument>());
        tab.DockFactory.RemoveDockable(document, true);
        var window = Assert.IsAssignableFrom<IDockWindow>(
            tab.DockFactory.CreateWindowFrom(document));
        window.Id = "floating-window";
        window.X = 113;
        window.Y = 79;
        window.Width = 920;
        window.Height = 640;
        tab.DockLayout.Windows!.Add(window);

        var restored = NewTab("floating-restored", tab.SerializeDockLayout());
        var restoredWindow = Assert.Single(restored.DockLayout.Windows!);

        Assert.Equal("floating-window", restoredWindow.Id);
        Assert.Equal(113, restoredWindow.X);
        Assert.Equal(79, restoredWindow.Y);
        Assert.Equal(920, restoredWindow.Width);
        Assert.Equal(640, restoredWindow.Height);
        Assert.Single(Enumerate(restored.DockLayout).OfType<IDocument>());
    }

    [Fact]
    public void Floating_window_round_trip_reconnects_selection_and_panel_context()
    {
        var source = NewTab("floating-selection-source");
        var sourcePanel = Panel("floating-selection-panel");
        source.AddPanel(sourcePanel);

        var sourceDocument = Assert.Single(
            Enumerate(source.DockLayout).OfType<IDocument>());
        source.DockFactory.RemoveDockable(sourceDocument, collapse: true);
        var sourceWindow = Assert.IsAssignableFrom<IDockWindow>(
            source.DockFactory.CreateWindowFrom(sourceDocument));
        source.DockLayout.Windows!.Add(sourceWindow);

        var restored = NewTab(
            "floating-selection-restored",
            source.SerializeDockLayout());
        var restoredWindow = Assert.Single(restored.DockLayout.Windows!);
        var restoredWindowRoot = Assert.IsAssignableFrom<IRootDock>(restoredWindow.Layout);
        var restoredLeaf = Assert.IsAssignableFrom<IDock>(
            Assert.Single(restoredWindowRoot.VisibleDockables!));
        var restoredDocument = Assert.IsAssignableFrom<IDocument>(
            Assert.Single(restoredLeaf.VisibleDockables!));

        Assert.Same(restoredLeaf, restoredWindowRoot.ActiveDockable);
        Assert.Same(restoredLeaf, restoredWindowRoot.DefaultDockable);
        Assert.Same(restoredDocument, restoredLeaf.ActiveDockable);
        Assert.Same(restoredWindow, restoredWindowRoot.Window);
        Assert.Null(restoredWindow.Host);

        var restoredPanel = Panel("floating-selection-live-panel");
        restored.AddPanel(restoredPanel, savedDockableId: sourcePanel.Id.Value);

        Assert.Same(restoredPanel, restoredDocument.Context);
        Assert.Equal(restoredPanel.Id.Value, restoredDocument.Id);
    }

    /// <summary>
    /// Floating a panel used to be one-way: Dock can send a dockable out to a
    /// window of its own and has no word for the workspace it came from, so a
    /// panel that left could only return by being dragged onto a placement
    /// target — which, over a browser, is not drawn at all.
    /// </summary>
    [Fact]
    public void A_floated_panel_comes_back_to_the_workspace_it_left()
    {
        var tab = NewTab("dock-back");
        var staying = Panel("dock-back-staying");
        var leaving = Panel("dock-back-leaving");
        tab.AddPanel(staying);
        tab.AddPanel(leaving);

        var document = Enumerate(tab.DockLayout).OfType<IDocument>()
            .Single(candidate => string.Equals(candidate.Id, leaving.Id.Value, StringComparison.Ordinal));
        Assert.True(tab.FloatPanel(leaving.Id));

        // Out of the layout, still the workspace's, still the same document.
        Assert.DoesNotContain(
            Enumerate(tab.DockLayout).OfType<IDocument>(),
            candidate => ReferenceEquals(candidate, document));
        Assert.Same(document, Assert.Single(tab.FloatingPanels).Document);

        Assert.True(tab.DockPanel(leaving.Id));

        Assert.Empty(tab.FloatingPanels);
        Assert.Empty(tab.DockLayout.Windows!);
        // The same document, so the same panel and the same session: only the
        // geometry around it was rebuilt.
        Assert.Same(
            document,
            Enumerate(tab.DockLayout).OfType<IDocument>()
                .Single(candidate => string.Equals(candidate.Id, leaving.Id.Value, StringComparison.Ordinal)));
        Assert.Same(leaving, document.Context);
        var leaf = Assert.IsAssignableFrom<IDock>(document.Owner);
        Assert.Contains(document, leaf.VisibleDockables!);
        Assert.Same(document, leaf.ActiveDockable);
    }

    /// <summary>
    /// A panel comes back where it was, not merely somewhere. It is remembered by
    /// the panel it sat beside rather than by a position, so the rest of the
    /// workspace can be rearranged while it is away and the answer still means
    /// something.
    /// </summary>
    [Theory]
    [InlineData(PanelSplitOrientation.LeftRight, Orientation.Horizontal)]
    [InlineData(PanelSplitOrientation.TopBottom, Orientation.Vertical)]
    public void A_returning_panel_takes_the_side_it_left_from(
        PanelSplitOrientation split,
        Orientation orientation)
    {
        var tab = NewTab($"dock-back-side-{orientation}");
        var first = Panel("dock-back-side-first");
        tab.AddPanel(first);
        var second = tab.SplitWithPlaceholder(first.Id, split);
        Assert.NotNull(second);

        var before = DocumentOrder(tab, orientation);
        Assert.True(tab.FloatPanel(second!.Id));
        Assert.True(tab.IsPanelFloating(second.Id));

        Assert.True(tab.DockPanel(second.Id));

        Assert.False(tab.IsPanelFloating(second.Id));
        Assert.Equal(before, DocumentOrder(tab, orientation));
    }

    /// <summary>
    /// A panel is a sibling at every level above it, and each level can name a
    /// different neighbour for it. Only the innermost says where it actually
    /// sits: a browser above a statistics panel, in a column beside a terminal,
    /// is "above statistics" — the level above calls the same panel "left of the
    /// terminal", and returning it there makes it a column of its own, which is
    /// what it did.
    /// </summary>
    [Fact]
    public void A_returning_panel_takes_its_place_in_the_column_not_beside_it()
    {
        var tab = NewTab("dock-back-nested");
        var terminal = Panel("dock-back-nested-terminal");
        tab.AddPanel(terminal);
        var browser = tab.SplitWithPlaceholder(
            terminal.Id,
            PanelSplitOrientation.LeftRight);
        Assert.NotNull(browser);
        var statistics = tab.SplitWithPlaceholder(
            browser!.Id,
            PanelSplitOrientation.TopBottom);
        Assert.NotNull(statistics);

        var column = Enumerate(tab.DockLayout).OfType<ProportionalDock>()
            .Single(dock => dock.Orientation == Orientation.Vertical);
        var before = ColumnOrder(column);
        Assert.Equal([browser.Id.Value, statistics!.Id.Value], before);

        Assert.True(tab.FloatPanel(browser.Id));

        Assert.True(tab.DockPanel(browser.Id));

        var restored = Enumerate(tab.DockLayout).OfType<ProportionalDock>()
            .Single(dock => dock.Orientation == Orientation.Vertical);
        Assert.Equal(before, ColumnOrder(restored));
    }

    private static string[] ColumnOrder(IDock column) =>
        [.. (column.VisibleDockables ?? [])
            .Select(FirstDocumentIn)
            .Where(id => id is not null)
            .Select(id => id!)];

    /// <summary>
    /// The panel it named has been closed while it was away. It still comes back.
    /// </summary>
    [Fact]
    public void A_returning_panel_settles_for_any_place_when_its_neighbour_has_gone()
    {
        var tab = NewTab("dock-back-lost-neighbour");
        var staying = Panel("dock-back-lost-staying");
        var leaving = Panel("dock-back-lost-leaving");
        var neighbour = Panel("dock-back-lost-neighbour-panel");
        tab.AddPanel(staying);
        tab.AddPanel(neighbour);
        tab.AddPanel(leaving);

        var document = Enumerate(tab.DockLayout).OfType<IDocument>()
            .Single(candidate => string.Equals(candidate.Id, leaving.Id.Value, StringComparison.Ordinal));
        Assert.True(tab.FloatPanel(leaving.Id));
        tab.RemovePanel(neighbour.Id);

        Assert.True(tab.DockPanel(leaving.Id));
        Assert.Contains(
            Enumerate(tab.DockLayout).OfType<IDocument>(),
            candidate => ReferenceEquals(candidate, document));
    }

    private static string[] DocumentOrder(RuntimeTabViewModel tab, Orientation orientation)
    {
        var split = Enumerate(tab.DockLayout).OfType<ProportionalDock>()
            .First(dock => dock.Orientation == orientation);
        return [.. (split.VisibleDockables ?? [])
            .Select(FirstDocumentIn)
            .Where(id => id is not null)
            .Select(id => id!)];
    }

    private static string? FirstDocumentIn(IDockable dockable) => dockable switch
    {
        IDocument document => document.Id,
        IDock dock => (dock.VisibleDockables ?? [])
            .Select(FirstDocumentIn)
            .FirstOrDefault(found => found is not null),
        _ => null,
    };

    [Fact]
    public void A_panel_that_never_left_cannot_be_docked_back()
    {
        var tab = NewTab("dock-back-noop");
        var panel = Panel("dock-back-noop-panel");
        tab.AddPanel(panel);

        Assert.False(tab.IsPanelFloating(panel.Id));
        Assert.False(tab.DockPanel(panel.Id));
    }

    private static RuntimeTabViewModel NewTab(string id, string? dockLayoutJson = null) =>
        new(
            new TabInstanceId(id),
            "Tab",
            "test",
            dockLayoutJson is null
                ? null
                : new LayoutDefinition(
                    new LayoutId($"{id}-layout"),
                    LayoutDefinition.CurrentSchemaVersion,
                    "Layout",
                    new LayoutGrid(1, 1),
                    [],
                    dockLayoutJson));

    private static UnavailableRuntimePanelViewModel Panel(string id) =>
        new(
            new PanelInstanceId(id),
            PanelKind.Terminal,
            id,
            "LOCAL",
            "unavailable");

    private static void RemoveLeafWithoutNormalizing(
        RuntimeTabViewModel tab,
        PanelInstanceId panelId)
    {
        var document = Assert.Single(
            Enumerate(tab.DockLayout).OfType<IDocument>(),
            candidate => string.Equals(candidate.Id, panelId.Value, StringComparison.Ordinal));
        var leaf = Assert.IsAssignableFrom<IDock>(document.Owner);
        tab.DockFactory.RemoveDockable(leaf, collapse: true);
    }

    private static void AssertSelectionsBelongToLayout(IRootDock root)
    {
        var dockables = Enumerate(root).ToArray();
        foreach (var dock in dockables.OfType<IDock>())
        {
            var children = (dock.VisibleDockables ?? [])
                .Where(dockable => dockable is not IProportionalDockSplitter)
                .ToArray();
            if (children.Length == 0)
            {
                Assert.Null(dock.ActiveDockable);
                Assert.Null(dock.DefaultDockable);
                continue;
            }

            Assert.Contains(children, child => ReferenceEquals(child, dock.ActiveDockable));
            if (dock.DefaultDockable is { } defaultDockable)
            {
                Assert.Contains(children, child => ReferenceEquals(child, defaultDockable));
            }
        }

        if (root.FocusedDockable is { } focused)
        {
            Assert.Contains(dockables, dockable => ReferenceEquals(dockable, focused));
        }
    }

    private static void SetPaneProportions(
        ProportionalDock dock,
        double first,
        double second)
    {
        var panes = dock.VisibleDockables!
            .Where(dockable => dockable is not ProportionalDockSplitter)
            .ToArray();
        Assert.Equal(2, panes.Length);
        panes[0].Proportion = first;
        panes[1].Proportion = second;
    }

    private static string Signature(IRootDock root) =>
        string.Join("|", Enumerate(root).Select(dockable => dockable switch
        {
            ProportionalDock dock =>
                $"split:{dock.Orientation}:"
                + string.Join(
                    ",",
                    dock.VisibleDockables!
                        .Where(item => item is not ProportionalDockSplitter)
                        .Select(item => item.Proportion.ToString("0.####", CultureInfo.InvariantCulture))),
            IDocument document => $"document:{document.Id}",
            _ => $"{dockable.GetType().Name}:{dockable.Id}",
        }));

    private static IEnumerable<IDockable> Enumerate(IRootDock root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        foreach (var window in root.Windows ?? [])
        {
            if (window.Layout is not null)
            {
                pending.Push(window.Layout);
            }
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is not IDock dock || dock.VisibleDockables is null)
            {
                continue;
            }

            for (var index = dock.VisibleDockables.Count - 1; index >= 0; index--)
            {
                pending.Push(dock.VisibleDockables[index]);
            }
        }
    }
}
