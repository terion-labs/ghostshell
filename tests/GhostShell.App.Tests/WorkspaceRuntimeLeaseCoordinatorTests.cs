using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.SessionHost;
using GhostShell.SessionHost.Tests;

namespace GhostShell.App.Tests;

public sealed class WorkspaceRuntimeLeaseCoordinatorTests
{
    private static readonly WorkspaceIsolationProviderDescriptor ProviderDescriptor = new(
        new WorkspaceIsolationProviderId("test-isolation"),
        "Test isolation",
        WorkspaceIsolationCapability.StructuredProcessExecution);

    [Fact]
    public async Task DirectAndIsolatedWorkspacesShareOneRuntimeLeaseSurface()
    {
        var hostRuntime = new RecordingConnectionRuntime();
        await using var host = HostSession();
        var provider = new RecordingIsolationProvider();
        var factory = new RecordingRuntimeServicesFactory();
        var hostServices = HostServices(host);
        var coordinator = new WorkspaceRuntimeLeaseCoordinator(
            hostRuntime,
            hostServices,
            provider,
            factory);
        var directId = new WorkspaceInstanceId("direct-runtime");
        var isolatedId = new WorkspaceInstanceId("isolated-runtime");
        var binding = Binding();
        coordinator.OwnPreparedBinding(binding);

        coordinator.Register(directId, binding: null);
        coordinator.Register(isolatedId, binding);

        Assert.NotSame(hostRuntime, coordinator.ConnectionRuntimeFor(directId));
        Assert.IsType<WorkspaceIsolatedConnectionRuntime>(
            coordinator.ConnectionRuntimeFor(isolatedId));
        Assert.NotSame(hostServices, coordinator.RuntimeServicesFor(directId));
        Assert.NotNull(coordinator.RuntimeServicesFor(isolatedId)?.NetworkRoute.ProxyUri);
        Assert.Collection(
            factory.Requests,
            request => Assert.Null(request.IsolationBinding),
            request => Assert.Same(binding, request.IsolationBinding));

        Assert.Null(await coordinator.ReleaseAsync(directId, CancellationToken.None));
        Assert.Null(await coordinator.ReleaseAsync(isolatedId, CancellationToken.None));
        Assert.Equal(1, Assert.Single(factory.Lifetimes).DisposeCount);
        Assert.Equal(binding, Assert.Single(provider.StopBindings));
    }

    [Fact]
    public async Task FailedServiceDisposalRetriesWithoutRepeatingSuccessfulProviderStop()
    {
        var provider = new RecordingIsolationProvider();
        await using var host = HostSession();
        var factory = new RecordingRuntimeServicesFactory
        {
            DisposeFailuresRemaining = 1,
        };
        var coordinator = new WorkspaceRuntimeLeaseCoordinator(
            new RecordingConnectionRuntime(),
            HostServices(host),
            provider,
            factory);
        var workspaceId = new WorkspaceInstanceId("retry-runtime");
        var binding = Binding();
        coordinator.OwnPreparedBinding(binding);
        coordinator.Register(workspaceId, binding);

        Assert.NotNull(await coordinator.ReleaseAsync(workspaceId, CancellationToken.None));
        Assert.Null(await coordinator.ReleaseAsync(workspaceId, CancellationToken.None));

        Assert.Equal(2, Assert.Single(factory.Lifetimes).DisposeCount);
        Assert.Single(provider.StopBindings);
    }

    [Fact]
    public async Task FailedProviderStopRetainsTheExactBindingForRetry()
    {
        var provider = new RecordingIsolationProvider
        {
            StopFailuresRemaining = 1,
        };
        await using var host = HostSession();
        var factory = new RecordingRuntimeServicesFactory();
        var coordinator = new WorkspaceRuntimeLeaseCoordinator(
            new RecordingConnectionRuntime(),
            HostServices(host),
            provider,
            factory);
        var workspaceId = new WorkspaceInstanceId("stop-retry-runtime");
        var binding = Binding();
        coordinator.OwnPreparedBinding(binding);
        coordinator.Register(workspaceId, binding);

        Assert.NotNull(await coordinator.ReleaseAsync(workspaceId, CancellationToken.None));
        Assert.Null(await coordinator.ReleaseAsync(workspaceId, CancellationToken.None));

        Assert.Equal(1, Assert.Single(factory.Lifetimes).DisposeCount);
        Assert.Equal(2, provider.StopBindings.Count);
        Assert.All(provider.StopBindings, stopped => Assert.Same(binding, stopped));
    }

