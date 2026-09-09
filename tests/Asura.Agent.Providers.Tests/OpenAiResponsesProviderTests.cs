using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Infrastructure;

namespace Asura.Agent.Providers.Tests;

public sealed class OpenAiResponsesProviderTests
{
    private const string ApiKey = "openai-test-key";
    private const string Model = "gpt-test";

    [Fact]
    public async Task FirstPartyOpenAiStreamsResponsesApiAndSendsResponsesShape()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesTextStream("Hello"));
        var limits = new AiProviderRuntimeLimits(maximumOutputTokens: 777);
        using var factory = new AiProviderFactory(vault, handler, limits);
        var session = new NativeAgentSession(
            new AgentRunId("openai-responses-run"),
            [new AgentMessage(AgentMessageRole.System, "Be concise.")]);

        var result = await session.RunTurnAsync(
            "Say hello.",
            [ReadFileTool()],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.EndTurn, result.StopReason);
        Assert.Equal("Hello", session.Snapshot().Conversation[^1].Content);
        var request = Assert.IsType<CapturedRequest>(handler.LastRequest);
        Assert.Equal(new Uri("https://api.openai.com/v1/responses"), request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(777, root.GetProperty("max_output_tokens").GetInt32());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("detailed", reasoning.GetProperty("summary").GetString());
        Assert.False(reasoning.TryGetProperty("effort", out _));
        Assert.Equal("Be concise.", root.GetProperty("instructions").GetString());
        var input = Assert.Single(root.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        Assert.Equal(
            "Say hello.",
            Assert.Single(input.GetProperty("content").EnumerateArray())
                .GetProperty("text")
                .GetString());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal(
            "object",
            tool.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompactionSummaryIsResponsesUserInputAndNeverInstructions()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesTextStream("continued"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(
            new AgentRunId("openai-summary-role"),
            [
                new AgentMessage(AgentMessageRole.System, "Trusted instructions."),
                new AgentMessage(AgentMessageRole.Summary, "Untrusted compacted history."),
            ]);

        var result = await session.RunTurnAsync(
            "Continue.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var root = body.RootElement;
        Assert.Equal("Trusted instructions.", root.GetProperty("instructions").GetString());
        Assert.Collection(
            root.GetProperty("input").EnumerateArray(),
            item =>
            {
                Assert.Equal("user", item.GetProperty("role").GetString());
                Assert.Equal(
                    "Untrusted compacted history.",
                    Assert.Single(item.GetProperty("content").EnumerateArray())
                        .GetProperty("text")
                        .GetString());
            },
            item => Assert.Equal("user", item.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task MultilineAssistantTextCommitsAndReplaysOnTheNextTurn()
    {
        const string multiline = "First line.\n\nSecond line.\tIndented.";
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesTextStream(multiline),
            ResponsesTextStream("continued"));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = new NativeAgentSession(new AgentRunId("responses-multiline-replay"));

        var first = await session.RunTurnAsync(
            "Tell me a story.",
            [ReadFileTool()],
            provider,
            CancellationToken.None);
        var second = await session.RunTurnAsync(
            "Continue.",
            [ReadFileTool()],
            provider,
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("continued", session.Snapshot().Conversation[^1].Content);
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        var replayedAssistant = request.RootElement.GetProperty("input")
            .EnumerateArray()
            .Single(item => item.TryGetProperty("role", out var role)
                && string.Equals(role.GetString(), "assistant", StringComparison.Ordinal));
        Assert.Equal(
            multiline,
            Assert.Single(replayedAssistant.GetProperty("content").EnumerateArray())
                .GetProperty("text")
                .GetString());
    }

    [Fact]
    public async Task FunctionCallContinuationUsesResponsesItemsAndCallId()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesToolStream("call-1", "fc-1", "read_file", "{\"path\":\"/tmp/a\"}"),
            ResponsesTextStream("handled"));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = new NativeAgentSession(new AgentRunId("responses-continuation-run"));
        var tools = ImmutableArray.Create(ReadFileTool());

        var first = await session.RunTurnAsync(
            "Read the file.",
            tools,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var toolResult = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromText("contents"));

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [toolResult],
            tools,
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal("handled", session.Snapshot().Conversation[^1].Content);
        Assert.Equal(2, handler.Requests.Count);
        using var document = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Collection(
            document.RootElement.GetProperty("input").EnumerateArray(),
            item =>
            {
                Assert.Equal("user", item.GetProperty("role").GetString());
                Assert.Equal(
                    "Read the file.",
                    Assert.Single(item.GetProperty("content").EnumerateArray())
                        .GetProperty("text")
                        .GetString());
            },
            item =>
            {
                Assert.Equal("function_call", item.GetProperty("type").GetString());
                Assert.Equal("fc-1", item.GetProperty("id").GetString());
                Assert.Equal("call-1", item.GetProperty("call_id").GetString());
                Assert.Equal("read_file", item.GetProperty("name").GetString());
                Assert.Equal(
                    "{\"path\":\"/tmp/a\"}",
                    item.GetProperty("arguments").GetString());
            },
            item =>
            {
                Assert.Equal("function_call_output", item.GetProperty("type").GetString());
                Assert.Equal("call-1", item.GetProperty("call_id").GetString());
                using var output = JsonDocument.Parse(item.GetProperty("output").GetString()!);
                Assert.True(output.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("contents", output.RootElement.GetProperty("value").GetString());
            });
    }

    [Fact]
    public async Task JsonToolResultPreservesUnicodeInProviderRequest()
    {
        const string description = "Знайомство з ChatGPT";
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesToolStream("call-unicode", "fc-unicode", "read_file", "{\"path\":\"/tmp/a\"}"),
            ResponsesTextStream("handled"));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = new NativeAgentSession(new AgentRunId("responses-unicode-tool-result"));
        var tools = ImmutableArray.Create(ReadFileTool());

        var first = await session.RunTurnAsync(
            "Read the file.",
            tools,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var value = AgentToolResultValue.FromJson(
            Encoding.UTF8.GetBytes($$"""{"desc":"{{description}}"}"""));

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                "ok",
                value)],
            tools,
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        var requestBody = handler.Requests[1].Body;
        Assert.Contains(description, requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0417", requestBody, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(requestBody);
        var output = document.RootElement.GetProperty("input")
            .EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type)
                && string.Equals(
                    type.GetString(),
                    "function_call_output",
                    StringComparison.Ordinal))
            .GetProperty("output")
            .GetString();
        Assert.Contains(description, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReasoningItemIsBackfilledAndReplayedBeforeItsToolSlot()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesReasoningToolStream(),
            ResponsesTextStream("handled"));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = new NativeAgentSession(new AgentRunId("responses-reasoning-replay"));
        var tools = ImmutableArray.Create(ReadFileTool());

        var first = await session.RunTurnAsync(
            "Read it.",
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
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(
            "reasoning.encrypted_content",
            Assert.Single(request.RootElement.GetProperty("include").EnumerateArray())
                .GetString());
        Assert.Collection(
            request.RootElement.GetProperty("input").EnumerateArray(),
            item => Assert.Equal("user", item.GetProperty("role").GetString()),
            item =>
            {
                Assert.Equal("reasoning", item.GetProperty("type").GetString());
                Assert.Equal("rs-1", item.GetProperty("id").GetString());
                Assert.Equal(
                    "encrypted-reasoning",
                    item.GetProperty("encrypted_content").GetString());
            },
            item =>
            {
                Assert.Equal("function_call", item.GetProperty("type").GetString());
                Assert.Equal("fc-1", item.GetProperty("id").GetString());
            },
            item => Assert.Equal(
                "function_call_output",
                    item.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task TerminalReasoningWithoutCiphertextPreservesDoneCiphertext()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesReasoningToolStreamWithDoneCiphertext(),
            ResponsesTextStream("handled"));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = new NativeAgentSession(new AgentRunId("responses-done-ciphertext"));
        var tools = ImmutableArray.Create(ReadFileTool());

        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            AgentReasoningEffort.High,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var continuation = await SubmitResultAsync(session, proposal, tools, provider);

        Assert.True(continuation.Succeeded);
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        var reasoning = request.RootElement.GetProperty("input")
            .EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "reasoning", StringComparison.Ordinal));
        Assert.Equal(
            "done-ciphertext",
            reasoning.GetProperty("encrypted_content").GetString());
    }

    [Fact]
    public async Task StoreFalseReasoningWithoutCiphertextFailsTheToolTurn()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesReasoningToolStreamWithoutCiphertext());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-missing-ciphertext"));

        var result = await session.RunTurnAsync(
            "Read it.",
            [ReadFileTool()],
            AgentReasoningEffort.High,
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RawReasoningRemainsPrivateWhileVisibleConversationCheckpoints()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesRawReasoningToolStream(),
            ResponsesTextStream("handled"));
        using var factory = new AiProviderFactory(vault, handler);
        var provider = factory.Create(profile);
        var session = new NativeAgentSession(new AgentRunId("responses-raw-reasoning"));
        var tools = ImmutableArray.Create(ReadFileTool());

        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            AgentReasoningEffort.High,
            provider,
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        Assert.Null(session.Snapshot().Conversation[^1].ReasoningSummary);
        Assert.True((await SubmitResultAsync(session, proposal, tools, provider)).Succeeded);

        var checkpoint = session.CaptureCheckpoint();
        Assert.True(checkpoint.Succeeded);
        Assert.DoesNotContain(
            "private thought",
            checkpoint.Checkpoint!.PayloadJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "opaque-raw",
            checkpoint.Checkpoint.PayloadJson,
            StringComparison.Ordinal);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(checkpoint.Checkpoint).Session);
        _ = Assert.Single(
            restored.Snapshot().Conversation,
            message => message.Role == AgentMessageRole.Assistant
                && message.ToolCalls.Length > 0);
        Assert.Contains(
            restored.Snapshot().Conversation,
            message => message.Role == AgentMessageRole.Assistant
                && string.Equals(message.Content, "handled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizedMessageMustMatchStreamedVisibleText()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesDivergentMessageStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-shadow-message"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
    }

    [Fact]
    public async Task AnotherModelKeepsVisibleTranscriptAndDropsOpaqueReplayState()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesReasoningToolStream(),
            ResponsesTextStream("handled"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-binding-drift"));
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            factory.Create(profile),
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
            factory.Create(profile, "gpt-other"),
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("gpt-other", body.RootElement.GetProperty("model").GetString());
        Assert.DoesNotContain(
            "encrypted-reasoning",
            handler.Requests[1].Body,
            StringComparison.Ordinal);
        Assert.Contains(
            body.RootElement.GetProperty("input").EnumerateArray(),
            item => item.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "function_call", StringComparison.Ordinal));
        Assert.Contains(
            body.RootElement.GetProperty("input").EnumerateArray(),
            item => item.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "function_call_output", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplayStateBoundToAnotherEndpointFailsBeforeContinuationHttp()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesToolStream("call-1", "fc-1", "read_file", "{}"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-endpoint-drift"));
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            factory.Create(profile),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var retargeted = new AiProviderProfile(
            profile.Id,
            profile.SchemaVersion,
            profile.Name,
            profile.Identity,
            new Uri("https://retargeted.example/v1/"),
            profile.Authentication,
            profile.DefaultModel,
            profile.Order,
            profile.IsEnabled,
            profile.Protocol,
            profile.Capabilities);

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                "ok",
                AgentToolResultValue.FromText("contents"))],
            tools,
            tools,
            factory.Create(retargeted),
            CancellationToken.None);

        Assert.False(continuation.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, continuation.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReplayStateBoundToAnotherCredentialReferenceFailsBeforeHttp()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        var replacementReference = new SecretRef("openai-replacement-secret");
        await StoreApiKeyAsync(vault, profile.Id, replacementReference, "replacement-key");
        var replacement = new AiProviderProfile(
            profile.Id,
            profile.SchemaVersion,
            profile.Name,
            profile.Identity,
            profile.Endpoint,
            new AiProviderAuthentication.ApiKey(replacementReference),
            profile.DefaultModel,
            profile.Order,
            profile.IsEnabled,
            profile.Protocol,
            profile.Capabilities);
        using var handler = new CapturingHandler(
            ResponsesToolStream("call-1", "fc-1", "read_file", "{}"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-credential-drift"));
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            factory.Create(profile),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);

        var continuation = await SubmitResultAsync(
            session,
            proposal,
            tools,
            factory.Create(replacement));

        Assert.False(continuation.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, continuation.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RestoredConversationCanRebindToAnotherProfileWithoutReplayingPrivateState()
    {
        using var vault = new InMemorySecretVault();
        var original = await CreateProfileAsync(vault);
        var replacementId = new AiProviderProfileId("replacement-openai-profile");
        var replacementSecret = new SecretRef("replacement-openai-secret");
        await StoreApiKeyAsync(vault, replacementId, replacementSecret, "replacement-key");
        var replacement = new AiProviderProfile(
            replacementId,
            AiProviderProfile.CurrentSchemaVersion,
            "Replacement OpenAI",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.ApiKey(replacementSecret),
            Model,
            order: 0);
        using var handler = new CapturingHandler(
            ResponsesTextStream("Original answer."),
            ResponsesTextStream("Rebound answer."));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-profile-rebind"));

        var first = await session.RunTurnAsync(
            "First prompt.",
            [],
            factory.Create(original),
            CancellationToken.None);
        Assert.True(first.Succeeded);

        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(checkpoint).Session);
        Assert.True(restored.TrySetConversationRoute(replacement.Id, replacement.DefaultModel));

        var second = await restored.RunTurnAsync(
            "Continue on the replacement profile.",
            [],
            factory.Create(replacement),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("Rebound answer.", restored.Snapshot().Conversation[^1].Content);
        Assert.Equal(2, handler.Requests.Count);
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.DoesNotContain(
            request.RootElement.GetProperty("input").EnumerateArray(),
            item => item.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "reasoning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplayStateBoundToApiKeyRouteRejectsOAuthRoute()
    {
        using var vault = new InMemorySecretVault();
        var apiProfile = await CreateProfileAsync(vault);
        var routedModel = "gpt-5.6-terra";
        using var handler = new CapturingHandler(
            ResponsesToolStream("call-1", "fc-1", "read_file", "{}"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-auth-drift"));
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            factory.Create(apiProfile, routedModel),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var oauthProfile = new AiProviderProfile(
            apiProfile.Id,
            apiProfile.SchemaVersion,
            apiProfile.Name,
            apiProfile.Identity,
            apiProfile.Endpoint,
            new AiProviderAuthentication.OAuth(
                new SecretRef("unused-oauth-session"),
                AiProviderOAuthFlow.Browser),
            routedModel,
            apiProfile.Order,
            apiProfile.IsEnabled,
            apiProfile.Protocol,
            apiProfile.Capabilities);

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                "ok",
                AgentToolResultValue.FromText("contents"))],
            tools,
            tools,
            factory.Create(oauthProfile),
            CancellationToken.None);

        Assert.False(continuation.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, continuation.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResponsesAdapterPreservesMultipleFunctionCallsInProviderOrder()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesToolBatchStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-tool-batch-run"));

        var result = await session.RunTurnAsync(
            "Read both files.",
            [ReadFileTool()],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.ToolUse, result.StopReason);
        Assert.Collection(
            result.ToolProposals,
            proposal =>
            {
                Assert.Equal("call-1", proposal.ProviderCallId);
                Assert.Equal("/tmp/a", proposal.Arguments.GetProperty("path").GetString());
            },
            proposal =>
            {
                Assert.Equal("call-2", proposal.ProviderCallId);
                Assert.Equal("/tmp/b", proposal.Arguments.GetProperty("path").GetString());
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponsesRejectsNonContiguousOrReorderedOutputSlots(bool gap)
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(
            gap ? ResponsesGappedToolStream() : ResponsesReorderedTerminalStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId($"responses-order-{gap}"));

        var result = await session.RunTurnAsync(
            "Read it.",
            [ReadFileTool()],
            AgentReasoningEffort.High,
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
    }

    [Fact]
    public async Task NarrowedOpenAiProfileRejectsExplicitReasoningBeforeHttp()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        var narrowed = new AiProviderProfile(
            profile.Id,
            profile.SchemaVersion,
            profile.Name,
            profile.Identity,
            profile.Endpoint,
            profile.Authentication,
            profile.DefaultModel,
            profile.Order,
            profile.IsEnabled,
            profile.Protocol,
            new AiProviderCapabilities(true, true, true, false, true));
        using var handler = new CapturingHandler();
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-effort-disabled"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            AgentReasoningEffort.High,
            factory.Create(narrowed),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(AiProviderKind.XAi)]
    [InlineData(AiProviderKind.DeepSeek)]
    [InlineData(AiProviderKind.MoonshotAi)]
    [InlineData(AiProviderKind.OpenRouter)]
    [InlineData(AiProviderKind.Ollama)]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    public async Task CompatibleProviderIdentitiesShareChatCompletionsTransport(
        AiProviderKind identity)
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault, identity);
        using var handler = new CapturingHandler(ChatCompletionTextStream("ok"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(
            new AgentRunId($"compatible-{identity.ToString().ToLowerInvariant()}"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var expectedUri = new Uri(
            AiProviderProfile.DefaultEndpoint(identity),
            "chat/completions");
        Assert.Equal(expectedUri, handler.LastRequest!.Uri);
        Assert.Equal($"Bearer {ApiKey}", handler.LastRequest.Authorization);
    }

    [Fact]
    public async Task MissingOAuthSessionIsNotReinterpretedAsStaticBearerCredential()
    {
        using var vault = new InMemorySecretVault();
        using var handler = new CapturingHandler();
        using var factory = new AiProviderFactory(vault, handler);
        var profile = new AiProviderProfile(
            new AiProviderProfileId("openai-oauth-profile"),
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(
                new SecretRef("openai-oauth-session"),
                AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);

        var session = new NativeAgentSession(new AgentRunId("oauth-session-run"));
        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenAiOAuthUsesCodexEndpointAndNarrowResponsesRequestShape()
    {
        using var vault = new InMemorySecretVault();
        var profileId = new AiProviderProfileId("openai-codex-profile");
        var reference = new SecretRef("openai-codex-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            profileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "openai",
                "codex-access-token",
                "codex-refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "chatgpt-account"),
            CancellationToken.None);
        var profile = new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Browser),
            "gpt-5.6-terra",
            order: 0);
        using var handler = new CapturingHandler(ResponsesTextStream("Hello"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("codex-shape-run"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            AgentReasoningEffort.High,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.IsType<CapturedRequest>(handler.LastRequest);
        Assert.Equal(
            new Uri("https://chatgpt.com/backend-api/codex/responses"),
            request.Uri);
        Assert.Equal("Bearer codex-access-token", request.Authorization);
        Assert.Equal("asura", request.Originator);
        Assert.Equal("responses=experimental", request.OpenAiBeta);
        Assert.Equal("chatgpt-account", request.AccountId);
        using var body = JsonDocument.Parse(request.Body);
        Assert.False(body.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(
            "high",
            body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(
            "low",
            body.RootElement.GetProperty("text").GetProperty("verbosity").GetString());
    }

    [Fact]
    public async Task OpenAiOAuthAcceptsPinnedCodexSseWithoutContentType()
    {
        using var vault = new InMemorySecretVault();
        var profileId = new AiProviderProfileId("openai-codex-no-media-profile");
        var reference = new SecretRef("openai-codex-no-media-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            profileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "openai",
                "codex-access-token",
                "codex-refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "chatgpt-account"),
            CancellationToken.None);
        var profile = new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "OpenAI OAuth",
            AiProviderKind.OpenAi,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.OpenAi),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Browser),
            "gpt-5.6-terra",
            order: 0);
        using var handler = new MissingContentTypeHandler(ResponsesTextStream("Hello"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("codex-no-media-run"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Hello", session.Snapshot().Conversation[^1].Content);
    }

    [Fact]
    public async Task GitHubCopilotRoutesCodexModelsToResponsesAndOthersToChatCompletions()
    {
        using var vault = new InMemorySecretVault();
        var profileId = new AiProviderProfileId("github-copilot-profile");
        var reference = new SecretRef("github-copilot-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            profileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "github-copilot",
                "copilot-access-token",
                refreshToken: null,
                expiresAt: DateTimeOffset.MaxValue),
            CancellationToken.None);
        var profile = new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "GitHub Copilot",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.6-terra",
            order: 0);
        using var handler = new CapturingHandler(
            ChatCompletionTextStream("chat"),
            ResponsesTextStream("codex"));
        using var factory = new AiProviderFactory(vault, handler);

        var chatSession = new NativeAgentSession(new AgentRunId("copilot-chat-run"));
        var chat = await chatSession.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);
        var responsesSession = new NativeAgentSession(
            new AgentRunId("copilot-responses-run"));
        var responses = await responsesSession.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile, "gpt-5.3-codex"),
            CancellationToken.None);

        Assert.True(chat.Succeeded);
        Assert.True(responses.Succeeded);
        Assert.Equal(
            new Uri("https://api.githubcopilot.com/chat/completions"),
            handler.Requests[0].Uri);
        Assert.Equal(
            new Uri("https://api.githubcopilot.com/responses"),
            handler.Requests[1].Uri);
        using var responsesBody = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(
            AiProviderRuntimeLimits.Default.MaximumOutputTokens,
            responsesBody.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(responsesBody.RootElement.TryGetProperty("store", out _));
    }

    [Fact]
    public async Task GitHubCodexReplayCannotContinueThroughChatCompletions()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateGitHubProfileAsync(vault);
        using var handler = new CapturingHandler(
            ResponsesToolStream("call-1", "fc-1", "read_file", "{}"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("copilot-route-drift"));
        var tools = ImmutableArray.Create(ReadFileTool());
        var first = await session.RunTurnAsync(
            "Read it.",
            tools,
            factory.Create(profile, "gpt-5.3-codex"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);

        var continuation = await SubmitResultAsync(
            session,
            proposal,
            tools,
            factory.Create(profile, "gpt-5.5"));

        Assert.False(continuation.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, continuation.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResponsesStreamRequiresAResponseTerminalEvent()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesTruncatedStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-truncated-run"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        Assert.Equal(NativeAgentSessionState.Failed, session.Snapshot().State);
    }

    [Fact]
    public async Task ResponsesIncompleteMapsMaximumOutputTokens()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesIncompleteStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-incomplete-run"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.MaximumTokens, result.StopReason);
    }

    [Fact]
    public async Task ReasoningEffortSummaryAndUsageUseResponsesNativeFields()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesMetadataStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-metadata-run"));

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.High,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Snapshot().Conversation[^1];
        Assert.Equal("A concise answer.", assistant.Content);
        Assert.Equal("Checked the constraints.", assistant.ReasoningSummary);
        Assert.Equal(12, assistant.Usage!.InputTokens);
        Assert.Equal(5, assistant.Usage.OutputTokens);
        Assert.Equal(3, assistant.Usage.CachedInputTokens);
        Assert.Equal(2, assistant.Usage.ReasoningTokens);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        var reasoning = body.RootElement.GetProperty("reasoning");
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());
        Assert.Equal("detailed", reasoning.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ReasoningSummaryPartsRemainSeparateParagraphs()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesMultipartReasoningStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-reasoning-parts"));

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.High,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "**Checked the constraints.**\n\n**Resolved the ambiguity.**",
            session.Snapshot().Conversation[^1].ReasoningSummary);
    }

    [Fact]
    public async Task FinalizedReasoningItemBackfillsAVisibleSummaryWhenNoDeltaArrives()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(
            vault,
            model: "gpt-5.6-terra");
        using var handler = new CapturingHandler(
            ResponsesFinalOnlyReasoningSummaryStream());
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(
            new AgentRunId("responses-final-reasoning-summary"));

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.High,
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Snapshot().Conversation[^1];
        Assert.Equal("A concise answer.", assistant.Content);
        Assert.Equal("Checked the constraints.", assistant.ReasoningSummary);
        Assert.Equal(2, assistant.Usage!.ReasoningTokens);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        Assert.Equal(
            "high",
            body.RootElement
                .GetProperty("reasoning")
                .GetProperty("effort")
                .GetString());
        Assert.Equal(
            "detailed",
            body.RootElement
                .GetProperty("reasoning")
                .GetProperty("summary")
                .GetString());
    }

    [Fact]
    public async Task CodexSparkOmitsUnsupportedReasoningSummary()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(
            vault,
            model: "gpt-5.3-codex-spark");
        using var handler = new CapturingHandler(ResponsesTextStream("done"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-spark-run"));

        var result = await session.RunTurnAsync(
            "Respond.",
            [],
            factory.Create(profile),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task Gpt56SerializesExtraHighReasoningAndPriorityServiceTier()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(
            vault,
            model: "gpt-5.6-terra");
        using var handler = new CapturingHandler(ResponsesTextStream("done"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-gpt56-tier-run"));

        var result = await session.RunTurnAsync(
            "Solve it.",
            [],
            AgentReasoningEffort.ExtraHigh,
            factory.Create(
                profile,
                serviceTier: AgentServiceTier.Priority),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body);
        Assert.Equal(
            "xhigh",
            body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(
            "priority",
            body.RootElement.GetProperty("service_tier").GetString());
    }

    [Fact]
    public async Task ResponsesSerializesBoundedImageAsDataUrl()
    {
        using var vault = new InMemorySecretVault();
        var profile = await CreateProfileAsync(vault);
        using var handler = new CapturingHandler(ResponsesTextStream("seen"));
        using var factory = new AiProviderFactory(vault, handler);
        var session = new NativeAgentSession(new AgentRunId("responses-image-run"));
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
                body.RootElement.GetProperty("input").EnumerateArray())
            .GetProperty("content")
            .EnumerateArray()
            .ToArray();
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        Assert.Equal("input_image", content[1].GetProperty("type").GetString());
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(image.Content)}",
            content[1].GetProperty("image_url").GetString());
    }

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

    private static ValueTask<AgentTurnResult> SubmitResultAsync(
        NativeAgentSession session,
        AgentToolProposal proposal,
        ImmutableArray<AgentToolDefinition> tools,
        IAgentProvider provider) =>
        session.SubmitToolResultsAsync(
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

    private static async Task<AiProviderProfile> CreateProfileAsync(
        InMemorySecretVault vault,
        AiProviderKind identity = AiProviderKind.OpenAi,
        string model = Model)
    {
        var suffix = identity.ToString().ToLowerInvariant();
        var profileId = new AiProviderProfileId($"{suffix}-profile");
        var reference = new SecretRef($"{suffix}-secret");
        await StoreApiKeyAsync(vault, profileId, reference, ApiKey);
        return new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            identity.ToString(),
            identity,
            AiProviderProfile.DefaultEndpoint(identity),
            new AiProviderAuthentication.ApiKey(reference),
            model,
            order: 0);
    }

    private static async Task StoreApiKeyAsync(
        InMemorySecretVault vault,
        AiProviderProfileId profileId,
        SecretRef reference,
        string value)
    {
        var purpose = new SecretUsePurpose(
            SecretUseKind.AiProviderAuthentication,
            profileId.Value);
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(value));
        var created = await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "AI provider API key",
                SecretKind.ApiKey,
                new SecretScope(SecretScopeKind.AiProvider, profileId.Value),
                purpose),
            material,
            CancellationToken.None);
        Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(created);
    }

    private static async Task<AiProviderProfile> CreateGitHubProfileAsync(
        InMemorySecretVault vault)
    {
        var profileId = new AiProviderProfileId("github-copilot-replay-profile");
        var reference = new SecretRef("github-copilot-replay-session");
        await new AiProviderOAuthVault(vault).StoreAsync(
            profileId,
            reference,
            new AiProviderOAuthSession(
                AiProviderOAuthSession.CurrentSchemaVersion,
                "github-copilot",
                "copilot-access-token",
                refreshToken: null,
                expiresAt: DateTimeOffset.MaxValue),
            CancellationToken.None);
        return new AiProviderProfile(
            profileId,
            AiProviderProfile.CurrentSchemaVersion,
            "GitHub Copilot",
            AiProviderKind.GitHubCopilot,
            AiProviderProfile.DefaultEndpoint(AiProviderKind.GitHubCopilot),
            new AiProviderAuthentication.OAuth(reference, AiProviderOAuthFlow.Device),
            "gpt-5.3-codex",
            order: 0);
    }

    private static string ChatCompletionTextStream(string value) =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":"
        + JsonSerializer.Serialize(value)
        + "},\"finish_reason\":null}]}\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static string ResponsesTextStream(string value)
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-1\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg-1\",\"delta\":"
            + JsonSerializer.Serialize(value)
            + "}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"status\":\"completed\"}}");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    private static string ResponsesTruncatedStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-truncated\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg-1\",\"delta\":\"partial\"}");
        return builder.ToString();
    }

    private static string ResponsesIncompleteStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-incomplete\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.incomplete",
            "{\"type\":\"response.incomplete\",\"response\":{\"id\":\"resp-incomplete\",\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}");
        return builder.ToString();
    }

    private static string ResponsesMetadataStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-meta\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"reason-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.reasoning_summary_text.delta",
            "{\"type\":\"response.reasoning_summary_text.delta\",\"output_index\":0,\"item_id\":\"reason-1\",\"delta\":\"Checked the constraints.\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"reason-1\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Checked the constraints.\"}],\"encrypted_content\":\"encrypted-reasoning\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}");
        AppendEvent(
            builder,
            "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"output_index\":1,\"item_id\":\"msg-1\",\"delta\":\"A concise answer.\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"A concise answer.\",\"annotations\":[]}]}}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-meta\",\"status\":\"completed\","
            + "\"usage\":{\"input_tokens\":12,\"output_tokens\":5,"
            + "\"input_tokens_details\":{\"cached_tokens\":3},"
            + "\"output_tokens_details\":{\"reasoning_tokens\":2}}}}");
        return builder.ToString();
    }

    private static string ResponsesMultipartReasoningStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-parts\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"reason-parts\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.reasoning_summary_part.added",
            "{\"type\":\"response.reasoning_summary_part.added\",\"output_index\":0,\"item_id\":\"reason-parts\",\"summary_index\":0,\"part\":{\"type\":\"summary_text\",\"text\":\"\"}}");
        AppendEvent(
            builder,
            "response.reasoning_summary_text.delta",
            "{\"type\":\"response.reasoning_summary_text.delta\",\"output_index\":0,\"item_id\":\"reason-parts\",\"summary_index\":0,\"delta\":\"**Checked the constraints.**\"}");
        AppendEvent(
            builder,
            "response.reasoning_summary_part.done",
            "{\"type\":\"response.reasoning_summary_part.done\",\"output_index\":0,\"item_id\":\"reason-parts\",\"summary_index\":0,\"part\":{\"type\":\"summary_text\",\"text\":\"**Checked the constraints.**\"}}");
        AppendEvent(
            builder,
            "response.reasoning_summary_part.added",
            "{\"type\":\"response.reasoning_summary_part.added\",\"output_index\":0,\"item_id\":\"reason-parts\",\"summary_index\":1,\"part\":{\"type\":\"summary_text\",\"text\":\"\"}}");
        AppendEvent(
            builder,
            "response.reasoning_summary_text.delta",
            "{\"type\":\"response.reasoning_summary_text.delta\",\"output_index\":0,\"item_id\":\"reason-parts\",\"summary_index\":1,\"delta\":\"**Resolved the ambiguity.**\"}");
        AppendEvent(
            builder,
            "response.reasoning_summary_part.done",
            "{\"type\":\"response.reasoning_summary_part.done\",\"output_index\":0,\"item_id\":\"reason-parts\",\"summary_index\":1,\"part\":{\"type\":\"summary_text\",\"text\":\"**Resolved the ambiguity.**\"}}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"reason-parts\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"**Checked the constraints.**\"},{\"type\":\"summary_text\",\"text\":\"**Resolved the ambiguity.**\"}],\"encrypted_content\":\"encrypted-reasoning\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"id\":\"msg-parts\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}");
        AppendEvent(
            builder,
            "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"output_index\":1,\"item_id\":\"msg-parts\",\"delta\":\"Done.\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"id\":\"msg-parts\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"Done.\",\"annotations\":[]}]}}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-parts\",\"status\":\"completed\",\"usage\":{\"input_tokens\":4,\"output_tokens\":6,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens_details\":{\"reasoning_tokens\":4}}}}");
        return builder.ToString();
    }

    private static string ResponsesFinalOnlyReasoningSummaryStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-final-meta\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"reason-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"reason-1\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Checked the constraints.\"}],\"encrypted_content\":\"encrypted-reasoning\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}");
        AppendEvent(
            builder,
            "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"output_index\":1,\"item_id\":\"msg-1\",\"delta\":\"A concise answer.\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"A concise answer.\",\"annotations\":[]}]}}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-final-meta\",\"status\":\"completed\","
            + "\"usage\":{\"input_tokens\":12,\"output_tokens\":5,"
            + "\"input_tokens_details\":{\"cached_tokens\":3},"
            + "\"output_tokens_details\":{\"reasoning_tokens\":2}}}}");
        return builder.ToString();
    }

    private static string ResponsesToolStream(
        string callId,
        string itemId,
        string name,
        string arguments)
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-tools\",\"status\":\"in_progress\"}}");
        AppendFunctionCall(builder, 0, callId, itemId, name, arguments);
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-tools\",\"status\":\"completed\"}}");
        return builder.ToString();
    }

    private static string ResponsesToolBatchStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-batch\",\"status\":\"in_progress\"}}");
        AppendFunctionCall(builder, 0, "call-1", "fc-1", "read_file", "{\"path\":\"/tmp/a\"}");
        AppendFunctionCall(builder, 1, "call-2", "fc-2", "read_file", "{\"path\":\"/tmp/b\"}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-batch\",\"status\":\"completed\"}}");
        return builder.ToString();
    }

    private static string ResponsesReasoningToolStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-reasoning\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.reasoning_summary_text.delta",
            "{\"type\":\"response.reasoning_summary_text.delta\",\"output_index\":0,\"item_id\":\"rs-1\",\"delta\":\"Checked.\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Checked.\"}]}}");
        AppendFunctionCall(builder, 1, "call-1", "fc-1", "read_file", "{}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-reasoning\",\"status\":\"completed\",\"output\":[{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Checked.\"}],\"encrypted_content\":\"encrypted-reasoning\"}]}}");
        return builder.ToString();
    }

    private static string ResponsesReasoningToolStreamWithDoneCiphertext()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-reasoning\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"done-ciphertext\"}}");
        AppendFunctionCall(builder, 1, "call-1", "fc-1", "read_file", "{}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-reasoning\",\"status\":\"completed\",\"output\":[{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[]}]}}");
        return builder.ToString();
    }

    private static string ResponsesReasoningToolStreamWithoutCiphertext()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-reasoning\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendFunctionCall(builder, 1, "call-1", "fc-1", "read_file", "{}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-reasoning\",\"status\":\"completed\"}}");
        return builder.ToString();
    }

    private static string ResponsesRawReasoningToolStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-raw\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs-raw\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.reasoning_text.delta",
            "{\"type\":\"response.reasoning_text.delta\",\"output_index\":0,\"item_id\":\"rs-raw\",\"delta\":\"private thought\"}");
        AppendEvent(
            builder,
            "response.reasoning_text.done",
            "{\"type\":\"response.reasoning_text.done\",\"output_index\":0,\"item_id\":\"rs-raw\",\"text\":\"private thought\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs-raw\",\"type\":\"reasoning\",\"summary\":[],\"content\":[{\"type\":\"reasoning_text\",\"text\":\"private thought\"}],\"encrypted_content\":\"opaque-raw\"}}");
        AppendFunctionCall(builder, 1, "call-1", "fc-1", "read_file", "{}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-raw\",\"status\":\"completed\"}}");
        return builder.ToString();
    }

    private static string ResponsesDivergentMessageStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-message\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}");
        AppendEvent(
            builder,
            "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"item_id\":\"msg-1\",\"delta\":\"visible\"}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"hidden\",\"annotations\":[]}]}}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-message\",\"status\":\"completed\"}}");
        return builder.ToString();
    }

    private static string ResponsesGappedToolStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-gap\",\"status\":\"in_progress\"}}");
        AppendFunctionCall(builder, 1, "call-1", "fc-1", "read_file", "{}");
        return builder.ToString();
    }

    private static string ResponsesReorderedTerminalStream()
    {
        var builder = new StringBuilder();
        AppendEvent(
            builder,
            "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp-order\",\"status\":\"in_progress\"}}");
        AppendEvent(
            builder,
            "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[]}}");
        AppendEvent(
            builder,
            "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"opaque\"}}");
        AppendFunctionCall(builder, 1, "call-1", "fc-1", "read_file", "{}");
        AppendEvent(
            builder,
            "response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-order\",\"status\":\"completed\",\"output\":[{\"id\":\"fc-1\",\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"read_file\",\"arguments\":\"{}\"},{\"id\":\"rs-1\",\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"opaque\"}]}}");
        return builder.ToString();
    }

    private static void AppendFunctionCall(
        StringBuilder builder,
        int outputIndex,
        string callId,
        string itemId,
        string name,
        string arguments)
    {
        var added = JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            output_index = outputIndex,
            item = new
            {
                id = itemId,
                type = "function_call",
                call_id = callId,
                name,
                arguments = string.Empty,
            },
        });
        AppendEvent(builder, "response.output_item.added", added);
        var midpoint = arguments.Length / 2;
        foreach (var fragment in new[] { arguments[..midpoint], arguments[midpoint..] })
        {
            var delta = JsonSerializer.Serialize(new
            {
                type = "response.function_call_arguments.delta",
                item_id = itemId,
                output_index = outputIndex,
                delta = fragment,
            });
            AppendEvent(builder, "response.function_call_arguments.delta", delta);
        }

        var done = JsonSerializer.Serialize(new
        {
            type = "response.function_call_arguments.done",
            item_id = itemId,
            output_index = outputIndex,
            arguments,
        });
        AppendEvent(builder, "response.function_call_arguments.done", done);
        var itemDone = JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            output_index = outputIndex,
            item = new
            {
                id = itemId,
                type = "function_call",
                call_id = callId,
                name,
                arguments,
            },
        });
        AppendEvent(builder, "response.output_item.done", itemDone);
    }

    private static void AppendEvent(
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

    private sealed class CapturingHandler(params string[] streams) : HttpMessageHandler
    {
        private int _index;

        public CapturedRequest? LastRequest { get; private set; }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = await CapturedRequest.CreateAsync(request, cancellationToken);
            LastRequest = captured;
            Requests.Add(captured);
            var index = Interlocked.Increment(ref _index) - 1;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    streams[index],
                    Encoding.UTF8,
                    "text/event-stream"),
                RequestMessage = request,
            };
            return response;
        }
    }

    private sealed class MissingContentTypeHandler(string stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(stream)),
                RequestMessage = request,
            });
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? Authorization,
        string? Originator,
        string? OpenAiBeta,
        string? AccountId,
        string Body)
    {
        public static async Task<CapturedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            new(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                Header(request, "originator"),
                Header(request, "OpenAI-Beta"),
                Header(request, "ChatGPT-Account-Id"),
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values)
                ? Assert.Single(values)
                : null;
    }
}
