using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Asura.App;
using Avalonia;

namespace Asura.Desktop;

/// <summary>
/// Locates the frontmost application's top regular window through AppKit and
/// Core Graphics. Window geometry remains available without screen-recording
/// permission; protected titles and image contents are never requested.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsActiveWindowBoundsSource : IActiveWindowBoundsSource
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AppKitFramework =
        "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphicsFramework =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const uint OnScreenOnly = 1U << 0;
    private const uint ExcludeDesktopElements = 1U << 4;
    private const int CfNumberIntType = 9;
    private const int CfNumberDoubleType = 13;

    private static readonly IntPtr AppKitHandle = LoadFramework(AppKitFramework);
    private static readonly IntPtr CoreGraphicsHandle = LoadFramework(CoreGraphicsFramework);
    private static readonly IntPtr WindowOwnerPidKey = LoadConstant("kCGWindowOwnerPID");
    private static readonly IntPtr WindowLayerKey = LoadConstant("kCGWindowLayer");
    private static readonly IntPtr WindowBoundsKey = LoadConstant("kCGWindowBounds");
    private static readonly IntPtr WindowAlphaKey = LoadConstant("kCGWindowAlpha");

    public PixelRect? TryGetBounds()
    {
        try
        {
            return TryGetBoundsCore();
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or ExternalException
            or OverflowException)
        {
            Asura.Application.SecretSafeDiagnosticProjection.WriteTrace(
                "desktop.active-window.macos-read.failed",
                exception);
            return null;
        }
    }

    private static PixelRect? TryGetBoundsCore()
    {
        var processId = GetFrontmostProcessId();
        if (processId <= 0
            || CoreGraphicsHandle == IntPtr.Zero
            || WindowOwnerPidKey == IntPtr.Zero
            || WindowLayerKey == IntPtr.Zero
            || WindowBoundsKey == IntPtr.Zero
            || WindowAlphaKey == IntPtr.Zero)
        {
            return null;
        }

        var windows = CGWindowListCopyWindowInfo(
            OnScreenOnly | ExcludeDesktopElements,
            relativeToWindow: 0);
        if (windows == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var count = CFArrayGetCount(windows);
            for (nint index = 0; index < count; index++)
            {
                var description = CFArrayGetValueAtIndex(windows, index);
                if (description == IntPtr.Zero
                    || ReadInt(description, WindowOwnerPidKey) != processId
                    || ReadInt(description, WindowLayerKey) != 0
                    || ReadDouble(description, WindowAlphaKey) is not > 0)
                {
                    continue;
                }

                var boundsValue = CFDictionaryGetValue(description, WindowBoundsKey);
                if (boundsValue != IntPtr.Zero
                    && CGRectMakeWithDictionaryRepresentation(boundsValue, out var bounds))
                {
                    var pixelBounds = ToPixelRect(bounds);
                    if (pixelBounds is not null)
                    {
                        return pixelBounds;
                    }
                }
            }

            return null;
        }
        finally
        {
            CFRelease(windows);
        }
    }

    private static int GetFrontmostProcessId()
    {
        if (AppKitHandle == IntPtr.Zero)
        {
            return 0;
        }

        var workspaceClass = objc_getClass("NSWorkspace");
        if (workspaceClass == IntPtr.Zero)
        {
            return 0;
        }

        var workspace = objc_msgSend_retIntPtr(
            workspaceClass,
            sel_registerName("sharedWorkspace"));
        var application = objc_msgSend_retIntPtr(
            workspace,
            sel_registerName("frontmostApplication"));
        return application == IntPtr.Zero
            ? 0
            : checked((int)objc_msgSend_retNint(
                application,
                sel_registerName("processIdentifier")));
    }

    private static int? ReadInt(IntPtr dictionary, IntPtr key)
    {
        var number = CFDictionaryGetValue(dictionary, key);
        return number != IntPtr.Zero
            && CFNumberGetValue(number, CfNumberIntType, out var value)
                ? value
                : null;
    }

    private static double? ReadDouble(IntPtr dictionary, IntPtr key)
    {
        var number = CFDictionaryGetValue(dictionary, key);
        return number != IntPtr.Zero
            && CFNumberGetDoubleValue(number, CfNumberDoubleType, out var value)
                ? value
                : null;
    }

    private static PixelRect? ToPixelRect(CGRect rectangle)
    {
        var left = Math.Floor(rectangle.Origin.X);
        var top = Math.Floor(rectangle.Origin.Y);
        var right = Math.Ceiling(rectangle.Origin.X + rectangle.Size.Width);
        var bottom = Math.Ceiling(rectangle.Origin.Y + rectangle.Size.Height);
        if (!double.IsFinite(left)
            || !double.IsFinite(top)
            || !double.IsFinite(right)
            || !double.IsFinite(bottom)
            || right <= left
            || bottom <= top
            || left < int.MinValue
            || top < int.MinValue
            || right > int.MaxValue
            || bottom > int.MaxValue)
        {
            return null;
        }

        return new PixelRect(
            (int)left,
            (int)top,
            (int)(right - left),
            (int)(bottom - top));
    }

    private static IntPtr LoadFramework(string path) =>
        NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;

    private static IntPtr LoadConstant(string name)
    {
        if (CoreGraphicsHandle == IntPtr.Zero
            || !NativeLibrary.TryGetExport(CoreGraphicsHandle, name, out var address))
        {
            return IntPtr.Zero;
        }

        return Marshal.ReadIntPtr(address);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint
    {
        public readonly double X;
        public readonly double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSize
    {
        public readonly double Width;
        public readonly double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRect
    {
        public readonly CGPoint Origin;
        public readonly CGSize Size;
    }

    [DllImport(ObjCLibrary)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLibrary)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_retIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_retNint(IntPtr receiver, IntPtr selector);

    [DllImport(CoreGraphicsFramework)]
    private static extern IntPtr CGWindowListCopyWindowInfo(
        uint option,
        uint relativeToWindow);

    [DllImport(CoreGraphicsFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(
        IntPtr dictionary,
        out CGRect rectangle);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundationFramework)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundationFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(
        IntPtr number,
        int numberType,
        out int value);

    [DllImport(CoreFoundationFramework, EntryPoint = "CFNumberGetValue")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetDoubleValue(
        IntPtr number,
        int numberType,
        out double value);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);
}
