using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Asura.Agent;
using Asura.Core;

namespace Asura.Agent.Tests;

public sealed partial class NativeAgentSessionTests
{
    [Fact]
    public async Task ValidProviderTurnCommitsUserAndAssistantAtomically()
    {
        var session = CreateSession();
        var provider = TextProvider("hello");

        var result = await session.RunTurnAsync(
            "Say hello",
            [],
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.EndTurn, result.StopReason);
        Assert.Empty(result.ToolProposals);
        var snapshot = session.Snapshot();
        Assert.Equal(NativeAgentSessionState.Ready, snapshot.State);
        Assert.Collection(
            snapshot.Conversation,
            message =>
            {
                Assert.Equal(AgentMessageRole.User, message.Role);
                Assert.Equal("Say hello", message.Content);
            },
            message =>
            {
                Assert.Equal(AgentMessageRole.Assistant, message.Role);
                Assert.Equal("hello", message.Content);
            });

        var events = await ReadCurrentEventBatchAsync(session);
        Assert.Equal(
            [
                AgentRunEventKind.TurnStarted,
                AgentRunEventKind.ProvisionalText,
                AgentRunEventKind.TurnCommitted,
            ],
            events.Select(agentEvent => agentEvent.Kind));
        Assert.True(events[1].ContainsUntrustedContent);
    }

