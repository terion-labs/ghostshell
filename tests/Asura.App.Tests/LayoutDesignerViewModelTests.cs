using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class LayoutDesignerViewModelTests
{
    [Fact]
    public void Existing_layout_starts_clean_and_preserves_save_identity()
    {
        var definition = ThreePanelLayout();
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 17);

        var request = editor.CreateSaveRequest();

        Assert.False(editor.IsNew);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanSave);
        Assert.Equal(LayoutDesignerCancelDisposition.Close, editor.RequestCancel());
        Assert.Equal(definition.Id, request.Definition.Id);
        Assert.Equal(definition.SchemaVersion, request.Definition.SchemaVersion);
        Assert.Equal(17, request.ExpectedRevision);
    }

    [Fact]
    public void New_layout_is_unsaved_even_before_the_first_edit()
    {
        var editor = LayoutDesignerViewModel.CreateNew();

        Assert.True(editor.IsNew);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);
        Assert.Equal(2, editor.PanelCount);
        Assert.Equal("Unsaved new layout", editor.DirtyStatus);
        Assert.Equal(
            LayoutDesignerCancelDisposition.ConfirmDiscard,
            editor.RequestCancel());
    }

    [Fact]
    public void Slot_identities_survive_the_dock_round_trip()
    {
        var definition = ThreePanelLayout();
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1);

        Assert.Equal(
            definition.Slots.Select(slot => slot.Id.Value).Order(StringComparer.Ordinal),
            editor.Slots.Select(slot => slot.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Selection_does_not_dirty_and_rejects_unknown_slots()
    {
        var editor = new LayoutDesignerViewModel(ThreePanelLayout(), expectedRevision: 1);
        var second = editor.Slots[1];

        Assert.True(editor.SelectSlot(second.Id).IsSuccess);
        Assert.Equal(second.Id, editor.SelectedSlotId);
        Assert.True(second.IsSelected);
        Assert.False(editor.IsDirty);

        var unknown = editor.SelectSlot("missing-slot");
        Assert.False(unknown.IsSuccess);
        Assert.Equal(DefinitionValidationCode.UnknownSlot, unknown.Issue?.Code);
        Assert.True(editor.HasOperationError);
    }

    [Fact]
    public void Splitting_adds_a_selected_slot_and_marks_the_layout_dirty()
    {
        var editor = new LayoutDesignerViewModel(ThreePanelLayout(), expectedRevision: 1);
        var target = editor.Slots[0];

        var result = editor.SplitSlot(target.Id, LayoutDesignerSplitDirection.Right);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, editor.PanelCount);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);
        Assert.NotNull(editor.SelectedSlot);
        Assert.NotEqual(target.Id, editor.SelectedSlotId, StringComparer.Ordinal);
        Assert.Equal([1, 2, 3, 4], editor.Slots.Select(slot => slot.Order));
    }

    [Fact]
    public void Splitting_halves_the_target_share_of_the_canvas()
    {
        var editor = LayoutDesignerViewModel.CreateNew();
        var target = editor.Slots[0];

        Assert.Equal(0.5, target.WidthShare, precision: 3);
        Assert.True(
            editor.SplitSlot(target.Id, LayoutDesignerSplitDirection.Right).IsSuccess);

        Assert.Equal(0.25, target.WidthShare, precision: 3);
        Assert.Equal(1.0, target.HeightShare, precision: 3);
    }

    [Fact]
    public void Removing_a_slot_reflows_and_refuses_to_remove_the_last_panel()
    {
        var editor = LayoutDesignerViewModel.CreateNew();
        var first = editor.Slots[0];
        var second = editor.Slots[1];

        Assert.True(editor.RemoveSlot(first.Id).IsSuccess);
        Assert.Equal(1, editor.PanelCount);
        Assert.Equal(1.0, second.WidthShare, precision: 3);

        var refused = editor.RemoveSlot(second.Id);
        Assert.False(refused.IsSuccess);
        Assert.Equal(DefinitionValidationCode.Required, refused.Issue?.Code);
        Assert.Equal(1, editor.PanelCount);
    }

    [Fact]
    public void Add_slot_splits_the_largest_panel_without_a_pointer()
    {
        var editor = LayoutDesignerViewModel.CreateNew();

        Assert.True(editor.AddSlot().IsSuccess);

        Assert.Equal(3, editor.PanelCount);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void Reset_restores_the_saved_geometry_name_and_clean_state()
    {
        var definition = ThreePanelLayout();
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1)
        {
            Name = "Renamed"
        };
        Assert.True(editor.AddSlot().IsSuccess);
        Assert.True(editor.IsDirty);

        editor.Reset();

        Assert.Equal(definition.Name, editor.Name);
        Assert.Equal(definition.Slots.Count, editor.PanelCount);
        Assert.False(editor.IsDirty);
        Assert.Equal(LayoutDesignerCancelDisposition.Close, editor.RequestCancel());
    }

    [Fact]
    public void Save_projects_a_valid_slot_grid_with_dock_geometry_attached()
    {
        var editor = LayoutDesignerViewModel.CreateNew("Terminal wall");
        Assert.True(editor.SplitSlot(
            editor.Slots[1].Id,
            LayoutDesignerSplitDirection.Down).IsSuccess);

        var request = editor.CreateSaveRequest();
        var definition = request.Definition;

        Assert.Equal("Terminal wall", definition.Name);
        Assert.Equal(3, definition.Slots.Count);
        Assert.NotNull(definition.DockLayoutJson);
        Assert.True(LayoutValidator.Validate(definition).IsValid);

        // The right column is split top/bottom: both right slots share the left
        // slot's right edge, and stack vertically.
        var left = definition.Slots.Single(slot => slot.Bounds.Column == 0);
        var right = definition.Slots
            .Where(slot => slot.Bounds.Column > 0)
            .OrderBy(slot => slot.Bounds.Row)
            .ToArray();
        Assert.Equal(definition.Grid.Rows, left.Bounds.RowSpan);
        Assert.Equal(2, right.Length);
        Assert.Equal(right[0].Bounds.Column, right[1].Bounds.Column);
        Assert.Equal(
            definition.Grid.Rows,
            right.Sum(slot => slot.Bounds.RowSpan));
    }

    [Fact]
    public void Saved_dock_geometry_reopens_with_the_same_slots()
    {
        var editor = LayoutDesignerViewModel.CreateNew("Round trip");
        Assert.True(editor.SplitSlot(
            editor.Slots[0].Id,
            LayoutDesignerSplitDirection.Down).IsSuccess);
        var saved = editor.CreateSaveRequest().Definition;

        var reopened = new LayoutDesignerViewModel(saved, expectedRevision: 3);

        Assert.False(reopened.IsDirty);
        Assert.Equal(
            saved.Slots.Select(slot => slot.Id.Value).Order(StringComparer.Ordinal),
            reopened.Slots.Select(slot => slot.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Save_preserves_each_slots_stored_minimum_size()
    {
        var definition = ThreePanelLayout();
        var editor = new LayoutDesignerViewModel(definition, expectedRevision: 1)
        {
            Name = "Adjusted"
        };

        var saved = editor.CreateSaveRequest().Definition;

        foreach (var slot in definition.Slots)
        {
            var counterpart = saved.Slots.Single(item => item.Id == slot.Id);
            Assert.Equal(slot.MinimumSize, counterpart.MinimumSize);
        }
    }

    /// <summary>
    /// The canvas renders each Dock document's Context; the sidebar renders the
    /// published slot list. Splitting fires factory mutation events while the
    /// tree is mid-rearrangement, which once pruned and re-minted slot view
    /// models — leaving the canvas holding stale instances that read
    /// "Panel 0 · 0% × 0%" while the sidebar was correct.
    /// </summary>
    [Fact]
    public void Canvas_documents_and_sidebar_share_one_slot_instance()
    {
        var editor = LayoutDesignerViewModel.CreateNew();
        Assert.True(editor.SplitSlot(
            editor.Slots[0].Id,
            LayoutDesignerSplitDirection.Right).IsSuccess);
        Assert.True(editor.SplitSlot(
            editor.Slots[^1].Id,
            LayoutDesignerSplitDirection.Down).IsSuccess);

        var contexts = DockLayoutProjection.CollectRegions(editor.DockLayout)
            .Select(region => region.Document.Context)
            .Cast<LayoutDesignerSlotViewModel>()
            .ToArray();

        Assert.Equal(editor.Slots.Count, contexts.Length);
        foreach (var slot in editor.Slots)
        {
            Assert.Contains(contexts, context => ReferenceEquals(context, slot));
        }

        Assert.All(contexts, context => Assert.True(context.Order >= 1));
        Assert.All(contexts, context => Assert.True(context.WidthShare > 0));
        Assert.All(contexts, context => Assert.True(context.HeightShare > 0));
    }

    [Fact]
    public void Whitespace_name_blocks_saving()
    {
        var editor = LayoutDesignerViewModel.CreateNew();
        editor.Name = "   ";

        Assert.False(editor.CanSave);
        Assert.Throws<InvalidOperationException>(() => editor.CreateSaveRequest());
    }

    /// <summary>
    /// One full-height panel on the left, two stacked on the right — the layout
    /// shape the designer's projection has to reproduce from Dock proportions.
    /// </summary>
    private static LayoutDefinition ThreePanelLayout() => new(
        LayoutId.New(),
        LayoutDefinition.CurrentSchemaVersion,
        "Three panels",
        new LayoutGrid(2, 2),
        [
            new(
                new LayoutSlotId("left"),
                new LayoutGridBounds(0, 0, 1, 2),
                new LayoutMinimumSize(200, 120)),
            new(
                new LayoutSlotId("top-right"),
                new LayoutGridBounds(1, 0, 1, 1),
                new LayoutMinimumSize(180, 100)),
            new(
                new LayoutSlotId("bottom-right"),
                new LayoutGridBounds(1, 1, 1, 1),
                new LayoutMinimumSize(180, 100)),
        ]);
}
