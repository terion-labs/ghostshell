using Asura.App;
using Asura.Application;
using Asura.Core;
using Asura.Desktop;
using Asura.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceSystemMonitorPanelSessionFactoryTests
{
    private static readonly WorkspaceInstanceId WorkspaceId = new("isolated-workspace");
    private static readonly WorkspaceInstanceId OtherWorkspaceId = new("plain-workspace");

    [Fact]
    public async Task RegistrationRoutesOnlyItsWorkspaceAndDisposalRejectsNewSessions()
    {
        using var host = new SystemMonitorPanelSessionFactory(TimeProvider.System);
        using var isolated = new RecordingMonitorFactory();
        var factory = new WorkspaceSystemMonitorPanelSessionFactory(host);

        using var hostRegistration = factory.Register(OtherWorkspaceId, host);
        await using var firstHostSession = await factory.CreateStatisticsAsync(
            OtherWorkspaceId,
            SessionId.New(),
            BuiltInConnections.Local,
            CancellationToken.None);
        using (factory.Register(WorkspaceId, isolated))
        {
            await using var isolatedStatistics = await factory.CreateStatisticsAsync(
                WorkspaceId,
                SessionId.New(),
                BuiltInConnections.Local,
                CancellationToken.None);
            await using var isolatedProcesses = await factory.CreateProcessMonitorAsync(
                WorkspaceId,
                SessionId.New(),
                BuiltInConnections.Local,
                CancellationToken.None);
        }

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateStatisticsAsync(
            WorkspaceId, SessionId.New(), BuiltInConnections.Local, CancellationToken.None));
        Assert.Contains("No host fallback", error.Message, StringComparison.Ordinal);

        Assert.Equal(1, isolated.StatisticsCreateCount);
        Assert.Equal(1, isolated.ProcessMonitorCreateCount);
    }

    [Fact]
    public void DuplicateWorkspaceRegistrationIsRejected()
    {
        using var host = new SystemMonitorPanelSessionFactory(TimeProvider.System);
        var factory = new WorkspaceSystemMonitorPanelSessionFactory(host);
        using var registration = factory.Register(WorkspaceId, new RecordingMonitorFactory());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            factory.Register(WorkspaceId, new RecordingMonitorFactory()));

        Assert.Equal(
            "The workspace already has a system-monitor factory.",
            exception.Message);
    }

    [Fact]
    public async Task DesktopRuntimeRoutesMonitorCommandsThroughTheIsolatedWorkspace()
    {
        await using var services = DesktopComposition.CreateServiceProvider();
        var routes = services.GetRequiredService<WorkspaceSystemMonitorPanelSessionFactory>();
        var runtimeFactory = services.GetRequiredService<IWorkspaceRuntimeServicesFactory>();
        var commandRuntime = new RecordingWorkspaceCommandRuntime();
        var hostServices = new WorkspaceRuntimeServices(
            new WorkspaceRuntimeBackends(
                dockerEngineClient: null,
                gitRepositoryClient: null,
                services.GetRequiredService<IFilePanelClient>(),
                fileTransferQueueClient: null,
                databasePanelClient: null,
                redisPanelSessionFactory: null,
                browserRendererViewFactory: null),
            WorkspaceNetworkRoute.Direct);
        var binding = new WorkspaceIsolationBinding(
            new WorkspaceId("isolated-definition"),
            new WorkspaceIsolationProviderId("test-provider"),
            WorkspaceIsolationCapability.StructuredProcessExecution,
            "test-resource",
            mounts: [],
            Guid.NewGuid());
        var workspaceServices = runtimeFactory.Create(new WorkspaceRuntimeServicesRequest(
            WorkspaceId,
            commandRuntime,
            hostServices,
            binding));

        await using (workspaceServices)
        {
            await using var monitor = await routes.CreateStatisticsAsync(
                WorkspaceId,
                SessionId.New(),
                BuiltInConnections.Local,
                CancellationToken.None);

            _ = await monitor.ReadStatisticsAsync(CancellationToken.None);

            Assert.True(commandRuntime.CommandPlanCount > 0);
        }

        using var replacement = routes.Register(
            WorkspaceId,
            services.GetRequiredService<SystemMonitorPanelSessionFactory>());
    }

    private sealed class RecordingMonitorFactory :
        ISystemMonitorPanelSessionFactory,
        IDisposable
    {
        private readonly SystemMonitorPanelSessionFactory _inner = new(TimeProvider.System);

        public CapabilitySet StatisticsCapabilities => _inner.StatisticsCapabilities;

        public CapabilitySet ProcessMonitorCapabilities => _inner.ProcessMonitorCapabilities;

        public int StatisticsCreateCount { get; private set; }

        public int ProcessMonitorCreateCount { get; private set; }

        public ValueTask<IStatisticsPanelSession> CreateStatisticsAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            StatisticsCreateCount++;
            return ((ISystemMonitorPanelSessionFactory)_inner).CreateStatisticsAsync(
                workspaceId,
                sessionId,
                connection,
                cancellationToken);
        }

        public ValueTask<IProcessMonitorPanelSession> CreateProcessMonitorAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            ProcessMonitorCreateCount++;
            return ((ISystemMonitorPanelSessionFactory)_inner).CreateProcessMonitorAsync(
                workspaceId,
                sessionId,
                connection,
                cancellationToken);
        }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class RecordingWorkspaceCommandRuntime :
        IConnectionRuntime,
        IConnectionCommandRuntime
    {
        public int CommandPlanCount { get; private set; }

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandPlanCount++;
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.ProcessFailed,
                    "test.monitor-command-stopped",
                    "The test stopped before launching an isolated command.",
                    Retryable: false,
                    ConnectionRecoveryAction.None)));
        }

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            PlanCommandAsync(connection, executable, arguments, cancellationToken);
    }
}
