using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceNetworkRuntimeTests
{
    private static readonly NetworkConnectionId ConnectionId = new("test-proxy");

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public async Task OpenVpn_password_is_prompted_on_each_attempt_unless_stored(bool stored, int expectedPrompts)
    {
        var provider = new RecordingProvider(NetworkConnectionKind.OpenVpn);
        var prompt = new RecordingPasswordPrompt("session-password");
        var runtime = new WorkspaceNetworkRuntime([provider], passwordPrompt: prompt);
        var profile = new NetworkConnectionProfile(
            ConnectionId, NetworkConnectionProfile.CurrentSchemaVersion, "OpenVPN",
            new NetworkConnectionConfiguration.OpenVpn(new SecretRef("profile"), "alice",
                stored ? new SecretRef("password") : null));
        var request = HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, false), [profile]);
        await using var first = await runtime.OpenAsync(request, progress: null, CancellationToken.None);
        await using var second = await runtime.OpenAsync(request, progress: null, CancellationToken.None);
        Assert.Equal(expectedPrompts, prompt.Requests.Count);
        Assert.Equal(2, provider.ConnectCount);
        if (!stored)
        {
            Assert.All(provider.Passwords, password => Assert.Equal("session-password", Encoding.UTF8.GetString(password!)));
            Assert.True(provider.LastRequest?.TransientPassword?.IsDisposed);
        }
    }

    [Fact]
    public async Task Disabled_host_policy_stays_direct_without_starting_a_provider()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);

        await using var session = await runtime.OpenAsync(
            HostRequest(NetworkPolicy.Direct, []),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
        Assert.Equal(0, provider.ConnectCount);
    }

    [Fact]
    public async Task Enabled_host_policy_publishes_the_provider_route()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = ProxyProfile();

        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, true), [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
        Assert.Equal(ConnectionId, session.Snapshot.SelectedConnectionId);
        Assert.Equal(provider.Session.Egress, session.Snapshot.Egress);
        Assert.Equal(BrowserHttpAuthentication.NetworkRouteIdentity(profile), session.Snapshot.AuthenticationRouteIdentity);
    }

    [Fact]
    public async Task Unstored_AnyConnect_password_is_prompted_for_each_new_host_connection()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var prompt = new RecordingPasswordPrompt("session-password");
        var runtime = new WorkspaceNetworkRuntime([provider], passwordPrompt: prompt);
        var profile = AnyConnectProfile();
        var request = HostRequest(
            new NetworkPolicy([ConnectionId], ConnectionId, true, false),
            [profile]);

        await using var first = await runtime.OpenAsync(
            request,
            progress: null,
            CancellationToken.None);
        await using var second = await runtime.OpenAsync(
            request,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, prompt.Requests.Count);
        Assert.All(
            provider.Passwords,
            password => Assert.Equal("session-password", Encoding.UTF8.GetString(password!)));
        Assert.True(provider.LastRequest?.TransientPassword?.IsDisposed);
    }

    [Fact]
    public async Task Stored_AnyConnect_password_does_not_open_the_password_prompt()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var prompt = new RecordingPasswordPrompt("unused");
        var runtime = new WorkspaceNetworkRuntime([provider], passwordPrompt: prompt);
        var profile = AnyConnectProfile(new SecretRef("stored-password"));

        await using var session = await runtime.OpenAsync(
            HostRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Empty(prompt.Requests);
        Assert.Null(provider.Passwords.Single());
    }

    [Fact]
    public async Task Cancelling_the_password_prompt_does_not_start_the_host_provider()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var prompt = new RecordingPasswordPrompt("unused", cancel: true);
        var runtime = new WorkspaceNetworkRuntime([provider], passwordPrompt: prompt);

        await using var session = await runtime.OpenAsync(
            HostRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [AnyConnectProfile()]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(NetworkConnectionErrorCode.Cancelled, session.Snapshot.Error?.Code);
        Assert.Equal(0, provider.ConnectCount);
    }

    [Fact]
    public async Task Missing_host_provider_falls_back_to_direct_without_a_kill_switch()
    {
        var runtime = new WorkspaceNetworkRuntime([]);

        await using var session = await runtime.OpenAsync(
            HostRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [ProxyProfile()]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Direct, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Missing_host_provider_blocks_when_kill_switch_is_enabled()
    {
        var runtime = new WorkspaceNetworkRuntime([]);

        await using var session = await runtime.OpenAsync(
            HostRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [ProxyProfile()]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
    }

    [Fact]
    public async Task Provider_failure_blocks_a_kill_switched_host_workspace()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        await using var session = await runtime.OpenAsync(
            HostRequest(
                new NetworkPolicy([ConnectionId], ConnectionId, true, true),
                [ProxyProfile()]),
            progress: null,
            CancellationToken.None);

        provider.Session.Publish(NetworkConnectionState.Failed, "Proxy stopped.");

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal("Proxy stopped.", session.Snapshot.Error?.Message);
    }

    [Fact]
    public async Task Disabling_an_active_host_policy_disposes_the_provider_session()
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = ProxyProfile();
        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, true), [profile]),
            progress: null,
            CancellationToken.None);

        var result = await session.ApplyAsync(
            new WorkspaceNetworkPolicyUpdate(
                new NetworkPolicy([ConnectionId], ConnectionId, false, true),
                [profile]),
            progress: null,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(result);
        Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
        Assert.True(provider.Session.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Isolated_placement_without_packet_gateway_fails_closed(
        bool useSelectedConnection)
    {
        var provider = new RecordingProvider();
        var runtime = new WorkspaceNetworkRuntime([provider]);
        var profile = ProxyProfile();
        var policy = useSelectedConnection
            ? new NetworkPolicy([ConnectionId], ConnectionId, true, false)
            : NetworkPolicy.Direct;

        await using var session = await runtime.OpenAsync(
            IsolatedRequest(policy, [profile]),
            progress: null,
            CancellationToken.None);

        Assert.Equal(WorkspaceNetworkState.Blocked, session.Snapshot.State);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal(
            "workspace_packet_gateway_unavailable",
            session.Snapshot.Error?.StableCode);
        Assert.Equal(0, provider.ConnectCount);
    }

    private static WorkspaceNetworkOpenRequest HostRequest(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> profiles) => new(
        new WorkspaceInstanceId("running-workspace"),
        new WorkspaceNetworkPolicyUpdate(policy, profiles),
        WorkspaceNetworkPlacement.Host);

    private static WorkspaceNetworkOpenRequest IsolatedRequest(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> profiles) => new(
        new WorkspaceInstanceId("running-workspace"),
        new WorkspaceNetworkPolicyUpdate(policy, profiles),
        WorkspaceNetworkPlacement.Isolated(new WorkspaceIsolationBinding(
            new WorkspaceId("workspace"),
            new WorkspaceIsolationProviderId("test-isolation"),
            WorkspaceIsolationCapability.DedicatedNetworkNamespace,
            "test-isolate",
            [],
            Guid.NewGuid())));

    private static NetworkConnectionProfile ProxyProfile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Test proxy",
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));

    private static NetworkConnectionProfile AnyConnectProfile(
        SecretRef? password = null) => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Test AnyConnect",
        new NetworkConnectionConfiguration.AnyConnect(
            new Uri("https://vpn.example.test"),
            username: "test-user",
            passwordSecret: password));

    private sealed class RecordingProvider(
        NetworkConnectionKind kind = NetworkConnectionKind.Proxy) :
        INetworkConnectionProvider
    {
        public NetworkConnectionKind Kind => kind;

        public List<RecordingSession> Sessions { get; } = [];

        public RecordingSession Session => Sessions[0];

        public int ConnectCount { get; private set; }

        public NetworkConnectionStartRequest? LastRequest { get; private set; }

        public List<byte[]?> Passwords { get; } = [];

        public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            LastRequest = request;
            if (request.TransientPassword is { } transientPassword)
            {
                var bytes = new byte[transientPassword.Length];
                transientPassword.CopyTo(bytes);
                Passwords.Add(bytes);
            }
            else
            {
                Passwords.Add(null);
            }

            var session = new RecordingSession(request.Connection.Id);
            Sessions.Add(session);
            return ValueTask.FromResult(
                NetworkConnectionResult<INetworkConnectionSession>.Succeed(session));
        }
    }

    private sealed class RecordingPasswordPrompt(
        string password,
        bool cancel = false) : INetworkPasswordPrompt
    {
        public List<NetworkPasswordPromptRequest> Requests { get; } = [];

        public ValueTask<NetworkConnectionResult<SecretMaterial>> RequestPasswordAsync(
            NetworkPasswordPromptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (cancel)
            {
                return ValueTask.FromResult(
                    NetworkConnectionResult<SecretMaterial>.Fail(
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.Cancelled,
                            "test_password_cancelled",
                            "The test password prompt was cancelled.",
                            retryable: true)));
            }

            return ValueTask.FromResult(
                NetworkConnectionResult<SecretMaterial>.Succeed(
                    SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(password))));
        }
    }

    private sealed class RecordingSession(NetworkConnectionId connectionId) :
        INetworkConnectionSession
    {
        public bool IsDisposed { get; private set; }

        public NetworkConnectionSnapshot Snapshot { get; private set; } = new(
            connectionId,
            NetworkConnectionState.Connected);

        public WorkspaceNetworkEgress Egress { get; } =
            WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:43123"));

        public event EventHandler<NetworkConnectionSnapshot>? Changed;

        public void Publish(NetworkConnectionState state, string? status)
        {
            Snapshot = new NetworkConnectionSnapshot(connectionId, state, status);
            Changed?.Invoke(this, Snapshot);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Snapshot = new NetworkConnectionSnapshot(
                connectionId,
                NetworkConnectionState.Disconnected);
            Changed = null;
            return ValueTask.CompletedTask;
        }
    }
}
