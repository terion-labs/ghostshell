using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;
using GhostShell.Protocol;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class DockerSessionHostTests
{
    private static readonly WindowInstanceId WindowId = new("docker-window");
    private static readonly WorkspaceInstanceId WorkspaceId = new("docker-workspace");
    private static readonly TabInstanceId TabId = new("docker-tab");
    private static readonly PanelInstanceId PanelId = new("docker-panel");
    private static readonly SessionId SessionId = new("docker-session");

    [Fact]
    public async Task CancellationDuringDockerSessionCreationRetainsUncertainReplay()
    {
        var factory = new FakeDockerPanelSessionFactory();
        var creationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.AfterCreateAsync = async (_, cancellationToken) =>
        {
            creationEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        };
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(
            new IdempotencyKey("docker-create-cancelled"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureDockerSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await creationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var uncertain = await pending;
        var replay = await host.EnsureDockerSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(WorkspaceId, factory.LastWorkspaceId);
    }

    [Fact]
    public async Task DockerSessionSnapshotFailureDisposesEngineAndRetainsUncertainReplay()
    {
        var factory = new FakeDockerPanelSessionFactory
        {
            BeforeSnapshotForNewSessions = static _ =>
                ValueTask.FromException(new IOException("fake snapshot failure")),
        };
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(new IdempotencyKey("docker-create-failed"));

        var uncertain = await host.EnsureDockerSessionAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await host.EnsureDockerSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, factory.Session!.DisposeCount);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ConcurrentDockerCreationCompletesKnownSuccessAfterCallerCancellation()
    {
        var factory = new FakeDockerPanelSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotToken = CancellationToken.None;
        factory.BeforeSnapshotForNewSessions = async cancellationToken =>
        {
            snapshotToken = cancellationToken;
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(new IdempotencyKey("docker-create-known"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureDockerSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var concurrentReplay = await host.EnsureDockerSessionAsync(
            request,
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        var completed = await pending;
        var completedReplay = await host.EnsureDockerSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            concurrentReplay.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completed);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completedReplay);
        Assert.False(snapshotToken.CanBeCanceled);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task CancellationBeforeDockerSessionCreationLeavesKeyFresh()
    {
        var factory = new FakeDockerPanelSessionFactory();
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(
            new IdempotencyKey("docker-create-pre-cancelled"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await host.EnsureDockerSessionAsync(
            request,
            context,
            cancellation.Token);
        var retry = await host.EnsureDockerSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(retry);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task DockerOpenReservationRejectsCrossFamilyTerminalOpen()
    {
        var factory = new FakeDockerPanelSessionFactory();
        var terminals = new FakeTerminalSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.BeforeSnapshotForNewSessions = async _ =>
        {
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(factory, terminals);
        var context = Context(new IdempotencyKey("docker-cross-family"));

        var docker = host.EnsureDockerSessionAsync(
            Request(),
            context,
            CancellationToken.None).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await host.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("docker-cross-family-terminal"),
                new SessionOwner(
                    HostMode.Desktop,
                    WindowId,
                    WorkspaceId,
                    TabId,
                    new PanelInstanceId("docker-cross-family-terminal-panel")),
                "Terminal",
                new TerminalLaunchRequest("/tmp")),
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        Assert.Equal(HostErrorCode.IdempotencyKeyReused, rejected.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(await docker);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, terminals.CreateCount);
    }

    [Fact]
    public async Task NegotiationReflectsTheConfiguredDockerFactory()
    {
        var factory = new FakeDockerPanelSessionFactory();
        await using var configured = CreateHost(factory);
        await using var missing = CreateHost(null);
        var hello = (await configured.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], AllDockerCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var missingHello = (await missing.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], AllDockerCapabilities()),
            Context(),
            CancellationToken.None)).Value();

        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DockerReadState));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DockerInspect));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DockerReadLogs));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DockerFilesList));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DockerFilesStat));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DockerFilesRead));
        Assert.False(missingHello.Capabilities.Contains(SessionCapabilities.DockerReadState));

        var unsupported = await missing.EnsureDockerSessionAsync(
            Request(),
            Context(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.CapabilityNotSupported, unsupported.Error().Code);
    }

    [Fact]
    public async Task EnsureCreatesAndLinksOnlyTheExactPrimaryDockerSession()
    {
        var factory = new FakeDockerPanelSessionFactory();
        await using var host = CreateHost(factory);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        var context = Context(idempotencyKey: new IdempotencyKey("open-docker-once"));

        var first = (await host.EnsureDockerSessionAsync(
            Request(),
            context,
            CancellationToken.None)).Value();
        var replay = (await host.EnsureDockerSessionAsync(
            Request(),
            context,
            CancellationToken.None)).Value();
        var graph = (await host.GetWorkspaceGraphAsync(
            WorkspaceId,
            Context(),
            CancellationToken.None)).Value();

        Assert.Equal(first, replay);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(PanelKind.Docker, first.Descriptor.Kind);
        var panel = Assert.Single(Assert.Single(graph.Workspace.Tabs).Panels);
        Assert.Equal(PanelKind.Docker, panel.Kind);
        Assert.Equal(SessionId, panel.SessionId);

        var changedBinding = await host.EnsureDockerSessionAsync(
            Request(bindingRevision: 8),
            Context(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.InvalidRequest, changedBinding.Error().Code);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ProviderFailureReturnsAFixedEndpointFreeError()
    {
        var factory = new FakeDockerPanelSessionFactory { FailOpen = true };
        await using var host = CreateHost(factory);
        var result = await host.EnsureDockerSessionAsync(
            Request(connection: RemoteConnection()),
            Context(),
            CancellationToken.None);
        var failure = Assert.IsType<HostResult<SessionSnapshot>.Failure>(result);

        Assert.Equal("docker_open_failed", failure.Error.StableCode);
        Assert.DoesNotContain("private.internal", failure.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("needle", failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GovernedReadConsumesOneAuthorizationAndAuditsOneResult()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeDockerPanelSessionFactory();
        var composer = new AgentDockerReadActionComposer();
        var authorization = new DockerAuthorizationConsumer(
            clock,
            new ClientId("docker-test-client"));
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            dockerPanelFactory: factory,
            agentAuthorizationConsumer: authorization,
            agentDockerReadActionComposer: composer);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.EnsureDockerSessionAsync(
            Request(),
            Context(),
            CancellationToken.None)).Value();
        var actor = new ActorDescriptor(
            new ActorId("docker-agent"),
            ActorKind.Agent,
            "Docker agent");
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(new AgentTarget.Workspace(WindowId, WorkspaceId)),
            new OperationContext(
                RequestId.New(),
                actor,
                CancellationId: CancellationId.New()),
            CancellationToken.None)).Value();
        var now = clock.GetUtcNow();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("docker-agent-run"),
                actor,
                policyGeneration: 0,
                now,
                now.AddMinutes(1)),
            context,
            new AgentDockerReadRequest.ReadState(PanelId, 10));
        var authorizationId = authorization.Arm(action);

        var first = (await host.RunAgentDockerReadAsync(
            authorizationId,
            action,
            CancellationToken.None)).Value();
        var replay = await host.RunAgentDockerReadAsync(
            authorizationId,
            action,
            CancellationToken.None);

        Assert.IsType<AgentDockerReadResult.State>(first);
        Assert.Equal(1, factory.Session!.ReadStateCount);
        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        var completion = Assert.Single(authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("docker_read_completed", completion.StableCode);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.AutoPolicy, false)]
    [InlineData(AgentAuthorizationSource.HumanApproval, true)]
    [InlineData(AgentAuthorizationSource.YoloPolicy, true)]
    public async Task GovernedControlRequiresExplicitApprovalSource(
        AgentAuthorizationSource source,
        bool expectedDispatch)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeDockerPanelSessionFactory();
        var composer = new AgentDockerReadActionComposer();
        var authorization = new DockerControlAuthorizationConsumer(
            clock,
            new ClientId("docker-control-client"));
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            dockerPanelFactory: factory,
            agentAuthorizationConsumer: authorization,
            agentDockerReadActionComposer: composer);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.EnsureDockerSessionAsync(
            Request(),
            Context(),
            CancellationToken.None)).Value();
        var actor = new ActorDescriptor(
            new ActorId("docker-control-agent"),
            ActorKind.Agent,
            "Docker control agent");
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(new AgentTarget.Workspace(WindowId, WorkspaceId)),
            new OperationContext(
                RequestId.New(),
                actor,
                CancellationId: CancellationId.New()),
            CancellationToken.None)).Value();
        var now = clock.GetUtcNow();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("docker-control-run"),
                actor,
                policyGeneration: 0,
                now,
                now.AddMinutes(1)),
            context,
            new AgentDockerControlRequest(
                PanelId,
                new DockerResourceReferenceId("opaque_container"),
                factory.Session!.State.EngineGeneration,
                new DockerContainerRevision("abcdef"),
                DockerContainerAction.Pause,
                "running"));
        var authorizationId = authorization.Arm(action, source);

        var result = await host.RunAgentDockerControlAsync(
            authorizationId,
            action,
            CancellationToken.None);

        Assert.Equal(expectedDispatch ? 1 : 0, factory.Session.ControlCount);
        if (expectedDispatch)
        {
            var applied = Assert.IsType<HostResult<AgentDockerControlResult>.Success>(result);
            Assert.Equal(DockerContainerControlOutcome.Applied, applied.Value.Outcome);
            Assert.Equal(AgentActionOutcome.Succeeded, Assert.Single(authorization.Completions).Outcome);
        }
        else
        {
            var rejected = Assert.IsType<HostResult<AgentDockerControlResult>.Failure>(result);
            Assert.Equal(HostErrorCode.InvalidRequest, rejected.Error.Code);
            Assert.Equal("docker_authorization_rejected", rejected.Error.StableCode);
            Assert.Equal(AgentActionOutcome.Failed, Assert.Single(authorization.Completions).Outcome);
        }
    }

    private static InMemorySessionHostClient CreateHost(
        IDockerPanelSessionFactory? dockerFactory,
        ITerminalSessionFactory? terminalFactory = null) =>
        new(
            terminalFactory ?? new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            dockerPanelFactory: dockerFactory);

    private static EnsureDockerSessionRequest Request(
        long bindingRevision = 7,
        ConnectionProfile? connection = null) =>
        new(
            SessionId,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                WorkspaceId,
                TabId,
                PanelId),
            "Docker",
            new DockerSessionTarget(connection ?? BuiltInConnections.Local, bindingRevision));

    private static ConnectionProfile RemoteConnection() => new(
        new ConnectionId("saved-private-docker"),
        ConnectionProfile.CurrentSchemaVersion,
        "Private Docker",
        new ConnectionEndpoint.Ssh("private.internal", username: "operator"),
        new ConnectionAuthentication.Password(new SecretRef("needle-secret-reference")),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.Strict);

    private static WorkspaceInstance Workspace()
    {
        var panel = new PanelInstance(PanelId, PanelKind.Docker, "Docker");
        var tab = new TabInstance(TabId, "Docker", [panel], PanelId);
        return new WorkspaceInstance(WorkspaceId, "Docker", [tab], TabId);
    }

    private static CapabilitySet AllDockerCapabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.DockerReadState,
        SessionCapabilities.DockerInspect,
        SessionCapabilities.DockerReadLogs,
        SessionCapabilities.DockerFilesList,
        SessionCapabilities.DockerFilesStat,
        SessionCapabilities.DockerFilesRead,
        SessionCapabilities.DockerContainerPause,
    ]);

    private static OperationContext Context(IdempotencyKey? idempotencyKey = null) =>
        new(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("docker-user"),
                ActorKind.Human,
                "Docker user",
                new ClientId("docker-user")),
            IdempotencyKey: idempotencyKey,
            CancellationId: CancellationId.New());

    private sealed class FakeDockerPanelSessionFactory : IDockerPanelSessionFactory
    {
        public CapabilitySet Capabilities { get; } = AllDockerCapabilities();

        public int CreateCount { get; private set; }

        public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

        public bool FailOpen { get; init; }

        public Func<FakeDockerPanelSession, CancellationToken, ValueTask>? AfterCreateAsync
        {
            get;
            set;
        }

        public Func<CancellationToken, ValueTask>? BeforeSnapshotForNewSessions
        {
            get;
            set;
        }

        public FakeDockerPanelSession? Session { get; private set; }

        public async ValueTask<IDockerPanelSession> CreateAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            DockerSessionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            LastWorkspaceId = workspaceId;
            if (FailOpen)
            {
                throw new InvalidOperationException(
                    "Provider included private.internal and Password=needle in its exception.");
            }

            var session = new FakeDockerPanelSession(
                sessionId,
                target.Binding,
                Capabilities)
            {
                BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
            };
            Session = session;
            if (AfterCreateAsync is { } afterCreate)
            {
                await afterCreate(session, cancellationToken).ConfigureAwait(false);
            }

            return session;
        }
    }

    private sealed class FakeDockerPanelSession(
        SessionId id,
        DockerSessionBinding binding,
        CapabilitySet capabilities) : IDockerPanelSession
    {
        private bool _closed;

        public SessionId Id { get; } = id;

        public PanelKind Kind => PanelKind.Docker;

        public CapabilitySet Capabilities { get; } = capabilities;

        public DockerSessionBinding Binding { get; } = binding;

        public DockerPanelSessionState State { get; } = new(
            "Docker",
            binding.ConnectionKind,
            DockerEngineGeneration.New(),
            new DockerEngineSummary("28.3.0", "Linux", "amd64", "1.51"),
            IsReady: true);

        public int ReadStateCount { get; private set; }

        public int ControlCount { get; private set; }

        public Func<CancellationToken, ValueTask>? BeforeSnapshotAsync { get; set; }

        public int DisposeCount { get; private set; }

        public ValueTask<DockerResult<DockerPanelSnapshot>> ReadStateAsync(
            int maximumResourcesPerKind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadStateCount++;
            return ValueTask.FromResult<DockerResult<DockerPanelSnapshot>>(
                new DockerResult<DockerPanelSnapshot>.Success(
                    new DockerPanelSnapshot(
                        State.Engine,
                        [],
                        [],
                        [],
                        [],
                        DateTimeOffset.UnixEpoch,
                        IsTruncated: false)));
        }

        public ValueTask<DockerResult<DockerInspectionSnapshot>> InspectAsync(
            DockerResourceReferenceId reference,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadLogsAsync(
            DockerLogReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DockerResult<DockerFilePage>> ListFilesAsync(
            DockerFileListRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
            DockerFileStatRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DockerResult<DockerFileSnapshot>> ReadFileAsync(
            DockerFileReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<DockerContainerControlResult> ControlContainerAsync(
            DockerContainerControlRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControlCount++;
            return ValueTask.FromResult(new DockerContainerControlResult(
                DockerContainerControlOutcome.Applied,
                "docker_container_control_applied",
                Retryable: false));
        }

        public async ValueTask<PanelSessionSnapshot> SnapshotAsync(
            CancellationToken cancellationToken)
        {
            if (BeforeSnapshotAsync is { } beforeSnapshot)
            {
                await beforeSnapshot(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _closed
                ? new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "Closed")
                : new PanelSessionSnapshot(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    false,
                    "Ready");
        }

        public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask<PanelCloseOutcome> CloseAsync(
            PanelCloseMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed)
            {
                return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
            }

            _closed = true;
            return ValueTask.FromResult(mode == PanelCloseMode.Force
                ? PanelCloseOutcome.ForceTerminated
                : PanelCloseOutcome.GracefullyClosed);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _closed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DockerAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentDockerReadAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _consumed;

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentDockerReadAction action)
        {
            _action = action;
            _authorizationId = AgentAuthorizationId.New();
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _action
                ?? throw new InvalidOperationException("An action must be armed.");
            var expected = AgentActionExecutionBinding.FromProposal(action.Proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(expected, currentBinding)
                || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationMismatch,
                            "The Docker execution binding changed.")));
            }

            Assert.True(BuiltInAgentTools.Catalog.TryGet(
                action.Proposal.ToolName,
                out var tool));
            var now = timeProvider.GetUtcNow();
            return ValueTask.FromResult<AgentPermitResult>(
                new AgentPermitResult.Granted(
                    new AgentActionPermit(
                        new AgentActionAuthorization(
                            authorizationId,
                            action.Proposal,
                            tool!,
                            AgentAuthorizationSource.AutoPolicy,
                            clientId,
                            now.AddMinutes(1)),
                        now,
                        CancellationToken.None)));
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            _ = permit;
            cancellationToken.ThrowIfCancellationRequested();
            _completions.Enqueue(completion);
            return ValueTask.FromResult<AgentAuthorizationError?>(null);
        }

        private static bool BindingsMatch(
            AgentActionExecutionBinding left,
            AgentActionExecutionBinding right) =>
            left.ActionId == right.ActionId
            && left.RunId == right.RunId
            && left.ActorId == right.ActorId
            && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
            && left.Target == right.Target
            && left.TargetIdentity == right.TargetIdentity
            && left.TargetFingerprint == right.TargetFingerprint
            && left.ArgumentDigest == right.ArgumentDigest
            && left.PolicyGeneration == right.PolicyGeneration;
    }

    private sealed class DockerControlAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentDockerControlAction? _action;
        private AgentAuthorizationId _authorizationId;
        private AgentAuthorizationSource _source;
        private int _consumed;

        public IReadOnlyList<AgentActionCompletion> Completions => [.. _completions];

        public AgentAuthorizationId Arm(
            AgentDockerControlAction action,
            AgentAuthorizationSource source)
        {
            _action = action;
            _source = source;
            _authorizationId = AgentAuthorizationId.New();
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _action
                ?? throw new InvalidOperationException("An action must be armed.");
            var expected = AgentActionExecutionBinding.FromProposal(action.Proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(expected, currentBinding)
                || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(new AgentAuthorizationError(
                        AgentAuthorizationErrorCode.AuthorizationMismatch,
                        "The Docker control binding changed.")));
            }

            Assert.True(BuiltInAgentTools.Catalog.TryGet(
                action.Proposal.ToolName,
                out var tool));
            var now = timeProvider.GetUtcNow();
            return ValueTask.FromResult<AgentPermitResult>(
                new AgentPermitResult.Granted(new AgentActionPermit(
                    new AgentActionAuthorization(
                        authorizationId,
                        action.Proposal,
                        tool!,
                        _source,
                        clientId,
                        now.AddMinutes(1)),
                    now,
                    CancellationToken.None)));
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            _ = permit;
            cancellationToken.ThrowIfCancellationRequested();
            _completions.Enqueue(completion);
            return ValueTask.FromResult<AgentAuthorizationError?>(null);
        }

        private static bool BindingsMatch(
            AgentActionExecutionBinding left,
            AgentActionExecutionBinding right) =>
            left.ActionId == right.ActionId
            && left.RunId == right.RunId
            && left.ActorId == right.ActorId
            && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
            && left.Target == right.Target
            && left.TargetIdentity == right.TargetIdentity
            && left.TargetFingerprint == right.TargetFingerprint
            && left.ArgumentDigest == right.ArgumentDigest
            && left.PolicyGeneration == right.PolicyGeneration;
    }
}
