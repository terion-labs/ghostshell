using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class MultiPanelTerminalAgentToolContractTests
{
    [Fact]
    public void OneActiveTerminalBroadScopeStillRequiresEnumeratedPanelSelection()
    {
        var panel = ContextPanel(
            "one",
            "panel-one",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalSendChord,
            SessionCapabilities.TerminalInterrupt);

        var exactTools = TerminalAgentToolSet.For(panel);
        var scopedTools = TerminalAgentToolSet.For([panel]);

        Assert.Equal(
            exactTools.Select(tool => tool.Name),
            scopedTools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(
            exactTools,
            exactTool =>
            {
                Assert.DoesNotContain(
                    "panel_id",
                    exactTool.InputSchema.GetRawText(),
                    StringComparison.Ordinal);
                var scopedTool = Assert.Single(
                    scopedTools,
                    candidate => string.Equals(candidate.Name, exactTool.Name, StringComparison.Ordinal));
                Assert.Equal(
                    [panel.PanelId.Value],
                    PanelIds(scopedTools, scopedTool.Name));
                Assert.Contains(
                    scopedTool.InputSchema
                        .GetProperty("required")
                        .EnumerateArray(),
                    requirement => string.Equals(requirement.GetString(), "panel_id", StringComparison.Ordinal));
            });
    }

    [Fact]
    public async Task TwoPanelToolsRouteToTheFreshlySelectedPanel()
    {
        var readPanel = ContextPanel(
            "read",
            "panel-read",
            SessionCapabilities.TerminalReadScreen);
        var writePanel = ContextPanel(
            "write",
            "panel-write",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWrite);
        AgentContextPanel[] scope = [readPanel, writePanel];

        var readProposal = await ProposalAsync(
            BuiltInAgentTools.TerminalReadScreen,
            JsonSerializer.Serialize(new
            {
                panel_id = readPanel.PanelId.Value,
            }));
        var writeProposal = await ProposalAsync(
            BuiltInAgentTools.TerminalSendText,
            JsonSerializer.Serialize(new
            {
                panel_id = writePanel.PanelId.Value,
                text = "status",
            }));

        var parsedRead = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(readProposal, scope));
        var parsedWrite = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(writeProposal, scope));

        Assert.Equal(readPanel.PanelId, parsedRead.PanelId);
        Assert.IsType<TerminalAgentIntent.ReadScreen>(parsedRead.Intent);
        Assert.Equal(writePanel.PanelId, parsedWrite.PanelId);
        Assert.Equal(
            "status",
            Assert.IsType<TerminalAgentIntent.SendText>(parsedWrite.Intent).Text);
    }

    [Fact]
    public void MultiPanelSchemasFilterIdsByTheExactToolCapability()
    {
        var readWaitPanel = ContextPanel(
            "read",
            "panel-read",
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait);
        var mutationPanel = ContextPanel(
            "mutation",
            "panel-mutation",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalPaste,
            SessionCapabilities.TerminalInterrupt);

        var tools = TerminalAgentToolSet.For(
            [readWaitPanel, mutationPanel]);

        Assert.Equal(
            [readWaitPanel.PanelId.Value, mutationPanel.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.TerminalReadScreen));
        Assert.Equal(
            [readWaitPanel.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.TerminalWait));
        Assert.Equal(
            [mutationPanel.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.TerminalSendText));
        Assert.Equal(
            [mutationPanel.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.TerminalPaste));
        Assert.Equal(
            [mutationPanel.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.TerminalInterrupt));
        Assert.DoesNotContain(
            tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendKeys, StringComparison.Ordinal));
        Assert.All(
            tools,
            tool => Assert.Contains(
                tool.InputSchema.GetProperty("required").EnumerateArray(),
                requirement => string.Equals(requirement.GetString(), "panel_id", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SelectionRejectsOmittedUnknownAndNonPanelIds()
    {
        var first = ContextPanel(
            "first",
            "panel-first",
            SessionCapabilities.TerminalReadScreen);
        var second = ContextPanel(
            "second",
            "panel-second",
            SessionCapabilities.TerminalReadScreen);
        AgentContextPanel[] scope = [first, second];
        var rejectedIds = new[]
        {
            "panel-missing",
            first.SessionId.GetValueOrDefault().Value,
            first.WindowId.Value,
            first.WorkspaceId.Value,
        };

        var omitted = await ProposalAsync(
            BuiltInAgentTools.TerminalReadScreen,
            "{}");
        AssertInvalidSelection(
            TerminalAgentToolParser.Parse(omitted, scope));

        foreach (var rejectedId in rejectedIds)
        {
            var proposal = await ProposalAsync(
                BuiltInAgentTools.TerminalReadScreen,
                JsonSerializer.Serialize(new
                {
                    panel_id = rejectedId,
                }));
            AssertInvalidSelection(
                TerminalAgentToolParser.Parse(proposal, scope));
        }
    }

    [Fact]
    public async Task SelectionRevalidatesTheChosenPanelsCapabilityAndInputBarrier()
    {
        var readPanel = ContextPanel(
            "read",
            "panel-read",
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWrite);
        var mutationPanel = ContextPanel(
            "mutation",
            "panel-mutation",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalWrite);
        AgentContextPanel[] scope = [readPanel, mutationPanel];
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalSendText,
            JsonSerializer.Serialize(new
            {
                panel_id = readPanel.PanelId.Value,
                text = "unsafe",
            }));

        AssertInvalidSelection(
            TerminalAgentToolParser.Parse(proposal, scope));
    }

    [Fact]
    public async Task Paste_routes_only_to_a_freshly_eligible_selected_panel()
    {
        var noBarrier = ContextPanel(
            "no-barrier",
            "panel-no-barrier",
            SessionCapabilities.TerminalPaste);
        var eligible = ContextPanel(
            "eligible",
            "panel-eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalPaste);
        AgentContextPanel[] scope = [noBarrier, eligible];
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalPaste,
            JsonSerializer.Serialize(new
            {
                panel_id = eligible.PanelId.Value,
                text = "first\nsecond",
            }));

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal, scope));

        Assert.Equal(eligible.PanelId, parsed.PanelId);
        Assert.Equal(
            "first\nsecond",
            Assert.IsType<TerminalAgentIntent.Paste>(parsed.Intent).Text);
    }

    [Fact]
    public async Task DuplicatePanelSelectionFailsClosed()
    {
        var providerResult = await RunProviderAsync(
            BuiltInAgentTools.TerminalReadScreen,
            """
            {
              "panel_id": "panel-first",
              "panel_id": "panel-second"
            }
            """);

        Assert.False(providerResult.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            providerResult.ErrorCode);

        var first = ContextPanel(
            "first",
            "panel-shared",
            SessionCapabilities.TerminalReadScreen);
        var second = ContextPanel(
            "second",
            "panel-shared",
            SessionCapabilities.TerminalReadScreen);
        Assert.Throws<ArgumentException>(
            () => TerminalAgentToolSet.For([first, second]));
    }

    [Fact]
    public void DynamicSchemasEscapeBoundedPanelIdsAsJsonValues()
    {
        var specialId = """panel-"]},"additionalProperties":true,"x":"\""";
        var maximumId = new string('p', 256);
        var special = ContextPanel(
            "special",
            specialId,
            SessionCapabilities.TerminalReadScreen);
        var maximum = ContextPanel(
            "maximum",
            maximumId,
            SessionCapabilities.TerminalReadScreen);

        var tools = TerminalAgentToolSet.For([special, maximum]);
        var readTool = Assert.Single(
            tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalReadScreen, StringComparison.Ordinal));

        Assert.False(
            readTool.InputSchema
                .GetProperty("additionalProperties")
                .GetBoolean());
        Assert.Equal(
            [specialId, maximumId],
            PanelIds(tools, BuiltInAgentTools.TerminalReadScreen));
        Assert.Throws<ArgumentException>(
            () => TerminalAgentToolSet.For(
                [.. Enumerable.Repeat(special, 257)]));
    }

    [Fact]
    public async Task ExactParsersDoNotRequireSelectionButBroadParserDoes()
    {
        var panel = ContextPanel(
            "one",
            "panel-one",
            SessionCapabilities.TerminalReadScreen);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalReadScreen,
            "{}");

        var contextlessExact = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var contextualExact = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal, panel));
        AssertInvalidSelection(
            TerminalAgentToolParser.Parse(proposal, [panel]));
        var selectedProposal = await ProposalAsync(
            BuiltInAgentTools.TerminalReadScreen,
            JsonSerializer.Serialize(new
            {
                panel_id = panel.PanelId.Value,
            }));
        var scoped = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(selectedProposal, [panel]));

        Assert.Null(contextlessExact.PanelId);
        Assert.Equal(panel.PanelId, contextualExact.PanelId);
        Assert.Equal(panel.PanelId, scoped.PanelId);
    }

    private static string[] PanelIds(
        ImmutableArray<AgentToolDefinition> tools,
        string toolName) =>
        [.. tools
            .Single(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))
            .InputSchema
            .GetProperty("properties")
            .GetProperty("panel_id")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)];

    private static void AssertInvalidSelection(
        TerminalAgentIntentResult result)
    {
        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(result);
        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var result = await RunProviderAsync(name, arguments);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static async Task<AgentTurnResult> RunProviderAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("run-multi-panel"));
        return await session.RunTurnAsync(
            "Use the terminal tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test terminal tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private static AgentContextPanel ContextPanel(
        string suffix,
        string panelIdValue,
        params string[] capabilities)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId(panelIdValue);
        var panel = new PanelInstance(
            panelId,
            PanelKind.Terminal,
            $"Terminal {suffix}",
            sessionId);
        var tab = new TabInstance(
            tabId,
            $"Tab {suffix}",
            [panel],
            panelId);
        var workspace = new WorkspaceInstance(
            workspaceId,
            $"Workspace {suffix}",
            [tab],
            tabId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            workspace,
            revision: 3,
            lastSequence: 3);
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
            Revision: 5,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private sealed class ToolProvider(string name, string arguments) : IAgentProvider
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
                "call-multi-panel",
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
