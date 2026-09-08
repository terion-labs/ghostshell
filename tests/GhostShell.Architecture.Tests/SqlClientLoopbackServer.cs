using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS.EndPoint;
using Microsoft.SqlServer.TDS.Servers;

namespace GhostShell.Architecture.Tests;

// The pinned server loads a synthetic certificate relative to the working directory.
// Its transient-login counter and our DNS observer are process-global, so all TDS
// tests use this nonparallel collection, including application-composition tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlClientRouteCollection : ICollectionFixture<SqlClientRouteEnvironment>
{
    public const string Name = "SqlClient loopback routes";
}

public sealed class SqlClientRouteEnvironment : IDisposable
{
    private readonly string _originalDirectory = Directory.GetCurrentDirectory();

    public SqlClientRouteEnvironment() => Directory.SetCurrentDirectory(AppContext.BaseDirectory);

    public void Dispose() => Directory.SetCurrentDirectory(_originalDirectory);
}

internal sealed class SqlClientLoopbackServer : IDisposable
{
    private readonly TDSServerEndPoint _endpoint;
    private int _disposed;

    public SqlClientLoopbackServer(GenericTDSServer server)
    {
        _endpoint = new TDSServerEndPoint(server)
        {
            ServerEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        };
        _endpoint.Start();
    }

    public int Port => _endpoint.ServerEndPoint.Port;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _endpoint.Stop();
        }
    }

    public static SqlConnectionStringBuilder Options(string host) => new()
    {
        DataSource = host + ",1433",
        Encrypt = SqlConnectionEncryptOption.Optional,
        ConnectTimeout = 5,
        ConnectRetryCount = 1,
        ConnectRetryInterval = 1,
        Pooling = true,
        ApplicationName = "Loopback route fixture " + Guid.NewGuid().ToString("N"),
    };

    public static SqlConnectionTcpTransport Transport(CancellationToken lifetime,
        ConcurrentQueue<(string, int)> calls, Func<string, int, int> resolvePort) =>
        new(async (host, port, _, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Enqueue((host, port));
            return await ConnectAsync(resolvePort(host, port), token);
        }, lifetime);

    public static async Task<Socket> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class SqlClientDnsObserver : EventListener
{
    private int _queries;
    public int Queries => Volatile.Read(ref _queries);

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (string.Equals(source.Name, "System.Net.NameResolution", StringComparison.Ordinal))
        {
            EnableEvents(source, EventLevel.Verbose);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (string.Equals(eventData.EventName, "ResolutionStart", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _queries);
        }
    }
}
