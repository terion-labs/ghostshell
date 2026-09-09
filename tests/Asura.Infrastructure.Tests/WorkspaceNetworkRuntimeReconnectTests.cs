using System.Threading.Channels;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed partial class WorkspaceNetworkRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_drop_reconnects_and_disposes_old_session(bool killSwitch)
    {
        // Host VPN kill switches require isolation; proxy kill switches are app-scoped.
        var provider = new RecordingProvider(killSwitch ? NetworkConnectionKind.Proxy : NetworkConnectionKind.AnyConnect);
        var delay = new ReconnectSteps();
        var profile = killSwitch ? ProxyProfile() : AnyConnectProfile(new SecretRef("stored-password"));
        var runtime = ReconnectingRuntime(provider, delay);
        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, killSwitch), [profile]),
            null,
            CancellationToken.None);
        var connected = ObserveState(session, WorkspaceNetworkState.Connected);

        provider.Session.Publish(NetworkConnectionState.Failed, "VPN stopped.");
        provider.Session.Publish(NetworkConnectionState.Disconnected, "VPN stopped.");
        Assert.Equal(killSwitch ? WorkspaceNetworkState.Blocked : WorkspaceNetworkState.Failed, session.Snapshot.State);
        Assert.Contains("Reconnecting automatically", session.Snapshot.Error?.Message, StringComparison.Ordinal);
        var step = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), step.Delay);
        step.Resume.SetResult();
        await connected.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, provider.ConnectCount);
        Assert.True(provider.Session.IsDisposed);
        Assert.Equal(provider.Sessions[1].Egress, session.Snapshot.Egress);
        Assert.Null(session.Snapshot.Error);
        Assert.All(provider.Passwords, Assert.Null);
        Assert.Same(profile, provider.LastRequest?.Connection);
    }

    [Fact]
    public async Task Host_VPN_drop_does_not_reprompt_for_an_unstored_password()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        var delay = new ReconnectSteps();
        var prompt = new RecordingPasswordPrompt("test-password");
        var runtime = ReconnectingRuntime(provider, delay, prompt);
        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, false), [AnyConnectProfile()]),
            null,
            CancellationToken.None);

        provider.Session.Publish(NetworkConnectionState.Failed, "VPN stopped.");

        Assert.Contains("Select the connection again", session.Snapshot.Error?.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("automatically", session.Snapshot.Error?.Message, StringComparison.Ordinal);
        Assert.Single(prompt.Requests);
        Assert.False(delay.HasPending);
        Assert.Equal(1, provider.ConnectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_host_workspace_cancels_pending_reconnect(bool dispose)
    {
        var provider = new RecordingProvider();
        var delay = new ReconnectSteps();
        var runtime = ReconnectingRuntime(provider, delay);
        var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, true), [ProxyProfile()]),
            null,
            CancellationToken.None);
        var disposed = false;
        try
        {
            provider.Session.Publish(NetworkConnectionState.Failed, "Proxy stopped.");
            var step = await delay.NextAsync();
            if (dispose)
            {
                await session.DisposeAsync();
                disposed = true;
            }
            else
            {
                var result = await session.ApplyAsync(
                    new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, []), null, CancellationToken.None);
                Assert.IsType<NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success>(result);
                Assert.Equal(WorkspaceNetworkState.Direct, session.Snapshot.State);
            }

            Assert.True(step.Cancellation.IsCancellationRequested);
            step.Resume.SetResult();
            Assert.True(provider.Session.IsDisposed);
            Assert.Equal(1, provider.ConnectCount);
            Assert.False(delay.HasPending);
        }
        finally
        {
            if (!disposed)
            {
                await session.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Host_reconnect_backs_off_then_stops_on_authentication_rejection()
    {
        var provider = new RecordingProvider(NetworkConnectionKind.AnyConnect);
        provider.ReconnectErrors.Enqueue(new NetworkConnectionError(
            NetworkConnectionErrorCode.ConnectionFailed, "test_timeout", "Timed out.", retryable: true));
        provider.ReconnectErrors.Enqueue(new NetworkConnectionError(
            NetworkConnectionErrorCode.AuthenticationRejected, "test_rejected", "Password rejected.", retryable: false));
        var delay = new ReconnectSteps();
        var runtime = ReconnectingRuntime(provider, delay);
        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, false),
                [AnyConnectProfile(new SecretRef("stored-password"))]),
            null,
            CancellationToken.None);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.Error?.Code == NetworkConnectionErrorCode.AuthenticationRejected)
            {
                rejected.TrySetResult();
            }
        };

        provider.Session.Publish(NetworkConnectionState.Failed, "VPN stopped.");
        var first = await delay.NextAsync();
        first.Resume.SetResult();
        var second = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), second.Delay);
        Assert.Contains("Reconnecting automatically", session.Snapshot.Error?.Message, StringComparison.Ordinal);
        second.Resume.SetResult();
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.Snapshot.Error?.Retryable);
        Assert.Equal(3, provider.ConnectCount);
        Assert.False(delay.HasPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_replacement_dropping_during_connected_publication_restarts_retry(bool killSwitch)
    {
        var provider = new RecordingProvider();
        var delay = new ReconnectSteps();
        var runtime = ReconnectingRuntime(provider, delay);
        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, killSwitch), [ProxyProfile()]),
            null,
            CancellationToken.None);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.State == WorkspaceNetworkState.Connected && provider.ConnectCount == 2)
            {
                provider.Sessions[1].Publish(NetworkConnectionState.Failed, "Replacement stopped.");
            }
        };

        provider.Session.Publish(NetworkConnectionState.Failed, "Proxy stopped.");
        (await delay.NextAsync()).Resume.SetResult();
        var replacement = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), replacement.Delay);
        var connected = ObserveState(session, WorkspaceNetworkState.Connected);
        replacement.Resume.SetResult();
        await connected.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, provider.ConnectCount);
        Assert.True(provider.Sessions[1].IsDisposed);
        Assert.Equal(WorkspaceNetworkState.Connected, session.Snapshot.State);
    }

    [Fact]
    public async Task Unexpected_host_reconnect_exception_leaves_visible_manual_recovery()
    {
        var provider = new RecordingProvider { ThrowOnReconnect = true };
        var delay = new ReconnectSteps();
        var runtime = ReconnectingRuntime(provider, delay);
        await using var session = await runtime.OpenAsync(
            HostRequest(new NetworkPolicy([ConnectionId], ConnectionId, true, false), [ProxyProfile()]),
            null,
            CancellationToken.None);
        provider.Session.Publish(NetworkConnectionState.Failed, "Proxy stopped.");
        var blocked = ObserveState(session, WorkspaceNetworkState.Blocked);
        (await delay.NextAsync()).Resume.SetResult();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(session.Snapshot.Error?.Retryable);
        Assert.Contains("Select the connection again", session.Snapshot.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(WorkspaceNetworkEgress.Blocked, session.Snapshot.Egress);
        Assert.Equal(2, provider.ConnectCount);
    }

    private static WorkspaceNetworkRuntime ReconnectingRuntime(
        RecordingProvider provider, ReconnectSteps delay, INetworkPasswordPrompt? prompt = null) =>
        new([provider], null, prompt, null, delay.WaitAsync);

    private static Task ObserveState(IWorkspaceNetworkSession session, WorkspaceNetworkState expected)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.State == expected)
            {
                observed.TrySetResult();
            }
        };
        return observed.Task;
    }

    private sealed class ReconnectSteps
    {
        private readonly Channel<(TimeSpan Delay, TaskCompletionSource Resume, CancellationToken Cancellation)> _steps =
            Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource, CancellationToken)>();

        public bool HasPending => _steps.Reader.TryPeek(out _);

        public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _steps.Writer.WriteAsync((delay, resume, cancellationToken), cancellationToken);
            await resume.Task.WaitAsync(cancellationToken);
        }

        public async Task<(TimeSpan Delay, TaskCompletionSource Resume, CancellationToken Cancellation)> NextAsync() =>
            await _steps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
