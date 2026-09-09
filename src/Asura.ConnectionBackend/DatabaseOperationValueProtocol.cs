using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Databases;

namespace Asura.ConnectionBackend;

internal enum DatabaseWireValueFormat { Null, Scalar, Text, Binary, Json, Array, NumericText, IpAddress, Sequence }

internal sealed record DatabaseWireValueHeader(
    DatabaseValueKind Kind,
    DatabaseWireValueFormat Format,
    string DisplayText,
    bool IsTruncated,
    DatabaseArrayScalarType? ScalarType = null);

[JsonSerializable(typeof(DatabaseWireValueHeader))]
internal sealed partial class DatabaseWireValueJsonContext : JsonSerializerContext;

/// <summary>
/// Closed typed values over private operation pipes. Only the provider worker
/// reconstructs full parameter objects; the desktop retains large result bytes
/// in result-owned encrypted storage.
/// </summary>
internal static class DatabaseOperationValueProtocol
{
    internal const int MaximumChunkBytes = 64 * 1024;
    internal const int MaximumHeaderBytes = 32 * 1024;
    internal const int MaximumInlineBytes = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Task WriteParameterAsync(Stream destination, object? value, CancellationToken token) =>
        WriteAsync(destination, new DatabaseValue(value, ParameterKind(value), string.Empty), token);

    public static Task WriteAsync(Stream destination, DatabaseValue value, CancellationToken token) =>
        WriteCoreAsync(destination, value, token, depth: 0);

    private static async Task WriteCoreAsync(Stream destination, DatabaseValue value, CancellationToken token, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("The database parameter sequence nesting is invalid.");
        }
        var header = Describe(value);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(header, DatabaseWireValueJsonContext.Default.DatabaseWireValueHeader);
        if (metadata.Length > MaximumHeaderBytes)
        {
            throw new InvalidDataException("The database value header is too large.");
        }

        await DatabaseOperationProtocol.WriteFrameAsync(destination, metadata, token).ConfigureAwait(false);
        if (header.Format == DatabaseWireValueFormat.Null)
        {
            return;
        }

        if (header.Format == DatabaseWireValueFormat.Sequence)
        {
            var sequence = (IReadOnlyList<object?>)value.RawValue!;
            DatabaseOperationProtocol.ValidateCount(sequence.Count, DatabaseOperationProtocol.MaximumParameterEntries);
            var count = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(count, sequence.Count);
            await DatabaseOperationProtocol.WriteFrameAsync(destination, count, token).ConfigureAwait(false);
            foreach (var item in sequence)
            {
                await WriteCoreAsync(destination, new DatabaseValue(item, ParameterKind(item), string.Empty), token, depth + 1).ConfigureAwait(false);
            }
            return;
        }

        switch (value.RawValue)
        {
            case DatabaseValueContent content:
                await using (var source = content.OpenRead())
                {
                    await WriteBodyAsync(destination, source, token).ConfigureAwait(false);
                }
                break;
            case byte[] bytes:
                await using (var source = new MemoryStream(bytes, writable: false))
                {
                    await WriteBodyAsync(destination, source, token).ConfigureAwait(false);
                }
                break;
            case Array array:
                await Task.Run(() =>
                {
                    using var chunks = new ChunkWriter(destination, token);
                    DatabaseArrayContent.Write(chunks, array, token);
                    chunks.Flush();
                }, token).ConfigureAwait(false);
                break;
            case JsonElement element:
                await Task.Run(() =>
                {
                    using var chunks = new ChunkWriter(destination, token);
                    using var writer = new Utf8JsonWriter(chunks);
                    element.WriteTo(writer);
                    writer.Flush();
                    chunks.Flush();
                }, token).ConfigureAwait(false);
                break;
            case string text:
                await WriteTextAsync(destination, text, token).ConfigureAwait(false);
                break;
            case Int128 or UInt128 or BigInteger or IPAddress:
                await WriteTextAsync(destination, Convert.ToString(value.RawValue, CultureInfo.InvariantCulture)!, token).ConfigureAwait(false);
                break;
            default:
                using (var scalar = new MemoryStream())
                {
                    DatabaseArrayContent.WriteScalarValue(scalar, value.RawValue!);
                    scalar.Position = 0;
                    await WriteBodyAsync(destination, scalar, token).ConfigureAwait(false);
                }
                break;
        }

        await DatabaseOperationProtocol.WriteFrameAsync(destination, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
    }

