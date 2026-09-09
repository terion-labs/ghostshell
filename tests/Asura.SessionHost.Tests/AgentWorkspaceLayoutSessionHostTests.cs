using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class AgentWorkspaceLayoutSessionHostTests
{
    [Fact]
    public async Task Authorized_tab_create_mutates_and_verifies_the_fresh_graph()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var request = new AgentWorkspaceLayoutRequest.TabCreate(
            PanelKind.Statistics);
        var action = await fixture.PrepareAsync(request);
        var authorizationId = fixture.Authorization.Arm(action.Proposal);

        var result = (await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            authorizationId,
            action,
            fixture.Port,
            default)).Value();

        Assert.Equal(BuiltInAgentTools.TabCreate, result.Operation);
        Assert.Equal(PanelKind.Statistics, result.PanelKind);
        Assert.NotNull(result.TabId);
        Assert.NotNull(result.PanelId);
        Assert.Equal(2, (await fixture.GraphAsync()).Workspace.Tabs.Count);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("tab_created", completion.StableCode);
    }

    [Fact]
    public async Task Later_graph_revision_that_preserves_the_split_target_is_verified()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var request = new AgentWorkspaceLayoutRequest.PanelSplit(
            fixture.PanelId,
            AgentPanelSplitOrientation.LeftRight,
            PanelKind.Statistics);
        var action = await fixture.PrepareAsync(request);
        var authorizationId = fixture.Authorization.Arm(action.Proposal);
        fixture.Port.AdvanceGraphAfterMutation = true;

        var result = (await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            authorizationId,
            action,
            fixture.Port,
            default)).Value();

        Assert.Equal(BuiltInAgentTools.PanelSplit, result.Operation);
        Assert.Equal((await fixture.GraphAsync()).Revision, result.WorkspaceRevision);
        Assert.Equal("panel_split", Assert.Single(
            fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Post_authorization_unknown_result_is_never_retried()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var request = new AgentWorkspaceLayoutRequest.TabCreate(
            PanelKind.Statistics);
        var action = await fixture.PrepareAsync(request);
        var authorizationId = fixture.Authorization.Arm(action.Proposal);
        fixture.Port.ReturnOutcomeUnknown = true;

        var result = await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            authorizationId,
            action,
            fixture.Port,
            default);

        Assert.Equal(
            "workspace_layout_outcome_unknown",
            result.Error().StableCode);
        Assert.Equal(1, fixture.Port.CallCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Exact_workspace_port_and_supported_kind_are_required()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var request = new AgentWorkspaceLayoutRequest.TabCreate(
            PanelKind.Statistics);
        var action = await fixture.PrepareAsync(request);
        var authorizationId = fixture.Authorization.Arm(action.Proposal);
        fixture.Port.SupportedKinds.Clear();

        var result = await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            authorizationId,
            action,
            fixture.Port,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Port.CallCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Authorized_connection_list_preserves_graph_and_returns_opaque_options()
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentWorkspaceLayoutRequest.ConnectionList());
        var authorizationId = fixture.Authorization.Arm(action.Proposal);
        var before = await fixture.GraphAsync();

        var result = (await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            authorizationId,
            action,
            fixture.Port,
            default)).Value();

        Assert.Equal(BuiltInAgentTools.ConnectionsList, result.Operation);
        var connection = Assert.Single(result.Connections);
        Assert.Equal("connection_test", connection.Reference);
        Assert.Equal(before.Revision, result.WorkspaceRevision);
        Assert.Equal("connections_listed", Assert.Single(
            fixture.Authorization.Completions).StableCode);
    }

    internal async Task SecurityCampaignDispatchesExactLayoutToolAsync(
        string toolName)
    {
        await using var fixture = await LayoutFixture.CreateAsync();
        var extraTabId = new TabInstanceId("layout-extra-tab");
        var extraPanelId = new PanelInstanceId("layout-extra-panel");
        if (toolName is BuiltInAgentTools.TabClose
            or BuiltInAgentTools.PanelClose)
        {
            var tabs = toolName == BuiltInAgentTools.TabClose
                ? new[]
                {
                    new TabInstance(
                        fixture.TabId,
                        "Tab",
                        [new PanelInstance(
                            fixture.PanelId,
                            PanelKind.Statistics,
                            "Panel")],
                        fixture.PanelId),
                    new TabInstance(
                        extraTabId,
                        "Extra",
                        [new PanelInstance(
                            extraPanelId,
                            PanelKind.Statistics,
                            "Extra")],
                        extraPanelId),
                }
                :
                [
                    new TabInstance(
                        fixture.TabId,
                        "Tab",
                        [
                            new PanelInstance(
                                fixture.PanelId,
                                PanelKind.Statistics,
                                "Panel"),
                            new PanelInstance(
                                extraPanelId,
                                PanelKind.Statistics,
                                "Extra"),
                        ],
                        fixture.PanelId),
                ];
            var before = await fixture.GraphAsync();
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    new WorkspaceInstance(
                        fixture.WorkspaceId,
                        "Workspace",
                        tabs,
                        fixture.TabId)),
                fixture.HumanContext(before.Revision),
                default)).Value();
        }

        AgentWorkspaceLayoutRequest request = toolName switch
        {
            BuiltInAgentTools.TabClose =>
                new AgentWorkspaceLayoutRequest.TabClose(extraTabId),
            BuiltInAgentTools.PanelConnect =>
                new AgentWorkspaceLayoutRequest.PanelConnect(
                    fixture.PanelId,
                    "connection_test"),
            BuiltInAgentTools.PanelAdd =>
                new AgentWorkspaceLayoutRequest.PanelAdd(
                    fixture.TabId,
                    PanelKind.Statistics),
            BuiltInAgentTools.PanelClose =>
                new AgentWorkspaceLayoutRequest.PanelClose(extraPanelId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(toolName),
                toolName,
                null),
        };
        var action = await fixture.PrepareAsync(request);

        var forged = await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            AgentAuthorizationId.New(),
            action,
            fixture.Port,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, forged.Error().Code);
        Assert.Equal(0, fixture.Port.CallCount);

        var result = await fixture.Client.RunAgentWorkspaceLayoutActionAsync(
            fixture.Authorization.Arm(action.Proposal),
            action,
            fixture.Port,
            default);

        Assert.Equal(toolName, result.Value().Operation);
        Assert.Equal(1, fixture.Port.CallCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    private sealed class LayoutFixture : IAsyncDisposable
    {
        private LayoutFixture()
        {
            Authorization = new AuthorizationConsumer();
            Composer = new AgentWorkspaceLayoutActionComposer();
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                agentAuthorizationConsumer: Authorization,
                agentWorkspaceLayoutActionComposer: Composer);
            Port = new LayoutPort(this);
        }

        public InMemorySessionHostClient Client { get; }
        public AgentWorkspaceLayoutActionComposer Composer { get; }
        public AuthorizationConsumer Authorization { get; }
        public LayoutPort Port { get; }
        public WindowInstanceId WindowId { get; } = new("layout-window");
        public WorkspaceInstanceId WorkspaceId { get; } = new("layout-workspace");
        public TabInstanceId TabId { get; } = new("layout-tab");
        public PanelInstanceId PanelId { get; } = new("layout-panel");

        public static async ValueTask<LayoutFixture> CreateAsync()
        {
            var fixture = new LayoutFixture();
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    fixture.InitialWorkspace()),
                fixture.HumanContext(),
                default)).Value();
            return fixture;
        }

        public async ValueTask<AgentWorkspaceLayoutAction> PrepareAsync(
            AgentWorkspaceLayoutRequest request)
        {
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Workspace(WindowId, WorkspaceId)),
                AgentContext(),
                default)).Value();
            var now = DateTimeOffset.UtcNow;
            return Composer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    new AgentRunId("layout-run"),
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

        private WorkspaceInstance InitialWorkspace() => new(
            WorkspaceId,
            "Workspace",
            [new TabInstance(
                TabId,
                "Tab",
                [new PanelInstance(PanelId, PanelKind.Statistics, "Panel")],
                PanelId)],
            TabId);

        public OperationContext HumanContext(long? expectedRevision = null)
        {
            var clientId = new ClientId("layout-client");
            return new OperationContext(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(clientId.Value),
                    ActorKind.Human,
                    "Test user",
                    clientId),
                expectedRevision,
                CancellationId: CancellationId.New());
        }

        private OperationContext AgentContext() => new(
            RequestId.New(),
            Agent,
            CancellationId: CancellationId.New());

        private static ActorDescriptor Agent { get; } = new(
            new ActorId("layout-agent"),
            ActorKind.Agent,
            "Layout agent");

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class LayoutPort(LayoutFixture fixture)
        : IAgentWorkspaceLayoutMutationPort
    {
        public WindowInstanceId WindowId => fixture.WindowId;
        public WorkspaceInstanceId WorkspaceId => fixture.WorkspaceId;
        public HashSet<PanelKind> SupportedKinds { get; } =
            [PanelKind.Statistics];
        public IReadOnlySet<PanelKind> SupportedPanelKinds => SupportedKinds;
        public bool ReturnOutcomeUnknown { get; set; }
        public bool AdvanceGraphAfterMutation { get; set; }
        public int CallCount { get; private set; }

        public async ValueTask<AgentWorkspaceLayoutMutationResult> MutateAsync(
            AgentWorkspaceLayoutRequest request,
            long expectedWorkspaceRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (ReturnOutcomeUnknown)
            {
                return new AgentWorkspaceLayoutMutationResult.OutcomeUnknown();
            }

            if (request is AgentWorkspaceLayoutRequest.ConnectionList)
            {
                return new AgentWorkspaceLayoutMutationResult.Observed(
                    await fixture.GraphAsync(),
                    [new AgentWorkspaceConnectionOption(
                        "connection_test",
                        "Local",
                        "Local",
                        [PanelKind.Terminal])]);
            }

            var before = await fixture.GraphAsync();
            var createdTabId = new TabInstanceId("created-tab");
            var createdPanelId = new PanelInstanceId("created-panel");
            WorkspaceInstance workspace;
            TabInstanceId? appliedTabId = null;
            PanelInstanceId? appliedPanelId = null;
            PanelKind? kind = null;
            switch (request)
            {
                case AgentWorkspaceLayoutRequest.TabCreate create:
                    workspace = new WorkspaceInstance(
                        fixture.WorkspaceId,
                        before.Workspace.Title,
                        before.Workspace.Tabs.Concat(
                        [
                            new TabInstance(
                                createdTabId,
                                "Created",
                                [new PanelInstance(
                                    createdPanelId,
                                    create.Kind,
                                    "Created")],
                                createdPanelId),
                        ]),
                        createdTabId);
                    appliedTabId = createdTabId;
                    appliedPanelId = createdPanelId;
                    kind = create.Kind;
                    break;
                case AgentWorkspaceLayoutRequest.PanelAdd add:
                    workspace = new WorkspaceInstance(
                        fixture.WorkspaceId,
                        before.Workspace.Title,
                        before.Workspace.Tabs.Select(tab => tab.Id == add.TabId
                            ? new TabInstance(
                                tab.Id,
                                tab.Title,
                                tab.Panels.Concat(
                                [
                                    new PanelInstance(
                                        createdPanelId,
                                        add.Kind,
                                        "Created"),
                                ]),
                                createdPanelId)
                            : tab),
                        add.TabId);
                    appliedTabId = add.TabId;
                    appliedPanelId = createdPanelId;
                    kind = add.Kind;
                    break;
                case AgentWorkspaceLayoutRequest.PanelSplit split:
                    workspace = new WorkspaceInstance(
                        fixture.WorkspaceId,
                        before.Workspace.Title,
                        before.Workspace.Tabs.Select(tab => tab.Id == fixture.TabId
                            ? new TabInstance(
                                tab.Id,
                                tab.Title,
                                tab.Panels.Concat(
                                [
                                    new PanelInstance(
                                        createdPanelId,
                                        split.Kind,
                                        "Created"),
                                ]),
                                createdPanelId)
                            : tab),
                        fixture.TabId);
                    appliedTabId = fixture.TabId;
                    appliedPanelId = createdPanelId;
                    kind = split.Kind;
                    break;
                case AgentWorkspaceLayoutRequest.TabClose close:
                    workspace = new WorkspaceInstance(
                        fixture.WorkspaceId,
                        before.Workspace.Title,
                        before.Workspace.Tabs.Where(tab => tab.Id != close.TabId),
                        fixture.TabId);
                    appliedTabId = close.TabId;
                    break;
                case AgentWorkspaceLayoutRequest.PanelClose close:
                    workspace = new WorkspaceInstance(
                        fixture.WorkspaceId,
                        before.Workspace.Title,
                        before.Workspace.Tabs.Select(tab => new TabInstance(
                            tab.Id,
                            tab.Title,
                            tab.Panels.Where(panel => panel.Id != close.PanelId),
                            tab.ActivePanelId == close.PanelId
                                ? tab.Panels.First(panel =>
                                    panel.Id != close.PanelId).Id
                                : tab.ActivePanelId)),
                        before.Workspace.ActiveTabId);
                    appliedPanelId = close.PanelId;
                    break;
                case AgentWorkspaceLayoutRequest.PanelConnect connect:
                    workspace = before.Workspace;
                    appliedPanelId = connect.PanelId;
                    kind = before.Workspace.Tabs
                        .SelectMany(tab => tab.Panels)
                        .Single(panel => panel.Id == connect.PanelId).Kind;
                    break;
                default:
                    throw new InvalidOperationException(
                        "The layout test port received an unsupported request.");
            }
            var snapshot = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(fixture.WindowId, workspace),
                fixture.HumanContext(expectedWorkspaceRevision),
                cancellationToken)).Value();
            if (AdvanceGraphAfterMutation)
            {
                _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                    new RegisterWorkspaceGraphRequest(fixture.WindowId, workspace),
                    fixture.HumanContext(snapshot.Revision),
                    cancellationToken)).Value();
            }

            return new AgentWorkspaceLayoutMutationResult.Applied(
                snapshot,
                appliedTabId,
                appliedPanelId,
                kind);
        }
    }

    private sealed class AuthorizationConsumer : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions =
            new();
        private AgentActionProposal? _proposal;
        private AgentAuthorizationId _authorizationId;

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentActionProposal proposal)
        {
            _proposal = proposal;
            _authorizationId = AgentAuthorizationId.New();
            return _authorizationId;
        }

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposal = _proposal ?? throw new InvalidOperationException();
            var expected = AgentActionExecutionBinding.FromProposal(proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(currentBinding, expected))
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationMismatch,
                            "Binding mismatch.")));
            }

            Assert.True(BuiltInAgentTools.Catalog.TryGet(
                proposal.ToolName,
                out var descriptor));
            var authorization = new AgentActionAuthorization(
                authorizationId,
                proposal,
                descriptor!,
                AgentAuthorizationSource.AutoPolicy,
                new ClientId("layout-client"),
                DateTimeOffset.UtcNow.AddMinutes(1));
            return ValueTask.FromResult<AgentPermitResult>(
                new AgentPermitResult.Granted(
                    new AgentActionPermit(
                        authorization,
                        DateTimeOffset.UtcNow,
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
