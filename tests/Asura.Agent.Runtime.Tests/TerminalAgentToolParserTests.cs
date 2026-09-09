using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class TerminalAgentToolParserTests
{
    [Theory]
    [InlineData(BuiltInAgentTools.TerminalReadScreen, "{}", typeof(TerminalAgentIntent.ReadScreen))]
    [InlineData(BuiltInAgentTools.TerminalReadScreenDiff, "{\"after_content_revision\":7,\"max_changed_rows\":16}", typeof(TerminalAgentIntent.ReadScreenDiff))]
    [InlineData(BuiltInAgentTools.TerminalFindOnScreen, "{\"text\":\"EDIT_PASS\",\"max_matches\":8}", typeof(TerminalAgentIntent.FindOnScreen))]
    [InlineData(BuiltInAgentTools.TerminalFindRenderedHistory, "{\"text\":\"TUIHISTORY_START\",\"direction\":\"backward\",\"max_matches\":8}", typeof(TerminalAgentIntent.FindRenderedHistory))]
    [InlineData(BuiltInAgentTools.TerminalReadScrollback, "{\"anchor\":\"bottom\",\"max_lines\":64}", typeof(TerminalAgentIntent.ReadScrollback))]
    [InlineData(BuiltInAgentTools.TerminalFind, "{\"text\":\"ready\",\"direction\":\"backward\",\"max_matches\":8}", typeof(TerminalAgentIntent.FindScrollback))]
    [InlineData(BuiltInAgentTools.TerminalScrollViewport, "{\"direction\":\"up\",\"unit\":\"page\",\"amount\":2}", typeof(TerminalAgentIntent.ScrollViewport))]
    [InlineData(BuiltInAgentTools.TerminalScrollViewport, "{\"direction\":\"bottom\"}", typeof(TerminalAgentIntent.ScrollViewport))]
    [InlineData(BuiltInAgentTools.TerminalSendText, "{\"text\":\"menu\"}", typeof(TerminalAgentIntent.SendText))]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"first\\n\\tsecond\"}", typeof(TerminalAgentIntent.Paste))]
    [InlineData(BuiltInAgentTools.TerminalSubmitText, "{\"text\":\"echo ready\"}", typeof(TerminalAgentIntent.SubmitText))]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"backspace\",\"repeat\":12}", typeof(TerminalAgentIntent.SendKey))]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"d\",\"modifier\":\"control\"}", typeof(TerminalAgentIntent.SendChord))]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"left_down\",\"column\":4,\"row\":7,\"expected_content_revision\":9}", typeof(TerminalAgentIntent.SendMouse))]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"text\":\"Selected\",\"timeout_ms\":5000}", typeof(TerminalAgentIntent.WaitForText))]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"delay_ms\":3600000}", typeof(TerminalAgentIntent.WaitForDelay))]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"prompt_ready\":true,\"after_shell_event_sequence\":0,\"timeout_ms\":5000}", typeof(TerminalAgentIntent.WaitForPromptReady))]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"command_finished\":true,\"after_shell_event_sequence\":7,\"timeout_ms\":5000}", typeof(TerminalAgentIntent.WaitForCommandFinished))]
    [InlineData(BuiltInAgentTools.TerminalInterrupt, "{}", typeof(TerminalAgentIntent.Interrupt))]
    public async Task ParsesTheClosedTerminalIntentSet(
        string toolName,
        string arguments,
        Type expectedType)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.IsType(expectedType, parsed.Intent);
    }

    [Theory]
    [InlineData(BuiltInAgentTools.TerminalReadScreen, "{\"extra\":true}")]
    [InlineData(BuiltInAgentTools.TerminalReadScreenDiff, "{\"after_content_revision\":7,\"max_changed_rows\":0}")]
    [InlineData(BuiltInAgentTools.TerminalFindOnScreen, "{\"text\":\"\",\"max_matches\":8}")]
    [InlineData(BuiltInAgentTools.TerminalFindRenderedHistory, "{\"text\":\"ready\",\"direction\":\"sideways\",\"max_matches\":8}")]
    [InlineData(BuiltInAgentTools.TerminalJumpToRenderedHistory, "{\"row_anchor\":\"invalid\"}")]
    [InlineData(BuiltInAgentTools.TerminalReadScrollback, "{\"anchor\":\"before\",\"max_lines\":64,\"row_anchor\":\"invalid\"}")]
    [InlineData(BuiltInAgentTools.TerminalFind, "{\"text\":\"ready\",\"direction\":\"sideways\",\"max_matches\":8}")]
    [InlineData(BuiltInAgentTools.TerminalScrollViewport, "{\"direction\":\"top\",\"unit\":\"page\",\"amount\":1}")]
    [InlineData(BuiltInAgentTools.TerminalScrollViewport, "{\"direction\":\"down\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendText, "{\"text\":\"line\\nfeed\"}")]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"\\u0000\"}")]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"\\u001b[31m\"}")]
    [InlineData(BuiltInAgentTools.TerminalPaste, "{\"text\":\"safe\",\"extra\":true}")]
    [InlineData(BuiltInAgentTools.TerminalSubmitText, "{\"text\":\"\\u001b[31m\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"a\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"enter\",\"modifiers\":[\"control\",\"control\"]}")]
    [InlineData(BuiltInAgentTools.TerminalSendKeys, "{\"key\":\"backspace\",\"repeat\":65}")]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"D\",\"modifier\":\"control\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"d\",\"modifier\":[\"control\",\"alt\"]}")]
    [InlineData(BuiltInAgentTools.TerminalSendChord, "{\"character\":\"d\",\"modifier\":\"control\",\"text\":\"\\u0004\"}")]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"click\",\"column\":4,\"row\":7}")]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"left_down\",\"column\":-1,\"row\":7}")]
    [InlineData(BuiltInAgentTools.TerminalSendMouse, "{\"event\":\"left_down\",\"column\":4,\"row\":1000001}")]
    [InlineData(BuiltInAgentTools.TerminalWait, "{\"text\":\"ready\",\"timeout_ms\":3600001}")]
    [InlineData("terminal.provider_extension", "{}")]
    public async Task RejectsUnknownOrUnboundedArguments(
        string toolName,
        string arguments)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(proposal));

        Assert.Contains(
            rejected.StableCode,
            ["invalid_tool_arguments", "unknown_tool"], StringComparer.Ordinal);
    }

    [Fact]
    public async Task Send_keys_projects_bounded_repeat_into_one_intent()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalSendKeys,
            "{\"key\":\"backspace\",\"repeat\":12}");

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var intent = Assert.IsType<TerminalAgentIntent.SendKey>(parsed.Intent);

        Assert.Equal(TerminalKey.Backspace, intent.KeyStroke.Key);
        Assert.Equal(12, intent.KeyStroke.RepeatCount);
    }

    [Fact]
    public async Task Absolute_viewport_scroll_requires_no_synthetic_unit_or_amount()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalScrollViewport,
            "{\"direction\":\"bottom\"}");

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var intent = Assert.IsType<TerminalAgentIntent.ScrollViewport>(parsed.Intent);

        Assert.Equal(TerminalViewportScrollDirection.Bottom, intent.Input.Direction);
        Assert.Equal(TerminalViewportScrollUnit.Line, intent.Input.Unit);
        Assert.Equal(1, intent.Input.Amount);
    }

    [Fact]
    public async Task Rendered_history_anchor_round_trips_only_into_viewport_jump()
    {
        var anchor = new TerminalRenderedHistoryRowAnchor(17, 42);
        var encoded = TerminalRenderedHistoryAnchorCodec.Encode(anchor);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalJumpToRenderedHistory,
            $"{{\"row_anchor\":\"{encoded}\"}}");

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var intent = Assert.IsType<TerminalAgentIntent.JumpToRenderedHistory>(
            parsed.Intent);

        Assert.Equal(anchor, intent.Anchor);
        Assert.False(TerminalScrollbackAnchorCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void Rendered_history_tools_require_the_explicit_engine_capability()
    {
        var readOnly = ContextPanel(
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalRenderedHistory);
        var interactive = ContextPanel(
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalRenderedHistory,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalScrollback);

        Assert.Contains(
            TerminalAgentToolSet.For(readOnly),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalFindRenderedHistory, StringComparison.Ordinal));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(readOnly),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalJumpToRenderedHistory, StringComparison.Ordinal));
        Assert.Contains(
            TerminalAgentToolSet.For(interactive),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalJumpToRenderedHistory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeKernelRejectsDuplicateJsonBeforeRuntimeParsing()
    {
        var session = new NativeAgentSession(new AgentRunId("run-1"));

        var result = await session.RunTurnAsync(
            "Use the tool.",
            [Tool(BuiltInAgentTools.TerminalSendText)],
            new ToolProvider(
                BuiltInAgentTools.TerminalSendText,
                "{\"text\":\"one\",\"text\":\"two\"}"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            result.ErrorCode);
        Assert.Empty(result.ToolProposals);
    }

    [Fact]
    public void ManagedTerminalGetsReadWaitAndMutationTools()
    {
        var tools = TerminalAgentToolSet.For(ContextPanel(
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalPaste,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse,
            SessionCapabilities.TerminalInterrupt));

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalReadScreenDiff,
                BuiltInAgentTools.TerminalFindOnScreen,
                BuiltInAgentTools.TerminalWait,
                BuiltInAgentTools.TerminalSendText,
                BuiltInAgentTools.TerminalPaste,
                BuiltInAgentTools.TerminalSendKeys,
                BuiltInAgentTools.TerminalSendMouse,
                BuiltInAgentTools.TerminalInterrupt,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(
            tools,
            tool =>
            {
                Assert.False(
                    tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
                Assert.DoesNotContain(
                    "session",
                    tool.InputSchema.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "lease",
                    tool.InputSchema.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
            });
        Assert.True(TerminalAgentToolSet.SupportsMutations(ContextPanel(
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalWrite)));
    }

    [Fact]
    public void NativeTerminalWithPhysicalInputBarrierGetsMutationTools()
    {
        var panel = ContextPanel(
            SessionCapabilities.NativeRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalPaste,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse,
            SessionCapabilities.TerminalInterrupt);

        var tools = TerminalAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalReadScreenDiff,
                BuiltInAgentTools.TerminalFindOnScreen,
                BuiltInAgentTools.TerminalWait,
                BuiltInAgentTools.TerminalSendText,
                BuiltInAgentTools.TerminalPaste,
                BuiltInAgentTools.TerminalSendKeys,
                BuiltInAgentTools.TerminalSendMouse,
                BuiltInAgentTools.TerminalInterrupt,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.True(TerminalAgentToolSet.SupportsMutations(panel));
    }

    [Fact]
    public void RendererWithoutPhysicalInputBarrierFailsClosedToReadOnly()
    {
        var panel = ContextPanel(
            SessionCapabilities.NativeRenderer,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite);

        var tools = TerminalAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalReadScreenDiff,
                BuiltInAgentTools.TerminalFindOnScreen,
                BuiltInAgentTools.TerminalWait,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.False(TerminalAgentToolSet.SupportsMutations(panel));
    }

    [Fact]
    public async Task Paste_preserves_approved_line_breaks_and_tabs_but_is_utf8_bounded()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalPaste,
            "{\"text\":\"first\\r\\n\\tsecond\"}");

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        Assert.Equal(
            "first\r\n\tsecond",
            Assert.IsType<TerminalAgentIntent.Paste>(parsed.Intent).Text);

        var oversized = await ProposalAsync(
            BuiltInAgentTools.TerminalPaste,
            $"{{\"text\":\"{new string('é', 1_025)}\"}}");
        var rejected = Assert.IsType<TerminalAgentIntentResult.Rejected>(
            TerminalAgentToolParser.Parse(oversized));
        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void Paste_requires_both_the_physical_input_barrier_and_paste_capability()
    {
        var eligible = ContextPanel(
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalPaste);
        var noBarrier = ContextPanel(SessionCapabilities.TerminalPaste);
        var noPaste = ContextPanel(SessionCapabilities.TerminalAgentInputBarrier);

        Assert.Contains(
            TerminalAgentToolSet.For(eligible),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalPaste, StringComparison.Ordinal));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noBarrier),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalPaste, StringComparison.Ordinal));
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noPaste),
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalPaste, StringComparison.Ordinal));
        Assert.True(TerminalAgentToolSet.SupportsMutations(eligible));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noBarrier));
        Assert.False(TerminalAgentToolSet.SupportsMutations(noPaste));
    }

    [Fact]
    public void Submit_text_requires_barrier_paste_and_enter_capabilities()
    {
        var eligible = ContextPanel(
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalPaste,
            SessionCapabilities.TerminalEnter);
        var noEnter = ContextPanel(
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalPaste);

        var tool = Assert.Single(
            TerminalAgentToolSet.For(eligible),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSubmitText, StringComparison.Ordinal));
        Assert.Contains(
            "Prefer this for submitting",
            tool.Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TerminalAgentToolSet.For(noEnter),
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.TerminalSubmitText, StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryProjectionAndViewportScrollUseDistinctCapabilities()
    {
        var projection = ContextPanel(
            SessionCapabilities.TerminalScrollbackRead,
            SessionCapabilities.TerminalScrollbackFind);
        var scrolling = ContextPanel(
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalScrollback);

        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScrollback,
                BuiltInAgentTools.TerminalFind,
            ],
            TerminalAgentToolSet.For(projection).Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.Equal(
            [BuiltInAgentTools.TerminalScrollViewport],
            TerminalAgentToolSet.For(scrolling).Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.False(TerminalAgentToolSet.SupportsMutations(projection));
        Assert.True(TerminalAgentToolSet.SupportsMutations(scrolling));
    }

    [Fact]
    public async Task ScrollbackRowAnchorsRoundTripAndRemainRevisionBound()
    {
        var anchor = new TerminalScrollbackRowAnchor(42, 7);
        var encoded = TerminalScrollbackAnchorCodec.Encode(anchor);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.TerminalReadScrollback,
            $$"""
            {"anchor":"after","row_anchor":"{{encoded}}","max_lines":16}
            """);

        var parsed = Assert.IsType<TerminalAgentIntentResult.Parsed>(
            TerminalAgentToolParser.Parse(proposal));
        var read = Assert.IsType<TerminalAgentIntent.ReadScrollback>(
            parsed.Intent);

        Assert.Equal(anchor, read.Input.RowAnchor);
        Assert.Equal(TerminalScrollbackReadOrigin.After, read.Input.Origin);
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("run-1"));
        var result = await session.RunTurnAsync(
            "Use the tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private static AgentContextPanel ContextPanel(params string[] capabilities)
    {
        var sessionId = new SessionId("session-1");
        var windowId = new WindowInstanceId("window-1");
        var workspaceId = new WorkspaceInstanceId("workspace-1");
        var tabId = new TabInstanceId("tab-1");
        var panelId = new PanelInstanceId("panel-1");
        var panel = new PanelInstance(
            panelId,
            PanelKind.Terminal,
            "Production",
            sessionId);
        var tab = new TabInstance(tabId, "Shells", [panel], panelId);
        var workspace = new WorkspaceInstance(
            workspaceId,
            "Operations",
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
                "call-1",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}
