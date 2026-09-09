using Microsoft.Win32.SafeHandles;

namespace Asura.Terminal.GhosttyVt;

internal sealed class GhosttyVtTerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtTerminalHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtTerminalHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.TerminalFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtRenderStateHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtRenderStateHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtRenderStateHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.RenderStateFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtRenderRowIteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtRenderRowIteratorHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtRenderRowIteratorHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.RenderRowIteratorFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtRenderRowCellsHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtRenderRowCellsHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtRenderRowCellsHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.RenderRowCellsFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKeyEventHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtKeyEventHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKeyEventHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KeyEventFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKeyEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtKeyEncoderHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKeyEncoderHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KeyEncoderFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtMouseEventHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtMouseEventHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtMouseEventHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.MouseEventFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtMouseEncoderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtMouseEncoderHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtMouseEncoderHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.MouseEncoderFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKittyPlacementIteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtKittyPlacementIteratorHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKittyPlacementIteratorHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KittyPlacementIteratorFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtKittyVirtualPlacementIteratorHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtKittyVirtualPlacementIteratorHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtKittyVirtualPlacementIteratorHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.KittyVirtualPlacementIteratorFree(handle);
        return true;
    }
}

internal sealed class GhosttyVtTrackedGridRefHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GhosttyVtTrackedGridRefHandle()
        : base(ownsHandle: true)
    {
    }

    internal GhosttyVtTrackedGridRefHandle(nint handle)
        : this() => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        GhosttyVtNative.TrackedGridRefFree(handle);
        return true;
    }
}
