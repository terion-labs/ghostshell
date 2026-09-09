using System.Collections.ObjectModel;

namespace Asura.Application;

public enum TerminalScrollbackReadOrigin
{
    Top,
    Bottom,
    Before,
    After,
}

public enum TerminalScrollbackFindDirection
{
    Forward,
    Backward,
}

/// <summary>
/// Identifies one physical history row in a specific terminal content revision.
/// The agent boundary serializes this value as an opaque token.
/// </summary>
public sealed record TerminalScrollbackRowAnchor
{
    public TerminalScrollbackRowAnchor(long ContentRevision, int LineIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ContentRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(LineIndex);
        this.ContentRevision = ContentRevision;
        this.LineIndex = LineIndex;
    }

    public long ContentRevision { get; }

    public int LineIndex { get; }
}

public sealed record TerminalScrollbackReadInput
{
    public const int SmallRead = 16;
    public const int MediumRead = 64;
    public const int LargeRead = 200;

    public TerminalScrollbackReadInput(
        TerminalScrollbackReadOrigin Origin,
        int MaximumLines,
        TerminalScrollbackRowAnchor? RowAnchor = null)
    {
        if (!Enum.IsDefined(Origin))
        {
            throw new ArgumentOutOfRangeException(nameof(Origin));
        }

        if (!IsAllowedMaximumLines(MaximumLines))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumLines),
                MaximumLines,
                "A scrollback read must request 16, 64, or 200 rows.");
        }

        var requiresAnchor = Origin is TerminalScrollbackReadOrigin.Before
            or TerminalScrollbackReadOrigin.After;
        if (requiresAnchor != (RowAnchor is not null))
        {
            throw new ArgumentException(
                "Before and after reads require one row anchor; top and bottom reads do not.",
                nameof(RowAnchor));
        }

        this.Origin = Origin;
        this.MaximumLines = MaximumLines;
        this.RowAnchor = RowAnchor;
    }

    public TerminalScrollbackReadOrigin Origin { get; }

    public int MaximumLines { get; }

    public TerminalScrollbackRowAnchor? RowAnchor { get; }

    public static bool IsAllowedMaximumLines(int value) =>
        value is SmallRead or MediumRead or LargeRead;
}

public sealed record TerminalScrollbackFindInput
{
    public const int MaximumQueryLength = 512;
    public const int MaximumMatches = 64;

    public TerminalScrollbackFindInput(
        string Query,
        TerminalScrollbackFindDirection Direction,
        int MaximumMatchCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Query);
        if (Query.Length > MaximumQueryLength || Query.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A terminal history query must be printable and at most {MaximumQueryLength} characters.",
                nameof(Query));
        }

        if (!Enum.IsDefined(Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(Direction));
        }

        if (MaximumMatchCount is < 1 or > MaximumMatches)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMatchCount));
        }

        this.Query = string.Concat(Query);
        this.Direction = Direction;
        this.MaximumMatchCount = MaximumMatchCount;
    }

    public string Query { get; }

    public TerminalScrollbackFindDirection Direction { get; }

    public int MaximumMatchCount { get; }
}

public sealed record TerminalScrollbackRow
{
    public const int MaximumTextCharacters = 64 * 1024;

    public TerminalScrollbackRow(
        TerminalScrollbackRowAnchor Anchor,
        string Text,
        bool IsTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(Anchor);
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length > MaximumTextCharacters)
        {
            throw new ArgumentException(
                $"A projected terminal row cannot exceed {MaximumTextCharacters} characters.",
                nameof(Text));
        }

        this.Anchor = Anchor;
        this.Text = string.Concat(Text);
        this.IsTruncated = IsTruncated;
    }

    public TerminalScrollbackRowAnchor Anchor { get; }

    public string Text { get; }

    public bool IsTruncated { get; }
}

