using System.Collections.ObjectModel;

namespace Asura.Application;

/// <summary>
/// An immutable, renderer-facing viewport snapshot. Agent and automation reads
/// continue to use the bounded, text-oriented <see cref="TerminalScreenSnapshot"/>.
/// </summary>
public sealed record TerminalRenderFrame
{
    public TerminalRenderFrame(
        long Revision,
        int Rows,
        int Columns,
        IReadOnlyList<TerminalRenderRow> ViewportRows,
        TerminalRenderCursor Cursor,
        TerminalRenderDelta Delta,
        TerminalKittyGraphicsFrame? KittyGraphics = null)
    {
        if (Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Revision));
        }

        if (Rows is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Rows));
        }

        if (Columns is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Columns));
        }

        if ((Rows == 0) != (Columns == 0))
        {
            throw new ArgumentException(
                "A render viewport must have both dimensions or be completely empty.",
                nameof(Rows));
        }

        ArgumentNullException.ThrowIfNull(ViewportRows);
        ArgumentNullException.ThrowIfNull(Cursor);
        ArgumentNullException.ThrowIfNull(Delta);
        var rows = SnapshotRows(ViewportRows, Rows, Columns);
        ValidateDamage(Delta, Rows);
        ValidateCursor(Cursor, Rows, Columns);

        this.Revision = Revision;
        this.Rows = Rows;
        this.Columns = Columns;
        this.ViewportRows = rows;
        this.Cursor = Cursor;
        this.Delta = Delta;
        this.KittyGraphics = KittyGraphics ?? TerminalKittyGraphicsFrame.Empty;
    }

    public long Revision { get; }

    public int Rows { get; }

    public int Columns { get; }

    public IReadOnlyList<TerminalRenderRow> ViewportRows { get; }

    public TerminalRenderCursor Cursor { get; }

    public TerminalRenderDelta Delta { get; }

    public TerminalKittyGraphicsFrame KittyGraphics { get; }

    private static IReadOnlyList<TerminalRenderRow> SnapshotRows(
        IReadOnlyList<TerminalRenderRow> rows,
        int expectedRows,
        int expectedColumns)
    {
        if (rows.Count != expectedRows)
        {
            throw new ArgumentException(
                "A render frame must contain every physical viewport row.",
                nameof(rows));
        }

        if (rows.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<TerminalRenderRow>());
        }

        var snapshot = new TerminalRenderRow[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]
                ?? throw new ArgumentException("Render frames cannot contain null rows.", nameof(rows));
            if (row.Index != index || row.Cells.Count != expectedColumns)
            {
                throw new ArgumentException(
                    "Render rows must be ordered and contain one cell per viewport column.",
                    nameof(rows));
            }

            snapshot[index] = row;
        }

        return new ReadOnlyCollection<TerminalRenderRow>(snapshot);
    }

    private static void ValidateDamage(TerminalRenderDelta delta, int rows)
    {
        if (delta.DirtyRows.Count != 0 && rows == 0)
        {
            throw new ArgumentException(
                "An empty render frame cannot have dirty rows.",
                nameof(delta));
        }

        if (delta.DirtyRows.Count != 0 && delta.DirtyRows[^1] >= rows)
        {
            throw new ArgumentException(
                "Dirty rows must fit the render viewport.",
                nameof(delta));
        }
    }

    private static void ValidateCursor(TerminalRenderCursor cursor, int rows, int columns)
    {
        if (!cursor.IsInViewport)
        {
            return;
        }

        if (cursor.ViewportRow >= rows || cursor.ViewportColumn >= columns)
        {
            throw new ArgumentException(
                "The render cursor must fit the viewport.",
                nameof(cursor));
        }

        if (cursor.IsWideCharacterTail && cursor.ViewportColumn == 0)
        {
            throw new ArgumentException(
                "A wide-character cursor tail cannot occupy the first viewport column.",
                nameof(cursor));
        }
    }
}
