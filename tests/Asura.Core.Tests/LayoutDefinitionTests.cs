namespace Asura.Core.Tests;

public sealed class LayoutDefinitionTests
{
    [Fact]
    public void Validator_rejects_slots_outside_the_normalized_grid()
    {
        var definition = CreateLayout(
            [Slot("main", 0, 0, 8, 8), Slot("outside", 8, 0, 5, 8)]);

        var result = LayoutValidator.Validate(definition);

        Assert.Contains(result.Issues, issue =>
            issue.Code == DefinitionValidationCode.OutOfBounds
            && string.Equals(issue.Target, "outside", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_overlapping_slots()
    {
        var definition = CreateLayout(
            [Slot("left", 0, 0, 8, 8), Slot("right", 6, 0, 6, 8)]);

        var result = LayoutValidator.Validate(definition);

        Assert.Contains(result.Issues, issue => issue.Code == DefinitionValidationCode.Overlap);
    }

    [Fact]
    public void Arranger_rejects_a_canvas_below_a_slot_minimum()
    {
        var definition = new LayoutDefinition(
            new LayoutId("two-columns"),
            LayoutDefinition.CurrentSchemaVersion,
            "Two columns",
            new LayoutGrid(2, 1),
            [
                new(new LayoutSlotId("left"), new(0, 0, 1, 1), new(120, 80)),
                new(new LayoutSlotId("right"), new(1, 0, 1, 1), new(120, 80)),
            ]);

        var result = LayoutArranger.Arrange(definition, new LayoutCanvasSize(200, 100));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Validation.Issues, issue =>
            issue.Code == DefinitionValidationCode.CanvasTooSmall);
    }

    [Fact]
    public void Arranger_projects_grid_units_into_device_independent_bounds()
    {
        var definition = new LayoutDefinition(
            new LayoutId("two-columns"),
            LayoutDefinition.CurrentSchemaVersion,
            "Two columns",
            new LayoutGrid(2, 1),
            [
                new(new LayoutSlotId("left"), new(0, 0, 1, 1), new(100, 80)),
                new(new LayoutSlotId("right"), new(1, 0, 1, 1), new(100, 80)),
            ]);

        var result = LayoutArranger.Arrange(definition, new LayoutCanvasSize(600, 300));

        Assert.True(result.IsSuccess);
        Assert.Equal(new LayoutRectangle(0, 0, 300, 300), result.Placements[0].Bounds);
        Assert.Equal(new LayoutRectangle(300, 0, 300, 300), result.Placements[1].Bounds);
    }

    private static LayoutDefinition CreateLayout(IReadOnlyList<LayoutSlotDefinition> slots) =>
        new(
            new LayoutId("dashboard"),
            LayoutDefinition.CurrentSchemaVersion,
            "Dashboard",
            new LayoutGrid(12, 8),
            slots);

    private static LayoutSlotDefinition Slot(
        string id,
        int column,
        int row,
        int columnSpan,
        int rowSpan) =>
        new(new LayoutSlotId(id), new(column, row, columnSpan, rowSpan), new(160, 100));
}
