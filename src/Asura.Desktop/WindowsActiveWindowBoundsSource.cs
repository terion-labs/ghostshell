using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Asura.App;
using Avalonia;

namespace Asura.Desktop;

[SupportedOSPlatform("windows")]
internal sealed class WindowsActiveWindowBoundsSource : IActiveWindowBoundsSource
{
    private const int ExtendedFrameBounds = 9;

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
                "desktop.active-window.windows-read.failed",
                exception);
            return null;
        }
    }

    private static PixelRect? TryGetBoundsCore()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var status = DwmGetWindowAttribute(
            window,
            ExtendedFrameBounds,
            out var rectangle,
            Marshal.SizeOf<NativeRect>());
        if (status != 0 && !GetWindowRect(window, out rectangle))
        {
            return null;
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        return width > 0 && height > 0
            ? new PixelRect(rectangle.Left, rectangle.Top, width, height)
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out NativeRect value,
        int valueSize);
}
