using System.Reflection;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class ProcessAgentToolContractTests
{
    [Fact]
    public void ExactSchemaExposesOnlyClosedSortPagingAndFilterOptions()
    {
        var panel = ProcessPanel("exact");

        var tool = Assert.Single(ProcessAgentToolSet.For(panel));
        var schema = tool.InputSchema;

        Assert.Equal(BuiltInAgentTools.ProcessesList, tool.Name);
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
        Assert.Equal(
            ["sort", "limit", "offset", "name_contains", "pid"],
            schema.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            ["cpu_desc", "memory_desc", "name_asc", "pid_asc"],
            schema.GetProperty("properties")
                .GetProperty("sort")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [16, 32, 64],
            schema.GetProperty("properties")
                .GetProperty("limit")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetInt32()));
        Assert.DoesNotContain(
            "panel_id",
            schema.GetRawText(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "session",
            string.Concat(tool.Description, schema.GetRawText()),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "untrusted local process metadata",
            tool.Description,
            StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in ForbiddenProcessFields)
        {
            Assert.DoesNotContain(
                forbidden,
                schema.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(ProcessMonitorSort.CpuAscending)]
    [InlineData(ProcessMonitorSort.MemoryAscending)]
    [InlineData(ProcessMonitorSort.NameDescending)]
    [InlineData(ProcessMonitorSort.ProcessIdDescending)]
    public void IntentRejectsHumanOnlyReverseSorts(ProcessMonitorSort sort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProcessAgentIntent(ProcessAgentToolSet.DefaultLimit, sort));
    }

    [Fact]
    public void BroadSchemaAlwaysRequiresAndEnumeratesOnlyEligiblePanels()
    {
        var first = ProcessPanel("first");
        var second = ProcessPanel("second");
        var incapable = ProcessPanel(
            "incapable",
            capabilities: CapabilitySet.Empty);
        var wrongKind = ProcessPanel(
            "statistics",
            kind: PanelKind.Statistics);

        var tool = Assert.Single(ProcessAgentToolSet.For(
            [first, incapable, wrongKind, second]));
        var schema = tool.InputSchema;

        Assert.Equal(
            ["panel_id"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [first.PanelId.Value, second.PanelId.Value],
            schema.GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);

        var onePanel = Assert.Single(ProcessAgentToolSet.For([first]));
        Assert.Equal(
            [first.PanelId.Value],
            onePanel.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Contains(
            "panel_id",
            onePanel.InputSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
    }

    [Fact]
    public void WrongKindLifecycleOrCapabilityExposeNoProcessTool()
    {
        Assert.Empty(ProcessAgentToolSet.For(
            ProcessPanel("wrong-kind", kind: PanelKind.Statistics)));
        Assert.Empty(ProcessAgentToolSet.For(
            ProcessPanel(
                "not-active",
                lifecycle: SessionLifecycle.Starting)));
        Assert.Empty(ProcessAgentToolSet.For(
            ProcessPanel(
                "incapable",
                capabilities: CapabilitySet.Empty)));
    }

    [Fact]
    public void ExactParserAppliesCpuAndThirtyTwoDefaults()
    {
        var panel = ProcessPanel("defaults");

        var parsed = Assert.IsType<ProcessAgentIntentResult.Parsed>(
            ProcessAgentToolParser.Parse(
                Proposal(BuiltInAgentTools.ProcessesList, "{}"),
                panel));

        Assert.Equal(panel.PanelId, parsed.PanelId);
        Assert.Equal(32, parsed.Intent.Limit);
        Assert.Equal(
            ProcessMonitorSort.CpuDescending,
            parsed.Intent.Sort);
        Assert.Equal(0, parsed.Intent.Offset);
        Assert.Null(parsed.Intent.NameContains);
        Assert.Null(parsed.Intent.ProcessId);
    }

    [Fact]
    public void ParserAcceptsBoundedPagingAndFilters()
    {
        var panel = ProcessPanel("filters");

        var parsed = Assert.IsType<ProcessAgentIntentResult.Parsed>(
            ProcessAgentToolParser.Parse(
                Proposal(
                    BuiltInAgentTools.ProcessesList,
                    """{"offset":32,"name_contains":"dotnet","pid":42}"""),
                panel));

        Assert.Equal(32, parsed.Intent.Offset);
        Assert.Equal("dotnet", parsed.Intent.NameContains);
        Assert.Equal(42, parsed.Intent.ProcessId);
    }

    [Theory]
    [InlineData("cpu_desc", ProcessMonitorSort.CpuDescending)]
    [InlineData("memory_desc", ProcessMonitorSort.MemoryDescending)]
    [InlineData("name_asc", ProcessMonitorSort.NameAscending)]
    [InlineData("pid_asc", ProcessMonitorSort.ProcessIdAscending)]
    public void ParserAcceptsOnlyNamedSortsAndFixedLimits(
        string sort,
        ProcessMonitorSort expected)
    {
        var panel = ProcessPanel("options");
        foreach (var limit in new[] { 16, 32, 64 })
        {
            var parsed = Assert.IsType<ProcessAgentIntentResult.Parsed>(
                ProcessAgentToolParser.Parse(
                    Proposal(
                        BuiltInAgentTools.ProcessesList,
                        $$"""{"sort":"{{sort}}","limit":{{limit}}}"""),
                    panel));

            Assert.Equal(expected, parsed.Intent.Sort);
            Assert.Equal(limit, parsed.Intent.Limit);
        }
    }

    [Fact]
    public void BroadParserRequiresAnEligiblePanelEvenWhenOnlyOneExists()
    {
        var panel = ProcessPanel("selected");
        AgentContextPanel[] panels = [panel];

        var omitted = ProcessAgentToolParser.Parse(
            Proposal(BuiltInAgentTools.ProcessesList, "{}"),
            panels);
        var selected = Assert.IsType<ProcessAgentIntentResult.Parsed>(
            ProcessAgentToolParser.Parse(
                Proposal(
                    BuiltInAgentTools.ProcessesList,
                    $$"""{"panel_id":"{{panel.PanelId.Value}}"}"""),
                panels));
        var outside = ProcessAgentToolParser.Parse(
            Proposal(
                BuiltInAgentTools.ProcessesList,
                """{"panel_id":"outside"}"""),
            panels);

        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<ProcessAgentIntentResult.Rejected>(
                omitted).StableCode);
        Assert.Equal(panel.PanelId, selected.PanelId);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<ProcessAgentIntentResult.Rejected>(
                outside).StableCode);
    }

    [Theory]
    [InlineData("""{"sort":"CPU_DESC"}""")]
    [InlineData("""{"sort":"cpu_desc","sort":"pid_asc"}""")]
    [InlineData("""{"sort":0}""")]
    [InlineData("""{"sort":null}""")]
    [InlineData("""{"limit":0}""")]
    [InlineData("""{"limit":1}""")]
    [InlineData("""{"limit":63}""")]
    [InlineData("""{"limit":65}""")]
    [InlineData("""{"limit":16.0}""")]
    [InlineData("""{"limit":"32"}""")]
    [InlineData("""{"limit":2147483648}""")]
    [InlineData("""{"maximum_results":64}""")]
    [InlineData("""{"include_command_line":true}""")]
    [InlineData("""{"user":"root"}""")]
    [InlineData("""{"filter":"asura"}""")]
    [InlineData("""{"offset":-1}""")]
    [InlineData("""{"offset":1000001}""")]
    [InlineData("""{"name_contains":""}""")]
    [InlineData("""{"pid":0}""")]
    public void ParserRejectsMalformedWideningOrUnknownArguments(
        string arguments)
    {
        var result = ProcessAgentToolParser.Parse(
            Proposal(BuiltInAgentTools.ProcessesList, arguments),
            ProcessPanel("reject"));

        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<ProcessAgentIntentResult.Rejected>(
                result).StableCode);
    }

    [Fact]
    public void ExactParserRejectsProviderSuppliedPanelIdentity()
    {
        var panel = ProcessPanel("host-owned");

        var result = ProcessAgentToolParser.Parse(
            Proposal(
                BuiltInAgentTools.ProcessesList,
                $$"""{"panel_id":"{{panel.PanelId.Value}}"}"""),
            panel);

        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<ProcessAgentIntentResult.Rejected>(
                result).StableCode);
    }

    [Fact]
    public void ParserSeparatesUnknownAndUnavailableTools()
    {
        var panel = ProcessPanel(
            "unavailable",
            capabilities: CapabilitySet.Empty);

        var unknown = ProcessAgentToolParser.Parse(
            Proposal("processes.provider_extension", "{}"),
            panel);
        var unavailable = ProcessAgentToolParser.Parse(
            Proposal(BuiltInAgentTools.ProcessesList, "{}"),
            panel);

        Assert.Equal(
            "unknown_tool",
            Assert.IsType<ProcessAgentIntentResult.Rejected>(
                unknown).StableCode);
        Assert.Equal(
            "tool_not_available",
            Assert.IsType<ProcessAgentIntentResult.Rejected>(
                unavailable).StableCode);
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
            "process-proposal",
            1L,
            "provider-call",
            toolName,
            document.RootElement,
        ]);
    }

    private static AgentContextPanel ProcessPanel(
        string id,
        PanelKind kind = PanelKind.ProcessMonitor,
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
                        [
                            new PanelInstance(
                                panelId,
                                kind,
                                $"Panel {id}",
                                sessionId),
                        ],
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
                ?? new CapabilitySet([SessionCapabilities.ProcessesList]),
            Revision: 1,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private static readonly string[] ForbiddenProcessFields =
    [
        "command_line",
        "executable",
        "environment",
        "username",
        "user_id",
        "open_files",
        "total_processor_time",
    ];
}
