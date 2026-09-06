using System.Net;
using System.Net.Sockets;
using ClickHouse.Client.ADO;

namespace GhostShell.Databases;

/// <summary>Changes only the TCP destination; HTTP authority and TLS identity stay logical.</summary>
internal sealed class RoutedClickHouseConnection : ClickHouseConnection
{
    private readonly HttpClient _client;

    public RoutedClickHouseConnection(string connectionString, string host, int port)
        : this(connectionString, CreateClient(host, port))
    {
    }

    private RoutedClickHouseConnection(string connectionString, HttpClient client)
        : base(connectionString, client)
    {
        _client = client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
        }

        base.Dispose(disposing);
    }

    private static HttpClient CreateClient(string host, int port)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
        return new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
    }
}
