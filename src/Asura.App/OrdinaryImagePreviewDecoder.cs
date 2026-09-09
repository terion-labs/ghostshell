using Asura.Application;
using Asura.Application.Previews;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Asura.App;

/// <summary>
/// Reads image metadata first, then asks Avalonia for a decode that already
/// fits the shared preview budget. The full source bitmap is never the input to
/// a later resize operation.
/// </summary>
internal static class OrdinaryImagePreviewDecoder
{
    internal const int PreferredMaximumWidth = 2_400;
    internal const int MaximumSourceDimension = 16_384;
    internal const long MaximumSourcePixels = 64_000_000;

    public static Bitmap? Decode(FilePreviewContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        PreviewRasterSize target;
        int sourceWidth;
        int sourceHeight;
        SKEncodedOrigin encodedOrigin;
        using (var metadataStream = content.OpenRead())
        using (var codec = SKCodec.Create(metadataStream))
        {
            if (codec is null)
            {
                return null;
            }

            sourceWidth = codec.Info.Width;
            sourceHeight = codec.Info.Height;
            encodedOrigin = codec.EncodedOrigin;
            if (!IsSupportedSourceSize(sourceWidth, sourceHeight))
            {
                return null;
            }

            var displayedSource = DisplayedSize(
                sourceWidth,
                sourceHeight,
                encodedOrigin);
            if (!PreviewRasterBudget.TryFit(
                    displayedSource.Width,
                    displayedSource.Height,
                    PreferredMaximumWidth,
                    out target))
            {
                return null;
            }
        }

        using var source = content.OpenRead();
        using var decoder = SKCodec.Create(source);
        if (decoder is null)
        {
            return null;
        }

        if (decoder.Info.Width != sourceWidth
            || decoder.Info.Height != sourceHeight
            || decoder.EncodedOrigin != encodedOrigin)
        {
            // The backing file changed between metadata inspection and decode.
            return null;
        }

        var encodedTarget = SwapsAxes(encodedOrigin)
            ? new PreviewRasterSize(target.Height, target.Width)
            : target;

        var requestedScale = Math.Min(
            encodedTarget.Width / (double)sourceWidth,
            encodedTarget.Height / (double)sourceHeight);
        var decodedSize = requestedScale >= 1d
            ? decoder.Info.Size
            : decoder.GetScaledDimensions((float)requestedScale);
        if (decodedSize.Width > sourceWidth
            || decodedSize.Height > sourceHeight
            || !PreviewRasterBudget.Contains(
                decodedSize.Width,
                decodedSize.Height))
        {
            return null;
        }

        var decodedInfo = new SKImageInfo(
            decodedSize.Width,
            decodedSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var decodedBitmap = new SKBitmap(decodedInfo);
        if (decoder.GetPixels(decodedInfo, decodedBitmap.GetPixels())
            is not SKCodecResult.Success)
        {
            return null;
        }

        using var resizedBitmap = decodedSize.Width > encodedTarget.Width
            || decodedSize.Height > encodedTarget.Height
                ? decodedBitmap.Resize(
                    new SKImageInfo(
                        encodedTarget.Width,
                        encodedTarget.Height,
                        SKColorType.Bgra8888,
                        SKAlphaType.Premul),
                    new SKSamplingOptions(SKCubicResampler.CatmullRom))
                : null;
        if (resizedBitmap is null
            && (decodedSize.Width > encodedTarget.Width
                || decodedSize.Height > encodedTarget.Height))
        {
            return null;
        }

        var scaledBitmap = resizedBitmap ?? decodedBitmap;
        using var orientedBitmap = Orient(scaledBitmap, encodedOrigin);
        var displayBitmap = orientedBitmap ?? scaledBitmap;

        var bitmap = new WriteableBitmap(
            PixelFormats.Bgra8888,
            AlphaFormat.Premul,
            displayBitmap.GetPixels(),
            new PixelSize(displayBitmap.Width, displayBitmap.Height),
            new Vector(96, 96),
            displayBitmap.RowBytes);
        var displayedSourceSize = DisplayedSize(
            sourceWidth,
            sourceHeight,
            encodedOrigin);
        if (bitmap.PixelSize.Width > displayedSourceSize.Width
            || bitmap.PixelSize.Height > displayedSourceSize.Height
            || !PreviewRasterBudget.Contains(
                bitmap.PixelSize.Width,
                bitmap.PixelSize.Height))
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    internal static bool IsSupportedSourceSize(int width, int height) =>
        width > 0
        && height > 0
        && width <= MaximumSourceDimension
        && height <= MaximumSourceDimension
        && (long)width * height <= MaximumSourcePixels;

    internal static PreviewRasterSize DisplayedSize(
        int width,
        int height,
        SKEncodedOrigin origin) =>
        SwapsAxes(origin)
            ? new PreviewRasterSize(height, width)
            : new PreviewRasterSize(width, height);

    private static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

    private static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        var displayed = DisplayedSize(source.Width, source.Height, origin);
        if (!PreviewRasterBudget.Contains(displayed.Width, displayed.Height))
        {
            return null;
        }

        var destination = new SKBitmap(new SKImageInfo(
            displayed.Width,
            displayed.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        using var canvas = new SKCanvas(destination);
        var width = source.Width;
        var height = source.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-1, 0, width, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Matrix(-1, 0, width, 0, -1, height),
            SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, height),
            SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -1, height, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -1, height, -1, 0, width),
            SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, width),
            _ => SKMatrix.CreateIdentity(),
        };
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return destination;

        static SKMatrix Matrix(
            float scaleX,
            float skewX,
            float transX,
            float skewY,
            float scaleY,
            float transY) =>
            new(
                scaleX,
                skewX,
                transX,
                skewY,
                scaleY,
                transY,
                0,
                0,
                1);
    }
}
