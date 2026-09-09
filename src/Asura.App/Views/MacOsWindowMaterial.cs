using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Asura.App.Views;

/// <summary>
/// Chooses which of the platform's materials the window sits on.
///
/// Avalonia creates the view but pins it to <c>NSVisualEffectMaterialLight</c>
/// — a fixed, long-deprecated light material, with nothing managed to change
/// it. A dark shell over a light material is why the base surface had to be
/// most of the way opaque to look right: the fill was doing the work the
/// material should be doing.
///
/// It also leaves the view's state at its default, which follows whether the
/// window is active, so the glass goes flat whenever focus moves elsewhere.
/// </summary>
internal static class MacOsWindowMaterial
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    /// <summary>Avalonia's macOS content view, the parent of the material.</summary>
    private const string AvaloniaContentView = "AutoFitContentView";

    /// <summary>NSVisualEffectBlendingModeBehindWindow.</summary>
    private const nint BlendsBehindWindow = 0;

    /// <summary>NSVisualEffectStateActive: the glass does not dull when unfocused.</summary>
    private const nint AlwaysActive = 1;

    public static bool TrySit(Window window, MacOsMaterial material)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return false;
        }

        try
        {
            var contentView = SendId(handle.NSWindow, Selector("contentView"));
            if (contentView == 0)
            {
                return false;
            }

            var root = contentView;
            for (var next = SendId(root, Selector("superview"));
                next != 0;
                next = SendId(root, Selector("superview")))
            {
                root = next;
            }

            var avaloniaContent = FindDescendantOfClass(root, AvaloniaContentView);
            if (avaloniaContent == 0)
            {
                return false;
            }

            foreach (var subview in SubviewsOf(avaloniaContent))
            {
                // The window-wide one, not the title bar's: they share a class
                // and the blending mode is what tells them apart.
                if (!string.Equals(ClassNameOf(subview), "NSVisualEffectView", StringComparison.Ordinal)
                    || SendNInt(subview, Selector("blendingMode")) != BlendsBehindWindow)
                {
                    continue;
                }

                SendNIntArgument(subview, Selector("setMaterial:"), (nint)material);
                SendNIntArgument(subview, Selector("setState:"), AlwaysActive);
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    private static nint FindDescendantOfClass(nint view, string className)
    {
        if (string.Equals(ClassNameOf(view), className, StringComparison.Ordinal))
        {
            return view;
        }

        foreach (var subview in SubviewsOf(view))
        {
            var found = FindDescendantOfClass(subview, className);
            if (found != 0)
            {
                return found;
            }
        }

        return 0;
    }

    private static IEnumerable<nint> SubviewsOf(nint view)
    {
        var subviews = SendId(view, Selector("subviews"));
        if (subviews == 0)
        {
            yield break;
        }

        var count = SendNUInt(subviews, Selector("count"));
        for (nuint index = 0; index < count; index++)
        {
            var subview = SendIdAtIndex(subviews, Selector("objectAtIndex:"), index);
            if (subview != 0)
            {
                yield return subview;
            }
        }
    }

    private static string ClassNameOf(nint instance) => instance == 0
        ? string.Empty
        : Marshal.PtrToStringAnsi(object_getClassName(instance)) ?? string.Empty;

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNIntArgument(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdAtIndex(nint receiver, nint selector, nuint index);
}

/// <summary>
/// The NSVisualEffectMaterial values worth a window's base surface.
/// </summary>
internal enum MacOsMaterial : long
{
    /// <summary>
    /// A lightweight transient surface. It preserves substantially more of
    /// the backdrop than a window-base material while retaining AppKit's full
    /// blur, which makes it appropriate for Quick Terminal's drop-down panel.
    /// </summary>
    Popover = 6,

    /// <summary>
    /// AppKit's sidebar glass, used for persistent control and navigation
    /// regions inside Quick Terminal.
    /// </summary>
    Sidebar = 7,

    /// <summary>
    /// AppKit's heads-up-display glass. On the current macOS compositor it is
    /// the clearest useful full-window Quick Terminal backdrop.
    /// </summary>
    HudWindow = 13,

    /// <summary>
    /// The base a window itself sits on. Flatter and less tinted than
    /// NSVisualEffectMaterialHUDWindow (13), which is built for floating
    /// panels and leans darker — both were tried against a bright backdrop.
    /// </summary>
    UnderWindowBackground = 21,
}
