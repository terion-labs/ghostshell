using System.Collections.Concurrent;
using System.Data;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS.Servers;

namespace GhostShell.Architecture.Tests;

[Collection(SqlClientRouteCollection.Name)]
public sealed class SqlClientRouteTests
{
    [Fact]
    public async Task RedirectsAndPoolsRetainRouteIdentityWithoutHostDnsAsync()
    {
        using var destination = new SqlClientLoopbackServer(new GenericTDSServer());
        using var origin = new SqlClientLoopbackServer(new RoutingTDSServer(new RoutingTDSServerArguments
        {
            RoutingTCPHost = "destination.synthetic.invalid",
            RoutingTCPPort = 15433,
        }));
        // Start probes localhost inside the pinned fixture. Observe provider work only.
        using var dns = new SqlClientDnsObserver();
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = new ConcurrentQueue<(string Host, int Port, bool Parallel)>();
        var sockets = new ConcurrentQueue<Socket>();
        async Task<Socket> ConnectAsync(string host, int port, bool parallel,
            SqlConnectionIPAddressPreference preference, CancellationToken cancellationToken)
        {
            calls.Enqueue((host, port, parallel));
            var endpoint = host switch
            {
                "origin.synthetic.invalid" when port == 1433 => origin.Port,
                "destination.synthetic.invalid" when port == 15433 => destination.Port,
                _ => throw new InvalidOperationException("Unexpected logical destination."),
            };
            var socket = await SqlClientLoopbackServer.ConnectAsync(endpoint, cancellationToken);
            sockets.Enqueue(socket);
            return socket;
        }

        var transport = new SqlConnectionTcpTransport(ConnectAsync, lifetime.Token);
        var options = SqlClientLoopbackServer.Options("origin.synthetic.invalid");
        options.ApplicationIntent = ApplicationIntent.ReadOnly;
        options.MultiSubnetFailover = true;
        using (var connection = new SqlConnection(options.ConnectionString) { TcpTransport = transport })
        {
            await connection.OpenAsync(deadline.Token);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal([("origin.synthetic.invalid", 1433, true), ("destination.synthetic.invalid", 15433, true)], calls);
        }
        var countAfterFirst = calls.Count;
        using (var connection = new SqlConnection(options.ConnectionString) { TcpTransport = transport })
        {
            await connection.OpenAsync(deadline.Token);
            Assert.Equal(countAfterFirst, calls.Count);
        }
        using (var connection = new SqlConnection(options.ConnectionString)
        {
            TcpTransport = new SqlConnectionTcpTransport(ConnectAsync, lifetime.Token),
        })
        {
            await connection.OpenAsync(deadline.Token);
            Assert.Equal(countAfterFirst + 2, calls.Count);
        }
        Assert.Equal(0, dns.Queries);
        using var canceledBorrow = new SqlConnection(options.ConnectionString) { TcpTransport = transport };
        lifetime.Cancel();
        Assert.All(sockets, socket => Assert.True(socket.SafeHandle.IsClosed));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledBorrow.OpenAsync(deadline.Token));
        Assert.Equal(countAfterFirst + 2, calls.Count);
    }

    [Theory]
    [InlineData("named.synthetic.invalid\\instance")]
    [InlineData(@"np:\\other.synthetic.invalid\pipe\sql\query")]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    public async Task UnsupportedRoutedTransportsFailBeforeDialAsync(string target)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        var transport = new SqlConnectionTcpTransport((_, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Unsupported transport must not dial.");
        }, deadline.Token);
        var options = SqlClientLoopbackServer.Options(target);
        options.DataSource = target;
        using var connection = new SqlConnection(options.ConnectionString) { TcpTransport = transport };
        await Assert.ThrowsAsync<NotSupportedException>(() => connection.OpenAsync(deadline.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RoutedSpnUsesLogicalNameAndPreservesExplicitOverride()
    {
        using var dns = new SqlClientDnsObserver();
        var provider = typeof(SqlConnection).Assembly;
        var proxy = provider.GetType("Microsoft.Data.SqlClient.SNI.SNIProxy", throwOnError: true)!;
        var source = proxy.GetNestedType("DataSource", BindingFlags.Public | BindingFlags.NonPublic)
            ?? provider.GetType("Microsoft.Data.SqlClient.SNI.DataSource", throwOnError: true)!;
        var parse = source.GetMethod("ParseServerName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var details = parse.Invoke(null, ["tcp:original.synthetic.invalid,1433"]);
        var method = proxy.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => string.Equals(candidate.Name, "GetSqlServerSPNs", StringComparison.Ordinal)
                && candidate.GetParameters().Length == 3);
        var explicitSpn = Assert.IsType<byte[][]>(method.Invoke(null, [details, "MSSQLSvc/explicit.synthetic.invalid:15433", false]));
        var implicitSpn = Assert.IsType<byte[][]>(method.Invoke(null, [details, null, false]));
        Assert.Equal("MSSQLSvc/explicit.synthetic.invalid:15433", Encoding.Unicode.GetString(Assert.Single(explicitSpn)));
        Assert.Equal("MSSQLSvc/original.synthetic.invalid:1433", Encoding.Unicode.GetString(Assert.Single(implicitSpn)));
        Assert.Equal(0, dns.Queries);
    }
}
