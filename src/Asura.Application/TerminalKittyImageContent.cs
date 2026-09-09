using System.Runtime.InteropServices;

namespace Asura.Application;

[StructLayout(LayoutKind.Auto)]
public readonly record struct TerminalKittyImageKey
{
    public TerminalKittyImageKey(uint ImageId, ulong Generation)
    {
        if (ImageId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ImageId));
        }

        if (Generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Generation));
        }

        this.ImageId = ImageId;
        this.Generation = Generation;
    }

    public uint ImageId { get; }

    /// <summary>
    /// The process-wide generation assigned when this image ID was transmitted
    /// or replaced. It prevents same-sized retransmissions from reusing stale textures.
    /// </summary>
    public ulong Generation { get; }
}

public enum TerminalKittyImagePixelFormat
{
    Rgb,
    Rgba,
    GrayAlpha,
    Gray,
}

/// <summary>
/// Immutable, decoded Kitty image pixels. Placement snapshots reference this
/// content by <see cref="TerminalKittyImageKey"/> instead of duplicating it.
/// </summary>
public sealed record TerminalKittyImageContent
{
    private readonly byte[] _pixels;

    public TerminalKittyImageContent(
        TerminalKittyImageKey Key,
        uint ImageNumber,
        int PixelWidth,
        int PixelHeight,
        TerminalKittyImagePixelFormat PixelFormat,
        ReadOnlyMemory<byte> Pixels)
        : this(
            Key,
            ImageNumber,
            PixelWidth,
            PixelHeight,
            PixelFormat,
            Pixels.ToArray())
    {
    }

    internal static TerminalKittyImageContent FromOwnedPixels(
        TerminalKittyImageKey key,
        uint imageNumber,
        int pixelWidth,
        int pixelHeight,
        TerminalKittyImagePixelFormat pixelFormat,
        byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        return new TerminalKittyImageContent(
            key,
            imageNumber,
            pixelWidth,
            pixelHeight,
            pixelFormat,
            pixels);
    }

    public TerminalKittyImageKey Key { get; }

    public uint ImageNumber { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public TerminalKittyImagePixelFormat PixelFormat { get; }

    public ReadOnlyMemory<byte> Pixels => _pixels;

    private TerminalKittyImageContent(
        TerminalKittyImageKey key,
        uint imageNumber,
        int pixelWidth,
        int pixelHeight,
        TerminalKittyImagePixelFormat pixelFormat,
        byte[] pixels)
    {
        ValidatePixels(key, pixelWidth, pixelHeight, pixelFormat, pixels.Length);
        Key = key;
        ImageNumber = imageNumber;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PixelFormat = pixelFormat;
        _pixels = pixels;
    }

    private static void ValidatePixels(
        TerminalKittyImageKey key,
        int pixelWidth,
        int pixelHeight,
        TerminalKittyImagePixelFormat pixelFormat,
        int pixelLength)
    {
        if (key.ImageId == 0 || key.Generation == 0)
        {
            throw new ArgumentException("A Kitty image key must be initialized.", nameof(key));
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        if (!Enum.IsDefined(pixelFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        }

        var bytesPerPixel = BytesPerPixel(pixelFormat);
        var pixelCount = (long)pixelWidth * pixelHeight;
        if (pixelCount > int.MaxValue / bytesPerPixel
            || pixelLength != pixelCount * bytesPerPixel)
        {
            throw new ArgumentException(
                "Decoded Kitty image data must exactly match its dimensions and pixel format.",
                nameof(pixelLength));
        }
    }

    private static int BytesPerPixel(TerminalKittyImagePixelFormat format) => format switch
    {
        TerminalKittyImagePixelFormat.Rgb => 3,
        TerminalKittyImagePixelFormat.Rgba => 4,
        TerminalKittyImagePixelFormat.GrayAlpha => 2,
        TerminalKittyImagePixelFormat.Gray => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
