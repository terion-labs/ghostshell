using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class TerminalAgentWaitContractTests
{
    [Fact]
    public void SchemaAdvertisesExactlySixClosedWaitShapesUpToOneHour()
    {
        var panel = ContextPanel("one", "panel-one");
        var wait = TerminalAgentToolSet.For(panel)
            .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalWait, StringComparison.Ordinal));
        var schema = wait.InputSchema;

        Assert.Equal(
            [
                "text",
                "delay_ms",
                "after_content_revision",
                "stable_for_ms",
                "prompt_ready",
                "command_finished",
                "after_shell_event_sequence",
                "timeout_ms",
            ],
            schema
                .GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            [],
            RequiredNames(schema));
        Assert.Equal(
            [
                ["delay_ms"],
                ["text", "timeout_ms"],
                ["after_content_revision", "timeout_ms"],
                ["stable_for_ms", "timeout_ms"],
                ["prompt_ready", "after_shell_event_sequence", "timeout_ms"],
                ["command_finished", "after_shell_event_sequence", "timeout_ms"],
            ],
            schema
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(RequiredNames));
        Assert.Equal(
            3_600_000,
            schema.GetProperty("properties")
                .GetProperty("timeout_ms")
                .GetProperty("maximum")
                .GetInt32());
        Assert.False(
            schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(
            "visual quiescence",
            wait.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "never proof of an idle prompt",
            wait.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "interactive_state_available",
            wait.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesDelayAndAllFiveHostedWaitConditions()
    {
        var delay = AssertParsed<TerminalAgentIntent.WaitForDelay>(
            await ProposalAsync(
                """
                {"delay_ms":3600000}
                """));
        var text = AssertParsed<TerminalAgentIntent.WaitForText>(
            await ProposalAsync(
                """
                {"text":"ready","timeout_ms":3600000}
                """));
        var change = AssertParsed<TerminalAgentIntent.WaitForChange>(
            await ProposalAsync(
                """
                {"after_content_revision":9223372036854775807,"timeout_ms":1}
                """));
        var stable = AssertParsed<TerminalAgentIntent.WaitForStable>(
            await ProposalAsync(
                """
                {"stable_for_ms":125,"timeout_ms":250}
                """));
        var promptReady = AssertParsed<TerminalAgentIntent.WaitForPromptReady>(
            await ProposalAsync(
                """
                {"prompt_ready":true,"after_shell_event_sequence":7,"timeout_ms":3600000}
                """));
        var commandFinished = AssertParsed<TerminalAgentIntent.WaitForCommandFinished>(
            await ProposalAsync(
                """
                {"command_finished":true,"after_shell_event_sequence":9223372036854775807,"timeout_ms":1}
                """));

        Assert.Equal("ready", text.Text);
        Assert.Equal(TimeSpan.FromHours(1), delay.Delay);
        Assert.Equal(TimeSpan.FromHours(1), text.Timeout);
        Assert.Equal(long.MaxValue, change.AfterContentRevision);
        Assert.Equal(TimeSpan.FromMilliseconds(1), change.Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(125), stable.StableFor);
        Assert.Equal(TimeSpan.FromMilliseconds(250), stable.Timeout);
        Assert.Equal(7, promptReady.AfterShellEventSequence);
        Assert.Equal(TimeSpan.FromHours(1), promptReady.Timeout);
        Assert.Equal(long.MaxValue, commandFinished.AfterShellEventSequence);
        Assert.Equal(TimeSpan.FromMilliseconds(1), commandFinished.Timeout);
    }

    [Theory]
    [InlineData("""{"timeout_ms":1000}""")]
    [InlineData("""{"delay_ms":0}""")]
    [InlineData("""{"delay_ms":3600001}""")]
    [InlineData("""{"delay_ms":1000,"timeout_ms":1000}""")]
    [InlineData("""{"text":"ready"}""")]
    [InlineData("""{"text":"","timeout_ms":1000}""")]
    [InlineData("""{"text":"line\nfeed","timeout_ms":1000}""")]
    [InlineData("""{"text":"ready","after_content_revision":1,"timeout_ms":1000}""")]
    [InlineData("""{"text":"ready","stable_for_ms":10,"timeout_ms":1000}""")]
    [InlineData("""{"after_content_revision":1,"stable_for_ms":10,"timeout_ms":1000}""")]
    [InlineData("""{"text":"ready","timeout_ms":1000,"extra":true}""")]
    [InlineData("""{"after_content_revision":-1,"timeout_ms":1000}""")]
    [InlineData("""{"after_content_revision":1.5,"timeout_ms":1000}""")]
    [InlineData("""{"after_content_revision":9223372036854775808,"timeout_ms":1000}""")]
    [InlineData("""{"stable_for_ms":0,"timeout_ms":1000}""")]
    [InlineData("""{"stable_for_ms":1001,"timeout_ms":1000}""")]
    [InlineData("""{"stable_for_ms":30001,"timeout_ms":30000}""")]
    [InlineData("""{"stable_for_ms":1,"timeout_ms":0}""")]
    [InlineData("""{"stable_for_ms":1,"timeout_ms":3600001}""")]
    [InlineData("""{"stable_for_ms":"1","timeout_ms":1000}""")]
    [InlineData("""{"stable_for_ms":1,"timeout_ms":1.5}""")]
    [InlineData("""{"prompt_ready":true,"timeout_ms":1000}""")]
    [InlineData("""{"prompt_ready":false,"after_shell_event_sequence":0,"timeout_ms":1000}""")]
    [InlineData("""{"prompt_ready":true,"after_shell_event_sequence":-1,"timeout_ms":1000}""")]
    [InlineData("""{"prompt_ready":true,"after_shell_event_sequence":1.5,"timeout_ms":1000}""")]
    [InlineData("""{"prompt_ready":true,"after_shell_event_sequence":9223372036854775808,"timeout_ms":1000}""")]
    [InlineData("""{"command_finished":true,"after_shell_event_sequence":0}""")]
    [InlineData("""{"command_finished":true,"after_shell_event_sequence":0,"timeout_ms":3600001}""")]
    [InlineData("""{"prompt_ready":true,"command_finished":true,"after_shell_event_sequence":0,"timeout_ms":1000}""")]
    [InlineData("""{"prompt_ready":true,"after_shell_event_sequence":0,"timeout_ms":1000,"text":"ready"}""")]
    public async Task RejectsInvalidOrAmbiguousWaitShapes(string arguments)
    {
        var proposal = await ProposalAsync(arguments);

        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task DuplicateWaitConditionIsRejectedBeforeRuntimeParsing()
    {
        var result = await RunProviderAsync(
            """
            {
              "stable_for_ms": 10,
              "stable_for_ms": 20,
              "timeout_ms": 100
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            result.ErrorCode);
        Assert.Empty(result.ToolProposals);
    }

    [Fact]
    public async Task StableIntervalMayEqualButNeverExceedTheTimeout()
    {
        var valid = AssertParsed<TerminalAgentIntent.WaitForStable>(
            await ProposalAsync(
                """
                {"stable_for_ms":30000,"timeout_ms":30000}
                """));
        var invalidProposal = await ProposalAsync(
            """
            {"stable_for_ms":2,"timeout_ms":1}
            """);

        Assert.Equal(TimeSpan.FromSeconds(30), valid.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(30), valid.Timeout);
        Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(invalidProposal));
    }

    [Fact]
    public void MultiPanelSchemaAddsSelectionWithoutWeakeningWaitShapes()
    {
        var first = ContextPanel("first", "panel-first");
        var second = ContextPanel("second", "panel-second");
        var wait = TerminalAgentToolSet.For([first, second])
            .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalWait, StringComparison.Ordinal));
        var schema = wait.InputSchema;

        Assert.Equal(
            [first.PanelId.Value, second.PanelId.Value],
            schema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            ["panel_id"],
            RequiredNames(schema));
        Assert.Equal(
            [
                ["delay_ms"],
                ["text", "timeout_ms"],
                ["after_content_revision", "timeout_ms"],
                ["stable_for_ms", "timeout_ms"],
                ["prompt_ready", "after_shell_event_sequence", "timeout_ms"],
                ["command_finished", "after_shell_event_sequence", "timeout_ms"],
            ],
            schema
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(RequiredNames));
        Assert.False(
            schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task MultiPanelParserRoutesEachWaitKindByExactPanelId()
    {
        var first = ContextPanel("first", "panel-first");
        var second = ContextPanel("second", "panel-second");
        AgentContextPanel[] scope = [first, second];
        var changeProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = first.PanelId.Value,
                after_content_revision = 7,
                timeout_ms = 250,
            }));
        var stableProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = second.PanelId.Value,
                stable_for_ms = 100,
                timeout_ms = 250,
            }));
        var commandFinishedProposal = await ProposalAsync(
            JsonSerializer.Serialize(new
            {
                panel_id = first.PanelId.Value,
                command_finished = true,
                after_shell_event_sequence = 9,
                timeout_ms = 500,
            }));

        var change = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(changeProposal, scope));
        var stable = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(stableProposal, scope));
        var commandFinished = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(commandFinishedProposal, scope));

        Assert.Equal(first.PanelId, change.PanelId);
        Assert.Equal(
            7,
            Assert.IsType<TerminalAgentIntent.WaitForChange>(
                change.Intent).AfterContentRevision);
        Assert.Equal(second.PanelId, stable.PanelId);
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            Assert.IsType<TerminalAgentIntent.WaitForStable>(
                stable.Intent).StableFor);
        Assert.Equal(first.PanelId, commandFinished.PanelId);
        Assert.Equal(
            9,
            Assert.IsType<TerminalAgentIntent.WaitForCommandFinished>(
                commandFinished.Intent).AfterShellEventSequence);
    }

    [Fact]
    public async Task ExactPanelParserAcceptsNewWaitsWithoutPanelId()
    {
        var panel = ContextPanel("one", "panel-one");
        var proposal = await ProposalAsync(
            """
            {"after_content_revision":0,"timeout_ms":1000}
            """);

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal, panel));

        Assert.Equal(panel.PanelId, parsed.PanelId);
        Assert.Equal(
            0,
            Assert.IsType<TerminalAgentIntent.WaitForChange>(
                parsed.Intent).AfterContentRevision);
    }

    private static TIntent AssertParsed<TIntent>(
        AgentToolProposal proposal)
        where TIntent : TerminalAgentIntent
    {
        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        Assert.Null(parsed.PanelId);
        return Assert.IsType<TIntent>(parsed.Intent);
    }

    private static string[] RequiredNames(JsonElement schema) =>
        [.. schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString()!)];

    private static async Task<AgentToolProposal> ProposalAsync(
        string arguments)
    {
        var result = await RunProviderAsync(arguments);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static async Task<AgentTurnResult> RunProviderAsync(
        string arguments)
    {
        var session = new NativeAgentSession(
            new AgentRunId("run-wait-contract"));
        return await session.RunTurnAsync(
            "Wait on the terminal.",
            [Tool()],
            new ToolProvider(arguments),
            CancellationToken.None);
    }

    private static AgentToolDefinition Tool() =>
        new(
            BuiltInAgentTools.TerminalWait,
            "Test terminal wait.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private static AgentContextPanel ContextPanel(
        string suffix,
        string panelIdValue)
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
            new CapabilitySet([SessionCapabilities.TerminalWait]),
            Revision: 5,
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
                "call-wait-contract",
                ProviderToolName.FromInternal(BuiltInAgentTools.TerminalWait));
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
