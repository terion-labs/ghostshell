using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Asura.Application;
using Asura.Core;
using Asura.Terminal.GhosttyVt;

namespace Asura.Terminal;

internal sealed partial class GhosttyVtTerminalSession
{
    private const ulong KittyImageStorageLimit = 256UL * 1024 * 1024;
    private const nuint MaximumApcBytes = 32 * 1024 * 1024;

    private unsafe void ConfigureTerminalUnsafe(TerminalRenderProfileSnapshot profile)
    {
        var userdata = (void*)GCHandle.ToIntPtr(_selfHandle);
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.UserData,
                userdata),
            "set terminal userdata");

        var callback =
            (void*)(delegate* unmanaged[Cdecl]<nint, nint, byte*, nuint, void>)&OnNativeWritePty;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.WritePty,
                callback),
            "set terminal protocol-response callback");

        var semanticPromptCallback =
            (void*)(delegate* unmanaged[Cdecl]<
                nint,
                nint,
                GhosttyVtSemanticPromptEvent*,
                void>)&OnNativeSemanticPrompt;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.SemanticPrompt,
                semanticPromptCallback),
            "set terminal shell-integration callback");

        var desktopNotificationCallback =
            (void*)(delegate* unmanaged[Cdecl]<
                nint,
                nint,
                GhosttyVtDesktopNotificationEvent*,
                void>)&OnNativeDesktopNotification;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.DesktopNotification,
                desktopNotificationCallback),
            "set terminal desktop-notification callback");

        var bellCallback =
            (void*)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnNativeBell;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.Bell,
                bellCallback),
            "set terminal bell callback");

        ConfigurePresentationUnsafe(profile);

        ulong imageLimit = KittyImageStorageLimit;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.KittyImageStorageLimit,
                &imageLimit),
            "set Kitty image storage limit");

        byte disabled = 0;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.KittyImageMediumFile,
                &disabled),
            "disable unrestricted Kitty file loading");
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.KittyImageMediumSharedMemory,
                &disabled),
            "disable Kitty shared-memory loading");

        nuint maximumApc = MaximumApcBytes;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.ApcMaximumBytes,
                &maximumApc),
            "set APC memory bound");
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.ApcMaximumKittyBytes,
                &maximumApc),
            "set Kitty APC memory bound");
    }

    private unsafe void ConfigurePresentationUnsafe(TerminalRenderProfileSnapshot profile)
    {
        var foreground = MapRgb(profile.Palette.Foreground);
        var background = MapRgb(profile.Palette.Background);
        var cursor = MapRgb(profile.Palette.Cursor);
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.ForegroundColor,
                &foreground),
            "set terminal foreground");
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.BackgroundColor,
                &background),
            "set terminal background");
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.CursorColor,
                &cursor),
            "set terminal cursor color");

        GhosttyVtColorRgb* palette = stackalloc GhosttyVtColorRgb[256];
        FillPalette(profile.Palette, palette);
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.ColorPalette,
                palette),
            "set terminal palette");

        // Ghostty initializes terminals with a separate 10 KB byte ceiling.
        // The user-facing profile is line-based, so leaving that default in
        // place would silently prune history long before the configured line
        // bound (and make full-history search impossible).
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.ScrollbackMaximumBytes,
                null),
            "remove the implicit scrollback byte bound");

        nuint scrollbackLines = checked((nuint)profile.ScrollbackLines);
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.ScrollbackMaximumLines,
                &scrollbackLines),
            "set scrollback line bound");

        var cursorStyle = profile.CursorStyle switch
        {
            TerminalCursorStyle.Bar => GhosttyVtTerminalCursorStyle.Bar,
            TerminalCursorStyle.Block => GhosttyVtTerminalCursorStyle.Block,
            TerminalCursorStyle.Underline => GhosttyVtTerminalCursorStyle.Underline,
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile.CursorStyle,
                "Unknown terminal cursor style."),
        };
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.DefaultCursorStyle,
                &cursorStyle),
            "set default cursor style");

        byte cursorBlink = profile.CursorBlink ? (byte)1 : (byte)0;
        EnsureSuccess(
            GhosttyVtNative.TerminalSet(
                _terminal,
                GhosttyVtTerminalOption.DefaultCursorBlink,
                &cursorBlink),
            "set default cursor blink");
    }

    private void ResizeUnsafe(ViewportDescriptor viewport)
    {
        var fontSize = _renderProfile.FontSize;
        var lineHeight = _renderProfile.LineHeight;
        var columns = viewport.Columns
            ?? (viewport.LogicalWidth > 0
                ? (int)Math.Floor(viewport.LogicalWidth / Math.Max(1, fontSize * 0.62))
                : _columns);
        var rows = viewport.Rows
            ?? (viewport.LogicalHeight > 0
                ? (int)Math.Floor(viewport.LogicalHeight / Math.Max(1, fontSize * lineHeight * 1.2))
                : _rows);
        columns = Math.Clamp(columns, 2, ushort.MaxValue);
        rows = Math.Clamp(rows, 1, ushort.MaxValue);

        var scale = double.IsFinite(viewport.RenderScale) && viewport.RenderScale > 0
            ? viewport.RenderScale
            : 1;
        var logicalCellWidth = viewport.LogicalWidth > 0
            ? viewport.LogicalWidth / columns
            : fontSize * 0.62;
        var logicalCellHeight = viewport.LogicalHeight > 0
            ? viewport.LogicalHeight / rows
            : fontSize * lineHeight * 1.2;
        var cellWidthPixels = checked((uint)Math.Clamp(
            (int)Math.Round(logicalCellWidth * scale),
            1,
            16_384));
        var cellHeightPixels = checked((uint)Math.Clamp(
            (int)Math.Round(logicalCellHeight * scale),
            1,
            16_384));

        if (columns == _columns
            && rows == _rows
            && cellWidthPixels == _cellWidthPixels
            && cellHeightPixels == _cellHeightPixels
            && Math.Abs(scale - _renderScale) < 0.0001)
        {
            return;
        }

        EnsureSuccess(
            GhosttyVtNative.TerminalResize(
                _terminal,
                checked((ushort)columns),
                checked((ushort)rows),
                cellWidthPixels,
                cellHeightPixels),
            "resize terminal");
        _pty.Resize(columns, rows);
        _columns = columns;
        _rows = rows;
        _cellWidthPixels = cellWidthPixels;
        _cellHeightPixels = cellHeightPixels;
        _renderScale = scale;
        MarkContentChangedUnsafe();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeWritePty(
        nint terminal,
        nint userdata,
        byte* data,
        nuint length)
    {
        _ = terminal;
        if (userdata == 0 || data is null || length == 0 || length > int.MaxValue)
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userdata).Target is not GhosttyVtTerminalSession session)
            {
                return;
            }

            var response = new byte[checked((int)length)];
            new ReadOnlySpan<byte>(data, response.Length).CopyTo(response);
            session.EnqueueProtocolResponse(response);
        }
        catch
        {
            // Exceptions cannot cross the native callback boundary. The read loop
            // notices the overflow flag immediately after the VT write and fails
            // the session instead of continuing after a lost protocol response.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeSemanticPrompt(
        nint terminal,
        nint userdata,
        GhosttyVtSemanticPromptEvent* eventData)
    {
        _ = terminal;
        if (userdata == 0
            || eventData is null
            || eventData->Size < (nuint)sizeof(GhosttyVtSemanticPromptEvent))
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userdata).Target is GhosttyVtTerminalSession session)
            {
                session.CaptureSemanticPromptUnsafe(*eventData);
            }
        }
        catch
        {
            // Native callbacks cannot unwind into Ghostty. A malformed marker
            // is deliberately dropped; terminal parsing and rendering continue.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeDesktopNotification(
        nint terminal,
        nint userdata,
        GhosttyVtDesktopNotificationEvent* notification)
    {
        _ = terminal;
        if (userdata == 0
            || notification is null
            || notification->Size < (nuint)sizeof(GhosttyVtDesktopNotificationEvent))
        {
            return;
        }

        try
        {
            if (notification->Title.Length
                    > (nuint)PanelNotificationTextBudget.MaximumTitleUtf8Bytes
                || notification->Body.Length
                    > (nuint)PanelNotificationTextBudget.MaximumBodyUtf8Bytes)
            {
                return;
            }

            // Both strings are borrowed for the length of this call, so they are
            // copied here rather than anywhere downstream. Their native byte
            // lengths are checked first so hostile OSC content is never copied
            // into an oversized managed string.
            var title = notification->Title.CopyUtf8();
            var body = notification->Body.CopyUtf8();
            if (GCHandle.FromIntPtr(userdata).Target is GhosttyVtTerminalSession session)
            {
                if (!session.TryCaptureInteractiveStateNotification(
                        title,
                        body,
                        DateTimeOffset.UtcNow))
                {
                    session.PublishNotification(PanelNotificationKind.Notification, title, body);
                }
            }
        }
        catch
        {
            // Native callbacks cannot unwind into Ghostty. A malformed
            // notification is dropped; parsing and rendering continue.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeBell(nint terminal, nint userdata)
    {
        _ = terminal;
        if (userdata == 0)
        {
            return;
        }

        try
        {
            if (GCHandle.FromIntPtr(userdata).Target is GhosttyVtTerminalSession session)
            {
                session.PublishNotification(
                    PanelNotificationKind.Bell,
                    string.Empty,
                    string.Empty);
            }
        }
        catch
        {
            // As above: a bell is never worth breaking the terminal for.
        }
    }

    private unsafe void CaptureSemanticPromptUnsafe(GhosttyVtSemanticPromptEvent eventData)
    {
        const int maximumSemanticMarkers = 4_096;
        var kind = eventData.Phase switch
        {
            GhosttyVtSemanticPromptPhase.Prompt => TerminalCommandBoundaryKind.PromptStarted,
            GhosttyVtSemanticPromptPhase.Input => TerminalCommandBoundaryKind.CommandInputStarted,
            GhosttyVtSemanticPromptPhase.Executed => TerminalCommandBoundaryKind.CommandExecuted,
            GhosttyVtSemanticPromptPhase.Finished => TerminalCommandBoundaryKind.CommandFinished,
            _ => throw new ArgumentOutOfRangeException(
                nameof(eventData),
                eventData.Phase,
                "Unknown Ghostty shell-integration phase."),
        };
        _shellActivity = eventData.Phase == GhosttyVtSemanticPromptPhase.Executed
            ? TerminalShellActivityState.Running
            : TerminalShellActivityState.Idle;

        ushort cursorX = 0;
        ushort cursorY = 0;
        var screen = GhosttyVtTerminalScreen.Primary;
        EnsureSuccess(
            GhosttyVtNative.TerminalGet(
                _terminal,
                GhosttyVtTerminalData.CursorX,
                &cursorX),
            "read shell-event cursor column");
        EnsureSuccess(
            GhosttyVtNative.TerminalGet(
                _terminal,
                GhosttyVtTerminalData.CursorY,
                &cursorY),
            "read shell-event cursor row");
        EnsureSuccess(
            GhosttyVtNative.TerminalGet(
                _terminal,
                GhosttyVtTerminalData.ActiveScreen,
                &screen),
            "read shell-event screen");

        GhosttyVtTrackedGridRefHandle? reference = null;
        var point = new GhosttyVtPoint
        {
            Tag = GhosttyVtPointTag.Active,
            Value = new GhosttyVtPointValue
            {
                Coordinate = new GhosttyVtPointCoordinate
                {
                    X = cursorX,
                    Y = cursorY,
                },
            },
        };
        if (GhosttyVtNative.TerminalGridRefTrack(_terminal, point, out var handle)
            == GhosttyVtResult.Success)
        {
            reference = new GhosttyVtTrackedGridRefHandle(handle);
        }

        var sequence = ++_commandBoundarySequence;
        _semanticMarkers.Add(new SemanticMarker(
            new TerminalShellIntegrationEvent(
                sequence,
                kind,
                DateTimeOffset.UtcNow,
                eventData.HasExitStatus != 0 ? eventData.ExitStatus : null),
            screen,
            reference));
        if (_semanticMarkers.Count > maximumSemanticMarkers)
        {
            _semanticMarkers[0].Reference?.Dispose();
            _semanticMarkers.RemoveAt(0);
        }
    }

    private void EnqueueProtocolResponse(byte[] response)
    {
        var input = new QueuedTerminalInput(
            response,
            CancellationToken.None,
            protocolResponse: true);
        if (!_writes.Writer.TryWrite(input))
        {
            _protocolResponseOverflow = true;
            return;
        }

        _ = ObserveProtocolResponseAsync(input.Completion);
    }

    private static async Task ObserveProtocolResponseAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch
        {
            // The writer loop records the durable session failure. This detached
            // observer only prevents its acknowledgement task becoming unobserved.
        }
    }

    private static GhosttyVtColorRgb MapRgb(RgbColor color) =>
        new(color.Red, color.Green, color.Blue);

    private static unsafe void FillPalette(
        TerminalPalette source,
        GhosttyVtColorRgb* destination)
    {
        for (var index = 0; index < 16; index++)
        {
            destination[index] = MapRgb(source.AnsiColors[index]);
        }

        ReadOnlySpan<byte> cube = [0, 95, 135, 175, 215, 255];
        var paletteIndex = 16;
        for (var red = 0; red < cube.Length; red++)
        {
            for (var green = 0; green < cube.Length; green++)
            {
                for (var blue = 0; blue < cube.Length; blue++)
                {
                    destination[paletteIndex++] = new GhosttyVtColorRgb(
                        cube[red],
                        cube[green],
                        cube[blue]);
                }
            }
        }

        for (var index = 0; index < 24; index++)
        {
            var value = checked((byte)(8 + index * 10));
            destination[232 + index] = new GhosttyVtColorRgb(value, value, value);
        }
    }
}
