using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Asura.Application;
using Asura.Application.Previews;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class OrdinaryImagePreviewDecoderTests
{
    [Fact]
    public async Task A_large_jpeg_uses_a_bounded_native_decode_then_fits_the_preview()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"asura-large-jpeg-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(
            path,
            EncodedImage(SKEncodedImageFormat.Jpeg, width: 3_456, height: 2_234));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(
                () =>
                {
                    using var content = FilePreviewContent.FromLocalFile(path);
                    using var bitmap = OrdinaryImagePreviewDecoder.Decode(content);
                    Assert.NotNull(bitmap);
                    Assert.Equal(2_400, bitmap!.PixelSize.Width);
                    Assert.True(bitmap.PixelSize.Height < 2_234);
                    Assert.True((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height
                        <= PreviewRasterBudget.MaximumPixels);
                    return Task.FromResult(true);
                },
                timeout.Token));
        }
        finally
        {
            await session.DisposeAsync();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(16_384, 1, true)]
    [InlineData(16_385, 1, false)]
    [InlineData(8_000, 8_000, true)]
    [InlineData(8_192, 8_192, false)]
    [InlineData(1, 100_000, false)]
    [InlineData(100_000, 1, false)]
    public void Declared_source_axes_and_work_are_bounded_before_decode(
        int width,
        int height,
        bool expected) =>
        Assert.Equal(
            expected,
            OrdinaryImagePreviewDecoder.IsSupportedSourceSize(width, height));

    [Fact]
    public async Task A_portrait_bitmap_is_not_upscaled_past_the_pixel_budget()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"asura-ordinary-image-{Guid.NewGuid():N}.bmp");
        await File.WriteAllBytesAsync(path, Bitmap24(1_000, 1_600));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(
            typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                () =>
                {
                    using (var source = File.OpenRead(path))
                    using (var codec = SKCodec.Create(source))
                    {
                        Assert.NotNull(codec);
                        Assert.Equal(1_000, codec!.Info.Width);
                        Assert.Equal(1_600, codec.Info.Height);
                    }

                    using var content = FilePreviewContent.FromLocalFile(path);
                    using var bitmap = OrdinaryImagePreviewDecoder.Decode(content);

                    Assert.NotNull(bitmap);
                    Assert.Equal(1_000, bitmap!.PixelSize.Width);
                    Assert.Equal(1_600, bitmap.PixelSize.Height);
                    return Task.FromResult(true);
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Ordinary_png_jpeg_gif_bmp_and_webp_keep_bounded_source_dimensions()
    {
        var root = Directory.CreateTempSubdirectory("asura-ordinary-formats");
        var fixtures = new[]
        {
            (Name: "image.png", Bytes: EncodedImage(SKEncodedImageFormat.Png), Width: 32, Height: 16),
            (Name: "image.jpg", Bytes: EncodedImage(SKEncodedImageFormat.Jpeg), Width: 32, Height: 16),
            (Name: "image.gif", Bytes: AnimatedGif(), Width: 1, Height: 1),
            (Name: "image.bmp", Bytes: Bitmap24(32, 16), Width: 32, Height: 16),
            (Name: "image.webp", Bytes: EncodedImage(SKEncodedImageFormat.Webp), Width: 32, Height: 16),
        };
        foreach (var fixture in fixtures)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(root.FullName, fixture.Name),
                fixture.Bytes);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(
            typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(
                () =>
                {
                    foreach (var fixture in fixtures)
                    {
                        using var content = FilePreviewContent.FromLocalFile(
                            Path.Combine(root.FullName, fixture.Name));
                        using var bitmap = OrdinaryImagePreviewDecoder.Decode(content);
                        Assert.NotNull(bitmap);
                        Assert.Equal(fixture.Width, bitmap!.PixelSize.Width);
                        Assert.Equal(fixture.Height, bitmap.PixelSize.Height);
                        Assert.True((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height
                            <= Asura.Application.Previews.PreviewRasterBudget.MaximumPixels);
                    }

                    return Task.FromResult(true);
                },
                timeout.Token));
        }
        finally
        {
            await session.DisposeAsync();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Exif_orientation_is_applied_without_upscaling()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"asura-oriented-image-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(
            path,
            WithExifOrientation(
                EncodedImage(SKEncodedImageFormat.Jpeg, width: 2, height: 1),
                orientation: 6));
        using var source = File.OpenRead(path);
        using var codec = SKCodec.Create(source);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedOrigin.RightTop, codec!.EncodedOrigin);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(
            typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(
                () =>
                {
                    using var content = FilePreviewContent.FromLocalFile(path);
                    using var bitmap = OrdinaryImagePreviewDecoder.Decode(content);
                    Assert.NotNull(bitmap);
                    Assert.Equal(1, bitmap!.PixelSize.Width);
                    Assert.Equal(2, bitmap.PixelSize.Height);
                    return Task.FromResult(true);
                },
                timeout.Token));
        }
        finally
        {
            await session.DisposeAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Animated_gif_uses_the_first_frame_policy()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"asura-animated-image-{Guid.NewGuid():N}.gif");
        await File.WriteAllBytesAsync(path, AnimatedGif());
        using (var source = File.OpenRead(path))
        using (var codec = SKCodec.Create(source))
        {
            Assert.NotNull(codec);
            Assert.Equal(2, codec!.FrameCount);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(
            typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(
                () =>
                {
                    using var content = FilePreviewContent.FromLocalFile(path);
                    using var bitmap = OrdinaryImagePreviewDecoder.Decode(content);
                    Assert.NotNull(bitmap);
                    var writeable = Assert.IsType<WriteableBitmap>(bitmap);
                    using var framebuffer = writeable.Lock();
                    Assert.Equal(0, Marshal.ReadByte(framebuffer.Address, 0));
                    Assert.Equal(0, Marshal.ReadByte(framebuffer.Address, 1));
                    Assert.Equal(0, Marshal.ReadByte(framebuffer.Address, 2));
                    return Task.FromResult(true);
                },
                timeout.Token));
        }
        finally
        {
            await session.DisposeAsync();
            File.Delete(path);
        }
    }

    private static byte[] EncodedImage(
        SKEncodedImageFormat format,
        int width = 32,
        int height = 16)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, quality: 90);
        Assert.NotNull(encoded);
        return encoded!.ToArray();
    }

    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        var app1 = new byte[]
        {
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'I', (byte)'I', 0x2A, 0x00,
            0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
        var result = new byte[jpeg.Length + app1.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        app1.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + app1.Length));
        return result;
    }

    private static byte[] AnimatedGif() =>
    [
        (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x4C, 0x01, 0x00,
        0x3B,
    ];

    private static byte[] Bitmap24(int width, int height)
    {
        const int headerBytes = 54;
        var rowBytes = checked((width * 3 + 3) & ~3);
        var pixelBytes = checked(rowBytes * height);
        var bytes = new byte[checked(headerBytes + pixelBytes)];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), pixelBytes);
        return bytes;
    }
}
