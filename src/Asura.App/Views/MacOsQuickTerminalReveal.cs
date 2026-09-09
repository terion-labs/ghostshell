using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Asura.App.Views;

/// <summary>
/// Slides Avalonia's macOS backdrop and Skia surface in one AppKit context.
///
/// Avalonia's <c>AutoFitContentView</c> directly owns the behind-window
/// <c>NSVisualEffectView</c> and the <c>AvnView</c> that presents Skia. A visual
/// effect does not obey ordinary layer transforms or masks: AppKit continues
/// producing its effect for the view's real bounds. The reveal therefore
/// changes those native bounds. The material grows down from zero height while
/// the full-size Skia view moves down by the same distance.
/// </summary>
internal static class MacOsQuickTerminalReveal
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AvaloniaContentView = "AutoFitContentView";
    private const string AvaloniaRenderView = "AvnView";
    private const string ChromeMaterialView = "AsuraQuickTerminalChromeView";
    private const string AgentMaterialView = "AsuraQuickTerminalAgentView";
    private const nint BlendsBehindWindow = 0;
    private const nint AlwaysActive = 1;
    private const nint WidthSizable = 2;
    private const nint PlaceBelow = -1;
    public static bool TryClearWindowBacking(Window window)
    {
        if (!TryGetNativeWindow(window, out var nsWindow))
        {
            return false;
        }

        var clearColor = SendId(objc_getClass("NSColor"), Selector("clearColor"));
        if (clearColor == 0)
        {
            return false;
        }

        SendIdArgument(nsWindow, Selector("setBackgroundColor:"), clearColor);
        return true;
    }

    /// <summary>
    /// Prevents AppKit from swapping the material to its inactive, flat fill
    /// before an outside-click dismissal has finished sliding off screen.
    /// </summary>
    public static bool TryKeepBackdropActive(Window window)
    {
        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        var activated = false;
        if (views.BlurView != 0)
        {
            SendIdArgument(views.BlurView, Selector("setState:"), AlwaysActive);
            activated = true;
        }

        if (views.ChromeView != 0)
        {
            SendIdArgument(views.ChromeView, Selector("setState:"), AlwaysActive);
            activated = true;
        }

        if (views.AgentView != 0)
        {
            SendIdArgument(views.AgentView, Selector("setState:"), AlwaysActive);
            activated = true;
        }

        return activated;
    }

    /// <summary>
    /// Places a second native material behind Avalonia's bottom controls. It
    /// is a sibling of the full-window material and the Skia render view, so
    /// it can be denser without changing the terminal viewport's glass.
    /// </summary>
    public static bool TrySetChromeMaterial(
        Window window,
        double height,
        MacOsMaterial material,
        bool isVisible)
    {
        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        var chrome = views.ChromeView;
        if (chrome == 0)
        {
            var chromeClass = GetOrCreateMaterialClass(ChromeMaterialView);
            if (chromeClass == 0)
            {
                return false;
            }

            var allocated = SendId(chromeClass, Selector("alloc"));
            chrome = SendIdRectArgument(
                allocated,
                Selector("initWithFrame:"),
                ChromeFrame(views.Width, height, offset: 0));
            if (chrome == 0)
            {
                return false;
            }

            SendNIntArgument(chrome, Selector("setAutoresizingMask:"), WidthSizable);
            SendNIntArgument(chrome, Selector("setBlendingMode:"), BlendsBehindWindow);
            SendThreeArguments(
                views.ContainerView,
                Selector("addSubview:positioned:relativeTo:"),
                chrome,
                PlaceBelow,
                views.ContentView);
            SendVoid(chrome, Selector("release"));
        }

        SendNIntArgument(chrome, Selector("setMaterial:"), (nint)material);
        SendNIntArgument(chrome, Selector("setState:"), AlwaysActive);
        SendBoolArgument(chrome, Selector("setHidden:"), !isVisible);
        SendRectArgument(
            chrome,
            Selector("setFrame:"),
            ChromeFrame(views.Width, height, offset: 0));
        return true;
    }

    /// <summary>
    /// Backs only the docked Agent column with native sidebar material. Its
    /// frame follows the resizable Avalonia surface above the controls strip.
    /// </summary>
    public static bool TrySetAgentMaterial(
        Window window,
        double width,
        double chromeHeight,
        MacOsMaterial material,
        bool isOnLeft,
        bool isVisible)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!double.IsFinite(chromeHeight) || chromeHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chromeHeight));
        }

        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        var agent = views.AgentView;
        if (agent == 0)
        {
            var agentClass = GetOrCreateMaterialClass(AgentMaterialView);
            if (agentClass == 0)
            {
                return false;
            }

            var allocated = SendId(agentClass, Selector("alloc"));
            agent = SendIdRectArgument(
                allocated,
                Selector("initWithFrame:"),
                AgentFrame(
                    views.Width,
                    views.Height,
                    width,
                    chromeHeight,
                    isOnLeft,
                    offset: 0));
            if (agent == 0)
            {
                return false;
            }

            SendNIntArgument(agent, Selector("setBlendingMode:"), BlendsBehindWindow);
            SendThreeArguments(
                views.ContainerView,
                Selector("addSubview:positioned:relativeTo:"),
                agent,
                PlaceBelow,
                views.ContentView);
            SendVoid(agent, Selector("release"));
        }

        SendNIntArgument(agent, Selector("setMaterial:"), (nint)material);
        SendNIntArgument(agent, Selector("setState:"), AlwaysActive);
        SendBoolArgument(agent, Selector("setHidden:"), !isVisible);
        SendRectArgument(
            agent,
            Selector("setFrame:"),
            AgentFrame(
                views.Width,
                views.Height,
                width,
                chromeHeight,
                isOnLeft,
                offset: 0));
        return true;
    }

    public static bool TrySetProgress(Window window, double progress)
    {
        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        SetRevealFrames(views, progress);
        return true;
    }

    public static bool TryAnimate(
        Window window,
        double from,
        double to,
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TrySetProgress(window, to);
        }

        if (!TryGetRevealViews(window, out var views))
        {
            return false;
        }

        SetRevealFrames(views, from);
        var contextClass = objc_getClass("NSAnimationContext");
        SendVoid(contextClass, Selector("beginGrouping"));
        var context = SendId(contextClass, Selector("currentContext"));
        SendDouble(context, Selector("setDuration:"), duration.TotalSeconds);
        var timing = CubicEaseOutTiming();
        if (timing != 0)
        {
            SendIdArgument(context, Selector("setTimingFunction:"), timing);
        }

        var target = FramesForProgress(views, to);
        if (views.BlurView != 0)
        {
            var blurAnimator = SendId(views.BlurView, Selector("animator"));
            SendRectArgument(blurAnimator, Selector("setFrame:"), target.Blur);
        }

        if (views.ChromeView != 0)
        {
            var chromeAnimator = SendId(views.ChromeView, Selector("animator"));
            SendRectArgument(chromeAnimator, Selector("setFrame:"), target.Chrome);
        }

        if (views.AgentView != 0)
        {
            var agentAnimator = SendId(views.AgentView, Selector("animator"));
            SendRectArgument(agentAnimator, Selector("setFrame:"), target.Agent);
        }

        var contentAnimator = SendId(views.ContentView, Selector("animator"));
        SendRectArgument(contentAnimator, Selector("setFrame:"), target.Content);
        SendVoid(contextClass, Selector("endGrouping"));
        return true;
    }

    private static void SetRevealFrames(RevealViews views, double progress)
    {
        var frames = FramesForProgress(views, progress);
        if (views.BlurView != 0)
        {
            SendRectArgument(views.BlurView, Selector("setFrame:"), frames.Blur);
        }

        if (views.ChromeView != 0)
        {
            SendRectArgument(views.ChromeView, Selector("setFrame:"), frames.Chrome);
        }

        if (views.AgentView != 0)
        {
            SendRectArgument(views.AgentView, Selector("setFrame:"), frames.Agent);
        }

        SendRectArgument(views.ContentView, Selector("setFrame:"), frames.Content);
    }

    private static RevealFrames FramesForProgress(
        RevealViews views,
        double progress)
    {
        var width = views.Width;
        var height = views.Height;
        var visibleHeight = height * Math.Clamp(progress, 0, 1);
        var offset = height - visibleHeight;
        return new RevealFrames(
            new CGRect
            {
                X = 0,
                Y = offset,
                Width = width,
                Height = visibleHeight,
            },
            new CGRect
            {
                X = 0,
                Y = offset,
                Width = width,
                Height = height,
            },
            ChromeFrame(width, 36, offset),
            views.AgentView == 0
                ? default
                : AgentRevealFrame(
                    SendRect(views.AgentView, Selector("frame")),
                    offset));
    }

    private static CGRect ChromeFrame(double width, double height, double offset) =>
        new()
        {
            X = 0,
            Y = offset,
            Width = width,
            Height = height,
        };

    private static CGRect AgentFrame(
        double windowWidth,
        double windowHeight,
        double width,
        double chromeHeight,
        bool isOnLeft,
        double offset) =>
        new()
        {
            X = isOnLeft ? 0 : Math.Max(0, windowWidth - width),
            Y = chromeHeight + offset,
            Width = Math.Min(width, windowWidth),
            Height = Math.Max(0, windowHeight - chromeHeight),
        };

    private static CGRect AgentRevealFrame(CGRect frame, double offset) =>
        new()
        {
            X = frame.X,
            Y = 36 + offset,
            Width = frame.Width,
            Height = frame.Height,
        };

    private static bool TryGetRevealViews(Window window, out RevealViews views)
    {
        views = default;
        if (!TryGetNativeWindow(window, out var nsWindow))
        {
            return false;
        }

        var contentView = SendId(nsWindow, Selector("contentView"));
        var container = FindDescendantOfClass(contentView, AvaloniaContentView);
        if (container == 0)
        {
            return false;
        }

        var bounds = SendRect(container, Selector("bounds"));
        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);
        nint blurView = 0;
        nint renderView = 0;
        nint chromeView = 0;
        nint agentView = 0;
        foreach (var subview in SubviewsOf(container))
        {
            var className = ClassNameOf(subview);
            if (string.Equals(className, AvaloniaRenderView, StringComparison.Ordinal))
            {
                renderView = subview;
                continue;
            }

            var isWindowMaterial = string.Equals(
                className,
                "NSVisualEffectView",
                StringComparison.Ordinal);
            var isChromeMaterial = string.Equals(
                className,
                ChromeMaterialView,
                StringComparison.Ordinal);
            var isAgentMaterial = string.Equals(
                className,
                AgentMaterialView,
                StringComparison.Ordinal);
            if ((isWindowMaterial || isChromeMaterial || isAgentMaterial)
                && SendNInt(subview, Selector("blendingMode")) == BlendsBehindWindow)
            {
                if (isChromeMaterial)
                {
                    chromeView = subview;
                }
                else if (isAgentMaterial)
                {
                    agentView = subview;
                }
                else if (!SendBoolWithResult(subview, Selector("isHidden")))
                {
                    blurView = subview;
                }
            }
        }

        if (renderView == 0)
        {
            return false;
        }

        views = new RevealViews(
            container,
            renderView,
            blurView,
            chromeView,
            agentView,
            width,
            height);
        return true;
    }

    /// <summary>
    /// NSVisualEffectView is an NSView, not an NSControl, and therefore has no
    /// tag property. A private subclass gives the localized chrome material a
    /// safe native identity without sending unsupported Objective-C messages.
    /// </summary>
    private static nint GetOrCreateMaterialClass(string className)
    {
        var existing = objc_getClass(className);
        if (existing != 0)
        {
            return existing;
        }

        var superclass = objc_getClass("NSVisualEffectView");
        if (superclass == 0)
        {
            return 0;
        }

        var materialClass = objc_allocateClassPair(
            superclass,
            className,
            extraBytes: 0);
        if (materialClass != 0)
        {
            objc_registerClassPair(materialClass);
        }

        return materialClass;
    }

    private static bool TryGetNativeWindow(Window window, out nint nsWindow)
    {
        nsWindow = 0;
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return false;
        }

        nsWindow = handle.NSWindow;
        return true;
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

    private static nint CubicEaseOutTiming() => SendFourFloats(
        objc_getClass("CAMediaTimingFunction"),
        Selector("functionWithControlPoints::::"),
        1f / 3f,
        1,
        2f / 3f,
        1);

    private static string ClassNameOf(nint instance) => instance == 0
        ? string.Empty
        : Marshal.PtrToStringAnsi(object_getClassName(instance)) ?? string.Empty;

    private static nint Selector(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RevealViews(
        nint ContainerView,
        nint ContentView,
        nint BlurView,
        nint ChromeView,
        nint AgentView,
        double Width,
        double Height);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RevealFrames(
        CGRect Blur,
        CGRect Content,
        CGRect Chrome,
        CGRect Agent);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_allocateClassPair")]
    private static extern nint objc_allocateClassPair(
        nint superclass,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        nuint extraBytes);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport(ObjectiveCLibrary, EntryPoint = "object_getClassName")]
    private static extern nint object_getClassName(nint instance);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SendBoolWithResult(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendIdArgument(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendNIntArgument(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBoolArgument(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.U1)] bool argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(nint receiver, nint selector, double value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendFourFloats(
        nint receiver,
        nint selector,
        float firstX,
        float firstY,
        float secondX,
        float secondY);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdAtIndex(
        nint receiver,
        nint selector,
        nuint index);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdRectArgument(
        nint receiver,
        nint selector,
        CGRect value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendRectArgument(
        nint receiver,
        nint selector,
        CGRect value);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendThreeArguments(
        nint receiver,
        nint selector,
        nint first,
        nint second,
        nint third);
}
