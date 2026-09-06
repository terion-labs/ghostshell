using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Git;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class GitSessionHostTests
{
    private static readonly WindowInstanceId WindowId = new("git-window");
    private static readonly WorkspaceInstanceId WorkspaceId = new("git-workspace");
    private static readonly TabInstanceId TabId = new("git-tab");
    private static readonly PanelInstanceId PanelId = new("git-panel");
    private static readonly SessionId SessionId = new("git-session");

    [Fact]
    public async Task EnsureCreatesOneExactSafeHostedGitSession()
    {
        var factory = new FakeGitPanelSessionFactory();
        await using var host = CreateHost(factory);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        var operation = Context(new IdempotencyKey("git-open"));

        var first = (await host.EnsureGitSessionAsync(
            Request(),
            operation,
            CancellationToken.None)).Value();
        var replay = (await host.EnsureGitSessionAsync(
            Request(),
            operation,
            CancellationToken.None)).Value();

        Assert.Equal(first, replay);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(WorkspaceId, factory.LastWorkspaceId);
        Assert.Equal(PanelKind.Git, first.Descriptor.Kind);
        Assert.Equal(Request().Target.Identity, first.Descriptor.GitMetadata?.RepositoryIdentity);
        Assert.DoesNotContain("/repo", first.Descriptor.StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentReadConsumesOneExactPermitAndDispatchesOnce()
    {
        var factory = new FakeGitPanelSessionFactory();
        var composer = new AgentGitActionComposer();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var authorization = new GitAuthorizationConsumer(clock, new ClientId("git-client"));
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            agentAuthorizationConsumer: authorization,
            gitPanelFactory: factory,
            agentGitActionComposer: composer);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.EnsureGitSessionAsync(
            Request(),
            Context(),
            CancellationToken.None)).Value();
        var actor = new ActorDescriptor(
            new ActorId("git-agent"),
            ActorKind.Agent,
            "Git agent");
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(new AgentTarget.Workspace(WindowId, WorkspaceId)),
            new OperationContext(RequestId.New(), actor),
            CancellationToken.None)).Value();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("git-run"),
                actor,
                policyGeneration: 1,
                clock.GetUtcNow(),
                clock.GetUtcNow().AddMinutes(1)),
            context,
            new AgentGitRequest.ReadState(PanelId));
        var authorizationId = authorization.Arm(action);

        var first = await host.RunAgentGitActionAsync(
            authorizationId,
            action,
            CancellationToken.None);
        var replay = await host.RunAgentGitActionAsync(
            authorizationId,
            action,
            CancellationToken.None);

        Assert.True(
            first is HostResult<GitAgentOperationResult>.Success,
            first is HostResult<GitAgentOperationResult>.Failure failure
                ? $"{failure.Error.Code}:{failure.Error.StableCode}"
                : "Unknown host result.");
        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        Assert.Equal(1, factory.Session!.ReadStateCount);
        Assert.Single(authorization.Completions);
    }

    private static InMemorySessionHostClient CreateHost(IGitPanelSessionFactory factory) =>
        new(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            gitPanelFactory: factory);

    private static EnsureGitSessionRequest Request() => new(
        SessionId,
        new SessionOwner(
            HostMode.Desktop,
            WindowId,
            WorkspaceId,
            TabId,
            PanelId),
        "Repository",
        new GitSessionTarget(
            new GitRepositoryHandle(BuiltInConnections.Local, "/repo"),
            bindingRevision: 7));

    private static WorkspaceInstance Workspace()
    {
        var panel = new PanelInstance(PanelId, PanelKind.Git, "Git");
        var tab = new TabInstance(TabId, "Git", [panel], PanelId);
        return new WorkspaceInstance(WorkspaceId, "Git", [tab], TabId);
    }

    private static OperationContext Context(IdempotencyKey? idempotencyKey = null) =>
        new(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("git-user"),
                ActorKind.Human,
                "Git user",
                new ClientId("git-user")),
            IdempotencyKey: idempotencyKey);

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

    private sealed class FakeGitPanelSessionFactory : IGitPanelSessionFactory
    {
        public CapabilitySet Capabilities { get; } = GitSessionHostTests.Capabilities();

        public int CreateCount { get; private set; }

        public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

        public FakeGitPanelSession? Session { get; private set; }

        public ValueTask<IGitPanelSession> CreateAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            GitSessionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            LastWorkspaceId = workspaceId;
            Session = new FakeGitPanelSession(sessionId, target.Binding, Capabilities);
            return ValueTask.FromResult<IGitPanelSession>(Session);
        }
    }

    private sealed class FakeGitPanelSession(
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

        public int ReadStateCount { get; private set; }

        public ValueTask<GitAgentOperationResult> ReadStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadStateCount++;
            return ValueTask.FromResult<GitAgentOperationResult>(
                new GitAgentOperationResult.State(new GitAgentStateSnapshot(
                    new GitStateReferenceId("state"),
                    "repository",
                    "Local",
                    "main",
                    new string('a', 40),
                    IsDetached: false,
                    IsUnborn: false,
                    HasConflicts: false,
                    IsDirty: false,
                    [],
                    [],
                    [],
                    IsTruncated: false,
                    MutationsQuarantined: false,
                    DateTimeOffset.UnixEpoch)));
        }

        public ValueTask<GitAgentOperationResult> ReadDiffAsync(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            GitChangeArea area,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> ReadRemoteRefAsync(
            GitStateReferenceId state,
            GitRemoteReferenceId remote,
            GitBranchReferenceId branch,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> StageAsync(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> UnstageAsync(
            GitStateReferenceId state,
            GitChangeReferenceId change,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> CreateBranchAsync(
            GitStateReferenceId state,
            string name,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> CheckoutBranchAsync(
            GitStateReferenceId state,
            GitBranchReferenceId branch,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> CommitAsync(
            GitStateReferenceId state,
            string subject,
            string? body,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GitAgentOperationResult> PushAsync(
            GitStateReferenceId state,
            GitRemoteStateReferenceId remoteState,
            GitRemoteReferenceId remote,
            GitBranchReferenceId branch,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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

    private sealed class GitAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentGitAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _consumed;

        public IReadOnlyList<AgentActionCompletion> Completions => [.. _completions];

        public AgentAuthorizationId Arm(AgentGitAction action)
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
                    new AgentPermitResult.Denied(new AgentAuthorizationError(
                        AgentAuthorizationErrorCode.AuthorizationMismatch,
                        "The Git authorization changed.")));
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
}
