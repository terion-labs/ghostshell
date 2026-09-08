using System.Collections.Concurrent;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Databases;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TDS.Servers;

namespace GhostShell.Architecture.Tests;

[Collection(SqlClientRouteCollection.Name)]
public sealed class DatabaseSqlClientCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectedEndpointBudgetPreservesResourceReasonAndDispatchOutcome(bool ownedOperation)
    {
        using var origin = new SqlClientLoopbackServer(new RoutingTDSServer(new RoutingTDSServerArguments
        {
            RoutingTCPHost = "destination.synthetic.invalid",
            RoutingTCPPort = 15433,
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var directory = Directory.CreateTempSubdirectory("ghostshell-composed-budget-");
        var factory = new CapturedFactory(CancellationToken.None, (host, _) =>
            host is "origin.synthetic.invalid" ? origin.Port : throw new DatabaseRouteEndpointBudgetException());
        var options = SqlClientLoopbackServer.Options("origin.synthetic.invalid");
        options.ApplicationIntent = ApplicationIntent.ReadOnly;
        Func<DatabaseValueContentStore> stores = () => new DatabaseResultContentStore(Path.Combine(directory.FullName, "results"));
        using var worker = new DatabaseOperationWorker(stores, Launch());
        await using var client = new DatabasePanelClient(factory, contentStoreFactory: stores, operationExecutor: ownedOperation ? worker : null);
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                using var result = await client.QueryAsync("sqlserver", options.ConnectionString,
                    BuiltInConnections.Local, "SELECT 1", 10, deadline.Token);
            });
            if (ownedOperation) { Assert.IsType<DatabaseMutationOutcomeUnknownException>(error); }
            else { Assert.IsType<DatabaseRouteEndpointBudgetException>(error); }
            Assert.Contains("32", error.Message, StringComparison.Ordinal);
            // SqlClient may retry a denied login callback until its connect
            // deadline. No query reaches the destination and no new forward
            // is accepted; this is not an automatic SQL retry.
            Assert.Single(factory.Calls, call => call.Host is "origin.synthetic.invalid");
            Assert.Contains(factory.Calls, call => call.Host is "destination.synthetic.invalid");
            Assert.All(factory.Calls, call => Assert.True(call.Host is "origin.synthetic.invalid" or "destination.synthetic.invalid"));
            Assert.Equal(ownedOperation ? 1 : 0, factory.Disposals);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientAndOwnedOperationFollowRedirectsOnCapturedAuthority(bool ownedOperation)
    {
        using var destination = new SqlClientLoopbackServer(new GenericTDSServer());
        using var origin = new SqlClientLoopbackServer(new RoutingTDSServer(new RoutingTDSServerArguments
        {
            RoutingTCPHost = "destination.synthetic.invalid",
            RoutingTCPPort = 15433,
        }));
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var directory = Directory.CreateTempSubdirectory("ghostshell-composed-sql-");
        var factory = new CapturedFactory(lifetime.Token, (host, port) => (host, port) switch
        {
            ("origin.synthetic.invalid", 1433) => origin.Port,
            ("destination.synthetic.invalid", 15433) => destination.Port,
            _ => throw new InvalidOperationException("Unexpected logical route endpoint."),
        });
        var profile = new ConnectionProfile(new("fixture-route"), ConnectionProfile.CurrentSchemaVersion,
            "fixture", new ConnectionEndpoint.Ssh("bastion.synthetic.invalid", username: "fixture"),
            new ConnectionAuthentication.None(), ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        var options = SqlClientLoopbackServer.Options("origin.synthetic.invalid");
        options.ApplicationIntent = ApplicationIntent.ReadOnly;
        Func<DatabaseValueContentStore> stores = () => new DatabaseResultContentStore(Path.Combine(directory.FullName, "results"));
        using var worker = new DatabaseOperationWorker(stores, Launch());
        var client = new DatabasePanelClient(factory, contentStoreFactory: stores, operationExecutor: ownedOperation ? worker : null);
        try
        {
            for (var attempt = 0; attempt < 2; ++attempt)
            {
                using var result = await client.QueryAsync("sqlserver", options.ConnectionString, profile, "SELECT 1", 10, deadline.Token);
                Assert.Equal(1L, Convert.ToInt64(Assert.Single(Assert.Single(result.ValueRows)).RawValue, System.Globalization.CultureInfo.InvariantCulture));
            }
            Assert.Equal(ownedOperation ? 4 : 2, factory.Calls.Count);
            Assert.All(factory.Calls, call => Assert.Equal(profile, call.Profile));
            Assert.Contains(factory.Calls, call => call.Host is "origin.synthetic.invalid" && call.Port == 1433);
            Assert.Contains(factory.Calls, call => call.Host is "destination.synthetic.invalid" && call.Port == 15433);
            Assert.Equal(ownedOperation ? 4 : 0, factory.Disposals);
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync(
                "sqlserver", options.ConnectionString, profile, "SELECT 1", 10, deadline.Token));
        }
        finally
        {
            await client.DisposeAsync();
            directory.Delete(recursive: true);
        }
        Assert.Equal(factory.Calls.Count, factory.Disposals);
    }

    private static SelfReentryLaunch Launch()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
        var dotnet = Path.Combine(root!.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return new SelfReentryLaunch(dotnet, [typeof(Program).Assembly.Location], dotnet);
    }

    private sealed class CapturedFactory(CancellationToken lifetime, Func<string, int, int> resolvePort) : IDatabaseTunnelFactory
    {
        public CancellationToken RouteLifetime => lifetime;
        public ConcurrentQueue<(ConnectionProfile Profile, string Host, int Port)> Calls { get; } = new();
        private int _disposals;
        public int Disposals => Volatile.Read(ref _disposals);
        public ValueTask<IDatabaseTunnelLease> OpenAsync(ConnectionProfile connection, string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.ThrowIfCancellationRequested();
            Calls.Enqueue((connection, targetHost, targetPort));
            return ValueTask.FromResult<IDatabaseTunnelLease>(new Lease(this, resolvePort(targetHost, targetPort)));
        }
        private sealed class Lease(CapturedFactory owner, int port) : IDatabaseTunnelLease
        {
            private int _disposed;
            public int LocalPort => port;
            public bool IsClosed => Volatile.Read(ref _disposed) != 0;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) { Interlocked.Increment(ref owner._disposals); }
                return ValueTask.CompletedTask;
            }
        }
    }
}
