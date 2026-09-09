using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class TerminalAgentMouseContractTests
{
    [Theory]
    [InlineData("move", TerminalMouseButton.None, TerminalMouseEventKind.Move)]
    [InlineData("left_down", TerminalMouseButton.Left, TerminalMouseEventKind.Down)]
    [InlineData("left_up", TerminalMouseButton.Left, TerminalMouseEventKind.Up)]
    [InlineData("left_drag", TerminalMouseButton.Left, TerminalMouseEventKind.Drag)]
    [InlineData("middle_down", TerminalMouseButton.Middle, TerminalMouseEventKind.Down)]
    [InlineData("middle_up", TerminalMouseButton.Middle, TerminalMouseEventKind.Up)]
    [InlineData("middle_drag", TerminalMouseButton.Middle, TerminalMouseEventKind.Drag)]
    [InlineData("right_down", TerminalMouseButton.Right, TerminalMouseEventKind.Down)]
    [InlineData("right_up", TerminalMouseButton.Right, TerminalMouseEventKind.Up)]
    [InlineData("right_drag", TerminalMouseButton.Right, TerminalMouseEventKind.Drag)]
    [InlineData("wheel_up", TerminalMouseButton.WheelUp, TerminalMouseEventKind.WheelUp)]
    [InlineData("wheel_down", TerminalMouseButton.WheelDown, TerminalMouseEventKind.WheelDown)]
    public async Task ParsesOnlyTheClosedMouseEventVocabulary(
        string eventName,
        TerminalMouseButton expectedButton,
        TerminalMouseEventKind expectedKind)
    {
        var proposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                @event = eventName,
                column = 1_000_000,
                row = 0,
                expected_content_revision = 42,
                modifiers = new[] { "shift", "meta" },
            }));

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var intent = Assert.IsType<TerminalAgentIntent.SendMouse>(parsed.Intent);
        var mouse = intent.MouseInput;

        Assert.Equal(expectedButton, mouse.Button);
        Assert.Equal(expectedKind, mouse.Kind);
        Assert.Equal(1_000_000, mouse.Column);
        Assert.Equal(0, mouse.Row);
        Assert.Equal(
            TerminalKeyModifiers.Shift | TerminalKeyModifiers.Meta,
            mouse.Modifiers);
        Assert.Equal(42, intent.ExpectedContentRevision);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"event\":\"left_down\",\"column\":0}")]
    [InlineData("{\"event\":\"click\",\"column\":0,\"row\":0}")]
    [InlineData("{\"event\":\"left_down\",\"column\":-1,\"row\":0}")]
    [InlineData("{\"event\":\"left_down\",\"column\":0,\"row\":1000001}")]
    [InlineData("{\"event\":\"left_down\",\"column\":0.5,\"row\":0}")]
    [InlineData("{\"event\":\"left_down\",\"column\":0,\"row\":0,\"modifiers\":[\"alt\",\"alt\"]}")]
    [InlineData("{\"event\":\"left_down\",\"column\":0,\"row\":0,\"extra\":true}")]
    [InlineData("{\"event\":\"left_down\",\"column\":0,\"row\":0,\"expected_content_revision\":-1}")]
    public async Task RejectsMalformedOrOutOfRangeMouseEvents(string arguments)
    {
        var proposal = await ProposalAsync(arguments);

        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void SchemaIsClosedBoundedAndContainsNoHostAuthority()
    {
        var panel = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse);
        var tool = Assert.Single(
            TerminalAgentToolSet.For(panel),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSendMouse, StringComparison.Ordinal));
        var schema = tool.InputSchema;
        var properties = schema.GetProperty("properties");

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            [
                "event",
                "column",
                "row",
                "expected_content_revision",
                "modifiers",
            ],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            0,
            properties.GetProperty("column").GetProperty("minimum").GetInt32());
        Assert.Equal(
            1_000_000,
            properties.GetProperty("row").GetProperty("maximum").GetInt32());
        Assert.Equal(
            ["event", "column", "row", "expected_content_revision"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        Assert.DoesNotContain(
            "session",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "lease",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MouseToolRequiresCapabilityRevisionBindingAndPhysicalInputBarrier()
    {
        var eligible = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse);
        var noBarrier = ContextPanel(
            "no-barrier",
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse);
        var noMouse = ContextPanel(
            "no-mouse",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalRevisionBoundMouse);
        var noRevisionBinding = ContextPanel(
            "no-revision",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalMouse);

        Assert.True(TerminalAgentToolSet.Supports(
            eligible,
            BuiltInAgentTools.TerminalSendMouse));
        Assert.True(TerminalAgentToolSet.SupportsMutations(eligible));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noBarrier),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendMouse, StringComparison.Ordinal));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noMouse),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendMouse, StringComparison.Ordinal));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noRevisionBinding),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendMouse, StringComparison.Ordinal));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noBarrier));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noMouse));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noRevisionBinding));
    }

    [Fact]
    public async Task MultiPanelSchemaAndParserAllowOnlyEligiblePanelIds()
    {
        var eligible = ContextPanel(
            "eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse);
        var noBarrier = ContextPanel(
            "no-barrier",
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse);
        var tools = TerminalAgentToolSet.For([eligible, noBarrier]);
        var mouseTool = Assert.Single(
            tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendMouse, StringComparison.Ordinal));

        Assert.Equal(
            [eligible.PanelId.Value],
            mouseTool.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);

        var acceptedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = eligible.PanelId.Value,
                @event = "wheel_down",
                column = 9,
                row = 4,
                expected_content_revision = 7,
            }));
        var accepted = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(
                acceptedProposal,
                [eligible, noBarrier]));
        Assert.Equal(eligible.PanelId, accepted.PanelId);

        var rejectedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = noBarrier.PanelId.Value,
                @event = "wheel_down",
                column = 9,
                row = 4,
                expected_content_revision = 7,
            }));
        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(
                rejectedProposal,
                [eligible, noBarrier]));
        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    private static async Task<AgentToolProposal> ProposalAsync(string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("mouse-run"));
        var result = await session.RunTurnAsync(
            "Use the terminal mouse.",
            [
                new AgentToolDefinition(
                    BuiltInAgentTools.TerminalSendMouse,
                    "Test mouse tool.",
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
                "mouse-call",
                ProviderToolName.FromInternal(BuiltInAgentTools.TerminalSendMouse));
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
