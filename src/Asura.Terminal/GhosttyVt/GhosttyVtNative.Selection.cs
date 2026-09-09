using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

internal static unsafe partial class GhosttyVtNative
{
    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_search")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSearch(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtTerminalSearchOptions* options,
        GhosttyVtTerminalSearchResult* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_select_word")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectWord(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelectWordOptions* options,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_select_word_between")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectWordBetween(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelectWordBetweenOptions* options,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_select_line")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectLine(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelectLineOptions* options,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_select_all")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectAll(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_select_output")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectOutput(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtGridRef reference,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_selection_format_buf")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectionFormat(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelectionFormatOptions options,
        byte* output,
        nuint outputLength,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_selection_adjust")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectionAdjust(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* selection,
        GhosttyVtSelectionAdjust adjustment);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_selection_order")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectionOrder(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* selection,
        GhosttyVtSelectionOrder* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_selection_ordered")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectionOrdered(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* selection,
        GhosttyVtSelectionOrder desired,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_selection_contains")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectionContains(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* selection,
        GhosttyVtPoint point,
        byte* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_selection_equal")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSelectionEqual(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* left,
        GhosttyVtSelection* right,
        byte* output);
}
