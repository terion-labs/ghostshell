using System.Runtime.InteropServices;
using Asura.Core;

namespace Asura.Application;

public sealed record NativeRendererHost(
    string HandleDescriptor,
    nint Handle,
    ViewportDescriptor Viewport,
    Func<NativeRendererKeyInput, bool>? KeyInterceptor = null,
    Func<NativeRendererPhysicalInput, bool>? PhysicalInputGate = null,
    // Presentation the host cannot express through Avalonia: a native child view
    // is not clipped by its Avalonia parent, and focus moving into one is
    // invisible to Avalonia's focus system.
    //
    // The radii are per corner because a terminal usually sits below a panel
    // header, so only its bottom corners are at the panel's edge. One radius for
    // all four rounded the two in the middle of the panel into notches.
    NativeRendererCorners Corners = default,
    Action? FocusObserver = null);

/// <summary>
/// Which of a native view's corners are at its host panel's edge, and how round.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NativeRendererCorners(
    double TopLeft,
    double TopRight,
    double BottomRight,
    double BottomLeft)
{
    public static NativeRendererCorners Uniform(double radius) =>
        new(radius, radius, radius, radius);

    /// <summary>A surface whose top edge is covered by a header.</summary>
    public static NativeRendererCorners BottomOnly(double radius) =>
        new(0, 0, radius, radius);

    public bool IsSquare =>
        TopLeft <= 0 && TopRight <= 0 && BottomRight <= 0 && BottomLeft <= 0;
}

/// <summary>
/// One physical key press raised by a native renderer before it reaches the terminal engine.
/// The interceptor is synchronous so the renderer can either consume the press or pass it through
/// unchanged. Implementations must return promptly and schedule longer-running work separately.
/// </summary>
public readonly record struct NativeRendererKeyInput(KeyStroke Stroke, bool IsRepeat);

/// <summary>
/// One physical input about to reach a native terminal renderer. The session host installs
/// the gate and synchronously reclaims the exact human attachment before returning true.
/// Implementations must not block on asynchronous client or transport work.
/// </summary>
public readonly record struct NativeRendererPhysicalInput(NativeRendererPhysicalInputKind Kind);

public enum NativeRendererPhysicalInputKind
{
    KeyDown = 0,
    KeyUp = 1,
    ModifiersChanged = 2,
    ImePreedit = 3,
    ImeCommit = 4,
    Paste = 5,
    MouseMove = 6,
    MouseDrag = 7,
    MouseButtonDown = 8,
    MouseButtonUp = 9,
    MouseScroll = 10,
}
