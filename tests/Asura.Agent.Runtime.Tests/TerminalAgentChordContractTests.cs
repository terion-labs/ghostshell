using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class TerminalAgentChordContractTests
{
    [Theory]
    [InlineData("d", "control", 'd', TerminalCharacterChordModifier.Control)]
    [InlineData("x", "alt", 'x', TerminalCharacterChordModifier.Alt)]
    public async Task ParsesOnlyCanonicalTypedCharacterChords(
        string character,
        string modifier,
        char expectedCharacter,
        TerminalCharacterChordModifier expectedModifier)
    {
        var proposal = await ProposalAsync(
            JsonSerializer.Serialize(new { character, modifier }));

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var chord = Assert.IsType<TerminalAgentIntent.SendChord>(parsed.Intent)
            .Chord;

        Assert.Equal(expectedCharacter, chord.Character);
        Assert.Equal(expectedModifier, chord.Modifier);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"character\":\"d\"}")]
    [InlineData("{\"modifier\":\"control\"}")]
    [InlineData("{\"character\":\"D\",\"modifier\":\"control\"}")]
    [InlineData("{\"character\":\"dd\",\"modifier\":\"control\"}")]
    [InlineData("{\"character\":\"4\",\"modifier\":\"control\"}")]
    [InlineData("{\"character\":\"é\",\"modifier\":\"control\"}")]
    [InlineData("{\"character\":\"d\",\"modifier\":\"shift\"}")]
    [InlineData("{\"character\":\"d\",\"modifier\":[\"control\",\"alt\"]}")]
    [InlineData("{\"character\":\"d\",\"modifier\":\"control\",\"bytes\":[4]}")]
    [InlineData("{\"character\":\"d\",\"modifier\":\"control\",\"text\":\"\\u0004\"}")]
    [InlineData("{\"character\":\"d\",\"modifier\":\"control\",\"sequence\":\"\\\\x04\"}")]
    [InlineData("{\"character\":\"d\",\"modifier\":\"control\",\"escape\":\"\\\\u001b\"}")]
    public async Task RejectsAliasesCombinationsAndRawInputEscapeHatches(
        string arguments)
    {
        var proposal = await ProposalAsync(arguments);

        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void SchemaIsClosedCanonicalAndContainsNoHostAuthority()
    {
        var tool = Assert.Single(
            TerminalAgentToolSet.For(Panel(
                "eligible",
                SessionCapabilities.TerminalAgentInputBarrier,
                SessionCapabilities.TerminalSendChord)),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSendChord, StringComparison.Ordinal));
        var schema = tool.InputSchema;
        var properties = schema.GetProperty("properties");

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["character", "modifier"],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            Enumerable.Range('a', 26).Select(value => ((char)value).ToString()),
            properties.GetProperty("character")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            ["control", "alt"],
            properties.GetProperty("modifier")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            ["character", "modifier"],
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
        Assert.DoesNotContain(
            "bytes",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "sequence",
            schema.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolRequiresBothChordCapabilityAndPhysicalInputBarrier()
    {
        var eligible = Panel(
            "eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalSendChord);
        var noBarrier = Panel(
            "no-barrier",
            SessionCapabilities.TerminalSendChord);
        var noChord = Panel(
            "no-chord",
            SessionCapabilities.TerminalAgentInputBarrier);

        Assert.True(TerminalAgentToolSet.Supports(
            eligible,
            BuiltInAgentTools.TerminalSendChord));
        Assert.True(TerminalAgentToolSet.SupportsMutations(eligible));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noBarrier),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendChord, StringComparison.Ordinal));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noChord),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendChord, StringComparison.Ordinal));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noBarrier));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noChord));
    }

    [Fact]
    public async Task BroadSchemaAndParserAllowOnlyFreshlyEligiblePanelIds()
    {
        var eligible = Panel(
            "eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalSendChord);
        var noBarrier = Panel(
            "no-barrier",
            SessionCapabilities.TerminalSendChord);
        var panels = new[] { eligible, noBarrier };
        var tool = Assert.Single(
            TerminalAgentToolSet.For(panels),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSendChord, StringComparison.Ordinal));

        Assert.Equal(
            [eligible.PanelId.Value],
            tool.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);

        var acceptedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = eligible.PanelId.Value,
                character = "r",
                modifier = "control",
            }));
        var accepted = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(acceptedProposal, panels));
        Assert.Equal(eligible.PanelId, accepted.PanelId);
        Assert.Equal(
            'r',
            Assert.IsType<TerminalAgentIntent.SendChord>(accepted.Intent)
                .Chord.Character);

        var rejectedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = noBarrier.PanelId.Value,
                character = "r",
                modifier = "control",
            }));
        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(rejectedProposal, panels));
        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task OneTerminalBroadChordScopeStillRequiresPanelId()
    {
        var panel = Panel(
            "eligible",
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalSendChord);
        var exactTool = Assert.Single(
            TerminalAgentToolSet.For(panel),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSendChord, StringComparison.Ordinal));
        var broadTool = Assert.Single(
            TerminalAgentToolSet.For([panel]),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSendChord, StringComparison.Ordinal));

        Assert.DoesNotContain(
            "panel_id",
            exactTool.InputSchema.GetRawText(),
            StringComparison.Ordinal);
        Assert.Equal(
            [panel.PanelId.Value],
            broadTool.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            ["character", "modifier", "panel_id"],
            broadTool.InputSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);

        var exactProposal = await ProposalAsync(
            """
            {"character":"d","modifier":"control"}
            """);
        var exact = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(exactProposal, panel));
        var omitted = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(exactProposal, [panel]));
        var selectedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                character = "d",
                modifier = "control",
                panel_id = panel.PanelId.Value,
            }));
        var selected = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(selectedProposal, [panel]));

        Assert.Equal(panel.PanelId, exact.PanelId);
        Assert.Equal("invalid_tool_arguments", omitted.StableCode);
        Assert.Equal(panel.PanelId, selected.PanelId);
        Assert.IsType<TerminalAgentIntent.SendChord>(selected.Intent);
    }

    private static async Task<AgentToolProposal> ProposalAsync(string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("chord-run"));
        var result = await session.RunTurnAsync(
            "Send the terminal chord.",
            [
                new AgentToolDefinition(
                    BuiltInAgentTools.TerminalSendChord,
                    "Test chord tool.",
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

    private static AgentContextPanel Panel(
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
                "chord-call",
                ProviderToolName.FromInternal(BuiltInAgentTools.TerminalSendChord));
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
