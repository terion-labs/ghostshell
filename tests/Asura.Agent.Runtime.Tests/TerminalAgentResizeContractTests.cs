using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class TerminalAgentResizeContractTests
{
    [Fact]
    public void SchemaExposesOnlyBoundedGridDimensionsToAnEligibleAttachment()
    {
        var panel = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalResize);
        HashSet<PanelInstanceId> eligiblePanelIds = [panel.PanelId];

        Assert.DoesNotContain(
            TerminalAgentToolSet.For(panel),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalResize, StringComparison.Ordinal));

        var resize = Assert.Single(
            TerminalAgentToolSet.For(panel, eligiblePanelIds),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalResize, StringComparison.Ordinal));
        var schema = resize.InputSchema;
        var properties = schema.GetProperty("properties");

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["columns", "rows"],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            2,
            properties
                .GetProperty("columns")
                .GetProperty("minimum")
                .GetInt32());
        Assert.Equal(
            1,
            properties
                .GetProperty("rows")
                .GetProperty("minimum")
                .GetInt32());
        Assert.All(
            properties.EnumerateObject(),
            property => Assert.Equal(
                1_000,
                property.Value.GetProperty("maximum").GetInt32()));
        Assert.Equal(
            ["columns", "rows"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.DoesNotContain(
            "attachment",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "logical",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "render_scale",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            TerminalAgentToolSet.Supports(
                panel,
                BuiltInAgentTools.TerminalResize));
        Assert.True(
            TerminalAgentToolSet.Supports(
                panel,
                BuiltInAgentTools.TerminalResize,
                eligiblePanelIds));
        Assert.False(TerminalAgentToolSet.SupportsMutations(panel));
        Assert.True(
            TerminalAgentToolSet.SupportsMutations(
                panel,
                eligiblePanelIds));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(80, 24)]
    [InlineData(1_000, 1_000)]
    public async Task ParserAcceptsOnlyEligibleBoundedCellDimensions(
        int columns,
        int rows)
    {
        var panel = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalResize);
        HashSet<PanelInstanceId> eligiblePanelIds = [panel.PanelId];
        var proposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                columns,
                rows,
            }));

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(
                proposal,
                panel,
                eligiblePanelIds));
        var resize = Assert.IsType<TerminalAgentIntent.Resize>(parsed.Intent);

        Assert.Equal(panel.PanelId, parsed.PanelId);
        Assert.Equal(columns, resize.Columns);
        Assert.Equal(rows, resize.Rows);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"columns\":80}")]
    [InlineData("{\"rows\":24}")]
    [InlineData("{\"columns\":0,\"rows\":24}")]
    [InlineData("{\"columns\":1,\"rows\":24}")]
    [InlineData("{\"columns\":1001,\"rows\":24}")]
    [InlineData("{\"columns\":80,\"rows\":-1}")]
    [InlineData("{\"columns\":80,\"rows\":1001}")]
    [InlineData("{\"columns\":80.5,\"rows\":24}")]
    [InlineData("{\"columns\":\"80\",\"rows\":24}")]
    [InlineData("{\"columns\":80,\"rows\":24,\"extra\":true}")]
    [InlineData("{\"columns\":80,\"rows\":24,\"attachment_id\":\"attachment-1\"}")]
    [InlineData("{\"columns\":80,\"rows\":24,\"logical_width\":800}")]
    [InlineData("{\"columns\":80,\"rows\":24,\"logical_height\":600}")]
    [InlineData("{\"columns\":80,\"rows\":24,\"render_scale\":2}")]
    public async Task ParserRejectsMissingUnknownAndInvalidResizeFields(
        string arguments)
    {
        var panel = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalResize);
        HashSet<PanelInstanceId> eligiblePanelIds = [panel.PanelId];
        var proposal = await ProposalAsync(arguments);

        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(
                proposal,
                panel,
                eligiblePanelIds));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task ResizeFailsClosedWithoutFreshAttachmentEligibility()
    {
        var panel = ContextPanel(
            "capable",
            SessionCapabilities.TerminalResize);
        var proposal = await ProposalAsync(
            """
            {
              "columns": 80,
              "rows": 24
            }
            """);

        var unscoped = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal));
        var exact = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal, panel));
        var broad = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal, [panel]));

        Assert.Equal("tool_not_available", unscoped.StableCode);
        Assert.Equal("tool_not_available", exact.StableCode);
        Assert.Equal("invalid_tool_arguments", broad.StableCode);
    }

    [Fact]
    public async Task BroadScopeAdvertisesAndRoutesOnlyExactEligiblePanelIds()
    {
        var eligible = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalResize);
        var noAttachment = ContextPanel(
            "no-attachment",
            SessionCapabilities.TerminalResize);
        var noCapability = ContextPanel("no-capability");
        AgentContextPanel[] scope = [eligible, noAttachment, noCapability];
        HashSet<PanelInstanceId> eligiblePanelIds =
        [
            eligible.PanelId,
            noCapability.PanelId,
        ];

        var tool = Assert.Single(
            TerminalAgentToolSet.For(scope, eligiblePanelIds),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalResize, StringComparison.Ordinal));
        var schema = tool.InputSchema;

        Assert.Equal(
            [eligible.PanelId.Value],
            schema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            ["columns", "rows", "panel_id"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);

        var acceptedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                columns = 120,
                rows = 40,
                panel_id = eligible.PanelId.Value,
            }));
        var accepted = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(
                acceptedProposal,
                scope,
                eligiblePanelIds));
        Assert.Equal(eligible.PanelId, accepted.PanelId);

        var rejectedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                columns = 120,
                rows = 40,
                panel_id = noAttachment.PanelId.Value,
            }));
        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(
                rejectedProposal,
                scope,
                eligiblePanelIds));
        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    private static async Task<AgentToolProposal> ProposalAsync(string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("resize-run"));
        var result = await session.RunTurnAsync(
            "Resize the terminal.",
            [
                new AgentToolDefinition(
                    BuiltInAgentTools.TerminalResize,
                    "Test resize tool.",
                    """
                    {
                      "type": "object",
                      "additionalProperties": true
                    }
                    """u8.ToArray()),
            ],
            new ToolProvider(arguments),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentContextPanel ContextPanel(
        string suffix,
        params string[] capabilities)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId($"panel-{suffix}");
        var panel = new PanelInstance(
            panelId,
            PanelKind.Terminal,
            $"Terminal {suffix}",
            sessionId);
        var tab = new TabInstance(tabId, "Terminals", [panel], panelId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                "Operations",
                [tab],
                tabId),
            revision: 2,
            lastSequence: 2);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.Terminal,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet(capabilities),
            Revision: 4,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private sealed class ToolProvider(string arguments) : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.ToolCallStarted(
                0,
                "resize-call",
                ProviderToolName.FromInternal(BuiltInAgentTools.TerminalResize));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(
                0,
                arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}