    public static async Task<DatabaseValue> ReadResultAsync(
        Stream source, Func<DatabaseValueContentStore> getStore, CancellationToken token,
        Func<int, bool>? reserveInlineBytes = null)
    {
        var header = await ReadHeaderAsync(source, token).ConfigureAwait(false);
        if (header.Format == DatabaseWireValueFormat.Sequence)
        {
            throw new InvalidDataException("Untyped parameter sequences are not database result values.");
        }
        if (header.Format == DatabaseWireValueFormat.Null)
        {
            return new DatabaseValue(null, header.Kind, header.DisplayText);
        }

        if (header.Format == DatabaseWireValueFormat.Array)
        {
            var array = await StoreBodyAsync(source, getStore(), DatabaseValueKind.Collection, token).ConfigureAwait(false);
            return new DatabaseValue(array, header.Kind, header.DisplayText, header.IsTruncated);
        }

        using var inline = new MemoryStream();
        while (true)
        {
            var chunk = await ReadChunkAsync(source, token).ConfigureAwait(false);
            if (chunk.Length == 0)
            {
                // A page shares this reservation across cells. Small cells may
                // spill too; the per-cell threshold alone cannot bound a page.
                if (header.Format is not (DatabaseWireValueFormat.Scalar or DatabaseWireValueFormat.IpAddress)
                    && reserveInlineBytes is not null && !reserveInlineBytes(checked((int)inline.Length * 2 + 32)))
                {
                    var stored = await getStore().StoreAsync(ContentKind(header), async (destination, cancellation) =>
                    {
                        inline.Position = 0;
                        await inline.CopyToAsync(destination, cancellation).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
                    if (header.ScalarType is { } storedType) { stored = new NumericContent(stored, storedType); }
                    return new DatabaseValue(stored, header.Kind, header.DisplayText, header.IsTruncated);
                }
                var raw = DecodeInline(inline.ToArray(), header);
                return new DatabaseValue(raw, header.Kind, header.DisplayText, header.IsTruncated);
            }

            if (inline.Length + chunk.Length > MaximumInlineBytes)
            {
                if (header.Format is DatabaseWireValueFormat.Scalar or DatabaseWireValueFormat.IpAddress)
                {
                    throw new InvalidDataException("The fixed database scalar payload is invalid.");
                }

                var content = await getStore().StoreAsync(ContentKind(header), async (destination, cancellation) =>
                {
                    inline.Position = 0;
                    await inline.CopyToAsync(destination, cancellation).ConfigureAwait(false);
                    await destination.WriteAsync(chunk, cancellation).ConfigureAwait(false);
                    await CopyRemainingBodyAsync(source, destination, cancellation).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                if (header.ScalarType is { } scalarType)
                {
                    content = new NumericContent(content, scalarType);
                }

                return new DatabaseValue(content, header.Kind, header.DisplayText, header.IsTruncated);
            }

            inline.Write(chunk);
        }
    }

    public static Task<object?> ReadParameterAsync(Stream source, CancellationToken token) =>
        ReadParameterCoreAsync(source, token, depth: 0);

    private static async Task<object?> ReadParameterCoreAsync(Stream source, CancellationToken token, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("The database parameter sequence nesting is invalid.");
        }
        var header = await ReadHeaderAsync(source, token).ConfigureAwait(false);
        if (header.Format == DatabaseWireValueFormat.Null)
        {
            return null;
        }

        if (header.Format == DatabaseWireValueFormat.Sequence)
        {
            var bytes = await DatabaseOperationProtocol.ReadFrameAsync(source, 4, token).ConfigureAwait(false);
            if (bytes.Length != 4)
            {
                throw new InvalidDataException("The database parameter sequence count is invalid.");
            }
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            DatabaseOperationProtocol.ValidateCount(count, DatabaseOperationProtocol.MaximumParameterEntries);
            var values = new object?[count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = await ReadParameterCoreAsync(source, token, depth + 1).ConfigureAwait(false);
            }
            return values;
        }

        // Only the watched worker calls this method. A provider may require a
        // full CLR parameter; no such allocation is done by ReadResultAsync.
        using var body = new MemoryStream();
        while (true)
        {
            var chunk = await ReadChunkAsync(source, token).ConfigureAwait(false);
            if (chunk.Length == 0) { break; }
            if (header.Format is DatabaseWireValueFormat.Scalar or DatabaseWireValueFormat.IpAddress
                && body.Length + chunk.Length > MaximumInlineBytes)
            {
                throw new InvalidDataException("The fixed database scalar payload is invalid.");
            }
            body.Write(chunk);
        }
        body.Position = 0;
        if (header.Format == DatabaseWireValueFormat.Array)
        {
            return DatabaseArrayReconstruction.Read(body, token);
        }

        return DecodeInline(body.ToArray(), header);
    }

    private static DatabaseWireValueHeader Describe(DatabaseValue value)
    {
        var format = value.RawValue switch
        {
            null => DatabaseWireValueFormat.Null,
            DatabaseValueContent { ScalarType: not null } => DatabaseWireValueFormat.NumericText,
            DatabaseValueContent content => content.Kind switch
            {
                DatabaseValueKind.Binary => DatabaseWireValueFormat.Binary,
                DatabaseValueKind.Json => DatabaseWireValueFormat.Json,
                DatabaseValueKind.Collection => DatabaseWireValueFormat.Array,
                _ => DatabaseWireValueFormat.Text,
            },
            byte[] => DatabaseWireValueFormat.Binary,
            object?[] array when array.GetType() == typeof(object[]) => DatabaseWireValueFormat.Sequence,
            IReadOnlyList<object?> when value.RawValue is not Array => DatabaseWireValueFormat.Sequence,
            Array => DatabaseWireValueFormat.Array,
            JsonElement => DatabaseWireValueFormat.Json,
            string text => value.Kind == DatabaseValueKind.Json && IsValidJson(text)
                ? DatabaseWireValueFormat.Json : DatabaseWireValueFormat.Text,
            Int128 or UInt128 or BigInteger => DatabaseWireValueFormat.NumericText,
            IPAddress => DatabaseWireValueFormat.IpAddress,
            _ => DatabaseWireValueFormat.Scalar,
        };
        var scalarType = value.RawValue switch
        {
            DatabaseValueContent content => content.ScalarType,
            Int128 => DatabaseArrayScalarType.Int128,
            UInt128 => DatabaseArrayScalarType.UInt128,
            BigInteger => DatabaseArrayScalarType.BigInteger,
            _ => (DatabaseArrayScalarType?)null,
        };
        if (value.DisplayText.Length > 4096)
        {
            throw new InvalidDataException("The database value preview exceeds its display bound.");
        }

        return new DatabaseWireValueHeader(value.Kind, format, value.DisplayText, value.IsTruncated, scalarType);
    }

    private static async Task<DatabaseWireValueHeader> ReadHeaderAsync(Stream source, CancellationToken token)
    {
        var bytes = await DatabaseOperationProtocol.ReadFrameAsync(source, MaximumHeaderBytes, token).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize(bytes, DatabaseWireValueJsonContext.Default.DatabaseWireValueHeader)
            ?? throw new InvalidDataException("The database value header is invalid.");
        if (!Enum.IsDefined(header.Kind) || !Enum.IsDefined(header.Format)
            || header.DisplayText is null || header.DisplayText.Length > 4096
            || (header.Format == DatabaseWireValueFormat.NumericText
                ? header.ScalarType is not (DatabaseArrayScalarType.Int128 or DatabaseArrayScalarType.UInt128 or DatabaseArrayScalarType.BigInteger)
                : header.ScalarType is not null))
        {
            throw new InvalidDataException("The database value header is invalid.");
        }

        return header;
    }

    private static object? DecodeInline(byte[] bytes, DatabaseWireValueHeader header) => header.Format switch
    {
        DatabaseWireValueFormat.Text or DatabaseWireValueFormat.Json => Utf8.GetString(bytes),
        DatabaseWireValueFormat.Binary => bytes,
        DatabaseWireValueFormat.NumericText => ParseNumber(Utf8.GetString(bytes), header.ScalarType),
        DatabaseWireValueFormat.IpAddress => IPAddress.Parse(Utf8.GetString(bytes)),
        DatabaseWireValueFormat.Scalar => ReadScalar(bytes),
        _ => throw new InvalidDataException("The database scalar encoding is invalid."),
    };

    private static object? ReadScalar(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, writable: false);
        var shape = DatabaseArrayContent.ReadShape(source);
        if (shape.Count != 1 || shape.Lengths.Length != 1 || shape.LowerBounds[0] != 0 || shape.Nullable)
        {
            throw new InvalidDataException("The database scalar shape is invalid.");
        }

        var result = DatabaseArrayContent.ReadScalar(source, shape, (_, _) =>
            throw new InvalidDataException("Text must use the streamed database value encoding."));
        if (source.Position != source.Length)
        {
            throw new InvalidDataException("The database scalar has trailing payload bytes.");
        }

        return result;
    }

    private static object ParseNumber(string text, DatabaseArrayScalarType? type) => type switch
    {
        DatabaseArrayScalarType.Int128 => (object)Int128.Parse(text, CultureInfo.InvariantCulture),
        DatabaseArrayScalarType.UInt128 => (object)UInt128.Parse(text, CultureInfo.InvariantCulture),
        DatabaseArrayScalarType.BigInteger => (object)BigInteger.Parse(text, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("The database numeric type is invalid."),
    };

    private static bool IsValidJson(string text)
    {
        try { using var document = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    private static DatabaseValueKind ContentKind(DatabaseWireValueHeader header) => header.Format switch
    {
        DatabaseWireValueFormat.Binary => DatabaseValueKind.Binary,
        DatabaseWireValueFormat.Json => DatabaseValueKind.Json,
        DatabaseWireValueFormat.Array => DatabaseValueKind.Collection,
        DatabaseWireValueFormat.NumericText => header.ScalarType == DatabaseArrayScalarType.UInt128
            ? DatabaseValueKind.UnsignedInteger : DatabaseValueKind.SignedInteger,
        _ => DatabaseValueKind.Text,
    };

    private static DatabaseValueKind ParameterKind(object? value) => value switch
    {
        DatabaseValueContent content => content.Kind,
        byte[] => DatabaseValueKind.Binary,
        Array => DatabaseValueKind.Collection,
        JsonElement => DatabaseValueKind.Json,
        string => DatabaseValueKind.Text,
        Int128 or BigInteger => DatabaseValueKind.SignedInteger,
        UInt128 => DatabaseValueKind.UnsignedInteger,
        _ => DatabaseValueKind.Other,
    };

    private static async Task<DatabaseValueContent> StoreBodyAsync(Stream source,
        DatabaseValueContentStore store, DatabaseValueKind kind, CancellationToken token) =>
        await store.StoreAsync(kind, async (destination, cancellation) =>
        {
            await CopyRemainingBodyAsync(source, destination, cancellation).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

    private static async Task CopyRemainingBodyAsync(Stream source, Stream destination, CancellationToken token)
    {
        while (true)
        {
            var chunk = await ReadChunkAsync(source, token).ConfigureAwait(false);
            if (chunk.Length == 0) { return; }
            await destination.WriteAsync(chunk, token).ConfigureAwait(false);
        }
    }

    private static Task<byte[]> ReadChunkAsync(Stream source, CancellationToken token) =>
        DatabaseOperationProtocol.ReadFrameAsync(source, MaximumChunkBytes, token);

    private static async Task WriteBodyAsync(Stream destination, Stream source, CancellationToken token)
    {
        var buffer = new byte[MaximumChunkBytes];
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            await DatabaseOperationProtocol.WriteFrameAsync(destination, buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private static async Task WriteTextAsync(Stream destination, string text, CancellationToken token)
    {
        var encoder = Utf8.GetEncoder();
        var buffer = new byte[MaximumChunkBytes];
        var offset = 0;
        while (offset < text.Length)
        {
            encoder.Convert(text.AsSpan(offset), buffer, flush: true, out var chars, out var bytes, out _);
            offset += chars;
            await DatabaseOperationProtocol.WriteFrameAsync(destination, buffer.AsMemory(0, bytes), token).ConfigureAwait(false);
        }
    }

    private sealed class NumericContent(DatabaseValueContent source, DatabaseArrayScalarType scalarType) : DatabaseValueContent
    {
        public override long Length => source.Length;
        public override DatabaseValueKind Kind => source.Kind;
        public override DatabaseArrayScalarType? ScalarType => scalarType;
        public override Stream OpenRead() => source.OpenRead();
    }

    // Array/DOM serializers are synchronous; invoke this only on the writer's
    // background task. Its fixed buffer provides pipe backpressure, not a
    // second complete payload. The process owner closes pipes on cancellation.
    private sealed class ChunkWriter(Stream destination, CancellationToken token) : Stream
    {
        private readonly byte[] _buffer = new byte[MaximumChunkBytes];
        private int _count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length, _buffer.Length - _count);
                buffer[..count].CopyTo(_buffer.AsSpan(_count));
                _count += count;
                buffer = buffer[count..];
                if (_count == _buffer.Length) { Flush(); }
            }
        }
        public override void Flush()
        {
            token.ThrowIfCancellationRequested();
            if (_count == 0) { return; }
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, _count);
            destination.Write(header);
            destination.Write(_buffer.AsSpan(0, _count));
            _count = 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
