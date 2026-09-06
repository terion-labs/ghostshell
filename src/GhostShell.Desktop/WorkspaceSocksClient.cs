using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;

namespace GhostShell.Desktop;

internal static class WorkspaceSocksClient
{
    public static async ValueTask<Stream> ConnectAsync(
        int proxyPort,
        WorkspaceNetworkProxyCredentials credentials,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(proxyPort, 1);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken)
                .ConfigureAwait(false);
            var stream = new OwnedTcpStream(client);
            await ConnectSocks5Async(stream, credentials, host, port, cancellationToken)
                .ConfigureAwait(false);
            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async ValueTask ConnectSocks5Async(
        Stream stream,
        WorkspaceNetworkProxyCredentials credentials,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, cancellationToken)
            .ConfigureAwait(false);
        var greeting = new byte[2];
        await ReadRequiredAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != 5 || greeting[1] != 2)
        {
            throw new IOException("The workspace SOCKS proxy rejected the connection.");
        }

        var username = Encoding.UTF8.GetBytes(credentials.Username);
        var password = Encoding.UTF8.GetBytes(credentials.Password);
        if (username.Length is 0 or > 255 || password.Length is 0 or > 255)
        {
            throw new IOException("The workspace SOCKS credentials are invalid.");
        }

        var authentication = new byte[3 + username.Length + password.Length];
        authentication[0] = 1;
        authentication[1] = (byte)username.Length;
        username.CopyTo(authentication, 2);
        authentication[2 + username.Length] = (byte)password.Length;
        password.CopyTo(authentication, 3 + username.Length);
        await stream.WriteAsync(authentication, cancellationToken).ConfigureAwait(false);
        Array.Clear(authentication);
        Array.Clear(password);
        var authenticationReply = new byte[2];
        await ReadRequiredAsync(stream, authenticationReply, cancellationToken)
            .ConfigureAwait(false);
        if (authenticationReply[0] != 1 || authenticationReply[1] != 0)
        {
            throw new IOException("The workspace SOCKS proxy rejected its credentials.");
        }

        await stream.WriteAsync(EncodeDestination(host, port), cancellationToken)
            .ConfigureAwait(false);
        var response = new byte[4];
        await ReadRequiredAsync(stream, response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 5 || response[1] != 0)
        {
            throw response[1] == 2
                ? new WorkspaceNetworkBlockedException()
                : new IOException("The workspace SOCKS proxy could not reach the destination.");
        }

        var addressLength = response[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => 0,
        };
        if (addressLength <= 0)
        {
            throw new IOException("The workspace SOCKS proxy returned an invalid response.");
        }

        await ReadRequiredAsync(
                stream,
                new byte[addressLength + 2],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] EncodeDestination(string host, int port)
    {
        byte addressType;
        byte[] address;
        if (IPAddress.TryParse(host, out var parsed))
        {
            address = parsed.GetAddressBytes();
            addressType = parsed.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        }
        else
        {
            address = Encoding.ASCII.GetBytes(host);
            if (address.Length is 0 or > 255)
            {
                throw new IOException("The destination host is too long for SOCKS5.");
            }

            addressType = 3;
        }

        var request = new byte[6 + address.Length + (addressType == 3 ? 1 : 0)];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = addressType;
        var offset = 4;
        if (addressType == 3)
        {
            request[offset++] = (byte)address.Length;
        }

        address.CopyTo(request, offset);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(offset + address.Length), (ushort)port);
        return request;
    }

    private static async ValueTask<int> ReadByteAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await ReadRequiredAsync(stream, value, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static async ValueTask ReadRequiredAsync(
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
                throw new EndOfStreamException("The workspace network route closed unexpectedly.");
            }

            offset += read;
        }
    }

    private sealed class OwnedTcpStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
