using System.Text;
using Asura.Application;
using Asura.Terminal.GhosttyVt;

namespace Asura.Terminal;

internal sealed partial class GhosttyVtTerminalSession
{
    private const int MaximumFindMatches = 4_096;
    private const int MaximumProjectedHistoryRowBytes = 256 * 1024;
    internal const int MaximumProjectedFindScanRows = 4_096;
    internal const int MaximumProjectedFindScanBytes = 4 * 1024 * 1024;

    public unsafe ValueTask<TerminalScrollbackSnapshot> ReadScrollbackAsync(
        TerminalScrollbackReadInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var totalLines = ReadScrollbackLineCountUnsafe();
            var (start, count) = ResolveScrollbackRangeUnsafe(input, totalLines);
            var rows = new TerminalScrollbackRow[count];
            for (var offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lineIndex = start + offset;
                rows[offset] = ReadScrollbackRowUnsafe(lineIndex, _contentRevision);
            }

            return ValueTask.FromResult(new TerminalScrollbackSnapshot(
                rows,
                totalLines,
                _contentRevision,
                HasMoreBefore: start > 0,
                HasMoreAfter: start + count < totalLines));
        }
    }

    public unsafe ValueTask<TerminalScrollbackFindResult> FindScrollbackAsync(
        TerminalScrollbackFindInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var totalLines = ReadScrollbackLineCountUnsafe();
            var matches = new List<TerminalScrollbackRow>(input.MaximumMatchCount);
            var truncated = false;
            var index = input.Direction == TerminalScrollbackFindDirection.Forward
                ? 0
                : totalLines - 1;
            var step = input.Direction == TerminalScrollbackFindDirection.Forward
                ? 1
                : -1;
            var scannedRows = 0;
            var scannedBytes = 0;
            while (index >= 0 && index < totalLines)
            {
                if (scannedRows == MaximumProjectedFindScanRows
                    || scannedBytes >= MaximumProjectedFindScanBytes)
                {
                    truncated = true;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var row = ReadScrollbackRowUnsafe(index, _contentRevision);
                scannedRows++;
                scannedBytes = Math.Min(
                    MaximumProjectedFindScanBytes,
                    scannedBytes + Encoding.UTF8.GetByteCount(row.Text));
                if (row.Text.Contains(input.Query, StringComparison.Ordinal))
                {
                    if (matches.Count == input.MaximumMatchCount)
                    {
                        truncated = true;
                        break;
                    }

                    matches.Add(row);
                }

                index += step;
            }

            return ValueTask.FromResult(new TerminalScrollbackFindResult(
                matches,
                totalLines,
                _contentRevision,
                truncated));
        }
    }

    public unsafe ValueTask<TerminalRenderedHistoryFindResult> FindRenderedHistoryAsync(
        TerminalRenderedHistoryFindInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var totalRows = ReadTotalRowCountUnsafe();
            var matches = new List<TerminalRenderedHistoryRow>(input.MaximumMatchCount);
            var truncated = false;
            var index = input.Direction == TerminalScrollbackFindDirection.Forward
                ? 0
                : totalRows - 1;
            var step = input.Direction == TerminalScrollbackFindDirection.Forward
                ? 1
                : -1;
            var scannedRows = 0;
            var scannedBytes = 0;
            while (index >= 0 && index < totalRows)
            {
                if (scannedRows == MaximumProjectedFindScanRows
                    || scannedBytes >= MaximumProjectedFindScanBytes)
                {
                    truncated = true;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var row = ReadRenderedHistoryRowUnsafe(index, _contentRevision);
                scannedRows++;
                scannedBytes = Math.Min(
                    MaximumProjectedFindScanBytes,
                    scannedBytes + Encoding.UTF8.GetByteCount(row.Text));
                if (row.Text.Contains(input.Query, StringComparison.Ordinal))
                {
                    if (matches.Count == input.MaximumMatchCount)
                    {
                        truncated = true;
                        break;
                    }

                    matches.Add(row);
                }

                index += step;
            }

            return ValueTask.FromResult(new TerminalRenderedHistoryFindResult(
                matches,
                totalRows,
                _contentRevision,
                truncated));
        }
    }

    public unsafe ValueTask JumpToRenderedHistoryAsync(
        TerminalRenderedHistoryRowAnchor anchor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (anchor.ContentRevision != _contentRevision)
            {
                throw new TerminalRenderedHistoryAnchorStaleException(
                    anchor.ContentRevision,
                    _contentRevision);
            }

            var totalRows = ReadTotalRowCountUnsafe();
            if (anchor.RowIndex >= totalRows)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(anchor),
                    "The rendered terminal history row is outside the retained screen.");
            }

            GhosttyVtNative.TerminalScrollViewport(
                _terminal,
                new GhosttyVtScrollViewport
                {
                    Tag = GhosttyVtScrollViewportTag.Row,
                    Value = new GhosttyVtScrollViewportValue
                    {
                        Row = checked((nuint)anchor.RowIndex),
                    },
                });
            MarkContentChangedUnsafe();
        }

        return ValueTask.CompletedTask;
    }

    public unsafe ValueTask ScrollViewportAsync(
        TerminalViewportScrollInput scrollInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var behavior = BuildViewportScrollUnsafe(scrollInput);
            GhosttyVtNative.TerminalScrollViewport(_terminal, behavior);
            MarkContentChangedUnsafe();
        }

        return ValueTask.CompletedTask;
    }

    private unsafe int ReadScrollbackLineCountUnsafe()
    {
        nuint scrollbackRows = 0;
        EnsureSuccess(
            GhosttyVtNative.TerminalGet(
                _terminal,
                GhosttyVtTerminalData.ScrollbackRows,
                &scrollbackRows),
            "read terminal scrollback row count");
        return checked((int)Math.Min(scrollbackRows, int.MaxValue));
    }

    private unsafe int ReadTotalRowCountUnsafe()
    {
        nuint totalRows = 0;
        EnsureSuccess(
            GhosttyVtNative.TerminalGet(
                _terminal,
                GhosttyVtTerminalData.TotalRows,
                &totalRows),
            "read total rendered terminal row count");
        return checked((int)Math.Min(totalRows, int.MaxValue));
    }

    private (int Start, int Count) ResolveScrollbackRangeUnsafe(
        TerminalScrollbackReadInput input,
        int totalLines)
    {
        if (input.RowAnchor is { } anchor
            && anchor.ContentRevision != _contentRevision)
        {
            throw new TerminalScrollbackAnchorStaleException(
                anchor.ContentRevision,
                _contentRevision);
        }

        if (input.RowAnchor is { LineIndex: var lineIndex }
            && lineIndex >= totalLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                "The terminal history row anchor is outside current scrollback.");
        }

        var start = input.Origin switch
        {
            TerminalScrollbackReadOrigin.Top => 0,
            TerminalScrollbackReadOrigin.Bottom =>
                Math.Max(0, totalLines - input.MaximumLines),
            TerminalScrollbackReadOrigin.Before =>
                Math.Max(0, input.RowAnchor!.LineIndex - input.MaximumLines),
            TerminalScrollbackReadOrigin.After =>
                Math.Min(totalLines, input.RowAnchor!.LineIndex + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
        var available = input.Origin == TerminalScrollbackReadOrigin.Before
            ? input.RowAnchor!.LineIndex - start
            : totalLines - start;
        return (start, Math.Min(input.MaximumLines, Math.Max(0, available)));
    }

    private unsafe TerminalScrollbackRow ReadScrollbackRowUnsafe(
        int lineIndex,
        long contentRevision)
    {
        var projected = ReadProjectedRowUnsafe(
            GhosttyVtPointTag.History,
            lineIndex,
            "terminal history");
        return new TerminalScrollbackRow(
            new TerminalScrollbackRowAnchor(contentRevision, lineIndex),
            projected.Text,
            projected.IsTruncated);
    }

    private unsafe TerminalRenderedHistoryRow ReadRenderedHistoryRowUnsafe(
        int rowIndex,
        long contentRevision)
    {
        var projected = ReadProjectedRowUnsafe(
            GhosttyVtPointTag.Screen,
            rowIndex,
            "rendered terminal history");
        return new TerminalRenderedHistoryRow(
            new TerminalRenderedHistoryRowAnchor(contentRevision, rowIndex),
            projected.Text,
            projected.IsTruncated);
    }

    private unsafe ProjectedRow ReadProjectedRowUnsafe(
        GhosttyVtPointTag pointTag,
        int rowIndex,
        string operation)
    {
        var point = new GhosttyVtPoint
        {
            Tag = pointTag,
            Value = new GhosttyVtPointValue
            {
                Coordinate = new GhosttyVtPointCoordinate
                {
                    X = 0,
                    Y = checked((uint)rowIndex),
                },
            },
        };
        var reference = GhosttyVtGridRef.CreateSized();
        EnsureSuccess(
            GhosttyVtNative.TerminalGridRef(_terminal, point, &reference),
            $"map {operation} row");
        var selection = GhosttyVtSelection.CreateSized();
        selection.Start = reference;
        selection.End = reference;
        EnsureSuccess(
            GhosttyVtNative.TerminalSelectionAdjust(
                _terminal,
                &selection,
                GhosttyVtSelectionAdjust.EndOfLine),
            $"bound {operation} row");

        var options = GhosttyVtSelectionFormatOptions.CreateSized();
        options.Format = GhosttyVtFormatterFormat.Plain;
        options.Unwrap = 0;
        options.Trim = 1;
        options.Selection = (nint)(&selection);
        nuint required = 0;
        var measure = GhosttyVtNative.TerminalSelectionFormat(
            _terminal,
            options,
            null,
            0,
            &required);
        if (measure != GhosttyVtResult.OutOfSpace)
        {
            EnsureSuccess(measure, $"measure {operation} row");
        }

        // libghostty-vt reports an empty, trimmed physical row as
        // OutOfSpace with a required size of zero. Treat that as a valid
        // empty history row instead of issuing a zero-length second read.
        if (required == 0)
        {
            return new ProjectedRow(string.Empty, IsTruncated: false);
        }

        if (required > MaximumProjectedHistoryRowBytes)
        {
            return new ProjectedRow(string.Empty, IsTruncated: true);
        }

        var bytes = new byte[checked((int)required)];
        fixed (byte* output = bytes)
        {
            EnsureSuccess(
                GhosttyVtNative.TerminalSelectionFormat(
                    _terminal,
                    options,
                    output,
                    checked((nuint)bytes.Length),
                    &required),
                $"read {operation} row");
        }

        var text = Encoding.UTF8.GetString(bytes, 0, checked((int)required));
        var truncated = text.Length > TerminalScrollbackRow.MaximumTextCharacters;
        if (truncated)
        {
            text = text[..TerminalScrollbackRow.MaximumTextCharacters];
            if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
            {
                text = text[..^1];
            }
        }

        return new ProjectedRow(text, truncated);
    }

    private readonly record struct ProjectedRow(string Text, bool IsTruncated);

    private GhosttyVtScrollViewport BuildViewportScrollUnsafe(
        TerminalViewportScrollInput input)
    {
        if (input.Direction == TerminalViewportScrollDirection.Top)
        {
            return new GhosttyVtScrollViewport
            {
                Tag = GhosttyVtScrollViewportTag.Top,
            };
        }

        if (input.Direction == TerminalViewportScrollDirection.Bottom)
        {
            return new GhosttyVtScrollViewport
            {
                Tag = GhosttyVtScrollViewportTag.Bottom,
            };
        }

        var magnitude = input.Unit == TerminalViewportScrollUnit.Page
            ? Math.Min(
                TerminalViewportScrollInput.MaximumLineDelta,
                checked((long)input.Amount * _rows))
            : input.Amount;
        var delta = input.Direction == TerminalViewportScrollDirection.Up
            ? -magnitude
            : magnitude;
        return new GhosttyVtScrollViewport
        {
            Tag = GhosttyVtScrollViewportTag.Delta,
            Value = new GhosttyVtScrollViewportValue
            {
                Delta = checked((nint)delta),
            },
        };
    }

    public unsafe ValueTask ClearScrollbackAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException(
            "The pinned libghostty-vt API does not yet expose a history-only clear operation."));

    public unsafe ValueTask UpdateSelectionAsync(
        TerminalSelectionInput selectionInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectionInput);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (selectionInput.Phase == TerminalSelectionPhase.Clear)
            {
                ClearSelectionUnsafe();
                MarkContentChangedUnsafe();
                return ValueTask.CompletedTask;
            }

            var column = Math.Clamp(selectionInput.Column, 0, _columns - 1);
            var row = Math.Clamp(selectionInput.Row, 0, _rows - 1);
            var endpoint = GetViewportGridReferenceUnsafe(column, row);
            GhosttyVtSelection selection;
            if (selectionInput.Phase == TerminalSelectionPhase.Start)
            {
                selection = GhosttyVtSelection.CreateSized();
                selection.Start = endpoint;
                selection.End = endpoint;
                _selectionAnchor = new SelectionAnchor(column, row);
            }
            else
            {
                selection = GhosttyVtSelection.CreateSized();
                var result = GhosttyVtNative.TerminalGet(
                    _terminal,
                    GhosttyVtTerminalData.Selection,
                    &selection);
                if (result == GhosttyVtResult.NoValue)
                {
                    var anchor = _selectionAnchor ?? new SelectionAnchor(column, row);
                    selection = GhosttyVtSelection.CreateSized();
                    selection.Start = GetViewportGridReferenceUnsafe(anchor.Column, anchor.Row);
                }
                else
                {
                    EnsureSuccess(result, "read terminal selection");
                }

                selection.End = endpoint;
            }

            EnsureSuccess(
                GhosttyVtNative.TerminalSet(
                    _terminal,
                    GhosttyVtTerminalOption.Selection,
                    &selection),
                "update terminal selection");
            MarkContentChangedUnsafe();
        }

        return ValueTask.CompletedTask;
    }

    public unsafe ValueTask<TerminalSelectionText> ReadSelectionAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var options = GhosttyVtSelectionFormatOptions.CreateSized();
            options.Format = GhosttyVtFormatterFormat.Plain;
            options.Unwrap = 1;
            options.Trim = 1;
            options.Selection = 0;

            nuint required = 0;
            var result = GhosttyVtNative.TerminalSelectionFormat(
                _terminal,
                options,
                null,
                0,
                &required);
            if (result == GhosttyVtResult.NoValue)
            {
                return ValueTask.FromResult(
                    new TerminalSelectionText(string.Empty, false, false));
            }

            if (result != GhosttyVtResult.OutOfSpace && result != GhosttyVtResult.Success)
            {
                EnsureSuccess(result, "measure terminal selection");
            }

            if (required == 0)
            {
                return ValueTask.FromResult(
                    new TerminalSelectionText(string.Empty, true, false));
            }

            if (required > int.MaxValue)
            {
                throw new InvalidOperationException("The terminal selection is too large to copy.");
            }

            var bytes = new byte[checked((int)required)];
            fixed (byte* output = bytes)
            {
                EnsureSuccess(
                    GhosttyVtNative.TerminalSelectionFormat(
                        _terminal,
                        options,
                        output,
                        checked((nuint)bytes.Length),
                        &required),
                    "copy terminal selection");
            }

            var text = Encoding.UTF8.GetString(bytes, 0, checked((int)required));
            var truncated = text.Length > TerminalSelectionText.MaximumCharacters;
            if (truncated)
            {
                var length = TerminalSelectionText.MaximumCharacters;
                if (length > 0
                    && char.IsHighSurrogate(text[length - 1])
                    && length < text.Length
                    && char.IsLowSurrogate(text[length]))
                {
                    length--;
                }

                text = text[..length];
            }

            return ValueTask.FromResult(new TerminalSelectionText(text, true, truncated));
        }
    }

    public unsafe ValueTask<TerminalFindResult> FindAsync(
        TerminalFindInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var query = Encoding.UTF8.GetBytes(input.Query);
            fixed (byte* queryPointer = query)
            {
                var options = GhosttyVtTerminalSearchOptions.CreateSized();
                options.Query = new GhosttyVtString(
                    (nint)queryPointer,
                    checked((nuint)query.Length));
                options.RequestedMatchIndex = input.RequestedMatchIndex;
                options.MaximumMatches = MaximumFindMatches;
                var result = GhosttyVtTerminalSearchResult.CreateSized();
                EnsureSuccess(
                    GhosttyVtNative.TerminalSearch(_terminal, &options, &result),
                    "search terminal history");
                MarkContentChangedUnsafe();
                if (result.MatchCount == 0)
                {
                    _selectionAnchor = null;
                    return ValueTask.FromResult(TerminalFindResult.Empty);
                }

                var matchCount = checked((int)result.MatchCount);
                var selectedIndex = checked((int)result.SelectedMatchIndex);
                _selectionAnchor = null;
                return ValueTask.FromResult(new TerminalFindResult(
                    matchCount,
                    selectedIndex,
                    result.ScanTruncated != 0));
            }
        }
    }

    private unsafe void ClearSelectionUnsafe()
    {
        GhosttyVtNative.TerminalSet(
            _terminal,
            GhosttyVtTerminalOption.Selection,
            null);
        _selectionAnchor = null;
    }

    private unsafe GhosttyVtGridRef GetViewportGridReferenceUnsafe(int column, int row)
    {
        var point = new GhosttyVtPoint
        {
            Tag = GhosttyVtPointTag.Viewport,
            Value = new GhosttyVtPointValue
            {
                Coordinate = new GhosttyVtPointCoordinate
                {
                    X = checked((ushort)column),
                    Y = checked((uint)row),
                },
            },
        };
        var reference = GhosttyVtGridRef.CreateSized();
        EnsureSuccess(
            GhosttyVtNative.TerminalGridRef(_terminal, point, &reference),
            "map a viewport cell");
        return reference;
    }

}
