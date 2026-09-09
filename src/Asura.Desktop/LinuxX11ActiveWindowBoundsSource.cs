using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Asura.App;
using Avalonia;

namespace Asura.Desktop;

[SupportedOSPlatform("linux")]
internal sealed class LinuxX11ActiveWindowBoundsSource : IActiveWindowBoundsSource
{
    private const string X11Library = "libX11.so.6";
    private const string ActiveWindowProperty = "_NET_ACTIVE_WINDOW";
    private const string WindowType = "WINDOW";

    public PixelRect? TryGetBounds()
    {
        try
        {
            return TryGetBoundsCore();
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or ExternalException)
        {
            Asura.Application.SecretSafeDiagnosticProjection.WriteTrace(
                "desktop.active-window.x11-read.failed",
                exception);
            return null;
        }
    }

    private static PixelRect? TryGetBoundsCore()
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var root = XDefaultRootWindow(display);
            var property = XInternAtom(display, ActiveWindowProperty, onlyIfExists: true);
            var windowType = XInternAtom(display, WindowType, onlyIfExists: true);
            if (property == 0
                || windowType == 0
                || XGetWindowProperty(
                    display,
                    root,
                    property,
                    offset: 0,
                    length: 1,
                    delete: false,
                    requestedType: 0,
                    out var actualType,
                    out var format,
                    out var itemCount,
                    out _,
                    out var data) != 0
                || data == IntPtr.Zero)
            {
                return null;
            }

            nuint window;
            try
            {
                if (actualType != windowType || format != 32 || itemCount != 1)
                {
                    return null;
                }

                window = (nuint)Marshal.ReadIntPtr(data);
            }
            finally
            {
                _ = XFree(data);
            }

            if (window == 0)
            {
                return null;
            }

            var geometryStatus = 0;
            var translationStatus = 0;
            var x = 0;
            var y = 0;
            uint width = 0;
            uint height = 0;
            var errorCode = X11HotkeyMessageLoop.CaptureErrors(display, () =>
            {
                geometryStatus = XGetGeometry(
                    display,
                    window,
                    out _,
                    out _,
                    out _,
                    out width,
                    out height,
                    out _,
                    out _);
                if (geometryStatus != 0)
                {
                    translationStatus = XTranslateCoordinates(
                        display,
                        window,
                        root,
                        0,
                        0,
                        out x,
                        out y,
                        out _);
                }
            });

            if (errorCode != 0
                || geometryStatus == 0
                || translationStatus == 0
                || width == 0
                || height == 0
                || width > int.MaxValue
                || height > int.MaxValue)
            {
                return null;
            }

            return new PixelRect(x, y, (int)width, (int)height);
        }
        finally
        {
            _ = XCloseDisplay(display);
        }
    }

    [DllImport(X11Library)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(X11Library)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11Library)]
    private static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport(X11Library)]
    private static extern nuint XInternAtom(
        IntPtr display,
        string name,
        [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport(X11Library)]
    private static extern int XGetWindowProperty(
        IntPtr display,
        nuint window,
        nuint property,
        nint offset,
        nint length,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        nuint requestedType,
        out nuint actualType,
        out int actualFormat,
        out nuint itemCount,
        out nuint bytesAfter,
        out IntPtr data);

    [DllImport(X11Library)]
    private static extern int XGetGeometry(
        IntPtr display,
        nuint drawable,
        out nuint root,
        out int x,
        out int y,
        out uint width,
        out uint height,
        out uint borderWidth,
        out uint depth);

    [DllImport(X11Library)]
    private static extern int XTranslateCoordinates(
        IntPtr display,
        nuint sourceWindow,
        nuint destinationWindow,
        int sourceX,
        int sourceY,
        out int destinationX,
        out int destinationY,
        out nuint child);

    [DllImport(X11Library)]
    private static extern int XFree(IntPtr data);
}
