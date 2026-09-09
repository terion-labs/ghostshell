using System.Text;
using Asura.Application;

namespace Asura.Terminal;

internal sealed partial class GhosttyVtTerminalSession
{
    public ValueTask<TerminalScreenDiffResult> ReadScreenDiffAsync(
        TerminalScreenDiffInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var baseline = _lastObservedScreen;
            var current = BuildScreenSnapshotUnsafe(BuildRenderFrameUnsafe());
            _lastObservedScreen = current;
            if (baseline?.ContentRevision != input.AfterContentRevision)
            {
                return ValueTask.FromResult(new TerminalScreenDiffResult(
                    input.AfterContentRevision,
                    current.ContentRevision,
                    BaselineAvailable: false,
                    [],
                    IsTruncated: false,
                    current.CursorRow,
                    current.CursorColumn,
                    current.IsCursorVisible,
                    current.InteractiveState));
            }

            var changes = BuildChangedRows(baseline, current, input.MaximumRowCount);
            return ValueTask.FromResult(new TerminalScreenDiffResult(
                input.AfterContentRevision,
                current.ContentRevision,
                BaselineAvailable: true,
                changes.Rows,
                changes.IsTruncated,
                current.CursorRow,
                current.CursorColumn,
                current.IsCursorVisible,
                current.InteractiveState));
        }
    }

    private static (IReadOnlyList<TerminalScreenDiffResult.RowChange> Rows, bool IsTruncated)
        BuildChangedRows(
            TerminalScreenSnapshot baseline,
            TerminalScreenSnapshot current,
            int maximumRows)
    {
        var changes = new List<TerminalScreenDiffResult.RowChange>(
            Math.Min(maximumRows, current.StructuredRows.Count));
        var rowCount = Math.Max(
            baseline.StructuredRows.Count,
            current.StructuredRows.Count);
        var truncated = false;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var previous = ReadRow(baseline, rowIndex);
            var next = ReadRow(current, rowIndex);
            if (string.Equals(previous.FullText, next.FullText
, StringComparison.Ordinal) && previous.IsWrapped == next.IsWrapped)
            {
                continue;
            }

            if (changes.Count == maximumRows)
            {
                truncated = true;
                break;
            }

            changes.Add(new TerminalScreenDiffResult.RowChange(
                rowIndex,
                next.BoundedText,
                next.IsWrapped,
                next.IsTruncated));
        }

        return (changes, truncated || current.IsTruncated);
    }

    private static ScreenRowText ReadRow(
        TerminalScreenSnapshot snapshot,
        int rowIndex)
    {
        if (rowIndex >= snapshot.StructuredRows.Count)
        {
            return new ScreenRowText(string.Empty, string.Empty, false, false);
        }

        var row = snapshot.StructuredRows[rowIndex];
        var text = new StringBuilder(Math.Min(snapshot.Columns, 1_024));
        foreach (var cell in row.Cells)
        {
            if (cell.Width == 0)
            {
                continue;
            }

            text.Append(cell.Text.Length == 0 ? ' ' : cell.Text);
        }

        var fullText = row.IsWrapped
            ? text.ToString()
            : text.ToString().TrimEnd();
        var truncated = fullText.Length
            > TerminalScreenDiffResult.RowChange.MaximumTextCharacters;
        var bounded = truncated
            ? fullText[..TerminalScreenDiffResult.RowChange.MaximumTextCharacters]
            : fullText;
        if (bounded.Length > 0 && char.IsHighSurrogate(bounded[^1]))
        {
            bounded = bounded[..^1];
        }

        return new ScreenRowText(fullText, bounded, row.IsWrapped, truncated);
    }

    private sealed record ScreenRowText(
        string FullText,
        string BoundedText,
        bool IsWrapped,
        bool IsTruncated);
}
