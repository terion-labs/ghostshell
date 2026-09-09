using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceNetworkPacketGatewayIntegrationTests
{
    private static readonly NetworkConnectionId ConnectionId = new("selected-proxy");

    [Fact]
    public async Task Disabled_isolated_policy_still_opens_attached_direct_gateway()
    {
        var gateway = new RecordingGatewayRuntime();
        var runtime = new WorkspaceNetworkRuntime([], packetGatewayRuntime: gateway);

        await using var session = await runtime.OpenAsync(
            Request(NetworkPolicy.Direct, []),
            progress: null,
            CancellationToken.None);

        Assert.Single(gateway.Requests);
        Assert.True(gateway.Requests[0].IsDirect);
        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Attached, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Selected_isolated_policy_is_owned_by_packet_gateway_not_guest_provider()
    {
        var gateway = new RecordingGatewayRuntime();
        var provider = new ThrowingProvider();
        var profile = Profile();
        var runtime = new WorkspaceNetworkRuntime(
            [provider],
            packetGatewayRuntime: gateway);

        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, isEnabled: true, killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Same(profile, Assert.Single(gateway.Requests).Connection);
        Assert.Equal(0, provider.ConnectCount);
        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Attached, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Packet_gateway_failure_blocks_isolated_workspace()
    {
        var gateway = new RecordingGatewayRuntime();
        var runtime = new WorkspaceNetworkRuntime([], packetGatewayRuntime: gateway);
        await using var session = await runtime.OpenAsync(
            Request(NetworkPolicy.Direct, []),
            progress: null,
            CancellationToken.None);

        gateway.Sessions[0].Fail();

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal("test_gateway_failed", session.Snapshot.Error?.StableCode);
    }

    [Fact]
    public async Task Retryable_gateway_failure_reconnects_stored_AnyConnect_with_backoff()
    {
        var gateway = new RecordingGatewayRuntime();
        var delay = new ControlledReconnectDelay();
        var profile = AnyConnectProfile(new SecretRef("stored-password"));
        var runtime = new WorkspaceNetworkRuntime(
            [],
            isolationEgressGuard: null,
            passwordPrompt: null,
            packetGatewayRuntime: gateway,
            reconnectDelay: delay.WaitAsync);
        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy(
                    [ConnectionId],
                    ConnectionId,
                    isEnabled: true,
                    killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);
        var reconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.State == WorkspaceNetworkState.Connected
                && gateway.Requests.Count == 2)
            {
                reconnected.TrySetResult();
            }
        };

        gateway.Sessions[0].Fail();

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Contains(
            "Reconnecting automatically.",
            session.Snapshot.Error?.Message,
            StringComparison.Ordinal);
        await delay.Requested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), delay.LastDelay);

        delay.Resume();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, gateway.Requests.Count);
        Assert.True(gateway.Sessions[0].Disposed);
        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
    }

    [Fact]
    public async Task Dropped_AnyConnect_with_an_unstored_password_waits_for_explicit_reconnect()
    {
        var gateway = new RecordingGatewayRuntime();
        var delay = new ControlledReconnectDelay();
        var prompt = new RecordingPasswordPrompt();
        var profile = AnyConnectProfile(password: null);
        var runtime = new WorkspaceNetworkRuntime(
            [],
            isolationEgressGuard: null,
            passwordPrompt: prompt,
            packetGatewayRuntime: gateway,
            reconnectDelay: delay.WaitAsync);
        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy(
                    [ConnectionId],
                    ConnectionId,
                    isEnabled: true,
                    killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);

        gateway.Sessions[0].Fail();

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Contains(
            "Select the connection again to reconnect.",
            session.Snapshot.Error?.Message,
            StringComparison.Ordinal);
        Assert.False(delay.Requested.Task.IsCompleted);
        Assert.Single(gateway.Requests);
        Assert.Equal(1, prompt.RequestCount);
    }

    [Fact]
    public async Task Policy_change_cancels_a_scheduled_automatic_reconnect()
    {
        var gateway = new RecordingGatewayRuntime();
        var delay = new ControlledReconnectDelay();
        var profile = AnyConnectProfile(new SecretRef("stored-password"));
        var runtime = new WorkspaceNetworkRuntime(
            [],
            isolationEgressGuard: null,
            passwordPrompt: null,
            packetGatewayRuntime: gateway,
            reconnectDelay: delay.WaitAsync);
        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy(
                    [ConnectionId],
                    ConnectionId,
                    isEnabled: true,
                    killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);

        gateway.Sessions[0].Fail();
        await delay.Requested.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disabled = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(
                new NetworkPolicy(
                    [ConnectionId],
                    ConnectionId,
                    isEnabled: false,
                    killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);
        delay.Resume();
        await Task.Yield();

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(disabled);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.True(gateway.Requests[1].IsDirect);
        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
    }

    [Fact]
    public async Task Failed_reconnect_keeps_the_next_retry_visible()
    {
        var gateway = new FailingReconnectGatewayRuntime();
        var secondDelayRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCount = 0;
        Task ReconnectDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            delayCount++;
            if (delayCount == 1)
            {
                return Task.CompletedTask;
            }

            secondDelayRequested.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var profile = AnyConnectProfile(new SecretRef("stored-password"));
        var runtime = new WorkspaceNetworkRuntime(
            [],
            isolationEgressGuard: null,
            passwordPrompt: null,
            packetGatewayRuntime: gateway,
            reconnectDelay: ReconnectDelay);
        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy(
                    [ConnectionId],
                    ConnectionId,
                    isEnabled: true,
                    killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);

        gateway.Session.Fail();
        await secondDelayRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Contains(
            "Reconnecting automatically.",
            session.Snapshot.Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, gateway.RequestCount);
    }

    [Fact]
    public async Task Gateway_failure_while_publishing_reconnected_state_schedules_another_reconnect()
    {
        var gateway = new RecordingGatewayRuntime();
        var runtime = new WorkspaceNetworkRuntime(
            [],
            isolationEgressGuard: null,
            passwordPrompt: null,
            packetGatewayRuntime: gateway,
            reconnectDelay: static (_, _) => Task.CompletedTask);
        await using var session = await runtime.OpenAsync(
            Request(
                new NetworkPolicy([ConnectionId], ConnectionId, isEnabled: true, killSwitchEnabled: true),
                [AnyConnectProfile(new SecretRef("stored-password"))]),
            progress: null,
            CancellationToken.None);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.State != WorkspaceNetworkState.Connected)
            {
                return;
            }

            if (gateway.Sessions.Count == 2)
            {
                gateway.Sessions[1].Fail();
            }
            else if (gateway.Sessions.Count == 3)
            {
                recovered.TrySetResult();
            }
        };

        gateway.Sessions[0].Fail();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(3, gateway.Requests.Count);
        Assert.True(gateway.Sessions[1].Disposed);
        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
    }

    [Fact]
    public async Task Unexpected_reconnect_failure_stops_connecting_and_allows_manual_retry()
    {
        var gateway = new ThrowingReconnectGatewayRuntime();
        var runtime = new WorkspaceNetworkRuntime(
            [],
            isolationEgressGuard: null,
            passwordPrompt: null,
            packetGatewayRuntime: gateway,
            reconnectDelay: static (_, _) => Task.CompletedTask);
        var update = new WorkspaceNetworkPolicyUpdate(
            new NetworkPolicy([ConnectionId], ConnectionId, isEnabled: true, killSwitchEnabled: true),
            [AnyConnectProfile(new SecretRef("stored-password"))]);
        await using var session = await runtime.OpenAsync(
            Request(update.Policy, update.Connections),
            progress: null,
            CancellationToken.None);
        var failed = new TaskCompletionSource<WorkspaceNetworkSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.Error?.StableCode == "workspace_network_reconnect_failed")
            {
                failed.TrySetResult(snapshot);
            }
        };

        gateway.Session.Fail();
        var snapshot = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkspaceNetworkState.Blocked, snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, snapshot.Egress);
        Assert.Equal(ConnectionId, snapshot.SelectedConnectionId);
        Assert.False(snapshot.Error!.Retryable);
        Assert.Contains("Select the connection again", snapshot.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", snapshot.Error.Message, StringComparison.Ordinal);
        Assert.Equal(2, gateway.RequestCount);

        var retried = await session.ApplyAsync(update, progress: null, CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(retried);
        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(3, gateway.RequestCount);
    }

    [Fact]
    public async Task Gateway_failure_during_runtime_handoff_is_not_overwritten_by_connected_state()
    {
        var gateway = new HandoffFailureGatewayRuntime();
        var runtime = new WorkspaceNetworkRuntime([], packetGatewayRuntime: gateway);

        await using var session = await runtime.OpenAsync(
            Request(NetworkPolicy.Direct, []),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal("test_gateway_handoff_failed", session.Snapshot.Error?.StableCode);
        Assert.True(gateway.Session.Disposed);
    }

    [Fact]
    public async Task Policy_change_closes_old_gateway_before_opening_replacement()
    {
        var gateway = new RecordingGatewayRuntime();
        var runtime = new WorkspaceNetworkRuntime([], packetGatewayRuntime: gateway);
        await using var session = await runtime.OpenAsync(
            Request(NetworkPolicy.Direct, []),
            progress: null,
            CancellationToken.None);
        var first = gateway.Sessions[0];
        var profile = Profile();

        _ = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(
                new NetworkPolicy([ConnectionId], ConnectionId, isEnabled: true, killSwitchEnabled: true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.True(first.Disposed);
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Same(profile, gateway.Requests[1].Connection);
    }

    private static WorkspaceNetworkOpenRequest Request(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> profiles) =>
        new(
            new WorkspaceInstanceId("running-workspace"),
            new WorkspaceNetworkPolicyUpdate(policy, profiles),
            WorkspaceNetworkPlacement.Isolated(new WorkspaceIsolationBinding(
                new WorkspaceId("workspace"),
                new WorkspaceIsolationProviderId("apple-container"),
                WorkspaceIsolationCapability.DedicatedNetworkNamespace,
                "asura-workspace",
                [],
                Guid.NewGuid(),
                network: new WorkspaceIsolationNetworkBinding(
                    "asura-workspace-network",
                    "192.168.64.1",
                    "192.168.64.0/24",
                    "/tmp/asura/network.sock",
                    "/run/asura/network.sock",
                    "/opt/asura/bin/workspace-gateway"))));

    private static NetworkConnectionProfile Profile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Selected proxy",
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));

    private static NetworkConnectionProfile AnyConnectProfile(SecretRef? password) => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Selected AnyConnect",
        new NetworkConnectionConfiguration.AnyConnect(
            new Uri("https://vpn.example.test"),
            username: "test-user",
            passwordSecret: password));

    private sealed class ControlledReconnectDelay
    {
        private readonly TaskCompletionSource _resume = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Requested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan LastDelay { get; private set; }

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            LastDelay = delay;
            Requested.TrySetResult();
            return _resume.Task.WaitAsync(cancellationToken);
        }

        public void Resume() => _resume.TrySetResult();
    }

    private sealed class RecordingPasswordPrompt : INetworkPasswordPrompt
    {
        public int RequestCount { get; private set; }

        public ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(
            NetworkPasswordPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return ValueTask.FromResult(
                NetworkConnectionResult<SecretMaterial>.Succeed(
                    SecretMaterial.CopyFrom("password"u8)));
        }
    }

    private sealed class RecordingGatewayRuntime : IWorkspacePacketGatewayRuntime
    {
        public List<WorkspacePacketGatewayOpenRequest> Requests { get; } = [];

        public List<RecordingGatewaySession> Sessions { get; } = [];

        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var session = new RecordingGatewaySession();
            Sessions.Add(session);
            return ValueTask.FromResult(
                NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(session));
        }
    }

    private sealed class HandoffFailureGatewayRuntime : IWorkspacePacketGatewayRuntime
    {
        public HandoffFailureGatewaySession Session { get; } = new();

        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(Session));
    }

    private sealed class FailingReconnectGatewayRuntime : IWorkspacePacketGatewayRuntime
    {
        public RecordingGatewaySession Session { get; } = new();

        public int RequestCount { get; private set; }

        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(
                RequestCount == 1
                    ? NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(Session)
                    : NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.ConnectionFailed,
                            "test_reconnect_failed",
                            "The reconnect attempt failed.",
                            retryable: true)));
        }
    }

    private sealed class ThrowingReconnectGatewayRuntime : IWorkspacePacketGatewayRuntime
    {
        public RecordingGatewaySession Session { get; } = new();

        public int RequestCount { get; private set; }

        public ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
            WorkspacePacketGatewayOpenRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 2)
            {
                throw new IOException("secret-canary");
            }

            return ValueTask.FromResult(NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(
                RequestCount == 1 ? Session : new RecordingGatewaySession()));
        }
    }

    private sealed class HandoffFailureGatewaySession : IWorkspacePacketGatewaySession
    {
        private bool _blocked;

        public bool Disposed { get; private set; }

        public WorkspacePacketGatewaySnapshot Snapshot =>
            _blocked
                ? new WorkspacePacketGatewaySnapshot(
                    WorkspacePacketGatewayState.Blocked,
                    error: new NetworkConnectionError(
                        NetworkConnectionErrorCode.ConnectionFailed,
                        "test_gateway_handoff_failed",
                        "The test gateway failed during handoff.",
                        retryable: true))
                : new WorkspacePacketGatewaySnapshot(
                    WorkspacePacketGatewayState.Ready,
                    new WorkspacePacketRouteCapabilities(
                        WorkspaceIpAddressFamilies.Ipv4,
                        WorkspaceIpProtocolCapabilities.Tcp
                        | WorkspaceIpProtocolCapabilities.Udp,
                        1280));

        public event EventHandler<WorkspacePacketGatewaySnapshot>? Changed
        {
            add => _blocked = true;
            remove { }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingGatewaySession : IWorkspacePacketGatewaySession
    {
        public WorkspacePacketGatewaySnapshot Snapshot { get; private set; } = new(
            WorkspacePacketGatewayState.Ready,
            new WorkspacePacketRouteCapabilities(
                WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
                WorkspaceIpProtocolCapabilities.All,
                1500));

        public bool Disposed { get; private set; }

        public event EventHandler<WorkspacePacketGatewaySnapshot>? Changed;

        public void Fail()
        {
            Snapshot = new WorkspacePacketGatewaySnapshot(
                WorkspacePacketGatewayState.Blocked,
                error: new NetworkConnectionError(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "test_gateway_failed",
                    "The test gateway failed.",
                    retryable: true));
            Changed?.Invoke(this, Snapshot);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Snapshot = new WorkspacePacketGatewaySnapshot(WorkspacePacketGatewayState.Stopped);
            Changed = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingProvider : INetworkConnectionProvider
    {
        public NetworkConnectionKind Kind => NetworkConnectionKind.Proxy;

        public int ConnectCount { get; private set; }

        public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            throw new InvalidOperationException("The workspace runtime must not connect providers in the guest.");
        }
    }
}
