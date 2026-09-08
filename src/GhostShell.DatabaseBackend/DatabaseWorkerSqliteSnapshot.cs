using System.Diagnostics;
using System.Security.Cryptography;
using GhostShell.Application;
using GhostShell.Databases;

namespace GhostShell.DatabaseBackend;

/// <summary>
/// Transfers the existing read-only preview image, not its parent-local token.
/// The child owns one contiguous image and unregisters it after its client closes.
/// </summary>
internal sealed class DatabaseWorkerSqliteSnapshot : IDisposable
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const long ProcessReserveBytes = 64L * 1024 * 1024;
    private readonly byte[] _image;
    private int _disposed;

    private DatabaseWorkerSqliteSnapshot(byte[] image)
    {
        _image = image;
        ConnectionString = SqliteInMemoryDatabases.Register(image);
    }

    internal string ConnectionString { get; }

    internal static Stream? Borrow(DatabaseWorkerConnection connection)
    {
        if (!string.Equals(connection.DriverId, "sqlite", StringComparison.Ordinal)) { return null; }
        var snapshot = SqliteInMemoryDatabases.OpenSnapshot(connection.ConnectionString);
        if (snapshot is null) { return null; }
        try { ValidateLength(snapshot.Length); return snapshot; }
        catch { snapshot.Dispose(); throw; }
    }

    internal static async Task SendAsync(Stream image, Stream input, Stream output, CancellationToken token)
    {
        ValidateLength(image.Length);
        var response = await DatabaseOperationProtocol.ReadFrameAsync(input, 32, token).ConfigureAwait(false);
        if (response.AsSpan().SequenceEqual("snapshot-resource"u8))
        {
            throw new IOException("There is not enough memory to open this complete database preview in its worker. No SQL was executed. Free memory and reopen the preview.");
        }
        if (!response.AsSpan().SequenceEqual("snapshot-ready"u8))
        {
            throw new InvalidDataException("The database preview worker could not prepare its image.");
        }
        var remaining = image.Length;
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            await image.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            await DatabaseOperationProtocol.WriteFrameAsync(output, buffer.AsMemory(0, count), token).ConfigureAwait(false);
            remaining -= count;
        }
        await DatabaseOperationProtocol.WriteFrameAsync(output, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
    }

    internal static async Task<DatabaseWorkerSqliteSnapshot> ReceiveAsync(long length, Stream input, Stream output,
        CancellationToken token)
    {
        ValidateLength(length);
        token.ThrowIfCancellationRequested();
        var memory = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        if (!HasHeadroom(length, memory.TotalAvailableMemoryBytes, memory.MemoryLoadBytes, process.WorkingSet64))
        {
            await DatabaseOperationProtocol.WriteFrameAsync(output, "snapshot-resource"u8.ToArray(), token).ConfigureAwait(false);
            throw new IOException("The database preview image exceeds available worker memory.");
        }
        byte[] image;
        try { image = new byte[checked((int)length)]; }
        catch (OutOfMemoryException)
        {
            await DatabaseOperationProtocol.WriteFrameAsync(output, "snapshot-resource"u8.ToArray(), token).ConfigureAwait(false);
            throw;
        }
        try
        {
            await DatabaseOperationProtocol.WriteFrameAsync(output, "snapshot-ready"u8.ToArray(), token).ConfigureAwait(false);
            var offset = 0;
            while (true)
            {
                var chunk = await DatabaseOperationProtocol.ReadFrameAsync(input, 64 * 1024, token).ConfigureAwait(false);
                if (chunk.Length == 0) { break; }
                if (chunk.Length > image.Length - offset) { throw new InvalidDataException("The database preview image length is invalid."); }
                chunk.CopyTo(image, offset);
                offset += chunk.Length;
            }
            if (offset != image.Length) { throw new InvalidDataException("The database preview image is incomplete."); }
            return new DatabaseWorkerSqliteSnapshot(image);
        }
        catch { CryptographicOperations.ZeroMemory(image); throw; }
    }

    internal static bool HasHeadroom(long length, long available, long systemLoad, long workingSet)
    {
        ValidateLength(length);
        // Same sampled accounting as result retention; system load includes
        // process working set. Unknown metrics retain the existing size ceiling.
        return available <= 0
            || Math.Max(0, available - Math.Max(0, Math.Max(systemLoad, workingSet))) >= length + ProcessReserveBytes;
    }

    internal static void ValidateLength(long length)
    {
        if (length is < 0 or > MaximumBytes)
        {
            throw new InvalidDataException("The database preview image exceeds the existing 256 MiB preview limit.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        SqliteInMemoryDatabases.Unregister(ConnectionString);
        // All child clients have closed before this owner is released.
        CryptographicOperations.ZeroMemory(_image);
    }
}
