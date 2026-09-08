using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GhostShell.ConnectionBackend;

/// <summary>Length-prefixed, source-generated JSON with a bound enforced while serializing and before reading allocation.</summary>
internal static class BackendJsonFrames
{
    internal const int MaximumBytes = 16 * 1024 * 1024;

    internal static async Task<byte[]> SerializeAsync<T>(T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        using var buffer = new BoundedFrame();
        await JsonSerializer.SerializeAsync(buffer, value, type, token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    internal static async Task<T> ReadAsync<T>(Stream input, JsonTypeInfo<T> type, CancellationToken token)
    {
        var frame = await DatabaseOperationProtocol.ReadFrameAsync(input, MaximumBytes, token).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize(frame, type) ?? throw new InvalidDataException("The backend frame is invalid."); }
        finally { CryptographicOperations.ZeroMemory(frame); }
    }

    internal static async Task WriteAsync<T>(Stream output, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        var bytes = await SerializeAsync(value, type, token).ConfigureAwait(false);
        try { await DatabaseOperationProtocol.WriteFrameAsync(output, bytes, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // SSH.NET trust/sign callbacks are synchronous. These operations run only in
    // the owned headless child; the desktop side always uses the async methods.
    internal static void Write<T>(Stream output, T value, JsonTypeInfo<T> type)
    {
        using var buffer = new BoundedFrame();
        JsonSerializer.Serialize(buffer, value, type);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, checked((int)buffer.Length));
        output.Write(header);
        var bytes = buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length));
        try { output.Write(bytes); output.Flush(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static T Read<T>(Stream input, JsonTypeInfo<T> type)
    {
        Span<byte> header = stackalloc byte[4];
        input.ReadExactly(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumBytes) { throw new InvalidDataException("The backend frame size is invalid."); }
        var bytes = new byte[length];
        try
        {
            input.ReadExactly(bytes);
            return JsonSerializer.Deserialize(bytes, type) ?? throw new InvalidDataException("The backend frame is invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class BoundedFrame : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var bytes)) { CryptographicOperations.ZeroMemory(bytes.Array!); }
            base.Dispose(disposing);
        }

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
            if (Position > MaximumBytes - count) { throw new InvalidDataException("The backend frame exceeds its 16 MiB limit."); }
        }
    }
}
