using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

internal static unsafe partial class GhosttyVtNative
{
    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KeyEventNew(nint allocator, out nint keyEvent);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventFree(nint keyEvent);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_action")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetAction(
        GhosttyVtKeyEventHandle keyEvent,
        GhosttyVtKeyAction action);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_key")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetKey(GhosttyVtKeyEventHandle keyEvent, GhosttyVtKey key);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_mods")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetModifiers(
        GhosttyVtKeyEventHandle keyEvent,
        GhosttyVtModifiers modifiers);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_consumed_mods")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetConsumedModifiers(
        GhosttyVtKeyEventHandle keyEvent,
        GhosttyVtModifiers modifiers);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_composing")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetComposing(GhosttyVtKeyEventHandle keyEvent, byte composing);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetUtf8(
        GhosttyVtKeyEventHandle keyEvent,
        byte* utf8,
        nuint length);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_get_utf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint KeyEventGetUtf8(GhosttyVtKeyEventHandle keyEvent, nuint* length);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_event_set_unshifted_codepoint")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEventSetUnshiftedCodepoint(
        GhosttyVtKeyEventHandle keyEvent,
        uint codepoint);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_encoder_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KeyEncoderNew(nint allocator, out nint encoder);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_encoder_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEncoderFree(nint encoder);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_encoder_setopt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEncoderSetOption(
        GhosttyVtKeyEncoderHandle encoder,
        GhosttyVtKeyEncoderOption option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_encoder_setopt_from_terminal")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KeyEncoderSetOptionsFromTerminal(
        GhosttyVtKeyEncoderHandle encoder,
        GhosttyVtTerminalHandle terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_key_encoder_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KeyEncoderEncode(
        GhosttyVtKeyEncoderHandle encoder,
        GhosttyVtKeyEventHandle keyEvent,
        byte* output,
        nuint outputLength,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult MouseEventNew(nint allocator, out nint mouseEvent);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEventFree(nint mouseEvent);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_set_action")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEventSetAction(
        GhosttyVtMouseEventHandle mouseEvent,
        GhosttyVtMouseAction action);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_set_button")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEventSetButton(
        GhosttyVtMouseEventHandle mouseEvent,
        GhosttyVtMouseButton button);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_clear_button")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEventClearButton(GhosttyVtMouseEventHandle mouseEvent);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_get_button")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool MouseEventGetButton(
        GhosttyVtMouseEventHandle mouseEvent,
        out GhosttyVtMouseButton button);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_set_mods")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEventSetModifiers(
        GhosttyVtMouseEventHandle mouseEvent,
        GhosttyVtModifiers modifiers);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_event_set_position")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEventSetPosition(
        GhosttyVtMouseEventHandle mouseEvent,
        GhosttyVtMousePosition position);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_encoder_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult MouseEncoderNew(nint allocator, out nint encoder);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_encoder_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEncoderFree(nint encoder);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_encoder_setopt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEncoderSetOption(
        GhosttyVtMouseEncoderHandle encoder,
        GhosttyVtMouseEncoderOption option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_encoder_setopt_from_terminal")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEncoderSetOptionsFromTerminal(
        GhosttyVtMouseEncoderHandle encoder,
        GhosttyVtTerminalHandle terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_encoder_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MouseEncoderReset(GhosttyVtMouseEncoderHandle encoder);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_mouse_encoder_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult MouseEncoderEncode(
        GhosttyVtMouseEncoderHandle encoder,
        GhosttyVtMouseEventHandle mouseEvent,
        byte* output,
        nuint outputLength,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_focus_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult FocusEncode(
        GhosttyVtFocusEvent focusEvent,
        byte* output,
        nuint outputLength,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_paste_is_safe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool PasteIsSafe(byte* data, nuint length);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_paste_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult PasteEncode(
        byte* data,
        nuint dataLength,
        byte bracketed,
        byte* output,
        nuint outputLength,
        nuint* written);
}
