using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;

namespace Asura.Desktop;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DatabaseDiagramRequest(
    string Operation,
    string? DriverId = null,
    string? ConnectionString = null,
    DatabaseDiagramViewport? Viewport = null,
    DatabaseDiagramExport Format = DatabaseDiagramExport.MermaidMarkdown,
    long? SqliteSnapshotBytes = null);

[JsonSerializable(typeof(DatabaseDiagramRequest))]
internal sealed partial class DatabaseDiagramJsonContext : JsonSerializerContext;

internal static class DatabaseDiagramProtocol
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    internal const int MaximumImageBytes = 68 * 1024 * 1024;
    internal const int MaximumExportChunkBytes = 64 * 1024;

    public static async Task WriteRequestAsync(Stream stream, DatabaseDiagramRequest request, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, DatabaseDiagramJsonContext.Default.DatabaseDiagramRequest);
        if (bytes.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException("The database worker request is too large.");
        }

        await WriteFrameAsync(stream, bytes, token).ConfigureAwait(false);
    }

    public static async Task<DatabaseDiagramRequest> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var bytes = await ReadFrameAsync(stream, MaximumRequestBytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, DatabaseDiagramJsonContext.Default.DatabaseDiagramRequest)
            ?? throw new InvalidDataException("The database worker request is invalid.");
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, int maximum, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maximum)
        {
            throw new InvalidDataException("The database worker response is invalid.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
}
