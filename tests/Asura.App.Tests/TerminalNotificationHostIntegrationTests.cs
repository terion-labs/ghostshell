using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;
using Asura.SessionHost.Tests;

namespace Asura.App.Tests;

public sealed class TerminalNotificationHostIntegrationTests
{
    [Fact]
    public async Task AcceptedHostedTerminalForwardsNotificationBeforeRendererAttachment()
    {
        var factory = new FakeTerminalSessionFactory();
        await using var host = new InMemorySessionHostClient(
            factory,
            new DesktopLifecyclePolicy(),
            TimeProvider.System);
        var clientId = ClientId.New();
        var panelId = PanelInstanceId.New();
        var connection = LocalConnection();
        var owner = new SessionOwner(
            HostMode.Desktop,
            WindowInstanceId.New(),
            WorkspaceInstanceId.New(),
            TabInstanceId.New(),
            panelId);
        using var panel = new TerminalRuntimePanelViewModel(
            panelId,
            connection.Name,
            new SuccessfulConnectionRuntime(),
            connection,
            owner,
            PanelStartupBehavior.None,
            renderProfile: null,
            host,
            clientId,
            new TerminalStartupCommandDispatcher(
                new SuccessfulAuditStore(),
                TimeProvider.System));

        await panel.Initialization;

        var request = Assert.IsType<EnsureTerminalSessionRequest>(panel.SessionRequest);
        Assert.Equal(0, factory.CreateCount);

        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureTerminalSessionAsync(
                request,
                OperationContext.ForHuman(clientId),
                CancellationToken.None));
        Assert.Equal(SessionLifecycle.Starting, opened.Value.Descriptor.Lifecycle);

        var received = new TaskCompletionSource<PanelNotificationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        panel.NotificationReceived += (_, notification) =>
            received.TrySetResult(notification);
        panel.ObserveSessionSnapshot(opened.Value);

        var engine = factory[request.SessionId];
        await engine.NotificationWatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        engine.PublishNotification(new PanelNotificationEvent(
            1,
            PanelNotificationKind.Notification,
            "Task runner",
            "Work complete",
            DateTimeOffset.UtcNow));

        var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Task runner", notification.Title);
        Assert.Equal("Work complete", notification.Body);
        Assert.Equal(
            PanelNotificationEffects.Visual | PanelNotificationEffects.System,
            notification.Effects);
    }

    private static ConnectionProfile LocalConnection() => new(
        new ConnectionId("terminal-notification-integration"),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local("/bin/zsh"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private sealed class SuccessfulConnectionRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                    new ConnectionOpenPlan(
                        profile.Id,
                        ConnectionKind.Local,
                        new TerminalLaunchRequest(null, "/bin/zsh"),
                        ConnectionAuthenticationMode.None,
                        SshHostKeyPolicy.NotApplicable,
                        ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class SuccessfulAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
    }
}
