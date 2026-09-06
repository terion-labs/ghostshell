using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal sealed class ProxySocksAdapter : IAsyncDisposable
{
    private const int MaximumHttpHeaderLength = 16 * 1024;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly IWorkspaceTcpConnector _connector;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly WorkspaceNetworkPlacement _placement;
    private readonly NetworkConnectionConfiguration.Proxy _proxy;
    private readonly byte[]? _password;
    private readonly Task _acceptLoop;
    private long _connectionSequence;
    private int _disposed;
    private int _authenticationRejected;

    public bool AuthenticationRejected => Volatile.Read(ref _authenticationRejected) != 0;

    public ProxySocksAdapter(
        IWorkspaceTcpConnector connector,
        WorkspaceNetworkPlacement placement,
        NetworkConnectionConfiguration.Proxy proxy,
        byte[]? password)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _password = password;
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int LocalPort { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await IgnoreExpectedFailureAsync(_acceptLoop).ConfigureAwait(false);
        await Task.WhenAll(_connections.Values.Select(IgnoreExpectedFailureAsync))
            .ConfigureAwait(false);
        _lifetime.Dispose();
        if (_password is not null)
        {
            CryptographicOperations.ZeroMemory(_password);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }

            var id = Interlocked.Increment(ref _connectionSequence);
            var task = ServeAsync(client, _lifetime.Token);
            _connections.TryAdd(id, task);
            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    _connections.TryRemove(id, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var downstream = client.GetStream();
            var destination = await AcceptSocksRequestAsync(downstream, cancellationToken)
                .ConfigureAwait(false);
            if (destination is null)
            {
                return;
            }

            Stream? upstream = null;
            var successReplyStarted = false;
            try
            {
                upstream = await _connector.ConnectAsync(
                        _placement,
                        _proxy.Host,
                        _proxy.Port,
                        cancellationToken)
                    .ConfigureAwait(false);
                var routedStream = await ConnectUpstreamAsync(
                        upstream,
                        destination.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    successReplyStarted = true;
                    await WriteSocksReplyAsync(downstream, 0, cancellationToken)
                        .ConfigureAwait(false);
                    await PumpAsync(downstream, routedStream, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (!ReferenceEquals(routedStream, upstream))
                    {
                        await routedStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is
                IOException or SocketException or AuthenticationException)
            {
                if (!successReplyStarted)
                {
                    await TryWriteFailureAsync(downstream, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (upstream is not null)
                {
                    await upstream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask<Stream> ConnectUpstreamAsync(
        Stream upstream,
        Destination destination,
        CancellationToken cancellationToken)
    {
        switch (_proxy.Protocol)
        {
            case NetworkProxyProtocol.Socks5:
                await ConnectSocks5Async(upstream, destination, cancellationToken)
                    .ConfigureAwait(false);
                return upstream;
            case NetworkProxyProtocol.Http:
                await ConnectHttpAsync(upstream, destination, cancellationToken)
                    .ConfigureAwait(false);
                return upstream;
            case NetworkProxyProtocol.Https:
                var tls = new SslStream(upstream, leaveInnerStreamOpen: true);
                try
                {
                    await tls.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions
                            {
                                TargetHost = _proxy.Host,
                                EnabledSslProtocols = SslProtocols.None,
                                CertificateRevocationCheckMode =
                                    System.Security.Cryptography.X509Certificates
                                        .X509RevocationMode.Online,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ConnectHttpAsync(tls, destination, cancellationToken)
                        .ConfigureAwait(false);
                    return tls;
                }
                catch
                {
                    await tls.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(_proxy.Protocol),
                    _proxy.Protocol,
                    null);
        }
    }

    private async ValueTask ConnectSocks5Async(
        Stream upstream,
        Destination destination,
        CancellationToken cancellationToken)
    {
        byte[] greeting = _password is null
            ? [5, 1, 0]
            : [5, 2, 0, 2];
        await upstream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);
        var selection = new byte[2];
        await ReadRequiredAsync(upstream, selection, cancellationToken).ConfigureAwait(false);
        if (selection[0] != 5 || selection[1] == 255)
        {
            throw new IOException("The upstream proxy rejected authentication.");
        }

        if (selection[1] == 2)
        {
            await AuthenticateSocks5Async(upstream, cancellationToken).ConfigureAwait(false);
        }
        else if (selection[1] != 0)
        {
            throw new IOException("The upstream proxy selected an unsupported authentication method.");
        }

        var request = EncodeSocksDestination(destination);
        await upstream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new byte[4];
        await ReadRequiredAsync(upstream, response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 5 || response[1] != 0)
        {
            throw new IOException("The upstream proxy could not reach the destination.");
        }

        await SkipSocksAddressAsync(upstream, response[3], cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask AuthenticateSocks5Async(
        Stream upstream,
        CancellationToken cancellationToken)
    {
        if (_proxy.Username is null || _password is null)
        {
            throw new IOException("The upstream proxy requires credentials.");
        }

        var username = Encoding.UTF8.GetBytes(_proxy.Username);
        if (username.Length is 0 or > 255 || _password.Length is 0 or > 255)
        {
            throw new IOException("The upstream proxy credentials are too long.");
        }

        var request = new byte[3 + username.Length + _password.Length];
        request[0] = 1;
        request[1] = (byte)username.Length;
        username.CopyTo(request, 2);
        request[2 + username.Length] = (byte)_password.Length;
        _password.CopyTo(request, 3 + username.Length);
        try
        {
            await upstream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request);
            CryptographicOperations.ZeroMemory(username);
        }

        var response = new byte[2];
        await ReadRequiredAsync(upstream, response, cancellationToken).ConfigureAwait(false);
        if (response[0] != 1 || response[1] != 0)
        {
            if (response[0] == 1 && response[1] != 0)
            {
                Volatile.Write(ref _authenticationRejected, 1);
            }

            throw new IOException("The upstream proxy rejected the supplied credentials.");
        }
    }

    private async ValueTask ConnectHttpAsync(
        Stream upstream,
        Destination destination,
        CancellationToken cancellationToken)
    {
        var request = BuildHttpConnectRequest(destination);
        try
        {
            await upstream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request);
        }

        var header = await ReadHttpHeaderAsync(upstream, cancellationToken).ConfigureAwait(false);
        var firstLineEnd = header.AsSpan().IndexOf("\r\n"u8);
        var firstLine = firstLineEnd >= 0 ? header.AsSpan(0, firstLineEnd) : header;
        if (_password is not null && firstLine.Length >= 12
            && (firstLine.StartsWith("HTTP/1.1 407"u8) || firstLine.StartsWith("HTTP/1.0 407"u8))
            && (firstLine.Length == 12 || firstLine[12] == (byte)' '))
        {
            Volatile.Write(ref _authenticationRejected, 1);
        }

        if (firstLine.Length < 12
            || !firstLine.StartsWith("HTTP/"u8)
            || firstLine[9] != (byte)'2')
        {
            throw new IOException("The upstream HTTP proxy rejected the connection.");
        }
    }

    private byte[] BuildHttpConnectRequest(Destination destination)
    {
        var authority = destination.Authority;
        var prefix = Encoding.ASCII.GetBytes(
            $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: Keep-Alive\r\n");
        if (_proxy.Username is null || _password is null)
        {
            return [.. prefix, .. "\r\n"u8];
        }

        var username = Encoding.UTF8.GetBytes(_proxy.Username);
        var credentials = new byte[username.Length + 1 + _password.Length];
        username.CopyTo(credentials, 0);
        credentials[username.Length] = (byte)':';
        _password.CopyTo(credentials, username.Length + 1);
        try
        {
            ReadOnlySpan<byte> authorizationPrefix = "Proxy-Authorization: Basic "u8;
            var encodedLength = Base64.GetMaxEncodedToUtf8Length(credentials.Length);
            var request = new byte[
                prefix.Length + authorizationPrefix.Length + encodedLength + 4];
            prefix.CopyTo(request, 0);
            var offset = prefix.Length;
            authorizationPrefix.CopyTo(request.AsSpan(offset));
            offset += authorizationPrefix.Length;
            var status = Base64.EncodeToUtf8(
                credentials,
                request.AsSpan(offset, encodedLength),
                out var consumed,
                out var written);
            if (status != System.Buffers.OperationStatus.Done
                || consumed != credentials.Length
                || written != encodedLength)
            {
                CryptographicOperations.ZeroMemory(request);
                throw new IOException("The proxy credentials could not be encoded.");
            }

            "\r\n\r\n"u8.CopyTo(request.AsSpan(offset + written));
            return request;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(username);
            CryptographicOperations.ZeroMemory(credentials);
        }
    }

    private static async ValueTask<Destination?> AcceptSocksRequestAsync(
        Stream downstream,
        CancellationToken cancellationToken)
    {
        var greeting = new byte[2];
        if (!await ReadExactlyAsync(downstream, greeting, cancellationToken).ConfigureAwait(false)
            || greeting[0] != 5)
        {
            return null;
        }

        var methods = new byte[greeting[1]];
        if (!await ReadExactlyAsync(downstream, methods, cancellationToken).ConfigureAwait(false)
            || !methods.Contains((byte)0))
        {
            await downstream.WriteAsync(new byte[] { 5, 255 }, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        await downstream.WriteAsync(new byte[] { 5, 0 }, cancellationToken)
            .ConfigureAwait(false);
        var request = new byte[4];
        if (!await ReadExactlyAsync(downstream, request, cancellationToken).ConfigureAwait(false)
            || request[0] != 5
            || request[1] != 1)
        {
            return null;
        }

        var host = await ReadSocksHostAsync(downstream, request[3], cancellationToken)
            .ConfigureAwait(false);
        var portBytes = new byte[2];
        if (host is null
            || !await ReadExactlyAsync(downstream, portBytes, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return new Destination(host, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
    }

    private static byte[] EncodeSocksDestination(Destination destination)
    {
        byte addressType;
        byte[] address;
        if (IPAddress.TryParse(destination.Host, out var ipAddress))
        {
            address = ipAddress.GetAddressBytes();
            addressType = ipAddress.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        }
        else
        {
            address = Encoding.ASCII.GetBytes(destination.Host);
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
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(offset + address.Length), destination.Port);
        return request;
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

    private static async ValueTask SkipSocksAddressAsync(
        Stream stream,
        byte addressType,
        CancellationToken cancellationToken)
    {
        var length = addressType switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new IOException("The upstream proxy returned an invalid address."),
        };
        var discard = new byte[length + 2];
        await ReadRequiredAsync(stream, discard, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadHttpHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHttpHeaderLength];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The upstream proxy closed the connection.");
            }

            length += read;
            if (length >= 4 && buffer.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8))
            {
                return buffer[..length];
            }
        }

        throw new IOException("The upstream proxy returned an oversized response header.");
    }

    private static async Task PumpAsync(
        Stream downstream,
        Stream upstream,
        CancellationToken cancellationToken)
    {
        var upload = downstream.CopyToAsync(upstream, cancellationToken);
        var download = upstream.CopyToAsync(downstream, cancellationToken);
        await Task.WhenAny(upload, download).ConfigureAwait(false);
        await downstream.DisposeAsync().ConfigureAwait(false);
        await upstream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(upload, download).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static ValueTask WriteSocksReplyAsync(
        Stream stream,
        byte status,
        CancellationToken cancellationToken) =>
        stream.WriteAsync(new byte[] { 5, status, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken);

    private static async ValueTask TryWriteFailureAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteSocksReplyAsync(stream, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
        }
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

    private static async ValueTask ReadRequiredAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (!await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The proxy closed the connection unexpectedly.");
        }
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

    private static async Task IgnoreExpectedFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or SocketException or ObjectDisposedException
            or OperationCanceledException)
        {
        }
    }

    private readonly record struct Destination(string Host, ushort Port)
    {
        public string Authority => IPAddress.TryParse(Host, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{Host}]:{Port}"
                : $"{Host}:{Port}";
    }
}
