using GhostShell.Application;
using GhostShell.Core;
using Microsoft.Data.SqlClient;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseConnectionRouteTests
{
    [Fact]
    public async Task StalledClosedCleanupHonorsCancellationWithoutFreeingSlotOrDisruptingHealthyForward()
    {
        var factory = new Factory { DisposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var route = new DatabaseConnectionRoute(factory, false, Profile());
        for (var i = 0; i < 32; ++i) { await route.GetLocalPortAsync($"db{i}.internal", 1433, CancellationToken.None); }
        factory.CloseFirst();
        using var cancellation = new CancellationTokenSource();
        var waiting = route.GetLocalPortAsync("new.internal", 1433, cancellation.Token);
        await factory.DisposalEntered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(32, factory.Opens);
        await route.GetLocalPortAsync("db1.internal", 1433, CancellationToken.None);
        Assert.Equal(32, factory.Opens);
        factory.DisposalGate.SetResult();
        await route.GetLocalPortAsync("new.internal", 1433, CancellationToken.None);
        Assert.Equal(33, factory.Opens);
    }

    [Fact]
    public async Task FailedClosedCleanupDoesNotReclaimItsBudgetSlot()
    {
        var factory = new Factory { FailDisposal = true };
        await using var route = new DatabaseConnectionRoute(factory, false, Profile());
        for (var i = 0; i < 32; ++i) { await route.GetLocalPortAsync($"db{i}.internal", 1433, CancellationToken.None); }
        factory.CloseFirst();
        await Assert.ThrowsAsync<IOException>(() => route.GetLocalPortAsync("new.internal", 1433, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => route.GetLocalPortAsync("new.internal", 1433, CancellationToken.None));
        Assert.Equal(32, factory.Opens);
        await route.GetLocalPortAsync("db1.internal", 1433, CancellationToken.None);
        Assert.Equal(32, factory.Opens);
    }

    [Fact]
    public async Task DefinitiveClosureIsReleasedBeforeItsBudgetSlotIsReused()
    {
        var factory = new Factory();
        await using var route = new DatabaseConnectionRoute(factory, false, Profile());
        for (var i = 0; i < 32; ++i) { await route.GetLocalPortAsync($"db{i}.internal", 1433, CancellationToken.None); }
        factory.CloseFirst();
        await route.GetLocalPortAsync("replacement.internal", 1433, CancellationToken.None);
        Assert.Equal(33, factory.Opens);
        Assert.Equal(1, factory.LeaseDisposals);
        await route.GetLocalPortAsync("db1.internal", 1433, CancellationToken.None);
        Assert.Equal(33, factory.Opens);
    }

    [Fact]
    public async Task RetainedEndpointBudgetReusesExistingEntriesAndRefusesBeforeOpeningAnother()
    {
        var factory = new Factory();
        var route = new DatabaseConnectionRoute(factory, false, Profile());
        for (var i = 0; i < 32; ++i) { await route.GetLocalPortAsync($"db{i}.internal", 1433, CancellationToken.None); }
        await route.GetLocalPortAsync("db0.internal", 1433, CancellationToken.None);
        var error = await Assert.ThrowsAsync<DatabaseRouteEndpointBudgetException>(() => route.GetLocalPortAsync("excess.internal", 1433, CancellationToken.None));
        Assert.Contains("32 route endpoints", error.Message, StringComparison.Ordinal);
        Assert.Equal(32, factory.Opens);
        await route.DisposeAsync();
        Assert.Equal(32, factory.LeaseDisposals);
    }

    [Fact]
    public async Task DistinctTrustedOriginalTargetsDoNotEvictEachOtherOrShareOneBudget()
    {
        var factory = new Factory();
        var workers = new DiagramWorkers();
        await using var client = new DatabasePanelClient(factory, diagramWorkers: workers);
        List<DatabaseWorkerRoute> routes = [];
        for (var i = 0; i < 33; ++i)
        {
            await client.OpenDatabaseDiagramAsync("postgres", $"Host=db{i}.internal", Profile(), CancellationToken.None);
            routes.Add(workers.Last!.Route!);
        }
        Assert.All(routes, route => Assert.False(route.Lifetime.IsCancellationRequested));
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db0.internal", Profile(), CancellationToken.None);
        Assert.Same(routes[0], workers.Last!.Route);
    }

    [Fact]
    public async Task OnlyDistinctCapturedFactoriesAreDisposedIncludingUnusedRecaptures()
    {
        var factory = new Factory { CaptureDistinct = true };
        var client = new DatabasePanelClient(factory, diagramWorkers: new DiagramWorkers());
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db.internal", Profile(), CancellationToken.None);
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db.internal", Profile(), CancellationToken.None);
        Assert.Equal(0, factory.Captures[0].FactoryDisposals);
        Assert.Equal(1, factory.Captures[1].FactoryDisposals);
        await client.DisposeAsync();
        Assert.All(factory.Captures, captured => Assert.Equal(1, captured.FactoryDisposals));
        Assert.Equal(0, factory.FactoryDisposals);
    }

    [Fact]
    public async Task CachedForwardAndSqlTransportRetainLogicalIdentityAndAreStable()
    {
        var factory = new Factory();
        await using var route = new DatabaseConnectionRoute(factory, false, Profile());
        Assert.Equal(await route.GetLocalPortAsync("db.internal", 1433, CancellationToken.None),
            await route.GetLocalPortAsync("db.internal", 1433, CancellationToken.None));
        Assert.Equal(1, factory.Opens);
        var driver = BuiltInDatabaseDrivers.All.Single(driver => driver.Descriptor.Id is "sqlserver");
        const string target = "Server=tcp:db.internal,1433;Database=app;User ID=fixture;Password=fixture;Encrypt=True;Failover Partner=other.internal,1433";
        using var first = Assert.IsType<SqlConnection>(driver.CreateRoutedConnection(target, "127.0.0.1", 44001, route));
        using var second = Assert.IsType<SqlConnection>(driver.CreateRoutedConnection(target, "127.0.0.1", 44001, route));
        Assert.Equal(target, first.ConnectionString);
        Assert.Same(first.TcpTransport, second.TcpTransport);
        Assert.Same(route.SqlTransport, first.TcpTransport);
        Assert.Throws<NotSupportedException>(() => driver.CreateRoutedConnection(target, "127.0.0.1", 44001));
    }

    [Fact]
    public async Task WorkerLeasesAreIndependentAndUnderlyingDisposalIsIdempotent()
    {
        var factory = new Factory();
        var route = new DatabaseConnectionRoute(factory, false, Profile());
        var first = await route.WorkerCapability.OpenAsync("db.internal", 1433, CancellationToken.None);
        var second = await route.WorkerCapability.OpenAsync("db.internal", 1433, CancellationToken.None);
        Assert.Equal(2, factory.Opens);
        await first.DisposeAsync();
        Assert.Equal(1, factory.LeaseDisposals);
        await route.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(2, factory.LeaseDisposals);
        Assert.Equal(0, factory.FactoryDisposals);
        Assert.True(route.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotEvictHealthyCachedForward()
    {
        var factory = new Factory();
        await using var route = new DatabaseConnectionRoute(factory, false, Profile());
        var original = await route.GetLocalPortAsync("db.internal", 1433, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        // A completed shared opening can already be available when cancellation
        // is observed; either immediate result or cancellation is valid.
        try { await route.GetLocalPortAsync("db.internal", 1433, cancellation.Token); }
        catch (OperationCanceledException) { }
        Assert.Equal(0, factory.LeaseDisposals);
        Assert.Equal(original, await route.GetLocalPortAsync("db.internal", 1433, CancellationToken.None));
        Assert.Equal(1, factory.Opens);
    }

    [Fact]
    public async Task LateCanceledOpenIsDisposedBeforeDrainCompletes()
    {
        var factory = new Factory { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var route = new DatabaseConnectionRoute(factory, true, Profile());
        using var cancellation = new CancellationTokenSource();
        var opening = route.WorkerCapability.OpenAsync("db.internal", 1433, cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        var closing = route.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        factory.Pending.SetResult(factory.NewLease());
        await closing;
        Assert.Equal(1, factory.LeaseDisposals);
        Assert.Equal(1, factory.FactoryDisposals);
    }

    [Fact]
    public async Task ChangedProfileAndGenerationGetNewAuthorityWithoutDisposingSharedFactory()
    {
        using var firstGeneration = new CancellationTokenSource();
        using var secondGeneration = new CancellationTokenSource();
        var factory = new Factory { Generation = firstGeneration.Token };
        var workers = new DiagramWorkers();
        await using var client = new DatabasePanelClient(factory, diagramWorkers: workers);
        var profile = Profile();
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db.internal", profile, CancellationToken.None);
        var first = workers.Last!.Route!;
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db.internal", profile, CancellationToken.None);
        Assert.Same(first, workers.Last!.Route);
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db.internal", Profile("edited"), CancellationToken.None);
        Assert.NotSame(first, workers.Last!.Route);
        Assert.True(first.Lifetime.IsCancellationRequested);
        var edited = workers.Last.Route!;
        factory.Generation = secondGeneration.Token;
        await client.OpenDatabaseDiagramAsync("postgres", "Host=db.internal", Profile("edited"), CancellationToken.None);
        Assert.NotSame(edited, workers.Last!.Route);
        Assert.True(edited.Lifetime.IsCancellationRequested);
        Assert.Equal(0, factory.FactoryDisposals);
    }

    private static ConnectionProfile Profile(string name = "route") => new(new("route"), ConnectionProfile.CurrentSchemaVersion,
        name, new ConnectionEndpoint.Ssh("bastion.test", username: "fixture"), new ConnectionAuthentication.None(),
        ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);

    private sealed class Factory : IDatabaseTunnelFactory, IDisposable
    {
        public int Opens { get; private set; }
        public int LeaseDisposals { get; private set; }
        public int FactoryDisposals { get; private set; }
        public CancellationToken Generation { get; set; }
        public CancellationToken RouteLifetime => Generation;
        public bool CaptureDistinct { get; init; }
        public List<Factory> Captures { get; } = [];
        public IDatabaseTunnelFactory CaptureRoute()
        {
            if (!CaptureDistinct) { return this; }
            var captured = new Factory { Generation = Generation };
            Captures.Add(captured);
            return captured;
        }
        public TaskCompletionSource<IDatabaseTunnelLease>? Pending { get; init; }
        public TaskCompletionSource? DisposalGate { get; init; }
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailDisposal { get; init; }
        private readonly List<Lease> _created = [];
        public ValueTask<IDatabaseTunnelLease> OpenAsync(ConnectionProfile connection, string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            ++Opens;
            return Pending is null ? ValueTask.FromResult(NewLease()) : new(Pending.Task);
        }
        public IDatabaseTunnelLease NewLease()
        {
            var lease = new Lease(this, 44000 + Opens);
            _created.Add(lease);
            return lease;
        }
        public void CloseFirst() => _created[0].Closed = true;
        public void Dispose() => ++FactoryDisposals;
        private sealed class Lease(Factory owner, int port) : IDatabaseTunnelLease
        {
            public int LocalPort => port;
            public bool Closed { get; set; }
            public bool IsClosed => Closed;
            public ValueTask DisposeAsync()
            {
                ++owner.LeaseDisposals;
                owner.DisposalEntered.TrySetResult();
                if (owner.FailDisposal && Closed) { return ValueTask.FromException(new IOException("synthetic cleanup failure")); }
                return owner.DisposalGate is { } gate ? new(gate.Task) : ValueTask.CompletedTask;
            }
        }
    }

    private sealed class DiagramWorkers : IDatabaseDiagramWorkerFactory
    {
        public DatabaseWorkerConnection? Last { get; private set; }
        public Task<IDatabaseDiagramSession> OpenAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken,
            DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display)
        {
            Last = connection;
            return Task.FromResult<IDatabaseDiagramSession>(new Session());
        }
        private sealed class Session : IDatabaseDiagramSession
        {
            public Task<byte[]> RenderViewportAsync(DatabaseDiagramViewport viewport, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
            public Task ExportAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
