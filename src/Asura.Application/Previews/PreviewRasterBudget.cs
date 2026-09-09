using System.Runtime.InteropServices;

namespace Asura.Application.Previews;

/// <summary>
/// The single allocation budget shared by file-preview rasterizers. Source
/// dimensions are fitted without enlargement before a decoder is asked to
/// allocate output pixels.
/// </summary>
public static class PreviewRasterBudget
{
    public const int MaximumDimension = 4_096;
    public const long MaximumPixels = 8_000_000;

    public static bool TryFit(
        int sourceWidth,
        int sourceHeight,
        int preferredMaximumWidth,
        out PreviewRasterSize fitted)
    {
        fitted = default;
        if (sourceWidth <= 0
            || sourceHeight <= 0
            || preferredMaximumWidth <= 0)
        {
            return false;
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                preferredMaximumWidth / (double)sourceWidth,
                Math.Min(
                    MaximumDimension / (double)sourceWidth,
                    Math.Min(
                        MaximumDimension / (double)sourceHeight,
                        Math.Sqrt(
                            MaximumPixels
                            / (double)sourceWidth
                            / sourceHeight)))));
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return false;
        }

        var width = Math.Max(1, (int)Math.Floor(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Floor(sourceHeight * scale));
        while (!Contains(width, height))
        {
            if (width == 1 && height == 1)
            {
                return false;
            }

            if (width / (double)sourceWidth
                >= height / (double)sourceHeight)
            {
                width = Math.Max(1, width - 1);
            }
            else
            {
                height = Math.Max(1, height - 1);
            }
        }

        fitted = new PreviewRasterSize(width, height);
        return true;
    }

    public static bool Contains(int width, int height) =>
        width > 0
        && height > 0
        && width <= MaximumDimension
        && height <= MaximumDimension
        && (long)width * height <= MaximumPixels;

    public static bool TryFitAspectRatio(
        double sourceWidth,
        double sourceHeight,
        int requestedWidth,
        out PreviewRasterSize fitted)
    {
        fitted = default;
        if (!double.IsFinite(sourceWidth)
            || !double.IsFinite(sourceHeight)
            || sourceWidth <= 0
            || sourceHeight <= 0
            || requestedWidth <= 0)
        {
            return false;
        }

        var width = (double)Math.Min(requestedWidth, MaximumDimension);
        var height = width * sourceHeight / sourceWidth;
        if (!double.IsFinite(height) || height <= 0)
        {
            return false;
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                MaximumDimension / height,
                Math.Sqrt(MaximumPixels / width / height)));
        width *= scale;
        height *= scale;
        if (width < 1 || height < 1)
        {
            return false;
        }

        var fittedWidth = (int)Math.Floor(width);
        var fittedHeight = (int)Math.Floor(height);
        if (!Contains(fittedWidth, fittedHeight))
        {
            return false;
        }

        fitted = new PreviewRasterSize(fittedWidth, fittedHeight);
        return true;
    }
}

[StructLayout(LayoutKind.Auto)]
public readonly record struct PreviewRasterSize(int Width, int Height);
