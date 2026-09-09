using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Asura.Application;

namespace Asura.Architecture.Tests;

// A controlled remote resolver: unknown names fail, never reach host DNS.
internal sealed class DatabaseServiceSocksFixture(Func<string, int, int> resolve) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _accept;
    public ConcurrentQueue<(string Host, int Port)> Calls { get; } = new();
    public WorkspaceNetworkProxyCredentials Credentials { get; } = new("fixture-user", "fixture-password");
    public Uri Start()
    {
        _listener.Start();
        _accept = AcceptAsync();
        return new Uri($"socks5://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
    }

    private async Task AcceptAsync()
    {
        var clients = new List<Task>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                clients.Add(ForwardAsync(await _listener.AcceptTcpClientAsync(_stop.Token), _stop.Token));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }
    }

    private async Task ForwardAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var destination = new TcpClient())
        using (var stop = token.Register(() => { client.Dispose(); destination.Dispose(); }))
        {
            try
            {
                var stream = client.GetStream();
                var hello = await ReadAsync(stream, 2, token);
                Assert.Equal(5, hello[0]);
                _ = await ReadAsync(stream, hello[1], token);
                await stream.WriteAsync(new byte[] { 5, 2 }, token);
                var auth = await ReadAsync(stream, 2, token);
                Assert.Equal(Credentials.Username, Encoding.UTF8.GetString(await ReadAsync(stream, auth[1], token)));
                var length = (await ReadAsync(stream, 1, token))[0];
                Assert.Equal(Credentials.Password, Encoding.UTF8.GetString(await ReadAsync(stream, length, token)));
                await stream.WriteAsync(new byte[] { 1, 0 }, token);
                var request = await ReadAsync(stream, 4, token);
                var host = request[3] switch
                {
                    1 => new IPAddress(await ReadAsync(stream, 4, token)).ToString(),
                    4 => new IPAddress(await ReadAsync(stream, 16, token)).ToString(),
                    3 => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1, token))[0], token)),
                    _ => throw new InvalidDataException("Unknown SOCKS address family."),
                };
                var bytes = await ReadAsync(stream, 2, token);
                var port = (bytes[0] << 8) | bytes[1];
                Calls.Enqueue((host, port));
                var destinationPort = resolve(host, port);
                if (destinationPort == 0)
                {
                    await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
                    return;
                }
                await destination.ConnectAsync(IPAddress.Loopback, destinationPort, token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                await RelayAsync(stream, destination.GetStream(), token);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private static async Task RelayAsync(Stream client, Stream server, CancellationToken token)
    {
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outbound = client.CopyToAsync(server, relay.Token);
        var inbound = server.CopyToAsync(client, relay.Token);
        await Task.WhenAny(outbound, inbound);
        await relay.CancelAsync();
        try { await Task.WhenAll(outbound, inbound); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
    {
        var result = new byte[count];
        await stream.ReadExactlyAsync(result, token);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_accept is not null) { await _accept; }
        _stop.Dispose();
    }
}
