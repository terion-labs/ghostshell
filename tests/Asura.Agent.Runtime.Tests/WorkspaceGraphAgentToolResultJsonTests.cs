using System.Reflection;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class WorkspaceGraphAgentToolResultJsonTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WorkspaceInspectShapeNestsOnlyTabsAndPanelsInsideTheRunScope()
    {
        var fixture = GraphFixture.Create();
        var result = fixture.Project(
            new AgentTarget.OpenTab(
                fixture.WindowId,
                fixture.WorkspaceId,
                fixture.FirstTabId),
            fixture.FirstTabPanels,
            new AgentWorkspaceGraphRequest.WorkspaceInspect());

        var projection = WorkspaceGraphAgentToolResultJson.Project(result);

        Assert.True(projection.IsSuccess);
        Assert.Equal("workspace_inspected", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        AssertRoot(
            root,
            "open_tab",
            scopeLimited: true,
            "workspace");
        var workspace = root.GetProperty("workspace");
        Assert.Equal(
            [
                "window_id",
                "workspace_id",
                "title",
                "tabs",
            ],
            workspace.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        var tab = Assert.Single(
            workspace.GetProperty("tabs").EnumerateArray());
        AssertTabShape(
            tab,
            includesPanels: true,
            includesGraphClock: false);
        Assert.Equal(
            fixture.FirstTabPanels.Select(panel => panel.Value),
            tab.GetProperty("panels")
                .EnumerateArray()
                .Select(panel =>
                    panel.GetProperty("panel_id").GetString()), StringComparer.Ordinal);
        AssertNoSensitiveOrOutOfScopeFields(root);
    }

    [Fact]
    public void TabListShapeCarriesAClosedPageReceipt()
    {
        var fixture = GraphFixture.Create();
        var selected = new AgentTarget.SelectedPanels(
        [
            fixture.Exact(fixture.StatisticsPanelId, fixture.FirstTabId),
            fixture.Exact(fixture.BrowserPanelId, fixture.SecondTabId),
        ]);
        var result = fixture.Project(
            selected,
            [fixture.StatisticsPanelId, fixture.BrowserPanelId],
            new AgentWorkspaceGraphRequest.TabList());

        var projection = WorkspaceGraphAgentToolResultJson.Project(result);

        Assert.True(projection.IsSuccess);
        Assert.Equal("tabs_listed", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        AssertRoot(
            root,
            "selected_panels",
            scopeLimited: true,
            "page");
        var page = root.GetProperty("page");
        AssertPageReceipt(
            page,
            offset: 0,
            returned: 2,
            nextOffset: null);
        Assert.All(
            page.GetProperty("items").EnumerateArray(),
            tab => AssertTabShape(
                tab,
                includesPanels: false,
                includesGraphClock: false));
        AssertNoSensitiveOrOutOfScopeFields(root);
    }

    [Fact]
    public void PanelListShapeContainsOnlyStructuralPanelMetadata()
    {
        var fixture = GraphFixture.Create();
        var result = fixture.Project(
            fixture.Exact(
                fixture.StatisticsPanelId,
                fixture.FirstTabId),
            [fixture.StatisticsPanelId],
            new AgentWorkspaceGraphRequest.PanelList());

        var projection = WorkspaceGraphAgentToolResultJson.Project(result);

        Assert.True(projection.IsSuccess);
        Assert.Equal("panels_listed", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        AssertRoot(root, "panel", scopeLimited: true, "page");
        var page = root.GetProperty("page");
        AssertPageReceipt(
            page,
            offset: 0,
            returned: 1,
            nextOffset: null);
        AssertPanelShape(
            Assert.Single(page.GetProperty("items").EnumerateArray()),
            includesGraphClock: false);
        AssertNoSensitiveOrOutOfScopeFields(root);
    }

    [Fact]
    public void ScopeLabelsCoverEveryFixedRunTargetWithoutChangingThePayloadShape()
    {
        var fixture = GraphFixture.Create();
        var cases = new[]
        {
            (
                Target: (AgentTarget)fixture.Exact(
                    fixture.StatisticsPanelId,
                    fixture.FirstTabId),
                Panels: new[] { fixture.StatisticsPanelId },
                Scope: "panel",
                Limited: true),
            (
                Target: (AgentTarget)new AgentTarget.ConnectionSession(
                    fixture.TerminalSessionId),
                Panels: new[] { fixture.TerminalPanelId },
                Scope: "connection_session",
                Limited: true),
            (
                Target: (AgentTarget)new AgentTarget.OpenTab(
                    fixture.WindowId,
                    fixture.WorkspaceId,
                    fixture.FirstTabId),
                Panels: fixture.FirstTabPanels,
                Scope: "open_tab",
                Limited: true),
            (
                Target: (AgentTarget)new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                Panels: fixture.AllPanels,
                Scope: "workspace",
                Limited: false),
            (
                Target: (AgentTarget)new AgentTarget.SelectedPanels(
                [
                    fixture.Exact(
                        fixture.StatisticsPanelId,
                        fixture.FirstTabId),
                    fixture.Exact(
                        fixture.BrowserPanelId,
                        fixture.SecondTabId),
                ]),
                Panels: new[]
                {
                    fixture.StatisticsPanelId,
                    fixture.BrowserPanelId,
                },
                Scope: "selected_panels",
                Limited: true),
        };

        foreach (var item in cases)
        {
            var result = fixture.Project(
                item.Target,
                item.Panels,
                new AgentWorkspaceGraphRequest.WorkspaceInspect());
            var projection = WorkspaceGraphAgentToolResultJson.Project(result);
            using var document = JsonDocument.Parse(projection.Json);

            Assert.Equal(
                item.Scope,
                document.RootElement.GetProperty("scope_kind").GetString());
            Assert.Equal(
                item.Limited,
                document.RootElement
                    .GetProperty("scope_limited")
                    .GetBoolean());
            Assert.True(
                document.RootElement.TryGetProperty("workspace", out _));
        }
    }

    [Fact]
    public void PageReceiptReportsTheFixedPageAndContinuationWithoutTotals()
    {
        var fixture = GraphFixture.Create(panelCount: 17);
        var target = new AgentTarget.Workspace(
            fixture.WindowId,
            fixture.WorkspaceId);

        var first = WorkspaceGraphAgentToolResultJson.Project(
            fixture.Project(
                target,
                fixture.AllPanels,
                new AgentWorkspaceGraphRequest.PanelList()));
        var second = WorkspaceGraphAgentToolResultJson.Project(
            fixture.Project(
                target,
                fixture.AllPanels,
                new AgentWorkspaceGraphRequest.PanelList(16)));

        using var firstDocument = JsonDocument.Parse(first.Json);
        AssertPageReceipt(
            firstDocument.RootElement.GetProperty("page"),
            offset: 0,
            returned: 16,
            nextOffset: 16);
        using var secondDocument = JsonDocument.Parse(second.Json);
        AssertPageReceipt(
            secondDocument.RootElement.GetProperty("page"),
            offset: 16,
            returned: 1,
            nextOffset: null);
        AssertNoSensitiveOrOutOfScopeFields(firstDocument.RootElement);
        AssertNoSensitiveOrOutOfScopeFields(secondDocument.RootElement);
    }

    [Fact]
    public void TitlesAreSecretRedactedAndUtf8TruncatedAtExactly128Bytes()
    {
        var longPanelTitle = string.Concat(Enumerable.Repeat("😀", 40));
        var fixture = GraphFixture.Create(
            workspaceTitle: "password=secret-canary",
            statisticsTitle: longPanelTitle);
        var result = fixture.Project(
            new AgentTarget.OpenTab(
                fixture.WindowId,
                fixture.WorkspaceId,
                fixture.FirstTabId),
            fixture.FirstTabPanels,
            new AgentWorkspaceGraphRequest.WorkspaceInspect());

        var projection = WorkspaceGraphAgentToolResultJson.Project(result);

        Assert.DoesNotContain(
            "secret-canary",
            projection.Json,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(projection.Json);
        var workspace = document.RootElement.GetProperty("workspace");
        var workspaceTitle = workspace.GetProperty("title");
        Assert.Equal(
            "[REDACTED SECRET-BEARING TITLE]",
            workspaceTitle.GetProperty("text").GetString());
        Assert.Equal(
            1,
            workspaceTitle.GetProperty("redactions").GetInt32());
        Assert.False(
            workspaceTitle.GetProperty("truncated").GetBoolean());

        var projectedPanelTitle = workspace
            .GetProperty("tabs")[0]
            .GetProperty("panels")[1]
            .GetProperty("title");
        var text = projectedPanelTitle.GetProperty("text").GetString()!;
        Assert.Equal(
            AgentWorkspaceGraphTitle.MaximumTextBytes,
            Encoding.UTF8.GetByteCount(text));
        Assert.Equal(32, text.EnumerateRunes().Count());
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Equal(
            0,
            projectedPanelTitle.GetProperty("redactions").GetInt32());
        Assert.True(
            projectedPanelTitle.GetProperty("truncated").GetBoolean());

        foreach (var unsafeTitle in new[]
                 {
                     "Operations\u200Dhidden",
                     "Operations\u2028hidden",
                     "Operations\u2029hidden",
                     "Operations\uD800hidden",
                     "Operations\uDC00hidden",
                 })
        {
            var unsafeFixture = GraphFixture.Create(
                workspaceTitle: unsafeTitle);
            var unsafeResult = unsafeFixture.Project(
                new AgentTarget.Workspace(
                    unsafeFixture.WindowId,
                    unsafeFixture.WorkspaceId),
                unsafeFixture.AllPanels,
                new AgentWorkspaceGraphRequest.WorkspaceInspect());
            var unsafeProjection =
                WorkspaceGraphAgentToolResultJson.Project(unsafeResult);
            using var unsafeDocument = JsonDocument.Parse(
                unsafeProjection.Json);
            var title = unsafeDocument.RootElement
                .GetProperty("workspace")
                .GetProperty("title");

            Assert.Equal(
                "[REDACTED SECRET-BEARING TITLE]",
                title.GetProperty("text").GetString());
            Assert.Equal(1, title.GetProperty("redactions").GetInt32());
            Assert.DoesNotContain(
                unsafeTitle,
                unsafeProjection.Json,
                StringComparison.Ordinal);
        }

        foreach (var literalSecretTitle in new[]
                 {
                     "https://alice:hunter2@example.test",
                     "curl --token hunter2",
                 })
        {
            var secretFixture = GraphFixture.Create(
                workspaceTitle: literalSecretTitle);
            var secretResult = secretFixture.Project(
                new AgentTarget.Workspace(
                    secretFixture.WindowId,
                    secretFixture.WorkspaceId),
                secretFixture.AllPanels,
                new AgentWorkspaceGraphRequest.WorkspaceInspect());
            var secretProjection =
                WorkspaceGraphAgentToolResultJson.Project(secretResult);
            using var secretDocument = JsonDocument.Parse(
                secretProjection.Json);
            var title = secretDocument.RootElement
                .GetProperty("workspace")
                .GetProperty("title");

            Assert.Equal(
                "[REDACTED SECRET-BEARING TITLE]",
                title.GetProperty("text").GetString());
            Assert.Equal(1, title.GetProperty("redactions").GetInt32());
            Assert.DoesNotContain(
                "hunter2",
                secretProjection.Json,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FinalJsonGuardAcceptsExactly64KiBAndRejectsOneByteMore()
    {
        var baseline = WorkspaceGraphAgentToolResultJson.Project(
            WorkspaceInspectedWithWindowId("x"));
        Assert.True(baseline.IsSuccess);
        var baselineBytes = Encoding.UTF8.GetByteCount(baseline.Json);
        var limit = AgentKernelLimits.Default.MaximumToolResultBytes;
        Assert.Equal(
            AgentWorkspaceGraphActionResult.MaximumProjectionBytes,
            limit);

        var exactWindowId = new string(
            'x',
            limit - baselineBytes + 1);
        var exact = WorkspaceGraphAgentToolResultJson.Project(
            WorkspaceInspectedWithWindowId(exactWindowId));
        var oneByteOver = WorkspaceGraphAgentToolResultJson.Project(
            WorkspaceInspectedWithWindowId(exactWindowId + "x"));

        Assert.True(exact.IsSuccess);
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(exact.Json));
        Assert.False(oneByteOver.IsSuccess);
        Assert.Equal(
            "workspace_graph_limit_exceeded",
            oneByteOver.StableCode);
        Assert.DoesNotContain(
            exactWindowId,
            oneByteOver.Json,
            StringComparison.Ordinal);
    }

    private static void AssertRoot(
        JsonElement root,
        string scopeKind,
        bool scopeLimited,
        string payloadProperty)
    {
        Assert.Equal(
            [
                "ok",
                "content_origin",
                "scope_kind",
                "scope_limited",
                payloadProperty,
            ],
            root.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "untrusted_workspace_graph_metadata",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(
            scopeKind,
            root.GetProperty("scope_kind").GetString());
        Assert.Equal(
            scopeLimited,
            root.GetProperty("scope_limited").GetBoolean());
    }

    private static void AssertTabShape(
        JsonElement tab,
        bool includesPanels,
        bool includesGraphClock)
    {
        var expected = new List<string>
        {
            "window_id",
            "workspace_id",
        };
        if (includesGraphClock)
        {
            expected.Add("workspace_revision");
            expected.Add("graph_sequence");
        }

        expected.Add("tab_id");
        expected.Add("active");
        expected.Add("title");
        if (includesPanels)
        {
            expected.Add("panels");
        }

        Assert.Equal(
            expected,
            tab.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        if (includesPanels)
        {
            Assert.All(
                tab.GetProperty("panels").EnumerateArray(),
                panel => AssertPanelShape(
                    panel,
                    includesGraphClock));
        }
    }

    private static void AssertPanelShape(
        JsonElement panel,
        bool includesGraphClock)
    {
        var expected = new List<string>
        {
            "window_id",
            "workspace_id",
        };
        if (includesGraphClock)
        {
            expected.Add("workspace_revision");
            expected.Add("graph_sequence");
        }

        expected.AddRange(
        [
            "tab_id",
            "panel_id",
            "kind",
            "visible",
            "focused",
            "title",
        ]);
        Assert.Equal(
            expected,
            panel.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
    }

    private static void AssertPageReceipt(
        JsonElement page,
        int offset,
        int returned,
        int? nextOffset)
    {
        Assert.Equal(
            [
                "offset",
                "page_size",
                "returned",
                "next_offset",
                "complete",
                "items",
            ],
            page.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(offset, page.GetProperty("offset").GetInt32());
        Assert.Equal(
            AgentWorkspaceGraphRequest.PageSize,
            page.GetProperty("page_size").GetInt32());
        Assert.Equal(returned, page.GetProperty("returned").GetInt32());
        if (nextOffset is { } expected)
        {
            Assert.Equal(
                expected,
                page.GetProperty("next_offset").GetInt32());
        }
        else
        {
            Assert.Equal(
                JsonValueKind.Null,
                page.GetProperty("next_offset").ValueKind);
        }

        Assert.Equal(
            nextOffset is null,
            page.GetProperty("complete").GetBoolean());
        Assert.Equal(
            returned,
            page.GetProperty("items").GetArrayLength());
    }

    private static void AssertNoSensitiveOrOutOfScopeFields(
        JsonElement element)
    {
        var forbiddenNames = new HashSet<string>(
            [
                "total",
                "total_count",
                "workspace_total",
                "tab_total",
                "panel_total",
                "session",
                "session_id",
                "capability",
                "capabilities",
                "connection",
                "connection_id",
                "connection_boundary",
                "cwd",
                "working_directory",
                "initial_working_directory",
                "current_working_directory",
                "file",
                "file_metadata",
                "browser",
                "browser_metadata",
            ],
            StringComparer.OrdinalIgnoreCase);
        foreach (var property in EnumerateProperties(element))
        {
            Assert.DoesNotContain(property.Name, forbiddenNames);
        }
    }

    private static IEnumerable<JsonProperty> EnumerateProperties(
        JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property;
                foreach (var descendant in EnumerateProperties(
                             property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in EnumerateProperties(item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static AgentWorkspaceGraphActionResult
        WorkspaceInspectedWithWindowId(string windowId)
    {
        var workspace = new AgentWorkspaceGraphWorkspace(
            new WindowInstanceId(windowId),
            new WorkspaceInstanceId("workspace"),
            WorkspaceRevision: 1,
            GraphSequence: 1,
            Title: null);
        var inspectionConstructor =
            typeof(AgentWorkspaceGraphWorkspaceInspection)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [
                        typeof(AgentWorkspaceGraphWorkspace),
                        typeof(IReadOnlyList<AgentWorkspaceGraphTabInspection>),
                    ],
                    modifiers: null)
            ?? throw new InvalidOperationException(
                "The workspace inspection constructor is unavailable.");
        var inspection =
            (AgentWorkspaceGraphWorkspaceInspection)inspectionConstructor.Invoke(
            [
                workspace,
                Array.Empty<AgentWorkspaceGraphTabInspection>(),
            ]);
        var resultConstructor =
            typeof(AgentWorkspaceGraphActionResult.WorkspaceInspected)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [
                        typeof(AgentWorkspaceGraphScopeKind),
                        typeof(bool),
                        typeof(AgentWorkspaceGraphWorkspaceInspection),
                    ],
                    modifiers: null)
            ?? throw new InvalidOperationException(
                "The workspace-inspect result constructor is unavailable.");
        return (AgentWorkspaceGraphActionResult)resultConstructor.Invoke(
        [
            AgentWorkspaceGraphScopeKind.Workspace,
            false,
            inspection,
        ]);
    }

    private sealed class GraphFixture
    {
        private readonly WorkspaceGraphSnapshot _graph;
        private readonly SessionDescriptor? _terminalDescriptor;

        private GraphFixture(
            WorkspaceGraphSnapshot graph,
            SessionDescriptor? terminalDescriptor,
            IReadOnlyList<PanelInstanceId> allPanels,
            IReadOnlyList<PanelInstanceId> firstTabPanels,
            TabInstanceId firstTabId,
            TabInstanceId secondTabId,
            PanelInstanceId terminalPanelId,
            PanelInstanceId statisticsPanelId,
            PanelInstanceId browserPanelId,
            SessionId terminalSessionId)
        {
            _graph = graph;
            _terminalDescriptor = terminalDescriptor;
            AllPanels = allPanels;
            FirstTabPanels = firstTabPanels;
            FirstTabId = firstTabId;
            SecondTabId = secondTabId;
            TerminalPanelId = terminalPanelId;
            StatisticsPanelId = statisticsPanelId;
            BrowserPanelId = browserPanelId;
            TerminalSessionId = terminalSessionId;
        }

        public WindowInstanceId WindowId => _graph.WindowId;

        public WorkspaceInstanceId WorkspaceId => _graph.Workspace.Id;

        public IReadOnlyList<PanelInstanceId> AllPanels { get; }

        public IReadOnlyList<PanelInstanceId> FirstTabPanels { get; }

        public TabInstanceId FirstTabId { get; }

        public TabInstanceId SecondTabId { get; }

        public PanelInstanceId TerminalPanelId { get; }

        public PanelInstanceId StatisticsPanelId { get; }

        public PanelInstanceId BrowserPanelId { get; }

        public SessionId TerminalSessionId { get; }

        public static GraphFixture Create(
            int panelCount = 4,
            string workspaceTitle = "Workspace",
            string statisticsTitle = "Statistics")
        {
            if (panelCount != 4 && panelCount != 17)
            {
                throw new ArgumentOutOfRangeException(nameof(panelCount));
            }

            var windowId = new WindowInstanceId("graph-window");
            var workspaceId = new WorkspaceInstanceId("graph-workspace");
            var firstTabId = new TabInstanceId("tab-primary");
            var secondTabId = new TabInstanceId("tab-secondary");
            var terminalPanelId = new PanelInstanceId("panel-terminal");
            var statisticsPanelId =
                new PanelInstanceId("panel-statistics");
            var browserPanelId = new PanelInstanceId("panel-browser");
            var terminalSessionId = new SessionId("terminal-session");

            if (panelCount == 17)
            {
                var panels = Enumerable.Range(0, panelCount)
                    .Select(index => new PanelInstance(
                        new PanelInstanceId($"panel-{index:D2}"),
                        PanelKind.Statistics,
                        $"Monitor {index:D2}"))
                    .ToArray();
                var tab = new TabInstance(
                    firstTabId,
                    "Primary",
                    panels,
                    panels[0].Id);
                var graph = new WorkspaceGraphSnapshot(
                    windowId,
                    new WorkspaceInstance(
                        workspaceId,
                        workspaceTitle,
                        [tab],
                        tab.Id),
                    revision: 11,
                    lastSequence: 13);
                return new GraphFixture(
                    graph,
                    terminalDescriptor: null,
                    [.. panels.Select(panel => panel.Id)],
                    [.. panels.Select(panel => panel.Id)],
                    firstTabId,
                    firstTabId,
                    panels[0].Id,
                    panels[1].Id,
                    panels[2].Id,
                    terminalSessionId);
            }

            var terminal = new PanelInstance(
                terminalPanelId,
                PanelKind.Terminal,
                "Terminal",
                terminalSessionId);
            var statistics = new PanelInstance(
                statisticsPanelId,
                PanelKind.Statistics,
                statisticsTitle);
            var browser = new PanelInstance(
                browserPanelId,
                PanelKind.Browser,
                "Browser");
            var file = new PanelInstance(
                new PanelInstanceId("panel-file"),
                PanelKind.FileViewer,
                "Files");
            var firstTab = new TabInstance(
                firstTabId,
                "Primary",
                [terminal, statistics],
                terminal.Id);
            var secondTab = new TabInstance(
                secondTabId,
                "Secondary",
                [browser, file],
                browser.Id);
            var graphSnapshot = new WorkspaceGraphSnapshot(
                windowId,
                new WorkspaceInstance(
                    workspaceId,
                    workspaceTitle,
                    [firstTab, secondTab],
                    firstTab.Id),
                revision: 11,
                lastSequence: 13);
            var terminalDescriptor = new SessionDescriptor(
                terminalSessionId,
                PanelKind.Terminal,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                new SessionOwner(
                    HostMode.Desktop,
                    windowId,
                    workspaceId,
                    firstTabId,
                    terminalPanelId),
                new CapabilitySet(
                [
                    SessionCapabilities.TerminalReadScreen,
                    SessionCapabilities.TerminalWrite,
                ]),
                Revision: 7,
                HasActiveWork: false,
                StatusDetail: "Ready");
            return new GraphFixture(
                graphSnapshot,
                terminalDescriptor,
                [terminal.Id, statistics.Id, browser.Id, file.Id],
                [terminal.Id, statistics.Id],
                firstTabId,
                secondTabId,
                terminalPanelId,
                statisticsPanelId,
                browserPanelId,
                terminalSessionId);
        }

        public AgentTarget.Panel Exact(
            PanelInstanceId panelId,
            TabInstanceId tabId) =>
            new(WindowId, WorkspaceId, tabId, panelId);

        public AgentWorkspaceGraphActionResult Project(
            AgentTarget target,
            IReadOnlyList<PanelInstanceId> panels,
            AgentWorkspaceGraphRequest request)
        {
            var contextPanels = panels.Select(panelId =>
            {
                var tab = _graph.Workspace.Tabs.Single(candidate =>
                    candidate.Panels.Any(panel => panel.Id == panelId));
                return AgentContextPanel.ForGraphPanel(
                    _graph,
                    tab.Id,
                    panelId,
                    panelId == TerminalPanelId
                        ? _terminalDescriptor
                        : null);
            });
            var context = new AgentContextSnapshot(
                target,
                contextPanels,
                Now);
            var composer = new AgentWorkspaceGraphActionComposer();
            var action = composer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    new AgentRunId("graph-json-run"),
                    new ActorDescriptor(
                        new ActorId("graph-json-agent"),
                        ActorKind.Agent,
                        "Graph JSON agent"),
                    policyGeneration: 3,
                    Now,
                    Now.AddMinutes(1)),
                context,
                request);
            return composer.Project(action, context);
        }
    }
}
