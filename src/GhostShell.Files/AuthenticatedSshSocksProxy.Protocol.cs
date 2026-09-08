using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Files;

internal sealed partial class AuthenticatedSshSocksProxy
{
    private async Task<bool> AuthenticateAsync(
        Stream stream,
        Action authenticatedCallback,
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 5 || header[1] == 0)
        {
            return false;
        }

        var methods = new byte[header[1]];
        await stream.ReadExactlyAsync(methods, cancellationToken).ConfigureAwait(false);
        var offered = methods.Contains((byte)2);
        await stream.WriteAsync(new byte[] { 5, offered ? (byte)2 : (byte)255 }, cancellationToken).ConfigureAwait(false);
        if (!offered)
        {
            return false;
        }

        byte[]? username = null;
        byte[]? password = null;
        var authenticated = false;
        try
        {
            if (await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) == 1)
            {
                username = await ReadCredentialAsync(stream, cancellationToken).ConfigureAwait(false);
                password = username is null ? null
                    : await ReadCredentialAsync(stream, cancellationToken).ConfigureAwait(false);
                if (username is not null && password is not null)
                {
                    authenticated = CryptographicOperations.FixedTimeEquals(SHA256.HashData(username), _usernameHash)
                        & CryptographicOperations.FixedTimeEquals(SHA256.HashData(password), _passwordHash);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // A half-closed truncated authentication still receives RFC 1929 failure.
        }
        finally
        {
            if (username is not null)
            {
                CryptographicOperations.ZeroMemory(username);
            }
            if (password is not null)
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }
        if (authenticated)
        {
            authenticatedCallback();
        }
        await stream.WriteAsync(new byte[] { 1, authenticated ? (byte)0 : (byte)1 }, cancellationToken).ConfigureAwait(false);
        return authenticated;
    }

    private static async Task<byte[]?> ReadCredentialAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length == 0)
        {
            return null;
        }
        var value = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
            return value;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(value);
            throw;
        }
    }

    private static async Task<(string Host, ushort Port)?> ReadDestinationAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != 5 || header[1] != 1 || header[2] != 0)
        {
            await WriteReplyAsync(stream, 7, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var length = header[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
        if (length == 0)
        {
            await WriteReplyAsync(stream, 8, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var address = new byte[length];
        await stream.ReadExactlyAsync(address, cancellationToken).ConfigureAwait(false);
        var host = header[3] == 3
            ? Encoding.UTF8.GetString(address)
            : new IPAddress(address).ToString();
        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, cancellationToken).ConfigureAwait(false);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
        if (string.IsNullOrWhiteSpace(host) || host.IndexOf('\0') >= 0 || port == 0)
        {
            await WriteReplyAsync(stream, 8, cancellationToken).ConfigureAwait(false);
            return null;
        }
        // Domain names are forwarded unchanged to SSH. No local DNS lookup occurs.
        return (host, port);
    }

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static ValueTask WriteReplyAsync(Stream stream, byte code, CancellationToken cancellationToken) =>
        stream.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken);
}
