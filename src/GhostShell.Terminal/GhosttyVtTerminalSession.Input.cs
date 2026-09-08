using System.Buffers;
using System.Text;
using System.Threading.Channels;
using GhostShell.Application;
using GhostShell.Terminal.GhosttyVt;

namespace GhostShell.Terminal;

internal sealed partial class GhosttyVtTerminalSession
{
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
        }

        return QueueInputAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    public ValueTask SendKeyAsync(
        TerminalKeyStroke keyStroke,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyStroke);
        byte[] encoded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            encoded = EncodeKeyUnsafe(
                MapKey(keyStroke.Key),
                MapModifiers(keyStroke.Modifiers),
                [],
                unshiftedCodepoint: 0,
                GhosttyVtKeyAction.Press,
                GhosttyVtModifiers.None,
                composing: false);
            if (keyStroke.RepeatCount > 1)
            {
                var repeated = GC.AllocateUninitializedArray<byte>(
                    checked(encoded.Length * keyStroke.RepeatCount));
                for (var index = 0; index < keyStroke.RepeatCount; index++)
                {
                    encoded.CopyTo(repeated, index * encoded.Length);
                }

                encoded = repeated;
            }
        }

        return QueueInputAsync(encoded, cancellationToken);
    }

    public ValueTask SendPhysicalKeyAsync(
        TerminalPhysicalKeyEvent keyEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        byte[] encoded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            encoded = EncodeKeyUnsafe(
                MapPhysicalKey(keyEvent.PhysicalKey),
                MapModifiers(keyEvent.Modifiers),
                Encoding.UTF8.GetBytes(keyEvent.Text),
                keyEvent.UnshiftedCodepoint,
                MapKeyAction(keyEvent.Action),
                MapModifiers(keyEvent.ConsumedModifiers),
                keyEvent.IsComposing);
        }

        return QueueInputAsync(encoded, cancellationToken);
    }

    public ValueTask SendChordAsync(
        TerminalCharacterChord chord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chord);
        byte[] encoded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            Span<byte> utf8 = [checked((byte)chord.Character)];
            var modifiers = chord.Modifier switch
            {
                TerminalCharacterChordModifier.Control => GhosttyVtModifiers.Control,
                TerminalCharacterChordModifier.Alt => GhosttyVtModifiers.Alt,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(chord),
                    chord.Modifier,
                    "Unknown terminal chord modifier."),
            };
            encoded = EncodeKeyUnsafe(
                new GhosttyVtKey(20 + chord.Character - 'a'),
                modifiers,
                utf8,
                chord.Character,
                GhosttyVtKeyAction.Press,
                GhosttyVtModifiers.None,
                composing: false);
        }

        return QueueInputAsync(encoded, cancellationToken);
    }

    public ValueTask EnterAsync(CancellationToken cancellationToken) =>
        SendKeyAsync(new TerminalKeyStroke(TerminalKey.Enter), cancellationToken);

    public ValueTask InterruptAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
        }

        return QueueInputAsync([0x03], cancellationToken);
    }

    public ValueTask FocusAsync(CancellationToken cancellationToken)
        => SendFocusAsync(GhosttyVtFocusEvent.Gained, cancellationToken);

    public ValueTask BlurAsync(CancellationToken cancellationToken)
        => SendFocusAsync(GhosttyVtFocusEvent.Lost, cancellationToken);

    private ValueTask SendFocusAsync(
        GhosttyVtFocusEvent focusEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!ModeEnabledUnsafe(1004))
            {
                return ValueTask.CompletedTask;
            }

            encoded = EncodeFocusUnsafe(focusEvent);
        }

        return QueueInputAsync(encoded, cancellationToken);
    }

    public unsafe ValueTask SendMouseAsync(
        TerminalMouseInput mouseInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mouseInput);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            encoded = EncodeMouseInputUnsafe(mouseInput);
        }

        return QueueInputAsync(encoded, cancellationToken);
    }

    public ValueTask<TerminalRevisionBoundMouseOutcome>
        SendMouseAtContentRevisionAsync(
            TerminalMouseInput mouseInput,
            long expectedContentRevision,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mouseInput);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedContentRevision);
        return QueueRevisionBoundMouseInputAsync(
            mouseInput,
            expectedContentRevision,
            cancellationToken);
    }

    private async ValueTask<TerminalRevisionBoundMouseOutcome>
        QueueRevisionBoundMouseInputAsync(
            TerminalMouseInput mouseInput,
            long expectedContentRevision,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_processExited)
            {
                throw new InvalidOperationException("The terminal process has exited.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    $"The terminal session has failed: {_failure.StableCode}.");
            }
        }

        var input = new QueuedTerminalInput(
            mouseInput,
            expectedContentRevision,
            cancellationToken);
        try
        {
            await _writes.Writer.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            return await input.Completion.ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new InvalidOperationException(
                "The terminal input queue is no longer accepting input.",
                exception);
        }
    }

    public async ValueTask<TerminalPasteResult> PasteAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pasteInput);
        byte[] encoded;
        bool bracketed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            bracketed = ModeEnabledUnsafe(2004);
            var normalized = PreparePasteText(pasteInput.Text, bracketed);
            encoded = EncodePasteUnsafe(Encoding.UTF8.GetBytes(normalized), bracketed);
        }

        await QueueInputAsync(encoded, cancellationToken).ConfigureAwait(false);
        return TerminalPasteResult.Completed(bracketed);
    }

    public async ValueTask<TerminalPasteResult> SubmitTextAsync(
        TerminalPasteInput pasteInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pasteInput);
        byte[] encoded;
        bool bracketed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            PrepareForTerminalInputUnsafe();
            bracketed = ModeEnabledUnsafe(2004);
            var normalized = PreparePasteText(pasteInput.Text, bracketed);
            var paste = EncodePasteUnsafe(
                Encoding.UTF8.GetBytes(normalized),
                bracketed);
            var enter = EncodeKeyUnsafe(
                MapKey(TerminalKey.Enter),
                GhosttyVtModifiers.None,
                [],
                unshiftedCodepoint: 0,
                GhosttyVtKeyAction.Press,
                GhosttyVtModifiers.None,
                composing: false);
            encoded = GC.AllocateUninitializedArray<byte>(
                checked(paste.Length + enter.Length));
            paste.CopyTo(encoded, 0);
            enter.CopyTo(encoded, paste.Length);
        }

        // A single queue item is one PTY write, so neither user input nor
        // another agent action can interleave between the text and Enter.
        await QueueInputAsync(encoded, cancellationToken).ConfigureAwait(false);
        return TerminalPasteResult.Completed(bracketed);
    }

    private async ValueTask QueueInputAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_processExited)
            {
                throw new InvalidOperationException("The terminal process has exited.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException(
                    $"The terminal session has failed: {_failure.StableCode}.");
            }
        }

        var input = new QueuedTerminalInput(bytes, cancellationToken);
        try
        {
            await _writes.Writer.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            await input.Completion.ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            throw new InvalidOperationException(
                "The terminal input queue is no longer accepting input.",
                exception);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await _pty.Reader
                    .ReadAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    MarkProcessExited(exitCode: null);
                    break;
                }

                lock (_gate)
                {
                    if (_closed)
                    {
                        return;
                    }

                    WriteTerminalBytesUnsafe(bytes, count);

                    MarkContentChangedUnsafe();
                }

                if (_protocolResponseOverflow)
                {
                    _protocolResponseOverflow = false;
                    throw new IOException(
                        "The terminal protocol-response queue overflowed; continuing would desynchronize the session.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closed)
        {
        }
        catch (IOException exception)
        {
            if (!TryMarkKnownProcessExit())
            {
                Fail("ghostty_vt_terminal_read_failed", exception);
            }
        }
        catch (Exception exception)
        {
            Fail("ghostty_vt_terminal_read_failed", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            await foreach (var input in _writes.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (input.CancellationToken.IsCancellationRequested)
                {
                    input.Cancel(input.CancellationToken);
                    continue;
                }

                var rejection = GetInputRejection();
                if (rejection is not null)
                {
                    input.Fail(rejection);
                    continue;
                }

                var committed = false;
                try
                {
                    using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        input.CancellationToken);
                    Task pendingWrite;
                    if (input.RevisionBoundMouseInput is not null)
                    {
                        if (!TryBeginRevisionBoundMouseWriteUnsafe(
                                input,
                                deliveryCancellation.Token,
                                out pendingWrite))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        pendingWrite = _pty.Writer.WriteAsync(
                            input.Bytes,
                            deliveryCancellation.Token).AsTask();
                    }

                    await pendingWrite.ConfigureAwait(false);
                    committed = true;
                    try
                    {
                        await _pty.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        input.Complete();
                    }
                    catch
                    {
                        // WriteAsync is the irreversible commit point. A flush
                        // failure must never invite the caller to retry bytes that
                        // may already have reached the process.
                        input.Complete();
                        throw;
                    }
                }
                catch (OperationCanceledException) when (
                    !committed && input.CancellationToken.IsCancellationRequested)
                {
                    input.Cancel(input.CancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    input.Cancel(cancellationToken);
                    throw;
                }
                catch (Exception exception)
                {
                    input.Fail(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            completionError = exception;
        }
        catch (ObjectDisposedException exception) when (_closed)
        {
            completionError = exception;
        }
        catch (IOException exception)
        {
            completionError = exception;
            if (!TryMarkKnownProcessExit())
            {
                Fail("ghostty_vt_terminal_write_failed", exception);
            }
        }
        catch (Exception exception)
        {
            completionError = exception;
            Fail("ghostty_vt_terminal_write_failed", exception);
        }
        finally
        {
            _writes.Writer.TryComplete(completionError);
            while (_writes.Reader.TryRead(out var input))
            {
                if (input.CancellationToken.IsCancellationRequested)
                {
                    input.Cancel(input.CancellationToken);
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    input.Cancel(cancellationToken);
                }
                else
                {
                    input.Fail(completionError ?? new InvalidOperationException(
                        "The terminal input queue stopped before delivery."));
                }
            }
        }
    }

    private unsafe bool TryBeginRevisionBoundMouseWriteUnsafe(
        QueuedTerminalInput input,
        CancellationToken cancellationToken,
        out Task pendingWrite)
    {
        lock (_gate)
        {
            if (GetInputRejectionUnsafe() is { } rejection)
            {
                input.Fail(rejection);
                pendingWrite = Task.CompletedTask;
                return false;
            }

            if (_contentRevision != input.ExpectedContentRevision)
            {
                input.Complete(TerminalRevisionBoundMouseOutcome.ContentRevisionChanged);
                pendingWrite = Task.CompletedTask;
                return false;
            }

            var mouseInput = input.RevisionBoundMouseInput!;
            if (mouseInput.Column >= _columns || mouseInput.Row >= _rows)
            {
                input.Complete(TerminalRevisionBoundMouseOutcome.CoordinatesOutOfBounds);
                pendingWrite = Task.CompletedTask;
                return false;
            }

            byte[] encoded;
            try
            {
                byte mouseTracking = 0;
                EnsureSuccess(
                    GhosttyVtNative.TerminalGet(
                        _terminal,
                        GhosttyVtTerminalData.MouseTracking,
                        &mouseTracking),
                    "read terminal mouse tracking");
                if (mouseTracking == 0)
                {
                    input.Complete(TerminalRevisionBoundMouseOutcome.MouseTrackingDisabled);
                    pendingWrite = Task.CompletedTask;
                    return false;
                }

                encoded = EncodeMouseInputUnsafe(mouseInput);
            }
            catch (Exception exception)
            {
                // Encoding used to run on the caller before queueing. Moving it
                // beside dispatch must not turn a single input failure into a
                // terminal-writer failure that rejects unrelated queued work.
                input.Fail(exception);
                pendingWrite = Task.CompletedTask;
                return false;
            }

            pendingWrite = _pty.Writer.WriteAsync(encoded, cancellationToken).AsTask();
            return true;
        }
    }

    private Exception? GetInputRejection()
    {
        lock (_gate)
        {
            return GetInputRejectionUnsafe();
        }
    }

    private Exception? GetInputRejectionUnsafe()
    {
        if (_closed)
        {
            return new ObjectDisposedException(nameof(GhosttyVtTerminalSession));
        }

        if (_processExited)
        {
            return new InvalidOperationException("The terminal process has exited.");
        }

        return _failure is null
            ? null
            : new InvalidOperationException(
                $"The terminal session has failed: {_failure.StableCode}.");
    }

    private unsafe byte[] EncodeKeyUnsafe(
        GhosttyVtKey key,
        GhosttyVtModifiers modifiers,
        ReadOnlySpan<byte> utf8,
        uint unshiftedCodepoint,
        GhosttyVtKeyAction action,
        GhosttyVtModifiers consumedModifiers,
        bool composing)
    {
        GhosttyVtNative.KeyEncoderSetOptionsFromTerminal(_keyEncoder, _terminal);
        GhosttyVtNative.KeyEventSetAction(_keyEvent, action);
        GhosttyVtNative.KeyEventSetKey(_keyEvent, key);
        GhosttyVtNative.KeyEventSetModifiers(_keyEvent, modifiers);
        GhosttyVtNative.KeyEventSetConsumedModifiers(_keyEvent, consumedModifiers);
        GhosttyVtNative.KeyEventSetComposing(_keyEvent, composing ? (byte)1 : (byte)0);
        GhosttyVtNative.KeyEventSetUnshiftedCodepoint(_keyEvent, unshiftedCodepoint);
        fixed (byte* text = utf8)
        {
            // libghostty-vt borrows this pointer rather than copying the text.
            // Keep it pinned until every encoder call has finished.
            GhosttyVtNative.KeyEventSetUtf8(_keyEvent, text, checked((nuint)utf8.Length));
            Span<byte> stack = stackalloc byte[128];
            fixed (byte* output = stack)
            {
                nuint written = 0;
                var result = GhosttyVtNative.KeyEncoderEncode(
                    _keyEncoder,
                    _keyEvent,
                    output,
                    checked((nuint)stack.Length),
                    &written);
                if (result == GhosttyVtResult.Success)
                {
                    return stack[..checked((int)written)].ToArray();
                }

                if (result != GhosttyVtResult.OutOfSpace || written > int.MaxValue)
                {
                    EnsureSuccess(result, "encode terminal key");
                }

                var buffer = new byte[checked((int)written)];
                fixed (byte* retry = buffer)
                {
                    EnsureSuccess(
                        GhosttyVtNative.KeyEncoderEncode(
                            _keyEncoder,
                            _keyEvent,
                            retry,
                            checked((nuint)buffer.Length),
                            &written),
                        "encode terminal key");
                }

                return buffer.AsSpan(0, checked((int)written)).ToArray();
            }
        }
    }

    private unsafe byte[] EncodeMouseInputUnsafe(TerminalMouseInput mouseInput)
    {
        GhosttyVtNative.MouseEncoderSetOptionsFromTerminal(_mouseEncoder, _terminal);

        var size = GhosttyVtMouseEncoderSize.CreateSized();
        size.ScreenWidth = checked((uint)_columns * _cellWidthPixels);
        size.ScreenHeight = checked((uint)_rows * _cellHeightPixels);
        size.CellWidth = _cellWidthPixels;
        size.CellHeight = _cellHeightPixels;
        GhosttyVtNative.MouseEncoderSetOption(
            _mouseEncoder,
            GhosttyVtMouseEncoderOption.Size,
            &size);

        byte anyPressed = mouseInput.Kind == TerminalMouseEventKind.Drag
            ? (byte)1
            : (byte)0;
        byte trackLastCell = 1;
        GhosttyVtNative.MouseEncoderSetOption(
            _mouseEncoder,
            GhosttyVtMouseEncoderOption.AnyButtonPressed,
            &anyPressed);
        GhosttyVtNative.MouseEncoderSetOption(
            _mouseEncoder,
            GhosttyVtMouseEncoderOption.TrackLastCell,
            &trackLastCell);

        GhosttyVtNative.MouseEventSetModifiers(
            _mouseEvent,
            MapModifiers(mouseInput.Modifiers));
        GhosttyVtNative.MouseEventSetPosition(
            _mouseEvent,
            new GhosttyVtMousePosition
            {
                X = (mouseInput.Column + 0.5f) * _cellWidthPixels,
                Y = (mouseInput.Row + 0.5f) * _cellHeightPixels,
            });

        switch (mouseInput.Kind)
        {
            case TerminalMouseEventKind.Down:
                GhosttyVtNative.MouseEventSetAction(
                    _mouseEvent,
                    GhosttyVtMouseAction.Press);
                GhosttyVtNative.MouseEventSetButton(
                    _mouseEvent,
                    MapMouseButton(mouseInput.Button));
                break;
            case TerminalMouseEventKind.Up:
                GhosttyVtNative.MouseEventSetAction(
                    _mouseEvent,
                    GhosttyVtMouseAction.Release);
                GhosttyVtNative.MouseEventSetButton(
                    _mouseEvent,
                    MapMouseButton(mouseInput.Button));
                break;
            case TerminalMouseEventKind.Move:
                GhosttyVtNative.MouseEventSetAction(
                    _mouseEvent,
                    GhosttyVtMouseAction.Motion);
                GhosttyVtNative.MouseEventClearButton(_mouseEvent);
                break;
            case TerminalMouseEventKind.Drag:
                GhosttyVtNative.MouseEventSetAction(
                    _mouseEvent,
                    GhosttyVtMouseAction.Motion);
                GhosttyVtNative.MouseEventSetButton(
                    _mouseEvent,
                    MapMouseButton(mouseInput.Button));
                break;
            case TerminalMouseEventKind.WheelUp:
                GhosttyVtNative.MouseEventSetAction(
                    _mouseEvent,
                    GhosttyVtMouseAction.Press);
                GhosttyVtNative.MouseEventSetButton(
                    _mouseEvent,
                    GhosttyVtMouseButton.Four);
                break;
            case TerminalMouseEventKind.WheelDown:
                GhosttyVtNative.MouseEventSetAction(
                    _mouseEvent,
                    GhosttyVtMouseAction.Press);
                GhosttyVtNative.MouseEventSetButton(
                    _mouseEvent,
                    GhosttyVtMouseButton.Five);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mouseInput),
                    mouseInput.Kind,
                    "Unknown terminal mouse event kind.");
        }

        return EncodeMouseUnsafe();
    }

    private unsafe byte[] EncodeMouseUnsafe()
    {
        Span<byte> stack = stackalloc byte[128];
        fixed (byte* output = stack)
        {
            nuint written = 0;
            var result = GhosttyVtNative.MouseEncoderEncode(
                _mouseEncoder,
                _mouseEvent,
                output,
                checked((nuint)stack.Length),
                &written);
            EnsureSuccess(result, "encode terminal mouse event");
            return stack[..checked((int)written)].ToArray();
        }
    }

    private static unsafe byte[] EncodeFocusUnsafe(GhosttyVtFocusEvent focusEvent)
    {
        Span<byte> stack = stackalloc byte[32];
        fixed (byte* output = stack)
        {
            nuint written = 0;
            EnsureSuccess(
                GhosttyVtNative.FocusEncode(
                    focusEvent,
                    output,
                    checked((nuint)stack.Length),
                    &written),
                "encode terminal focus event");
            return stack[..checked((int)written)].ToArray();
        }
    }

    private static unsafe byte[] EncodePasteUnsafe(byte[] utf8, bool bracketed)
    {
        var buffer = new byte[checked(utf8.Length + 64)];
        fixed (byte* input = utf8)
        fixed (byte* output = buffer)
        {
            nuint written = 0;
            var result = GhosttyVtNative.PasteEncode(
                input,
                checked((nuint)utf8.Length),
                bracketed ? (byte)1 : (byte)0,
                output,
                checked((nuint)buffer.Length),
                &written);
            if (result == GhosttyVtResult.OutOfSpace && written <= int.MaxValue)
            {
                buffer = new byte[checked((int)written)];
            }
            else
            {
                EnsureSuccess(result, "encode terminal paste");
                return buffer.AsSpan(0, checked((int)written)).ToArray();
            }
        }

        fixed (byte* input = utf8)
        fixed (byte* output = buffer)
        {
            nuint written = 0;
            EnsureSuccess(
                GhosttyVtNative.PasteEncode(
                    input,
                    checked((nuint)utf8.Length),
                    bracketed ? (byte)1 : (byte)0,
                    output,
                    checked((nuint)buffer.Length),
                    &written),
                "encode terminal paste");
            return buffer.AsSpan(0, checked((int)written)).ToArray();
        }
    }

    private unsafe bool ModeEnabledUnsafe(ushort modeValue)
    {
        byte enabled = 0;
        var result = GhosttyVtNative.TerminalModeGet(
            _terminal,
            new GhosttyVtMode(modeValue),
            &enabled);
        return result == GhosttyVtResult.Success && enabled != 0;
    }

    private void PrepareForTerminalInputUnsafe()
    {
        var behavior = new GhosttyVtScrollViewport
        {
            Tag = GhosttyVtScrollViewportTag.Bottom,
        };
        GhosttyVtNative.TerminalScrollViewport(_terminal, behavior);
        ClearSelectionUnsafe();
        MarkContentChangedUnsafe();
    }

    private unsafe void WriteTerminalBytesUnsafe(byte[] bytes, int count)
    {
        fixed (byte* pointer = bytes)
        {
            GhosttyVtNative.TerminalWrite(
                _terminal,
                pointer,
                checked((nuint)count));
        }
    }

    private static string PreparePasteText(string text, bool bracketed)
    {
        // Preserve ordinary multiline paste without a confirmation dialog. Keep
        // terminal editing/escape controls inert, including embedded bracketed-
        // paste terminators; never pass these clipboard bytes as key presses.
        var characters = text.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (IsUnsafePasteControl(character))
            {
                characters[index] = ' ';
            }
            else if (!bracketed && character == '\n')
            {
                characters[index] = '\r';
            }
        }

        return new string(characters);
    }

    private static bool IsUnsafePasteControl(char character) => character is
        '\u0000' or '\u0003' or '\u0004' or '\u0005' or '\u0008'
        or '\u000F' or '\u0011' or '\u0012' or '\u0013' or '\u0015'
        or '\u0016' or '\u0017' or '\u001A' or '\u001B' or '\u001C'
        or '\u007F';

    private static GhosttyVtModifiers MapModifiers(TerminalKeyModifiers modifiers)
    {
        var result = GhosttyVtModifiers.None;
        if (modifiers.HasFlag(TerminalKeyModifiers.Shift))
        {
            result |= GhosttyVtModifiers.Shift;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.Control))
        {
            result |= GhosttyVtModifiers.Control;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.Alt))
        {
            result |= GhosttyVtModifiers.Alt;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.Meta))
        {
            result |= GhosttyVtModifiers.Super;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.CapsLock))
        {
            result |= GhosttyVtModifiers.CapsLock;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.NumLock))
        {
            result |= GhosttyVtModifiers.NumLock;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.RightShift))
        {
            result |= GhosttyVtModifiers.RightShift;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.RightControl))
        {
            result |= GhosttyVtModifiers.RightControl;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.RightAlt))
        {
            result |= GhosttyVtModifiers.RightAlt;
        }

        if (modifiers.HasFlag(TerminalKeyModifiers.RightMeta))
        {
            result |= GhosttyVtModifiers.RightSuper;
        }

        return result;
    }

    private static GhosttyVtKeyAction MapKeyAction(TerminalKeyAction action) => action switch
    {
        TerminalKeyAction.Release => GhosttyVtKeyAction.Release,
        TerminalKeyAction.Press => GhosttyVtKeyAction.Press,
        TerminalKeyAction.Repeat => GhosttyVtKeyAction.Repeat,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown key action."),
    };

    private static GhosttyVtKey MapPhysicalKey(TerminalPhysicalKey key)
    {
        // TerminalPhysicalKey is copied in order from Ghostty's public W3C-key
        // C enum. Keeping the cast at this one boundary makes ABI drift visible
        // to the encoder integration tests instead of spreading native values
        // through the application layer.
        return new GhosttyVtKey((int)key);
    }

    private static GhosttyVtMouseButton MapMouseButton(TerminalMouseButton button) => button switch
    {
        TerminalMouseButton.Left => GhosttyVtMouseButton.Left,
        TerminalMouseButton.Middle => GhosttyVtMouseButton.Middle,
        TerminalMouseButton.Right => GhosttyVtMouseButton.Right,
        TerminalMouseButton.WheelUp => GhosttyVtMouseButton.Four,
        TerminalMouseButton.WheelDown => GhosttyVtMouseButton.Five,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    private static GhosttyVtKey MapKey(TerminalKey key) => new(key switch
    {
        TerminalKey.Backspace => 53,
        TerminalKey.Enter => 58,
        TerminalKey.Space => 63,
        TerminalKey.Tab => 64,
        TerminalKey.Delete => 68,
        TerminalKey.End => 69,
        TerminalKey.Home => 71,
        TerminalKey.Insert => 72,
        TerminalKey.PageDown => 73,
        TerminalKey.PageUp => 74,
        TerminalKey.Down => 75,
        TerminalKey.Left => 76,
        TerminalKey.Right => 77,
        TerminalKey.Up => 78,
        TerminalKey.Escape => 120,
        >= TerminalKey.F1 and <= TerminalKey.F20 => 121 + key - TerminalKey.F1,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown terminal key."),
    });
}
