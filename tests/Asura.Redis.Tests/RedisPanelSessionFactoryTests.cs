using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace Asura.Redis.Tests;

public sealed class RedisPanelSessionFactoryTests
{
    [Fact]
    public async Task OpenRejectsMultiplexerWithoutConnectedPrimary()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var connectionString =
            $"127.0.0.1:{port},abortConnect=false,connectRetry=0,connectTimeout=100,syncTimeout=100";

        var exception = await Assert.ThrowsAsync<RedisConnectionException>(() =>
            new RedisPanelSessionFactory().OpenAsync(
                connectionString,
                tunnel: null,
                CancellationToken.None));

        Assert.Contains("usable primary", exception.Message, StringComparison.Ordinal);
    }
}
