using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class HostWorkspacePacketGatewayRuntimeTests
{
    private static readonly NetworkConnectionId ConnectionId = new("packet-gateway-test");

    [Fact]
    public async Task Runtime_rejects_an_isolate_without_a_host_only_network()
    {
        var backend = new RecordingBackend(() => new RecordingBackendSession(FullRoute()));
        var runtime = new HostWorkspacePacketGatewayRuntime(backend);

        var result = await runtime.OpenAsync(
            Request("isolate-a", includeNetwork: false),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.InvalidConfiguration, failure.Error.Code);
        Assert.Equal(
            "workspace_packet_gateway_host_only_network_missing",
            failure.Error.StableCode);
        Assert.Equal(0, backend.OpenCount);
    }

    [Fact]
    public async Task Runtime_rejects_a_backend_without_tcp_as_an_incomplete_guest_route()
    {
        var backendSession = new RecordingBackendSession(
            new WorkspacePacketRouteCapabilities(
                WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
                WorkspaceIpProtocolCapabilities.Udp,
                1500));
        var runtime = new HostWorkspacePacketGatewayRuntime(
            new RecordingBackend(() => backendSession));

        var result = await runtime.OpenAsync(
            Request("isolate-a"),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure>(
            result);
        Assert.Equal("workspace_packet_gateway_route_incomplete", failure.Error.StableCode);
        Assert.True(backendSession.IsDisposed);
    }

    [Fact]
    public async Task Runtime_opens_the_host_direct_route_without_a_connection_profile()
    {
        var backend = new RecordingBackend(() => new RecordingBackendSession(FullRoute()));
        var runtime = new HostWorkspacePacketGatewayRuntime(backend);

        await using var session = Success(await runtime.OpenAsync(
            Request("isolate-a", direct: true),
            progress: null,
            CancellationToken.None));

        Assert.True(backend.LastRequest?.IsDirect);
        Assert.Null(backend.LastRequest?.Connection);
        Assert.Equal(WorkspacePacketGatewayState.Ready, session.Snapshot.State);
    }

    [Fact]
    public async Task Only_one_gateway_can_own_a_persistent_isolate()
    {
        var backend = new RecordingBackend(() => new RecordingBackendSession(FullRoute()));
        var runtime = new HostWorkspacePacketGatewayRuntime(backend);
        await using var first = Success(await runtime.OpenAsync(
            Request("isolate-a"),
            progress: null,
            CancellationToken.None));

        var duplicate = await runtime.OpenAsync(
            Request("isolate-a", "another-window"),
            progress: null,
            CancellationToken.None);
        await using var otherWorkspace = Success(await runtime.OpenAsync(
            Request("isolate-b"),
            progress: null,
            CancellationToken.None));

        var failure = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure>(
            duplicate);
        Assert.Equal("workspace_packet_gateway_already_active", failure.Error.StableCode);
        Assert.Equal(2, backend.OpenCount);
    }

    [Fact]
    public async Task Disposing_a_gateway_releases_the_isolate_for_a_clean_reopen()
    {
        var backend = new RecordingBackend(() => new RecordingBackendSession(FullRoute()));
        var runtime = new HostWorkspacePacketGatewayRuntime(backend);
        var first = Success(await runtime.OpenAsync(
            Request("isolate-a"),
            progress: null,
            CancellationToken.None));

        await first.DisposeAsync();
        await using var reopened = Success(await runtime.OpenAsync(
            Request("isolate-a"),
            progress: null,
            CancellationToken.None));

        Assert.Equal(WorkspacePacketGatewayState.Stopped, first.Snapshot.State);
        Assert.Equal(WorkspacePacketGatewayState.Ready, reopened.Snapshot.State);
        Assert.Equal(2, backend.OpenCount);
    }

    [Fact]
    public async Task Asynchronous_backend_failure_closes_the_route_before_the_next_packet()
    {
        var backendSession = new RecordingBackendSession(FullRoute());
        var runtime = new HostWorkspacePacketGatewayRuntime(
            new RecordingBackend(() => backendSession));
        await using var session = Success(await runtime.OpenAsync(
            Request("isolate-a"),
            progress: null,
            CancellationToken.None));

        backendSession.Fail(new NetworkConnectionError(
            NetworkConnectionErrorCode.ConnectionFailed,
            "test_backend_exited",
            "The test backend exited.",
            retryable: true));
        Assert.Equal(WorkspacePacketGatewayState.Blocked, session.Snapshot.State);
        Assert.Equal("test_backend_exited", session.Snapshot.Error?.StableCode);
    }

    private static IWorkspacePacketGatewaySession Success(
        NetworkConnectionResult<IWorkspacePacketGatewaySession> result) =>
        Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success>(result).Value;

    private static WorkspacePacketGatewayOpenRequest Request(
        string resourceName,
        string workspaceInstanceId = "running-workspace",
        bool includeNetwork = true,
        bool direct = false) =>
        new(
            new WorkspaceInstanceId(workspaceInstanceId),
            new WorkspaceIsolationBinding(
                new WorkspaceId("persistent-workspace"),
                new WorkspaceIsolationProviderId("test-isolation"),
                WorkspaceIsolationCapability.StructuredProcessExecution,
                resourceName,
                [],
                Guid.NewGuid(),
                network: includeNetwork
                    ? new WorkspaceIsolationNetworkBinding(
                        $"{resourceName}-network",
                        "192.168.100.1",
                        "192.168.100.0/24")
                    : null),
            direct
                ? null
                : new NetworkConnectionProfile(
                    ConnectionId,
                    NetworkConnectionProfile.CurrentSchemaVersion,
                    "Test VPN",
                    new NetworkConnectionConfiguration.Tailscale("exit-node")));

    private static WorkspacePacketRouteCapabilities FullRoute() =>
        new(
            WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
            WorkspaceIpProtocolCapabilities.All,
            1500);

    private sealed class RecordingBackend(
        Func<IHostWorkspacePacketGatewayBackendSession> createSession) :
        IHostWorkspacePacketGatewayBackend
    {
        public int OpenCount { get; private set; }

        public WorkspacePacketGatewayOpenRequest? LastRequest { get; private set; }

        public ValueTask<NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>>
            OpenAsync(
                WorkspacePacketGatewayOpenRequest request,
                IProgress<NetworkConnectionProgress>? progress,
                CancellationToken cancellationToken)
        {
            OpenCount++;
            LastRequest = request;
            return ValueTask.FromResult(
                NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Succeed(
                    createSession()));
        }
    }

    private sealed class RecordingBackendSession(
        WorkspacePacketRouteCapabilities capabilities) :
        IHostWorkspacePacketGatewayBackendSession
    {
        public WorkspacePacketRouteCapabilities Capabilities { get; } = capabilities;

        public NetworkConnectionError? Failure { get; private set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler<NetworkConnectionError>? Failed;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Fail(NetworkConnectionError error)
        {
            Failure = error;
            Failed?.Invoke(this, error);
        }
    }
}