    [Fact]
    public async Task ProviderMetadataCommitsAtomicallyWithTheAssistantMessage()
    {
        var session = CreateSession();
        var usage = new AgentTokenUsage(
            inputTokens: 90,
            outputTokens: 25,
            cachedInputTokens: 40,
            reasoningTokens: 8);
        var provider = new SequenceProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.ReasoningSummaryDelta(
                "Inspected the bounded context."),
            new AgentProviderEvent.TextDelta("The service is healthy."),
            new AgentProviderEvent.Usage(usage),
            new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.EndTurn));

        var result = await session.RunTurnAsync(
            "Check the service",
            [],
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Snapshot().Conversation[^1];
        Assert.Equal(
            "Inspected the bounded context.",
            assistant.ReasoningSummary);
        Assert.Same(usage, assistant.Usage);
        Assert.Equal("The service is healthy.", assistant.Content);
        var events = await ReadCurrentEventBatchAsync(session);
        var reasoningEvent = Assert.Single(events, agentEvent =>
            agentEvent.Kind
                == AgentRunEventKind.ProvisionalReasoningSummary);
        Assert.Equal(
            "Inspected the bounded context.",
            reasoningEvent.ProvisionalReasoningSummary);
        Assert.True(reasoningEvent.ContainsUntrustedContent);
    }

    [Fact]
    public async Task RequestedReasoningEffortReachesTheProviderBoundary()
    {
        var session = CreateSession();
        var provider = TextProvider("done");

        var result = await session.RunTurnAsync(
            "Think carefully",
            [],
            AgentReasoningEffort.High,
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            AgentReasoningEffort.High,
            Assert.IsType<AgentProviderRequest>(provider.LastRequest).ReasoningEffort);
        Assert.Equal(
            AgentReasoningEffort.High,
            session.Snapshot().Conversation[^1].RequestedReasoningEffort);
    }

    [Fact]
    public async Task ImageOnlyTurnCopiesBoundedImageToTheProviderBoundary()
    {
        var session = CreateSession();
        var provider = TextProvider("described");
        var image = new AgentImageAttachment(
            "sample.png",
            "image/png",
            [
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
                0x01,
            ]);

        var result = await session.RunTurnAsync(
            string.Empty,
            [image],
            [],
            AgentReasoningEffort.Automatic,
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var user = Assert.IsType<AgentProviderRequest>(provider.LastRequest)
            .Messages[^1];
        Assert.Empty(user.Content);
        Assert.Same(image, Assert.Single(user.Images));
        Assert.Same(image, Assert.Single(session.Snapshot().Conversation[0].Images));
    }

    [Fact]
    public async Task PartialToolCallFollowedByProviderFailureCommitsNothing()
    {
        var session = CreateSession();
        var provider = new ThrowingProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.ToolCallStarted(
                0,
                "provider-1",
                ProviderName("terminal.send_text")),
            new AgentProviderEvent.ToolCallArgumentsDelta(0, "{\"text\":\"partial\"}"));

        var result = await session.RunTurnAsync(
            "Type something",
            [Tool("terminal.send_text")],
            provider,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        var snapshot = session.Snapshot();
        Assert.Empty(snapshot.Conversation);
        Assert.Empty(snapshot.PendingToolProposals);
        Assert.Equal(NativeAgentSessionState.Failed, snapshot.State);
    }

    [Fact]
    public async Task ProviderSafeFailureIsReturnedWithoutLeakingExceptionDetails()
    {
        var session = CreateSession();

        var result = await session.RunTurnAsync(
            "Respond",
            [],
            new SafeFailingProvider(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.ProviderFailure, result.ErrorCode);
        var failure = Assert.IsType<AgentProviderFailure>(result.ProviderFailure);
        Assert.Equal("ai_provider_model_unavailable", failure.StableCode);
        Assert.Equal("The configured AI model is unavailable.", failure.Message);
        Assert.DoesNotContain("private transport detail", failure.Message);
    }

    [Fact]
    public async Task LaterInvalidEventDiscardsAnOtherwiseCompleteResponse()
    {
        var session = CreateSession();
        var provider = new SequenceProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta("provisional"),
            new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn),
            new AgentProviderEvent.TextDelta("late"));

        var result = await session.RunTurnAsync(
            "Do work",
            [],
            provider,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.InvalidProviderStream, result.ErrorCode);
        Assert.Empty(session.Snapshot().Conversation);
    }

    [Fact]
    public async Task CompleteToolCallBecomesInertProposalAndBlocksAnotherTurn()
    {
        var session = CreateSession();
        var provider = ToolProvider("terminal.read_screen", "{\"panelId\":\"panel-1\"}");

        var result = await session.RunTurnAsync(
            "Inspect the terminal",
            [Tool("terminal.read_screen")],
            provider,
            CancellationToken.None);

        var proposal = Assert.Single(result.ToolProposals);
        Assert.Equal("terminal.read_screen", proposal.ToolName);
        Assert.Equal("panel-1", proposal.Arguments.GetProperty("panelId").GetString());
        var snapshot = session.Snapshot();
        Assert.Equal(NativeAgentSessionState.AwaitingToolDecision, snapshot.State);
        Assert.Single(snapshot.PendingToolProposals);

        var unusedProvider = TextProvider("must not run");
        var second = await session.RunTurnAsync(
            "Continue",
            [],
            unusedProvider,
            CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal(AgentTurnErrorCode.PendingToolDecision, second.ErrorCode);
        Assert.Equal(0, unusedProvider.CallCount);
    }

    [Fact]
    public async Task ExactToolResultContinuesWithoutInventingAUserMessage()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var proposalTurn = await session.RunTurnAsync(
            "Inspect the terminal",
            tools,
            ToolProvider(
                "terminal.read_screen",
                "{\"panelId\":\"panel-1\"}"),
            CancellationToken.None);
        var proposal = Assert.Single(proposalTurn.ToolProposals);
        var value = AgentToolResultValue.FromJson(
            "{\"text\":\"ready\"}"u8.ToArray());
        var result = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "screen_read",
            value);
        var provider = TextProvider("The terminal is ready.");

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal(AgentProviderStopReason.EndTurn, continuation.StopReason);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal(1, provider.LastRequest.Messages.Count(
            message => message.Role == AgentMessageRole.User));
        Assert.Equal(
            [
                AgentMessageRole.User,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
            ],
            provider.LastRequest.Messages.Select(message => message.Role));
        Assert.Same(
            proposal,
            Assert.Single(provider.LastRequest.Messages[1].ToolCalls));
        Assert.Same(result, provider.LastRequest.Messages[2].ToolResult);

        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Equal("Inspect the terminal", message.Content),
            message => Assert.Same(proposal, Assert.Single(message.ToolCalls)),
            message => Assert.Same(result, message.ToolResult),
            message => Assert.Equal("The terminal is ready.", message.Content));
    }

    [Fact]
    public async Task ToolResultsSettleBeforeCompactionAndProviderContinuation()
    {
        var session = CreateSession();
        Assert.True((await session.RunTurnAsync(
            "Remember this older turn",
            [],
            TextProvider("Older answer"),
            CancellationToken.None)).Succeeded);
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var proposalTurn = await session.RunTurnAsync(
            "Inspect after maintenance",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(proposalTurn.ToolProposals);

        var commitError = session.CommitToolResults(
            proposal.Generation,
            [SuccessJson(proposal, "{\"text\":\"ready\"}")],
            tools);

        Assert.Null(commitError);
        Assert.Equal(
            NativeAgentSessionState.AwaitingProviderContinuation,
            session.Snapshot().State);
        Assert.True(session.CaptureInterruptedCheckpoint().Succeeded);
        var compacted = await session.CompactAsync(
            1,
            new ImmediateCompactor(
                new AgentMessage(AgentMessageRole.Summary, "Older turn summary")),
            CancellationToken.None);
        Assert.True(compacted.Succeeded);
        var provider = TextProvider("Continued after compaction");

        var continuation = await session.ContinueToolTurnAsync(
            tools,
            provider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.NotNull(provider.LastRequest);
        Assert.Contains(
            provider.LastRequest.Messages,
            message => message.Role == AgentMessageRole.Summary
                && string.Equals(message.Content, "Older turn summary", StringComparison.Ordinal));
        Assert.Contains(
            provider.LastRequest.Messages,
            message => message.ToolResult is not null);
    }

    [Fact]
    public async Task AContinuationCanEmitAnotherBoundedProposalRound()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var firstTurn = await session.RunTurnAsync(
            "Keep inspecting",
            tools,
            ToolProvider(
                "terminal.read_screen",
                "{\"step\":1}",
                "provider-call-1"),
            CancellationToken.None);
        var firstProposal = Assert.Single(firstTurn.ToolProposals);

        var secondTurn = await session.SubmitToolResultsAsync(
            firstProposal.Generation,
            [SuccessJson(firstProposal, "{\"step\":1}")],
            tools,
            ToolProvider(
                "terminal.read_screen",
                "{\"step\":2}",
                "provider-call-2"),
            CancellationToken.None);

        Assert.True(secondTurn.Succeeded);
        var secondProposal = Assert.Single(secondTurn.ToolProposals);
        Assert.Equal(2, secondProposal.Generation);
        Assert.Equal("provider-call-2", secondProposal.ProviderCallId);

        var completed = await session.SubmitToolResultsAsync(
            secondProposal.Generation,
            [SuccessJson(secondProposal, "{\"step\":2}")],
            tools,
            TextProvider("finished"),
            CancellationToken.None);

        Assert.True(completed.Succeeded);
        var conversation = session.Snapshot().Conversation;
        Assert.Equal(
            [
                AgentMessageRole.User,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
                AgentMessageRole.Assistant,
            ],
            conversation.Select(message => message.Role));
        Assert.Single(conversation, message => message.Role == AgentMessageRole.User);
        Assert.Equal(3, session.Snapshot().Generation);
    }

    [Fact]
    public async Task ToolResultSubmissionCanRefreshTheContinuationManifest()
    {
        var session = CreateSession();
        var proposalTools =
            ImmutableArray.Create(Tool("agent.request_capability"));
        var continuationTools =
            ImmutableArray.Create(Tool("processes.list"));
        var firstTurn = await session.RunTurnAsync(
            "Inspect local processes",
            proposalTools,
            ToolProvider("agent.request_capability", "{}"),
            CancellationToken.None);
        var requestProposal = Assert.Single(firstTurn.ToolProposals);
        var continuationProvider = ToolProvider(
            "processes.list",
            "{}",
            "provider-call-2");

        var continuation = await session.SubmitToolResultsAsync(
            requestProposal.Generation,
            [SuccessJson(requestProposal, "{\"permission\":\"ask\"}")],
            proposalTools,
            continuationTools,
            continuationProvider,
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        var processProposal = Assert.Single(continuation.ToolProposals);
        Assert.Equal("processes.list", processProposal.ToolName);
        Assert.NotNull(continuationProvider.LastRequest);
        Assert.Equal(
            ["processes.list"],
            continuationProvider.LastRequest.Tools.Select(tool => tool.Name), StringComparer.Ordinal);

        var completed = await session.SubmitToolResultsAsync(
            processProposal.Generation,
            [SuccessJson(processProposal, "{\"processes\":[]}")],
            continuationTools,
            TextProvider("finished"),
            CancellationToken.None);

        Assert.True(completed.Succeeded);
    }

    [Fact]
    public async Task InvalidContinuationManifestDoesNotCommitToolResults()
    {
        var limits = new AgentKernelLimits(maximumToolDefinitions: 1);
        var session = CreateSession(limits: limits);
        var proposalTools =
            ImmutableArray.Create(Tool("agent.request_capability"));
        var firstTurn = await session.RunTurnAsync(
            "Inspect local processes",
            proposalTools,
            ToolProvider("agent.request_capability", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(firstTurn.ToolProposals);
        var continuationProvider = TextProvider("must not run");

        var result = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{\"permission\":\"ask\"}")],
            proposalTools,
            [Tool("processes.list"), Tool("terminal.read_screen")],
            continuationProvider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.LimitExceeded, result.ErrorCode);
        Assert.Equal(0, continuationProvider.CallCount);
        Assert.Equal(
            NativeAgentSessionState.AwaitingToolDecision,
            session.Snapshot().State);
        Assert.Same(
            proposal,
            Assert.Single(session.Snapshot().PendingToolProposals));
        Assert.DoesNotContain(
            session.Snapshot().Conversation,
            message => message.Role == AgentMessageRole.Tool);
    }

    [Fact]
    public async Task RefreshedContinuationStillRequiresTheExactProposalManifest()
    {
        var session = CreateSession();
        var proposalTools =
            ImmutableArray.Create(Tool("agent.request_capability"));
        var firstTurn = await session.RunTurnAsync(
            "Inspect local processes",
            proposalTools,
            ToolProvider("agent.request_capability", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(firstTurn.ToolProposals);
        var continuationProvider = TextProvider("must not run");

        var result = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{\"permission\":\"ask\"}")],
            [Tool("foreign.tool")],
            [Tool("processes.list")],
            continuationProvider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.ToolResultMismatch, result.ErrorCode);
        Assert.Equal(0, continuationProvider.CallCount);
        Assert.Equal(
            NativeAgentSessionState.AwaitingToolDecision,
            session.Snapshot().State);
        Assert.Same(
            proposal,
            Assert.Single(session.Snapshot().PendingToolProposals));
        Assert.DoesNotContain(
            session.Snapshot().Conversation,
            message => message.Role == AgentMessageRole.Tool);
    }

    [Fact]
    public async Task RemovedToolCannotBeProposedAfterManifestRefresh()
    {
        var session = CreateSession();
        var proposalTools =
            ImmutableArray.Create(Tool("agent.request_capability"));
        var continuationTools =
            ImmutableArray.Create(Tool("processes.list"));
        var firstTurn = await session.RunTurnAsync(
            "Inspect local processes",
            proposalTools,
            ToolProvider("agent.request_capability", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(firstTurn.ToolProposals);

        var continuation = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{\"permission\":\"ask\"}")],
            proposalTools,
            continuationTools,
            ToolProvider(
                "agent.request_capability",
                "{}",
                "provider-call-2"),
            CancellationToken.None);

        Assert.False(continuation.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            continuation.ErrorCode);
        Assert.Empty(continuation.ToolProposals);
        Assert.Empty(session.Snapshot().PendingToolProposals);
    }

    [Fact]
    public async Task MissingDuplicateForeignAndReplayedToolResultsFailClosed()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var first = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var valid = SuccessJson(proposal, "{}");
        var provider = TextProvider("must not run");

        var missing = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [],
            tools,
            provider,
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.ToolResultMismatch, missing.ErrorCode);

        var duplicate = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [valid, valid],
            tools,
            provider,
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.ToolResultMismatch, duplicate.ErrorCode);

        var foreign = new AgentToolResult(
            "agent-run:1:foreign",
            proposal.Generation,
            proposal.ProviderCallId,
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromJson("{}"u8.ToArray()));
        var mismatched = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [foreign],
            tools,
            provider,
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.ToolResultMismatch, mismatched.ErrorCode);

        var foreignGeneration = new AgentToolResult(
            proposal.Id,
            proposal.Generation + 1,
            proposal.ProviderCallId,
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromJson("{}"u8.ToArray()));
        Assert.Equal(
            AgentTurnErrorCode.ToolResultMismatch,
            (await session.SubmitToolResultsAsync(
                proposal.Generation,
                [foreignGeneration],
                tools,
                provider,
                CancellationToken.None)).ErrorCode);

        var foreignProviderCall = new AgentToolResult(
            proposal.Id,
            proposal.Generation,
            "foreign-provider-call",
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromJson("{}"u8.ToArray()));
        Assert.Equal(
            AgentTurnErrorCode.ToolResultMismatch,
            (await session.SubmitToolResultsAsync(
                proposal.Generation,
                [foreignProviderCall],
                tools,
                provider,
                CancellationToken.None)).ErrorCode);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(
            NativeAgentSessionState.AwaitingToolDecision,
            session.Snapshot().State);
        Assert.Same(proposal, Assert.Single(session.Snapshot().PendingToolProposals));

        var completed = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [valid],
            tools,
            TextProvider("done"),
            CancellationToken.None);
        Assert.True(completed.Succeeded);

        var replay = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [valid],
            tools,
            provider,
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.StaleToolResults, replay.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task MultiToolResultsMustMatchEveryProposalInProviderOrder()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var first = await session.RunTurnAsync(
            "Inspect twice",
            tools,
            new SequenceProvider(
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ToolCallStarted(
                    0,
                    "provider-call-1",
                    ProviderName("terminal.read_screen")),
                new AgentProviderEvent.ToolCallArgumentsDelta(0, "{\"step\":1}"),
                new AgentProviderEvent.ToolCallCompleted(0),
                new AgentProviderEvent.ToolCallStarted(
                    1,
                    "provider-call-2",
                    ProviderName("terminal.read_screen")),
                new AgentProviderEvent.ToolCallArgumentsDelta(1, "{\"step\":2}"),
                new AgentProviderEvent.ToolCallCompleted(1),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse)),
            CancellationToken.None);
        Assert.Equal(2, first.ToolProposals.Length);
        var firstResult = SuccessJson(first.ToolProposals[0], "{\"step\":1}");
        var secondResult = SuccessJson(first.ToolProposals[1], "{\"step\":2}");
        var provider = TextProvider("must not run");

        var reversed = await session.SubmitToolResultsAsync(
            first.ToolProposals[0].Generation,
            [secondResult, firstResult],
            tools,
            provider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.ToolResultMismatch, reversed.ErrorCode);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(2, session.Snapshot().PendingToolProposals.Length);

        var completed = await session.SubmitToolResultsAsync(
            first.ToolProposals[0].Generation,
            [firstResult, secondResult],
            tools,
            TextProvider("done"),
            CancellationToken.None);
        Assert.True(completed.Succeeded);
    }

    [Fact]
    public async Task ToolResultBoundsRejectBeforeStateMutationOrProviderInvocation()
    {
        var limits = new AgentKernelLimits(
            maximumToolCallsPerTurn: 1,
            maximumToolResultBytes: 4,
            maximumTotalToolResultBytesPerTurn: 4);
        var session = CreateSession(limits: limits);
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var first = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var oversized = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromText("12345"));
        var provider = TextProvider("must not run");

        var tooMany = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{}"), SuccessJson(proposal, "{}")],
            tools,
            provider,
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.LimitExceeded, tooMany.ErrorCode);

        var oversizedResult = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [oversized],
            tools,
            provider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.LimitExceeded, oversizedResult.ErrorCode);
        Assert.Equal(0, provider.CallCount);
        Assert.Same(proposal, Assert.Single(session.Snapshot().PendingToolProposals));
    }

    [Fact]
    public async Task CancellationBeforeSubmissionRetainsPendingProposal()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var first = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = TextProvider("must not run");

        var result = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{}")],
            tools,
            provider,
            cancellation.Token);

        Assert.Equal(AgentTurnErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
        Assert.Same(proposal, Assert.Single(session.Snapshot().PendingToolProposals));
    }

    [Fact]
    public async Task CancellationAfterSubmissionPreservesExecutedExchangeAndFencesLateProvider()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var first = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var result = SuccessJson(proposal, "{\"text\":\"executed\"}");
        var provider = new NonCooperativeProvider();
        using var cancellation = new CancellationTokenSource();
        var continuation = session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            provider,
            cancellation.Token).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();

        var cancelled = await continuation.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentTurnErrorCode.Cancelled, cancelled.ErrorCode);
        var snapshot = session.Snapshot();
        Assert.Equal(NativeAgentSessionState.Cancelled, snapshot.State);
        Assert.Empty(snapshot.PendingToolProposals);
        Assert.Equal(
            [
                AgentMessageRole.User,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
            ],
            snapshot.Conversation.Select(message => message.Role));
        Assert.Same(result, snapshot.Conversation[^1].ToolResult);

        var replay = await session.SubmitToolResultsAsync(
            proposal.Generation,
            [result],
            tools,
            TextProvider("must not run"),
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.StaleToolResults, replay.ErrorCode);
        var newUserTurn = await session.RunTurnAsync(
            "Do not skip the executed result",
            [],
            TextProvider("must not run"),
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.ConversationConflict, newUserTurn.ErrorCode);

        provider.Release.TrySetResult();
        await provider.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, session.Snapshot().Conversation.Length);
    }

    [Fact]
    public async Task CancelDiscardsPendingProposalsWithoutExecutingThem()
    {
        ImmutableArray<AgentMessage> initial =
        [
            new(AgentMessageRole.System, "system"),
            new(AgentMessageRole.User, "previous user"),
            new(AgentMessageRole.Assistant, "previous assistant"),
        ];
        var session = CreateSession(initial);
        var first = await session.RunTurnAsync(
            "Inspect the terminal",
            [Tool("terminal.read_screen")],
            ToolProvider("terminal.read_screen", "{\"panelId\":\"panel-1\"}"),
            CancellationToken.None);
        Assert.Single(first.ToolProposals);

        Assert.True(session.Cancel());

        var cancelled = session.Snapshot();
        Assert.Equal(NativeAgentSessionState.Cancelled, cancelled.State);
        Assert.Equal(initial.ToArray(), cancelled.Conversation.ToArray());
        Assert.Empty(cancelled.PendingToolProposals);
        var next = await session.RunTurnAsync(
            "Continue without the tool",
            [],
            TextProvider("continued"),
            CancellationToken.None);
        Assert.True(next.Succeeded);
        var events = await ReadCurrentEventBatchAsync(session);
        Assert.Single(
            events,
            agentEvent => agentEvent.Kind == AgentRunEventKind.ToolProposalsDiscarded);
    }

    [Theory]
    [InlineData(AgentProviderStopReason.MaximumTokens)]
    [InlineData(AgentProviderStopReason.ContentFiltered)]
    public async Task NonTerminalProviderStopReasonRemainsVisible(
        AgentProviderStopReason stopReason)
    {
        var session = CreateSession();
        var provider = new SequenceProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta("partial"),
            new AgentProviderEvent.ResponseCompleted(stopReason));

        var result = await session.RunTurnAsync(
            "Generate",
            [],
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(stopReason, result.StopReason);
        Assert.Equal("partial", session.Snapshot().Conversation[^1].Content);
    }

    [Fact]
    public async Task CancelFencesLateProviderEventsAndAllowsANewGeneration()
    {
        var session = CreateSession();
        var oldProvider = new NonCooperativeProvider();
        var oldTurn = session.RunTurnAsync(
            "old request",
            [],
            oldProvider,
            CancellationToken.None).AsTask();
        await oldProvider.Started.Task.WaitAsync(CancellationToken.None);

        Assert.True(session.Cancel());
        Assert.True(oldProvider.SeenToken.IsCancellationRequested);
        var oldResult = await oldTurn.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentTurnErrorCode.Cancelled, oldResult.ErrorCode);

        var newTurn = await session.RunTurnAsync(
            "new request",
            [],
            TextProvider("new response"),
            CancellationToken.None);
        Assert.True(newTurn.Succeeded);

        oldProvider.Release.TrySetResult();
        await oldProvider.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var snapshot = session.Snapshot();
        Assert.Equal(3, snapshot.Generation);
        Assert.Collection(
            snapshot.Conversation,
            message => Assert.Equal("new request", message.Content),
            message => Assert.Equal("new response", message.Content));
        var events = await ReadCurrentEventBatchAsync(session);
        Assert.Single(events, agentEvent => agentEvent.Kind == AgentRunEventKind.TurnCancelled);
        Assert.DoesNotContain(
            events,
            agentEvent => string.Equals(agentEvent.ProvisionalText, "late response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellationIsImmediatelyVisibleAndReachesProvider()
    {
        var session = CreateSession();
        var provider = new NonCooperativeProvider(throwOnCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var turn = session.RunTurnAsync(
            "cancel me",
            [],
            provider,
            cancellation.Token).AsTask();
        await provider.Started.Task.WaitAsync(CancellationToken.None);

        var cancellationException = Record.Exception(cancellation.Cancel);

        Assert.Null(cancellationException);
        Assert.Equal(NativeAgentSessionState.Cancelled, session.Snapshot().State);
        Assert.True(provider.SeenToken.IsCancellationRequested);
        var result = await turn.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentTurnErrorCode.Cancelled, result.ErrorCode);
        provider.Release.TrySetResult();
        await provider.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var events = await ReadCurrentEventBatchAsync(session);
        Assert.Single(events, agentEvent => agentEvent.Kind == AgentRunEventKind.TurnCancelled);
    }

    [Fact]
    public async Task NonCooperativeProviderOperationsAreBounded()
    {
        var limits = new AgentKernelLimits(maximumConcurrentProviderOperations: 1);
        var session = CreateSession(limits: limits);
        var provider = new NonCooperativeProvider();
        var first = session.RunTurnAsync(
            "first",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(session.Cancel());
        Assert.Equal(
            AgentTurnErrorCode.Cancelled,
            (await first.WaitAsync(TimeSpan.FromSeconds(1))).ErrorCode);

        var rejectedProvider = TextProvider("must not run");
        var rejected = await session.RunTurnAsync(
            "second",
            [],
            rejectedProvider,
            CancellationToken.None);
        Assert.Equal(AgentTurnErrorCode.ProviderOperationLimit, rejected.ErrorCode);
        Assert.Equal(0, rejectedProvider.CallCount);

        provider.Release.TrySetResult();
        await provider.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        AgentTurnResult retry;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        do
        {
            await Task.Delay(1);
            retry = await session.RunTurnAsync(
                "retry",
                [],
                TextProvider("accepted"),
                CancellationToken.None);
        }
        while (retry.ErrorCode == AgentTurnErrorCode.ProviderOperationLimit
               && DateTimeOffset.UtcNow < deadline);

        Assert.True(retry.Succeeded);
    }

    [Fact]
    public async Task BlockingProviderCancellationCallbackCannotBlockPublicCancel()
    {
        var session = CreateSession();
        var provider = new NonCooperativeProvider(blockOnCancellation: true);
        var turn = session.RunTurnAsync(
            "cancel me",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var cancel = Task.Run(session.Cancel);
        Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(1)));
        await provider.CancellationCallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(
            AgentTurnErrorCode.Cancelled,
            (await turn.WaitAsync(TimeSpan.FromSeconds(1))).ErrorCode);

        provider.ReleaseCancellationCallback.TrySetResult();
        provider.Release.TrySetResult();
        await provider.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SynchronouslyBlockingProviderEntrypointDoesNotPinTheCaller()
    {
        var session = CreateSession();
        var provider = new BlockingEntrypointProvider();
        var invocationReturned = new TaskCompletionSource<Task<AgentTurnResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(
            () => invocationReturned.TrySetResult(
                session.RunTurnAsync(
                    "start",
                    [],
                    provider,
                    CancellationToken.None).AsTask()));

        try
        {
            var turn = await invocationReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(session.Cancel());
            Assert.Equal(
                AgentTurnErrorCode.Cancelled,
                (await turn.WaitAsync(TimeSpan.FromSeconds(1))).ErrorCode);
        }
        finally
        {
            provider.Release.TrySetResult();
            await invocation.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task StaleCursorGetsBoundedResynchronization()
    {
        var limits = new AgentKernelLimits(
            maximumRetainedEvents: 3,
            maximumEventBatchSize: 2);
        var session = CreateSession(limits: limits);
        var provider = new SequenceProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta("1"),
            new AgentProviderEvent.TextDelta("2"),
            new AgentProviderEvent.TextDelta("3"),
            new AgentProviderEvent.TextDelta("4"),
            new AgentProviderEvent.TextDelta("5"),
            new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn));
        var result = await session.RunTurnAsync(
            "count",
            [],
            provider,
            CancellationToken.None);
        Assert.True(result.Succeeded);

        await using var watcher = session.WatchAsync(
                new AgentEventWatchRequest(0, 2),
                CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await watcher.MoveNextAsync());
        var resynchronization =
            Assert.IsType<AgentRunStreamItem.ResynchronizationRequired>(watcher.Current);
        Assert.Equal(session.Snapshot().LastSequence, resynchronization.ResumeAfterSequence);
        Assert.True(resynchronization.Snapshot.Conversation.Length <= 2);
        Assert.False(await watcher.MoveNextAsync());
    }

    [Fact]
    public async Task WatchBatchesNeverExceedTheRequestedMaximum()
    {
        var limits = new AgentKernelLimits(
            maximumRetainedEvents: 3,
            maximumEventBatchSize: 2);
        var session = CreateSession(limits: limits);
        var provider = new SequenceProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta("1"),
            new AgentProviderEvent.TextDelta("2"),
            new AgentProviderEvent.TextDelta("3"),
            new AgentProviderEvent.TextDelta("4"),
            new AgentProviderEvent.TextDelta("5"),
            new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn));
        Assert.True((await session.RunTurnAsync(
            "count",
            [],
            provider,
            CancellationToken.None)).Succeeded);

        await using var watcher = session.WatchAsync(
                new AgentEventWatchRequest(4, 2),
                CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await watcher.MoveNextAsync());
        var first = Assert.IsType<AgentRunStreamItem.EventBatch>(watcher.Current);
        Assert.Equal(2, first.Events.Length);
        Assert.True(await watcher.MoveNextAsync());
        var second = Assert.IsType<AgentRunStreamItem.EventBatch>(watcher.Current);
        Assert.Single(second.Events);
        Assert.Equal(first.Events[^1].Sequence + 1, second.Events[0].Sequence);
    }

    [Fact]
    public async Task FutureCursorRequiresImmediateResynchronization()
    {
        var session = CreateSession();

        await using var watcher = session.WatchAsync(
                new AgentEventWatchRequest(42, 1),
                CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await watcher.MoveNextAsync());
        var item = Assert.IsType<AgentRunStreamItem.ResynchronizationRequired>(
            watcher.Current);
        Assert.Equal(0, item.ResumeAfterSequence);
        Assert.False(await watcher.MoveNextAsync());
    }

    [Fact]
    public async Task WatchCancellationStopsBeforeAnotherBufferedBatch()
    {
        var limits = new AgentKernelLimits(
            maximumRetainedEvents: 16,
            maximumEventBatchSize: 2);
        var session = CreateSession(limits: limits);
        Assert.True((await session.RunTurnAsync(
            "count",
            [],
            new SequenceProvider(
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.TextDelta("1"),
                new AgentProviderEvent.TextDelta("2"),
                new AgentProviderEvent.TextDelta("3"),
                new AgentProviderEvent.TextDelta("4"),
                new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn)),
            CancellationToken.None)).Succeeded);
        using var cancellation = new CancellationTokenSource();
        await using var watcher = session.WatchAsync(
                new AgentEventWatchRequest(0, 2),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await watcher.MoveNextAsync());
        Assert.IsType<AgentRunStreamItem.EventBatch>(watcher.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await watcher.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task ConversationLimitRejectsBeforeCallingProvider()
    {
        var limits = new AgentKernelLimits(
            maximumProviderTextFragmentBytes: 4,
            maximumAssistantTextBytes: 4,
            maximumConversationMessages: 4,
            maximumConversationBytes: 8);
        var session = CreateSession(
            [new AgentMessage(AgentMessageRole.System, "123456")],
            limits);
        var provider = TextProvider("ok");

        var result = await session.RunTurnAsync(
            "123",
            [],
            provider,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentTurnErrorCode.LimitExceeded, result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
        Assert.Single(session.Snapshot().Conversation);
    }

    [Fact]
    public void InvalidStableTranscriptShapesAreRejected()
    {
        AgentMessage[][] invalidTranscripts =
        [
            [new AgentMessage(AgentMessageRole.Assistant, "assistant first")],
            [new AgentMessage(AgentMessageRole.User, "trailing user")],
            [
                new AgentMessage(AgentMessageRole.User, "one"),
                new AgentMessage(AgentMessageRole.User, "two"),
            ],
            [
                new AgentMessage(AgentMessageRole.User, "user"),
                new AgentMessage(AgentMessageRole.Assistant, "assistant"),
                new AgentMessage(AgentMessageRole.System, "late system"),
            ],
            [
                new AgentMessage(AgentMessageRole.Summary, "one"),
                new AgentMessage(AgentMessageRole.Summary, "two"),
            ],
            [new AgentMessage(AgentMessageRole.Tool, "unstructured tool result")],
            [new AgentMessage(AgentMessageRole.System, " ")],
        ];

        foreach (var transcript in invalidTranscripts)
        {
            Assert.Throws<ArgumentException>(() => CreateSession(transcript));
        }
    }

    [Fact]
    public void InitialConversationEnumerationStopsAtTheConfiguredBound()
    {
        var source = new CountingInfiniteConversation();
        var limits = new AgentKernelLimits(maximumConversationMessages: 2);

        Assert.Throws<ArgumentException>(() => CreateSession(source, limits));
        Assert.Equal(3, source.YieldCount);
    }

    [Fact]
    public async Task ToolSchemasRespectSessionDepthAndNodeLimits()
    {
        var limits = new AgentKernelLimits(maximumJsonDepth: 2);
        var session = CreateSession(limits: limits);
        var provider = TextProvider("must not run");
        var tool = new AgentToolDefinition(
            "terminal.read_screen",
            "Read a terminal snapshot.",
            """
            {
              "type": "object",
              "properties": {
                "panel": {
                  "type": "string"
                }
              }
            }
            """u8.ToArray());

        var result = await session.RunTurnAsync(
            "Inspect",
            [tool],
            provider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.LimitExceeded, result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ToolSchemasRespectTheirAggregateByteLimit()
    {
        var limits = new AgentKernelLimits(
            maximumToolSchemaBytes: 32,
            maximumTotalToolSchemaBytes: 32);
        var session = CreateSession(limits: limits);
        var provider = TextProvider("must not run");

        var result = await session.RunTurnAsync(
            "Inspect",
            [Tool("one"), Tool("two")],
            provider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.LimitExceeded, result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ProviderAliasCollisionRejectsBeforeProviderInvocation()
    {
        var internalTool = Tool("terminal.read_screen");
        var collidingSafeTool = Tool(internalTool.ProviderName);
        var session = CreateSession();
        var provider = TextProvider("must not run");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.RunTurnAsync(
                "Inspect",
                [internalTool, collidingSafeTool],
                provider,
                CancellationToken.None));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ContinuationManifestCannotRebindAProviderAlias()
    {
        var session = CreateSession();
        var proposalTool = Tool("terminal.read_screen");
        var proposalTools = ImmutableArray.Create(proposalTool);
        var first = await session.RunTurnAsync(
            "Inspect",
            proposalTools,
            ToolProvider(proposalTool.Name, "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        var result = SuccessJson(proposal, "{}");
        var continuationProvider = TextProvider("must not run");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.SubmitToolResultsAsync(
                proposal.Generation,
                [result],
                proposalTools,
                [Tool(proposalTool.ProviderName)],
                continuationProvider,
                CancellationToken.None));

        Assert.Equal(0, continuationProvider.CallCount);
        Assert.Equal(
            NativeAgentSessionState.AwaitingToolDecision,
            session.Snapshot().State);
        Assert.Equal(proposal, Assert.Single(session.Snapshot().PendingToolProposals));
    }

    [Fact]
    public async Task LaterTurnCannotRebindAProviderAlias()
    {
        var session = CreateSession();
        var originalTool = Tool("terminal.read_screen");
        var first = await session.RunTurnAsync(
            "Inspect",
            [originalTool],
            TextProvider("done"),
            CancellationToken.None);
        Assert.True(first.Succeeded);
        var reboundProvider = TextProvider("must not run");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.RunTurnAsync(
                "Inspect again",
                [Tool(originalTool.ProviderName)],
                reboundProvider,
                CancellationToken.None));

        Assert.Equal(0, reboundProvider.CallCount);
    }

    [Fact]
    public async Task InitialStructuredHistorySeedsProviderAliasBindings()
    {
        using var arguments = System.Text.Json.JsonDocument.Parse("{}");
        var proposal = new AgentToolProposal(
            "agent-run:1:0",
            1,
            "provider-call-1",
            "terminal.read_screen",
            arguments.RootElement);
        var result = SuccessJson(proposal, "{}");
        var session = CreateSession(
        [
            new AgentMessage(AgentMessageRole.User, "Inspect"),
            AgentMessage.Assistant("", [proposal]),
            AgentMessage.FromToolResult(result),
            new AgentMessage(AgentMessageRole.Assistant, "Done"),
        ]);
        var reboundProvider = TextProvider("must not run");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.RunTurnAsync(
                "Inspect again",
                [Tool(proposal.ProviderName)],
                reboundProvider,
                CancellationToken.None));

        Assert.Equal(0, reboundProvider.CallCount);
    }

    [Fact]
    public async Task ProviderAliasBindingSurvivesCancelledProviderVisibleTurn()
    {
        var session = CreateSession();
        var originalTool = Tool("terminal.read_screen");
        var blockingProvider = new NonCooperativeProvider();
        var turn = session.RunTurnAsync(
            "Inspect",
            [originalTool],
            blockingProvider,
            CancellationToken.None).AsTask();
        await blockingProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(session.Cancel());
        var cancelled = await turn.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentTurnErrorCode.Cancelled, cancelled.ErrorCode);
        blockingProvider.Release.TrySetResult();
        await blockingProvider.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var reboundProvider = TextProvider("must not run");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.RunTurnAsync(
                "Inspect again",
                [Tool(originalTool.ProviderName)],
                reboundProvider,
                CancellationToken.None));

        Assert.Equal(0, reboundProvider.CallCount);
    }

    [Fact]
    public async Task ProviderAliasBindingSurvivesConversationCompaction()
    {
        var session = CreateSession();
        var originalTool = Tool("terminal.read_screen");
        var first = await session.RunTurnAsync(
            "Inspect",
            [originalTool],
            TextProvider("done"),
            CancellationToken.None);
        Assert.True(first.Succeeded);
        var compacted = await session.CompactAsync(
            0,
            new ImmediateCompactor(
                new AgentMessage(AgentMessageRole.Summary, "summary")),
            CancellationToken.None);
        Assert.True(compacted.Succeeded);
        var reboundProvider = TextProvider("must not run");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await session.RunTurnAsync(
                "Inspect again",
                [Tool(originalTool.ProviderName)],
                reboundProvider,
                CancellationToken.None));

        Assert.Equal(0, reboundProvider.CallCount);
    }

    [Fact]
    public async Task SessionProviderAliasBindingCountIsBounded()
    {
        var limits = AgentKernelLimits.Default;
        var tools = Enumerable.Range(0, limits.MaximumToolDefinitions)
            .Select(index => Tool($"bounded_tool_{index}"))
            .ToImmutableArray();
        var session = CreateSession(limits: limits);
        var first = await session.RunTurnAsync(
            "Inspect",
            tools,
            TextProvider("done"),
            CancellationToken.None);
        Assert.True(first.Succeeded);
        var overflowProvider = TextProvider("must not run");

        var overflow = await session.RunTurnAsync(
            "Inspect again",
            [Tool("overflow_tool")],
            overflowProvider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.LimitExceeded, overflow.ErrorCode);
        Assert.Equal(0, overflowProvider.CallCount);
    }

    [Fact]
    public async Task ProviderTurnAcceptsTheMaximumDefinitionCount()
    {
        var limits = AgentKernelLimits.Default;
        var tools = Enumerable.Range(0, limits.MaximumToolDefinitions)
            .Select(index => Tool($"tool_{index}"))
            .ToImmutableArray();
        var session = CreateSession(limits: limits);
        var provider = TextProvider("accepted");

        var result = await session.RunTurnAsync(
            "Inspect",
            tools,
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(tools.Length > limits.MaximumToolCallsPerTurn);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(tools, provider.LastRequest!.Tools);
    }

    [Fact]
    public async Task ToolDefinitionLimitRejectsBeforeProviderInvocation()
    {
        var limits = AgentKernelLimits.Default;
        var tools = Enumerable.Range(0, limits.MaximumToolDefinitions + 1)
            .Select(index => Tool($"tool_{index}"))
            .ToImmutableArray();
        var session = CreateSession(limits: limits);
        var provider = TextProvider("must not run");

        var result = await session.RunTurnAsync(
            "Inspect",
            tools,
            provider,
            CancellationToken.None);

        Assert.Equal(AgentTurnErrorCode.LimitExceeded, result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ToolSchemaLimitsAreIndependentFromToolArgumentLimits()
    {
        var limits = new AgentKernelLimits(
            maximumToolArgumentFragmentBytes: 1,
            maximumToolArgumentBytes: 1,
            maximumTotalToolArgumentBytesPerTurn: 1);
        var session = CreateSession(limits: limits);
        var provider = TextProvider("accepted");

        var result = await session.RunTurnAsync(
            "Inspect",
            [Tool("terminal.read_screen")],
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SuccessfulCompactionSwapsOnceAndPreservesSuffix()
    {
        var initial = ConversationFixture();
        var session = CreateSession(initial);
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "summary"));

        var result = await session.CompactAsync(
            1,
            compactor,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Same(initial[0], message),
            message => Assert.Equal("summary", message.Content),
            message => Assert.Same(initial[3], message),
            message => Assert.Same(initial[4], message));
        Assert.Equal(initial.ToArray(), session.Snapshot().Transcript.ToArray());
        Assert.NotNull(compactor.LastRequest);
        Assert.Collection(
            compactor.LastRequest.Messages,
            message => Assert.Same(initial[1], message),
            message => Assert.Same(initial[2], message));
        var events = await ReadCurrentEventBatchAsync(session);
        Assert.Single(events);
        Assert.Equal(AgentRunEventKind.ConversationCompacted, events[0].Kind);
    }

    [Fact]
    public async Task TurnAfterCompactionKeepsVisibleTranscriptAndUsesSummaryContext()
    {
        var initial = ConversationFixture();
        var session = CreateSession(initial);
        Assert.True((await session.CompactAsync(
            1,
            new ImmediateCompactor(
                new AgentMessage(AgentMessageRole.Summary, "summary")),
            CancellationToken.None)).Succeeded);
        var provider = TextProvider("next assistant");

        var result = await session.RunTurnAsync(
            "next user",
            [],
            provider,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                "system",
                "summary",
                "current user",
                "current assistant",
                "next user",
                "next assistant",
            ],
            session.Snapshot().Conversation.Select(message => message.Content), StringComparer.Ordinal);
        Assert.Equal(
            [
                "system",
                "old user",
                "old assistant",
                "current user",
                "current assistant",
                "next user",
                "next assistant",
            ],
            session.Snapshot().Transcript.Select(message => message.Content), StringComparer.Ordinal);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal(
            ["system", "summary", "current user", "current assistant", "next user"],
            provider.LastRequest.Messages.Select(message => message.Content), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CompactionTreatsStructuredToolExchangeAsOneConversationTurn()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var first = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(first.ToolProposals);
        Assert.True((await session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{\"text\":\"ready\"}")],
            tools,
            TextProvider("observed"),
            CancellationToken.None)).Succeeded);
        Assert.True((await session.RunTurnAsync(
            "Explain",
            [],
            TextProvider("explained"),
            CancellationToken.None)).Succeeded);
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "tool exchange summary"));

        var compacted = await session.CompactAsync(
            1,
            compactor,
            CancellationToken.None);

        Assert.True(compacted.Succeeded);
        Assert.NotNull(compactor.LastRequest);
        Assert.Equal(
            [
                AgentMessageRole.User,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
                AgentMessageRole.Assistant,
            ],
            compactor.LastRequest.Messages.Select(message => message.Role));
        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Equal(AgentMessageRole.Summary, message.Role),
            message => Assert.Equal("Explain", message.Content),
            message => Assert.Equal("explained", message.Content));
    }

    [Fact]
    public async Task CompactionConflictLeavesTheNewConversationUntouched()
    {
        var initial = ConversationFixture();
        var session = CreateSession(initial);
        var compactor = new ControlledCompactor();
        var compaction = session.CompactAsync(
            1,
            compactor,
            CancellationToken.None).AsTask();
        await compactor.Started.Task.WaitAsync(CancellationToken.None);

        var turn = await session.RunTurnAsync(
            "new user",
            [],
            TextProvider("new assistant"),
            CancellationToken.None);
        Assert.True(turn.Succeeded);
        compactor.Release.TrySetResult(
            new AgentMessage(AgentMessageRole.Summary, "stale summary"));

        var result = await compaction.WaitAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(AgentCompactionErrorCode.ConversationConflict, result.ErrorCode);
        Assert.DoesNotContain(
            session.Snapshot().Conversation,
            message => string.Equals(message.Content, "stale summary", StringComparison.Ordinal));
        Assert.Equal(7, session.Snapshot().Conversation.Length);
    }

    [Fact]
    public async Task CancelledOrFailedCompactionChangesNothing()
    {
        var initial = ConversationFixture();
        var session = CreateSession(initial);
        using var cancellation = new CancellationTokenSource();
        var blocking = new CancellingCompactor();
        var cancelledTask = session.CompactAsync(1, blocking, cancellation.Token).AsTask();
        await blocking.Started.Task.WaitAsync(CancellationToken.None);
        cancellation.Cancel();
        var cancelled = await cancelledTask.WaitAsync(CancellationToken.None);
        Assert.Equal(AgentCompactionErrorCode.Cancelled, cancelled.ErrorCode);
        Assert.Equal(initial.ToArray(), session.Snapshot().Conversation.ToArray());
        await blocking.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        AgentCompactionResult failed;
        var attempts = 0;
        do
        {
            await Task.Yield();
            failed = await session.CompactAsync(
                1,
                new FailingCompactor(),
                CancellationToken.None);
            attempts++;
        }
        while (failed.ErrorCode == AgentCompactionErrorCode.Busy && attempts < 100);

        Assert.Equal(AgentCompactionErrorCode.CompactorFailure, failed.ErrorCode);
        Assert.Equal(initial.ToArray(), session.Snapshot().Conversation.ToArray());
        Assert.Equal(0, session.Snapshot().Revision);
    }

    [Fact]
    public async Task NonCooperativeCompactionCancellationReturnsAndNeverCommitsLate()
    {
        var initial = ConversationFixture();
        var session = CreateSession(initial);
        var compactor = new NonCooperativeCompactor(throwOnCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var compaction = session.CompactAsync(
            1,
            compactor,
            cancellation.Token).AsTask();
        await compactor.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var cancellationException = Record.Exception(cancellation.Cancel);

        Assert.Null(cancellationException);
        var result = await compaction.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentCompactionErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(initial.ToArray(), session.Snapshot().Conversation.ToArray());
        var whileStopping = await session.CompactAsync(
            1,
            new ImmediateCompactor(
                new AgentMessage(AgentMessageRole.Summary, "must not run")),
            CancellationToken.None);
        Assert.Equal(AgentCompactionErrorCode.Busy, whileStopping.ErrorCode);

        compactor.Release.TrySetResult(
            new AgentMessage(AgentMessageRole.Summary, "late summary"));
        await compactor.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(initial.ToArray(), session.Snapshot().Conversation.ToArray());
        Assert.Equal(0, session.Snapshot().Revision);
    }

    [Fact]
    public async Task SynchronouslyBlockingCompactorEntrypointDoesNotPinTheCaller()
    {
        var session = CreateSession(ConversationFixture());
        var compactor = new BlockingEntrypointCompactor();
        using var cancellation = new CancellationTokenSource();
        var invocationReturned = new TaskCompletionSource<Task<AgentCompactionResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(
            () => invocationReturned.TrySetResult(
                session.CompactAsync(1, compactor, cancellation.Token).AsTask()));

        try
        {
            var compaction = await invocationReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await compactor.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();
            Assert.Equal(
                AgentCompactionErrorCode.Cancelled,
                (await compaction.WaitAsync(TimeSpan.FromSeconds(1))).ErrorCode);
        }
        finally
        {
            compactor.Release.TrySetResult();
            await invocation.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task CompactionPinsSystemPreambleAndRollsForwardExistingSummary()
    {
        ImmutableArray<AgentMessage> initial =
        [
            new(AgentMessageRole.System, "system one"),
            new(AgentMessageRole.System, "system two"),
            new(AgentMessageRole.Summary, "old summary"),
            new(AgentMessageRole.User, "old user"),
            new(AgentMessageRole.Assistant, "old assistant"),
            new(AgentMessageRole.User, "current user"),
            new(AgentMessageRole.Assistant, "current assistant"),
        ];
        var session = CreateSession(initial);
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "new summary"));

        var result = await session.CompactAsync(1, compactor, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Same(initial[0], message),
            message => Assert.Same(initial[1], message),
            message => Assert.Equal("new summary", message.Content),
            message => Assert.Same(initial[5], message),
            message => Assert.Same(initial[6], message));
        Assert.NotNull(compactor.LastRequest);
        Assert.Collection(
            compactor.LastRequest.Messages,
            message => Assert.Same(initial[2], message),
            message => Assert.Same(initial[3], message),
            message => Assert.Same(initial[4], message));
    }

    [Fact]
    public async Task TokenBudgetCompactionUsesPiThresholdAndSplitsRecentTurn()
    {
        ImmutableArray<AgentMessage> initial =
        [
            new(AgentMessageRole.User, new string('a', 120)),
            new(AgentMessageRole.Assistant, new string('b', 120)),
            new(AgentMessageRole.User, new string('c', 80)),
            new(AgentMessageRole.Assistant, new string('d', 80)),
        ];
        var session = CreateSession(initial);
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "summary"));

        var result = await session.CompactAsync(
            contextWindowTokens: 100,
            new AgentCompactionSettings(reserveTokens: 16, keepRecentTokens: 20),
            compactor,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(compactor.LastRequest);
        Assert.Equal(initial[..2].ToArray(), compactor.LastRequest.Messages.ToArray());
        Assert.Equal(
            initial[2..3].ToArray(),
            compactor.LastRequest.TurnPrefixMessages.ToArray());
        Assert.True(compactor.LastRequest.IsSplitTurn);
        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Equal(AgentMessageRole.Summary, message.Role),
            message => Assert.Same(initial[3], message));
    }

    [Fact]
    public async Task TokenBudgetCompactionSplitsOneToolTurnAndKeepsContinuationValid()
    {
        var session = CreateSession();
        var tools = ImmutableArray.Create(Tool("terminal.read_screen"));
        var proposalTurn = await session.RunTurnAsync(
            "Inspect the terminal",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(proposalTurn.ToolProposals);
        Assert.Null(session.CommitToolResults(
            proposal.Generation,
            [SuccessJson(proposal, $"{{\"text\":\"{new string('x', 400)}\"}}")],
            tools));
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "split-turn summary"));

        var compacted = await session.CompactAsync(
            contextWindowTokens: 100,
            new AgentCompactionSettings(reserveTokens: 16, keepRecentTokens: 80),
            compactor,
            CancellationToken.None);

        Assert.True(compacted.Succeeded);
        Assert.NotNull(compactor.LastRequest);
        Assert.Empty(compactor.LastRequest.Messages);
        Assert.Equal(
            [AgentMessageRole.User],
            compactor.LastRequest.TurnPrefixMessages.Select(message => message.Role));
        Assert.Collection(
            session.Snapshot().Conversation,
            message => Assert.Equal(AgentMessageRole.Summary, message.Role),
            message => Assert.Equal(AgentMessageRole.Assistant, message.Role),
            message => Assert.Equal(AgentMessageRole.Tool, message.Role));

        var continuation = await session.ContinueToolTurnAsync(
            tools,
            TextProvider("The terminal is ready."),
            CancellationToken.None);

        Assert.True(continuation.Succeeded);
        Assert.Equal(
            "The terminal is ready.",
            session.Snapshot().Conversation[^1].Content);
    }

    [Fact]
    public async Task PostCompactionUsageReplacesStaleRetainedUsage()
    {
        var staleUsage = new AgentTokenUsage(90, 10);
        ImmutableArray<AgentMessage> initial =
        [
            new(AgentMessageRole.User, new string('a', 120)),
            AgentMessage.Assistant(new string('b', 120), [], usage: staleUsage),
            new(AgentMessageRole.User, new string('c', 80)),
            AgentMessage.Assistant(new string('d', 80), [], usage: staleUsage),
        ];
        var session = CreateSession(initial);
        var compacted = await session.CompactAsync(
            contextWindowTokens: 100,
            new AgentCompactionSettings(reserveTokens: 16, keepRecentTokens: 20),
            new ImmediateCompactor(
                new AgentMessage(AgentMessageRole.Summary, "summary")),
            CancellationToken.None);

        Assert.True(compacted.Succeeded);
        Assert.Null(session.Snapshot().Conversation[^1].Usage);
        Assert.False(session.EstimateContextUsage().UsesProviderReportedUsage);

        var freshUsage = new AgentTokenUsage(40, 5);
        var provider = new SequenceProvider(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta("fresh answer"),
            new AgentProviderEvent.Usage(freshUsage),
            new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn));
        Assert.True((await session.RunTurnAsync(
            "Continue",
            [],
            provider,
            CancellationToken.None)).Succeeded);

        var usage = session.EstimateContextUsage();
        Assert.True(usage.UsesProviderReportedUsage);
        Assert.Equal(freshUsage.TotalTokens, usage.EstimatedTokens);
    }

    [Fact]
    public async Task TokenBudgetCompactionDoesNothingBelowReservedThreshold()
    {
        var session = CreateSession(ConversationFixture());
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "unused"));

        var result = await session.CompactAsync(
            contextWindowTokens: 1_000,
            new AgentCompactionSettings(reserveTokens: 100, keepRecentTokens: 100),
            compactor,
            CancellationToken.None);

        Assert.Equal(AgentCompactionErrorCode.NothingToCompact, result.ErrorCode);
        Assert.Null(compactor.LastRequest);
    }

    [Fact]
    public async Task RetainingEveryTurnDoesNotCallTheCompactor()
    {
        var session = CreateSession(ConversationFixture());
        var compactor = new ImmediateCompactor(
            new AgentMessage(AgentMessageRole.Summary, "unused"));

        var result = await session.CompactAsync(2, compactor, CancellationToken.None);

        Assert.Equal(AgentCompactionErrorCode.NothingToCompact, result.ErrorCode);
        Assert.Null(compactor.LastRequest);
    }

    private static NativeAgentSession CreateSession(
        IEnumerable<AgentMessage>? initial = null,
        AgentKernelLimits? limits = null) =>
        new(
            new AgentRunId("agent-run"),
            initial,
            limits,
            TimeProvider.System);

    private static SequenceProvider TextProvider(string text) =>
        new(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.TextDelta(text),
            new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.EndTurn));

    private static SequenceProvider ToolProvider(
        string name,
        string arguments,
        string providerCallId = "provider-call-1") =>
        new(
            new AgentProviderEvent.ResponseStarted(),
            new AgentProviderEvent.ToolCallStarted(
                0,
                providerCallId,
                ProviderName(name)),
            new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments),
            new AgentProviderEvent.ToolCallCompleted(0),
            new AgentProviderEvent.ResponseCompleted(AgentProviderStopReason.ToolUse));

    private static AgentToolResult SuccessJson(
        AgentToolProposal proposal,
        string json) =>
        new(
            proposal,
            AgentToolResultStatus.Succeeded,
            "ok",
            AgentToolResultValue.FromJson(System.Text.Encoding.UTF8.GetBytes(json)));

    private static AgentToolDefinition Tool(string name)
        => new(
            name,
            $"Schema for {name}.",
            "{\"type\":\"object\"}"u8.ToArray());

    private static string ProviderName(string internalName) =>
        AgentToolDefinition.GetProviderName(internalName);

    private static ImmutableArray<AgentMessage> ConversationFixture() =>
    [
        new AgentMessage(AgentMessageRole.System, "system"),
        new AgentMessage(AgentMessageRole.User, "old user"),
        new AgentMessage(AgentMessageRole.Assistant, "old assistant"),
        new AgentMessage(AgentMessageRole.User, "current user"),
        new AgentMessage(AgentMessageRole.Assistant, "current assistant"),
    ];

    private static async Task<ImmutableArray<AgentRunEvent>> ReadCurrentEventBatchAsync(
        NativeAgentSession session)
    {
        await using var watcher = session.WatchAsync(
                new AgentEventWatchRequest(0, AgentKernelLimits.Default.MaximumEventBatchSize),
                CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await watcher.MoveNextAsync());
        return Assert.IsType<AgentRunStreamItem.EventBatch>(watcher.Current).Events;
    }

    private sealed class SequenceProvider(params AgentProviderEvent[] events) : IAgentProvider
    {
        public int CallCount { get; private set; }

        public AgentProviderRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            await Task.Yield();
            foreach (var providerEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return providerEvent;
            }
        }
    }

    private sealed class ThrowingProvider(params AgentProviderEvent[] events) : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var providerEvent in events)
            {
                yield return providerEvent;
            }

            throw new InvalidOperationException("Provider failure detail must not escape.");
        }
    }

    private sealed class SafeFailingProvider : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new TestProviderException(
                "ai_provider_model_unavailable",
                "The configured AI model is unavailable.",
                new InvalidOperationException("private transport detail"));
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class TestProviderException(
        string stableCode,
        string publicMessage,
        Exception innerException)
        : AgentProviderException(stableCode, publicMessage, innerException)
    {
    }

    private sealed class NonCooperativeProvider(
        bool throwOnCancellation = false,
        bool blockOnCancellation = false) : IAgentProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationCallbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancellationCallback { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken SeenToken { get; private set; }

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var registration = throwOnCancellation
                ? cancellationToken.Register(
                    () => throw new InvalidOperationException(
                        "Provider cancellation callback must not escape."))
                : blockOnCancellation
                    ? cancellationToken.Register(
                        () =>
                        {
                            CancellationCallbackStarted.TrySetResult();
                            ReleaseCancellationCallback.Task.GetAwaiter().GetResult();
                        })
                : default;
            try
            {
                SeenToken = cancellationToken;
                Started.TrySetResult();
                yield return new AgentProviderEvent.ResponseStarted();
                await Release.Task.ConfigureAwait(false);
                yield return new AgentProviderEvent.TextDelta("late response");
                yield return new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class BlockingEntrypointProvider : IAgentProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return EmptyStream();
        }

        private static async IAsyncEnumerable<AgentProviderEvent> EmptyStream()
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class ImmediateCompactor(AgentMessage summary) : IAgentConversationCompactor
    {
        public AgentCompactionRequest? LastRequest { get; private set; }

        public ValueTask<AgentMessage> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(summary);
        }
    }

    private sealed class ControlledCompactor : IAgentConversationCompactor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AgentMessage> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentMessage> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CancellingCompactor : IAgentConversationCompactor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentMessage> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable.");
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class FailingCompactor : IAgentConversationCompactor
    {
        public ValueTask<AgentMessage> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<AgentMessage>(
                new InvalidOperationException("Compactor failure detail must not escape."));
    }

    private sealed class NonCooperativeCompactor(bool throwOnCancellation = false)
        : IAgentConversationCompactor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<AgentMessage> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentMessage> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            using var registration = throwOnCancellation
                ? cancellationToken.Register(
                    () => throw new InvalidOperationException(
                        "Compactor cancellation callback must not escape."))
                : default;
            try
            {
                return await Release.Task.ConfigureAwait(false);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class BlockingEntrypointCompactor : IAgentConversationCompactor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AgentMessage> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return ValueTask.FromResult(
                new AgentMessage(AgentMessageRole.Summary, "unused"));
        }
    }

    private sealed class CountingInfiniteConversation : IEnumerable<AgentMessage>
    {
        public int YieldCount { get; private set; }

        public IEnumerator<AgentMessage> GetEnumerator()
        {
            while (true)
            {
                YieldCount++;
                yield return new AgentMessage(
                    AgentMessageRole.System,
                    $"system-{YieldCount}");
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
