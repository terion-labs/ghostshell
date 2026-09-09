namespace Asura.Core;

/// <summary>
/// Logical grid units are normalized coordinates; they do not represent device pixels.
/// </summary>
public sealed record LayoutGrid(int Columns, int Rows);
