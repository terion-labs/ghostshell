using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Desktop;

internal static class WorkspaceLoopbackProxyProtocol
{
    private const int MaximumHttpHeaderBytes = 16 * 1024;
    private static readonly byte[] HttpHeaderTerminator = "\r\n\r\n"u8.ToArray();

    public static WorkspaceNetworkProxyCredentials CreateCredentials() =>
        new(
            $"ghostshell-{RandomNumberGenerator.GetHexString(8, lowercase: true)}",
            RandomNumberGenerator.GetHexString(32, lowercase: true));

    public static async ValueTask<Request?> AuthenticateAndReadAsync(
        Stream stream,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken cancellationToken)
    {
        var first = new byte[1];
        if (!await ReadExactlyAsync(stream, first, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return first[0] == 5
            ? await ReadSocksAsync(stream, credentials, cancellationToken).ConfigureAwait(false)
            : await ReadHttpProxyAsync(
                    stream,
                    first[0],
                    credentials,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public static ValueTask ReplyAsync(
        Stream stream,
        Protocol protocol,
        byte socksStatus,
        CancellationToken cancellationToken) =>
        protocol == Protocol.Socks5
            ? stream.WriteAsync(
                new byte[] { 5, socksStatus, 0, 1, 0, 0, 0, 0, 0, 0 },
                cancellationToken)
            : stream.WriteAsync(
                socksStatus == 0
                    ? "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray()
                    : "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(),
                cancellationToken);

    private static async ValueTask<Request?> ReadSocksAsync(
        Stream stream,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken cancellationToken)
    {
        var methodCount = new byte[1];
        if (!await ReadExactlyAsync(stream, methodCount, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var methods = new byte[methodCount[0]];
        if (!await ReadExactlyAsync(stream, methods, cancellationToken).ConfigureAwait(false)
            || !methods.Contains((byte)2))
        {
            await stream.WriteAsync(new byte[] { 5, 255 }, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        await stream.WriteAsync(new byte[] { 5, 2 }, cancellationToken).ConfigureAwait(false);
        if (!await AuthenticateSocksAsync(stream, credentials, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        var request = new byte[4];
        if (!await ReadExactlyAsync(stream, request, cancellationToken).ConfigureAwait(false)
            || request[0] != 5
            || request[1] != 1)
        {
            return null;
        }

        var host = await ReadSocksHostAsync(stream, request[3], cancellationToken)
            .ConfigureAwait(false);
        var port = new byte[2];
        if (host is null
            || !await ReadExactlyAsync(stream, port, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Request(
            Protocol.Socks5,
            host,
            BinaryPrimitives.ReadUInt16BigEndian(port));
    }

    private static async ValueTask<bool> AuthenticateSocksAsync(
        Stream stream,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)
            || header[0] != 1
            || header[1] == 0)
        {
            await stream.WriteAsync(new byte[] { 1, 1 }, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var username = new byte[header[1]];
        var passwordLength = new byte[1];
        if (!await ReadExactlyAsync(stream, username, cancellationToken).ConfigureAwait(false)
            || !await ReadExactlyAsync(stream, passwordLength, cancellationToken)
                .ConfigureAwait(false)
            || passwordLength[0] == 0)
        {
            await stream.WriteAsync(new byte[] { 1, 1 }, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var password = new byte[passwordLength[0]];
        if (!await ReadExactlyAsync(stream, password, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var expectedUsername = Encoding.UTF8.GetBytes(credentials.Username);
        var expectedPassword = Encoding.UTF8.GetBytes(credentials.Password);
        var authenticated = FixedTimeEquals(username, expectedUsername)
            & FixedTimeEquals(password, expectedPassword);
        CryptographicOperations.ZeroMemory(password);
        CryptographicOperations.ZeroMemory(expectedPassword);
        await stream.WriteAsync(
                authenticated ? (byte[])[1, 0] : [1, 1],
                cancellationToken)
            .ConfigureAwait(false);
        return authenticated;
    }

    private static async ValueTask<Request?> ReadHttpProxyAsync(
        Stream stream,
        byte first,
        WorkspaceNetworkProxyCredentials credentials,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512) { first };
        while (bytes.Count < MaximumHttpHeaderBytes)
        {
            var next = new byte[1];
            if (!await ReadExactlyAsync(stream, next, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            bytes.Add(next[0]);
            if (bytes.Count >= HttpHeaderTerminator.Length
                && CollectionsMarshal.AsSpan(bytes)[
                    (bytes.Count - HttpHeaderTerminator.Length)..]
                    .SequenceEqual(HttpHeaderTerminator))
            {
                break;
            }
        }

        if (bytes.Count >= MaximumHttpHeaderBytes)
        {
            await WriteHttpFailureAsync(stream, 431, "Request Header Fields Too Large", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var lines = Encoding.Latin1.GetString(CollectionsMarshal.AsSpan(bytes))
            .Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            await WriteHttpFailureAsync(stream, 400, "Bad Request", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var authorization = lines
            .Skip(1)
            .Select(line => line.Split(':', 2))
            .FirstOrDefault(parts => parts.Length == 2
                && string.Equals(
                    parts[0].Trim(),
                    "Proxy-Authorization",
                    StringComparison.OrdinalIgnoreCase));
        var supplied = authorization is null ? string.Empty : authorization[1].Trim();
        var expected = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
        if (!FixedTimeEquals(
                Encoding.ASCII.GetBytes(supplied),
                Encoding.ASCII.GetBytes(expected)))
        {
            await stream.WriteAsync(
                    "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"GhostSHELL workspace\"\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (string.Equals(requestLine[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseAuthority(requestLine[1], out var connectHost, out var connectPort))
            {
                await WriteHttpFailureAsync(stream, 400, "Bad Request", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            return new Request(
                Protocol.HttpConnect,
                connectHost,
                connectPort,
                InitialPayload: null,
                AcknowledgeConnection: true);
        }

        if (!Uri.TryCreate(requestLine[1], UriKind.Absolute, out var target)
            || !string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(target.Host)
            || target.Port is < 1 or > 65_535)
        {
            await WriteHttpFailureAsync(stream, 400, "Bad Request", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var forwarded = new StringBuilder()
            .Append(requestLine[0])
            .Append(' ')
            .Append(string.IsNullOrEmpty(target.PathAndQuery) ? "/" : target.PathAndQuery)
            .Append(' ')
            .Append(requestLine[2])
            .Append("\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var name = separator < 0 ? line : line[..separator];
            if (string.Equals(name.Trim(), "Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Trim(), "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Trim(), "Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _ = forwarded.Append(line).Append("\r\n");
        }

        // The broker rewrites one HTTP request before switching to a raw byte relay.
        // Closing the origin connection prevents a later absolute-form proxy request
        // (and its Proxy-Authorization header) from being forwarded through that relay.
        _ = forwarded.Append("Connection: close\r\n\r\n");
        return new Request(
            Protocol.HttpForward,
            target.Host,
            checked((ushort)target.Port),
            Encoding.Latin1.GetBytes(forwarded.ToString()),
            AcknowledgeConnection: false);
    }

    private static bool TryParseAuthority(string authority, out string host, out ushort port)
    {
        host = string.Empty;
        port = 0;
        if (authority.Contains('/', StringComparison.Ordinal)
            || !Uri.TryCreate($"http://{authority}", UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is < 1 or > 65_535)
        {
            return false;
        }

        host = uri.Host;
        port = (ushort)uri.Port;
        return true;
    }

    private static async ValueTask<string?> ReadSocksHostAsync(
        Stream stream,
        byte addressType,
        CancellationToken cancellationToken)
    {
        var length = addressType switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
        if (length <= 0)
        {
            return null;
        }

        var bytes = new byte[length];
        if (!await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return addressType == 3
            ? Encoding.ASCII.GetString(bytes)
            : new IPAddress(bytes).ToString();
    }

    private static async ValueTask<int> ReadByteAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var value = new byte[1];
        return await ReadExactlyAsync(stream, value, cancellationToken).ConfigureAwait(false)
            ? value[0]
            : -1;
    }

    private static async ValueTask WriteHttpFailureAsync(
        Stream stream,
        int status,
        string reason,
        CancellationToken cancellationToken) =>
        await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"),
                cancellationToken)
            .ConfigureAwait(false);

    private static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    internal enum Protocol
    {
        Socks5,
        HttpConnect,
        HttpForward,
    }

    internal readonly record struct Request(
        Protocol Protocol,
        string Host,
        ushort Port,
        byte[]? InitialPayload = null,
        bool AcknowledgeConnection = true);
}
