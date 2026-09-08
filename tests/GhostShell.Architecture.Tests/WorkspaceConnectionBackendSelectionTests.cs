using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Infrastructure;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceConnectionBackendSelectionTests
{
    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("duckdb", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    public async Task File_database_with_explicit_ssh_hop_rejects_before_any_worker_launch(string driver, bool isolated)
    {
        using var host = HostWorker();
        await using var session = Factory(host).Create(new WorkspaceInstanceId("fixture"),
            new Connector(WorkspaceNetworkEgress.Direct), null!, isolated ? new RejectingCommands() : null);
        var hop = new ConnectionProfile(new("ssh-fixture"), 1, "fixture",
            new ConnectionEndpoint.Ssh("must-not-contact.invalid", username: "fixture"),
            new ConnectionAuthentication.SshAgent(), ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.SelectDatabaseAsync(
            new(driver, "Fixture", "", IsFileBased: true), hop, CancellationToken.None).AsTask());

        Assert.Contains("not the remote SSH filesystem", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Nonisolated_direct_file_databases_keep_host_file_identity(string driver)
    {
        using var host = HostWorker();
        var factory = Factory(host);
        await using var session = factory.Create(new WorkspaceInstanceId("fixture"), new Connector(WorkspaceNetworkEgress.Direct), null!, null);
        var selected = await session.SelectDatabaseAsync(new(driver, "Fixture", "", IsFileBased: true), null, CancellationToken.None);
        if (string.Equals(driver, "sqlite", StringComparison.Ordinal)) { Assert.Same(host, selected); }
        else { Assert.NotSame(host, selected); Assert.IsType<DatabaseOperationWorker>(selected); }
    }

    [Fact]
    public async Task Nonisolated_custom_route_does_not_run_network_capable_DuckDB_on_host()
    {
        using var host = HostWorker();
        await using var session = Factory(host).Create(new WorkspaceInstanceId("fixture"),
            new Connector(WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:1"))), null!, null);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => session.SelectDatabaseAsync(
            new("duckdb", "DuckDB", "", IsFileBased: true), null, CancellationToken.None).AsTask());

        Assert.Contains("isolated workspace", error.Message, StringComparison.Ordinal);
        Assert.Same(host, await session.SelectDatabaseAsync(new("sqlite", "SQLite", "", IsFileBased: true), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    [InlineData("sqlserver", false)]
    public async Task Isolated_local_operations_select_workspace_worker_not_host(string driver, bool file)
    {
        using var host = HostWorker();
        await using var session = Factory(host).Create(new WorkspaceInstanceId("fixture"),
            new Connector(WorkspaceNetworkEgress.Attached), null!, new RejectingCommands());

        var selected = await session.SelectDatabaseAsync(new(driver, "Fixture", "", IsFileBased: file), BuiltInConnections.Local, CancellationToken.None);

        Assert.IsType<DatabaseOperationWorker>(selected);
        Assert.NotSame(host, selected);
    }

    [Fact]
    public async Task Server_database_never_falls_back_to_host_when_service_runtime_missing()
    {
        using var host = HostWorker();
        await using var session = Factory(host).Create(new WorkspaceInstanceId("fixture"),
            new Connector(WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:1"))), null!, null);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => session.SelectDatabaseAsync(
            new("sqlserver", "SQL Server", "", DefaultPort: 1433), null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Direct_server_database_uses_owned_host_worker_without_requesting_a_vm()
    {
        using var host = HostWorker();
        var factory = new WorkspaceConnectionBackendFactory(
            () => throw new InvalidOperationException("Direct selection must not request a service VM."), null!, null!, null!,
            () => throw new InvalidOperationException("Selection must not create result storage."), host,
            new SelfReentryLaunch("fixture-host", [], "fixture-host"));
        await using var session = factory.Create(new WorkspaceInstanceId("fixture"), new Connector(WorkspaceNetworkEgress.Direct), null!, null);
        var selected = await session.SelectDatabaseAsync(new("sqlserver", "SQL Server", ""), null, CancellationToken.None);
        Assert.IsType<DatabaseOperationWorker>(selected);
        Assert.NotSame(host, selected);
        var launch = await session.PlanAsync("http", null, CancellationToken.None);
        try
        {
            Assert.Equal("fixture-host", launch.StartInfo.FileName);
            Assert.Equal(ConnectionBackendCommand.Marker, launch.StartInfo.ArgumentList[0]);
            Assert.Equal("http", launch.StartInfo.ArgumentList[1]);
            Assert.True(Guid.TryParseExact(launch.StartInfo.ArgumentList[2], "N", out _));
        }
        finally { await launch.CleanupAsync(); }
    }

    [Fact]
    public async Task Captured_direct_executor_is_revoked_when_workspace_changes_to_custom_route()
    {
        using var host = HostWorker();
        using var connector = new ChangingConnector();
        await using var session = Factory(host).Create(new WorkspaceInstanceId("fixture"), connector, null!, null);
        var selected = await session.SelectDatabaseAsync(new("sqlserver", "SQL Server", ""), null, CancellationToken.None);
        var launch = await session.PlanAsync("files", null, CancellationToken.None);
        connector.SwitchToCustom();
        Assert.True(launch.Lifetime.IsCancellationRequested);
        await launch.CleanupAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.ListTablesAsync(
            new("sqlserver", "Server=must-not-contact.invalid;Database=app"), CancellationToken.None));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => session.PlanAsync("http", null, CancellationToken.None));
    }

    private static DatabaseOperationWorker HostWorker() => new(() => throw new InvalidOperationException("No operation should execute during selection."));

    private static WorkspaceConnectionBackendFactory Factory(DatabaseOperationWorker host) =>
        new(() => null, null!, null!, null!, () => throw new InvalidOperationException("No result store should be created."), host,
            new SelfReentryLaunch("fixture-host", [], "fixture-host"));

    private sealed class ChangingConnector : IWorkspaceNetworkConnector, IDisposable
    {
        private readonly CancellationTokenSource _direct = new();
        private bool _custom;
        public WorkspaceNetworkEgress Egress => _custom ? WorkspaceNetworkEgress.ViaProxy(LocalProxyEndpoint) : WorkspaceNetworkEgress.Direct;
        public CancellationToken RouteLifetime => _custom ? CancellationToken.None : _direct.Token;
        public Uri LocalProxyEndpoint => new("socks5://127.0.0.1:1");
        public IWorkspaceNetworkConnector CaptureRoute() => new Snapshot(Egress, RouteLifetime);
        internal void SwitchToCustom() { _direct.Cancel(); _custom = true; }
        public void Dispose() => _direct.Dispose();
        public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) => throw new InvalidOperationException("Selection must not dial.");

        private sealed class Snapshot(WorkspaceNetworkEgress egress, CancellationToken lifetime) : IWorkspaceNetworkConnector
        {
            public WorkspaceNetworkEgress Egress => egress;
            public CancellationToken RouteLifetime => lifetime;
            public Uri LocalProxyEndpoint => new("socks5://127.0.0.1:1");
            public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) => throw new InvalidOperationException("Selection must not dial.");
        }
    }

    private sealed class Connector(WorkspaceNetworkEgress egress) : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => egress;
        public Uri LocalProxyEndpoint => new("socks5://127.0.0.1:1");
        public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Selection must not dial a host endpoint.");
    }

    private sealed class RejectingCommands : IConnectionCommandRuntime
    {
        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(ConnectionProfile connection,
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Selection must not launch a worker.");
        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(ConnectionProfile connection,
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Selection must not launch a worker.");
    }
}
