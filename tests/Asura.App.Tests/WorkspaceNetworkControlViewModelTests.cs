using Asura.App;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class WorkspaceNetworkControlViewModelTests
{
    [Fact]
    public async Task Browser_authentication_reset_failure_is_visible_and_retryable_without_reapplying_route()
    {
        var connector = new FailingBrowserRouteConnector();
        var applied = 0;
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: true, FirstId),
            new FakeWorkspaceNetworkSession(new WorkspaceNetworkSnapshot(WorkspaceNetworkState.Connected,
                WorkspaceNetworkEgress.Attached, FirstId)),
            new ImmediateDispatcher(), _ => applied++, connector);

        connector.Fail();

        Assert.True(viewModel.IsBlocked);
        Assert.Contains("Select the connection again", viewModel.StatusText, StringComparison.Ordinal);
        Assert.True(viewModel.CanToggle);
        Assert.Equal(1, applied);
    }

    private sealed class FailingBrowserRouteConnector : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Blocked;
        public Uri LocalProxyEndpoint => new("socks5://127.0.0.1:41001");
        public event EventHandler? BrowserAuthenticationRouteFailed;
        public void Fail() => BrowserAuthenticationRouteFailed?.Invoke(this, EventArgs.Empty);
        public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());
    }

    private static readonly NetworkConnectionId FirstId = new("first-network");
    private static readonly NetworkConnectionId SecondId = new("second-network");

    [Fact]
    public void Tcp_only_gateway_explains_dns_and_unsupported_udp_in_the_status()
    {
        var snapshot = new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected,
            WorkspaceNetworkEgress.Attached,
            FirstId,
            routeCapabilities: new WorkspacePacketRouteCapabilities(
                WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
                WorkspaceIpProtocolCapabilities.Tcp,
                1280));
        var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: true, FirstId),
            new FakeWorkspaceNetworkSession(snapshot),
            new ImmediateDispatcher());

        Assert.Contains("TCP traffic and DNS", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("does not support other UDP traffic or ping", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Tcp_and_udp_gateway_does_not_claim_ping_or_arbitrary_ip_support()
    {
        var snapshot = new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected,
            WorkspaceNetworkEgress.Attached,
            FirstId,
            routeCapabilities: new WorkspacePacketRouteCapabilities(
                WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
                WorkspaceIpProtocolCapabilities.Tcp | WorkspaceIpProtocolCapabilities.Udp,
                1280));
        var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: true, FirstId),
            new FakeWorkspaceNetworkSession(snapshot),
            new ImmediateDispatcher());

        Assert.Contains("TCP and UDP traffic", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("does not support ping or other IP protocols", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_policy_reports_the_effective_route()
    {
        var policy = Update(enabled: false, killSwitch: false, FirstId);
        var session = new FakeWorkspaceNetworkSession(WorkspaceNetworkSnapshot.Direct);

        var viewModel = new WorkspaceNetworkControlViewModel(
            policy,
            session,
            new ImmediateDispatcher());

        Assert.Equal("Direct", viewModel.CompactStatus);
        Assert.Contains("direct connection", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.IsNetworkingEnabled);
        Assert.False(viewModel.IsBlocked);
        Assert.True(viewModel.Connections[0].IsSelected);
    }

    [Fact]
    public async Task Session_snapshots_are_applied_to_workspace_runtime_egress()
    {
        var applied = new List<WorkspaceNetworkEgress>();
        var session = new FakeWorkspaceNetworkSession(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected,
            WorkspaceNetworkEgress.Attached,
            FirstId));
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: true, FirstId),
            session,
            new ImmediateDispatcher(),
            snapshot => applied.Add(snapshot.Egress));

        session.Publish(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Blocked,
            WorkspaceNetworkEgress.Blocked,
            FirstId));

        Assert.Equal(
            [WorkspaceNetworkEgress.Attached, WorkspaceNetworkEgress.Blocked],
            applied);
    }

    [Fact]
    public async Task Catalog_policy_update_rebuilds_options_and_reapplies_the_session()
    {
        var session = new FakeWorkspaceNetworkSession(WorkspaceNetworkSnapshot.Direct);
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: false, killSwitch: false, FirstId),
            session,
            new ImmediateDispatcher());

        await viewModel.UpdatePolicyAsync(
            Update(enabled: true, killSwitch: true, SecondId),
            CancellationToken.None);

        Assert.Equal(SecondId, session.LastUpdate!.Policy.SelectedConnectionId);
        Assert.True(session.LastUpdate.Policy.KillSwitchEnabled);
        var option = Assert.Single(viewModel.Connections);
        Assert.Equal("First", option.Name);
        Assert.Equal(SecondId, option.Id);
        Assert.True(option.IsSelected);
    }

    [Fact]
    public async Task Selecting_a_connection_enables_it_and_reports_connected_state()
    {
        var session = new FakeWorkspaceNetworkSession(WorkspaceNetworkSnapshot.Direct)
        {
            Apply = update => new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Connected,
                WorkspaceNetworkEgress.Attached,
                update.Policy.SelectedConnectionId),
        };
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: false, killSwitch: false, FirstId, SecondId),
            session,
            new ImmediateDispatcher());

        await viewModel.SelectAsync(SecondId, CancellationToken.None);

        Assert.True(session.LastUpdate!.Policy.IsEnabled);
        Assert.Equal(SecondId, session.LastUpdate.Policy.SelectedConnectionId);
        Assert.Equal("Second · Connected", viewModel.CompactStatus);
        Assert.Contains("Second", viewModel.AutomationLabel, StringComparison.Ordinal);
        Assert.False(viewModel.Connections[0].IsSelected);
        Assert.True(viewModel.Connections[1].IsSelected);
    }

    [Fact]
    public async Task Turning_networking_off_keeps_the_selected_connection()
    {
        var session = new FakeWorkspaceNetworkSession(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected,
            WorkspaceNetworkEgress.Attached,
            FirstId));
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: true, FirstId),
            session,
            new ImmediateDispatcher());

        await viewModel.ToggleAsync(CancellationToken.None);

        Assert.False(session.LastUpdate!.Policy.IsEnabled);
        Assert.Equal(FirstId, session.LastUpdate.Policy.SelectedConnectionId);
        Assert.True(session.LastUpdate.Policy.KillSwitchEnabled);
        Assert.Equal("Direct", viewModel.CompactStatus);
    }

    [Fact]
    public async Task Unchanged_catalog_refresh_preserves_a_manually_enabled_connection()
    {
        var session = new FakeWorkspaceNetworkSession(WorkspaceNetworkSnapshot.Direct);
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: false, killSwitch: false, FirstId, SecondId),
            session,
            new ImmediateDispatcher());

        await viewModel.SelectAsync(SecondId, CancellationToken.None);
        var liveUpdate = session.LastUpdate;
        // Browser/tab autosave publishes fresh catalog objects with unchanged
        // network settings. It must not restore the disabled saved default.
        await viewModel.UpdatePolicyAsync(
            Update(enabled: false, killSwitch: false, FirstId, SecondId),
            CancellationToken.None);

        Assert.Same(liveUpdate, session.LastUpdate);
        Assert.Equal("Second · Connected", viewModel.CompactStatus);
        Assert.True(viewModel.IsNetworkingEnabled);
        Assert.True(viewModel.Connections[1].IsSelected);
    }

    [Fact]
    public async Task Unchanged_catalog_refresh_does_not_reenable_a_manually_disabled_connection()
    {
        var session = new FakeWorkspaceNetworkSession(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected, WorkspaceNetworkEgress.Attached, FirstId));
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: false, FirstId), session, new ImmediateDispatcher());

        await viewModel.ToggleAsync(CancellationToken.None);
        var liveUpdate = session.LastUpdate;
        await viewModel.UpdatePolicyAsync(
            Update(enabled: true, killSwitch: false, FirstId), CancellationToken.None);

        Assert.Same(liveUpdate, session.LastUpdate);
        Assert.Equal("Direct", viewModel.CompactStatus);
        Assert.False(viewModel.IsNetworkingEnabled);
    }

    [Fact]
    public async Task Route_failure_is_announced_as_blocked_traffic()
    {
        var error = new NetworkConnectionError(
            NetworkConnectionErrorCode.ConnectionFailed,
            "test_connection_failed",
            "The test VPN could not connect.",
            retryable: true);
        var session = new FakeWorkspaceNetworkSession(WorkspaceNetworkSnapshot.Direct)
        {
            Apply = update => new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Blocked,
                WorkspaceNetworkEgress.Blocked,
                update.Policy.SelectedConnectionId,
                error),
            ReturnFailure = error,
        };
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: false, killSwitch: true, FirstId),
            session,
            new ImmediateDispatcher());

        await viewModel.ToggleAsync(CancellationToken.None);

        Assert.True(viewModel.IsBlocked);
        Assert.Equal("Traffic blocked", viewModel.CompactStatus);
        Assert.Contains("Workspace traffic is blocked", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains(error.Message, viewModel.AutomationLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_state_changes_update_the_control()
    {
        var session = new FakeWorkspaceNetworkSession(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Connected,
            WorkspaceNetworkEgress.Attached,
            FirstId));
        await using var viewModel = new WorkspaceNetworkControlViewModel(
            Update(enabled: true, killSwitch: false, FirstId),
            session,
            new ImmediateDispatcher());
        var error = new NetworkConnectionError(
            NetworkConnectionErrorCode.ConnectionFailed,
            "connection_lost",
            "The VPN connection was lost.",
            retryable: true);

        session.Publish(new WorkspaceNetworkSnapshot(
            WorkspaceNetworkState.Failed,
            WorkspaceNetworkEgress.Direct,
            FirstId,
            error));

        Assert.True(viewModel.IsFailed);
        Assert.Equal("First · Failed", viewModel.CompactStatus);
        Assert.Equal(error.Message, viewModel.StatusText);
    }

    private static WorkspaceNetworkPolicyUpdate Update(
        bool enabled,
        bool killSwitch,
        params NetworkConnectionId[] connections)
    {
        var profiles = connections.Select((id, index) => new NetworkConnectionProfile(
            id,
            NetworkConnectionProfile.CurrentSchemaVersion,
            index == 0 ? "First" : "Second",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                $"proxy-{index}.example.test",
                1080 + index))).ToArray();
        return new WorkspaceNetworkPolicyUpdate(
            new NetworkPolicy(connections, connections.FirstOrDefault(), enabled, killSwitch),
            profiles);
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspaceNetworkSession(
        WorkspaceNetworkSnapshot snapshot) : IWorkspaceNetworkSession
    {
        public WorkspaceNetworkSnapshot Snapshot { get; private set; } = snapshot;

        public Func<WorkspaceNetworkPolicyUpdate, WorkspaceNetworkSnapshot>? Apply { get; init; }

        public NetworkConnectionError? ReturnFailure { get; init; }

        public WorkspaceNetworkPolicyUpdate? LastUpdate { get; private set; }

        public event EventHandler<WorkspaceNetworkSnapshot>? Changed;

        public ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyAsync(
            WorkspaceNetworkPolicyUpdate update,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUpdate = update;
            Snapshot = Apply?.Invoke(update)
                ?? (update.Policy.IsEnabled
                    ? new WorkspaceNetworkSnapshot(
                        WorkspaceNetworkState.Connected,
                        WorkspaceNetworkEgress.Attached,
                        update.Policy.SelectedConnectionId)
                    : WorkspaceNetworkSnapshot.Direct);
            Changed?.Invoke(this, Snapshot);
            return ValueTask.FromResult(ReturnFailure is null
                ? NetworkConnectionResult<WorkspaceNetworkSnapshot>.Succeed(Snapshot)
                : NetworkConnectionResult<WorkspaceNetworkSnapshot>.Fail(ReturnFailure));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Publish(WorkspaceNetworkSnapshot next)
        {
            Snapshot = next;
            Changed?.Invoke(this, next);
        }
    }
}
