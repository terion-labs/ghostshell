using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;

namespace Asura.Infrastructure;

internal enum ConnectionCredentialAskpassRole
{
    Password,
    PrivateKeyPassphrase,
}

internal sealed record ConnectionCredentialAskpassAccess(
    string PipeName,
    string Token,
    ConnectionCredentialAskpassRole Role);

internal sealed class ConnectionCredentialAskpassServer : IAsyncDisposable
{
    private static ReadOnlySpan<byte> RequestMagic => "GAP2"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "GAR2"u8;
    private readonly SecretMaterial _material;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _completion;
    private bool _disposed;

    private ConnectionCredentialAskpassServer(
        ConnectionCredentialAskpassAccess access,
        SecretMaterial material)
    {
        Access = access;
        _material = material;
        _completion = ServeAsync();
    }

    public ConnectionCredentialAskpassAccess Access { get; }

    public static ConnectionCredentialAskpassServer Create(
        SecretMaterial material,
        ConnectionCredentialAskpassRole role)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }

        ValidateLine(material);
        return new ConnectionCredentialAskpassServer(
            new ConnectionCredentialAskpassAccess(
                $"asura-askpass-{RandomNumberGenerator.GetHexString(16, lowercase: true)}",
                RandomNumberGenerator.GetHexString(32, lowercase: true),
                role),
            material);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _material.Dispose();
        _lifetime.Dispose();
    }

    public static async ValueTask<SecretMaterial?> ClaimAsync(
        ConnectionCredentialAskpassAccess access,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                access.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(RequestMagic.ToArray(), timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { (byte)access.Role }, timeout.Token).ConfigureAwait(false);
            await WriteStringAsync(pipe, access.Token, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            var magic = new byte[ResponseMagic.Length];
            await pipe.ReadExactlyAsync(magic, timeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(magic, ResponseMagic))
            {
                return null;
            }

            var lengthBytes = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(lengthBytes, timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is <= 0 or > SecretMaterial.MaximumLength)
            {
                return null;
            }

            var value = new byte[length];
            try
            {
                await pipe.ReadExactlyAsync(value, timeout.Token).ConfigureAwait(false);
                var material = SecretMaterial.TakeOwnership(value);
                value = [];
                return material;
            }
            finally
            {
                if (value.Length > 0)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task ServeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var pipe = new NamedPipeServerStream(
                Access.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var magic = new byte[RequestMagic.Length];
            await pipe.ReadExactlyAsync(magic, timeout.Token).ConfigureAwait(false);
            var roleValue = new byte[1];
            await pipe.ReadExactlyAsync(roleValue, timeout.Token).ConfigureAwait(false);
            var role = (ConnectionCredentialAskpassRole)roleValue[0];
            var token = await ReadStringAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(magic, RequestMagic)
                || !Enum.IsDefined(role)
                || role != Access.Role
                || token is null
                || !FixedTimeEquals(Access.Token, token))
            {
                return;
            }

            await pipe.WriteAsync(ResponseMagic.ToArray(), timeout.Token).ConfigureAwait(false);
            var lengthBytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, _material.Length);
            await pipe.WriteAsync(lengthBytes, timeout.Token).ConfigureAwait(false);
            var value = new byte[_material.Length];
            try
            {
                _material.CopyTo(value);
                await pipe.WriteAsync(value, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(value);
                _material.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateLine(SecretMaterial material)
    {
        var value = new byte[material.Length];
        try
        {
            material.CopyTo(value);
            if (value.AsSpan().IndexOfAny((byte)'\0', (byte)'\r', (byte)'\n') >= 0)
            {
                throw new InvalidDataException("The credential cannot be supplied to OpenSSH askpass.");
            }

            _ = new UTF8Encoding(false, true).GetCharCount(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The credential cannot be supplied to OpenSSH askpass.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static async ValueTask WriteStringAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ReadStringAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > 256)
        {
            return null;
        }

        var value = new byte[length];
        await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
        return Encoding.ASCII.GetString(value);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
