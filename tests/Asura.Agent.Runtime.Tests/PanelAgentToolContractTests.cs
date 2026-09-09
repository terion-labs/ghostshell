using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class PanelAgentToolContractTests
{
    [Fact]
    public void Exact_and_broad_schemas_keep_panel_selection_host_bounded()
    {
        var live = ContextPanel("live", SessionLifecycle.Active);
        var starting = ContextPanel("starting", SessionLifecycle.Starting);
        var exact = new AgentContextSnapshot(
            ExactTarget(live),
            [live],
            DateTimeOffset.UnixEpoch);
        var broad = new AgentContextSnapshot(
            new AgentTarget.Workspace(
                live.WindowId,
                live.WorkspaceId),
            [live],
            DateTimeOffset.UnixEpoch);

        var exactTools = PanelAgentToolSet.For(exact);
        var broadTools = PanelAgentToolSet.For(broad);

        Assert.Equal(
            [BuiltInAgentTools.PanelInspect, BuiltInAgentTools.PanelFocus],
            exactTools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(
            exactTools,
            tool =>
            {
                Assert.Empty(
                    tool.InputSchema.GetProperty("properties")
                        .EnumerateObject());
                Assert.Empty(
                    tool.InputSchema.GetProperty("required")
                        .EnumerateArray());
                Assert.False(
                    tool.InputSchema.GetProperty("additionalProperties")
                        .GetBoolean());
            });
        Assert.Equal(
            [BuiltInAgentTools.PanelInspect, BuiltInAgentTools.PanelFocus],
            broadTools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(
            broadTools,
            tool =>
            {
                var panel = tool.InputSchema
                    .GetProperty("properties")
                    .GetProperty("panel_id");
                Assert.Equal("string", panel.GetProperty("type").GetString());
                Assert.Equal(
                    [live.PanelId.Value],
                    panel.GetProperty("enum")
                        .EnumerateArray()
                        .Select(value => value.GetString()), StringComparer.Ordinal);
                Assert.Equal(
                    ["panel_id"],
                    tool.InputSchema.GetProperty("required")
                        .EnumerateArray()
                        .Select(value => value.GetString()), StringComparer.Ordinal);
                Assert.False(
                    tool.InputSchema.GetProperty("additionalProperties")
                        .GetBoolean());
            });
        Assert.Empty(
            PanelAgentToolSet.For(
                new AgentContextSnapshot(
                    new AgentTarget.Workspace(
                        starting.WindowId,
                        starting.WorkspaceId),
                    [starting],
                    DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public async Task Exact_and_broad_parsers_enforce_the_advertised_scope()
    {
        var panel = ContextPanel("selected", SessionLifecycle.Active);
        var exact = new AgentContextSnapshot(
            ExactTarget(panel),
            [panel],
            DateTimeOffset.UnixEpoch);
        var broad = new AgentContextSnapshot(
            new AgentTarget.Workspace(
                panel.WindowId,
                panel.WorkspaceId),
            [panel],
            DateTimeOffset.UnixEpoch);
        var empty = await ProposalAsync(
            BuiltInAgentTools.PanelInspect,
            "{}");
        var selected = await ProposalAsync(
            BuiltInAgentTools.PanelFocus,
            JsonSerializer.Serialize(new
            {
                panel_id = panel.PanelId.Value,
            }));

        var exactParsed = Assert.IsType<PanelAgentIntentResult.Parsed>(
            PanelAgentToolParser.Parse(empty, exact));
        Assert.IsType<PanelAgentIntent.Inspect>(exactParsed.Intent);
        Assert.Equal(panel.PanelId, exactParsed.PanelId);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<PanelAgentIntentResult.Rejected>(
                PanelAgentToolParser.Parse(selected, exact)).StableCode);

        var broadParsed = Assert.IsType<PanelAgentIntentResult.Parsed>(
            PanelAgentToolParser.Parse(selected, broad));
        Assert.IsType<PanelAgentIntent.Focus>(broadParsed.Intent);
        Assert.Equal(panel.PanelId, broadParsed.PanelId);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<PanelAgentIntentResult.Rejected>(
                PanelAgentToolParser.Parse(empty, broad)).StableCode);

        var outside = await ProposalAsync(
            BuiltInAgentTools.PanelInspect,
            """{"panel_id":"outside-panel"}""");
        var widened = await ProposalAsync(
            BuiltInAgentTools.PanelInspect,
            $$"""{"panel_id":"{{panel.PanelId.Value}}","workspace_id":"outside"}""");
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<PanelAgentIntentResult.Rejected>(
                PanelAgentToolParser.Parse(outside, broad)).StableCode);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<PanelAgentIntentResult.Rejected>(
                PanelAgentToolParser.Parse(widened, broad)).StableCode);
    }

    [Fact]
    public void Inspection_result_marks_redacts_and_bounds_untrusted_metadata()
    {
        var panel = ContextPanel(
            "result",
            SessionLifecycle.Active,
            workspaceTitle: new string('w', 200),
            panelTitle: "token=do-not-expose");

        using var document = JsonDocument.Parse(
            PanelAgentToolResultJson.Success(
                new AgentPanelActionResult.Inspected(panel),
                panel.PanelId));
        var root = document.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "untrusted_panel_metadata",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(
            panel.PanelId.Value,
            root.GetProperty("panel_id").GetString());
        Assert.Equal(
            panel.SessionId!.Value.Value,
            root.GetProperty("session_id").GetString());
        Assert.Equal(
            panel.WorkspaceRevision,
            root.GetProperty("workspace_revision").GetInt64());
        Assert.Equal(1, root.GetProperty("redactions").GetInt32());
        Assert.DoesNotContain(
            "do-not-expose",
            root.GetRawText(),
            StringComparison.Ordinal);
        Assert.True(
            Encoding.UTF8.GetByteCount(
                root.GetProperty("workspace_title").GetString()!) <= 128);
    }

    [Fact]
    public void Focus_result_contains_only_the_committed_host_receipt()
    {
        var panel = ContextPanel("focused", SessionLifecycle.Active);
        var receipt = new AgentPanelFocusReceipt(
            panel.WindowId,
            panel.WorkspaceId,
            panel.TabId,
            panel.PanelId,
            workspaceRevision: 8,
            graphSequence: 13,
            changed: true);

        using var document = JsonDocument.Parse(
            PanelAgentToolResultJson.Success(
                new AgentPanelActionResult.Focused(receipt),
                panel.PanelId));
        var root = document.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("focused").GetBoolean());
        Assert.True(root.GetProperty("changed").GetBoolean());
        Assert.Equal(8, root.GetProperty("workspace_revision").GetInt64());
        Assert.Equal(13, root.GetProperty("graph_sequence").GetInt64());
        Assert.False(root.TryGetProperty("content_origin", out _));
    }

    private static AgentContextPanel ContextPanel(
        string suffix,
        SessionLifecycle lifecycle,
        string? workspaceTitle = null,
        string? panelTitle = null)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId($"panel-{suffix}");
        var panel = new PanelInstance(
            panelId,
            PanelKind.FileViewer,
            panelTitle ?? $"Panel {suffix}",
            sessionId);
        var tab = new TabInstance(
            tabId,
            $"Tab {suffix}",
            [panel],
            panelId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                workspaceTitle ?? $"Workspace {suffix}",
                [tab],
                tabId),
            revision: 5,
            lastSequence: 7);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.FileViewer,
            lifecycle,
            lifecycle == SessionLifecycle.Active
                ? SessionHealth.Healthy
                : SessionHealth.Starting,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            CapabilitySet.Empty,
            Revision: 3,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private static AgentTarget.Panel ExactTarget(
        AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.TabId,
            panel.PanelId);

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(
            new AgentRunId("panel-contract"));
        var result = await session.RunTurnAsync(
            "Use the panel tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test panel tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private sealed class ToolProvider(
        string name,
        string arguments) : IAgentProvider
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
                "panel-call",
                ProviderToolName.FromInternal(name));
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
