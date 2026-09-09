using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

internal static unsafe partial class GhosttyVtNative
{
    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyGraphicsGet(
        nint graphics,
        GhosttyVtKittyGraphicsData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_image")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint KittyGraphicsImage(nint graphics, uint imageId);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_image_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyGraphicsImageGet(
        nint image,
        GhosttyVtKittyImageData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_image_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyGraphicsImageGetMulti(
        nint image,
        nuint count,
        GhosttyVtKittyImageData* keys,
        void** values,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_iterator_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementIteratorNew(
        nint allocator,
        out nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_iterator_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KittyPlacementIteratorFree(nint iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_iterator_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementIteratorSet(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        GhosttyVtKittyPlacementIteratorOption option,
        void* value);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool KittyPlacementNext(GhosttyVtKittyPlacementIteratorHandle iterator);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementGet(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        GhosttyVtKittyPlacementData data,
        void* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_get_multi")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementGetMulti(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nuint count,
        GhosttyVtKittyPlacementData* keys,
        void** values,
        nuint* written);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_render_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementRenderInfo(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nint image,
        GhosttyVtTerminalHandle terminal,
        GhosttyVtKittyPlacementRenderInfo* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_rect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementRectangle(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nint image,
        GhosttyVtTerminalHandle terminal,
        GhosttyVtSelection* output);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_pixel_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementPixelSize(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nint image,
        GhosttyVtTerminalHandle terminal,
        uint* width,
        uint* height);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_grid_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementGridSize(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nint image,
        GhosttyVtTerminalHandle terminal,
        uint* columns,
        uint* rows);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_viewport_pos")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementViewportPosition(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nint image,
        GhosttyVtTerminalHandle terminal,
        int* column,
        int* row);

    [LibraryImport(LibraryName, EntryPoint = "ghostty_kitty_graphics_placement_source_rect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyPlacementSourceRectangle(
        GhosttyVtKittyPlacementIteratorHandle iterator,
        nint image,
        uint* x,
        uint* y,
        uint* width,
        uint* height);

    [LibraryImport(
        LibraryName,
        EntryPoint = "ghostty_kitty_graphics_virtual_placement_iterator_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyVirtualPlacementIteratorNew(
        nint allocator,
        out nint iterator);

    [LibraryImport(
        LibraryName,
        EntryPoint = "ghostty_kitty_graphics_virtual_placement_iterator_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void KittyVirtualPlacementIteratorFree(nint iterator);

    [LibraryImport(
        LibraryName,
        EntryPoint = "ghostty_kitty_graphics_virtual_placement_iterator_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyVirtualPlacementIteratorReset(
        GhosttyVtKittyVirtualPlacementIteratorHandle iterator,
        GhosttyVtTerminalHandle terminal,
        uint cellWidth,
        uint cellHeight);

    [LibraryImport(
        LibraryName,
        EntryPoint = "ghostty_kitty_graphics_virtual_placement_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GhosttyVtResult KittyVirtualPlacementNext(
        GhosttyVtKittyVirtualPlacementIteratorHandle iterator,
        GhosttyVtKittyVirtualPlacementRenderInfo* output);
}
