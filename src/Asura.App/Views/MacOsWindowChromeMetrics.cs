using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Asura.App.Views;

/// <summary>
/// Reads the leading edge occupied by AppKit's standard window buttons.
/// Avalonia reports the native title-bar height but does not expose the
/// horizontal traffic-light bounds.
/// </summary>
internal static class MacOsWindowChromeMetrics
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nint ZoomButton = 2;
    private const nint CloseButton = 0;

    public static double? TryGetTrafficLightRightEdge(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return null;
        }

        var button = SendObject(
            handle.NSWindow,
            Selector("standardWindowButton:"),
            ZoomButton);
        if (button == 0)
        {
            return null;
        }

        var frame = SendRect(button, Selector("frame"));
        var rightEdge = frame.X + frame.Width;
        return double.IsFinite(rightEdge) && rightEdge is > 0 and < 400
            ? rightEdge
            : null;
    }

    /// <summary>
    /// How far down the window the standard buttons are centred.
    ///
    /// They do not sit at a fixed height: this desktop places them against the
    /// window's corner, so a rounder window puts them lower. Everything the
    /// shell draws in that band has to sit on the same axis or it reads as
    /// crooked, and the axis is not ours to choose.
    ///
    /// Measured from the button's own centre within the title bar it sits in,
    /// which is at the top of the window — so the distance from the top is the
    /// title bar's height less that centre.
    /// </summary>
    public static double? TryGetButtonCentreFromTop(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return null;
        }

        var button = SendObject(
            handle.NSWindow,
            Selector("standardWindowButton:"),
            CloseButton);
        if (button == 0)
        {
            return null;
        }

        var titleBar = SendMessage(button, Selector("superview"));
        if (titleBar == 0)
        {
            return null;
        }

        var frame = SendRect(button, Selector("frame"));
        var titleBarFrame = SendRect(titleBar, Selector("frame"));
        // AppKit measures up from the bottom; the shell lays out down from the
        // top.
        var centre = titleBarFrame.Height - (frame.Y + (frame.Height / 2));
        return double.IsFinite(centre) && centre is > 0 and < 200 ? centre : null;
    }

    /// <summary>
    /// How far in from the window's edge the standard buttons start.
    ///
    /// The same distance the shell owes its own controls at the other end: the
    /// buttons are placed clear of the corner, and a corner that is now a
    /// setting moves them. Anything sitting in the opposite corner has to keep
    /// the same distance or the band reads as leaning to one side.
    /// </summary>
    public static double? TryGetButtonLeadingInset(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()
            || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle
            || handle.NSWindow == 0)
        {
            return null;
        }

        var button = SendObject(
            handle.NSWindow,
            Selector("standardWindowButton:"),
            CloseButton);
        if (button == 0)
        {
            return null;
        }

        var frame = SendRect(button, Selector("frame"));
        return double.IsFinite(frame.X) && frame.X is > 0 and < 200 ? frame.X : null;
    }

    private static NativeRect SendRect(nint receiver, nint selector) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? SendRectX64(receiver, selector)
            : objc_msgSend_rect(receiver, selector);

    private static NativeRect SendRectX64(nint receiver, nint selector)
    {
        objc_msgSend_stret(out var result, receiver, selector);
        return result;
    }

    private static nint Selector(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(
        double X,
        double Y,
        double Width,
        double Height);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendMessage(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(
        nint receiver,
        nint selector,
        nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern NativeRect objc_msgSend_rect(
        nint receiver,
        nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend_stret")]
    private static extern void objc_msgSend_stret(
        out NativeRect result,
        nint receiver,
        nint selector);
}
