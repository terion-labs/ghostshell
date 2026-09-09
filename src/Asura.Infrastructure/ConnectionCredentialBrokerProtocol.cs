using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal enum ConnectionCredentialClaimStatus : byte
{
    Success = 0,
    Denied = 1,
    Expired = 2,
    Cancelled = 3,
    VaultFailure = 4,
    Invalid = 5,
    Unavailable = 6,
}

internal sealed record ConnectionCredentialBrokerAccess(
    string PipeName,
    string TicketId,
    string Token,
    ConnectionId ConnectionId);

internal sealed record ConnectionCredentialClaimEntry(
    ConnectionSecretRole Role,
    string? EnvironmentVariableName,
    SecretMaterial Material)
{
    public override string ToString() => $"Credential claim entry ({Role})";
}

internal sealed class ConnectionCredentialClaim : IDisposable
{
    private readonly List<ConnectionCredentialClaimEntry> _entries;
    private bool _disposed;

    public ConnectionCredentialClaim(IEnumerable<ConnectionCredentialClaimEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
    }

    public IReadOnlyList<ConnectionCredentialClaimEntry> Entries => _entries;

    public SecretMaterial? Take(ConnectionSecretRole role)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = _entries.FindIndex(entry => entry.Role == role);
        if (index < 0)
        {
            return null;
        }

        var material = _entries[index].Material;
        _entries.RemoveAt(index);
        return material;
    }

    public IReadOnlyList<ConnectionCredentialClaimEntry> TakeEnvironment()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var environment = _entries
            .Where(entry => entry.Role == ConnectionSecretRole.EnvironmentVariable)
            .ToArray();
        _entries.RemoveAll(entry => entry.Role == ConnectionSecretRole.EnvironmentVariable);
        return environment;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var entry in _entries)
        {
            entry.Material.Dispose();
        }

        _entries.Clear();
        _disposed = true;
    }

    public override string ToString() => $"Credential claim ({_entries.Count} values)";
}

internal abstract record ConnectionCredentialClaimResult
{
    private ConnectionCredentialClaimResult()
    {
    }

    public sealed record Success(ConnectionCredentialClaim Claim) : ConnectionCredentialClaimResult;

    public sealed record Failure(ConnectionCredentialClaimStatus Status) : ConnectionCredentialClaimResult;
}

internal static class ConnectionCredentialBrokerProtocol
{
    private static ReadOnlySpan<byte> RequestMagic => "GCB1"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "GCR1"u8;
    private const int MaximumStringBytes = 4 * 1024;
    private const int MaximumClaimEntries = 256;