public sealed record TerminalScrollbackSnapshot
{
    public TerminalScrollbackSnapshot(
        IReadOnlyList<TerminalScrollbackRow> Rows,
        int TotalLines,
        long ContentRevision,
        bool HasMoreBefore,
        bool HasMoreAfter)
    {
        ArgumentNullException.ThrowIfNull(Rows);
        ArgumentOutOfRangeException.ThrowIfNegative(TotalLines);
        ArgumentOutOfRangeException.ThrowIfNegative(ContentRevision);
        if (Rows.Count > TerminalScrollbackReadInput.LargeRead)
        {
            throw new ArgumentException("A scrollback snapshot contains too many rows.", nameof(Rows));
        }

        var snapshot = Rows.ToArray();
        for (var index = 0; index < snapshot.Length; index++)
        {
            var row = snapshot[index]
                ?? throw new ArgumentException("Scrollback rows cannot contain null values.", nameof(Rows));
            if (row.Anchor.ContentRevision != ContentRevision
                || row.Anchor.LineIndex >= TotalLines
                || (index > 0
                    && row.Anchor.LineIndex != snapshot[index - 1].Anchor.LineIndex + 1))
            {
                throw new ArgumentException(
                    "Scrollback rows must be contiguous and belong to the snapshot revision.",
                    nameof(Rows));
            }
        }

        this.Rows = new ReadOnlyCollection<TerminalScrollbackRow>(snapshot);
        this.TotalLines = TotalLines;
        this.ContentRevision = ContentRevision;
        this.HasMoreBefore = HasMoreBefore;
        this.HasMoreAfter = HasMoreAfter;
    }

    public IReadOnlyList<TerminalScrollbackRow> Rows { get; }

    public int TotalLines { get; }

    public long ContentRevision { get; }

    public bool HasMoreBefore { get; }

    public bool HasMoreAfter { get; }
}

public sealed record TerminalScrollbackFindResult
{
    public TerminalScrollbackFindResult(
        IReadOnlyList<TerminalScrollbackRow> Matches,
        int TotalLines,
        long ContentRevision,
        bool IsTruncated)
    {
        ArgumentNullException.ThrowIfNull(Matches);
        ArgumentOutOfRangeException.ThrowIfNegative(TotalLines);
        ArgumentOutOfRangeException.ThrowIfNegative(ContentRevision);
        if (Matches.Count > TerminalScrollbackFindInput.MaximumMatches)
        {
            throw new ArgumentException("A terminal history search returned too many matches.", nameof(Matches));
        }

        var snapshot = Matches.ToArray();
        foreach (var match in snapshot)
        {
            if (match is null
                || match.Anchor.ContentRevision != ContentRevision
                || match.Anchor.LineIndex >= TotalLines)
            {
                throw new ArgumentException(
                    "Terminal history matches must belong to the result revision.",
                    nameof(Matches));
            }
        }

        this.Matches = new ReadOnlyCollection<TerminalScrollbackRow>(snapshot);
        this.TotalLines = TotalLines;
        this.ContentRevision = ContentRevision;
        this.IsTruncated = IsTruncated;
    }

    public IReadOnlyList<TerminalScrollbackRow> Matches { get; }

    public int TotalLines { get; }

    public long ContentRevision { get; }

    public bool IsTruncated { get; }
}

public sealed class TerminalScrollbackAnchorStaleException(
    long expectedContentRevision,
    long currentContentRevision)
    : InvalidOperationException(
        $"Terminal history revision changed from {expectedContentRevision} to {currentContentRevision}.")
{
    public long ExpectedContentRevision { get; } = expectedContentRevision;

    public long CurrentContentRevision { get; } = currentContentRevision;

}

public sealed class TerminalRenderedHistoryAnchorStaleException(
    long expectedContentRevision,
    long currentContentRevision)
    : InvalidOperationException(
        $"Rendered terminal history revision changed from {expectedContentRevision} to {currentContentRevision}.")
{
    public long ExpectedContentRevision { get; } = expectedContentRevision;

    public long CurrentContentRevision { get; } = currentContentRevision;

}

