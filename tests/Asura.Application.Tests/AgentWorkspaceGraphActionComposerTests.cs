using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentWorkspaceGraphActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void All_five_targets_project_only_their_scope_clipped_graph_shells()
    {
        var composer = new AgentWorkspaceGraphActionComposer();
        var graph = Graph();
        var terminalSession = Descriptor(
            TerminalPanel(),
            new SessionId("terminal-session"));
        var cases = new[]
        {
            (
                Target: (AgentTarget)new AgentTarget.Panel(
                    Window(),
                    Workspace(),
                    FirstTab(),
                    StatisticsPanel()),
                Panels: new[] { StatisticsPanel() },
                ExpectedKind: AgentWorkspaceGraphScopeKind.Panel,
                ExpectedTabs: 1),
            (
                Target: (AgentTarget)new AgentTarget.ConnectionSession(
                    terminalSession.Id),
                Panels: [TerminalPanel()],
                ExpectedKind: AgentWorkspaceGraphScopeKind.ConnectionSession,
                ExpectedTabs: 1),
            (
                Target: (AgentTarget)new AgentTarget.OpenTab(
                    Window(),
                    Workspace(),
                    FirstTab()),
                Panels: [TerminalPanel(), StatisticsPanel()],
                ExpectedKind: AgentWorkspaceGraphScopeKind.OpenTab,
                ExpectedTabs: 1),
            (
                Target: (AgentTarget)new AgentTarget.Workspace(
                    Window(),
                    Workspace()),
                Panels:
                [
                    TerminalPanel(),
                    StatisticsPanel(),
                    ProcessPanel(),
                    BrowserPanel(),
                ],
                ExpectedKind: AgentWorkspaceGraphScopeKind.Workspace,
                ExpectedTabs: 2),
            (
                Target: (AgentTarget)new AgentTarget.SelectedPanels(
                [
                    Exact(StatisticsPanel(), FirstTab()),
                    Exact(ProcessPanel(), SecondTab()),
                ]),
                Panels: [StatisticsPanel(), ProcessPanel()],
                ExpectedKind: AgentWorkspaceGraphScopeKind.SelectedPanels,
                ExpectedTabs: 2),
        };

        foreach (var item in cases)
        {
            var context = Context(
                graph,
                item.Target,
                item.Panels,
                terminalSession);
            var action = composer.Prepare(
                Envelope(),
                context,
                new AgentWorkspaceGraphRequest.WorkspaceInspect());
            var result =
                Assert.IsType<
                    AgentWorkspaceGraphActionResult.WorkspaceInspected>(
                    composer.Project(action, context));

            Assert.Equal(item.Target, action.Proposal.Target);
            Assert.Equal(item.ExpectedKind, result.ScopeKind);
            Assert.Equal(
                item.Target is not AgentTarget.Workspace,
                result.ScopeLimited);
            Assert.Equal(item.ExpectedTabs, result.Workspace.Tabs.Count);
            Assert.Equal(
                item.Panels,
                result.Workspace.Tabs
                    .SelectMany(tab => tab.Panels)
                    .Select(panel => panel.PanelId));
        }
    }

    [Fact]
    public void Panel_listing_is_scope_relative_and_includes_non_session_monitors()
    {
        var composer = new AgentWorkspaceGraphActionComposer();
        var graph = Graph();
        var selected = new AgentTarget.SelectedPanels(
        [
            Exact(StatisticsPanel(), FirstTab()),
            Exact(ProcessPanel(), SecondTab()),
        ]);
        var context = Context(
            graph,
            selected,
            [StatisticsPanel(), ProcessPanel()]);

        var panelAction = composer.Prepare(
            Envelope(),
            context,
            new AgentWorkspaceGraphRequest.PanelList());
        var panels =
            Assert.IsType<AgentWorkspaceGraphActionResult.PanelsListed>(
                composer.Project(panelAction, context));
        Assert.Equal(
            [PanelKind.Statistics, PanelKind.ProcessMonitor],
            panels.Page.Items.Select(panel => panel.Kind));
        Assert.Null(panels.Page.NextOffset);
        Assert.Equal(16, panels.Page.PageSize);

        Assert.DoesNotContain(
            typeof(AgentWorkspaceGraphPanel).GetProperties(),
            property => property.Name.Contains(
                "Session",
                StringComparison.Ordinal)
                || property.Name.Contains("Capabilities", StringComparison.Ordinal)
                || property.Name.Contains("Connection", StringComparison.Ordinal)
                || property.Name.Contains("WorkingDirectory", StringComparison.Ordinal)
                || property.Name.Contains("File", StringComparison.Ordinal)
                || property.Name.Contains("Browser", StringComparison.Ordinal));
    }

    [Fact]
    public void Structural_binding_ignores_descriptive_refresh_but_detects_graph_drift()
    {
        var composer = new AgentWorkspaceGraphActionComposer();
        var target = new AgentTarget.Workspace(Window(), Workspace());
        var preparedContext = Context(
            Graph(),
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel(), BrowserPanel()],
            Descriptor(
                TerminalPanel(),
                new SessionId("terminal-session"),
                SessionLifecycle.Active));
        var action = composer.Prepare(
            Envelope(),
            preparedContext,
            new AgentWorkspaceGraphRequest.PanelList());

        var refreshed = Context(
            Graph(
                revision: 19,
                sequence: 23,
                workspaceTitle: "Renamed",
                activeTab: SecondTab(),
                activePanel: ProcessPanel()),
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel(), BrowserPanel()],
            Descriptor(
                TerminalPanel(),
                new SessionId("terminal-session"),
                SessionLifecycle.Starting));
        var refreshedBinding = composer.BindForExecution(action, refreshed);

        Assert.NotEqual(
            preparedContext.BindingFingerprint,
            refreshed.BindingFingerprint);
        Assert.Equal(
            action.Proposal.TargetFingerprint,
            refreshedBinding.TargetFingerprint);

        var reordered = Context(
            Graph(firstTabPanels:
            [
                (StatisticsPanel(), PanelKind.Statistics, "Statistics"),
                (TerminalPanel(), PanelKind.Terminal, "Terminal"),
            ]),
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel(), BrowserPanel()],
            Descriptor(TerminalPanel(), new SessionId("terminal-session")));
        var kindChanged = Context(
            Graph(firstTabPanels:
            [
                (TerminalPanel(), PanelKind.Browser, "Terminal"),
                (StatisticsPanel(), PanelKind.Statistics, "Statistics"),
            ]),
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel(), BrowserPanel()]);
        var removed = Context(
            Graph(secondTabPanels:
            [
                (ProcessPanel(), PanelKind.ProcessMonitor, "Processes"),
            ]),
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel()],
            Descriptor(TerminalPanel(), new SessionId("terminal-session")));
        var addedPanel = new PanelInstanceId("panel-added");
        var added = Context(
            Graph(secondTabPanels:
            [
                (ProcessPanel(), PanelKind.ProcessMonitor, "Processes"),
                (BrowserPanel(), PanelKind.Browser, "Browser"),
                (addedPanel, PanelKind.Statistics, "Added"),
            ]),
            target,
            [
                TerminalPanel(),
                StatisticsPanel(),
                ProcessPanel(),
                BrowserPanel(),
                addedPanel,
            ],
            Descriptor(TerminalPanel(), new SessionId("terminal-session")));

        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            composer.BindForExecution(action, reordered).TargetFingerprint);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            composer.BindForExecution(action, kindChanged).TargetFingerprint);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            composer.BindForExecution(action, removed).TargetFingerprint);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            composer.BindForExecution(action, added).TargetFingerprint);
    }

    [Fact]
    public void Titles_are_redacted_and_projection_contract_rejects_oversize_results()
    {
        var composer = new AgentWorkspaceGraphActionComposer();
        var target = new AgentTarget.Workspace(Window(), Workspace());
        var secretGraph = Graph(
            workspaceTitle: "password=hunter2",
            firstTabPanels:
            [
                (TerminalPanel(), PanelKind.Terminal, "sk-1234567890123456"),
                (StatisticsPanel(), PanelKind.Statistics, "Statistics"),
            ]);
        var secretContext = Context(
            secretGraph,
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel(), BrowserPanel()],
            Descriptor(TerminalPanel(), new SessionId("terminal-session")));
        var secretAction = composer.Prepare(
            Envelope(),
            secretContext,
            new AgentWorkspaceGraphRequest.WorkspaceInspect());
        var projected =
            Assert.IsType<
                AgentWorkspaceGraphActionResult.WorkspaceInspected>(
                composer.Project(secretAction, secretContext));

        Assert.Equal(1, projected.Workspace.Workspace.Title!.Redactions);
        Assert.Equal(
            1,
            projected.Workspace.Tabs[0].Panels[0].Title!.Redactions);
        Assert.DoesNotContain(
            "hunter2",
            projected.Workspace.Workspace.Title.Text,
            StringComparison.Ordinal);

        var multibyteTitle = string.Concat(
            Enumerable.Repeat("😀", 80));
        var multibyteGraph = Graph(workspaceTitle: multibyteTitle);
        var multibyteContext = Context(
            multibyteGraph,
            target,
            [TerminalPanel(), StatisticsPanel(), ProcessPanel(), BrowserPanel()],
            Descriptor(TerminalPanel(), new SessionId("terminal-session")));
        var multibyteAction = composer.Prepare(
            Envelope(),
            multibyteContext,
            new AgentWorkspaceGraphRequest.WorkspaceInspect());
        var multibyteResult =
            Assert.IsType<
                AgentWorkspaceGraphActionResult.WorkspaceInspected>(
                composer.Project(multibyteAction, multibyteContext));
        var boundedTitle = multibyteResult.Workspace.Workspace.Title!;
        Assert.Equal(
            AgentWorkspaceGraphTitle.MaximumTextBytes,
            System.Text.Encoding.UTF8.GetByteCount(boundedTitle.Text));
        Assert.True(boundedTitle.Truncated);
        Assert.All(
            boundedTitle.Text.EnumerateRunes(),
            rune => Assert.Equal("😀", rune.ToString()));

        var largePanels = Enumerable
            .Range(0, AgentTarget.SelectedPanels.MaximumPanelCount)
            .Select(index => (
                Id: new PanelInstanceId(
                    $"panel-{index:D2}-" + new string('x', 180)),
                Kind: PanelKind.Statistics,
                Title: $"Monitor {index}"))
            .ToArray();
        var largeTab = new TabInstance(
            FirstTab(),
            "Large",
            largePanels.Select(panel =>
                new PanelInstance(panel.Id, panel.Kind, panel.Title)),
            largePanels[0].Id);
        var largeGraph = new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Large workspace",
                [largeTab],
                largeTab.Id),
            revision: 1,
            lastSequence: 1);
        var largeContext = Context(
            largeGraph,
            target,
            [.. largePanels.Select(panel => panel.Id)]);
        var largeAction = composer.Prepare(
            Envelope(),
            largeContext,
            new AgentWorkspaceGraphRequest.WorkspaceInspect());

        Assert.Throws<ArgumentException>(
            () => composer.Project(largeAction, largeContext));
    }

    [Fact]
    public void Requests_are_closed_and_offsets_are_fixed_and_bounded()
    {
        Assert.Empty(
            typeof(AgentWorkspaceGraphRequest.WorkspaceInspect)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.Equal(
            [
                typeof(AgentWorkspaceGraphRequest.PanelList),
                typeof(AgentWorkspaceGraphRequest.TabList),
                typeof(AgentWorkspaceGraphRequest.WorkspaceInspect),
            ],
            typeof(AgentWorkspaceGraphRequest)
                .GetNestedTypes()
                .Where(type => !type.IsAbstract)
                .OrderBy(type => type.Name, StringComparer.Ordinal));
        Assert.Equal(
            [0, 16, 32, 48],
            new[] { 0, 16, 32, 48 }
                .Select(offset =>
                    new AgentWorkspaceGraphRequest.PanelList(offset).Offset));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgentWorkspaceGraphRequest.TabList(1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgentWorkspaceGraphRequest.PanelList(int.MaxValue));
    }

    private static AgentContextSnapshot Context(
        WorkspaceGraphSnapshot graph,
        AgentTarget target,
        IReadOnlyList<PanelInstanceId> panelIds,
        SessionDescriptor? terminal = null)
    {
        var projected = new List<AgentContextPanel>();
        foreach (var panelId in panelIds)
        {
            var tab = graph.Workspace.Tabs.Single(candidate =>
                candidate.Panels.Any(panel => panel.Id == panelId));
            var session = panelId == TerminalPanel()
                ? terminal
                : null;
            projected.Add(AgentContextPanel.ForGraphPanel(
                graph,
                tab.Id,
                panelId,
                session));
        }

        return new AgentContextSnapshot(target, projected, Now);
    }

    private static WorkspaceGraphSnapshot Graph(
        long revision = 11,
        long sequence = 13,
        string workspaceTitle = "Workspace",
        TabInstanceId? activeTab = null,
        PanelInstanceId? activePanel = null,
        IReadOnlyList<(PanelInstanceId Id, PanelKind Kind, string Title)>?
            firstTabPanels = null,
        IReadOnlyList<(PanelInstanceId Id, PanelKind Kind, string Title)>?
            secondTabPanels = null)
    {
        firstTabPanels ??=
        [
            (TerminalPanel(), PanelKind.Terminal, "Terminal"),
            (StatisticsPanel(), PanelKind.Statistics, "Statistics"),
        ];
        secondTabPanels ??=
        [
            (ProcessPanel(), PanelKind.ProcessMonitor, "Processes"),
            (BrowserPanel(), PanelKind.Browser, "Browser"),
        ];
        var first = Tab(
            FirstTab(),
            "Primary",
            firstTabPanels,
            activeTab == FirstTab() && activePanel is { } firstActive
                ? firstActive
                : firstTabPanels[0].Id);
        var second = Tab(
            SecondTab(),
            "Secondary",
            secondTabPanels,
            activeTab == SecondTab() && activePanel is { } secondActive
                ? secondActive
                : secondTabPanels[0].Id);
        return new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                workspaceTitle,
                [first, second],
                activeTab ?? FirstTab()),
            revision,
            sequence);
    }

    private static TabInstance Tab(
        TabInstanceId id,
        string title,
        IReadOnlyList<(PanelInstanceId Id, PanelKind Kind, string Title)> panels,
        PanelInstanceId activePanel) =>
        new(
            id,
            title,
            panels.Select(panel =>
                new PanelInstance(
                    panel.Id,
                    panel.Kind,
                    panel.Title,
                    panel.Id == TerminalPanel()
                        && panel.Kind == PanelKind.Terminal
                            ? new SessionId("terminal-session")
                            : null)),
            activePanel);

    private static SessionDescriptor Descriptor(
        PanelInstanceId panelId,
        SessionId sessionId,
        SessionLifecycle lifecycle = SessionLifecycle.Active) =>
        new(
            sessionId,
            PanelKind.Terminal,
            lifecycle,
            lifecycle == SessionLifecycle.Active
                ? SessionHealth.Healthy
                : SessionHealth.Starting,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                FirstTab(),
                panelId),
            CapabilitySet.Empty,
            Revision: 7,
            HasActiveWork: false,
            StatusDetail: "Ready");

    private static AgentActionEnvelope Envelope() =>
        new(
            AgentActionId.New(),
            new AgentRunId("graph-run"),
            new ActorDescriptor(
                new ActorId("graph-agent"),
                ActorKind.Agent,
                "Graph agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentTarget.Panel Exact(
        PanelInstanceId panelId,
        TabInstanceId tabId) =>
        new(Window(), Workspace(), tabId, panelId);

    private static WindowInstanceId Window() => new("graph-window");

    private static WorkspaceInstanceId Workspace() => new("graph-workspace");

    private static TabInstanceId FirstTab() => new("tab-primary");

    private static TabInstanceId SecondTab() => new("tab-secondary");

    private static PanelInstanceId TerminalPanel() => new("panel-terminal");

    private static PanelInstanceId StatisticsPanel() => new("panel-statistics");

    private static PanelInstanceId ProcessPanel() => new("panel-process");

    private static PanelInstanceId BrowserPanel() => new("panel-browser");
}
