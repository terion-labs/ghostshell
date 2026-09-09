using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Asura.Terminal.GhosttyVt;

/// <summary>
/// Thin declarations over Ghostty's unstable C ABI. The native revision is pinned by
/// Asura's native build; higher layers must not infer ABI compatibility from a
/// matching semantic version alone.
/// </summary>
internal static unsafe partial class GhosttyVtNative
{
    internal const string LibraryName = "ghostty-vt";

    static GhosttyVtNative()
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(GhosttyVtNative).Assembly);
        loadContext?.ResolvingUnmanagedDll += ResolveLibrary;
    }

    private static nint ResolveLibrary(Assembly assembly, string libraryName)
    {
        if (!ReferenceEquals(assembly, typeof(GhosttyVtNative).Assembly) ||
            !string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        return GhosttyVtRuntimeProbe.TryLoadConfiguredRuntime(out var handle) ? handle : 0;
    }

    [LibraryImport(LibraryName, EntryPoint = "ghostty_build_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult BuildInfo(GhosttyVtBuildInfo data, void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_asura_extension_abi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AsuraExtensionAbi();

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalNew(
        nint allocator,
        out nint terminal,
        ushort columns,
        ushort rows);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalFree(nint terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalReset(GhosttyVtTerminalHandle terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalResize(
        GhosttyVtTerminalHandle terminal,
        ushort columns,
        ushort rows,
        uint cellWidthPixels,
        uint cellHeightPixels);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalSet(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtTerminalOption option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_vt_write")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalWrite(
        GhosttyVtTerminalHandle terminal,
        byte* data,
        nuint length);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_scroll_viewport")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TerminalScrollViewport(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtScrollViewport behavior);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_mode_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalModeGet(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtMode mode,
        byte* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_mode_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalModeSet(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtMode mode,
        byte value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalGet(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtTerminalData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalGetMulti(
        GhosttyVtTerminalHandle terminal,
        nuint count,
        GhosttyVtTerminalData* keys,
        void** values,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_grid_ref")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalGridRef(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtPoint point,
        GhosttyVtGridRef* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_grid_ref_track")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalGridRefTrack(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtPoint point,
        out nint output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_tracked_grid_ref_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void TrackedGridRefFree(nint reference);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_tracked_grid_ref_point")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TrackedGridRefPoint(
        GhosttyVtTrackedGridRefHandle reference,
        GhosttyVtPointTag tag,
        GhosttyVtPointCoordinate* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_terminal_point_from_grid_ref")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult TerminalPointFromGridRef(
        GhosttyVtTerminalHandle terminal,
        GhosttyVtGridRef* reference,
        GhosttyVtPointTag tag,
        GhosttyVtPointCoordinate* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_grid_ref_cell")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult GridRefCell(GhosttyVtGridRef* reference, ulong* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_grid_ref_row")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult GridRefRow(GhosttyVtGridRef* reference, ulong* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_grid_ref_graphemes")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult GridRefGraphemes(
        GhosttyVtGridRef* reference,
        uint* buffer,
        nuint bufferLength,
        nuint* outputLength);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_grid_ref_hyperlink_uri")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult GridRefHyperlinkUri(
        GhosttyVtGridRef* reference,
        byte* buffer,
        nuint bufferLength,
        nuint* outputLength);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_grid_ref_style")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult GridRefStyle(
        GhosttyVtGridRef* reference,
        GhosttyVtStyle* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_cell_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult CellGet(
        ulong cell,
        GhosttyVtCellData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_row_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RowGet(
        ulong row,
        GhosttyVtRowData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_style_default")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void StyleDefault(GhosttyVtStyle* style);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_style_is_default")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool StyleIsDefault(GhosttyVtStyle* style);
}