    [Fact]
    public async Task WindowCloseDrainBlocksNewActivationsUntilTheAttemptResumes()
    {
        await using var host = HostSession();
        var coordinator = new WorkspaceRuntimeLeaseCoordinator(
            new RecordingConnectionRuntime(),
            HostServices(host),
            isolationProvider: null,
            servicesFactory: null);
        Assert.True(coordinator.TryBeginActivation(
            isClosing: false,
            out var firstId,
            out var firstCompletion));

        var draining = coordinator.BeginWindowCloseActivationDrain();

        var drainingActivation = Assert.Single(draining);
        Assert.False(coordinator.TryBeginActivation(
            isClosing: false,
            out _,
            out _));
        coordinator.CompleteActivation(firstId, firstCompletion);
        await drainingActivation;
        coordinator.ResumeAfterWindowCloseAttempt(shutdownStarted: false);
        Assert.True(coordinator.TryBeginActivation(
            isClosing: false,
            out var resumedId,
            out var resumedCompletion));
        coordinator.CompleteActivation(resumedId, resumedCompletion);
        await coordinator.AwaitActivationsAsync();
    }

    private static InMemorySessionHostClient HostSession() => new(
        new FakeTerminalSessionFactory(),
        new DesktopLifecyclePolicy(),
        TimeProvider.System);

    private static WorkspaceRuntimeServices HostServices(
        ISessionHostClient sessionClient) => new(
        new WorkspaceRuntimeBackends(
            dockerEngineClient: null,
            gitRepositoryClient: null,
            filePanelClient: new EmptyFilePanelClient(),
            fileTransferQueueClient: null,
            databasePanelClient: null,
            redisPanelSessionFactory: null,
            browserRendererViewFactory: null),
        WorkspaceNetworkRoute.Direct);

    private sealed class EmptyFilePanelClient : IFilePanelClient
    {
        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; } = [];

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static WorkspaceIsolationBinding Binding() => new(
        new WorkspaceId("coordinator-workspace"),
        ProviderDescriptor.Id,
        ProviderDescriptor.Capabilities,
        "coordinator-resource",
        [],
        Guid.NewGuid());

    private sealed class RecordingRuntimeServicesFactory : IWorkspaceRuntimeServicesFactory
    {
        public int DisposeFailuresRemaining { get; init; }

        public List<WorkspaceRuntimeServicesRequest> Requests { get; } = [];

        public List<RecordingLifetime> Lifetimes { get; } = [];

        public WorkspaceRuntimeServices Create(WorkspaceRuntimeServicesRequest request)
        {
            Requests.Add(request);
            if (request.IsolationBinding is null)
            {
                return request.HostServices;
            }

            var lifetime = new RecordingLifetime(DisposeFailuresRemaining);
            Lifetimes.Add(lifetime);
            return new WorkspaceRuntimeServices(
                request.HostServices.Backends,
                WorkspaceNetworkRoute.ViaProxy(new Uri(
                    "socks5://127.0.0.1:1",
                    UriKind.Absolute)),
                lifetime);
        }
    }

    private sealed class RecordingLifetime(int failuresRemaining) : IAsyncDisposable
    {
        private int _failuresRemaining = failuresRemaining;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new IOException("The runtime service cleanup failed.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingIsolationProvider : IWorkspaceIsolationProvider
    {
        public int StopFailuresRemaining { get; init; }

        public List<WorkspaceIsolationBinding> StopBindings { get; } = [];

        public WorkspaceIsolationProviderDescriptor Descriptor => ProviderDescriptor;

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding,
            WorkspaceIsolationProcessRequest request) =>
            throw new NotSupportedException();

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
            WorkspaceIsolationBinding binding,
            CancellationToken cancellationToken)
        {
            StopBindings.Add(binding);
            if (StopFailuresRemaining >= StopBindings.Count)
            {
                return ValueTask.FromResult(
                    WorkspaceIsolationResult<WorkspaceIsolationBinding>.Fail(
                        WorkspaceIsolationErrorCode.StopFailed));
            }

            return ValueTask.FromResult(
                WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
        }
    }

    private sealed class RecordingConnectionRuntime : IConnectionRuntime
    {
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
    }
}
