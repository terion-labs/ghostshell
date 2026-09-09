using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Asura.App;

/// <summary>
/// Reads the colour of a single point of a window as it is currently rendered.
///
/// The sample is taken from the application's own window rather than the screen:
/// a screen-wide picker needs a platform screen-capture capability and, on macOS,
/// the user's screen-recording permission. Sampling what Asura already draws
/// needs neither, works the same on every platform, and covers the case the
/// palette editor is for — lifting a colour out of live terminal output, a
/// preview, or another swatch.
/// </summary>
public static class ColorSampling
{
    /// <summary>
    /// Returns the colour at <paramref name="point"/> in <paramref name="window"/>
    /// client coordinates, or <c>null</c> when the point lies outside the window
    /// or the window has nothing rendered yet.
    /// </summary>
    public static Color? Sample(Window window, Point point)
    {
        ArgumentNullException.ThrowIfNull(window);

        var scaling = window.RenderScaling;
        var width = (int)Math.Ceiling(window.Bounds.Width * scaling);
        var height = (int)Math.Ceiling(window.Bounds.Height * scaling);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var x = (int)Math.Floor(point.X * scaling);
        var y = (int)Math.Floor(point.Y * scaling);
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return null;
        }

        using var bitmap = new RenderTargetBitmap(
            new PixelSize(width, height),
            new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);
        return ReadPixel(bitmap, x, y);
    }

    private static Color? ReadPixel(RenderTargetBitmap bitmap, int x, int y)
    {
        // One pixel, copied straight out of the rendered surface. The buffer is
        // four bytes because every format Avalonia renders to here is 32bpp.
        const int bytesPerPixel = 4;
        var buffer = Marshal.AllocHGlobal(bytesPerPixel);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(x, y, 1, 1),
                buffer,
                bytesPerPixel,
                bytesPerPixel);
            var pixel = new byte[bytesPerPixel];
            Marshal.Copy(buffer, pixel, 0, bytesPerPixel);
            return ToColor(pixel, bitmap.Format);
        }
        catch (NotSupportedException)
        {
            // A backend that cannot read its own surface back gives no colour
            // rather than a wrong one.
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Color ToColor(byte[] pixel, PixelFormat? format) =>
        format == PixelFormats.Rgba8888
            ? Color.FromRgb(pixel[0], pixel[1], pixel[2])
            : Color.FromRgb(pixel[2], pixel[1], pixel[0]);
}
