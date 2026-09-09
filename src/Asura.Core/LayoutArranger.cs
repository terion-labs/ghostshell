namespace Asura.Core;

public static class LayoutArranger
{
    public static LayoutArrangementResult Arrange(LayoutDefinition definition, LayoutCanvasSize canvas)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(canvas);

        var definitionValidation = LayoutValidator.Validate(definition);
        if (!definitionValidation.IsValid)
        {
            return new([], definitionValidation);
        }

        if (!double.IsFinite(canvas.Width)
            || !double.IsFinite(canvas.Height)
            || canvas.Width <= 0
            || canvas.Height <= 0)
        {
            return Failure(
                DefinitionValidationCode.InvalidBounds,
                "Layout canvas dimensions must be finite and positive.",
                definition.Id.Value);
        }

        List<LayoutSlotPlacement> placements = [];
        List<DefinitionValidationIssue> issues = [];
        foreach (var slot in definition.Slots)
        {
            var bounds = ArrangeSlot(definition.Grid, slot.Bounds, canvas);
            if (bounds.Width + double.Epsilon < slot.MinimumSize.Width
                || bounds.Height + double.Epsilon < slot.MinimumSize.Height)
            {
                issues.Add(new(
                    DefinitionValidationCode.CanvasTooSmall,
                    $"The canvas is too small for layout slot '{slot.Id}'.",
                    slot.Id.Value));
                continue;
            }

            placements.Add(new(slot.Id, bounds));
        }

        return issues.Count == 0
            ? new(placements, DefinitionValidationResult.Valid)
            : new([], new(issues));
    }

    private static LayoutRectangle ArrangeSlot(
        LayoutGrid grid,
        LayoutGridBounds bounds,
        LayoutCanvasSize canvas)
    {
        var columnWidth = canvas.Width / grid.Columns;
        var rowHeight = canvas.Height / grid.Rows;
        return new(
            bounds.Column * columnWidth,
            bounds.Row * rowHeight,
            bounds.ColumnSpan * columnWidth,
            bounds.RowSpan * rowHeight);
    }

    private static LayoutArrangementResult Failure(
        DefinitionValidationCode code,
        string message,
        string? target) =>
        new([], new([new(code, message, target)]));
}
