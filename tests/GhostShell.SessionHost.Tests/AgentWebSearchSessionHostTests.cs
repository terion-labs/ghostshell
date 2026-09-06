using GhostShell.Application;
using GhostShell.Core;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class AgentWebSearchSessionHostTests
{
    [Fact]
    public async Task AuthorizedSearchExecutesOnceAndIsAudited()
    {
        await using var fixture = await WebSearchFixture.CreateAsync();
        var action = await fixture.PrepareAsync();

        var result = (await fixture.Client.RunAgentWebToolAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Value();

        var search = Assert.IsType<AgentWebSearchResult>(result);
        Assert.Equal("Search results", search.Title);
        Assert.Equal(1, fixture.Executor.SearchCount);
        Assert.Equal(fixture.WorkspaceId, fixture.Executor.LastWorkspaceId);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("web_search_completed", completion.StableCode);
        Assert.Equal(1, completion.ResultCount);
    }

    [Fact]
    public async Task AuthorizationMismatchPreventsBrowserExecution()
    {
        await using var fixture = await WebSearchFixture.CreateAsync();
        var action = await fixture.PrepareAsync();
        _ = fixture.Authorization.Arm(action);

        var failure = (await fixture.Client.RunAgentWebToolAsync(
            AgentAuthorizationId.New(),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, fixture.Executor.SearchCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task OneActionAuthorizationCannotBeReplayed()
    {
        await using var fixture = await WebSearchFixture.CreateAsync();
        var action = await fixture.PrepareAsync();
        var authorizationId = fixture.Authorization.Arm(action);

        _ = (await fixture.Client.RunAgentWebToolAsync(
            authorizationId,
            action,
            default)).Value();
        var replay = await fixture.Client.RunAgentWebToolAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        Assert.Equal(1, fixture.Executor.SearchCount);
    }

    private sealed class WebSearchFixture : IAsyncDisposable
    {
        private WebSearchFixture()
        {
            Clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            Composer = new AgentWebToolActionComposer();
            Executor = new RecordingWebSearchExecutor();
            Authorization = new WebSearchAuthorizationConsumer(Clock, ClientId);
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                Clock,
                agentAuthorizationConsumer: Authorization,
                agentWebToolActionComposer: Composer,
                agentWebToolExecutor: Executor);
        }

        public ManualTimeProvider Clock { get; }

        public AgentWebToolActionComposer Composer { get; }

        public RecordingWebSearchExecutor Executor { get; }

        public WebSearchAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("web-search-client");

        public WindowInstanceId WindowId { get; } = new("web-search-window");

        public WorkspaceInstanceId WorkspaceId { get; } =
            new("web-search-workspace");

        public TabInstanceId TabId { get; } = new("web-search-tab");

        public PanelInstanceId PanelId { get; } = new("web-search-panel");

        public AgentRunId RunId { get; } = new("web-search-run");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("web-search-agent"),
            ActorKind.Agent,
            "Web search agent");

        public static async ValueTask<WebSearchFixture> CreateAsync()
        {
            var fixture = new WebSearchFixture();
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    fixture.Workspace()),
                fixture.HumanContext(),
                default)).Value();
            return fixture;
        }

        public async ValueTask<AgentWebToolAction> PrepareAsync()
        {
            var target = new AgentTarget.OpenTab(
                WindowId,
                WorkspaceId,
                TabId);
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(target),
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
                new AgentWebSearchRequest("CEF offscreen", 3));
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();

        private OperationContext HumanContext() =>
            new(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(ClientId.Value),
                    ActorKind.Human,
                    "Test user",
                    ClientId),
                CancellationId: CancellationId.New());

        private OperationContext AgentContext() =>
            new(
                RequestId.New(),
                Agent,
                CancellationId: CancellationId.New());

        private WorkspaceInstance Workspace()
        {
            var panel = new PanelInstance(PanelId, PanelKind.Browser, "Web");
            var tab = new TabInstance(TabId, "Web", [panel], PanelId);
            return new WorkspaceInstance(
                WorkspaceId,
                "Workspace",
                [tab],
                TabId);
        }
    }

    private sealed class RecordingWebSearchExecutor : IAgentWebToolExecutor
    {
        public int SearchCount { get; private set; }

        public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

        public ValueTask<AgentWebToolExecutionResult> ExecuteAsync(
            WorkspaceInstanceId workspaceId,
            AgentWebToolRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            SearchCount++;
            LastWorkspaceId = workspaceId;
            return ValueTask.FromResult<AgentWebToolExecutionResult>(
                new AgentWebToolExecutionResult.Succeeded(
                    new AgentWebSearchResult(
                        "https://www.google.com/search?q=cef",
                        "Search results",
                        [
                            new AgentWebSearchEntry(
                                "https://example.test",
                                "Example",
                                "Example result"),
                        ],
                        truncated: false)));
        }
    }

    private sealed class WebSearchAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private AgentWebToolAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _consumed;

        public List<AgentActionCompletion> Completions { get; } = [];

        public AgentAuthorizationId Arm(AgentWebToolAction action)
        {
            _action = action;
            _authorizationId = AgentAuthorizationId.New();
            _consumed = 0;
            return _authorizationId;
        }

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _action
                ?? throw new InvalidOperationException("No action is armed.");
            var expected = AgentActionExecutionBinding.FromProposal(action.Proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(expected, currentBinding)
                || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationMismatch,
                            "The web search binding changed.")));
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
            Completions.Add(completion);
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
