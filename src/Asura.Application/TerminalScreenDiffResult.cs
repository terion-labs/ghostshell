using System.Collections.ObjectModel;

namespace Asura.Application;

public sealed record TerminalScreenDiffResult
{
    public TerminalScreenDiffResult(
        long InitialContentRevision,
        long CurrentContentRevision,
        bool BaselineAvailable,
        IReadOnlyList<RowChange> ChangedRows,
        bool IsTruncated,
        int CursorRow,
        int CursorColumn,
        bool IsCursorVisible,
        TerminalInteractiveStateSnapshot? InteractiveState)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(InitialContentRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(CurrentContentRevision);
        ArgumentNullException.ThrowIfNull(ChangedRows);
        if (ChangedRows.Count > TerminalScreenDiffInput.MaximumChangedRows)
        {
            throw new ArgumentException(
                $"A screen diff cannot contain more than {TerminalScreenDiffInput.MaximumChangedRows} changed rows.",
                nameof(ChangedRows));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(CursorRow);
        ArgumentOutOfRangeException.ThrowIfNegative(CursorColumn);
        this.InitialContentRevision = InitialContentRevision;
        this.CurrentContentRevision = CurrentContentRevision;
        this.BaselineAvailable = BaselineAvailable;
        this.ChangedRows = new ReadOnlyCollection<RowChange>([.. ChangedRows]);
        this.IsTruncated = IsTruncated;
        this.CursorRow = CursorRow;
        this.CursorColumn = CursorColumn;
        this.IsCursorVisible = IsCursorVisible;
        this.InteractiveState = InteractiveState;
    }

    public long InitialContentRevision { get; }

    public long CurrentContentRevision { get; }

    public bool BaselineAvailable { get; }

    public IReadOnlyList<RowChange> ChangedRows { get; }

    public bool IsTruncated { get; }

    public int CursorRow { get; }

    public int CursorColumn { get; }

    public bool IsCursorVisible { get; }

    public TerminalInteractiveStateSnapshot? InteractiveState { get; }

    public sealed record RowChange
    {
        public const int MaximumTextCharacters = 8_192;

        public RowChange(int Row, string Text, bool IsWrapped, bool IsTextTruncated)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(Row);
            ArgumentNullException.ThrowIfNull(Text);
            if (Text.Length > MaximumTextCharacters)
            {
                throw new ArgumentException(
                    $"A changed terminal row cannot exceed {MaximumTextCharacters} characters.",
                    nameof(Text));
            }

            this.Row = Row;
            this.Text = Text;
            this.IsWrapped = IsWrapped;
            this.IsTextTruncated = IsTextTruncated;
        }

        public int Row { get; }

        public string Text { get; }

        public bool IsWrapped { get; }

        public bool IsTextTruncated { get; }
    }
}
