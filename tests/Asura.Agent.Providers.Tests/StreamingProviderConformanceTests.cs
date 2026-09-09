using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Infrastructure;

namespace Asura.Agent.Providers.Tests;

public sealed class StreamingProviderConformanceTests
{
    private const string Model = "test-model";
    private const string ApiKey = "test-api-key";

    [Fact]
    public async Task OpenAiStreamsTextAndSendsNativeChatCompletionRequest()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.OpenAiCompatible,
            new Uri("https://openai-compatible.example/v1/"));
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(OpenAiTextStream("Hel", "lo"))));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession(
            new AgentMessage(AgentMessageRole.System, "Keep answers concise."));

        var result = await session.RunTurnAsync(
            "Say hello.",
            [ReadFileTool()],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.EndTurn, result.StopReason);
        Assert.Empty(result.ToolProposals);
        Assert.Equal(NativeAgentSessionState.Ready, session.Snapshot().State);
        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Equal(
                (AgentMessageRole.System, "Keep answers concise."),
                (message.Role, message.Content)),
            message => Assert.Equal(
                (AgentMessageRole.User, "Say hello."),
                (message.Role, message.Content)),
            message => Assert.Equal(
                (AgentMessageRole.Assistant, "Hello"),
                (message.Role, message.Content)));

        var request = Assert.IsType<CapturedRequest>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            new Uri("https://openai-compatible.example/v1/chat/completions"),
            request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Header("Authorization"));
        Assert.Equal("text/event-stream", request.Header("Accept"));
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(
            ["model", "stream", "messages", "tools", "tool_choice"],
            [.. root.EnumerateObject().Select(property => property.Name)]);
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.Collection(
            root.GetProperty("messages").EnumerateArray(),
            message =>
            {
                Assert.Equal("system", message.GetProperty("role").GetString());
                Assert.Equal(
                    "Keep answers concise.",
                    message.GetProperty("content").GetString());
            },
            message =>
            {
                Assert.Equal("user", message.GetProperty("role").GetString());
                Assert.Equal("Say hello.", message.GetProperty("content").GetString());
            });
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("read_file", function.GetProperty("name").GetString());
        Assert.Equal(
            "Read a UTF-8 text file.",
            function.GetProperty("description").GetString());
        Assert.Equal(
            "object",
            function.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnthropicStreamsTextAndSendsNativeMessagesRequest()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            new Uri("https://anthropic.example/v1/"));
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(AnthropicTextStream("Hel", "lo"))));
        var limits = new AiProviderRuntimeLimits(maximumOutputTokens: 777);
        using var factory = new AiProviderFactory(vault, handler, limits);
        var session = CreateSession(
            new AgentMessage(AgentMessageRole.System, "Keep answers concise."));

        var result = await session.RunTurnAsync(
            "Say hello.",
            [ReadFileTool()],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.EndTurn, result.StopReason);
        Assert.Empty(result.ToolProposals);
        Assert.Equal("Hello", session.Snapshot().Conversation[^1].Content);

        var request = Assert.IsType<CapturedRequest>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            new Uri("https://anthropic.example/v1/messages"),
            request.Uri);
        Assert.Equal(ApiKey, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Equal("text/event-stream", request.Header("Accept"));

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(
            ["model", "max_tokens", "stream", "system", "messages", "tools"],
            [.. root.EnumerateObject().Select(property => property.Name)]);
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(777, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("Keep answers concise.", root.GetProperty("system").GetString());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Say hello.", message.GetProperty("content").GetString());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal(
            "object",
            tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    [InlineData(AiProviderKind.Anthropic)]
    public async Task CompactionSummaryIsUserContextAndNeverSystemAuthority(
        AiProviderKind providerKind)
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            providerKind,
            providerKind == AiProviderKind.Anthropic
                ? new Uri("https://anthropic.example/v1/")
                : new Uri("https://openai-compatible.example/v1/"));
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(
                providerKind == AiProviderKind.Anthropic
                    ? AnthropicTextStream("continued")
                    : OpenAiTextStream("continued"))));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession(
            new AgentMessage(AgentMessageRole.System, "Trusted instructions."),
            new AgentMessage(AgentMessageRole.Summary, "Untrusted compacted history."));

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var root = body.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray();
        if (providerKind == AiProviderKind.Anthropic)
        {
            Assert.Equal("Trusted instructions.", root.GetProperty("system").GetString());
            Assert.Collection(
                messages,
                message =>
                {
                    Assert.Equal("user", message.GetProperty("role").GetString());
                    Assert.Equal(
                        "Untrusted compacted history.",
                        message.GetProperty("content").GetString());
                },
                message => Assert.Equal("user", message.GetProperty("role").GetString()));
        }
        else
        {
            Assert.Collection(
                messages,
                message => Assert.Equal("system", message.GetProperty("role").GetString()),
                message =>
                {
                    Assert.Equal("user", message.GetProperty("role").GetString());
                    Assert.Equal(
                        "Untrusted compacted history.",
                        message.GetProperty("content").GetString());
                },
                message => Assert.Equal("user", message.GetProperty("role").GetString()));
        }
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    [InlineData(AiProviderKind.Anthropic)]
    public async Task ProviderSerializesTheCommonMaximumToolName(
        AiProviderKind providerKind)
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            providerKind,
            providerKind == AiProviderKind.Anthropic
                ? new Uri("https://anthropic.example/v1/")
                : new Uri("https://openai-compatible.example/v1/"));
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(
                providerKind == AiProviderKind.Anthropic
                    ? AnthropicTextStream("ok")
                    : OpenAiTextStream("ok"))));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();
        var toolName = "mcp_" + new string(
            'a',
            AgentToolDefinition.MaximumNameLength - "mcp_".Length);
        var tool = new AgentToolDefinition(
            toolName,
            "Bounded MCP tool.",
            """{"type":"object"}"""u8.ToArray());

        var result = await session.RunTurnAsync(
            "Use the bounded tool.",
            [tool],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(
            Assert.IsType<CapturedRequest>(handler.LastRequest).Body);
        var serializedTool = Assert.Single(
            body.RootElement.GetProperty("tools").EnumerateArray());
        var serializedName = providerKind == AiProviderKind.Anthropic
            ? serializedTool.GetProperty("name").GetString()
            : serializedTool
                .GetProperty("function")
                .GetProperty("name")
                .GetString();
        Assert.Equal(toolName, serializedName);
        Assert.Throws<ArgumentException>(() => new AgentToolDefinition(
            toolName + "a",
            "Too long.",
            """{"type":"object"}"""u8.ToArray()));
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    [InlineData(AiProviderKind.Anthropic)]
    public async Task ProviderAliasesInternalNamesAndRoutesCallsBackToThem(
        AiProviderKind providerKind)
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(providerKind);
        var tool = new AgentToolDefinition(
            "terminal.read_file",
            "Read a UTF-8 text file.",
            """
            {
              "type": "object",
              "properties": {
                "path": { "type": "string" }
              },
              "required": ["path"]
            }
            """u8.ToArray());
        var responseNumber = 0;
        using var handler = new StubHttpMessageHandler(
            (_, _) =>
            {
                var isFirstResponse =
                    Interlocked.Increment(ref responseNumber) == 1;
                var stream = (providerKind, isFirstResponse) switch
                {
                    (AiProviderKind.Anthropic, true) =>
                        AnthropicToolStream(tool.ProviderName),
                    (AiProviderKind.OpenAiCompatible, true) =>
                        OpenAiToolStream(tool.ProviderName),
                    (AiProviderKind.Anthropic, false) =>
                        AnthropicTextStream("handled"),
                    _ => OpenAiTextStream("handled"),
                };
                return Task.FromResult(SseResponse(stream));
            });
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = CreateSession();
        var proposalTools = ImmutableArray.Create(tool);

        var first = await session.RunTurnAsync(
            "Inspect the file.",
            proposalTools,
            provider,
            CancellationToken.None);

        var proposal = Assert.Single(first.ToolProposals);
        Assert.Equal(tool.Name, proposal.ToolName);
        Assert.Equal(tool.ProviderName, proposal.ProviderName);
        Assert.NotEqual(tool.Name, tool.ProviderName, StringComparer.Ordinal);
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", tool.ProviderName);
        using (var firstBody = JsonDocument.Parse(handler.Requests[0].Body))
        {
            var serializedTool = Assert.Single(
                firstBody.RootElement.GetProperty("tools").EnumerateArray());
            var serializedName = providerKind == AiProviderKind.Anthropic
                ? serializedTool.GetProperty("name").GetString()
                : serializedTool
                    .GetProperty("function")
                    .GetProperty("name")
                    .GetString();
            Assert.Equal(tool.ProviderName, serializedName);
        }

        var toolResult = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromText("contents"));
        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [toolResult],
            proposalTools,
            [],
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        using var secondBody = JsonDocument.Parse(handler.Requests[1].Body);
        var assistant = secondBody.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(message => string.Equals(message.GetProperty("role").GetString(), "assistant", StringComparison.Ordinal));
        var historicalName = providerKind == AiProviderKind.Anthropic
            ? Assert.Single(assistant.GetProperty("content").EnumerateArray())
                .GetProperty("name")
                .GetString()
            : Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray())
                .GetProperty("function")
                .GetProperty("name")
                .GetString();
        Assert.Equal(tool.ProviderName, historicalName);
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    [InlineData(AiProviderKind.Anthropic)]
    public async Task ProviderAdapterSupportsBoundedSteeringOverlapOnOneInstance(
        AiProviderKind providerKind)
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(providerKind);
        using var handler = new ConcurrentSteeringHandler(providerKind);
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = CreateSession();
        var turn = session.RunTurnAsync(
            "Inspect production.",
            [],
            provider,
            CancellationToken.None).AsTask();
        await handler.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var steering = session.Steer(
                session.Snapshot().Generation,
                "Inspect staging instead.");

            Assert.True(steering.Succeeded);
            await handler.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await turn.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Succeeded);
            Assert.Equal(2, handler.MaximumActiveRequests);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(
                [
                    "Inspect production.\n\nSteering update:\n"
                        + "Inspect staging instead.",
                    "replacement response",
                ],
                session.Snapshot().Conversation.Select(message =>
                    message.Content), StringComparer.Ordinal);
        }
        finally
        {
            handler.ReleaseFirst.TrySetResult();
            await handler.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task OpenAiFragmentedToolArgumentsBecomeAnInertProposal()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(OpenAiToolStream())));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Inspect the file.",
            [ReadFileTool()],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.ToolUse, result.StopReason);
        var proposal = Assert.Single(result.ToolProposals);
        Assert.True(proposal.ContainsUntrustedContent);
        Assert.Equal("call-1", proposal.ProviderCallId);
        Assert.Equal("read_file", proposal.ToolName);
        Assert.Equal("/tmp/input.txt", proposal.Arguments.GetProperty("path").GetString());
        Assert.Equal(NativeAgentSessionState.AwaitingToolDecision, session.Snapshot().State);
        Assert.Equal(proposal, Assert.Single(session.Snapshot().PendingToolProposals));
    }

    [Fact]
    public async Task AnthropicFragmentedToolArgumentsBecomeAnInertProposal()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(AnthropicToolStream())));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Inspect the file.",
            [ReadFileTool()],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.ToolUse, result.StopReason);
        var proposal = Assert.Single(result.ToolProposals);
        Assert.True(proposal.ContainsUntrustedContent);
        Assert.Equal("tool-1", proposal.ProviderCallId);
        Assert.Equal("read_file", proposal.ToolName);
        Assert.Equal("/tmp/input.txt", proposal.Arguments.GetProperty("path").GetString());
        Assert.Equal(NativeAgentSessionState.AwaitingToolDecision, session.Snapshot().State);
        Assert.Equal(proposal, Assert.Single(session.Snapshot().PendingToolProposals));
    }

    [Fact]
    public async Task OpenAiContinuationSendsAssistantToolCallAndCorrelatedToolResult()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        var responseNumber = 0;
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(
                SseResponse(
                    Interlocked.Increment(ref responseNumber) == 1
                        ? OpenAiToolStream()
                        : OpenAiTextStream("handled"))));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = CreateSession();
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Inspect the file.",
            tools,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var result = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Failed,
            "file_not_found",
            AgentToolResultValue.FromText("No such file."));

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal("handled", session.Snapshot().Conversation[^1].Content);
        Assert.Equal(2, handler.Requests.Count);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Collection(
            body.RootElement.GetProperty("messages").EnumerateArray(),
            message =>
            {
                Assert.Equal("user", message.GetProperty("role").GetString());
                Assert.Equal(
                    "Inspect the file.",
                    message.GetProperty("content").GetString());
            },
            message =>
            {
                Assert.Equal("assistant", message.GetProperty("role").GetString());
                Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);
                var call = Assert.Single(
                    message.GetProperty("tool_calls").EnumerateArray());
                Assert.Equal("call-1", call.GetProperty("id").GetString());
                Assert.Equal("function", call.GetProperty("type").GetString());
                var function = call.GetProperty("function");
                Assert.Equal("read_file", function.GetProperty("name").GetString());
                Assert.Equal(
                    "{\"path\":\"/tmp/input.txt\"}",
                    function.GetProperty("arguments").GetString());
            },
            message =>
            {
                Assert.Equal("tool", message.GetProperty("role").GetString());
                Assert.Equal(
                    "call-1",
                    message.GetProperty("tool_call_id").GetString());
                using var content = JsonDocument.Parse(
                    message.GetProperty("content").GetString()!);
                Assert.False(content.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(
                    "file_not_found",
                    content.RootElement.GetProperty("code").GetString());
                Assert.Equal(
                    "text",
                    content.RootElement.GetProperty("value_kind").GetString());
                Assert.Equal(
                    "No such file.",
                    content.RootElement.GetProperty("value").GetString());
            });
    }

    [Fact]
    public async Task AnthropicContinuationSendsToolUseAndGroupedToolResultBlocks()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        var responseNumber = 0;
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(
                SseResponse(
                    Interlocked.Increment(ref responseNumber) == 1
                        ? AnthropicToolStream()
                        : AnthropicTextStream("handled"))));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = CreateSession();
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Inspect the file.",
            tools,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var result = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Failed,
            "file_read_failed",
            AgentToolResultValue.FromJson(
                "{\"text\":\"hello\"}"u8.ToArray()));

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Collection(
            body.RootElement.GetProperty("messages").EnumerateArray(),
            message =>
            {
                Assert.Equal("user", message.GetProperty("role").GetString());
                Assert.Equal(
                    "Inspect the file.",
                    message.GetProperty("content").GetString());
            },
            message =>
            {
                Assert.Equal("assistant", message.GetProperty("role").GetString());
                var block = Assert.Single(
                    message.GetProperty("content").EnumerateArray());
                Assert.Equal("tool_use", block.GetProperty("type").GetString());
                Assert.Equal("tool-1", block.GetProperty("id").GetString());
                Assert.Equal("read_file", block.GetProperty("name").GetString());
                Assert.Equal(
                    "/tmp/input.txt",
                    block.GetProperty("input").GetProperty("path").GetString());
            },
            message =>
            {
                Assert.Equal("user", message.GetProperty("role").GetString());
                var block = Assert.Single(
                    message.GetProperty("content").EnumerateArray());
                Assert.Equal("tool_result", block.GetProperty("type").GetString());
                Assert.Equal(
                    "tool-1",
                    block.GetProperty("tool_use_id").GetString());
                Assert.True(block.GetProperty("is_error").GetBoolean());
                using var content = JsonDocument.Parse(
                    block.GetProperty("content").GetString()!);
                Assert.False(content.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(
                    "file_read_failed",
                    content.RootElement.GetProperty("code").GetString());
                Assert.Equal(
                    "json",
                    content.RootElement.GetProperty("value_kind").GetString());
                Assert.Equal(
                    "hello",
                    content.RootElement
                        .GetProperty("value")
                        .GetProperty("text")
                        .GetString());
            });
    }

    [Fact]
    public async Task AnthropicContinuationReplaysSignedThinkingBeforeItsToolSlot()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        var responseNumber = 0;
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(
                Interlocked.Increment(ref responseNumber) == 1
                    ? AnthropicSignedThinkingToolStream()
                    : AnthropicTextStream("handled"))));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile, "claude-sonnet-5");
        var session = CreateSession();
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Inspect the file.",
            [],
            tools,
            AgentReasoningEffort.High,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                "ok",
                AgentToolResultValue.FromText("contents"))],
            tools,
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        var assistant = body.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(message => string.Equals(message.GetProperty("role").GetString(), "assistant", StringComparison.Ordinal));
        Assert.Collection(
            assistant.GetProperty("content").EnumerateArray(),
            block =>
            {
                Assert.Equal("thinking", block.GetProperty("type").GetString());
                Assert.Equal("Safe summary.", block.GetProperty("thinking").GetString());
                Assert.Equal("opaque-signature", block.GetProperty("signature").GetString());
            },
            block =>
            {
                Assert.Equal("tool_use", block.GetProperty("type").GetString());
                Assert.Equal("tool-1", block.GetProperty("id").GetString());
            });
    }

    [Fact]
    public async Task AnthropicModelSwitchKeepsVisibleTranscriptAndDropsSignedThinking()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        var responseNumber = 0;
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(
                Interlocked.Increment(ref responseNumber) == 1
                    ? AnthropicSignedThinkingToolStream()
                    : AnthropicTextStream("handled"))));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Inspect the file.",
            [],
            tools,
            AgentReasoningEffort.High,
            factory.Create(profile, "claude-sonnet-5"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                "ok",
                AgentToolResultValue.FromText("contents"))],
            tools,
            tools,
            factory.Create(profile, "claude-sonnet-5-fast"),
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(
            "claude-sonnet-5-fast",
            body.RootElement.GetProperty("model").GetString());
        var assistant = body.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Single(message => string.Equals(message.GetProperty("role").GetString(), "assistant", StringComparison.Ordinal));
        var toolUse = Assert.Single(assistant.GetProperty("content").EnumerateArray());
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.DoesNotContain(
            "opaque-signature",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicReplayBoundToAnotherCredentialReferenceFailsBeforeHttp()
    {
        using var vault = new InMemorySecretVault();
        var endpoint = new Uri("http://127.0.0.1:4242/v1/");
        var firstProfile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            endpoint,
            "claude-sonnet-5",
            "secret-anthropic-first");
        var replacementProfile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            endpoint,
            "claude-sonnet-5",
            "secret-anthropic-replacement");
        var responseNumber = 0;
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(
                Interlocked.Increment(ref responseNumber) == 1
                    ? AnthropicSignedThinkingToolStream()
                    : AnthropicTextStream("must not be sent"))));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Inspect the file.",
            [],
            tools,
            AgentReasoningEffort.High,
            factory.Create(firstProfile, "claude-sonnet-5"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                "ok",
                AgentToolResultValue.FromText("contents"))],
            tools,
            tools,
            factory.Create(replacementProfile, "claude-sonnet-5"),
            CancellationToken.None);

        Assert.False(continuation.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, continuation.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnthropicToolTurnWithUnsignedThinkingFailsWithoutAProposal()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        var unsigned = AnthropicSignedThinkingToolStream().Replace(
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"opaque-signature\"}}\n\n",
            string.Empty,
            StringComparison.Ordinal);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(unsigned)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Inspect the file.",
            [],
            [ReadFileTool()],
            AgentReasoningEffort.High,
            factory.Create(profile, "claude-sonnet-5"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Empty(result.ToolProposals);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnthropicRejectsOverlappingContentBlocks()
    {
        var builder = new StringBuilder();
        AppendAnthropicEvent(builder, "message_start", "{\"type\":\"message_start\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");

        var result = await RunAnthropicStreamAsync(builder.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
    }

    [Fact]
    public async Task AnthropicRejectsDeltasForOpaqueRedactedThinking()
    {
        var builder = new StringBuilder();
        AppendAnthropicEvent(builder, "message_start", "{\"type\":\"message_start\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"opaque\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"must-not-be-accepted\"}}");

        var result = await RunAnthropicStreamAsync(builder.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
    }

    [Fact]
    public async Task AnthropicRejectsAnEmptyFinalTextBlock()
    {
        var builder = new StringBuilder();
        AppendAnthropicEvent(builder, "message_start", "{\"type\":\"message_start\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":0}");

        var result = await RunAnthropicStreamAsync(builder.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
    }

    [Theory]
    [InlineData(
        AiProviderKind.OpenAiCompatible,
        "length",
        AgentProviderStopReason.MaximumTokens)]
    [InlineData(
        AiProviderKind.OpenAiCompatible,
        "content_filter",
        AgentProviderStopReason.ContentFiltered)]
    [InlineData(
        AiProviderKind.Anthropic,
        "max_tokens",
        AgentProviderStopReason.MaximumTokens)]
    [InlineData(
        AiProviderKind.Anthropic,
        "refusal",
        AgentProviderStopReason.ContentFiltered)]
    public async Task ProviderStopReasonsMapToKernelReasons(
        AiProviderKind providerKind,
        string providerReason,
        AgentProviderStopReason expected)
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(providerKind);
        var stream = providerKind == AiProviderKind.Anthropic
            ? AnthropicTextStream("result", stopReason: providerReason)
            : OpenAiTextStream("result", finishReason: providerReason);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.StopReason);
        Assert.Equal("result", session.Snapshot().Conversation[^1].Content);
    }

    [Fact]
    public async Task DuplicateJsonPropertiesFailWithoutCommittingProviderContent()
    {
        const string stream = """
            data: {"choices":[{"index":0,"delta":{"content":"untrusted"},"finish_reason":null}],"choices":[]}

            data: [DONE]

            """;
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task MalformedJsonFailsWithoutCommittingProviderContent()
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start"

            """;
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task OpenAiRequiresDoneTerminalEvent()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        var stream = OpenAiTextStream("orphaned", includeDone: false);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task AnthropicRequiresMessageStopTerminalEvent()
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        var stream = AnthropicTextStream("orphaned", includeMessageStop: false);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task OpenAiRejectsContentAfterFinishReason()
    {
        const string stream = """
            data: {"choices":[{"index":0,"delta":{"content":"before"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"choices":[{"index":0,"delta":{"content":"after"},"finish_reason":null}]}

            data: [DONE]

            """;
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task AnthropicRejectsContentBlockAfterStopReason()
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start"}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"before"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":"after"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task OversizedSseEventFailsBeforeItsContentReachesTheKernel()
    {
        var limits = new AiProviderRuntimeLimits(
            maximumStreamResponseBytes: 1024,
            maximumSseEventBytes: 256,
            maximumProviderFragmentBytes: 64);
        var padding = new string('x', 300);
        var stream =
            $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":null}}],"
            + $"\"padding\":\"{padding}\"}}\n\n";
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler, limits);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task OversizedStreamFailsBeforeItsContentReachesTheKernel()
    {
        var limits = new AiProviderRuntimeLimits(
            maximumStreamResponseBytes: 1024,
            maximumSseEventBytes: 512,
            maximumProviderFragmentBytes: 64);
        var stream = $"data: {new string('x', 2 * 1024)}\n\n";
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler, limits);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task CancellingTurnCancelsInFlightHttpRequestWithoutCommitting()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return SseResponse(OpenAiTextStream("late"));
                }
                finally
                {
                    requestStopped.TrySetResult();
                }
            });
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();
        using var cancellation = new CancellationTokenSource();

        var turn = session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            cancellation.Token).AsTask();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var result = await turn.WaitAsync(TimeSpan.FromSeconds(5));
        await requestStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Cancelled, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task HttpFailureIsMappedWithoutExposingProviderBody()
    {
        const string sentinel = "provider-internal-secret-diagnostic";
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    $"{{\"error\":\"{sentinel}\"}}",
                    Encoding.UTF8,
                    "application/json"),
            }));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);
        Assert.DoesNotContain(
            sentinel,
            JsonSerializer.Serialize(session.Snapshot()),
            StringComparison.Ordinal);

        using var directHandler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(sentinel),
            }));
        using var transport = new AiProviderHttpTransport(vault, directHandler);
        using var request = await transport.CreateRequestAsync(
            profile,
            HttpMethod.Post,
            "chat/completions",
            "text/event-stream",
            body: null,
            CancellationToken.None);
        var exception = await Assert.ThrowsAsync<AiProviderClientException>(
            async () =>
            {
                using var response = await transport.SendAsync(
                    profile,
                    request,
                    CancellationToken.None);
            });
        Assert.Equal(AiProviderRuntimeErrorCode.AuthenticationFailed, exception.Code);
        Assert.Equal("ai_provider_authentication_failed", exception.StableCode);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicProjectsAdaptiveReasoningSummaryAndUsage()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            new Uri("https://anthropic.example/v1/"),
            "claude-sonnet-5");
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(AnthropicMetadataStream())));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.Medium,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Snapshot().Conversation[^1];
        Assert.Equal("Answer.", assistant.Content);
        Assert.Equal("Checked safely.", assistant.ReasoningSummary);
        Assert.Equal(15, assistant.Usage!.InputTokens);
        Assert.Equal(6, assistant.Usage.OutputTokens);
        Assert.Equal(4, assistant.Usage.CachedInputTokens);
        Assert.Equal(2, assistant.Usage.ReasoningTokens);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal("adaptive", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(
            "summarized",
            root.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal(
            "medium",
            root.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task AnthropicAdaptive46ConsumesRawThinkingWithoutExposingItAsSummary()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            new Uri("https://anthropic.example/v1/"),
            "claude-sonnet-4-6");
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(AnthropicMetadataStream())));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.Medium,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Snapshot().Conversation[^1];
        Assert.Equal("Answer.", assistant.Content);
        Assert.Null(assistant.ReasoningSummary);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var thinking = body.RootElement.GetProperty("thinking");
        Assert.Equal("adaptive", thinking.GetProperty("type").GetString());
        Assert.False(thinking.TryGetProperty("display", out _));
        Assert.Equal(
            "medium",
            body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Theory]
    [InlineData("claude-opus-4-6", "max")]
    [InlineData("claude-opus-4-7", "xhigh")]
    public async Task AnthropicMapsExtraHighToTheExactModelNativeEffort(
        string model,
        string expectedEffort)
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            new Uri("https://anthropic.example/v1/"),
            model);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(AnthropicMetadataStream())));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.ExtraHigh,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        Assert.Equal(
            expectedEffort,
            body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task AnthropicModelThatCannotDisableThinkingRejectsOffBeforeHttp()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.Anthropic,
            new Uri("https://anthropic.example/v1/"),
            "claude-fable-5");
        using var handler = new StubHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("HTTP must not run."));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.Off,
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CompatibleStreamProjectsStandardUsageButRejectsUnportableEffort()
    {
        const string stream = """
            data: {"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":3},"completion_tokens_details":{"reasoning_tokens":1}}}

            data: [DONE]

            """ + "\n\n";
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.OpenAiCompatible);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var usage = session.Snapshot().Conversation[^1].Usage!;
        Assert.Equal((10L, 4L, 3L, 1L),
            (usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.ReasoningTokens));

        using var rejectedHandler = new StubHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("HTTP must not run."));
        using var rejectedFactory = new AiProviderFactory(vault, rejectedHandler);
        var rejectedSession = CreateSession();
        var rejected = await rejectedSession.RunTurnAsync(
            "Respond.",
            [],
            AgentReasoningEffort.High,
            rejectedFactory.Create(profile),
            CancellationToken.None);
        Assert.False(rejected.Succeeded);
        Assert.Empty(rejectedHandler.Requests);
    }

    [Theory]
    [InlineData(AiProviderKind.Anthropic)]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    public async Task ProviderSerializesImageUsingItsNativeWireShape(
        AiProviderKind providerKind)
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            providerKind,
            providerKind == AiProviderKind.Anthropic
                ? new Uri("https://anthropic.example/v1/")
                : new Uri("https://compatible.example/v1/"));
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(
                providerKind == AiProviderKind.Anthropic
                    ? AnthropicTextStream("seen")
                    : OpenAiTextStream("seen"))));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();
        var image = TinyPng();

        var result = await session.RunTurnAsync(
            "Describe it.",
            [image],
            [],
            AgentReasoningEffort.Automatic,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var content = Assert.Single(
                body.RootElement.GetProperty("messages").EnumerateArray())
            .GetProperty("content")
            .EnumerateArray()
            .ToArray();
        var imagePart = providerKind == AiProviderKind.Anthropic
            ? content[0]
            : content[1];
        if (providerKind == AiProviderKind.Anthropic)
        {
            Assert.Equal("image", imagePart.GetProperty("type").GetString());
            Assert.Equal(
                Convert.ToBase64String(image.Content),
                imagePart.GetProperty("source").GetProperty("data").GetString());
        }
        else
        {
            Assert.Equal("image_url", imagePart.GetProperty("type").GetString());
            Assert.StartsWith(
                "data:image/png;base64,",
                imagePart.GetProperty("image_url").GetProperty("url").GetString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProviderWithoutImageCapabilityFailsBeforeHttp()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateAuthenticatedProfileAsync(
            vault,
            AiProviderKind.DeepSeek,
            new Uri("https://api.deepseek.com/v1/"));
        using var handler = new StubHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("HTTP must not run."));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Describe it.",
            [TinyPng()],
            [],
            AgentReasoningEffort.Automatic,
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(handler.Requests);
    }

    private static async Task<AgentTurnResult> RunAnthropicStreamAsync(string stream)
    {
        using var vault = new InMemorySecretVault();
        var profile = CreateLoopbackProfile(AiProviderKind.Anthropic);
        using var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(stream)));
        using var factory = new AiProviderFactory(vault, handler);
        var session = CreateSession();
        return await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);
    }

    private static NativeAgentSession CreateSession(params AgentMessage[] initialMessages) =>
        new(new AgentRunId("provider-conformance-run"), initialMessages);

    private static AgentToolDefinition ReadFileTool() =>
        new(
            "read_file",
            "Read a UTF-8 text file.",
            Encoding.UTF8.GetBytes(
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string" }
                  },
                  "required": ["path"]
                }
            """));

    private static AgentImageAttachment TinyPng() =>
        new(
            "capture.png",
            "image/png",
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

    private static AiProviderProfile CreateLoopbackProfile(AiProviderKind providerKind) =>
        new(
            new AiProviderProfileId($"profile-{providerKind.ToString().ToLowerInvariant()}"),
            AiProviderProfile.CurrentSchemaVersion,
            "Test provider",
            providerKind,
            new Uri("http://127.0.0.1:4242/v1/"),
            new AiProviderAuthentication.None(),
            Model,
            order: 0);

    private static async Task<AiProviderProfile> CreateAuthenticatedProfileAsync(
        InMemorySecretVault vault,
        AiProviderKind providerKind,
        Uri endpoint,
        string model = Model,
        string? secretReference = null)
    {
        var profileId = new AiProviderProfileId(
            $"profile-{providerKind.ToString().ToLowerInvariant()}");
        var reference = new SecretRef(
            secretReference ?? $"secret-{providerKind.ToString().ToLowerInvariant()}");
        var scope = new SecretScope(SecretScopeKind.AiProvider, profileId.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            profileId.Value);
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(ApiKey));
        var created = await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Provider API key",
                SecretKind.ApiKey,
                scope,
                purpose),
            material,
            CancellationToken.None);
        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(created);
        return new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "Test provider",
            providerKind,
            endpoint,
            new AiProviderAuthentication.ApiKey(reference),
            model,
            order: 0);
    }

    private static HttpResponseMessage SseResponse(string stream) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        };

    private static string OpenAiTextStream(
        string first,
        string? second = null,
        string finishReason = "stop",
        bool includeDone = true)
    {
        var builder = new StringBuilder();
        AppendOpenAiTextDelta(builder, first);
        if (second is not null)
        {
            AppendOpenAiTextDelta(builder, second);
        }

        builder.Append(
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":");
        builder.Append(JsonSerializer.Serialize(finishReason));
        builder.Append("}]}\n\n");
        if (includeDone)
        {
            builder.Append("data: [DONE]\n\n");
        }

        return builder.ToString();
    }

    private static void AppendOpenAiTextDelta(StringBuilder builder, string value)
    {
        builder.Append(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":");
        builder.Append(JsonSerializer.Serialize(value));
        builder.Append("},\"finish_reason\":null}]}\n\n");
    }

    private static string OpenAiToolStream(string name = "read_file")
    {
        var builder = new StringBuilder();
        builder.Append(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{"
            + "\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{"
            + "\"name\":");
        builder.Append(JsonSerializer.Serialize(name));
        builder.Append(",\"arguments\":");
        builder.Append(JsonSerializer.Serialize("{\"path\":\"/tmp/"));
        builder.Append("}}]},\"finish_reason\":null}]}\n\n");
        builder.Append(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{"
            + "\"index\":0,\"function\":{\"arguments\":");
        builder.Append(JsonSerializer.Serialize("input.txt\"}"));
        builder.Append("}}]},\"finish_reason\":null}]}\n\n");
        builder.Append(
            "data: {\"choices\":[{\"index\":0,\"delta\":{},"
            + "\"finish_reason\":\"tool_calls\"}]}\n\n");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    private static string AnthropicTextStream(
        string first,
        string? second = null,
        string stopReason = "end_turn",
        bool includeMessageStop = true)
    {
        var builder = new StringBuilder();
        AppendAnthropicEvent(builder, "message_start", "{\"type\":\"message_start\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":0,"
            + "\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        AppendAnthropicTextDelta(builder, first);
        if (second is not null)
        {
            AppendAnthropicTextDelta(builder, second);
        }

        AppendAnthropicEvent(
            builder,
            "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":0}");
        AppendAnthropicEvent(
            builder,
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":"
            + JsonSerializer.Serialize(stopReason)
            + "}}");
        if (includeMessageStop)
        {
            AppendAnthropicEvent(
                builder,
                "message_stop",
                "{\"type\":\"message_stop\"}");
        }

        return builder.ToString();
    }

    private static void AppendAnthropicTextDelta(StringBuilder builder, string value) =>
        AppendAnthropicEvent(
            builder,
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":0,"
            + "\"delta\":{\"type\":\"text_delta\",\"text\":"
            + JsonSerializer.Serialize(value)
            + "}}");

    private static string AnthropicToolStream(string name = "read_file")
    {
        var builder = new StringBuilder();
        AppendAnthropicEvent(builder, "message_start", "{\"type\":\"message_start\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":0,"
            + "\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool-1\","
            + "\"name\":"
            + JsonSerializer.Serialize(name)
            + ",\"input\":{}}}");
        AppendAnthropicToolDelta(builder, "{\"path\":\"/tmp/");
        AppendAnthropicToolDelta(builder, "input.txt\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":0}");
        AppendAnthropicEvent(
            builder,
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}");
        AppendAnthropicEvent(builder, "message_stop", "{\"type\":\"message_stop\"}");
        return builder.ToString();
    }

    private static string AnthropicSignedThinkingToolStream()
    {
        var builder = new StringBuilder();
        AppendAnthropicEvent(builder, "message_start", "{\"type\":\"message_start\"}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Safe summary.\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"opaque-signature\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":0}");
        AppendAnthropicEvent(
            builder,
            "content_block_start",
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tool-1\",\"name\":\"read_file\",\"input\":{}}}");
        AppendAnthropicEvent(
            builder,
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"/tmp/input.txt\\\"}\"}}");
        AppendAnthropicEvent(
            builder,
            "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":1}");
        AppendAnthropicEvent(
            builder,
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}");
        AppendAnthropicEvent(builder, "message_stop", "{\"type\":\"message_stop\"}");
        return builder.ToString();
    }

    private static string AnthropicMetadataStream() =>
        """
        event: message_start
        data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_creation_input_tokens":1,"cache_read_input_tokens":4}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Checked safely."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"opaque-signature"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Answer."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":6,"output_tokens_details":{"thinking_tokens":2}}}

        event: message_stop
        data: {"type":"message_stop"}

        """ + "\n\n";

    private static void AppendAnthropicToolDelta(StringBuilder builder, string value) =>
        AppendAnthropicEvent(
            builder,
            "content_block_delta",
            "{\"type\":\"content_block_delta\",\"index\":0,"
            + "\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":"
            + JsonSerializer.Serialize(value)
            + "}}");

    private static void AppendAnthropicEvent(
        StringBuilder builder,
        string eventType,
        string json)
    {
        builder.Append("event: ");
        builder.Append(eventType);
        builder.Append('\n');
        builder.Append("data: ");
        builder.Append(json);
        builder.Append("\n\n");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = [];

        public CapturedRequest? LastRequest { get; private set; }

        public IReadOnlyList<CapturedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = await CapturedRequest.CreateAsync(
                request,
                cancellationToken);
            _requests.Add(LastRequest);
            var response = await respond(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class ConcurrentSteeringHandler(
        AiProviderKind providerKind) : HttpMessageHandler
    {
        private int _callCount;
        private int _activeRequests;
        private int _maximumActiveRequests;

        public ConcurrentQueue<CapturedRequest> Requests { get; } = [];

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumActiveRequests =>
            Volatile.Read(ref _maximumActiveRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            Requests.Enqueue(
                await CapturedRequest.CreateAsync(
                    request,
                    CancellationToken.None));
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximumActiveRequests(active);
            try
            {
                if (call == 1)
                {
                    FirstEntered.TrySetResult();
                    await ReleaseFirst.Task.ConfigureAwait(false);
                    return Response("obsolete response", request);
                }

                if (call == 2)
                {
                    SecondEntered.TrySetResult();
                    return Response("replacement response", request);
                }

                throw new InvalidOperationException(
                    "Bounded steering may create only two provider requests.");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
                if (call == 1)
                {
                    FirstCompleted.TrySetResult();
                }
            }
        }

        private HttpResponseMessage Response(
            string value,
            HttpRequestMessage request)
        {
            var stream = providerKind == AiProviderKind.Anthropic
                ? AnthropicTextStream(value)
                : OpenAiTextStream(value);
            var response = SseResponse(stream);
            response.RequestMessage = request;
            return response;
        }

        private void UpdateMaximumActiveRequests(int active)
        {
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumActiveRequests);
                if (active <= maximum
                    || Interlocked.CompareExchange(
                        ref _maximumActiveRequests,
                        active,
                        maximum) == maximum)
                {
                    return;
                }
            }
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string ContentType,
        string Body)
    {
        public string Header(string name) => Headers[name];

        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers.Add(header.Key, string.Join(", ", header.Value));
            }

            var contentType = request.Content?.Headers.ContentType?.ToString() ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new CapturedRequest(
                request.Method,
                request.RequestUri
                    ?? throw new InvalidOperationException("The request URI is required."),
                headers,
                contentType,
                body);
        }
    }
}
