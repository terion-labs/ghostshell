using System.Collections.ObjectModel;

namespace Asura.Application;

/// <summary>
/// The global damage classification reported by a terminal render-state update.
/// </summary>
public enum TerminalRenderDamageKind
{
    None,
    Partial,
    Full,
}

/// <summary>
/// Immutable damage metadata for a <see cref="TerminalRenderFrame"/>.
/// </summary>
public sealed record TerminalRenderDelta
{
    private static readonly IReadOnlyList<int> NoDirtyRows =
        Array.AsReadOnly(Array.Empty<int>());

    public TerminalRenderDelta(
        TerminalRenderDamageKind Kind,
        IReadOnlyList<int>? DirtyRows = null)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        var rows = SnapshotRows(DirtyRows);
        if (Kind == TerminalRenderDamageKind.None && rows.Count != 0)
        {
            throw new ArgumentException(
                "A clean render delta cannot contain dirty rows.",
                nameof(DirtyRows));
        }

        if (Kind == TerminalRenderDamageKind.Partial && rows.Count == 0)
        {
            throw new ArgumentException(
                "A partial render delta must identify at least one dirty row.",
                nameof(DirtyRows));
        }

        this.Kind = Kind;
        this.DirtyRows = rows;
    }

    public TerminalRenderDamageKind Kind { get; }

    /// <summary>
    /// Ordered viewport row indices whose cell content changed.
    ///
    /// Full damage may still carry row flags because libghostty-vt tracks the
    /// global and row damage layers independently.
    /// </summary>
    public IReadOnlyList<int> DirtyRows { get; }

    private static IReadOnlyList<int> SnapshotRows(IReadOnlyList<int>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return NoDirtyRows;
        }

        if (rows.Count > ushort.MaxValue)
        {
            throw new ArgumentException(
                "A render delta cannot contain more than 65,535 dirty rows.",
                nameof(rows));
        }

        var snapshot = new int[rows.Count];
        var previous = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rows));
            }

            if (row <= previous)
            {
                throw new ArgumentException(
                    "Dirty render rows must be unique and ordered.",
                    nameof(rows));
            }

            snapshot[index] = row;
            previous = row;
        }

        return new ReadOnlyCollection<int>(snapshot);
    }
}
