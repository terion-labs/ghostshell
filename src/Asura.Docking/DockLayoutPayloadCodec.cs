using System.IO.Compression;
using System.Text;

namespace Asura.Docking;

/// <summary>
/// Keeps Dock's serializer as the authority for layout semantics while storing
/// its highly repetitive object graph in a bounded, compact envelope.
/// </summary>
public static class DockLayoutPayloadCodec
{
    private const string BrotliPrefix = "dock.br.1:";
    public const int MaximumDecodedBytes = 4 * 1024 * 1024;

    public static string Encode(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDecodedBytes)
        {
            throw new InvalidDataException(
                "The serialized Dock layout exceeds the supported size.");
        }

        var source = Encoding.UTF8.GetBytes(json);
        using var destination = new MemoryStream();
        using (var compressor = new BrotliStream(
                   destination,
                   CompressionLevel.Fastest,
                   leaveOpen: true))
        {
            compressor.Write(source);
        }

        return BrotliPrefix + Convert.ToBase64String(
            destination.GetBuffer(),
            0,
            checked((int)destination.Length));
    }

    public static string Decode(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (!payload.StartsWith(BrotliPrefix, StringComparison.Ordinal))
        {
            if (Encoding.UTF8.GetByteCount(payload) > MaximumDecodedBytes)
            {
                throw new InvalidDataException(
                    "The serialized Dock layout exceeds the supported size.");
            }

            // Bounded Dock JSON written before the compact envelope remains readable.
            return payload;
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(payload[BrotliPrefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The serialized Dock layout envelope is invalid.",
                exception);
        }

        using var source = new MemoryStream(compressed, writable: false);
        using var decompressor = new BrotliStream(
            source,
            CompressionMode.Decompress,
            leaveOpen: false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = decompressor.Read(buffer);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumDecodedBytes)
            {
                throw new InvalidDataException(
                    "The serialized Dock layout expands beyond the supported size.");
            }

            destination.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }
}
