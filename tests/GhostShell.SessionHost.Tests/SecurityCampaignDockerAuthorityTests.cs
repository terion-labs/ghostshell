using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Docker;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class SecurityCampaignDockerAuthorityTests
{
    [Fact(DisplayName = "authority.docker.container_start broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.docker.container_start")]
    public Task StartAsync() => ExerciseAsync(
        BuiltInAgentTools.DockerContainerStart,
        DockerContainerAction.Start,
        "created");

    [Fact(DisplayName = "authority.docker.container_stop broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.docker.container_stop")]
    public Task StopAsync() => ExerciseAsync(
        BuiltInAgentTools.DockerContainerStop,
        DockerContainerAction.Stop,
        "running");

    [Fact(DisplayName = "authority.docker.container_restart broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.docker.container_restart")]
    public Task RestartAsync() => ExerciseAsync(
        BuiltInAgentTools.DockerContainerRestart,
        DockerContainerAction.Restart,
        "running");

    [Fact(DisplayName = "authority.docker.container_pause broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.docker.container_pause")]
    public Task PauseAsync() => ExerciseAsync(
        BuiltInAgentTools.DockerContainerPause,
        DockerContainerAction.Pause,
        "running");

    [Fact(DisplayName = "authority.docker.container_resume broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.docker.container_resume")]
    public Task ResumeAsync() => ExerciseAsync(
        BuiltInAgentTools.DockerContainerResume,
        DockerContainerAction.Resume,
        "paused");

    [Fact(DisplayName = "authority.docker.container_remove broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.docker.container_remove")]
    public Task RemoveAsync() => ExerciseAsync(
        BuiltInAgentTools.DockerContainerRemove,
        DockerContainerAction.Remove,
        "exited");

    private static async Task ExerciseAsync(
        string toolName,
        DockerContainerAction expectedAction,
        string expectedState)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        var composer = new AgentDockerReadActionComposer();
        var factory = new DockerControlSessionFactory();
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            dockerPanelFactory: factory,
            agentAuthorizationConsumer: broker,
            agentDockerReadActionComposer: composer);
        var ids = new TestIds(toolName.Replace('.', '-'));
        var human = new ActorDescriptor(
            new ActorId(ids.ClientId.Value),
            ActorKind.Human,
            "Docker security campaign user",
            ids.ClientId);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(ids.WindowId, ids.Workspace()),
            new OperationContext(
                RequestId.New(),
                human,
                CancellationId: CancellationId.New()),
            CancellationToken.None)).Value();
        _ = (await host.EnsureDockerSessionAsync(
            new EnsureDockerSessionRequest(
                ids.SessionId,
                ids.Owner,
                "Docker",
                new DockerSessionTarget(BuiltInConnections.Local, 1)),
            new OperationContext(
                RequestId.New(),
                human,
                CancellationId: CancellationId.New()),
            CancellationToken.None)).Value();

        var agent = new ActorDescriptor(
            new ActorId($"agent-{ids.Suffix}"),
            ActorKind.Agent,
            "Docker security campaign agent");
        var runId = new AgentRunId($"run-{ids.Suffix}");
        var target = new AgentTarget.Workspace(ids.WindowId, ids.WorkspaceId);
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.Docker,
                AgentPermission.Ask),
        };
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                runId,
                agent,
                ids.ClientId,
                target,
                policy,
                policyGeneration: 1),
            CancellationToken.None));
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(target),
            new OperationContext(
                RequestId.New(),
                agent,
                CancellationId: CancellationId.New()),
            CancellationToken.None)).Value();
        var now = clock.GetUtcNow();
        var request = new AgentDockerControlRequest(
            ids.PanelId,
            new DockerResourceReferenceId("opaque_container_ref"),
            factory.Session!.State.EngineGeneration,
            new DockerContainerRevision("abcdef0123456789"),
            expectedAction,
            expectedState);
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                runId,
                agent,
                policyGeneration: 1,
                now,
                now.AddMinutes(1)),
            context,
            request);
        Assert.Equal(toolName, action.Proposal.ToolName);
        var required = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(action.Proposal, CancellationToken.None));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    required.Approval.Id,
                    human,
                    approved: true,
                    AgentApprovalDuration.Once,
                    now),
                CancellationToken.None));
        Assert.Equal(AgentAuthorizationSource.HumanApproval, authorized.Authorization.Source);

        var result = await host.RunAgentDockerControlAsync(
            authorized.Authorization.Id,
            action,
            CancellationToken.None);
        var replay = await host.RunAgentDockerControlAsync(
            authorized.Authorization.Id,
            action,
            CancellationToken.None);

        var success = Assert.IsType<HostResult<AgentDockerControlResult>.Success>(result);
        Assert.Equal(toolName, success.Value.ToolName);
        Assert.Equal(DockerContainerControlOutcome.Applied, success.Value.Outcome);
        Assert.Equal(1, factory.Session.ControlCount);
        var dispatched = Assert.IsType<DockerContainerControlRequest>(
            factory.Session.LastControlRequest);
        Assert.Equal(expectedAction, dispatched.Action);
        Assert.Equal(expectedState, dispatched.ExpectedState);
        Assert.Equal(request.Container, dispatched.Container);
        Assert.Equal(request.EngineGeneration, dispatched.EngineGeneration);
        Assert.Equal(request.ContainerRevision, dispatched.ContainerRevision);
        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        Assert.Contains(audit.Events, auditEvent =>
            string.Equals(
                auditEvent.CorrelationId,
                action.Proposal.Id.Value,
                StringComparison.Ordinal)
            && auditEvent.Outcome == AuditOutcome.Succeeded);
    }

    private sealed class TestIds(string suffix)
    {
        public string Suffix { get; } = suffix;

        public WindowInstanceId WindowId { get; } = new($"window-{suffix}");

        public WorkspaceInstanceId WorkspaceId { get; } = new($"workspace-{suffix}");

        public TabInstanceId TabId { get; } = new($"tab-{suffix}");

        public PanelInstanceId PanelId { get; } = new($"panel-{suffix}");

        public SessionId SessionId { get; } = new($"session-{suffix}");

        public ClientId ClientId { get; } = new($"client-{suffix}");

        public SessionOwner Owner => new(
            HostMode.Desktop,
            WindowId,
            WorkspaceId,
            TabId,
            PanelId);

        public WorkspaceInstance Workspace()
        {
            var panel = new PanelInstance(PanelId, PanelKind.Docker, "Docker");
            var tab = new TabInstance(TabId, "Docker", [panel], PanelId);
            return new WorkspaceInstance(WorkspaceId, "Docker", [tab], TabId);
        }
    }

    private sealed class DockerControlSessionFactory : IDockerPanelSessionFactory
    {
        public CapabilitySet Capabilities { get; } = new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.DockerContainerStart,
            SessionCapabilities.DockerContainerStop,
            SessionCapabilities.DockerContainerRestart,
            SessionCapabilities.DockerContainerPause,
            SessionCapabilities.DockerContainerResume,
            SessionCapabilities.DockerContainerRemove,
        ]);

        public DockerControlSession? Session { get; private set; }

        public ValueTask<IDockerPanelSession> CreateAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            DockerSessionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session = new DockerControlSession(sessionId, target.Binding, Capabilities);
            return ValueTask.FromResult<IDockerPanelSession>(Session);
        }
    }

    private sealed class DockerControlSession(
        SessionId id,
        DockerSessionBinding binding,
        CapabilitySet capabilities) : IDockerPanelSession
    {
        public SessionId Id { get; } = id;

        public PanelKind Kind => PanelKind.Docker;

        public CapabilitySet Capabilities { get; } = capabilities;

        public DockerSessionBinding Binding { get; } = binding;

        public DockerPanelSessionState State { get; } = new(
            "Docker security sink",
            ConnectionKind.Local,
            new DockerEngineGeneration("security_engine_generation"),
            new DockerEngineSummary("29", "Linux", "arm64", "1.51"),
            IsReady: true);

        public int ControlCount { get; private set; }

        public DockerContainerControlRequest? LastControlRequest { get; private set; }

        public ValueTask<DockerContainerControlResult> ControlContainerAsync(
            DockerContainerControlRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ControlCount++;
            LastControlRequest = request;
            return ValueTask.FromResult(new DockerContainerControlResult(
                DockerContainerControlOutcome.Applied,
                "docker_container_control_applied",
                Retryable: false));
        }

        public ValueTask<DockerResult<DockerPanelSnapshot>> ReadStateAsync(
            int maximumResourcesPerKind,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerInspectionSnapshot>> InspectAsync(
            DockerResourceReferenceId reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadLogsAsync(
            DockerLogReadRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerFilePage>> ListFilesAsync(
            DockerFileListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
            DockerFileStatRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerFileSnapshot>> ReadFileAsync(
            DockerFileReadRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PanelSessionSnapshot> SnapshotAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                HasActiveWork: false,
                "Ready"));
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
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PanelCloseOutcome.GracefullyClosed);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryAuditStore : IAuditStore
    {
        private readonly List<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events => _events;

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(auditEvent);
            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AuditEventRecord> matches =
            [
                .. _events.Where(item => string.Equals(
                    item.CorrelationId,
                    correlationId,
                    StringComparison.Ordinal)),
            ];
            return ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(matches));
        }
    }
}
