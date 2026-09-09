using System.Runtime.InteropServices;
using Asura.Application;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Asura.App.Views;

/// <summary>
/// Reconciles Avalonia's cached render scale with AppKit after display wake.
///
/// AppKit can enumerate displays in more than one step while waking. Avalonia
/// caches the scale delivered by <c>viewDidChangeBackingProperties</c>; if that
/// callback observes the intermediate value, the whole client area stays at
/// the wrong scale until another native backing change occurs.
/// </summary>
internal static class MacOsWindowBackingScale
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AvaloniaRenderView = "AvnView";

    public static bool TryReconcile(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return false;
        }

        var nativeScale = SendDouble(handle.NSWindow, Selector("backingScaleFactor"));
        if (!double.IsFinite(nativeScale) || nativeScale <= 0)
        {
            return false;
        }

        var contentView = SendId(handle.NSWindow, Selector("contentView"));
        var renderView = FindDescendantOfClass(contentView, AvaloniaRenderView);
        if (renderView == 0)
        {
            return false;
        }

        // This is the same native callback Avalonia relies on. It must also run
        // when the numeric scale agrees: after display wake AppKit can leave the
        // AvnView render target at an intermediate pixel size while both AppKit
        // and Avalonia already report the final scale. Replaying the callback
        // recalculates that pixel size and republishes the scale through
        // Avalonia's own rendering path instead of applying an app-level
        // transform that would disagree with input and popup coordinates.
        var managedScale = window.RenderScaling;
        SendVoid(renderView, Selector("viewDidChangeBackingProperties"));

        if (Math.Abs(nativeScale - managedScale) >= 0.001)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "display.macos.backing-scale-reconciled",
                SecretSafeDiagnosticKind.Unexpected);
        }

        return true;
    }

    private static nint FindDescendantOfClass(nint view, string className)
    {
        if (view == 0)
        {
            return 0;
        }

        if (string.Equals(ClassNameOf(view), className, StringComparison.Ordinal))
        {
            return view;
        }

        var subviews = SendId(view, Selector("subviews"));
        var count = subviews == 0 ? 0 : SendNUInt(subviews, Selector("count"));
        for (nuint index = 0; index < count; index++)
        {
            var child = SendIdArgument(
                subviews,
                Selector("objectAtIndex:"),
                index);
            var match = FindDescendantOfClass(child, className);
            if (match != 0)
            {
                return match;
            }
        }

        return 0;
    }

    private static string ClassNameOf(nint instance) => instance == 0
        ? string.Empty
        : Marshal.PtrToStringAnsi(object_getClassName(instance)) ?? string.Empty;

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdArgument(
        nint receiver,
        nint selector,
        nuint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern double SendDouble(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);
}
