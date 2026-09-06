using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Git;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class SecurityCampaignGitAuthorityTests
{
    private static readonly WindowInstanceId WindowId = new("campaign-git-window");
    private static readonly WorkspaceInstanceId WorkspaceId = new("campaign-git-workspace");
    private static readonly TabInstanceId TabId = new("campaign-git-tab");
    private static readonly PanelInstanceId PanelId = new("campaign-git-panel");
    private static readonly SessionId SessionId = new("campaign-git-session");
    private static readonly GitStateReferenceId StateReference = new("campaign-state");
    private static readonly GitChangeReferenceId ChangeReference = new("campaign-change");
    private static readonly GitRemoteReferenceId RemoteReference = new("campaign-remote");
    private static readonly GitRemoteStateReferenceId RemoteStateReference =
        new("campaign-remote-state");
    private static readonly GitBranchReferenceId BranchReference = new("campaign-branch");

    [Fact(DisplayName = "authority.git.read_remote_ref broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.git.read_remote_ref")]
    public Task ReadRemoteRefAsync() => RunCaseAsync(
        new AgentGitRequest.ReadRemoteRef(
            PanelId,
            StateReference,
            RemoteReference,
            BranchReference),
        GitAgentToolNames.ReadRemoteRef,
        expectDispatch: true);

    [Fact(DisplayName = "authority.git.stage broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.git.stage")]
    public Task StageAsync() => RunCaseAsync(
        new AgentGitRequest.Stage(PanelId, StateReference, ChangeReference),
        GitAgentToolNames.Stage,
        expectDispatch: true);

    [Fact(DisplayName = "authority.git.unstage broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.git.unstage")]
    public Task UnstageAsync() => RunCaseAsync(
        new AgentGitRequest.Unstage(PanelId, StateReference, ChangeReference),
        GitAgentToolNames.Unstage,
        expectDispatch: true);

    [Fact(DisplayName = "authority.git.branch_create broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.git.branch_create")]
    public Task BranchCreateAsync() => RunCaseAsync(
        new AgentGitRequest.BranchCreate(PanelId, StateReference, "campaign-branch"),
        GitAgentToolNames.BranchCreate,
        expectDispatch: true);

    [Fact(DisplayName = "authority.git.branch_checkout broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.git.branch_checkout")]
    public Task BranchCheckoutAsync() => RunCaseAsync(
        new AgentGitRequest.BranchCheckout(PanelId, StateReference, BranchReference),
        GitAgentToolNames.BranchCheckout,
        expectDispatch: true);

    [Fact(DisplayName = "authority.git.commit broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.git.commit")]
    public Task CommitAsync() => RunCaseAsync(
        new AgentGitRequest.Commit(
            PanelId,
            StateReference,
            "Campaign commit",
            "Exact campaign body"),
        GitAgentToolNames.Commit,
        expectDispatch: true);

    [Fact(DisplayName = "authority.git.push exact authority fails closed before sink")]
    [Trait("SecurityCampaignCase", "authority.git.push")]
    public Task PushAsync() => RunCaseAsync(
        new AgentGitRequest.Push(
            PanelId,
            StateReference,
            RemoteStateReference,
            RemoteReference,
            BranchReference),
        GitAgentToolNames.Push,
        expectDispatch: false);

    private static async Task RunCaseAsync(
        AgentGitRequest request,
        string expectedTool,
        bool expectDispatch)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new CampaignAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        var factory = new CampaignGitPanelSessionFactory();
        var composer = new AgentGitActionComposer();
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            agentAuthorizationConsumer: broker,
            gitPanelFactory: factory,
            agentGitActionComposer: composer);
        var human = new ActorDescriptor(
            new ActorId("campaign-git-client"),
            ActorKind.Human,
            "Campaign Git user",
            new ClientId("campaign-git-client"));
        var agent = new ActorDescriptor(
            new ActorId("campaign-git-agent"),
            ActorKind.Agent,
            "Campaign Git agent");
        var runId = new AgentRunId($"campaign-{expectedTool.Replace('.', '-')}");
        var workspaceTarget = new AgentTarget.Workspace(WindowId, WorkspaceId);
        var descriptor = Assert.Single(
            BuiltInAgentTools.Catalog.Tools,
            tool => string.Equals(tool.Name, expectedTool, StringComparison.Ordinal));
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                descriptor.Capability,
                AgentPermission.Ask),
        };
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                runId,
                agent,
                Assert.IsType<ClientId>(human.ClientId),
                workspaceTarget,
                policy,
                policyGeneration: 1),
            default));
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            new OperationContext(RequestId.New(), human),
            default)).Value();
        _ = (await host.EnsureGitSessionAsync(
            EnsureRequest(),
            new OperationContext(RequestId.New(), human),
            default)).Value();
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(workspaceTarget),
            new OperationContext(RequestId.New(), agent),
            default)).Value();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                runId,
                agent,
                policyGeneration: 1,
                clock.GetUtcNow(),
                clock.GetUtcNow().AddMinutes(1)),
            context,
            request);
        var approval = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(action.Proposal, default));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    approval.Approval.Id,
                    human,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                default));

        var result = await host.RunAgentGitActionAsync(
            authorized.Authorization.Id,
            action,
            default);
        var replay = await host.RunAgentGitActionAsync(
            authorized.Authorization.Id,
            action,
            default);

        if (expectDispatch)
        {
            Assert.IsType<HostResult<GitAgentOperationResult>.Success>(result);
            Assert.Equal([expectedTool], factory.Session!.Dispatches, StringComparer.Ordinal);
        }
        else
        {
            var failure = Assert.IsType<HostResult<GitAgentOperationResult>.Failure>(result);
            Assert.Equal("git_push_transport_unavailable", failure.Error.StableCode);
            Assert.Empty(factory.Session!.Dispatches);
        }

        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        Assert.Equal(expectDispatch ? 1 : 0, factory.Session!.Dispatches.Count);
        Assert.Equal(
            expectDispatch
                ? [
                    AuditOutcome.Requested,
                    AuditOutcome.Approved,
                    AuditOutcome.Started,
                    AuditOutcome.Succeeded,
                ]
                : [
                    AuditOutcome.Requested,
                    AuditOutcome.Approved,
                    AuditOutcome.Started,
                    AuditOutcome.Failed,
                ],
            audit.Events
                .Where(item => string.Equals(
                    item.CorrelationId,
                    action.Proposal.Id.Value,
                    StringComparison.Ordinal))
                .Select(item => item.Outcome));
    }

    private static EnsureGitSessionRequest EnsureRequest() => new(
        SessionId,
        new SessionOwner(
            HostMode.Desktop,
            WindowId,
            WorkspaceId,
            TabId,
            PanelId),
        "Campaign repository",
        new GitSessionTarget(
            new GitRepositoryHandle(BuiltInConnections.Local, "/campaign/repository"),
            bindingRevision: 7));

    private static WorkspaceInstance Workspace()
    {
        var panel = new PanelInstance(PanelId, PanelKind.Git, "Campaign Git");
        var tab = new TabInstance(TabId, "Campaign Git", [panel], PanelId);
        return new WorkspaceInstance(WorkspaceId, "Campaign Git", [tab], TabId);
    }

    private static CapabilitySet Capabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.GitReadState,
        SessionCapabilities.GitReadDiff,
        SessionCapabilities.GitReadRemoteRef,
        SessionCapabilities.GitStage,
        SessionCapabilities.GitUnstage,
        SessionCapabilities.GitBranchCreate,
        SessionCapabilities.GitBranchCheckout,
        SessionCapabilities.GitCommit,
        SessionCapabilities.GitPush,
    ]);

    private sealed class CampaignGitPanelSessionFactory : IGitPanelSessionFactory
    {
        public CapabilitySet Capabilities { get; } =
            SecurityCampaignGitAuthorityTests.Capabilities();

        public CampaignGitPanelSession? Session { get; private set; }

        public ValueTask<IGitPanelSession> CreateAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            GitSessionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session = new CampaignGitPanelSession(
                sessionId,
                target.Binding,
                Capabilities);
            return ValueTask.FromResult<IGitPanelSession>(Session);
        }
    }

    private sealed class CampaignGitPanelSession(
        SessionId id,
        GitSessionBinding binding,
        CapabilitySet capabilities) : IGitPanelSession
    {
        public SessionId Id { get; } = id;

        public PanelKind Kind => PanelKind.Git;

        public CapabilitySet Capabilities { get; } = capabilities;

        public GitSessionBinding Binding { get; } = binding;

        public GitPanelSessionState State { get; } = new(
            new GitSessionMetadata(
                binding.RepositoryIdentity,
                binding.BindingRevision,
                "Local",
                ConnectionKind.Local,
                MutationsQuarantined: false),
            IsReady: true);

        public List<string> Dispatches { get; } = [];

        public ValueTask<GitAgentOperationResult> ReadStateAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> ReadDiffAsync(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            GitChangeArea area,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> ReadRemoteRefAsync(
            GitStateReferenceId state,
            GitRemoteReferenceId remote,
            GitBranchReferenceId branch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StateReference, state);
            Assert.Equal(RemoteReference, remote);
            Assert.Equal(BranchReference, branch);
            Dispatches.Add(GitAgentToolNames.ReadRemoteRef);
            return ValueTask.FromResult<GitAgentOperationResult>(
                new GitAgentOperationResult.RemoteRef(
                    new GitAgentRemoteRefSnapshot(
                        RemoteStateReference,
                        "origin",
                        "main",
                        new string('a', 40),
                        IsAbsent: false,
                        DateTimeOffset.UnixEpoch)));
        }

        public ValueTask<GitAgentOperationResult> StageAsync(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            CancellationToken cancellationToken)
        {
            AssertChange(state, change, cancellationToken);
            return Mutation(GitAgentToolNames.Stage);
        }

        public ValueTask<GitAgentOperationResult> UnstageAsync(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            CancellationToken cancellationToken)
        {
            AssertChange(state, change, cancellationToken);
            return Mutation(GitAgentToolNames.Unstage);
        }

        public ValueTask<GitAgentOperationResult> CreateBranchAsync(
            GitStateReferenceId state,
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StateReference, state);
            Assert.Equal("campaign-branch", name);
            return Mutation(GitAgentToolNames.BranchCreate);
        }

        public ValueTask<GitAgentOperationResult> CheckoutBranchAsync(
            GitStateReferenceId state,
            GitBranchReferenceId branch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StateReference, state);
            Assert.Equal(BranchReference, branch);
            return Mutation(GitAgentToolNames.BranchCheckout);
        }

        public ValueTask<GitAgentOperationResult> CommitAsync(
            GitStateReferenceId state,
            string subject,
            string? body,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StateReference, state);
            Assert.Equal("Campaign commit", subject);
            Assert.Equal("Exact campaign body", body);
            return Mutation(GitAgentToolNames.Commit);
        }

        public ValueTask<GitAgentOperationResult> PushAsync(
            GitStateReferenceId state,
            GitRemoteStateReferenceId remoteState,
            GitRemoteReferenceId remote,
            GitBranchReferenceId branch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dispatches.Add(GitAgentToolNames.Push);
            return ValueTask.FromResult<GitAgentOperationResult>(
                new GitAgentOperationResult.Rejected("unexpected_push_dispatch"));
        }

        public ValueTask<PanelSessionSnapshot> SnapshotAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                HasActiveWork: false,
                "Ready"));

        public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = afterSequence;
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask<PanelCloseOutcome> CloseAsync(
            PanelCloseMode mode,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PanelCloseOutcome.GracefullyClosed);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void AssertChange(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(StateReference, state);
            Assert.Equal(ChangeReference, change);
        }

        private ValueTask<GitAgentOperationResult> Mutation(string toolName)
        {
            Dispatches.Add(toolName);
            return ValueTask.FromResult<GitAgentOperationResult>(
                new GitAgentOperationResult.Mutation(
                    new GitAgentMutationReceipt(
                        toolName,
                        StateReference,
                        new string('a', 40),
                        "main",
                        RemoteName: null,
                        RemoteSha: null,
                        ChangedPathCount: 1)));
        }
    }

    private sealed class CampaignAuditStore : IAuditStore
    {
        private readonly List<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events => _events;

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(auditEvent);
            return ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));
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
