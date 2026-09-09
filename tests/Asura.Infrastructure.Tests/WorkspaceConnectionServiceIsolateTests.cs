using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceConnectionServiceIsolateTests
{
    [Theory]
    [InlineData("http://127.0.0.1:4321")]
    [InlineData("socks5://example.com:4321")]
    [InlineData("socks5://127.0.0.1:4321/path")]
    [InlineData("socks5://user:secret@127.0.0.1:4321")]
    public void Service_proxy_rejects_nonprivate_or_credential_bearing_endpoints(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new WorkspacePacketGatewayServiceProxy(new Uri(endpoint),
            new WorkspaceNetworkProxyCredentials("route", "password")));

    [Fact]
    public void Service_proxy_checks_encoded_socks_credential_length() =>
        Assert.Throws<ArgumentException>(() => new WorkspacePacketGatewayServiceProxy(new Uri("socks5://127.0.0.1:4321"),
            new WorkspaceNetworkProxyCredentials("route", new string('é', 128))));

    [Fact]
    public async Task Service_has_fresh_mount_free_scope_and_sdk_only_local_commands()
    {
        var provider = new Provider();
        var gateways = new Gateways();
        await using var service = await OpenAsync(provider, gateways);
        Assert.StartsWith("service-", provider.Request!.WorkspaceId.Value, StringComparison.Ordinal);
        Assert.Empty(provider.Request.Mounts);
        Assert.NotNull(gateways.Request!.ServiceProxy);
        Assert.Null(gateways.Request.Connection);
        Assert.False(gateways.Request.IsDirect);

        var result = await service.Commands.PlanDuplexCommandAsync(BuiltInConnections.Local,
            "/bin/sh", ["-c", "printf '%s' '$unchanged'"], CancellationToken.None);
        var launch = Assert.IsType<ConnectionRuntimeResult<TerminalLaunchRequest>.Success>(result).Value;
        Assert.Equal("/app/workspace-runtime", launch.Executable);
        Assert.Equal(ConnectionKind.Local, provider.Exec!.ConnectionKind);
        Assert.Equal(WorkspaceProcessMode.Interactive, provider.Exec.Mode);
        Assert.Empty(provider.Exec.Environment);
        Assert.Null(provider.Exec.HostWorkingDirectory);
        Assert.False(provider.Exec.UsesHostCredentialBroker);
        Assert.Equal("printf '%s' '$unchanged'", provider.Exec.Arguments[1]);
    }

    [Fact]
    public async Task Route_loss_cancels_work_but_retains_control_for_drain_before_disposal()
    {
        var provider = new Provider();
        var gateways = new Gateways();
        using var route = new CancellationTokenSource();
        var service = await OpenAsync(provider, gateways, route.Token);
        await route.CancelAsync();
        Assert.True(service.Lifetime.IsCancellationRequested);
        Assert.Equal(0, provider.Stops);
        Assert.False(gateways.Session.Disposed);
        _ = await service.Commands.PlanDuplexCommandAsync(BuiltInConnections.Local,
            "/bin/sh", ["-c", "cleanup"], CancellationToken.None);
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.True(gateways.Session.Disposed);
        Assert.Equal(1, provider.Stops);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.Commands.PlanCommandAsync(BuiltInConnections.Local, "true", [], CancellationToken.None));
    }

    [Fact]
    public async Task Gateway_exit_invalidates_scope_without_reattaching_a_new_dns_map()
    {
        var provider = new Provider();
        var gateways = new Gateways();
        await using var service = await OpenAsync(provider, gateways);
        gateways.Session.Block();
        Assert.True(service.Lifetime.IsCancellationRequested);
        Assert.Equal(1, gateways.Opens);
        Assert.Equal(0, provider.Stops);
    }

    [Fact]
    public async Task Gateway_failure_releases_prepared_vm()
    {
        var provider = new Provider();
        var gateways = new Gateways { Fail = true };
        await Assert.ThrowsAsync<IOException>(async () => await OpenAsync(provider, gateways));
        Assert.Equal(1, provider.Stops);
    }

    [Fact]
    public async Task Failed_start_and_failed_stop_return_the_exact_cleanup_owner_for_retry()
    {
        var provider = new Provider { FailStop = true };
        var exception = await Assert.ThrowsAsync<WorkspaceConnectionServiceStartException>(async () =>
            await OpenAsync(provider, new Gateways { Fail = true }));
        Assert.Equal(1, provider.Stops);
        Assert.IsType<AggregateException>(exception.InnerException);
        provider.FailStop = false;
        await exception.Cleanup.DisposeAsync();
        Assert.Equal(2, provider.Stops);
        await exception.Cleanup.DisposeAsync();
        Assert.Equal(2, provider.Stops);
    }

    [Fact]
    public async Task Provider_prepare_failure_cleanup_is_not_lost_when_stop_fails()
    {
        var provider = new Provider { FailPrepare = true, FailStop = true };
        var gateways = new Gateways();
        var exception = await Assert.ThrowsAsync<WorkspaceConnectionServiceStartException>(async () =>
            await OpenAsync(provider, gateways));
        Assert.Equal(0, gateways.Opens);
        provider.FailStop = false;
        await exception.Cleanup.DisposeAsync();
        Assert.Equal(2, provider.Stops);
    }

    [Fact]
    public async Task Gateway_ready_race_releases_gateway_and_vm_once()
    {
        var provider = new Provider();
        var gateways = new Gateways();
        gateways.Session.Block();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await OpenAsync(provider, gateways));
        Assert.True(gateways.Session.Disposed);
        Assert.Equal(1, provider.Stops);
    }

    [Fact]
    public async Task Already_cancelled_route_cannot_prepare_vm()
    {
        var provider = new Provider();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await OpenAsync(provider, new Gateways(), new CancellationToken(canceled: true)));
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task Gateway_dispose_failure_still_releases_vm()
    {
        var provider = new Provider();
        var gateways = new Gateways();
        var service = await OpenAsync(provider, gateways);
        gateways.Session.FailDispose = true;
        await Assert.ThrowsAsync<IOException>(async () => await service.DisposeAsync());
        Assert.Equal(1, provider.Stops);
        Assert.True(service.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task Failed_stop_retains_cleanup_authority_for_retry()
    {
        var provider = new Provider { FailStop = true };
        var service = await OpenAsync(provider, new Gateways());
        await Assert.ThrowsAsync<IOException>(async () => await service.DisposeAsync());
        provider.FailStop = false;
        await service.DisposeAsync();
        Assert.Equal(2, provider.Stops);
        await service.DisposeAsync();
        Assert.Equal(2, provider.Stops);
    }

    [Fact]
    public async Task Each_scope_has_a_distinct_isolation_identity()
    {
        var provider = new Provider();
        await using var first = await OpenAsync(provider, new Gateways());
        await using var second = await OpenAsync(provider, new Gateways());
        Assert.NotEqual(first.Binding.WorkspaceId, second.Binding.WorkspaceId);
    }

    private static ValueTask<WorkspaceConnectionServiceIsolate> OpenAsync(Provider provider,
        Gateways gateways, CancellationToken routeLifetime = default) =>
        WorkspaceConnectionServiceIsolate.OpenAsync(provider, gateways, new WorkspaceInstanceId("owner"),
            new WorkspacePacketGatewayServiceProxy(new Uri("socks5://127.0.0.1:4321"),
                new WorkspaceNetworkProxyCredentials("route", "password")), routeLifetime, CancellationToken.None);

    private sealed class Provider : IWorkspaceIsolationProvider
    {
        public WorkspaceIsolationProviderDescriptor Descriptor => WorkspaceSdkIsolationProvider.ProviderDescriptor;
        public WorkspaceIsolationPrepareRequest? Request { get; private set; }
        public WorkspaceIsolationProcessRequest? Exec { get; private set; }
        public int Stops { get; private set; }
        public bool FailStop { get; set; }
        public bool FailPrepare { get; init; }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            var binding = new WorkspaceIsolationBinding(request.WorkspaceId, Descriptor.Id, Descriptor.Capabilities,
                    request.WorkspaceId.Value, [], Guid.NewGuid(), network: new WorkspaceIsolationNetworkBinding(
                        "service", "100.64.0.1", "100.64.0.0/30", hostAttachment:
                        new WorkspaceHostNetworkAttachment("/tmp/service.control", "/tmp/service.packets")));
            return ValueTask.FromResult(FailPrepare
                ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(WorkspaceIsolationErrorCode.PrepareFailed, binding)
                : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
        }

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding, WorkspaceIsolationProcessRequest request)
        {
            Exec = request;
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch("/app/workspace-runtime", ["exec", "--socket", "/tmp/service.control"],
                    new Dictionary<string, string>(StringComparer.Ordinal), null));
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
            WorkspaceIsolationBinding binding, CancellationToken cancellationToken)
        {
            Stops++;
            return ValueTask.FromResult(FailStop
                ? WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(WorkspaceIsolationErrorCode.StopFailed, binding)
                : WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
        }
    }

    private sealed class Gateways : IWorkspacePacketGatewayRuntime
    {
        public WorkspacePacketGatewayOpenRequest? Request { get; private set; }
        public Gateway Session { get; } = new();
        public bool Fail { get; init; }
        public int Opens { get; private set; }

        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request, IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Request = request;
            Opens++;
            return ValueTask.FromResult(Fail
                ? NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(new NetworkConnectionError(
                    NetworkConnectionErrorCode.ConnectionFailed, "fixture", "Route unavailable.", retryable: false))
                : NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(Session));
        }
    }

    private sealed class Gateway : IWorkspacePacketGatewaySession
    {
        public WorkspacePacketGatewaySnapshot Snapshot { get; private set; } = new(WorkspacePacketGatewayState.Ready,
            new WorkspacePacketRouteCapabilities(WorkspaceIpAddressFamilies.Ipv4, WorkspaceIpProtocolCapabilities.Tcp, 1280));
        public event EventHandler<WorkspacePacketGatewaySnapshot>? Changed;
        public bool Disposed { get; private set; }
        public bool FailDispose { get; set; }

        public void Block()
        {
            Snapshot = new(WorkspacePacketGatewayState.Stopped);
            Changed?.Invoke(this, Snapshot);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            if (FailDispose) { throw new IOException("Fixture failure."); }
            return ValueTask.CompletedTask;
        }
    }
}
