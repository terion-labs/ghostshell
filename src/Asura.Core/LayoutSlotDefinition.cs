namespace Asura.Core;

public sealed record LayoutSlotDefinition(
    LayoutSlotId Id,
    LayoutGridBounds Bounds,
    LayoutMinimumSize MinimumSize);
