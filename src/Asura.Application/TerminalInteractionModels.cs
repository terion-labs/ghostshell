using Asura.Core;

namespace Asura.Application;

public enum TerminalViewportScrollDirection
{
    Up,
    Down,
    Top,
    Bottom,
}

public enum TerminalViewportScrollUnit
{
    Line,
    Page,
}

public sealed record TerminalViewportScrollInput
{
    public const int MaximumLineDelta = 1_000_000;
    public const int MaximumAmount = MaximumLineDelta;

    public TerminalViewportScrollInput(int Lines)
        : this(
            Lines < 0
                ? TerminalViewportScrollDirection.Up
                : TerminalViewportScrollDirection.Down,
            TerminalViewportScrollUnit.Line,
            LegacyAmount(Lines))
    {
    }

    public TerminalViewportScrollInput(
        TerminalViewportScrollDirection Direction,
        TerminalViewportScrollUnit Unit,
        int Amount)
    {
        if (!Enum.IsDefined(Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(Direction));
        }

        if (!Enum.IsDefined(Unit))
        {
            throw new ArgumentOutOfRangeException(nameof(Unit));
        }

        if (Amount is < 1 or > MaximumAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(Amount));
        }

        if (Direction is TerminalViewportScrollDirection.Top
                or TerminalViewportScrollDirection.Bottom
            && (Unit != TerminalViewportScrollUnit.Line || Amount != 1))
        {
            throw new ArgumentException(
                "Top and bottom scrolling use one line unit as a canonical absolute request.",
                nameof(Amount));
        }

        this.Direction = Direction;
        this.Unit = Unit;
        this.Amount = Amount;
    }

    public TerminalViewportScrollDirection Direction { get; }

    public TerminalViewportScrollUnit Unit { get; }

    public int Amount { get; }

    /// <summary>
    /// Signed line delta for legacy line-scrolling callers. Absolute and page
    /// requests intentionally have no context-free line delta.
    /// </summary>
    public int? Lines => (Direction, Unit) switch
    {
        (TerminalViewportScrollDirection.Up, TerminalViewportScrollUnit.Line) => -Amount,
        (TerminalViewportScrollDirection.Down, TerminalViewportScrollUnit.Line) => Amount,
        _ => null,
    };

    private static int LegacyAmount(int lines)
    {
        var magnitude = Math.Abs((long)lines);
        if (magnitude is < 1 or > MaximumLineDelta)
        {
            throw new ArgumentOutOfRangeException(nameof(lines));
        }

        return checked((int)magnitude);
    }
}

public enum TerminalSelectionPhase
{
    Start,
    Update,
    End,
    Clear,
}

public sealed record TerminalSelectionInput
{
    public TerminalSelectionInput(TerminalSelectionPhase Phase, int Column = 0, int Row = 0)
    {
        if (!Enum.IsDefined(Phase))
        {
            throw new ArgumentOutOfRangeException(nameof(Phase));
        }

        if (Column is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Column));
        }

        if (Row is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(Row));
        }

        this.Phase = Phase;
        this.Column = Column;
        this.Row = Row;
    }

    public TerminalSelectionPhase Phase { get; }

    public int Column { get; }

    public int Row { get; }
}

public sealed record TerminalSelectionText
{
    public const int MaximumCharacters = 4 * 1024 * 1024;

    public TerminalSelectionText(string Text, bool HasSelection, bool IsTruncated)
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length > MaximumCharacters)
        {
            throw new ArgumentException(
                $"Selected terminal text cannot exceed {MaximumCharacters} characters.",
                nameof(Text));
        }

        this.Text = Text;
        this.HasSelection = HasSelection;
        this.IsTruncated = IsTruncated;
    }

    public string Text { get; }

    public bool HasSelection { get; }

    public bool IsTruncated { get; }
}

public sealed record TerminalViewportScrollRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalViewportScrollInput ScrollInput);

public sealed record TerminalClearScrollbackRequest(
    SessionId SessionId,
    InputLeaseId LeaseId);

public sealed record TerminalFindInput
{
    public const int MaximumQueryLength = 512;

    public TerminalFindInput(string Query, int RequestedMatchIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(Query);
        if (Query.Length > MaximumQueryLength)
        {
            throw new ArgumentException(
                $"A terminal find query cannot exceed {MaximumQueryLength} characters.",
                nameof(Query));
        }

        this.Query = Query;
        this.RequestedMatchIndex = RequestedMatchIndex;
    }

    /// <summary>An empty query clears the active terminal search selection.</summary>
    public string Query { get; }

    /// <summary>The zero-based match to select; values outside the result range wrap.</summary>
    public int RequestedMatchIndex { get; }
}

public sealed record TerminalFindResult
{
    public TerminalFindResult(int MatchCount, int SelectedMatchIndex, bool IsScanTruncated)
    {
        if (MatchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MatchCount));
        }

        if ((MatchCount == 0 && SelectedMatchIndex != -1)
            || (MatchCount > 0 && (SelectedMatchIndex < 0 || SelectedMatchIndex >= MatchCount)))
        {
            throw new ArgumentOutOfRangeException(nameof(SelectedMatchIndex));
        }

        this.MatchCount = MatchCount;
        this.SelectedMatchIndex = SelectedMatchIndex;
        this.IsScanTruncated = IsScanTruncated;
    }

    public int MatchCount { get; }

    public int SelectedMatchIndex { get; }

    public bool IsScanTruncated { get; }

    public static TerminalFindResult Empty { get; } = new(0, -1, false);
}

public sealed record TerminalFindRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalFindInput Find);

public sealed record TerminalSelectionRequest(
    SessionId SessionId,
    InputLeaseId LeaseId,
    TerminalSelectionInput SelectionInput);

public sealed record TerminalSelectionReadRequest(
    SessionId SessionId,
    InputLeaseId LeaseId);

public enum TerminalCommandBoundaryKind
{
    PromptStarted,
    CommandInputStarted,
    CommandExecuted,
    CommandFinished,
}

public sealed record TerminalCommandBoundary
{
    public TerminalCommandBoundary(
        long Sequence,
        TerminalCommandBoundaryKind Kind,
        int Row,
        int Column,
        int? ExitCode = null)
    {
        if (Sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Sequence));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (Row < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Row));
        }

        if (Column < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Column));
        }

        this.Sequence = Sequence;
        this.Kind = Kind;
        this.Row = Row;
        this.Column = Column;
        this.ExitCode = ExitCode;
    }

    public long Sequence { get; }

    public TerminalCommandBoundaryKind Kind { get; }

    /// <summary>Viewport-relative row at capture time.</summary>
    public int Row { get; }

    public int Column { get; }

    public int? ExitCode { get; }
}
