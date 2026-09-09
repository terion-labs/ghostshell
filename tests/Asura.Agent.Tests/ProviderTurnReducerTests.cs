using Asura.Agent;

namespace Asura.Agent.Tests;

public sealed class ProviderTurnReducerTests
{
    [Fact]
    public void CompleteTextResponseBuildsOneAuthoritativeTurn()
    {
        var reducer = CreateReducer();

        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.TextDelta("hello "));
        reducer.Apply(new AgentProviderEvent.TextDelta("world"));
        reducer.Apply(new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn));

        var turn = reducer.Build();

        Assert.Equal("hello world", turn.AssistantText);
        Assert.Empty(turn.ToolCalls);
        Assert.Equal(AgentProviderStopReason.EndTurn, turn.StopReason);
    }

    [Fact]
    public void ReasoningSummaryAndUsageAreReducedAsBoundedMetadata()
    {
        var reducer = CreateReducer();
        var usage = new AgentTokenUsage(
            inputTokens: 120,
            outputTokens: 30,
            cachedInputTokens: 20,
            reasoningTokens: 10);

        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ReasoningSummaryDelta(
            "Checked the workspace. "));
        reducer.Apply(new AgentProviderEvent.ReasoningSummaryDelta(
            "No mutation was needed."));
        reducer.Apply(new AgentProviderEvent.TextDelta("Everything looks healthy."));
        reducer.Apply(new AgentProviderEvent.Usage(usage));
        reducer.Apply(new AgentProviderEvent.ResponseCompleted(
            AgentProviderStopReason.EndTurn));

        var turn = reducer.Build();

        Assert.Equal(
            "Checked the workspace. No mutation was needed.",
            turn.ReasoningSummary);
        Assert.Same(usage, turn.Usage);
        Assert.Equal("Everything looks healthy.", turn.AssistantText);
    }

    [Fact]
    public void DuplicateUsageIsRejected()
    {
        var reducer = CreateReducer();
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.Usage(new AgentTokenUsage(1, 1)));

        var failure = Assert.Throws<ProviderStreamException>(() =>
            reducer.Apply(new AgentProviderEvent.Usage(
                new AgentTokenUsage(1, 1))));

        Assert.Equal(ProviderStreamErrorCode.InvalidValue, failure.Code);
    }

    [Fact]
    public void ReplayStateMustBeUniqueAndImmediatelyPrecedeCompletion()
    {
        var replay = new AgentProviderReplayState(
            new AgentProviderReplayBinding(
                new Asura.Core.AiProviderProfileId("profile"),
                Asura.Core.AiProviderKind.OpenAi,
                Asura.Core.AiProviderProtocol.OpenAiResponses,
                "model",
                new Uri("https://provider.example/v1/"),
                "responses:test"),
            AgentProviderReplayFormat.OpenAiResponseItems,
            [new AgentProviderReplayItem(
                0,
                AgentProviderReplayItemKind.OpenAiMessage,
                "{\"type\":\"message\",\"id\":\"msg-1\"}")]);
        var reducer = CreateReducer();
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ReplayStateFinalized(replay));

        var failure = Assert.Throws<ProviderStreamException>(() =>
            reducer.Apply(new AgentProviderEvent.TextDelta("late")));

        Assert.Equal(ProviderStreamErrorCode.InvalidTransition, failure.Code);

        var completed = CreateReducer();
        completed.Apply(new AgentProviderEvent.ResponseStarted());
        completed.Apply(new AgentProviderEvent.ReplayStateFinalized(replay));
        completed.Apply(new AgentProviderEvent.ResponseCompleted(
            AgentProviderStopReason.EndTurn));
        Assert.Same(replay, completed.Build().ProviderReplayState);
    }

    [Fact]
    public void ReplayStateRejectsDuplicateSlotsAndUnboundedJson()
    {
        var binding = new AgentProviderReplayBinding(
            new Asura.Core.AiProviderProfileId("profile"),
            Asura.Core.AiProviderKind.OpenAi,
            Asura.Core.AiProviderProtocol.OpenAiResponses,
            "model",
            new Uri("https://provider.example/v1/"),
            "responses-test");
        var first = new AgentProviderReplayItem(
            0,
            AgentProviderReplayItemKind.OpenAiFunctionCall,
            "{\"type\":\"function_call\"}",
            toolIndex: 0);
        var duplicate = new AgentProviderReplayItem(
            1,
            AgentProviderReplayItemKind.OpenAiFunctionCall,
            "{\"type\":\"function_call\"}",
            toolIndex: 0);

        Assert.Throws<ArgumentException>(() => new AgentProviderReplayState(
            binding,
            AgentProviderReplayFormat.OpenAiResponseItems,
            [first, duplicate]));
        Assert.Throws<ArgumentException>(() => new AgentProviderReplayItem(
            0,
            AgentProviderReplayItemKind.OpenAiReasoning,
            "{\"type\":\"reasoning\",\"type\":\"reasoning\"}"));
        Assert.Throws<ArgumentException>(() => new AgentProviderReplayItem(
            0,
            AgentProviderReplayItemKind.OpenAiReasoning,
            "{\"value\":" + new string('[', AgentProviderReplayState.MaximumJsonDepth + 1)
            + "0" + new string(']', AgentProviderReplayState.MaximumJsonDepth + 1)
            + "}"));
        Assert.Throws<ArgumentException>(() => new AgentProviderReplayItem(
            0,
            AgentProviderReplayItemKind.OpenAiReasoning,
            "{\"value\":\"" + new string('x', AgentProviderReplayState.MaximumItemBytes)
            + "\"}"));
    }

    [Fact]
    public void OpenAiResponseReplayFormatAcceptsExplicitGitHubCopilotRoute()
    {
        var state = new AgentProviderReplayState(
            new AgentProviderReplayBinding(
                new Asura.Core.AiProviderProfileId("copilot-profile"),
                Asura.Core.AiProviderKind.GitHubCopilot,
                Asura.Core.AiProviderProtocol.GitHubCopilot,
                "gpt-5.3-codex",
                new Uri("https://api.githubcopilot.com/"),
                "github-copilot-oauth-responses"),
            AgentProviderReplayFormat.OpenAiResponseItems,
            [new AgentProviderReplayItem(
                0,
                AgentProviderReplayItemKind.OpenAiReasoning,
                "{\"type\":\"reasoning\",\"id\":\"rs-1\",\"encrypted_content\":\"opaque\"}")]);

        Assert.Equal(
            Asura.Core.AiProviderProtocol.GitHubCopilot,
            state.Binding.Protocol);
        Assert.Equal(
            AgentProviderReplayFormat.OpenAiResponseItems,
            state.Format);
    }

    [Fact]
    public void ReasoningSummaryUsesItsOwnAggregateByteLimit()
    {
        var limits = new AgentKernelLimits(
            maximumProviderTextFragmentBytes: 4,
            maximumAssistantTextBytes: 8,
            maximumReasoningSummaryBytes: 4);
        var reducer = CreateReducer([], limits);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ReasoningSummaryDelta("ab"));

        var failure = Assert.Throws<ProviderStreamException>(() =>
            reducer.Apply(new AgentProviderEvent.ReasoningSummaryDelta("cde")));

        Assert.Equal(ProviderStreamErrorCode.LimitExceeded, failure.Code);
    }

    [Fact]
    public void CompleteToolResponseParsesFragmentedArgumentsWithoutRetainingTheDocument()
    {
        var reducer = CreateReducer(["terminal.read_screen"]);

        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.read_screen")));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, "{\"panelId\":"));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, "\"panel-1\"}"));
        reducer.Apply(new AgentProviderEvent.ToolCallCompleted(0));
        reducer.Apply(new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.ToolUse));

        var toolCall = Assert.Single(reducer.Build().ToolCalls);
        Assert.Equal(0, toolCall.Index);
        Assert.Equal("provider-call-1", toolCall.ProviderCallId);
        Assert.Equal("terminal.read_screen", toolCall.Name);
        Assert.Equal("panel-1", toolCall.Arguments.GetProperty("panelId").GetString());
    }

    [Fact]
    public void BuildBeforeTerminalProviderEventPublishesNothing()
    {
        var reducer = CreateReducer(["terminal.send_text"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.send_text")));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, "{\"text\":\"partial\"}"));

        var failure = Assert.Throws<ProviderStreamException>(reducer.Build);

        Assert.Equal(ProviderStreamErrorCode.IncompleteResponse, failure.Code);
    }

    [Fact]
    public void EventAfterResponseCompletionInvalidatesTheStream()
    {
        var reducer = CreateReducer();
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn));

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.TextDelta("late")));

        Assert.Equal(ProviderStreamErrorCode.InvalidTransition, failure.Code);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("tool")]
    [InlineData("complete")]
    public void ContentBeforeResponseStartIsRejected(string eventKind)
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        AgentProviderEvent providerEvent = eventKind switch
        {
            "text" => new AgentProviderEvent.TextDelta("text"),
            "tool" => new AgentProviderEvent.ToolCallStarted(
                0,
                "provider-call-1",
                ProviderName("terminal.read_screen")),
            "complete" => new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn),
            _ => throw new ArgumentOutOfRangeException(nameof(eventKind)),
        };

        var failure = Assert.Throws<ProviderStreamException>(() => reducer.Apply(providerEvent));

        Assert.Equal(ProviderStreamErrorCode.InvalidTransition, failure.Code);
    }

    [Fact]
    public void DuplicateResponseStartIsRejected()
    {
        var reducer = CreateReducer();
        reducer.Apply(new AgentProviderEvent.ResponseStarted());

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ResponseStarted()));

        Assert.Equal(ProviderStreamErrorCode.InvalidTransition, failure.Code);
    }

    [Fact]
    public void NullProviderEventIsAStableStreamFailure()
    {
        var reducer = CreateReducer();

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(null!));

        Assert.Equal(ProviderStreamErrorCode.InvalidValue, failure.Code);
    }

    [Fact]
    public void ProviderEventCountIsBoundedIndependentlyOfByteCount()
    {
        var limits = new AgentKernelLimits(maximumProviderEventsPerTurn: 3);
        var reducer = CreateReducer([], limits);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.TextDelta("a"));
        reducer.Apply(new AgentProviderEvent.TextDelta("b"));

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(
                new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn)));

        Assert.Equal(ProviderStreamErrorCode.LimitExceeded, failure.Code);
    }

    [Fact]
    public void ToolCallsRequireContiguousIndicesAndUniqueProviderIds()
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.read_screen")));

        var indexFailure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallStarted(
                2,
                "provider-call-2",
                ProviderName("terminal.read_screen"))));
        Assert.Equal(ProviderStreamErrorCode.InvalidValue, indexFailure.Code);

        var idFailure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallStarted(
                1,
                "provider-call-1",
                ProviderName("terminal.read_screen"))));
        Assert.Equal(ProviderStreamErrorCode.DuplicateToolCall, idFailure.Code);
    }

    [Fact]
    public void UnknownToolIsRejected()
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallStarted(
                0,
                "provider-call-1",
                ProviderName("terminal.send_text"))));

        Assert.Equal(ProviderStreamErrorCode.UnknownTool, failure.Code);
    }

    [Fact]
    public void ProviderToolNameMustUseTheProviderAlphabet()
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallStarted(
                0,
                "provider-call-1",
                "terminal.read_screen")));

        Assert.Equal(ProviderStreamErrorCode.InvalidValue, failure.Code);
    }

    [Fact]
    public void ProviderCallIdMustBeWellFormedUtf16()
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallStarted(
                0,
                new string('\uD800', 1),
                ProviderName("terminal.read_screen"))));

        Assert.Equal(ProviderStreamErrorCode.InvalidValue, failure.Code);
    }

    [Theory]
    [InlineData("{\"value\":1,\"value\":2}")]
    [InlineData("[]")]
    [InlineData("{\"value\":}")]
    public void AmbiguousOrMalformedToolArgumentsAreRejected(string arguments)
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.read_screen")));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments));

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallCompleted(0)));

        Assert.Equal(ProviderStreamErrorCode.InvalidToolArguments, failure.Code);
    }

    [Fact]
    public void ToolArgumentsRespectDepthAndNodeLimits()
    {
        var limits = new AgentKernelLimits(
            maximumJsonDepth: 2,
            maximumJsonNodes: 3);
        var reducer = CreateReducer(["terminal.read_screen"], limits);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.read_screen")));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(
            0,
            "{\"one\":{\"two\":{\"three\":3}}}"));

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ToolCallCompleted(0)));

        Assert.Contains(
            failure.Code,
            new[]
            {
                ProviderStreamErrorCode.InvalidToolArguments,
                ProviderStreamErrorCode.LimitExceeded,
            });
    }

    [Fact]
    public void TextAndToolArgumentFragmentsUseUtf8ByteLimits()
    {
        var limits = TinyLimits();
        var textReducer = CreateReducer([], limits);
        textReducer.Apply(new AgentProviderEvent.ResponseStarted());

        var textFailure = Assert.Throws<ProviderStreamException>(
            () => textReducer.Apply(new AgentProviderEvent.TextDelta("ééé")));
        Assert.Equal(ProviderStreamErrorCode.LimitExceeded, textFailure.Code);

        var toolReducer = CreateReducer(["terminal.send_text"], limits);
        toolReducer.Apply(new AgentProviderEvent.ResponseStarted());
        toolReducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.send_text")));
        var argumentFailure = Assert.Throws<ProviderStreamException>(
            () => toolReducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(
                0,
                "ééé")));
        Assert.Equal(ProviderStreamErrorCode.LimitExceeded, argumentFailure.Code);
    }

    [Fact]
    public void AggregateToolArgumentsUseATurnWideByteLimit()
    {
        var limits = new AgentKernelLimits(
            maximumToolCallsPerTurn: 2,
            maximumToolArgumentFragmentBytes: 4,
            maximumToolArgumentBytes: 4,
            maximumTotalToolArgumentBytesPerTurn: 4);
        var reducer = CreateReducer(["one", "two"], limits);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(0, "call-1", "one"));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, "{}"));
        reducer.Apply(new AgentProviderEvent.ToolCallCompleted(0));
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(1, "call-2", "two"));

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(
                new AgentProviderEvent.ToolCallArgumentsDelta(1, "{}x")));

        Assert.Equal(ProviderStreamErrorCode.LimitExceeded, failure.Code);
    }

    [Theory]
    [InlineData(AgentProviderStopReason.ToolUse)]
    [InlineData(AgentProviderStopReason.EndTurn)]
    public void StopReasonMustMatchToolPresence(AgentProviderStopReason stopReason)
    {
        var withTool = stopReason == AgentProviderStopReason.EndTurn;
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        if (withTool)
        {
            reducer.Apply(new AgentProviderEvent.ToolCallStarted(
                0,
                "provider-call-1",
                ProviderName("terminal.read_screen")));
            reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, "{}"));
            reducer.Apply(new AgentProviderEvent.ToolCallCompleted(0));
        }

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(new AgentProviderEvent.ResponseCompleted(stopReason)));

        Assert.Equal(ProviderStreamErrorCode.InconsistentStopReason, failure.Code);
    }

    [Fact]
    public void ResponseCannotCompleteWithAnOpenToolCall()
    {
        var reducer = CreateReducer(["terminal.read_screen"]);
        reducer.Apply(new AgentProviderEvent.ResponseStarted());
        reducer.Apply(new AgentProviderEvent.ToolCallStarted(
            0,
            "provider-call-1",
            ProviderName("terminal.read_screen")));
        reducer.Apply(new AgentProviderEvent.ToolCallArgumentsDelta(0, "{}"));

        var failure = Assert.Throws<ProviderStreamException>(
            () => reducer.Apply(
                new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.ToolUse)));

        Assert.Equal(ProviderStreamErrorCode.IncompleteResponse, failure.Code);
    }

    private static ProviderTurnReducer CreateReducer(
        IEnumerable<string>? tools = null,
        AgentKernelLimits? limits = null)
    {
        var toolNamesByProviderName = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var toolName in tools ?? [])
        {
            toolNamesByProviderName.Add(ProviderName(toolName), toolName);
        }

        return new ProviderTurnReducer(
            toolNamesByProviderName,
            limits ?? AgentKernelLimits.Default);
    }

    private static string ProviderName(string internalName) =>
        AgentToolDefinition.GetProviderName(internalName);

    private static AgentKernelLimits TinyLimits() =>
        new(
            maximumProviderTextFragmentBytes: 4,
            maximumAssistantTextBytes: 8,
            maximumToolCallsPerTurn: 2,
            maximumToolArgumentFragmentBytes: 4,
            maximumToolArgumentBytes: 8,
            maximumJsonDepth: 4,
            maximumJsonNodes: 8,
            maximumConversationMessages: 4,
            maximumConversationBytes: 64,
            maximumRetainedEvents: 4,
            maximumEventBatchSize: 2);
}
