using System.Reflection;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class WorkspaceGraphAgentToolContractTests
{
    [Fact]
    public void RegisteredGraphAdvertisesOnlyClosedScopeClippedObservationSchemas()
    {
        var context = RegisteredGraphContext();

        var tools = WorkspaceGraphAgentToolSet.For(context);

        Assert.Equal(
            [
                BuiltInAgentTools.WorkspaceInspect,
                BuiltInAgentTools.TabList,
                BuiltInAgentTools.PanelList,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var schema = tool.InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.False(
                schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Empty(schema.GetProperty("required").EnumerateArray());

            var publicContract = string.Concat(
                tool.Description,
                schema.GetRawText());
            Assert.DoesNotContain(
                "outside-window-canary",
                publicContract,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "window_id",
                publicContract,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "workspace_id",
                publicContract,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "tab_id",
                publicContract,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "panel_id",
                publicContract,
                StringComparison.Ordinal);
            Assert.Contains(
                "scope",
                tool.Description,
                StringComparison.OrdinalIgnoreCase);
        }

        var inspectSchema = tools
            .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.WorkspaceInspect, StringComparison.Ordinal))
            .InputSchema;
        Assert.Empty(
            inspectSchema.GetProperty("properties").EnumerateObject());

        foreach (var toolName in new[]
                 {
                     BuiltInAgentTools.TabList,
                     BuiltInAgentTools.PanelList,
                 })
        {
            var schema = tools
                .Single(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))
                .InputSchema;
            var properties = schema.GetProperty("properties");
            var offset = Assert.Single(properties.EnumerateObject());
            Assert.Equal("offset", offset.Name);
            Assert.Equal(
                "integer",
                offset.Value.GetProperty("type").GetString());
            Assert.Equal(
                [0, 16, 32, 48],
                offset.Value
                    .GetProperty("enum")
                    .EnumerateArray()
                    .Select(value => value.GetInt32()));
        }
    }

    [Fact]
    public void GraphlessSessionAdvertisesNoWorkspaceGraphTools()
    {
        var sessionId = new SessionId("graphless-session");
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.Terminal,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("graphless-window"),
                new WorkspaceInstanceId("graphless-workspace"),
                new TabInstanceId("graphless-tab"),
                new PanelInstanceId("graphless-panel")),
            CapabilitySet.Empty,
            Revision: 1,
            HasActiveWork: false,
            StatusDetail: "Ready");
        var context = new AgentContextSnapshot(
            new AgentTarget.ConnectionSession(sessionId),
            [AgentContextPanel.ForExactSession(descriptor)],
            DateTimeOffset.UnixEpoch);

        Assert.Empty(WorkspaceGraphAgentToolSet.For(context));
    }

    [Fact]
    public void WorkspaceInspectAcceptsOnlyAnEmptyObject()
    {
        var accepted = WorkspaceGraphAgentToolParser.Parse(
            Proposal(BuiltInAgentTools.WorkspaceInspect, "{}"));
        var rejected = WorkspaceGraphAgentToolParser.Parse(
            Proposal(
                BuiltInAgentTools.WorkspaceInspect,
                """{"offset":0}"""));

        Assert.IsType<WorkspaceGraphAgentIntent.WorkspaceInspect>(
            Assert.IsType<WorkspaceGraphAgentIntentResult.Parsed>(
                accepted).Intent);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<WorkspaceGraphAgentIntentResult.Rejected>(
                rejected).StableCode);
    }

    [Fact]
    public void WorkspaceListIsAbsentAndRejectedAsUnknown()
    {
        const string removedTool = "workspace.list";
        var context = RegisteredGraphContext();

        Assert.False(BuiltInAgentTools.Catalog.TryGet(removedTool, out _));
        Assert.DoesNotContain(
            WorkspaceGraphAgentToolSet.For(context),
            tool => string.Equals(tool.Name, removedTool, StringComparison.Ordinal));
        Assert.Equal(
            "unknown_tool",
            Assert.IsType<WorkspaceGraphAgentIntentResult.Rejected>(
                WorkspaceGraphAgentToolParser.Parse(
                    Proposal(removedTool, "{}"))).StableCode);
    }

    [Fact]
    public void PageToolsDefaultToZeroAndAcceptOnlyFixedPageStarts()
    {
        foreach (var toolName in new[]
                 {
                     BuiltInAgentTools.TabList,
                     BuiltInAgentTools.PanelList,
                 })
        {
            var defaulted = ParsePage(toolName, "{}");
            Assert.Equal(0, defaulted);

            foreach (var offset in new[] { 0, 16, 32, 48 })
            {
                Assert.Equal(
                    offset,
                    ParsePage(toolName, $$"""{"offset":{{offset}}}"""));
            }
        }
    }

    [Theory]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":0,"offset":16}""")]
    [InlineData(BuiltInAgentTools.PanelList, """{"offset":0,"panel_id":"outside"}""")]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":"0"}""")]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":true}""")]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":null}""")]
    [InlineData(BuiltInAgentTools.PanelList, """{"offset":[]}""")]
    [InlineData(BuiltInAgentTools.PanelList, """{"offset":{}}""")]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":16.5}""")]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":-16}""")]
    [InlineData(BuiltInAgentTools.PanelList, """{"offset":1}""")]
    [InlineData(BuiltInAgentTools.PanelList, """{"offset":64}""")]
    [InlineData(BuiltInAgentTools.TabList, """{"offset":2147483648}""")]
    [InlineData(BuiltInAgentTools.PanelList, """{"offset":1e100}""")]
    public void ParserRejectsDuplicateUnknownWrongTypeFractionalOrHugeOffsets(
        string toolName,
        string arguments)
    {
        var result = WorkspaceGraphAgentToolParser.Parse(
            Proposal(toolName, arguments));

        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<WorkspaceGraphAgentIntentResult.Rejected>(
                result).StableCode);
    }

    [Fact]
    public void ParserRejectsUnknownWorkspaceGraphExtensions()
    {
        var result = WorkspaceGraphAgentToolParser.Parse(
            Proposal("workspace.provider_extension", "{}"));

        Assert.Equal(
            "unknown_tool",
            Assert.IsType<WorkspaceGraphAgentIntentResult.Rejected>(
                result).StableCode);
    }

    private static int ParsePage(string toolName, string arguments)
    {
        var parsed = Assert.IsType<WorkspaceGraphAgentIntentResult.Parsed>(
            WorkspaceGraphAgentToolParser.Parse(
                Proposal(toolName, arguments)));
        return parsed.Intent switch
        {
            WorkspaceGraphAgentIntent.TabList tab => tab.Offset,
            WorkspaceGraphAgentIntent.PanelList panel => panel.Offset,
            _ => throw new Xunit.Sdk.XunitException(
                "Expected a page-list intent."),
        };
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
            "graph-proposal",
            1L,
            "provider-call",
            toolName,
            document.RootElement,
        ]);
    }

    private static AgentContextSnapshot RegisteredGraphContext()
    {
        var windowId = new WindowInstanceId("fixed-window");
        var workspaceId = new WorkspaceInstanceId("fixed-workspace");
        var tabId = new TabInstanceId("fixed-tab");
        var panelId = new PanelInstanceId("fixed-panel");
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                "Fixed workspace",
                [
                    new TabInstance(
                        tabId,
                        "Fixed tab",
                        [
                            new PanelInstance(
                                panelId,
                                PanelKind.Statistics,
                                "Fixed panel"),
                        ],
                        panelId),
                ],
                tabId),
            revision: 3,
            lastSequence: 4);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(windowId, workspaceId),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    tabId,
                    panelId,
                    session: null),
            ],
            DateTimeOffset.UnixEpoch);
    }
}
