using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS;
using Microsoft.SqlServer.TDS.EndPoint;
using Microsoft.SqlServer.TDS.EnvChange;
using Microsoft.SqlServer.TDS.Servers;
using static GhostShell.Architecture.Tests.SqlClientLoopbackServer;

namespace GhostShell.Architecture.Tests;

// Independent routing regressions. All target names are synthetic; only the
// captured route callback can translate them into disposable loopback fixtures.
[Collection(SqlClientRouteCollection.Name)]
public sealed class SqlClientRecoveryRouteTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RetryAsync(int retries)
    {
        using var behavior = new TransientFaultTDSServer(new TransientFaultTDSServerArguments
        {
            IsEnabledTransientError = true,
            Number = 40613,
            Message = "Synthetic retry fixture",
        });
        // The pinned fixture resets its static first-request counter in Dispose.
        using var server = new SqlClientLoopbackServer(behavior);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var routeLife = new CancellationTokenSource();
        using var dns = new SqlClientDnsObserver();
        var calls = new ConcurrentQueue<(string, int)>();
        var transport = Transport(routeLife.Token, calls, (host, port) =>
            host == "retry.synthetic.invalid" && port == 1433 ? server.Port : throw new InvalidOperationException("Unexpected retry endpoint."));
        var builder = Options("retry.synthetic.invalid");
        builder.ConnectRetryCount = retries;
        using var connection = new SqlConnection(builder.ConnectionString) { TcpTransport = transport };
        if (retries == 0)
        {
            try { await connection.OpenAsync(deadline.Token); throw new InvalidOperationException("Disabled retry opened."); }
            catch (SqlException failure) when (failure.Number == 40613) { }
            Check(calls.Count == 1 && connection.State == ConnectionState.Closed, "disabled retry keeps one routed attempt");
        }
        else
        {
            await connection.OpenAsync(deadline.Token);
            Check(connection.State == ConnectionState.Open && calls.Count == 2, "transient login retries through captured route");
        }
        Check(calls.All(call => call == ("retry.synthetic.invalid", 1433)), "retry preserves logical endpoint");
        Check(dns.Queries == 0, "retry does not use host DNS");
        routeLife.Cancel();
    }

    [Fact]
    public async Task CancelAfterLoginAsync()
    {
        using var routeLife = new CancellationTokenSource();
        using var behavior = new CancelingLoginServer(routeLife);
        using var server = new SqlClientLoopbackServer(behavior);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var dns = new SqlClientDnsObserver();
        var calls = new ConcurrentQueue<(string, int)>();
        var transport = Transport(routeLife.Token, calls, (host, port) =>
            host == "cancel-retry.synthetic.invalid" && port == 1433 ? server.Port : throw new InvalidOperationException("Unexpected canceled endpoint."));
        using var connection = new SqlConnection(Options("cancel-retry.synthetic.invalid").ConnectionString) { TcpTransport = transport };
        try { await connection.OpenAsync(deadline.Token); throw new InvalidOperationException("Revoked login opened."); }
        catch (OperationCanceledException) when (routeLife.IsCancellationRequested) { }
        catch (SqlException) when (routeLife.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (routeLife.IsCancellationRequested) { }
        Check(!deadline.IsCancellationRequested && calls.Count == 1 && connection.State != ConnectionState.Open,
            "revocation during initial login prevents retry dial");
        Check(dns.Queries == 0, "canceled retry does not use host DNS");
    }

    [Fact]
    public async Task FailoverAsync()
    {
        using var server = new SqlClientLoopbackServer(new PartnerServer());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var routeLife = new CancellationTokenSource();
        using var dns = new SqlClientDnsObserver();
        var calls = new ConcurrentQueue<(string, int)>();
        var transport = Transport(routeLife.Token, calls, (host, port) => host switch
        {
            "primary.synthetic.invalid" when port == 1433 => throw new SocketException((int)SocketError.ConnectionRefused),
            "partner.synthetic.invalid" when port == 1433 => server.Port,
            _ => throw new InvalidOperationException("Unexpected failover endpoint."),
        });
        var builder = Options("primary.synthetic.invalid");
        builder.FailoverPartner = "partner.synthetic.invalid,1433";
        builder.InitialCatalog = "fixture";
        using var connection = new SqlConnection(builder.ConnectionString) { TcpTransport = transport };
        await connection.OpenAsync(deadline.Token);
        Check(connection.State == ConnectionState.Open, "failover partner opens through captured route");
        Check(calls.SequenceEqual([("primary.synthetic.invalid", 1433), ("partner.synthetic.invalid", 1433)]),
            "primary and partner both use the same routed connector");
        Check(dns.Queries == 0, "failover does not use host DNS");
        connection.Close();
        routeLife.Cancel();
        var previousCalls = calls.Count;
        try { await connection.OpenAsync(deadline.Token); throw new InvalidOperationException("Revoked partner route reopened."); }
        catch (OperationCanceledException) when (routeLife.IsCancellationRequested) { }
        Check(calls.Count == previousCalls, "canceled failover route cannot borrow or reopen");
    }

    [Fact]
    public async Task RecoverIdleSessionAsync()
    {
        using var original = new SqlClientLoopbackServer(new GenericTDSServer());
        using var replacement = new SqlClientLoopbackServer(new GenericTDSServer());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var routeLife = new CancellationTokenSource();
        using var dns = new SqlClientDnsObserver();
        var calls = new ConcurrentQueue<(string, int)>();
        var targetPort = original.Port;
        var transport = Transport(routeLife.Token, calls, (host, port) =>
            host == "recover.synthetic.invalid" && port == 1433 ? Volatile.Read(ref targetPort) : throw new InvalidOperationException("Unexpected recovery endpoint."));
        using var connection = new SqlConnection(Options("recover.synthetic.invalid").ConnectionString) { TcpTransport = transport };
        await connection.OpenAsync(deadline.Token);
        using (var first = connection.CreateCommand())
        {
            first.CommandText = "SELECT 1";
            Check(Assert.IsType<int>(await first.ExecuteScalarAsync(deadline.Token)) == 1, "initial recoverable session executes");
        }
        Volatile.Write(ref targetPort, replacement.Port);
        original.Dispose(); // Existing endpoint Stop closes and joins its owned connections.
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1";
        Check(Assert.IsType<int>(await query.ExecuteScalarAsync(deadline.Token)) == 1,
            "idle session recovers without an explicit reopen");
        Check(calls.Count == 2 && calls.All(call => call == ("recover.synthetic.invalid", 1433)),
            "session recovery reuses captured route and logical endpoint");
        Check(dns.Queries == 0, "session recovery does not use host DNS");
        routeLife.Cancel();
        var previousCalls = calls.Count;
        try { await query.ExecuteScalarAsync(deadline.Token); throw new InvalidOperationException("Revoked recovered session executed."); }
        catch (OperationCanceledException) when (routeLife.IsCancellationRequested) { }
        catch (SqlException) when (routeLife.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (routeLife.IsCancellationRequested) { }
        Check(calls.Count == previousCalls, "canceled recovered session cannot reconnect");
    }

    private sealed class CancelingLoginServer(CancellationTokenSource lifetime)
        : TransientFaultTDSServer(new TransientFaultTDSServerArguments { IsEnabledTransientError = true, Number = 40613 })
    {
        public override TDSMessageCollection OnLogin7Request(ITDSServerSession session, TDSMessage request)
        {
            var response = base.OnLogin7Request(session, request);
            lifetime.Cancel();
            return response;
        }
    }

    private sealed class PartnerServer : GenericTDSServer
    {
        public override TDSMessageCollection OnLogin7Request(ITDSServerSession session, TDSMessage request)
        {
            var messages = base.OnLogin7Request(session, request);
            var login = messages[^1];
            login.Insert(login.Count - 1, new TDSEnvChangeToken(TDSEnvChangeTokenType.RealTimeLogShipping, "primary.synthetic.invalid"));
            return messages;
        }
    }

    private static void Check(bool condition, string message) => Assert.True(condition, message);
}
