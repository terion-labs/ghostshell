using System.Reflection;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class WorkspaceLayoutAgentToolContractTests
{
    private static readonly IReadOnlySet<PanelKind> SupportedKinds =
        new HashSet<PanelKind>
        {
            PanelKind.Terminal,
            PanelKind.Browser,
        };

    [Fact]
    public void Workspace_advertises_stable_layout_and_connection_schemas()
    {
        var tools = WorkspaceLayoutAgentToolSet.For(Context(), SupportedKinds);

        Assert.Equal(
            [
                BuiltInAgentTools.ConnectionsList,
                BuiltInAgentTools.TabCreate,
                BuiltInAgentTools.TabClose,
                BuiltInAgentTools.PanelAdd,
                BuiltInAgentTools.PanelSplit,
                BuiltInAgentTools.PanelClose,
                BuiltInAgentTools.PanelConnect,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            Assert.False(tool.InputSchema
                .GetProperty("additionalProperties")
                .GetBoolean());
            var contract = tool.InputSchema.GetRawText();
            Assert.DoesNotContain("window_id", contract, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace_id", contract, StringComparison.Ordinal);
            Assert.DoesNotContain("outside", contract, StringComparison.Ordinal);
        }

        var split = tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.PanelSplit, StringComparison.Ordinal))
            .InputSchema;
        var panelId = split.GetProperty("properties").GetProperty("panel_id");
        Assert.Equal("string", panelId.GetProperty("type").GetString());
        Assert.False(panelId.TryGetProperty("enum", out _));
        Assert.Equal(
            ["left_right", "top_bottom"],
            split.GetProperty("properties")
                .GetProperty("orientation")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [
                "terminal", "browser", "file_viewer", "statistics",
                "process_monitor", "placeholder", "database_viewer", "docker",
            ],
            split.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(BuiltInAgentTools.ConnectionsList, "{}", typeof(WorkspaceLayoutAgentIntent.ConnectionList))]
    [InlineData(BuiltInAgentTools.TabCreate, "{\"kind\":\"browser\"}", typeof(WorkspaceLayoutAgentIntent.TabCreate))]
    [InlineData(BuiltInAgentTools.TabCreate, "{\"kind\":\"browser\",\"connection_ref\":\"connection_test\"}", typeof(WorkspaceLayoutAgentIntent.TabCreate))]
    [InlineData(BuiltInAgentTools.TabClose, "{\"tab_id\":\"layout-tab\"}", typeof(WorkspaceLayoutAgentIntent.TabClose))]
    [InlineData(BuiltInAgentTools.PanelAdd, "{\"tab_id\":\"layout-tab\",\"kind\":\"terminal\",\"connection_ref\":\"connection_test\"}", typeof(WorkspaceLayoutAgentIntent.PanelAdd))]
    [InlineData(BuiltInAgentTools.PanelSplit, "{\"panel_id\":\"layout-panel\",\"orientation\":\"top_bottom\",\"kind\":\"browser\"}", typeof(WorkspaceLayoutAgentIntent.PanelSplit))]
    [InlineData(BuiltInAgentTools.PanelClose, "{\"panel_id\":\"layout-panel\"}", typeof(WorkspaceLayoutAgentIntent.PanelClose))]
    [InlineData(BuiltInAgentTools.PanelConnect, "{\"panel_id\":\"layout-panel\",\"connection_ref\":\"connection_test\"}", typeof(WorkspaceLayoutAgentIntent.PanelConnect))]
    public void Parser_accepts_each_closed_request(
        string toolName,
        string arguments,
        Type expectedType)
    {
        var parsed = Assert.IsType<WorkspaceLayoutAgentIntentResult.Parsed>(
            WorkspaceLayoutAgentToolParser.Parse(
                Proposal(toolName, arguments),
                Context(),
                SupportedKinds));

        Assert.IsType(expectedType, parsed.Intent);
    }

    [Theory]
    [InlineData(BuiltInAgentTools.PanelClose, "{\"panel_id\":\"outside\"}")]
    [InlineData(BuiltInAgentTools.TabClose, "{\"tab_id\":\"layout-tab\",\"extra\":true}")]
    [InlineData(BuiltInAgentTools.TabCreate, "{\"kind\":\"docker\"}")]
    [InlineData(BuiltInAgentTools.PanelSplit, "{\"panel_id\":\"layout-panel\",\"orientation\":\"diagonal\",\"kind\":\"browser\"}")]
    [InlineData(BuiltInAgentTools.PanelAdd, "{\"tab_id\":\"layout-tab\",\"kind\":\"browser\",\"kind\":\"terminal\"}")]
    public void Parser_rejects_out_of_scope_unsupported_unknown_and_duplicate_inputs(
        string toolName,
        string arguments)
    {
        var rejected = Assert.IsType<WorkspaceLayoutAgentIntentResult.Rejected>(
            WorkspaceLayoutAgentToolParser.Parse(
                Proposal(toolName, arguments),
                Context(),
                SupportedKinds));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void Partial_scope_advertises_no_layout_mutations()
    {
        var workspace = Context();
        var partial = new AgentContextSnapshot(
            new AgentTarget.Panel(Window(), Workspace(), Tab(), Panel()),
            workspace.Panels,
            DateTimeOffset.UnixEpoch);

        Assert.Empty(WorkspaceLayoutAgentToolSet.For(partial, SupportedKinds));
    }

    [Fact]
    public void Live_supported_kind_changes_do_not_change_the_tool_manifest()
    {
        Assert.Equal(
            WorkspaceLayoutAgentToolSet.For(Context(), SupportedKinds)
                .Select(tool => (tool.Name, tool.InputSchema.GetRawText())),
            WorkspaceLayoutAgentToolSet.For(Context(), new HashSet<PanelKind>())
                .Select(tool => (tool.Name, tool.InputSchema.GetRawText())));
    }

    [Fact]
    public void Connection_results_expose_only_opaque_refs_and_compatibility()
    {
        var receipt = new AgentWorkspaceLayoutReceipt(
            BuiltInAgentTools.ConnectionsList,
            Window(),
            Workspace(),
            4,
            5,
            null,
            null,
            null,
            [new AgentWorkspaceConnectionOption(
                "connection_opaque",
                "Local terminal",
                "Local",
                [PanelKind.Terminal, PanelKind.Docker])]);

        var json = WorkspaceLayoutAgentToolResultJson.Success(receipt);

        Assert.Contains("\"connection_ref\":\"connection_opaque\"", json);
        Assert.Contains("\"supported_panel_kinds\":[\"terminal\",\"docker\"]", json);
        Assert.DoesNotContain("endpoint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Receipts_have_operation_specific_codes_and_bounded_identity_only()
    {
        var receipt = new AgentWorkspaceLayoutReceipt(
            BuiltInAgentTools.PanelSplit,
            Window(),
            Workspace(),
            workspaceRevision: 4,
            graphSequence: 5,
            Tab(),
            new PanelInstanceId("created-panel"),
            PanelKind.Browser);
        var json = WorkspaceLayoutAgentToolResultJson.Success(receipt);

        Assert.Equal("panel_split", WorkspaceLayoutAgentToolResultJson.SuccessStableCode(receipt));
        Assert.Contains("\"workspace_revision\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"panel_id\":\"created-panel\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_layout_outcome_is_explicitly_not_retryable()
    {
        using var document = JsonDocument.Parse(
            WorkspaceLayoutAgentToolResultJson.Failure(
                new HostError(
                    HostErrorCode.EngineFailed,
                    WorkspaceLayoutAgentToolResultJson.OutcomeUnknownStableCode,
                    "The applied layout could not be verified.")));

        var error = document.RootElement.GetProperty("error");
        Assert.Equal(
            WorkspaceLayoutAgentToolResultJson.OutcomeUnknownStableCode,
            error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void Known_panel_startup_failure_does_not_become_outcome_unknown()
    {
        using var document = JsonDocument.Parse(
            WorkspaceLayoutAgentToolResultJson.Failure(
                new HostError(
                    HostErrorCode.InvalidRequest,
                    "workspace_panel_startup_failed",
                    "The panel session could not be started.")));

        Assert.Equal(
            "workspace_panel_startup_failed",
            document.RootElement.GetProperty("error").GetString());
    }

    private static AgentToolProposal Proposal(string toolName, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        var constructor = typeof(AgentToolProposal).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(string), typeof(long), typeof(string), typeof(string), typeof(JsonElement)],
            null)!;
        return (AgentToolProposal)constructor.Invoke(
            ["layout-proposal", 1L, "provider-call", toolName, document.RootElement]);
    }

    private static AgentContextSnapshot Context()
    {
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Workspace",
                [new TabInstance(
                    Tab(),
                    "Tab",
                    [new PanelInstance(Panel(), PanelKind.Statistics, "Panel")],
                    Panel())],
                Tab()),
            revision: 3,
            lastSequence: 4);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [AgentContextPanel.ForGraphPanel(graph, Tab(), Panel(), null)],
            DateTimeOffset.UnixEpoch);
    }

    private static WindowInstanceId Window() => new("layout-window");
    private static WorkspaceInstanceId Workspace() => new("layout-workspace");
    private static TabInstanceId Tab() => new("layout-tab");
    private static PanelInstanceId Panel() => new("layout-panel");
}
