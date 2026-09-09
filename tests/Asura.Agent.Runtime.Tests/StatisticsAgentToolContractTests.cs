using System.Reflection;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class StatisticsAgentToolContractTests
{
    [Fact]
    public void ExactSchemaIsClosedAndArgumentFree()
    {
        var tool = Assert.Single(StatisticsAgentToolSet.For(
            StatisticsPanel("exact")));
        var schema = tool.InputSchema;

        Assert.Equal(BuiltInAgentTools.StatisticsRead, tool.Name);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
        Assert.DoesNotContain("panel_id", schema.GetRawText());
        Assert.Contains("numeric", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never exposes", tool.Description, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in ForbiddenSurfaces)
        {
            Assert.DoesNotContain(
                forbidden,
                schema.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BroadSchemaRequiresOnlyEligibleStatisticsPanelIdentity()
    {
        var first = StatisticsPanel("first");
        var second = StatisticsPanel("second");
        var incapable = StatisticsPanel(
            "incapable",
            capabilities: CapabilitySet.Empty);
        var wrongKind = StatisticsPanel(
            "process",
            kind: PanelKind.ProcessMonitor);

        var tool = Assert.Single(StatisticsAgentToolSet.For(
            [first, incapable, wrongKind, second]));

        Assert.Equal(
            ["panel_id"],
            tool.InputSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [first.PanelId.Value, second.PanelId.Value],
            tool.InputSchema.GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
    }

    [Fact]
    public void WrongKindLifecycleOrCapabilityExposeNoStatisticsTool()
    {
        Assert.Empty(StatisticsAgentToolSet.For(
            StatisticsPanel("wrong-kind", kind: PanelKind.ProcessMonitor)));
        Assert.Empty(StatisticsAgentToolSet.For(
            StatisticsPanel(
                "starting",
                lifecycle: SessionLifecycle.Starting)));
        Assert.Empty(StatisticsAgentToolSet.For(
            StatisticsPanel(
                "incapable",
                capabilities: CapabilitySet.Empty)));
    }

    [Fact]
    public void ExactParserAcceptsOnlyEmptyObjectAndHostOwnedIdentity()
    {
        var panel = StatisticsPanel("exact");

        var parsed = Assert.IsType<StatisticsAgentIntentResult.Parsed>(
            StatisticsAgentToolParser.Parse(
                Proposal(BuiltInAgentTools.StatisticsRead, "{}"),
                panel));
        var suppliedPanel = StatisticsAgentToolParser.Parse(
            Proposal(
                BuiltInAgentTools.StatisticsRead,
                $$"""{"panel_id":"{{panel.PanelId.Value}}"}"""),
            panel);

        Assert.Equal(panel.PanelId, parsed.PanelId);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<StatisticsAgentIntentResult.Rejected>(
                suppliedPanel).StableCode);
    }

    [Fact]
    public void BroadParserRequiresOneEligiblePanelId()
    {
        var panel = StatisticsPanel("selected");
        AgentContextPanel[] panels = [panel];

        var selected = Assert.IsType<StatisticsAgentIntentResult.Parsed>(
            StatisticsAgentToolParser.Parse(
                Proposal(
                    BuiltInAgentTools.StatisticsRead,
                    $$"""{"panel_id":"{{panel.PanelId.Value}}"}"""),
                panels));
        var omitted = StatisticsAgentToolParser.Parse(
            Proposal(BuiltInAgentTools.StatisticsRead, "{}"),
            panels);
        var outside = StatisticsAgentToolParser.Parse(
            Proposal(
                BuiltInAgentTools.StatisticsRead,
                """{"panel_id":"outside"}"""),
            panels);

        Assert.Equal(panel.PanelId, selected.PanelId);
        Assert.All(
            [omitted, outside],
            result => Assert.Equal(
                "invalid_tool_arguments",
                Assert.IsType<StatisticsAgentIntentResult.Rejected>(
                    result).StableCode));
    }

    [Theory]
    [InlineData("{\"panel_id\":\"a\",\"panel_id\":\"b\"}")]
    [InlineData("{\"database_query\":\"select *\"}")]
    [InlineData("{\"docker_action\":\"restart\"}")]
    [InlineData("{\"include_processes\":true}")]
    public void ParserRejectsMalformedOrWideningArguments(string arguments)
    {
        var result = StatisticsAgentToolParser.Parse(
            Proposal(BuiltInAgentTools.StatisticsRead, arguments),
            StatisticsPanel("reject"));

        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<StatisticsAgentIntentResult.Rejected>(
                result).StableCode);
    }

    [Fact]
    public void ParserSeparatesUnknownAndUnavailableTools()
    {
        var unavailable = StatisticsPanel(
            "unavailable",
            capabilities: CapabilitySet.Empty);

        Assert.Equal(
            "unknown_tool",
            Assert.IsType<StatisticsAgentIntentResult.Rejected>(
                StatisticsAgentToolParser.Parse(
                    Proposal("statistics.mutate", "{}"),
                    unavailable)).StableCode);
        Assert.Equal(
            "tool_not_available",
            Assert.IsType<StatisticsAgentIntentResult.Rejected>(
                StatisticsAgentToolParser.Parse(
                    Proposal(BuiltInAgentTools.StatisticsRead, "{}"),
                    unavailable)).StableCode);
    }

    private static AgentToolProposal Proposal(
        string toolName,
        string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        var constructor = typeof(AgentToolProposal).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(string),
                typeof(long),
                typeof(string),
                typeof(string),
                typeof(JsonElement),
            ],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "The tool proposal constructor is unavailable.");
        return (AgentToolProposal)constructor.Invoke(
        [
            "statistics-proposal",
            1L,
            "provider-call",
            toolName,
            document.RootElement,
        ]);
    }

    private static AgentContextPanel StatisticsPanel(
        string id,
        PanelKind kind = PanelKind.Statistics,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        CapabilitySet? capabilities = null)
    {
        var windowId = new WindowInstanceId($"window-{id}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{id}");
        var tabId = new TabInstanceId($"tab-{id}");
        var panelId = new PanelInstanceId($"panel-{id}");
        var sessionId = new SessionId($"session-{id}");
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                $"Workspace {id}",
                [
                    new TabInstance(
                        tabId,
                        $"Tab {id}",
                        [new PanelInstance(
                            panelId,
                            kind,
                            $"Panel {id}",
                            sessionId)],
                        panelId),
                ],
                tabId),
            revision: 1,
            lastSequence: 1);
        var descriptor = new SessionDescriptor(
            sessionId,
            kind,
            lifecycle,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            capabilities
                ?? new CapabilitySet([SessionCapabilities.StatisticsRead]),
            Revision: 1,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private static readonly string[] ForbiddenSurfaces =
    [
        "database_query",
        "docker_action",
        "command_line",
        "process_name",
        "mutation",
    ];
}
