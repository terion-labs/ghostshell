using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Files;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceFilePanelSessionFactoryTests
{
    private static readonly WorkspaceInstanceId WorkspaceId = new("isolated-workspace");
    private static readonly WorkspaceInstanceId OtherWorkspaceId = new("plain-workspace");

    [Fact]
    public async Task RegistrationRoutesOnlyItsWorkspaceAndDisposalRestoresHostRouting()
    {
        await using var services = DesktopComposition.CreateServiceProvider();
        var routes = services.GetRequiredService<WorkspaceFilePanelSessionFactory>();
        var host = services.GetRequiredService<FilePanelSessionFactory>();
        var fileClient = services.GetRequiredService<IFilePanelClient>();
        var initialLocation = fileClient.Profiles
            .Single(profile => string.Equals(
                profile.Id,
                BuiltInFileProviders.HomeId.Value,
                StringComparison.Ordinal))
            .StartLocation;
        var workspace = new RecordingFilePanelSessionFactory(host);

        await using var firstHostSession = await routes.CreateAsync(
            OtherWorkspaceId,
            SessionId.New(),
            initialLocation,
            CancellationToken.None);
        using (routes.Register(WorkspaceId, workspace))
        {
            await using var workspaceSession = await routes.CreateAsync(
                WorkspaceId,
                SessionId.New(),
                initialLocation,
                CancellationToken.None);
        }

        await using var secondHostSession = await routes.CreateAsync(
            WorkspaceId,
            SessionId.New(),
            initialLocation,
            CancellationToken.None);

        Assert.Equal(1, workspace.CreateCount);
        Assert.Equal(WorkspaceId, workspace.LastWorkspaceId);
    }

    [Fact]
    public async Task DuplicateWorkspaceRegistrationIsRejected()
    {
        await using var services = DesktopComposition.CreateServiceProvider();
        var routes = services.GetRequiredService<WorkspaceFilePanelSessionFactory>();
        var host = services.GetRequiredService<FilePanelSessionFactory>();
        using var registration = routes.Register(
            WorkspaceId,
            new RecordingFilePanelSessionFactory(host));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            routes.Register(
                WorkspaceId,
                new RecordingFilePanelSessionFactory(host)));

        Assert.Equal(
            "The workspace already has a file-panel factory.",
            exception.Message);
    }

    [Fact]
    public async Task DesktopRuntimeRoutesFileCommandsThroughTheIsolatedWorkspace()
    {
        await using var services = DesktopComposition.CreateServiceProvider();
        var routes = services.GetRequiredService<WorkspaceFilePanelSessionFactory>();
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
            var profile = workspaceServices.Backends.FilePanelClient.Profiles
                .Single(candidate => string.Equals(
                    candidate.Name,
                    "Workspace",
                    StringComparison.Ordinal));
            await using var fileSession = await routes.CreateAsync(
                WorkspaceId,
                SessionId.New(),
                profile.StartLocation,
                CancellationToken.None);

            _ = await fileSession.ListAsync(
                new FilePanelListRequest(
                    profile.StartLocation,
                    25,
                    ContinuationToken: null,
                    ShowHidden: false),
                CancellationToken.None);

            Assert.True(commandRuntime.CommandPlanCount > 0);
        }

        using var replacement = routes.Register(
            WorkspaceId,
            services.GetRequiredService<FilePanelSessionFactory>());
    }

    private sealed class RecordingFilePanelSessionFactory(
        IFilePanelSessionFactory inner) : IFilePanelSessionFactory
    {
        public CapabilitySet Capabilities => inner.Capabilities;

        public int CreateCount { get; private set; }

        public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

        public ValueTask<IFilePanelSession> CreateAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            FilePanelLocation initialLocation,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            LastWorkspaceId = workspaceId;
            return inner.CreateAsync(
                workspaceId,
                sessionId,
                initialLocation,
                cancellationToken);
        }
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
                    "test.file-command-stopped",
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
