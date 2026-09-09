using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Asura.App.Views;

/// <summary>
/// Lets the shell's base surface run to the top edge of the window.
///
/// A translucent shell with an extended client area still showed a paler strip
/// across the top, exactly as tall as the title bar, and a hairline under it.
/// Turning the base surface opaque made both vanish, which places whatever
/// draws them <em>behind</em> the base rather than over it — visible only
/// through the fraction of the base that is not opaque. That rules out every
/// fill of ours, and it is why matching the band by hand never worked in
/// either direction.
///
/// They are Avalonia's. Its macOS content view keeps a title-bar material and
/// a separator box, and <c>SetExtendClientArea</c> reveals both whenever the
/// window has full decorations — the two are wired together in the backend
/// with nothing managed in between, so extending under the decorations and
/// having the decorations painted are the same switch. The shell wants the
/// first without the second, so the two views are hidden directly.
///
/// Only those two. The standard window buttons live in the title-bar container
/// and are not touched, so the window keeps its frame, rounded corners, resize
/// edges and traffic lights.
/// </summary>
internal static class MacOsWindowTitleBar
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    /// <summary>Avalonia's macOS content view, the parent of both views.</summary>
    private const string AvaloniaContentView = "AutoFitContentView";

    /// <summary>
    /// This desktop sizes a window's corners by what kind of window it is —
    /// 26pt with a toolbar, 20pt with a compact one, 16pt with only a title
    /// bar. The shell had 16 because it had no toolbar, which was the platform
    /// being right about what it had been asked to be.
    ///
    /// Unified, so the toolbar merges into the band the shell already draws
    /// its tabs across. Expanded is the older separate strip, and asking for
    /// that made the chrome taller without moving the corners.
    /// </summary>
    private static void SitAsTheWindowKind(nint nsWindow, MacOsWindowKind kind)
    {
        if (kind is MacOsWindowKind.TitleBarOnly)
        {
            SendIdArgument(nsWindow, Selector("setToolbar:"), 0);
            return;
        }

        if (SendId(nsWindow, Selector("toolbar")) == 0)
        {
            var toolbar = SendId(
                SendId(objc_getClass("NSToolbar"), Selector("alloc")),
                Selector("init"));
            if (toolbar == 0)
            {
                return;
            }

            SendBool(toolbar, Selector("setShowsBaselineSeparator:"), false);
            SendIdArgument(nsWindow, Selector("setToolbar:"), toolbar);
        }

        SendNIntArgument(
            nsWindow,
            Selector("setToolbarStyle:"),
            kind is MacOsWindowKind.CompactToolbar ? UnifiedCompactToolbar : UnifiedToolbar);
    }

    /// <summary>NSWindowToolbarStyleUnified: merged into the title bar, not a strip below it.</summary>
    private const nint UnifiedToolbar = 3;

    /// <summary>NSWindowToolbarStyleUnifiedCompact.</summary>
    private const nint UnifiedCompactToolbar = 4;

    /// <summary>NSWindowStyleMaskTexturedBackground, deprecated since 10.12.</summary>
    private const nuint TexturedBackground = 1 << 8;

    /// <summary>NSVisualEffectBlendingModeWithinWindow.</summary>
    private const nint BlendsWithinWindow = 1;

    /// <summary>
    /// Hides the title-bar material and its underline, and reports how many it
    /// found. Safe to call repeatedly: Avalonia re-shows them when the
    /// decorations or the full-screen state change.
    /// </summary>
    public static MacOsTitleBarOutcome TryLetTheBaseSurfaceRunToTheTop(
        Window window,
        MacOsWindowKind kind)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return MacOsTitleBarOutcome.Unreachable;
        }

        try
        {
            var contentView = SendId(handle.NSWindow, Selector("contentView"));
            if (contentView == 0)
            {
                return MacOsTitleBarOutcome.NoContentView;
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
                return MacOsTitleBarOutcome.NoContentView;
            }

            // Avalonia flags an extended client area as a textured window:
            //
            //     if (_isClientAreaExtended)
            //         s |= FullSizeContentView | TexturedBackground;
            //
            // TexturedBackground has been deprecated since 10.12 and marks the
            // window as the old metal style, which is chrome this desktop no
            // longer draws the way it draws everything else. Extending the
            // client area is wanted; being treated as a window from four
            // designs ago is not, so the bit is taken back off. Avalonia sets
            // it again whenever it recalculates the mask, which is why this
            // runs on every backdrop pass rather than once.
            var mask = SendNUInt(handle.NSWindow, Selector("styleMask"));
            if ((mask & TexturedBackground) != 0)
            {
                SendNUIntArgument(
                    handle.NSWindow,
                    Selector("setStyleMask:"),
                    mask & ~TexturedBackground);
            }

            SitAsTheWindowKind(handle.NSWindow, kind);

            // Avalonia deliberately turns this off after AppKit enters full
            // screen. FullSizeContentView still lets the shell run beneath the
            // title bar, so making that bar opaque again covers the top of the
            // shell — including the tab strip — with an empty native band.
            // This method is called for every window-state change, after
            // Avalonia has applied its state, so restore the transparent title
            // bar here as well as hiding Avalonia's material below.
            SendBool(
                handle.NSWindow,
                Selector("setTitlebarAppearsTransparent:"),
                true);

            var found = 0;
            foreach (var subview in SubviewsOf(avaloniaContent))
            {
                if (!PaintsTheTitleBarBand(subview))
                {
                    continue;
                }

                found++;
                if (!SendBoolResult(subview, Selector("isHidden")))
                {
                    SendBool(subview, Selector("setHidden:"), true);
                }
            }

            return found == 0
                ? MacOsTitleBarOutcome.NothingToHide
                : MacOsTitleBarOutcome.Hidden;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return MacOsTitleBarOutcome.Unreachable;
        }
    }

    /// <summary>
    /// The material and the separator, and neither of the things beside them.
    /// The blending mode is what separates the title-bar material from the
    /// window-wide blur, which shares its class, sits behind the content and is
    /// the shell's own backdrop when it is asked for.
    /// </summary>
    private static bool PaintsTheTitleBarBand(nint view) => ClassNameOf(view) switch
    {
        "NSVisualEffectView" =>
            SendNInt(view, Selector("blendingMode")) == BlendsWithinWindow,
        "NSBox" => true,
        _ => false,
    };

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

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendIdArgument(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNIntArgument(nint receiver, nint selector, nint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNUIntArgument(nint receiver, nint selector, nuint value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolResult(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdAtIndex(nint receiver, nint selector, nuint index);
}

/// <summary>
/// What asking Avalonia's content view to stop painting the band actually did.
/// </summary>
internal enum MacOsTitleBarOutcome
{
    /// <summary>Not macOS, or no window to ask.</summary>
    Unreachable,

    /// <summary>Avalonia's content view was not where it was looked for.</summary>
    NoContentView,

    /// <summary>The content view was found, and nothing in it paints the band.</summary>
    NothingToHide,

    /// <summary>The material and its underline are hidden.</summary>
    Hidden,
}

/// <summary>
/// What kind of window the shell asks to be, which is what decides the radius
/// the platform draws its corners at.
/// </summary>
internal enum MacOsWindowKind
{
    /// <summary>A title bar and nothing above it: 16pt.</summary>
    TitleBarOnly,

    /// <summary>A compact toolbar merged into the title bar: 20pt.</summary>
    CompactToolbar,

    /// <summary>A toolbar merged into the title bar: 26pt.</summary>
    Toolbar,
}
