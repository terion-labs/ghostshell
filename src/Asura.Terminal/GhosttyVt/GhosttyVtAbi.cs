using System.Runtime.InteropServices;

namespace Asura.Terminal.GhosttyVt;

internal static class GhosttyVtAbi
{
    internal static bool TryValidateManagedLayouts(out string detail)
    {
        if (IntPtr.Size != 8)
        {
            detail = "Asura currently packages libghostty-vt for 64-bit processes only.";
            return false;
        }

        (string TypeName, int ActualSize, int ExpectedSize)[] layouts =
        [
            (nameof(GhosttyVtString), Marshal.SizeOf<GhosttyVtString>(), 16),
            (nameof(GhosttyVtColorRgb), Marshal.SizeOf<GhosttyVtColorRgb>(), 3),
            (nameof(GhosttyVtStyleColorValue), Marshal.SizeOf<GhosttyVtStyleColorValue>(), 8),
            (nameof(GhosttyVtStyleColor), Marshal.SizeOf<GhosttyVtStyleColor>(), 16),
            (nameof(GhosttyVtStyle), Marshal.SizeOf<GhosttyVtStyle>(), 72),
            (nameof(GhosttyVtSemanticPromptEvent), Marshal.SizeOf<GhosttyVtSemanticPromptEvent>(), 24),
            (nameof(GhosttyVtTerminalScrollbar), Marshal.SizeOf<GhosttyVtTerminalScrollbar>(), 24),
            (nameof(GhosttyVtScrollViewport), Marshal.SizeOf<GhosttyVtScrollViewport>(), 24),
            (nameof(GhosttyVtRenderRowSelection), Marshal.SizeOf<GhosttyVtRenderRowSelection>(), 16),
            (nameof(GhosttyVtRenderStateColors), Marshal.SizeOf<GhosttyVtRenderStateColors>(), 792),
            (nameof(GhosttyVtPointCoordinate), Marshal.SizeOf<GhosttyVtPointCoordinate>(), 8),
            (nameof(GhosttyVtPoint), Marshal.SizeOf<GhosttyVtPoint>(), 24),
            (nameof(GhosttyVtGridRef), Marshal.SizeOf<GhosttyVtGridRef>(), 24),
            (nameof(GhosttyVtSelection), Marshal.SizeOf<GhosttyVtSelection>(), 64),
            (nameof(GhosttyVtTerminalSearchOptions), Marshal.SizeOf<GhosttyVtTerminalSearchOptions>(), 40),
            (nameof(GhosttyVtTerminalSearchResult), Marshal.SizeOf<GhosttyVtTerminalSearchResult>(), 32),
            (nameof(GhosttyVtSelectWordOptions), Marshal.SizeOf<GhosttyVtSelectWordOptions>(), 48),
            (nameof(GhosttyVtSelectWordBetweenOptions), Marshal.SizeOf<GhosttyVtSelectWordBetweenOptions>(), 72),
            (nameof(GhosttyVtSelectLineOptions), Marshal.SizeOf<GhosttyVtSelectLineOptions>(), 56),
            (nameof(GhosttyVtSelectionFormatOptions), Marshal.SizeOf<GhosttyVtSelectionFormatOptions>(), 24),
            (nameof(GhosttyVtKey), Marshal.SizeOf<GhosttyVtKey>(), 4),
            (nameof(GhosttyVtMode), Marshal.SizeOf<GhosttyVtMode>(), 2),
            (nameof(GhosttyVtMousePosition), Marshal.SizeOf<GhosttyVtMousePosition>(), 8),
            (nameof(GhosttyVtMouseEncoderSize), Marshal.SizeOf<GhosttyVtMouseEncoderSize>(), 40),
            (nameof(GhosttyVtKittyPlacementRenderInfo), Marshal.SizeOf<GhosttyVtKittyPlacementRenderInfo>(), 56),
            (nameof(GhosttyVtKittyVirtualPlacementRenderInfo), Marshal.SizeOf<GhosttyVtKittyVirtualPlacementRenderInfo>(), 64),
        ];

        foreach (var (typeName, actualSize, expectedSize) in layouts)
        {
            if (actualSize == expectedSize)
            {
                continue;
            }

            detail = $"Managed ABI layout mismatch for {typeName}: expected {expectedSize}, found {actualSize}.";
            return false;
        }

        detail = "Managed libghostty-vt ABI layouts are valid.";
        return true;
    }
}
