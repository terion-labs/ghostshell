using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Core;

namespace Asura.Agent.Tests;

public sealed partial class NativeAgentSessionTests
{
    [Fact]
    public async Task CheckpointRoundTripPreservesSafeProviderReplayArtifacts()
    {
        var session = CreateSession();
        var openAi = ReplayState(
            "openai-profile",
            AiProviderKind.OpenAi,
            AiProviderProtocol.OpenAiResponses,
            "gpt-test",
            AgentProviderReplayFormat.OpenAiResponseItems,
            [
                new AgentProviderReplayItem(
                    0,
                    AgentProviderReplayItemKind.OpenAiReasoning,
                    "{\"type\":\"reasoning\",\"id\":\"rs-1\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Safe summary.\"}],\"encrypted_content\":\"opaque-ciphertext\"}"),
                new AgentProviderReplayItem(
                    1,
                    AgentProviderReplayItemKind.OpenAiMessage,
                    "{\"type\":\"message\",\"id\":\"msg-1\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"Answer one.\"}]}"),
            ]);
        var anthropic = ReplayState(
            "anthropic-profile",
            AiProviderKind.Anthropic,
            AiProviderProtocol.AnthropicMessages,
            "claude-test",
            AgentProviderReplayFormat.AnthropicContentBlocks,
            [
                new AgentProviderReplayItem(
                    0,
                    AgentProviderReplayItemKind.AnthropicSummarizedThinking,
                    "{\"type\":\"thinking\",\"thinking\":\"Safe summary.\",\"signature\":\"opaque-signature\"}"),
                new AgentProviderReplayItem(
                    1,
                    AgentProviderReplayItemKind.AnthropicText,
                    "{\"type\":\"text\",\"text\":\"Answer two.\"}"),
            ]);

        Assert.True((await session.RunTurnAsync(
            "First",
            [],
            ReplayProvider("Answer one.", openAi, "Safe summary."),
            CancellationToken.None)).Succeeded);
        Assert.True((await session.RunTurnAsync(
            "Second",
            [],
            ReplayProvider("Answer two.", anthropic, "Safe summary."),
            CancellationToken.None)).Succeeded);

        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(checkpoint).Session);
        var replayStates = restored.Snapshot().Conversation
            .Where(message => message.ProviderReplayState is not null)
            .Select(message => message.ProviderReplayState!)
            .ToArray();

