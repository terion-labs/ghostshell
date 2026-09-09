using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Asura.ConnectionBackend;

/// <summary>Detached schema documents can span frames, but never exceed the result metadata budget.</summary>
internal static class DatabaseMetadataProtocol
{
    internal static async Task WriteAsync<T>(Stream output, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var document = new BoundedDocument();
        await JsonSerializer.SerializeAsync(document, value, type, token).ConfigureAwait(false);
        var bytes = document.GetBuffer().AsMemory(0, checked((int)document.Length));
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(bytes.Length, DatabaseOperationProtocol.MaximumMetadataBytes);
            await DatabaseOperationProtocol.WriteFrameAsync(output, bytes[..count], token).ConfigureAwait(false);
            bytes = bytes[count..];
        }
        await DatabaseOperationProtocol.WriteFrameAsync(output, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream input, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var document = new BoundedDocument();
        while (true)
        {
            var frame = await DatabaseOperationProtocol.ReadFrameAsync(input, DatabaseOperationProtocol.MaximumMetadataBytes, token).ConfigureAwait(false);
            if (frame.Length == 0) { break; }
            await document.WriteAsync(frame, token).ConfigureAwait(false);
        }
        document.Position = 0;
        return await JsonSerializer.DeserializeAsync(document, type, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The database metadata document is invalid.");
    }

    private sealed class BoundedDocument : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckCapacity(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void CheckCapacity(int count)
        {
            if (Position > DatabaseOperationProtocol.MaximumResultMetadataBytes - count)
            {
                throw new DatabaseResultRetentionException();
            }
        }
    }
}
