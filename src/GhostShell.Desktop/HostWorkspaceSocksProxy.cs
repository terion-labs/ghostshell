using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.App;
using GhostShell.Application;

namespace GhostShell.Desktop;

internal sealed class HostWorkspaceSocksProxy :
    IWorkspaceNetworkEgressSink,
    IWorkspaceNetworkConnector,
    IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly object _egressGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _acceptLoop;
    private WorkspaceNetworkEgress _egress = WorkspaceNetworkEgress.Direct;
    private CancellationTokenSource _routeLifetime = new();
    private long _connectionSequence;
    private int _disposed;

    public HostWorkspaceSocksProxy(string? browserProfileRouteIdentity = null)
    {
        BrowserProfileRouteIdentity = browserProfileRouteIdentity;
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalProxyEndpoint = new Uri(
            $"socks5://127.0.0.1:{LocalPort}",
            UriKind.Absolute);
        BrowserProxyEndpoint = new Uri(
            $"http://127.0.0.1:{LocalPort}",
            UriKind.Absolute);
        _acceptLoop = AcceptLoopAsync();
    }

    public int LocalPort { get; }

    public WorkspaceNetworkEgress Egress => CurrentRoute().Egress;

    public Uri LocalProxyEndpoint { get; }

    public WorkspaceNetworkProxyCredentials LocalProxyCredentials { get; } =
        WorkspaceLoopbackProxyProtocol.CreateCredentials();

    public Uri BrowserProxyEndpoint { get; }

    public string? BrowserProfileRouteIdentity { get; }

    public ValueTask<Stream> ConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken) =>
        WorkspaceSocksClient.ConnectAsync(
            LocalPort,
            LocalProxyCredentials,
            host,
            port,
            cancellationToken);

    public void Apply(WorkspaceNetworkEgress egress)
    {
        ArgumentNullException.ThrowIfNull(egress);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CancellationTokenSource previous;
        lock (_egressGate)
        {
            if (_egress == egress)
            {
                return;
            }

            _egress = egress;
            previous = _routeLifetime;
            _routeLifetime = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        CancellationTokenSource routeLifetime;
        lock (_egressGate)
        {
            routeLifetime = _routeLifetime;
        }
        await routeLifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await IgnoreExpectedFailureAsync(_acceptLoop).ConfigureAwait(false);
        await Task.WhenAll(_connections.Values.Select(IgnoreExpectedFailureAsync))
            .ConfigureAwait(false);
        _lifetime.Dispose();
        routeLifetime.Dispose();
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
        var route = CurrentRoute();
        using (client)
        using (var routeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken,
                   route.CancellationToken))
        {
            cancellationToken = routeCancellation.Token;
            client.NoDelay = true;
            var downstream = client.GetStream();
            var request = await WorkspaceLoopbackProxyProtocol.AuthenticateAndReadAsync(
                    downstream,
                    LocalProxyCredentials,
                    cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var egress = route.Egress;
            if (egress == WorkspaceNetworkEgress.Blocked)
            {
                await WorkspaceLoopbackProxyProtocol.ReplyAsync(
                        downstream,
                        request.Value.Protocol,
                        2,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            using var upstreamClient = new TcpClient { NoDelay = true };
            var successReplyStarted = false;
            try
            {
                var endpoint = egress.ProxyEndpoint;
                await upstreamClient.ConnectAsync(
                        endpoint?.Host ?? request.Value.Host,
                        endpoint?.Port ?? request.Value.Port,
                        cancellationToken)
                    .ConfigureAwait(false);
                var upstream = upstreamClient.GetStream();
                if (endpoint is not null)
                {
                    await ConnectSocksAsync(
                            upstream,
                            new Destination(request.Value.Host, request.Value.Port),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (request.Value.InitialPayload is { } initialPayload)
                {
                    await upstream.WriteAsync(initialPayload, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (request.Value.AcknowledgeConnection)
                {
                    await WorkspaceLoopbackProxyProtocol.ReplyAsync(
                            downstream,
                            request.Value.Protocol,
                            0,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                successReplyStarted = true;
                if (request.Value.Protocol == WorkspaceLoopbackProxyProtocol.Protocol.HttpForward)
                {
                    upstreamClient.Client.Shutdown(SocketShutdown.Send);
                    await upstream.CopyToAsync(downstream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await PumpAsync(downstream, upstream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                if (!successReplyStarted)
                {
                    await TryReplyFailureAsync(
                            downstream,
                            request.Value.Protocol,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private (WorkspaceNetworkEgress Egress, CancellationToken CancellationToken) CurrentRoute()
    {
        lock (_egressGate)
        {
            return (_egress, _routeLifetime.Token);
        }
    }

    private static async ValueTask ConnectSocksAsync(
        Stream stream,
        Destination destination,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken)
            .ConfigureAwait(false);
        var greeting = new byte[2];
        if (!await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false)
            || greeting[0] != 5
            || greeting[1] != 0)
        {
            throw new IOException("The workspace proxy rejected the connection.");
        }

        var request = EncodeDestination(destination);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new byte[4];
        if (!await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false)
            || response[0] != 5
            || response[1] != 0)
        {
            throw new IOException("The workspace proxy could not reach the destination.");
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
            throw new IOException("The workspace proxy returned an invalid response.");
        }

        var remainder = new byte[addressLength + 2];
        if (!await ReadExactlyAsync(stream, remainder, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The workspace proxy closed the connection.");
        }
    }

    private static byte[] EncodeDestination(Destination destination)
    {
        byte addressType;
        byte[] address;
        if (IPAddress.TryParse(destination.Host, out var parsed))
        {
            address = parsed.GetAddressBytes();
            addressType = parsed.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
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

    private static async ValueTask TryReplyFailureAsync(
        Stream stream,
        WorkspaceLoopbackProxyProtocol.Protocol protocol,
        CancellationToken cancellationToken)
    {
        try
        {
            await WorkspaceLoopbackProxyProtocol.ReplyAsync(
                    stream,
                    protocol,
                    1,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private readonly record struct Destination(string Host, ushort Port);
}
