using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class AgentPanelSessionHostTests
{
    [Fact]
    public async Task Focus_waits_for_one_action_permit_before_committing_graph_state()
    {
        await using var fixture = await AgentPanelHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentPanelRequest.Focus(fixture.SecondPanelId));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.BlockConsume = true;

        var execution = fixture.Client.RunAgentPanelActionAsync(
            authorizationId,
            action,
            default);
        await fixture.Authorization.ConsumeStarted.Task;

        var whileAwaitingPermit = await fixture.GraphAsync();
        Assert.Equal(
            fixture.FirstPanelId,
            whileAwaitingPermit.Workspace.Tabs[0].ActivePanelId);

        fixture.Authorization.ReleaseConsume.TrySetResult();
        var result = Assert.IsType<AgentPanelActionResult.Focused>(
            (await execution).Value());
        var committed = await fixture.GraphAsync();

        Assert.True(result.Receipt.Changed);
        Assert.Equal(fixture.SecondPanelId, result.Receipt.PanelId);
        Assert.Equal(
            fixture.SecondPanelId,
            committed.Workspace.Tabs[0].ActivePanelId);
        Assert.Equal(
            "panel_focused",
            Assert.Single(fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Graph_revision_drift_rejects_the_prepared_permit_without_focusing()
    {
        await using var fixture = await AgentPanelHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentPanelRequest.Focus(fixture.SecondPanelId));
        var authorizationId = fixture.Authorization.Arm(action);
        var beforeDrift = await fixture.GraphAsync();

        var changed = (await fixture.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                fixture.WorkspaceId,
                fixture.TabId,
                fixture.ThirdPanelId),
            fixture.HumanContext(expectedRevision: beforeDrift.Revision),
            default)).Value();

        var result = await fixture.Client.RunAgentPanelActionAsync(
            authorizationId,
            action,
            default);
        var after = await fixture.GraphAsync();

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(fixture.ThirdPanelId, changed.Workspace.Tabs[0].ActivePanelId);
        Assert.Equal(fixture.ThirdPanelId, after.Workspace.Tabs[0].ActivePanelId);
        Assert.Equal(changed.Revision, after.Revision);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Focusing_the_already_focused_panel_is_revision_stable()
    {
        await using var fixture = await AgentPanelHostFixture.CreateAsync();
        var before = await fixture.GraphAsync();
        var action = await fixture.PrepareAsync(
            new AgentPanelRequest.Focus(fixture.FirstPanelId));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = Assert.IsType<AgentPanelActionResult.Focused>(
            (await fixture.Client.RunAgentPanelActionAsync(
                authorizationId,
                action,
                default)).Value());
        var after = await fixture.GraphAsync();

        Assert.False(result.Receipt.Changed);
        Assert.Equal(before.Revision, result.Receipt.WorkspaceRevision);
        Assert.Equal(before.LastSequence, result.Receipt.GraphSequence);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.LastSequence, after.LastSequence);
        Assert.Equal(fixture.FirstPanelId, after.Workspace.Tabs[0].ActivePanelId);
    }

    private sealed class AgentPanelHostFixture : IAsyncDisposable
    {
        private AgentPanelHostFixture()
        {
            Clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            Composer = new AgentPanelActionComposer();
            Authorization = new FakeAuthorizationConsumer(Clock);
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                Clock,
                filePanelFactory: new FakeFilePanelSessionFactory(),
                agentAuthorizationConsumer: Authorization,
                agentPanelActionComposer: Composer);
        }

        public ManualTimeProvider Clock { get; }

        public AgentPanelActionComposer Composer { get; }

        public FakeAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("panel-test-client");

        public WindowInstanceId WindowId { get; } = new("panel-window");

        public WorkspaceInstanceId WorkspaceId { get; } =
            new("panel-workspace");

        public TabInstanceId TabId { get; } = new("panel-tab");

        public PanelInstanceId FirstPanelId { get; } = new("panel-first");

        public PanelInstanceId SecondPanelId { get; } = new("panel-second");

        public PanelInstanceId ThirdPanelId { get; } = new("panel-third");

        public AgentRunId RunId { get; } = new("panel-run");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("panel-agent"),
            ActorKind.Agent,
            "Panel agent");

        public static async ValueTask<AgentPanelHostFixture> CreateAsync()
        {
            var fixture = new AgentPanelHostFixture();
            var panels = new[]
            {
                new PanelInstance(
                    fixture.FirstPanelId,
                    PanelKind.FileViewer,
                    "First"),
                new PanelInstance(
                    fixture.SecondPanelId,
                    PanelKind.FileViewer,
                    "Second"),
                new PanelInstance(
                    fixture.ThirdPanelId,
                    PanelKind.FileViewer,
                    "Third"),
            };
            var tab = new TabInstance(
                fixture.TabId,
                "Work",
                panels,
                fixture.FirstPanelId);
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    new WorkspaceInstance(
                        fixture.WorkspaceId,
                        "Workspace",
                        [tab],
                        tab.Id)),
                fixture.HumanContext(),
                default)).Value();
            foreach (var panel in panels)
            {
                _ = (await fixture.Client.EnsureFilePanelSessionAsync(
                    new EnsureFilePanelSessionRequest(
                        Session(panel.Id),
                        new SessionOwner(
                            HostMode.Desktop,
                            fixture.WindowId,
                            fixture.WorkspaceId,
                            fixture.TabId,
                            panel.Id),
                        panel.Title,
                        Root(panel.Id)),
                    fixture.HumanContext(),
                    default)).Value();
            }

            return fixture;
        }

        public async ValueTask<AgentPanelAction> PrepareAsync(
            AgentPanelRequest request)
        {
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Workspace(
                        WindowId,
                        WorkspaceId)),
                AgentContext(),
                default)).Value();
            var now = Clock.GetUtcNow();
            return Composer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    RunId,
                    Agent,
                    policyGeneration: 0,
                    now,
                    now.AddMinutes(1)),
                context,
                request);
        }

        public async ValueTask<WorkspaceGraphSnapshot> GraphAsync() =>
            (await Client.GetWorkspaceGraphAsync(
                WorkspaceId,
                HumanContext(),
                default)).Value();

        public OperationContext HumanContext(long? expectedRevision = null) =>
            new(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(ClientId.Value),
                    ActorKind.Human,
                    "Test user",
                    ClientId),
                expectedRevision,
                CancellationId: CancellationId.New());

        private OperationContext AgentContext() =>
            new(
                RequestId.New(),
                Agent,
                CancellationId: CancellationId.New());

        public ValueTask DisposeAsync() => Client.DisposeAsync();

        private static SessionId Session(PanelInstanceId panelId) =>
            new($"session-{panelId.Value}");

        private static FilePanelLocation Root(PanelInstanceId panelId) =>
            new(
                "panel-files",
                "server.example",
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments(
                    [
                        new FilePanelPathSegment("workspace"),
                        new FilePanelPathSegment(panelId.Value),
                    ])));
    }

    private sealed class FakeAuthorizationConsumer(TimeProvider timeProvider)
        : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions =
            new();
        private AgentPanelAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _consumed;

        public bool BlockConsume { get; set; }

        public TaskCompletionSource ConsumeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseConsume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentPanelAction action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _authorizationId = AgentAuthorizationId.New();
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

        public async ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumeStarted.TrySetResult();
            if (BlockConsume)
            {
                await ReleaseConsume.Task.WaitAsync(cancellationToken);
            }

            var action = _action
                ?? throw new InvalidOperationException(
                    "An action must be armed before authorization is consumed.");
            var expected = AgentActionExecutionBinding.FromProposal(
                action.Proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(expected, currentBinding))
            {
                return new AgentPermitResult.Denied(
                    new AgentAuthorizationError(
                        AgentAuthorizationErrorCode.AuthorizationMismatch,
                        "The panel execution binding changed."));
            }

            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return new AgentPermitResult.Denied(
                    new AgentAuthorizationError(
                        AgentAuthorizationErrorCode.AuthorizationNotFound,
                        "The authorization was already consumed."));
            }

            Assert.True(BuiltInAgentTools.Catalog.TryGet(
                action.Proposal.ToolName,
                out var tool));
            var now = timeProvider.GetUtcNow();
            return new AgentPermitResult.Granted(
                new AgentActionPermit(
                    new AgentActionAuthorization(
                        authorizationId,
                        action.Proposal,
                        tool!,
                        AgentAuthorizationSource.AutoPolicy,
                        new ClientId("panel-test-client"),
                        now.AddMinutes(1)),
                    now,
                    CancellationToken.None));
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
            && string.Equals(
                left.ToolName,
                right.ToolName,
                StringComparison.Ordinal)
            && left.Target == right.Target
            && left.TargetIdentity == right.TargetIdentity
            && left.TargetFingerprint == right.TargetFingerprint
            && left.ArgumentDigest == right.ArgumentDigest
            && left.PolicyGeneration == right.PolicyGeneration;
    }
}