/// <summary>
/// Identifies one row in the terminal engine's retained rendered screen: history plus
/// the currently written screen. This coordinate space is separate from shell scrollback.
/// </summary>
public sealed record TerminalRenderedHistoryRowAnchor
{
    public TerminalRenderedHistoryRowAnchor(long ContentRevision, int RowIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ContentRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(RowIndex);
        this.ContentRevision = ContentRevision;
        this.RowIndex = RowIndex;
    }

    public long ContentRevision { get; }

    public int RowIndex { get; }
}

public sealed record TerminalRenderedHistoryFindInput
{
    public const int MaximumQueryLength = TerminalScrollbackFindInput.MaximumQueryLength;
    public const int MaximumMatches = TerminalScrollbackFindInput.MaximumMatches;

    public TerminalRenderedHistoryFindInput(
        string Query,
        TerminalScrollbackFindDirection Direction,
        int MaximumMatchCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Query);
        if (Query.Length > MaximumQueryLength || Query.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A rendered terminal history query must be printable and at most {MaximumQueryLength} characters.",
                nameof(Query));
        }

        if (!Enum.IsDefined(Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(Direction));
        }

        if (MaximumMatchCount is < 1 or > MaximumMatches)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumMatchCount));
        }

        this.Query = string.Concat(Query);
        this.Direction = Direction;
        this.MaximumMatchCount = MaximumMatchCount;
    }

    public string Query { get; }

    public TerminalScrollbackFindDirection Direction { get; }

    public int MaximumMatchCount { get; }
}

public sealed record TerminalRenderedHistoryRow
{
    public TerminalRenderedHistoryRow(
        TerminalRenderedHistoryRowAnchor Anchor,
        string Text,
        bool IsTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(Anchor);
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length > TerminalScrollbackRow.MaximumTextCharacters)
        {
            throw new ArgumentException(
                $"A rendered terminal row cannot exceed {TerminalScrollbackRow.MaximumTextCharacters} characters.",
                nameof(Text));
        }

        this.Anchor = Anchor;
        this.Text = string.Concat(Text);
        this.IsTruncated = IsTruncated;
    }

    public TerminalRenderedHistoryRowAnchor Anchor { get; }

    public string Text { get; }

    public bool IsTruncated { get; }
}

public sealed record TerminalRenderedHistoryFindResult
{
    public TerminalRenderedHistoryFindResult(
        IReadOnlyList<TerminalRenderedHistoryRow> Matches,
        int TotalRows,
        long ContentRevision,
        bool IsTruncated)
    {
        ArgumentNullException.ThrowIfNull(Matches);
        ArgumentOutOfRangeException.ThrowIfNegative(TotalRows);
        ArgumentOutOfRangeException.ThrowIfNegative(ContentRevision);
        if (Matches.Count > TerminalRenderedHistoryFindInput.MaximumMatches)
        {
            throw new ArgumentException(
                "A rendered terminal history search returned too many matches.",
                nameof(Matches));
        }

        var snapshot = Matches.ToArray();
        foreach (var match in snapshot)
        {
            if (match is null
                || match.Anchor.ContentRevision != ContentRevision
                || match.Anchor.RowIndex >= TotalRows)
            {
                throw new ArgumentException(
                    "Rendered terminal history matches must belong to the result revision.",
                    nameof(Matches));
            }
        }

        this.Matches = new ReadOnlyCollection<TerminalRenderedHistoryRow>(snapshot);
        this.TotalRows = TotalRows;
        this.ContentRevision = ContentRevision;
        this.IsTruncated = IsTruncated;
    }

    public IReadOnlyList<TerminalRenderedHistoryRow> Matches { get; }

    public int TotalRows { get; }

    public long ContentRevision { get; }

    public bool IsTruncated { get; }
}