        Assert.Collection(
            replayStates,
            state =>
            {
                Assert.Equal(AgentProviderReplayFormat.OpenAiResponseItems, state.Format);
                Assert.Contains("opaque-ciphertext", state.Items[0].PayloadJson);
            },
            state =>
            {
                Assert.Equal(AgentProviderReplayFormat.AnthropicContentBlocks, state.Format);
                Assert.Contains("opaque-signature", state.Items[0].PayloadJson);
                Assert.Contains("Safe summary.", state.Items[0].PayloadJson);
            });
        Assert.DoesNotContain("opaque-ciphertext", checkpoint.PayloadJson);
        Assert.DoesNotContain("opaque-signature", checkpoint.PayloadJson);
    }

    [Fact]
    public async Task CheckpointDropsSuppressedReasoningButRejectsItOnRestore()
    {
        var safeSession = CreateSession();
        var safeState = ReplayState(
            "anthropic-profile",
            AiProviderKind.Anthropic,
            AiProviderProtocol.AnthropicMessages,
            "claude-test",
            AgentProviderReplayFormat.AnthropicContentBlocks,
            [
                new AgentProviderReplayItem(
                    0,
                    AgentProviderReplayItemKind.AnthropicSummarizedThinking,
                    "{\"type\":\"thinking\",\"thinking\":\"Safe summary.\",\"signature\":\"opaque-signature\"}"),
                new AgentProviderReplayItem(
                    1,
                    AgentProviderReplayItemKind.AnthropicText,
                    "{\"type\":\"text\",\"text\":\"Answer.\"}"),
            ]);
        Assert.True((await safeSession.RunTurnAsync(
            "Safe",
            [],
            ReplayProvider("Answer.", safeState, "Safe summary."),
            CancellationToken.None)).Succeeded);
        var safeCheckpoint = Assert.IsType<AgentSessionCheckpoint>(
            safeSession.CaptureCheckpoint().Checkpoint);
        var tamperedPayload = safeCheckpoint.PayloadJson.Replace(
            "\"containsSuppressedRawReasoning\":false",
            "\"containsSuppressedRawReasoning\":true",
            StringComparison.Ordinal);
        Assert.NotEqual(safeCheckpoint.PayloadJson, tamperedPayload, StringComparer.Ordinal);
        var tampered = new AgentSessionCheckpoint(
            safeCheckpoint.RunId,
            safeCheckpoint.SchemaVersion,
            safeCheckpoint.Generation,
            safeCheckpoint.Revision,
            tamperedPayload,
            safeCheckpoint.UpdatedAt);

        Assert.Equal(
            AgentCheckpointRestoreErrorCode.InvalidPayload,
            NativeAgentSession.RestoreCheckpoint(tampered).ErrorCode);

        var smuggledPayload = safeCheckpoint.PayloadJson.Replace(
            $"\"kind\":{(int)AgentProviderReplayItemKind.AnthropicSummarizedThinking}",
            $"\"kind\":{(int)AgentProviderReplayItemKind.AnthropicSuppressedThinking}",
            StringComparison.Ordinal);
        Assert.NotEqual(safeCheckpoint.PayloadJson, smuggledPayload, StringComparer.Ordinal);
        var smuggled = new AgentSessionCheckpoint(
            safeCheckpoint.RunId,
            safeCheckpoint.SchemaVersion,
            safeCheckpoint.Generation,
            safeCheckpoint.Revision,
            smuggledPayload,
            safeCheckpoint.UpdatedAt);
        Assert.Equal(
            AgentCheckpointRestoreErrorCode.InvalidPayload,
            NativeAgentSession.RestoreCheckpoint(smuggled).ErrorCode);

        var encodedReplayPayload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(safeState.Items[0].PayloadJson));
        var invalidUtf8Payload = safeCheckpoint.PayloadJson.Replace(
            encodedReplayPayload,
            "/w==",
            StringComparison.Ordinal);
        Assert.NotEqual(safeCheckpoint.PayloadJson, invalidUtf8Payload, StringComparer.Ordinal);
        var invalidUtf8 = new AgentSessionCheckpoint(
            safeCheckpoint.RunId,
            safeCheckpoint.SchemaVersion,
            safeCheckpoint.Generation,
            safeCheckpoint.Revision,
            invalidUtf8Payload,
            safeCheckpoint.UpdatedAt);
        Assert.Equal(
            AgentCheckpointRestoreErrorCode.InvalidPayload,
            NativeAgentSession.RestoreCheckpoint(invalidUtf8).ErrorCode);

        var encodedVisibleText = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(safeState.Items[1].PayloadJson));
        var divergentTextJson = safeState.Items[1].PayloadJson.Replace(
            "Answer.",
            "Hidden alternate transcript.",
            StringComparison.Ordinal);
        var divergentPayload = safeCheckpoint.PayloadJson.Replace(
            encodedVisibleText,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(divergentTextJson)),
            StringComparison.Ordinal);
        Assert.NotEqual(safeCheckpoint.PayloadJson, divergentPayload, StringComparer.Ordinal);
        var divergent = new AgentSessionCheckpoint(
            safeCheckpoint.RunId,
            safeCheckpoint.SchemaVersion,
            safeCheckpoint.Generation,
            safeCheckpoint.Revision,
            divergentPayload,
            safeCheckpoint.UpdatedAt);
        Assert.Equal(
            AgentCheckpointRestoreErrorCode.InvalidPayload,
            NativeAgentSession.RestoreCheckpoint(divergent).ErrorCode);

        var unsafeSession = CreateSession();
        var unsafeState = new AgentProviderReplayState(
            safeState.Binding,
            safeState.Format,
            [
                new AgentProviderReplayItem(
                    0,
                    AgentProviderReplayItemKind.AnthropicSuppressedThinking,
                    safeState.Items[0].PayloadJson),
                safeState.Items[1],
            ]);
        Assert.True((await unsafeSession.RunTurnAsync(
            "Unsafe",
            [],
            ReplayProvider("Answer.", unsafeState),
            CancellationToken.None)).Succeeded);

        var capture = unsafeSession.CaptureCheckpoint();

        Assert.True(capture.Succeeded);
        var restoredUnsafe = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(capture.Checkpoint!).Session);
        var restoredAssistant = restoredUnsafe.Snapshot().Conversation[^1];
        Assert.Null(restoredAssistant.ProviderReplayState);
        var restoredDescriptor = restoredUnsafe.DescribeConversation();
        Assert.Equal(safeState.Binding.ProfileId, restoredDescriptor.ProviderId);
        Assert.Equal(safeState.Binding.Model, restoredDescriptor.Model);
        Assert.DoesNotContain(
            "opaque-signature",
            capture.Checkpoint!.PayloadJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleCheckpointRoundTripPreservesCommittedStructuredTranscript()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var proposalTurn = await session.RunTurnAsync(
            "Inspect the terminal",
            tools,
            AgentReasoningEffort.High,
            new SequenceProvider(
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ReasoningSummaryDelta("Prepared a bounded read."),
                new AgentProviderEvent.ToolCallStarted(
                    0,
                    "provider-call-1",
                    ProviderName("terminal.read_screen")),
                new AgentProviderEvent.ToolCallArgumentsDelta(
                    0,
                    "{\"panelId\":\"panel-1\"}"),
                new AgentProviderEvent.ToolCallCompleted(0),
                new AgentProviderEvent.Usage(new AgentTokenUsage(20, 4, 5, 2)),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse)),
            CancellationToken.None);
        var proposal = Assert.Single(proposalTurn.ToolProposals);
        var completed = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{\"text\":\"ready\"}")],
            tools,
            new SequenceProvider(
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ReasoningSummaryDelta("The terminal is ready."),
                new AgentProviderEvent.TextDelta("Ready."),
                new AgentProviderEvent.Usage(new AgentTokenUsage(30, 6, 10, 1)),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn)),
            CancellationToken.None);
        Assert.True(completed.Succeeded);

        var capture = session.CaptureCheckpoint();
        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(capture.Checkpoint);
        var restored = NativeAgentSession.RestoreCheckpoint(checkpoint);
        var restoredSession = Assert.IsType<NativeAgentSession>(restored.Session);

        Assert.True(capture.Succeeded);
        Assert.True(restored.Succeeded);
        Assert.Equal(session.RunId, restoredSession.RunId);
        var before = session.Snapshot();
        var after = restoredSession.Snapshot();
        Assert.Equal(NativeAgentSessionState.Ready, after.State);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.LastSequence, after.LastSequence);
        Assert.Equal(before.Conversation.Count(), after.Conversation.Count());
        Assert.Equal(before.Transcript.Count(), after.Transcript.Count());
        Assert.Equal(
            before.Conversation.Select(message => message.Role),
            after.Conversation.Select(message => message.Role));
        Assert.Equal(
            before.Conversation.Select(message => message.Content),
            after.Conversation.Select(message => message.Content), StringComparer.Ordinal);
        Assert.Equal(
            before.Transcript.Select(message => message.Content),
            after.Transcript.Select(message => message.Content), StringComparer.Ordinal);
        var restoredProposal = Assert.Single(after.Conversation[1].ToolCalls);
        Assert.Equal(proposal.Id, restoredProposal.Id);
        Assert.Equal(proposal.ProviderName, restoredProposal.ProviderName);
        Assert.Equal("panel-1", restoredProposal.Arguments
            .GetProperty("panelId")
            .GetString());
        Assert.Equal(
            AgentToolResultValueKind.Json,
            Assert.IsType<AgentToolResult>(after.Conversation[2].ToolResult)
                .Value.Kind);
        Assert.Equal(
            "Prepared a bounded read.",
            after.Conversation[1].ReasoningSummary);
        Assert.Equal(20, after.Conversation[1].Usage?.InputTokens);
        Assert.Equal(2, after.Conversation[1].Usage?.ReasoningTokens);
        Assert.Equal(
            AgentReasoningEffort.High,
            after.Conversation[1].RequestedReasoningEffort);
        Assert.Equal("The terminal is ready.", after.Conversation[3].ReasoningSummary);
        Assert.Equal(10, after.Conversation[3].Usage?.CachedInputTokens);
        Assert.Equal(
            AgentReasoningEffort.High,
            after.Conversation[3].RequestedReasoningEffort);

        var continued = await restoredSession.RunTurnAsync(
            "Inspect again",
            tools,
            TextProvider("Still ready."),
            CancellationToken.None);
        Assert.True(continued.Succeeded);
        Assert.Equal(before.Generation + 1, restoredSession.Snapshot().Generation);
    }

    [Fact]
    public async Task CheckpointPreservesFullTranscriptBesideCompactedProviderContext()
    {
        var initial = ConversationFixture();
        var session = CreateSession(initial);
        Assert.True((await session.CompactAsync(
            1,
            new ImmediateCompactor(
                new AgentMessage(AgentMessageRole.Summary, "summary")),
            CancellationToken.None)).Succeeded);

        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(checkpoint).Session);

        Assert.Equal(
            ["system", "summary", "current user", "current assistant"],
            restored.Snapshot().Conversation.Select(message => message.Content), StringComparer.Ordinal);
        Assert.Equal(
            initial.Select(message => message.Content),
            restored.Snapshot().Transcript.Select(message => message.Content), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GeneratedConversationTitleRoundTripsAndLegacyCheckpointUsesFallback()
    {
        var session = CreateSession();
        Assert.True((await session.RunTurnAsync(
            "Explain the Roman aqueduct system",
            [],
            TextProvider("Aqueducts moved water by gravity."),
            CancellationToken.None)).Succeeded);
        Assert.True(session.TrySetConversationTitle("Roman Aqueduct Engineering"));

        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(checkpoint).Session);

        Assert.Equal("Roman Aqueduct Engineering", restored.DescribeConversation().Title);
        Assert.True(restored.HasGeneratedTitle);

        var legacyPayload = checkpoint.PayloadJson.Replace(
            "\"title\":\"Roman Aqueduct Engineering\",",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(checkpoint.PayloadJson, legacyPayload, StringComparer.Ordinal);
        var legacy = new AgentSessionCheckpoint(
            checkpoint.RunId,
            1,
            checkpoint.Generation,
            checkpoint.Revision,
            legacyPayload,
            checkpoint.UpdatedAt);
        var legacySession = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(legacy).Session);

        Assert.False(legacySession.HasGeneratedTitle);
        Assert.Equal(
            "Explain the Roman aqueduct system",
            legacySession.DescribeConversation().Title);
    }

    [Fact]
    public async Task RestoredSystemPromptRebasePreservesAndAdvancesDurableRevision()
    {
        var session = CreateSession(
        [
            new AgentMessage(AgentMessageRole.System, "Original trusted context."),
        ]);
        Assert.True((await session.RunTurnAsync(
            "Inspect the workspace.",
            [],
            TextProvider("The workspace is ready."),
            CancellationToken.None)).Succeeded);
        var before = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(before).Session);

        Assert.True(restored.TryRebaseSystemPrompt("Refreshed trusted context."));
        var after = Assert.IsType<AgentSessionCheckpoint>(
            restored.CaptureCheckpoint().Checkpoint);

        Assert.Equal(before.RunId, after.RunId);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(before.Generation, after.Generation);
        var conversation = restored.Snapshot().Conversation;
        Assert.Equal("Refreshed trusted context.", conversation[0].Content);
        Assert.Equal("Inspect the workspace.", conversation[1].Content);
        Assert.Equal("The workspace is ready.", conversation[2].Content);
    }

    [Fact]
    public async Task CheckpointRoundTripCopiesUserImagesAndRevalidatesSignatures()
    {
        var imageBytes = new byte[]
        {
            0x89,
            0x50,
            0x4e,
            0x47,
            0x0d,
            0x0a,
            0x1a,
            0x0a,
        };
        var session = CreateSession();
        Assert.True((await session.RunTurnAsync(
            "Inspect this image",
            [new AgentImageAttachment("screen.png", "image/png", imageBytes)],
            [],
            AgentReasoningEffort.Medium,
            TextProvider("It is a PNG."),
            CancellationToken.None)).Succeeded);
        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);

        var restored = NativeAgentSession.RestoreCheckpoint(checkpoint);
        var restoredImage = Assert.Single(
            Assert.IsType<NativeAgentSession>(restored.Session)
                .Snapshot()
                .Conversation[0]
                .Images);

        Assert.True(restored.Succeeded);
        Assert.Equal("screen.png", restoredImage.FileName);
        Assert.Equal("image/png", restoredImage.MediaType);
        Assert.Equal(imageBytes, restoredImage.Content.ToArray());
        imageBytes[0] = 0;
        Assert.Equal(0x89, restoredImage.Content[0]);

        var corrupt = new AgentSessionCheckpoint(
            checkpoint.RunId,
            checkpoint.SchemaVersion,
            checkpoint.Generation,
            checkpoint.Revision,
            checkpoint.PayloadJson.Replace(
                "iVBORw0KGgo=",
                "AAAAAAAAAAA=",
                StringComparison.Ordinal),
            checkpoint.UpdatedAt);
        Assert.Equal(
            AgentCheckpointRestoreErrorCode.InvalidPayload,
            NativeAgentSession.RestoreCheckpoint(corrupt).ErrorCode);
    }

    [Fact]
    public async Task CheckpointCaptureRejectsStreamingAndPendingApprovalState()
    {
        var streamingSession = CreateSession();
        var provider = new BlockingEntrypointProvider();
        using var cancellation = new CancellationTokenSource();
        var turn = streamingSession.RunTurnAsync(
            "Wait",
            [],
            provider,
            cancellation.Token).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var streamingCapture = streamingSession.CaptureCheckpoint();

        Assert.False(streamingCapture.Succeeded);
        Assert.Equal(
            AgentCheckpointCaptureErrorCode.SessionNotIdle,
            streamingCapture.ErrorCode);
        cancellation.Cancel();
        provider.Release.TrySetResult();
        await turn.WaitAsync(TimeSpan.FromSeconds(1));

        var pendingSession = CreateSession();
        Assert.True((await pendingSession.RunTurnAsync(
            "Inspect",
            [Tool("terminal.read_screen")],
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None)).Succeeded);

        var pendingCapture = pendingSession.CaptureCheckpoint();

        Assert.False(pendingCapture.Succeeded);
        Assert.Equal(
            AgentCheckpointCaptureErrorCode.SessionNotIdle,
            pendingCapture.ErrorCode);
    }

    [Fact]
    public void InterruptedCheckpointPreservesAcceptedUserMessageWithoutResumingWork()
    {
        var session = CreateSession();

        var captured = session.CaptureInterruptedCheckpoint(
            "unfinished request",
            []);
        var restored = NativeAgentSession.RestoreCheckpoint(
            Assert.IsType<AgentSessionCheckpoint>(captured.Checkpoint));

        Assert.True(restored.Succeeded);
        var snapshot = Assert.IsType<NativeAgentSession>(restored.Session).Snapshot();
        Assert.Equal(NativeAgentSessionState.Ready, snapshot.State);
        Assert.Collection(
            snapshot.Conversation,
            message => Assert.Equal("unfinished request", message.Content),
            message => Assert.Contains("was interrupted", message.Content));
        Assert.Empty(snapshot.PendingToolProposals);
    }

    [Fact]
    public async Task InterruptedCheckpointRetainsCompletedToolBatchButNoPendingAction()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var turn = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(turn.ToolProposals);
        var result = SuccessJson(proposal, "{\"text\":\"ready\"}");

        var captured = session.CaptureInterruptedCheckpoint([result]);
        var restored = NativeAgentSession.RestoreCheckpoint(
            Assert.IsType<AgentSessionCheckpoint>(captured.Checkpoint));

        Assert.True(restored.Succeeded);
        var snapshot = Assert.IsType<NativeAgentSession>(restored.Session).Snapshot();
        Assert.Equal(NativeAgentSessionState.Ready, snapshot.State);
        Assert.Equal(
            [
                AgentMessageRole.User,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
                AgentMessageRole.Assistant,
            ],
            snapshot.Conversation.Select(message => message.Role));
        Assert.Equal("{\"text\":\"ready\"}", snapshot.Conversation[2].Content);
        Assert.Empty(snapshot.PendingToolProposals);
    }

    [Fact]
    public async Task CheckpointRestoreAcceptsEquivalentJsonEscapingInToolContent()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var turn = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(turn.ToolProposals);
        var result = SuccessJson(
            proposal,
            "{\"window_title\":\"\\u2026/project\",\"ready\":true}");
        var continued = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            TextProvider("Done."),
            CancellationToken.None);
        Assert.True(continued.Succeeded);

        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var restored = NativeAgentSession.RestoreCheckpoint(checkpoint);

        Assert.True(restored.Succeeded);
        var toolMessage = Assert.Single(
            Assert.IsType<NativeAgentSession>(restored.Session)
                .Snapshot()
                .Conversation,
            message => message.Role == AgentMessageRole.Tool);
        using var json = JsonDocument.Parse(toolMessage.Content);
        Assert.Equal("…/project", json.RootElement
            .GetProperty("window_title")
            .GetString());
    }

    [Fact]
    public async Task CheckpointCaptureRejectsLikelyLiteralSecretMaterial()
    {
        var session = CreateSession();
        Assert.True((await session.RunTurnAsync(
            "Use api_key=literal-secret-value",
            [],
            TextProvider("I will not retain it."),
            CancellationToken.None)).Succeeded);

        var capture = session.CaptureCheckpoint();

        Assert.False(capture.Succeeded);
        Assert.Equal(AgentCheckpointCaptureErrorCode.UnsafeContent, capture.ErrorCode);
    }

    [Fact]
    public async Task CheckpointRedactsUnsafeToolOutputAndPreservesLaterMessages()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("web.read"));
        var turn = await session.RunTurnAsync(
            "Read the authentication guide",
            tools,
            ToolProvider("web.read", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(turn.ToolProposals);
        var result = SuccessJson(
            proposal,
            "{\"content\":\"password: values.password\",\"ok\":true}");
        var completed = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            TextProvider("The guide uses password sign-in."),
            CancellationToken.None);
        Assert.True(completed.Succeeded);

        var capture = session.CaptureCheckpoint();
        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(capture.Checkpoint);
        var restored = Assert.IsType<NativeAgentSession>(
            NativeAgentSession.RestoreCheckpoint(checkpoint).Session);
        var transcript = restored.Snapshot().Transcript;
        var toolMessage = Assert.Single(
            transcript,
            message => message.Role is AgentMessageRole.Tool);

        Assert.True(capture.Succeeded);
        Assert.DoesNotContain(
            "values.password",
            checkpoint.PayloadJson,
            StringComparison.Ordinal);
        using var redacted = JsonDocument.Parse(toolMessage.Content);
        Assert.True(redacted.RootElement.GetProperty("redacted").GetBoolean());
        Assert.Equal(
            "The guide uses password sign-in.",
            transcript[^1].Content);
    }

    [Fact]
    public void RestoreRejectsUnknownFieldsUnsupportedSchemaAndTighterBounds()
    {
        var session = CreateSession(
        [
            new AgentMessage(AgentMessageRole.User, "hello"),
            new AgentMessage(AgentMessageRole.Assistant, "world"),
        ]);
        var checkpoint = Assert.IsType<AgentSessionCheckpoint>(
            session.CaptureCheckpoint().Checkpoint);
        var unknownField = new AgentSessionCheckpoint(
            checkpoint.RunId,
            checkpoint.SchemaVersion,
            checkpoint.Generation,
            checkpoint.Revision,
            checkpoint.PayloadJson[..^1] + ",\"future\":true}",
            checkpoint.UpdatedAt);
        var futureSchema = new AgentSessionCheckpoint(
            checkpoint.RunId,
            checkpoint.SchemaVersion + 1,
            checkpoint.Generation,
            checkpoint.Revision,
            checkpoint.PayloadJson,
            checkpoint.UpdatedAt);
        var tightLimits = new AgentKernelLimits(
            maximumProviderTextFragmentBytes: 1,
            maximumAssistantTextBytes: 1,
            maximumConversationBytes: 2);

        Assert.Equal(
            AgentCheckpointRestoreErrorCode.InvalidPayload,
            NativeAgentSession.RestoreCheckpoint(unknownField).ErrorCode);
        Assert.Equal(
            AgentCheckpointRestoreErrorCode.UnsupportedSchema,
            NativeAgentSession.RestoreCheckpoint(futureSchema).ErrorCode);
        Assert.Equal(
            AgentCheckpointRestoreErrorCode.LimitExceeded,
            NativeAgentSession.RestoreCheckpoint(checkpoint, tightLimits).ErrorCode);
    }

    private static AgentProviderReplayState ReplayState(
        string profileId,
        AiProviderKind providerKind,
        AiProviderProtocol protocol,
        string model,
        AgentProviderReplayFormat format,
        ImmutableArray<AgentProviderReplayItem> items) =>
        new(
            new AgentProviderReplayBinding(
                new AiProviderProfileId(profileId),
                providerKind,
                protocol,
                model,
                new Uri("https://provider.example/v1/"),
                $"{protocol}:test"),
            format,
            items);

    private static SequenceProvider ReplayProvider(
        string text,
        AgentProviderReplayState state,
        string? reasoningSummary = null) =>
        reasoningSummary is null
            ? new SequenceProvider(
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.TextDelta(text),
                new AgentProviderEvent.ReplayStateFinalized(state),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn))
            : new SequenceProvider(
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ReasoningSummaryDelta(reasoningSummary),
                new AgentProviderEvent.TextDelta(text),
                new AgentProviderEvent.ReplayStateFinalized(state),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn));
}
