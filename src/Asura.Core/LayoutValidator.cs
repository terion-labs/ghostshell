namespace Asura.Core;

public static class LayoutValidator
{
    public static DefinitionValidationResult Validate(LayoutDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DefinitionValidationIssue> issues = [];

        ValidateHeader(definition, issues);
        if (definition.Grid.Columns < 1 || definition.Grid.Rows < 1)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidGrid,
                "A layout grid must have at least one column and one row.",
                definition.Id.Value));
        }

        if (definition.Slots.Count == 0)
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "A layout must contain at least one slot.",
                definition.Id.Value));
        }

        AddDuplicateIdIssues(definition.Slots, issues);
        var comparableSlots = new List<LayoutSlotDefinition>();
        foreach (var slot in definition.Slots)
        {
            var boundsAreValid = ValidateSlot(definition.Grid, slot, issues);
            if (boundsAreValid)
            {
                comparableSlots.Add(slot);
            }
        }

        AddOverlapIssues(comparableSlots, issues);
        return new(issues);
    }

    private static void ValidateHeader(
        LayoutDefinition definition,
        ICollection<DefinitionValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value))
        {
            issues.Add(new(DefinitionValidationCode.Required, "A layout ID is required."));
        }

        if (definition.SchemaVersion < 1)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidSchemaVersion,
                "A layout schema version must be at least one.",
                definition.Id.Value));
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "A layout name is required.",
                definition.Id.Value));
        }
    }

    private static void AddDuplicateIdIssues(
        IReadOnlyList<LayoutSlotDefinition> slots,
        ICollection<DefinitionValidationIssue> issues)
    {
        foreach (var duplicate in slots
                     .GroupBy(slot => slot.Id.Value, StringComparer.Ordinal)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1))
        {
            issues.Add(new(
                DefinitionValidationCode.DuplicateId,
                "Layout slot IDs must be present and unique.",
                duplicate.Key));
        }
    }

    private static bool ValidateSlot(
        LayoutGrid grid,
        LayoutSlotDefinition slot,
        ICollection<DefinitionValidationIssue> issues)
    {
        var bounds = slot.Bounds;
        var hasPositiveBounds = bounds.Column >= 0
            && bounds.Row >= 0
            && bounds.ColumnSpan > 0
            && bounds.RowSpan > 0;
        if (!hasPositiveBounds)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidBounds,
                "Slot bounds require non-negative origins and positive spans.",
                slot.Id.Value));
            return false;
        }

        var right = (long)bounds.Column + bounds.ColumnSpan;
        var bottom = (long)bounds.Row + bounds.RowSpan;
        var liesInsideGrid = grid.Columns > 0
            && grid.Rows > 0
            && right <= grid.Columns
            && bottom <= grid.Rows;
        if (!liesInsideGrid)
        {
            issues.Add(new(
                DefinitionValidationCode.OutOfBounds,
                "A layout slot extends beyond the logical grid.",
                slot.Id.Value));
        }

        var minimum = slot.MinimumSize;
        if (!double.IsFinite(minimum.Width)
            || !double.IsFinite(minimum.Height)
            || minimum.Width <= 0
            || minimum.Height <= 0)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidMinimumSize,
                "A layout slot minimum size must be finite and positive.",
                slot.Id.Value));
        }

        return liesInsideGrid;
    }

    private static void AddOverlapIssues(
        IReadOnlyList<LayoutSlotDefinition> slots,
        ICollection<DefinitionValidationIssue> issues)
    {
        for (var leftIndex = 0; leftIndex < slots.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < slots.Count; rightIndex++)
            {
                var left = slots[leftIndex];
                var right = slots[rightIndex];
                if (!Overlaps(left.Bounds, right.Bounds))
                {
                    continue;
                }

                issues.Add(new(
                    DefinitionValidationCode.Overlap,
                    $"Layout slots '{left.Id}' and '{right.Id}' overlap.",
                    right.Id.Value));
            }
        }
    }

    private static bool Overlaps(LayoutGridBounds left, LayoutGridBounds right) =>
        left.Column < right.Column + right.ColumnSpan
        && right.Column < left.Column + left.ColumnSpan
        && left.Row < right.Row + right.RowSpan
        && right.Row < left.Row + left.RowSpan;
}
