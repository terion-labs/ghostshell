using System.Collections.ObjectModel;

namespace Asura.Application;

public sealed record TerminalScreenSnapshot
{
    private static readonly IReadOnlyList<TerminalScreenRow> EmptyRows =
        Array.AsReadOnly(Array.Empty<TerminalScreenRow>());
    private static readonly IReadOnlyList<TerminalCommandBoundary> EmptyBoundaries =
        Array.AsReadOnly(Array.Empty<TerminalCommandBoundary>());
    private static readonly IReadOnlyList<TerminalShellIntegrationEvent> EmptyShellEvents =
        Array.AsReadOnly(Array.Empty<TerminalShellIntegrationEvent>());

    public TerminalScreenSnapshot(
        string PlainText,
        int CursorRow,
        int CursorColumn,
        int Rows,
        int Columns,
        bool IsAlternateScreen,
        string? WorkingDirectory,
        DateTimeOffset CapturedAtUtc,
        bool IsTruncated = false,
        IReadOnlyList<TerminalScreenRow>? StructuredRows = null,
        bool IsBracketedPasteEnabled = false,
        bool IsMouseTrackingEnabled = false,
        long ContentRevision = 0,
        string? WindowTitle = null,
        bool IsCursorVisible = true,
        int ScrollbackLinesAbove = 0,
        int ScrollbackLinesBelow = 0,
        IReadOnlyList<TerminalCommandBoundary>? CommandBoundaries = null,
        IReadOnlyList<TerminalShellIntegrationEvent>? ShellIntegrationEvents = null,
        TerminalInteractiveStateSnapshot? InteractiveState = null)
    {
        ArgumentNullException.ThrowIfNull(PlainText);
        if (Rows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Rows));
        }

        if (Columns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Columns));
        }

        if (CursorRow < 0 || (Rows > 0 && CursorRow >= Rows))
        {
            throw new ArgumentOutOfRangeException(nameof(CursorRow));
        }

        if (CursorColumn < 0 || (Columns > 0 && CursorColumn >= Columns))
        {
            throw new ArgumentOutOfRangeException(nameof(CursorColumn));
        }

        if (ContentRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ContentRevision));
        }

        if (ScrollbackLinesAbove < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ScrollbackLinesAbove));
        }

        if (ScrollbackLinesBelow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ScrollbackLinesBelow));
        }

        this.PlainText = PlainText;
        this.CursorRow = CursorRow;
        this.CursorColumn = CursorColumn;
        this.Rows = Rows;
        this.Columns = Columns;
        this.IsAlternateScreen = IsAlternateScreen;
        this.WorkingDirectory = WorkingDirectory;
        this.CapturedAtUtc = CapturedAtUtc;
        this.IsTruncated = IsTruncated;
        this.StructuredRows = SnapshotRows(StructuredRows, Rows, Columns);
        this.IsBracketedPasteEnabled = IsBracketedPasteEnabled;
        this.IsMouseTrackingEnabled = IsMouseTrackingEnabled;
        this.ContentRevision = ContentRevision;
        this.WindowTitle = WindowTitle;
        this.IsCursorVisible = IsCursorVisible;
        this.ScrollbackLinesAbove = ScrollbackLinesAbove;
        this.ScrollbackLinesBelow = ScrollbackLinesBelow;
        this.CommandBoundaries = SnapshotBoundaries(CommandBoundaries, Rows, Columns);
        this.ShellIntegrationEvents = SnapshotShellEvents(ShellIntegrationEvents);
        var liveInteractiveState = InteractiveState is { } state
            && state.ExpiresAtUtc > CapturedAtUtc
                ? state
                : null;
        this.InteractiveState = liveInteractiveState is
        { InputRegion: { } inputRegion }
            && !inputRegion.Fits(Rows, Columns)
                ? liveInteractiveState with { InputRegion = null }
                : liveInteractiveState;
    }

    public string PlainText { get; }

    public int CursorRow { get; }

    public int CursorColumn { get; }

    public int Rows { get; }

    public int Columns { get; }

    public bool IsAlternateScreen { get; }

    public string? WorkingDirectory { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public bool IsTruncated { get; }

    public IReadOnlyList<TerminalScreenRow> StructuredRows { get; }

    public bool IsBracketedPasteEnabled { get; }

    public bool IsMouseTrackingEnabled { get; }

    public long ContentRevision { get; }

    public string? WindowTitle { get; }

    public bool IsCursorVisible { get; }

    public int ScrollbackLinesAbove { get; }

    public int ScrollbackLinesBelow { get; }

    public bool IsViewportAtBottom => ScrollbackLinesBelow == 0;

    public IReadOnlyList<TerminalCommandBoundary> CommandBoundaries { get; }

    public IReadOnlyList<TerminalShellIntegrationEvent> ShellIntegrationEvents { get; }

    /// <summary>
    /// Optional, expiring application-authored state. Its absence means unknown,
    /// not idle, and callers must never treat it as authorization.
    /// </summary>
    public TerminalInteractiveStateSnapshot? InteractiveState { get; }

    private static IReadOnlyList<TerminalScreenRow> SnapshotRows(
        IReadOnlyList<TerminalScreenRow>? rows,
        int expectedRows,
        int expectedColumns)
    {
        if (rows is null || rows.Count == 0)
        {
            return EmptyRows;
        }

        if (expectedRows == 0 || rows.Count > expectedRows)
        {
            throw new ArgumentException(
                "Structured terminal rows cannot exceed the declared viewport.",
                nameof(rows));
        }

        var snapshot = new TerminalScreenRow[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]
                ?? throw new ArgumentException("Structured terminal rows cannot contain null values.", nameof(rows));
            if (row.Index != index || row.Cells.Sum(cell => cell.Width) > expectedColumns)
            {
                throw new ArgumentException(
                    "Structured terminal rows must be ordered and fit the declared viewport.",
                    nameof(rows));
            }

            snapshot[index] = row;
        }

        return new ReadOnlyCollection<TerminalScreenRow>(snapshot);
    }

    private static IReadOnlyList<TerminalCommandBoundary> SnapshotBoundaries(
        IReadOnlyList<TerminalCommandBoundary>? boundaries,
        int rows,
        int columns)
    {
        if (boundaries is null || boundaries.Count == 0)
        {
            return EmptyBoundaries;
        }

        if (boundaries.Count > 4_096)
        {
            throw new ArgumentException(
                "A terminal snapshot cannot contain more than 4,096 command boundaries.",
                nameof(boundaries));
        }

        var snapshot = new TerminalCommandBoundary[boundaries.Count];
        for (var index = 0; index < boundaries.Count; index++)
        {
            var boundary = boundaries[index]
                ?? throw new ArgumentException("Command boundaries cannot contain null values.", nameof(boundaries));
            if (boundary.Row >= rows || boundary.Column >= columns)
            {
                throw new ArgumentException(
                    "Command boundaries must fit the declared terminal viewport.",
                    nameof(boundaries));
            }

            snapshot[index] = boundary;
        }

        return new ReadOnlyCollection<TerminalCommandBoundary>(snapshot);
    }

    private static IReadOnlyList<TerminalShellIntegrationEvent> SnapshotShellEvents(
        IReadOnlyList<TerminalShellIntegrationEvent>? events)
    {
        if (events is null || events.Count == 0)
        {
            return EmptyShellEvents;
        }

        if (events.Count > 4_096)
        {
            throw new ArgumentException(
                "A terminal snapshot cannot contain more than 4,096 shell-integration events.",
                nameof(events));
        }

        var snapshot = new TerminalShellIntegrationEvent[events.Count];
        long previousSequence = 0;
        for (var index = 0; index < events.Count; index++)
        {
            var shellEvent = events[index]
                ?? throw new ArgumentException(
                    "Shell-integration events cannot contain null values.",
                    nameof(events));
            if (shellEvent.Sequence <= previousSequence)
            {
                throw new ArgumentException(
                    "Shell-integration events must be ordered by unique sequence.",
                    nameof(events));
            }

            snapshot[index] = shellEvent;
            previousSequence = shellEvent.Sequence;
        }

        return new ReadOnlyCollection<TerminalShellIntegrationEvent>(snapshot);
    }
}
