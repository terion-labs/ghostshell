using System.Net;
using System.Net.Sockets;
using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace Asura.Databases.Tests;

public sealed class OracleProxyDriverLimitationTests
{
    [Fact]
    public async Task Pinned_Oracle_driver_loses_unresolved_host_in_its_HTTP_CONNECT_authority()
    {
        // Regression canary for the driver's built-in HTTP proxy API, which
        // cannot preserve an unresolved authority. Production routed Oracle
        // uses the service backend's network boundary, not this proxy API.
        // This canary does not establish service-backend TCPS interoperability.
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var observed = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var count = 0;
            while (count < buffer.Length)
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(count++, 1), lifetime.Token);
                if (count >= 4 && buffer.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8))
                {
                    break;
                }
            }
            await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), lifetime.Token);
            return Encoding.ASCII.GetString(buffer, 0, count).Split("\r\n", StringSplitOptions.None)[0];
        });
        var options = new OracleConnectionStringBuilder
        {
            DataSource = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=asura-oracle-route.invalid)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=app))(SECURITY=(SSL_SERVER_DN_MATCH=YES)))",
            UserID = "fixture",
            Password = "fixture",
            Pooling = false,
            ConnectionTimeout = 3,
        };
        using var connection = new OracleConnection(options.ConnectionString)
        {
            AutoProxy = false,
            HttpsProxy = "127.0.0.1",
            HttpsProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port,
        };

        await Assert.ThrowsAsync<OracleException>(() => connection.OpenAsync(lifetime.Token));
        Assert.Equal("CONNECT :1522 HTTP/1.1", await observed);
    }
}
