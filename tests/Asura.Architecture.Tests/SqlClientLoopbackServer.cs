using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS.EndPoint;
using Microsoft.SqlServer.TDS.Servers;

namespace Asura.Architecture.Tests;

// The pinned server loads a synthetic certificate relative to the working directory.
// Its transient-login counter is process-global, so the TDS tests use this
// nonparallel collection.
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

}
