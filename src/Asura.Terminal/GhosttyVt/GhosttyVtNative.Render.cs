using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

internal static unsafe partial class GhosttyVtNative
{
    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateNew(nint allocator, out nint state);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RenderStateFree(nint state);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_update")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateUpdate(
        GhosttyVtRenderStateHandle state,
        GhosttyVtTerminalHandle terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_begin_update")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateBeginUpdate(
        GhosttyVtRenderStateHandle state,
        GhosttyVtTerminalHandle terminal);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_end_update")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateEndUpdate(GhosttyVtRenderStateHandle state);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateGet(
        GhosttyVtRenderStateHandle state,
        GhosttyVtRenderStateData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateGetMulti(
        GhosttyVtRenderStateHandle state,
        nuint count,
        GhosttyVtRenderStateData* keys,
        void** values,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateSet(
        GhosttyVtRenderStateHandle state,
        GhosttyVtRenderStateOption option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_colors_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderStateColorsGet(
        GhosttyVtRenderStateHandle state,
        GhosttyVtRenderStateColors* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_iterator_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowIteratorNew(nint allocator, out nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_iterator_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RenderRowIteratorFree(nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_iterator_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool RenderRowIteratorNext(GhosttyVtRenderRowIteratorHandle iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowGet(
        GhosttyVtRenderRowIteratorHandle iterator,
        GhosttyVtRenderRowData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowGetMulti(
        GhosttyVtRenderRowIteratorHandle iterator,
        nuint count,
        GhosttyVtRenderRowData* keys,
        void** values,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowSet(
        GhosttyVtRenderRowIteratorHandle iterator,
        GhosttyVtRenderRowOption option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowCellsNew(nint allocator, out nint cells);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RenderRowCellsFree(nint cells);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool RenderRowCellsNext(GhosttyVtRenderRowCellsHandle cells);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_select")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowCellsSelect(
        GhosttyVtRenderRowCellsHandle cells,
        ushort column);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowCellsGet(
        GhosttyVtRenderRowCellsHandle cells,
        GhosttyVtRenderCellData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_render_state_row_cells_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult RenderRowCellsGetMulti(
        GhosttyVtRenderRowCellsHandle cells,
        nuint count,
        GhosttyVtRenderCellData* keys,
        void** values,
        nuint* written);
}
