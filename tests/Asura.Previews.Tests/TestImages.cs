using System.Buffers.Binary;

namespace Asura.Previews.Tests;

/// <summary>
/// Test images built byte by byte rather than checked in, so the fixtures are
/// readable, exact about their dimensions, and cannot drift from what the tests
/// claim they are.
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// A minimal uncompressed little-endian RGB TIFF: header, strip of pixels,
    /// then the tag directory.
    /// </summary>
    public static byte[] Tiff(int width, int height)
    {
        const int headerLength = 8;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                pixels[offset] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[offset + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[offset + 2] = 128;
            }
        }

        var entries = new (ushort Tag, ushort Type, uint Count, uint Value)[]
        {
            (256, 3, 1, (uint)width),                        // ImageWidth
            (257, 3, 1, (uint)height),                       // ImageLength
            (258, 3, 3, (uint)(headerLength + pixels.Length + 2 + (12 * 9) + 4)), // BitsPerSample
            (259, 3, 1, 1),                                  // Compression: none
            (262, 3, 1, 2),                                  // Photometric: RGB
            (273, 4, 1, headerLength),                       // StripOffsets
            (277, 3, 1, 3),                                  // SamplesPerPixel
            (278, 3, 1, (uint)height),                       // RowsPerStrip
            (279, 4, 1, (uint)pixels.Length),                // StripByteCounts
        };

        var directoryOffset = headerLength + pixels.Length;
        var bitsPerSampleOffset = directoryOffset + 2 + (12 * entries.Length) + 4;
        var image = new byte[bitsPerSampleOffset + 6];

        image[0] = (byte)'I';
        image[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)directoryOffset);
        pixels.CopyTo(image.AsSpan(headerLength));

        BinaryPrimitives.WriteUInt16LittleEndian(
            image.AsSpan(directoryOffset),
            (ushort)entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var at = directoryOffset + 2 + (index * 12);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), entry.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 2), entry.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 4), entry.Count);
            if (entry.Type == 3 && entry.Count == 1)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 8), (ushort)entry.Value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 8), entry.Value);
            }
        }

        for (var sample = 0; sample < 3; sample++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                image.AsSpan(bitsPerSampleOffset + (sample * 2)),
                8);
        }

        return image;
    }

    /// <summary>The dimensions a PNG declares in its header chunk.</summary>
    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png[16..]),
            BinaryPrimitives.ReadInt32BigEndian(png[20..]));
}
