using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class AgentWorkspaceGraphSessionHostTests
{
    [Fact]
    public async Task Projection_waits_for_permit_and_audits_each_success_code()
    {
        await using var fixture = await GraphHostFixture.CreateAsync();
        var requests = new (AgentWorkspaceGraphRequest Request, string Code)[]
        {
            (new AgentWorkspaceGraphRequest.WorkspaceInspect(), "workspace_inspected"),
            (new AgentWorkspaceGraphRequest.TabList(), "tabs_listed"),
            (new AgentWorkspaceGraphRequest.PanelList(), "panels_listed"),
        };

        foreach (var item in requests)
        {
            var action = await fixture.PrepareAsync(item.Request);
            var authorizationId = fixture.Authorization.Arm(action);
            fixture.Authorization.BlockConsume = true;

            var execution = fixture.Client.RunAgentWorkspaceGraphActionAsync(
                authorizationId,
                action,
                default);
            await fixture.Authorization.ConsumeStarted.Task;
            Assert.False(execution.IsCompleted);

            fixture.Authorization.ReleaseConsume.TrySetResult();
            _ = (await execution).Value();

            Assert.Equal(
                item.Code,
                fixture.Authorization.Completions[^1].StableCode);
            fixture.Authorization.ResetBlock();
        }
    }

    [Fact]
    public async Task Structural_drift_rejects_before_consumption_and_audit()
    {
        await using var fixture = await GraphHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentWorkspaceGraphRequest.PanelList());
        var authorizationId = fixture.Authorization.Arm(action);
        var before = await fixture.GraphAsync();
        await fixture.ReplaceGraphAsync(
            fixture.Workspace(
                firstPanels:
                [
                    fixture.StatisticsPanel(),
                    fixture.TerminalPanel(),
                ]),
            before.Revision);

        var result = await fixture.Client.RunAgentWorkspaceGraphActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Focus_and_title_refresh_do_not_widen_or_invalidate_projection()
    {
        await using var fixture = await GraphHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentWorkspaceGraphRequest.WorkspaceInspect());
        var authorizationId = fixture.Authorization.Arm(action);
        var before = await fixture.GraphAsync();
        await fixture.ReplaceGraphAsync(
            fixture.Workspace(
                workspaceTitle: "Renamed workspace",
                activeTab: fixture.SecondTabId,
                activePanel: fixture.ProcessPanel()),
            before.Revision);

        var result =
            Assert.IsType<
                AgentWorkspaceGraphActionResult.WorkspaceInspected>(
                (await fixture.Client.RunAgentWorkspaceGraphActionAsync(
                    authorizationId,
                    action,
                    default)).Value());

        Assert.Equal(
            "Renamed workspace",
            result.Workspace.Workspace.Title!.Text);
        Assert.Equal(2, result.Workspace.Tabs.Count);
        Assert.Equal(
            [
                fixture.TerminalPanel(),
                fixture.StatisticsPanel(),
                fixture.ProcessPanel(),
            ],
            result.Workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .Select(panel => panel.PanelId));
        Assert.Equal(
            "workspace_inspected",
            Assert.Single(fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Revoked_permit_cancels_and_records_completion()
    {
        await using var fixture = await GraphHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentWorkspaceGraphRequest.PanelList());
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.RevokeBeforeGrant = true;

        var result = await fixture.Client.RunAgentWorkspaceGraphActionAsync(
            authorizationId,
            action,
            default);

        var failure =
            Assert.IsType<
                HostResult<AgentWorkspaceGraphActionResult>.Failure>(result);
        Assert.Equal(HostErrorCode.Cancelled, failure.Error.Code);
        Assert.Equal("authority_revoked", failure.Error.StableCode);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("authority_revoked", completion.StableCode);
    }

    [Fact]
    public async Task Completion_audit_failure_replaces_an_observed_result()
    {
        await using var fixture = await GraphHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentWorkspaceGraphRequest.WorkspaceInspect());
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.CompletionError =
            new AgentAuthorizationError(
                AgentAuthorizationErrorCode.AlreadyCompleted,
                "Audit rejected.");

        var result = await fixture.Client.RunAgentWorkspaceGraphActionAsync(
            authorizationId,
            action,
            default);

        var failure =
            Assert.IsType<
                HostResult<AgentWorkspaceGraphActionResult>.Failure>(result);
        Assert.Equal(HostErrorCode.EngineFailed, failure.Error.Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            failure.Error.StableCode);
    }

    private sealed class GraphHostFixture : IAsyncDisposable
    {
        private GraphHostFixture()
        {
            Clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            Composer = new AgentWorkspaceGraphActionComposer();
            Authorization = new FakeAuthorizationConsumer(Clock);
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                Clock,
                agentAuthorizationConsumer: Authorization,
                agentWorkspaceGraphActionComposer: Composer);
        }

        public ManualTimeProvider Clock { get; }

        public AgentWorkspaceGraphActionComposer Composer { get; }

        public FakeAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public WindowInstanceId WindowId { get; } = new("graph-window");

        public WorkspaceInstanceId WorkspaceId { get; } =
            new("graph-workspace");

        public TabInstanceId FirstTabId { get; } = new("tab-primary");

        public TabInstanceId SecondTabId { get; } = new("tab-secondary");

        public AgentRunId RunId { get; } = new("graph-run");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("graph-agent"),
            ActorKind.Agent,
            "Graph agent");

        public static async ValueTask<GraphHostFixture> CreateAsync()
        {
            var fixture = new GraphHostFixture();
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    fixture.Workspace()),
                fixture.HumanContext(),
                default)).Value();
            return fixture;
        }

        public async ValueTask<AgentWorkspaceGraphAction> PrepareAsync(
            AgentWorkspaceGraphRequest request)
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

        public async ValueTask ReplaceGraphAsync(
            WorkspaceInstance workspace,
            long expectedRevision)
        {
            _ = (await Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(WindowId, workspace),
                HumanContext(expectedRevision),
                default)).Value();
        }

        public async ValueTask<WorkspaceGraphSnapshot> GraphAsync() =>
            (await Client.GetWorkspaceGraphAsync(
                WorkspaceId,
                HumanContext(),
                default)).Value();

        public WorkspaceInstance Workspace(
            string workspaceTitle = "Workspace",
            TabInstanceId? activeTab = null,
            PanelInstanceId? activePanel = null,
            IReadOnlyList<PanelInstanceId>? firstPanels = null)
        {
            firstPanels ??= [TerminalPanel(), StatisticsPanel()];
            var first = new TabInstance(
                FirstTabId,
                "Primary",
                firstPanels.Select(panel => new PanelInstance(
                    panel,
                    panel == TerminalPanel()
                        ? PanelKind.Terminal
                        : PanelKind.Statistics,
                    panel.Value)),
                activeTab == FirstTabId && activePanel is { } firstActive
                    ? firstActive
                    : firstPanels[0]);
            var process = ProcessPanel();
            var second = new TabInstance(
                SecondTabId,
                "Secondary",
                [new PanelInstance(
                    process,
                    PanelKind.ProcessMonitor,
                    "Processes")],
                process);
            return new WorkspaceInstance(
                WorkspaceId,
                workspaceTitle,
                [first, second],
                activeTab ?? FirstTabId);
        }

        public PanelInstanceId TerminalPanel() => new("panel-terminal");

        public PanelInstanceId StatisticsPanel() => new("panel-statistics");

        public PanelInstanceId ProcessPanel() => new("panel-process");

        public OperationContext HumanContext(long? expectedRevision = null)
        {
            var clientId = new ClientId("graph-client");
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

        private OperationContext AgentContext() =>
            new(
                RequestId.New(),
                Agent,
                CancellationId: CancellationId.New());

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class FakeAuthorizationConsumer(TimeProvider timeProvider)
        : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions =
            new();
        private AgentWorkspaceGraphAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _consumed;

        public bool BlockConsume { get; set; }

        public bool RevokeBeforeGrant { get; set; }

        public AgentAuthorizationError? CompletionError { get; set; }

        public TaskCompletionSource ConsumeStarted { get; private set; } =
            NewCompletionSource();

        public TaskCompletionSource ReleaseConsume { get; private set; } =
            NewCompletionSource();

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentWorkspaceGraphAction action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _authorizationId = AgentAuthorizationId.New();
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

        public void ResetBlock()
        {
            BlockConsume = false;
            ConsumeStarted = NewCompletionSource();
            ReleaseConsume = NewCompletionSource();
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
                        "The graph execution binding changed."));
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
            var revocation = new CancellationTokenSource();
            if (RevokeBeforeGrant)
            {
                revocation.Cancel();
            }

            return new AgentPermitResult.Granted(
                new AgentActionPermit(
                    new AgentActionAuthorization(
                        authorizationId,
                        action.Proposal,
                        tool!,
                        AgentAuthorizationSource.AutoPolicy,
                        new ClientId("graph-client"),
                        now.AddMinutes(1)),
                    now,
                    revocation.Token));
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            _ = permit;
            cancellationToken.ThrowIfCancellationRequested();
            _completions.Enqueue(completion);
            return ValueTask.FromResult(CompletionError);
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

        private static TaskCompletionSource NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
