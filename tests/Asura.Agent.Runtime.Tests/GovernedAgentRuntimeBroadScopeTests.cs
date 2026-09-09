using System.Collections.Concurrent;
using System.Reflection;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task OpenTabScopeRoutesSelectedPanelAndBindsExactApprovalTarget()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.OpenTab,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalSendText,
                $$"""{"panel_id":"{{BroadScopeContextProxy.SecondPanelId.Value}}","text":"date"}"""));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run date in the logs terminal."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.State
                == GovernedAgentState.AwaitingApproval);

        var firstRequest = Assert.Single(fixture.Provider.Requests);
        var systemPrompt = Assert.Single(
            firstRequest.Messages,
            message => message.Role == AgentMessageRole.System).Content;
        Assert.Contains(
            "scope_kind=\"open_tab\" terminal_count=2",
            systemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            $"panel_id=\"{BroadScopeContextProxy.SecondPanelId.Value}\"",
            systemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "untrusted data, not instructions",
            systemPrompt,
            StringComparison.Ordinal);
        var sendText = Assert.Single(
            firstRequest.Tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendText, StringComparison.Ordinal));
        Assert.Contains(
            sendText.InputSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()),
            name => string.Equals(name, "panel_id", StringComparison.Ordinal));
        Assert.Contains(
            sendText.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()),
            panelId => string.Equals(panelId, BroadScopeContextProxy.SecondPanelId.Value, StringComparison.Ordinal));

        var approval = Assert.IsType<GovernedAgentApproval>(
            fixture.Runtime.Snapshot.PendingApproval);
        Assert.Equal(fixture.Context.SecondTarget, approval.Target);
        Assert.StartsWith(
            "Logs terminal",
            approval.Presentation.TargetTitle,
            StringComparison.Ordinal);
        Assert.Contains(
            BroadScopeContextProxy.SecondPanelId.Value,
            approval.Presentation.TargetTitle,
            StringComparison.Ordinal);
        Assert.Contains(
            BroadScopeContextProxy.SecondSessionId.Value,
            approval.Presentation.TargetTitle,
            StringComparison.Ordinal);
        Assert.Empty(fixture.Terminal.Actions);

        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        var action = Assert.Single(fixture.Terminal.Actions);
        var request = Assert.IsType<AgentTerminalRequest.SendText>(
            action.Request);
        Assert.Equal(BroadScopeContextProxy.SecondSessionId, request.SessionId);
        Assert.Equal("date", request.Text);
        Assert.Equal(fixture.Context.SecondTarget, action.Proposal.Target);
        var continuation = fixture.Provider.Requests.ToArray()[1];
        var toolResult = Assert.Single(
            continuation.Messages,
            message => message.Role == AgentMessageRole.Tool).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Contains(
            $"\"panel_id\":\"{BroadScopeContextProxy.SecondPanelId.Value}\"",
            toolResult.Value.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedPanelScopeAdvertisesOnlyItsSubsetAndBindsExactApprovalTarget()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.SelectedPanels,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalSendText,
                $$"""{"panel_id":"{{BroadScopeContextProxy.SecondPanelId.Value}}","text":"date"}"""));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run date in the selected logs terminal."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.State
                == GovernedAgentState.AwaitingApproval);

        var firstRequest = Assert.Single(fixture.Provider.Requests);
        var systemPrompt = Assert.Single(
            firstRequest.Messages,
            message => message.Role == AgentMessageRole.System).Content;
        Assert.Contains(
            "scope_kind=\"selected_panels\" terminal_count=2",
            systemPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            BroadScopeContextProxy.ThirdPanelId.Value,
            systemPrompt,
            StringComparison.Ordinal);

        var sendText = Assert.Single(
            firstRequest.Tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendText, StringComparison.Ordinal));
        var advertisedPanelIds = sendText.InputSchema
            .GetProperty("properties")
            .GetProperty("panel_id")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(
            [
                BroadScopeContextProxy.FirstPanelId.Value,
                BroadScopeContextProxy.SecondPanelId.Value,
            ],
            advertisedPanelIds);
        Assert.DoesNotContain(
            BroadScopeContextProxy.ThirdPanelId.Value,
            advertisedPanelIds, StringComparer.Ordinal);

        var approval = Assert.IsType<GovernedAgentApproval>(
            fixture.Runtime.Snapshot.PendingApproval);
        Assert.Equal(fixture.Context.SecondTarget, approval.Target);
        Assert.Contains(
            BroadScopeContextProxy.SecondSessionId.Value,
            approval.Presentation.TargetTitle,
            StringComparison.Ordinal);
        Assert.Empty(fixture.Terminal.Actions);

        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        var action = Assert.Single(fixture.Terminal.Actions);
        var request = Assert.IsType<AgentTerminalRequest.SendText>(
            action.Request);
        Assert.Equal(BroadScopeContextProxy.SecondSessionId, request.SessionId);
        Assert.Equal(fixture.Context.SecondTarget, action.Proposal.Target);
    }

    [Fact]
    public async Task SelectedPanelScopeRejectsAnUnselectedLivePanelBeforeAuthorization()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.SelectedPanels,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.ThirdPanelId.Value}}"}"""));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect an unselected terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action, BuiltInAgentTools.TerminalReadScreen, StringComparison.Ordinal));
        var continuation = fixture.Provider.Requests.ToArray()[1];
        var toolResult = Assert.Single(
            continuation.Messages,
            message => message.Role == AgentMessageRole.Tool).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal("invalid_tool_arguments", toolResult.StableCode);
    }

    [Fact]
    public async Task SelectedPanelScopeRejectsAPartiallyUnlinkedSetBeforeAnyAuthority()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.SelectedPanels,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.FirstPanelId.Value}}"}"""));
        fixture.Context.UnlinkSecondPanel();

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the selected terminals."),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_target_unavailable", result.Code);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.Audit.Events);
        Assert.Empty(fixture.Terminal.Actions);
    }

    [Theory]
    [InlineData(SelectedScopeDrift.SessionReplacement, "agent_target_changed")]
    [InlineData(SelectedScopeDrift.PanelRemoval, "agent_target_unavailable")]
    [InlineData(SelectedScopeDrift.PanelUnlinked, "agent_target_unavailable")]
    public async Task SelectedPanelScopeDriftRequiresClearingTheRun(
        SelectedScopeDrift drift,
        string expectedCode)
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.SelectedPanels,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.FirstPanelId.Value}}"}"""));

        var first = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the selected operations terminal."),
            CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.Single(fixture.Terminal.Actions);
        Assert.Equal(2, fixture.Provider.Requests.Count);

        switch (drift)
        {
            case SelectedScopeDrift.SessionReplacement:
                fixture.Context.ReplaceSecondSession();
                break;
            case SelectedScopeDrift.PanelRemoval:
                fixture.Context.RemoveSecondPanel();
                break;
            case SelectedScopeDrift.PanelUnlinked:
                fixture.Context.UnlinkSecondPanel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }

        var second = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the same selected terminals again."),
            CancellationToken.None);

        Assert.False(second.IsSuccess);
        Assert.Equal(expectedCode, second.Code);
        Assert.Equal(GovernedAgentState.Failed, fixture.Runtime.Snapshot.State);
        Assert.Single(fixture.Terminal.Actions);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.True(await fixture.Runtime.ClearAsync(CancellationToken.None));
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
    }

    [Fact]
    public async Task WorkspaceScopeRejectsOutOfScopePanelWithoutHostExecution()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.Workspace,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                """{"panel_id":"outside-this-workspace"}"""));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the outside terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
        var continuation = fixture.Provider.Requests.ToArray()[1];
        var toolResult = Assert.Single(
            continuation.Messages,
            message => message.Role == AgentMessageRole.Tool).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal("invalid_tool_arguments", toolResult.StableCode);
    }

    [Fact]
    public async Task BroadScopeBindingChangeRebindsTheLivePanelBeforeExecution()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.OpenTab,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.SecondPanelId.Value}}"}"""));
        fixture.Context.ReplaceSecondSessionAfterInspection = 1;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the logs terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var action = Assert.Single(fixture.Terminal.Actions);
        Assert.Equal(
            BroadScopeContextProxy.ReplacementSecondSessionId,
            Assert.IsType<AgentTerminalRequest.ReadScreen>(action.Request)
                .SessionId);
        Assert.Equal(fixture.Context.SecondTarget, action.Proposal.Target);
        var continuation = fixture.Provider.Requests.ToArray()[1];
        var toolResult = Assert.Single(
            continuation.Messages,
            message => message.Role == AgentMessageRole.Tool).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal("tool_succeeded", toolResult.StableCode);
    }

    [Fact]
    public async Task BroadScopeFullAccessIsBoundToTheConfirmedRunScope()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.Workspace,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.FirstPanelId.Value}}"}"""));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(Screen("ready")));
        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the operations terminal."),
            CancellationToken.None)).IsSuccess);

        var result = await fixture.Runtime.EnableYoloAsync(
            TimeSpan.FromMinutes(15),
            CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Equal("yolo_enabled", result.Code);
        Assert.Equal(
            fixture.Context.ScopeTarget,
            fixture.Runtime.Snapshot.YoloAuthority?.Target);
        Assert.Equal(
            AgentPermission.Yolo,
            fixture.Runtime.Snapshot.TerminalMutationPermission);
    }

    private static ProviderRound ToolThenAnswer(
        string toolName,
        string arguments) =>
        new((call, request) => call switch
        {
            1 =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ToolCallStarted(
                    0,
                    "broad-scope-call",
                    ProviderToolName.FromInternal(toolName)),
                new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments),
                new AgentProviderEvent.ToolCallCompleted(0),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse),
            ],
            2 when request.Messages.Any(
                message => message.Role == AgentMessageRole.Tool) =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.TextDelta("The request was handled."),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn),
            ],
            _ => throw new InvalidOperationException(
                "The broad-scope provider received an unexpected round."),
        });

    private static TerminalScreenSnapshot Screen(string text) =>
        new(
            text,
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: false,
            WorkingDirectory: "/srv/operations",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            ContentRevision: 1,
            WindowTitle: "operations");

    internal enum ScopeKind
    {
        OpenTab,
        Workspace,
        SelectedPanels,
    }

    public enum SelectedScopeDrift
    {
        SessionReplacement,
        PanelRemoval,
        PanelUnlinked,
    }

    private sealed class BroadScopeFixture : IAsyncDisposable
    {
        private BroadScopeFixture(
            ISessionHostClient sessionHost,
            BroadScopeContextProxy context,
            ProviderRound provider)
        {
            Context = context;
            Provider = provider;
            Audit = new RecordingAuditStore();
            Broker = new AgentCapabilityBroker(
                BuiltInAgentTools.Catalog,
                Audit,
                TimeProvider.System);
            var composer = new AgentTerminalActionComposer();
            Terminal = new BroadScopeTerminalHost(Broker, composer, context);
            Runtime = new GovernedAgentRuntime(
                sessionHost,
                Broker,
                Terminal,
                composer,
                BuiltInAgentTools.Catalog,
                new FixedProviderResolver(provider),
                new TestApprovalPrincipal(new ClientId("broad-scope-client")),
                TimeProvider.System,
                AgentPolicy.Default);
        }

        public BroadScopeContextProxy Context { get; }

        public ProviderRound Provider { get; }

        public RecordingAuditStore Audit { get; }

        public AgentCapabilityBroker Broker { get; }

        public BroadScopeTerminalHost Terminal { get; }

        public GovernedAgentRuntime Runtime { get; }

        public static BroadScopeFixture Create(
            ScopeKind scopeKind,
            ProviderRound provider)
        {
            var sessionHost =
                DispatchProxy.Create<ISessionHostClient, BroadScopeContextProxy>();
            var context = (BroadScopeContextProxy)(object)sessionHost;
            context.Initialize(scopeKind);
            return new BroadScopeFixture(sessionHost, context, provider);
        }

        public GovernedAgentPrompt Prompt(string message) =>
            new(
                new AiProviderProfileId("provider-1"),
                message,
                Context.CreatePromptTarget(),
                Runtime.Snapshot.EffectivePolicy!.SelectPrimaryModel(
                    "provider-1",
                    "provider-default-model"));

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Broker.DisposeAsync();
        }
    }

    public class BroadScopeContextProxy : DispatchProxy
    {
        public static readonly WindowInstanceId WindowId = new("broad-window");
        public static readonly WorkspaceInstanceId WorkspaceId =
            new("broad-workspace");
        public static readonly TabInstanceId TabId = new("broad-tab");
        public static readonly PanelInstanceId FirstPanelId =
            new("operations-panel");
        public static readonly PanelInstanceId SecondPanelId =
            new("logs-panel");
        public static readonly PanelInstanceId ThirdPanelId =
            new("unselected-panel");
        public static readonly SessionId FirstSessionId =
            new("operations-session");
        public static readonly SessionId SecondSessionId =
            new("logs-session");
        public static readonly SessionId ThirdSessionId =
            new("unselected-session");
        public static readonly SessionId ReplacementSecondSessionId =
            new("replacement-logs-session");

        private int _inspectionCount;
        private SessionId _currentSecondSessionId = SecondSessionId;
        private bool _reversePanels;
        private bool _secondPanelRemoved;
        private bool _secondPanelUnlinked;
        private ScopeKind _scopeKind;

        public AgentTarget ScopeTarget { get; private set; } = null!;

        public AgentTarget.Panel SecondTarget =>
            new(WindowId, WorkspaceId, TabId, SecondPanelId);

        public int ReplaceSecondSessionAfterInspection { get; set; } =
            int.MaxValue;

        public int ReversePanelsAfterInspection { get; set; } =
            int.MaxValue;

        internal void Initialize(ScopeKind scopeKind)
        {
            _scopeKind = scopeKind;
            ScopeTarget = scopeKind switch
            {
                ScopeKind.OpenTab =>
                    new AgentTarget.OpenTab(WindowId, WorkspaceId, TabId),
                ScopeKind.Workspace =>
                    new AgentTarget.Workspace(WindowId, WorkspaceId),
                ScopeKind.SelectedPanels =>
                    new AgentTarget.SelectedPanels(
                    [
                        new AgentTarget.Panel(
                            WindowId,
                            WorkspaceId,
                            TabId,
                            SecondPanelId),
                        new AgentTarget.Panel(
                            WindowId,
                            WorkspaceId,
                            TabId,
                            FirstPanelId),
                    ]),
                _ => throw new ArgumentOutOfRangeException(nameof(scopeKind)),
            };
        }

        public AgentTarget CreatePromptTarget() =>
            ScopeTarget is AgentTarget.SelectedPanels selected
                ? new AgentTarget.SelectedPanels(selected.Panels.Reverse())
                : ScopeTarget;

        public void ReplaceSecondSession() =>
            _currentSecondSessionId = ReplacementSecondSessionId;

        public void RemoveSecondPanel() =>
            _secondPanelRemoved = true;

        public void UnlinkSecondPanel() =>
            _secondPanelUnlinked = true;

        public AgentContextSnapshot CurrentContext(AgentTarget target)
        {
            var graph = CreateGraph();
            var panels = CreatePanels(graph);
            if (target == ScopeTarget)
            {
                var scopedPanels = target is AgentTarget.SelectedPanels selected
                    ? SelectPanels(selected, panels)
                    : panels;
                return new AgentContextSnapshot(
                    target,
                    scopedPanels,
                    DateTimeOffset.UtcNow);
            }

            if (target is not AgentTarget.Panel exactTarget)
            {
                throw new ArgumentException(
                    "The requested target is outside the test scope.",
                    nameof(target));
            }

            var panel = panels.Single(candidate =>
                candidate.WindowId == exactTarget.WindowId
                && candidate.WorkspaceId == exactTarget.WorkspaceId
                && candidate.TabId == exactTarget.TabId
                && candidate.PanelId == exactTarget.PanelId);
            return new AgentContextSnapshot(
                exactTarget,
                [panel],
                DateTimeOffset.UtcNow);
        }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(ISessionHostClient.InspectAgentContextAsync)
                    when args is
                    [
                        AgentContextRequest request,
                        OperationContext _,
                        CancellationToken cancellationToken,
                    ] => InspectAsync(request, cancellationToken),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private ValueTask<HostResult<AgentContextSnapshot>> InspectAsync(
            AgentContextRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = Interlocked.Increment(ref _inspectionCount);
            if (inspection > ReplaceSecondSessionAfterInspection)
            {
                _currentSecondSessionId = ReplacementSecondSessionId;
            }

            _reversePanels = inspection > ReversePanelsAfterInspection;
            AgentContextSnapshot snapshot;
            try
            {
                snapshot = CurrentContext(request.Target);
            }
            catch (ArgumentException)
            {
                return ValueTask.FromResult(
                    HostResult<AgentContextSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.NotFound,
                            "Target unavailable."),
                        0));
            }

            return ValueTask.FromResult(
                HostResult<AgentContextSnapshot>.Succeed(
                    snapshot,
                    snapshot.Revision));
        }

        private WorkspaceGraphSnapshot CreateGraph()
        {
            var first = new PanelInstance(
                FirstPanelId,
                PanelKind.Terminal,
                "Operations terminal",
                FirstSessionId);
            var second = new PanelInstance(
                SecondPanelId,
                PanelKind.Terminal,
                "Logs terminal",
                _secondPanelUnlinked
                    ? null
                    : _currentSecondSessionId);
            var third = new PanelInstance(
                ThirdPanelId,
                PanelKind.Terminal,
                "Unselected terminal",
                ThirdSessionId);
            var panels = new List<PanelInstance> { first };
            if (!_secondPanelRemoved)
            {
                panels.Add(second);
            }

            if (_scopeKind == ScopeKind.SelectedPanels)
            {
                panels.Add(third);
            }

            var tab = new TabInstance(
                TabId,
                "Operations",
                panels,
                first.Id);
            var workspace = new WorkspaceInstance(
                WorkspaceId,
                "Production workspace",
                [tab],
                tab.Id);
            return new WorkspaceGraphSnapshot(
                WindowId,
                workspace,
                revision: _currentSecondSessionId == SecondSessionId
                    && !_secondPanelRemoved
                    && !_secondPanelUnlinked
                        ? 3
                        : 4,
                lastSequence: 3);
        }

        private IReadOnlyList<AgentContextPanel> CreatePanels(
            WorkspaceGraphSnapshot graph)
        {
            var panels = new List<AgentContextPanel>
            {
                AgentContextPanel.ForGraphPanel(
                    graph,
                    TabId,
                    FirstPanelId,
                    Descriptor(
                        FirstSessionId,
                        FirstPanelId,
                        revision: 5,
                        "SSH · operations")),
            };
            if (!_secondPanelRemoved)
            {
                panels.Add(AgentContextPanel.ForGraphPanel(
                    graph,
                    TabId,
                    SecondPanelId,
                    _secondPanelUnlinked
                        ? null
                        : Descriptor(
                            _currentSecondSessionId,
                            SecondPanelId,
                            revision: _currentSecondSessionId == SecondSessionId ? 5 : 6,
                            "SSH · logs")));
            }

            if (_scopeKind == ScopeKind.SelectedPanels)
            {
                panels.Add(AgentContextPanel.ForGraphPanel(
                    graph,
                    TabId,
                    ThirdPanelId,
                    Descriptor(
                        ThirdSessionId,
                        ThirdPanelId,
                        revision: 5,
                        "SSH · unselected")));
            }

            if (_reversePanels)
            {
                panels.Reverse();
            }

            return panels;
        }

        private static IReadOnlyList<AgentContextPanel> SelectPanels(
            AgentTarget.SelectedPanels selected,
            IReadOnlyList<AgentContextPanel> panels)
        {
            var resolved = panels
                .Where(panel => selected.Panels.Any(target =>
                    target.WindowId == panel.WindowId
                    && target.WorkspaceId == panel.WorkspaceId
                    && target.TabId == panel.TabId
                    && target.PanelId == panel.PanelId))
                .ToArray();
            if (resolved.Length != selected.Panels.Count)
            {
                throw new ArgumentException(
                    "A selected test panel is no longer present.",
                    nameof(selected));
            }

            return resolved;
        }

        private static SessionDescriptor Descriptor(
            SessionId sessionId,
            PanelInstanceId panelId,
            long revision,
            string connectionBoundary) =>
            new(
                sessionId,
                PanelKind.Terminal,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                new SessionOwner(
                    HostMode.Desktop,
                    WindowId,
                    WorkspaceId,
                    TabId,
                    panelId),
                new CapabilitySet(
                [
                    SessionCapabilities.ManagedRenderer,
                    SessionCapabilities.TerminalAgentInputBarrier,
                    SessionCapabilities.TerminalReadScreen,
                    SessionCapabilities.TerminalWait,
                    SessionCapabilities.TerminalWrite,
                    SessionCapabilities.TerminalSendKeys,
                    SessionCapabilities.TerminalInterrupt,
                ]),
                revision,
                HasActiveWork: false,
                StatusDetail: "Ready",
                TerminalMetadata: new TerminalSessionMetadata(
                    connectionId: null,
                    connectionBoundary,
                    initialWorkingDirectory: "/srv/operations",
                    currentWorkingDirectory: "/srv/operations"));
    }

    private sealed class BroadScopeTerminalHost(
        IAgentCapabilityBroker broker,
        AgentTerminalActionComposer composer,
        BroadScopeContextProxy context)
        : IAgentTerminalSessionHost
    {
        public ConcurrentQueue<AgentTerminalActionResult> Results { get; } = [];

        public ConcurrentQueue<AgentTerminalAction> Actions { get; } = [];

        public async ValueTask<HostResult<AgentTerminalActionResult>>
            RunAgentTerminalActionAsync(
                AgentAuthorizationId authorizationId,
                AgentTerminalAction action,
                CancellationToken cancellationToken)
        {
            var binding = composer.BindForExecution(
                action,
                context.CurrentContext(action.Proposal.Target));
            var consumed = await broker.ConsumeAsync(
                authorizationId,
                binding,
                cancellationToken);
            if (consumed is AgentPermitResult.Denied denied)
            {
                return HostResult<AgentTerminalActionResult>.Fail(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        denied.Error.Code.ToString().ToLowerInvariant(),
                        "Denied."),
                    1);
            }

            var permit = ((AgentPermitResult.Granted)consumed).Permit;
            Actions.Enqueue(action);
            var result = Results.TryDequeue(out var queued)
                ? queued
                : new AgentTerminalActionResult.Completed();
            var completion = await broker.CompleteAsync(
                permit,
                new AgentActionCompletion(
                    AgentActionOutcome.Succeeded,
                    "ok",
                    DateTimeOffset.UtcNow),
                cancellationToken);
            Assert.Null(completion);
            return HostResult<AgentTerminalActionResult>.Succeed(result, 1);
        }
    }
}