    public static async ValueTask WriteRequestAsync(
        Stream stream,
        ConnectionCredentialBrokerAccess access,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(RequestMagic.ToArray(), cancellationToken).ConfigureAwait(false);
        await WriteStringAsync(stream, access.TicketId, cancellationToken).ConfigureAwait(false);
        await WriteStringAsync(stream, access.Token, cancellationToken).ConfigureAwait(false);
        await WriteStringAsync(stream, access.ConnectionId.Value, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ConnectionCredentialBrokerAccess?> ReadRequestAsync(
        Stream stream,
        string pipeName,
        CancellationToken cancellationToken)
    {
        var magic = new byte[RequestMagic.Length];
        await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(magic, RequestMagic))
        {
            return null;
        }

        var ticket = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
        var token = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
        var connection = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
        if (ticket is null || token is null || connection is null)
        {
            return null;
        }

        try
        {
            return new ConnectionCredentialBrokerAccess(
                pipeName,
                ticket,
                token,
                new ConnectionId(connection));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static async ValueTask WriteFailureAsync(
        Stream stream,
        ConnectionCredentialClaimStatus status,
        CancellationToken cancellationToken)
    {
        if (status == ConnectionCredentialClaimStatus.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A failure status is required.");
        }

        await stream.WriteAsync(ResponseMagic.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)status }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteSuccessAsync(
        Stream stream,
        IReadOnlyList<(ConnectionSecretRequirement Requirement, SecretMaterial Material)> values,
        CancellationToken cancellationToken)
    {
        if (values.Count is <= 0 or > MaximumClaimEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }

        await stream.WriteAsync(ResponseMagic.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(
                new byte[] { (byte)ConnectionCredentialClaimStatus.Success },
                cancellationToken)
            .ConfigureAwait(false);
        await WriteInt32Async(stream, values.Count, cancellationToken).ConfigureAwait(false);
        foreach (var (requirement, material) in values)
        {
            await stream.WriteAsync(
                    new byte[] { (byte)requirement.Role },
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteStringAsync(
                    stream,
                    requirement.EnvironmentVariableName ?? string.Empty,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteInt32Async(stream, material.Length, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[material.Length];
            try
            {
                material.CopyTo(buffer);
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ConnectionCredentialClaimResult> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var magic = new byte[ResponseMagic.Length];
        await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(magic, ResponseMagic))
        {
            return new ConnectionCredentialClaimResult.Failure(ConnectionCredentialClaimStatus.Invalid);
        }

        var statusByte = new byte[1];
        await stream.ReadExactlyAsync(statusByte, cancellationToken).ConfigureAwait(false);
        var status = (ConnectionCredentialClaimStatus)statusByte[0];
        if (status != ConnectionCredentialClaimStatus.Success)
        {
            return Enum.IsDefined(status)
                ? new ConnectionCredentialClaimResult.Failure(status)
                : new ConnectionCredentialClaimResult.Failure(ConnectionCredentialClaimStatus.Invalid);
        }

        var count = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (count is <= 0 or > MaximumClaimEntries)
        {
            return new ConnectionCredentialClaimResult.Failure(ConnectionCredentialClaimStatus.Invalid);
        }

        var entries = new List<ConnectionCredentialClaimEntry>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                await stream.ReadExactlyAsync(statusByte, cancellationToken).ConfigureAwait(false);
                var role = (ConnectionSecretRole)statusByte[0];
                var environmentName = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
                var length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
                if (!Enum.IsDefined(role)
                    || environmentName is null
                    || length is <= 0 or > SecretMaterial.MaximumLength
                    || (role == ConnectionSecretRole.EnvironmentVariable) != (environmentName.Length > 0))
                {
                    return Invalid(entries);
                }

                var value = new byte[length];
                try
                {
                    await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
                    entries.Add(new ConnectionCredentialClaimEntry(
                        role,
                        environmentName.Length == 0 ? null : environmentName,
                        SecretMaterial.TakeOwnership(value)));
                    value = [];
                }
                finally
                {
                    if (value.Length > 0)
                    {
                        CryptographicOperations.ZeroMemory(value);
                    }
                }
            }

            return new ConnectionCredentialClaimResult.Success(new ConnectionCredentialClaim(entries));
        }
        catch
        {
            foreach (var entry in entries)
            {
                entry.Material.Dispose();
            }

            throw;
        }
    }

    private static ConnectionCredentialClaimResult Invalid(
        IEnumerable<ConnectionCredentialClaimEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.Material.Dispose();
        }

        return new ConnectionCredentialClaimResult.Failure(ConnectionCredentialClaimStatus.Invalid);
    }

    private static async ValueTask WriteStringAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
        {
            throw new InvalidDataException("The credential-broker metadata is too large.");
        }

        await WriteInt32Async(stream, bytes.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ReadStringAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length is < 0 or > MaximumStringBytes)
        {
            return null;
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static async ValueTask WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}

internal static class ConnectionCredentialBrokerClient
{
    public static async ValueTask<ConnectionCredentialClaimResult> ClaimAsync(
        ConnectionCredentialBrokerAccess access,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);
        while (true)
        {
            try
            {
                await using var pipe = await ConnectAsync(access.PipeName, timeout.Token)
                    .ConfigureAwait(false);
                await ConnectionCredentialBrokerProtocol.WriteRequestAsync(pipe, access, timeout.Token)
                    .ConfigureAwait(false);
                return await ConnectionCredentialBrokerProtocol.ReadResponseAsync(pipe, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ConnectionCredentialClaimResult.Failure(
                    ConnectionCredentialClaimStatus.Cancelled);
            }
            catch (OperationCanceledException)
            {
                return new ConnectionCredentialClaimResult.Failure(
                    ConnectionCredentialClaimStatus.Unavailable);
            }
            catch (IOException) when (!timeout.IsCancellationRequested)
            {
                // A denied claim closes and recreates its one-instance pipe. A following valid
                // helper can briefly reach the retiring socket; retry inside the same deadline.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new ConnectionCredentialClaimResult.Failure(
                        cancellationToken.IsCancellationRequested
                            ? ConnectionCredentialClaimStatus.Cancelled
                            : ConnectionCredentialClaimStatus.Unavailable);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new ConnectionCredentialClaimResult.Failure(
                    ConnectionCredentialClaimStatus.Unavailable);
            }
        }
    }

    private static async ValueTask<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
