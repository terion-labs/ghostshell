using System.Collections.Concurrent;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    private static readonly DateTimeOffset QuestionTestNow =
        new(2026, 7, 25, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AskUserIsAlwaysAdvertisedWithAClosedBoundedSchema()
    {
        var provider = new ProviderRound((_, _) => Answer("Done."));
        await using var fixture = new RuntimeFixture(provider);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var tools = Assert.Single(provider.Requests).Tools;
        var tool = Assert.Single(
            tools,
            candidate => string.Equals(candidate.Name, IntrinsicAgentTools.AskUser, StringComparison.Ordinal));
        Assert.Contains(
            "Never request credentials",
            tool.Description,
            StringComparison.Ordinal);
        Assert.Contains(
            "never authorizes",
            tool.Description,
            StringComparison.Ordinal);
        var schema = tool.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["question"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);

        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["question"],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        var question = properties.GetProperty("question");
        Assert.Equal("string", question.GetProperty("type").GetString());
        Assert.Equal(1, question.GetProperty("minLength").GetInt32());
        Assert.Equal(
            GovernedAgentQuestion.MaximumQuestionBytes,
            question.GetProperty("maxLength").GetInt32());
        Assert.Single(
            tools,
            candidate => string.Equals(candidate.Name
, IntrinsicAgentTools.ReportProgress, StringComparison.Ordinal));
    }

    [Fact]
    public void AskUserParserProjectsOnlyValidatedDomainContent()
    {
        using var document = JsonDocument.Parse(
            """{"question":"Which deployment region should I inspect?"}""");
        var id = new AgentQuestionId("question-1");
        var expiry = QuestionTestNow.AddMinutes(2);

        var parsed = Assert.IsType<AgentAskUserParseResult.Parsed>(
            AgentAskUserIntrinsic.Parse(
                document.RootElement,
                id,
                expiry));

        Assert.Equal(id, parsed.Question.Id);
        Assert.Equal(
            "Which deployment region should I inspect?",
            parsed.Question.Question);
        Assert.Equal(expiry, parsed.Question.ExpiresAtUtc);
        Assert.Equal(
            GovernedAgentQuestion.UntrustedModelContentOrigin,
            parsed.Question.ContentOrigin);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"question":null}""")]
    [InlineData("""{"question":42}""")]
    [InlineData("""{"question":"Where?","extra":true}""")]
    [InlineData("""{"question":"one","question":"two"}""")]
    [InlineData("""{"Question":"Where?"}""")]
    [InlineData("""{"question":"first\nsecond"}""")]
    [InlineData("""{"question":"token=literal-question-secret"}""")]
    public void AskUserParserRejectsUnknownDuplicateOrUnsafeInput(
        string json)
    {
        using var document = JsonDocument.Parse(json);

        var rejected = Assert.IsType<AgentAskUserParseResult.Rejected>(
            AgentAskUserIntrinsic.Parse(
                document.RootElement,
                new AgentQuestionId("question-1"),
                QuestionTestNow.AddMinutes(2)));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void AskUserParserRejectsInvalidIdentityExpiryAndUtf8Overflow()
    {
        var oversized = string.Concat(
            new string('\u00E9', 512),
            "x");
        using var oversizedDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                question = oversized,
            }));
        using var validDocument = JsonDocument.Parse(
            """{"question":"Which region?"}""");

        Assert.IsType<AgentAskUserParseResult.Rejected>(
            AgentAskUserIntrinsic.Parse(
                oversizedDocument.RootElement,
                new AgentQuestionId("question-1"),
                QuestionTestNow.AddMinutes(2)));
        Assert.IsType<AgentAskUserParseResult.Rejected>(
            AgentAskUserIntrinsic.Parse(
                validDocument.RootElement,
                default,
                QuestionTestNow.AddMinutes(2)));
        Assert.IsType<AgentAskUserParseResult.Rejected>(
            AgentAskUserIntrinsic.Parse(
                validDocument.RootElement,
                new AgentQuestionId("question-1"),
                QuestionTestNow
                    .AddMinutes(2)
                    .ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public async Task SubmittedAnswerIsEscapedContinuedAndNeverBrokered()
    {
        const string question =
            "Which non-sensitive deployment label should I inspect?";
        const string answer = "staging \"blue\" \\\\ path";
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "ask-1",
                IntrinsicAgentTools.AskUser,
                JsonSerializer.Serialize(new
                {
                    question,
                })),
            2 => Answer("The staging deployment is healthy."),
            _ => throw new InvalidOperationException(
                "The ask-user provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);
        ConcurrentQueue<GovernedAgentState> observedStates = [];
        fixture.Runtime.Changed += (_, _) =>
            observedStates.Enqueue(fixture.Runtime.Snapshot.State);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(
            () => fixture.Runtime.Snapshot.State
                == GovernedAgentState.AwaitingUserInput);

        var pending = Assert.IsType<GovernedAgentQuestion>(
            fixture.Runtime.Snapshot.PendingQuestion);
        Assert.Equal(question, pending.Question);
        Assert.Equal(
            GovernedAgentQuestion.UntrustedModelContentOrigin,
            pending.ContentOrigin);
        Assert.InRange(
            pending.ExpiresAtUtc - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2));
        Assert.True(fixture.Runtime.Snapshot.IsBusy);
        Assert.False(fixture.Runtime.Snapshot.CanSend);
        Assert.Null(fixture.Runtime.Snapshot.PendingApproval);
        Assert.Null(fixture.Runtime.Snapshot.ActiveTool);
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.Empty(fixture.Runtime.Snapshot.ProvisionalAssistantText);

        var response = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted(answer),
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.IsAccepted);
        Assert.Equal("question_answered", response.Code);
        Assert.True(result.IsSuccess);
        Assert.Equal("agent_turn_completed", result.Code);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        Assert.Contains(
            GovernedAgentState.AwaitingUserInput,
            observedStates);
        Assert.Equal(
            [
                ("Inspect the deployment.", AgentChatMessageRole.User),
                (question, AgentChatMessageRole.Assistant),
                (answer, AgentChatMessageRole.User),
                (
                    "The staging deployment is healthy.",
                    AgentChatMessageRole.Assistant),
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                (message.Content, message.Role)));

        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId, "ask-1", StringComparison.Ordinal))
            .ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal(AgentToolResultStatus.Succeeded, toolResult.Status);
        Assert.Equal("tool_succeeded", toolResult.StableCode);
        using var resultDocument = JsonDocument.Parse(
            toolResult.Value.Content);
        Assert.True(resultDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            GovernedAgentQuestionResponse.UserContentOrigin,
            resultDocument.RootElement
                .GetProperty("content_origin")
                .GetString());
        Assert.Equal(
            answer,
            resultDocument.RootElement.GetProperty("answer").GetString());
        Assert.Equal(
            3,
            resultDocument.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain(
            question,
            toolResult.Value.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            pending.Id.Value,
            toolResult.Value.Content,
            StringComparison.Ordinal);
        Assert.Equal(3, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Terminal.Permits);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task DeclinedQuestionReturnsAReceiptAndProviderContinues()
    {
        var provider = AskThenAnswerProvider(
            "ask-declined",
            "Which region?",
            "I continued without the optional region.");
        await using var fixture = new RuntimeFixture(provider);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        var response = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Declined(),
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.IsAccepted);
        Assert.Equal("question_declined", response.Code);
        Assert.True(result.IsSuccess);
        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "ask-declined", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal(AgentToolResultStatus.Failed, toolResult.Status);
        Assert.Equal("user_input_declined", toolResult.StableCode);
        Assert.Equal(
            """{"ok":false,"error":{"code":"user_input_declined","retryable":false}}""",
            toolResult.Value.Content);
        Assert.Equal(
            [
                "Inspect the deployment.",
                "I continued without the optional region.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task InvalidQuestionReturnsAClosedFailureWithoutPublishingIt()
    {
        const string invalidQuestion = "Which region?";
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "ask-invalid",
                IntrinsicAgentTools.AskUser,
                $$"""
                {
                  "question": "{{invalidQuestion}}",
                  "approval": true
                }
                """),
            2 => Answer("The invalid question was rejected."),
            _ => throw new InvalidOperationException(
                "The invalid-question provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);
        var publishedQuestion = false;
        fixture.Runtime.Changed += (_, _) =>
            publishedQuestion |=
                fixture.Runtime.Snapshot.PendingQuestion is not null;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(publishedQuestion);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        Assert.Equal(
            [
                "Inspect the deployment.",
                "The invalid question was rejected.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "ask-invalid", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal("invalid_tool_arguments", toolResult.StableCode);
        Assert.DoesNotContain(
            invalidQuestion,
            toolResult.Value.Content,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task ProviderFailureAfterCommitPreservesTheVisibleQuestionAnswerPair()
    {
        const string question = "Which region?";
        const string answer = "staging";
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "ask-before-provider-failure",
                IntrinsicAgentTools.AskUser,
                JsonSerializer.Serialize(new
                {
                    question,
                })),
            2 => [new AgentProviderEvent.ResponseStarted()],
            _ => throw new InvalidOperationException(
                "The failing provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        var response = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted(answer),
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.IsAccepted);
        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_provider_stream", result.Code);
        Assert.Equal(
            GovernedAgentState.Failed,
            fixture.Runtime.Snapshot.State);
        Assert.Equal(
            [
                ("Inspect the deployment.", AgentChatMessageRole.User),
                (question, AgentChatMessageRole.Assistant),
                (answer, AgentChatMessageRole.User),
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                (message.Content, message.Role)));
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task WrongAndDoubleQuestionIdsCannotReplaceAClaimedAnswer()
    {
        var provider = AskThenAnswerProvider(
            "ask-single-response",
            "Which region?",
            "The answer was applied once.");
        await using var fixture = new RuntimeFixture(provider);
        fixture.Context.BlockInspectionNumber = 3;
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);
        using var responseCancellation = new CancellationTokenSource();

        var responding = fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("staging"),
            responseCancellation.Token).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var wrongId = await fixture.Runtime.RespondToQuestionAsync(
            new AgentQuestionId("different-question"),
            new GovernedAgentQuestionResponse.Submitted("production"),
            CancellationToken.None);
        var duplicate = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("production"),
            CancellationToken.None);
        Assert.False(wrongId.IsAccepted);
        Assert.Equal("question_not_found", wrongId.Code);
        Assert.False(duplicate.IsAccepted);
        Assert.Equal("question_response_pending", duplicate.Code);

        responseCancellation.Cancel();
        fixture.Context.ReleaseInspection.TrySetResult();
        var accepted = await responding.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(accepted.IsAccepted);
        Assert.Equal("question_answered", accepted.Code);
        Assert.True(result.IsSuccess);
        var alreadyApplied = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("production"),
            CancellationToken.None);
        Assert.False(alreadyApplied.IsAccepted);
        Assert.Equal("question_not_found", alreadyApplied.Code);
        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "ask-single-response", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(toolResult);
        using var resultDocument = JsonDocument.Parse(
            toolResult.Value.Content);
        Assert.Equal(
            "staging",
            resultDocument.RootElement.GetProperty("answer").GetString());
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task EveryQuestionUsesAFreshIdAndRejectsAStalePriorId()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "ask-first",
                IntrinsicAgentTools.AskUser,
                """{"question":"Which region?"}"""),
            2 => ToolCall(
                "ask-second",
                IntrinsicAgentTools.AskUser,
                """{"question":"Which service?"}"""),
            3 => Answer("The staging API is healthy."),
            _ => throw new InvalidOperationException(
                "The two-question provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var first = await WaitForQuestionAsync(fixture.Runtime);

        Assert.True((await fixture.Runtime.RespondToQuestionAsync(
            first.Id,
            new GovernedAgentQuestionResponse.Submitted("staging"),
            CancellationToken.None)).IsAccepted);
        var second = await WaitForQuestionAsync(fixture.Runtime);

        Assert.NotEqual(first.Id, second.Id);
        var stale = await fixture.Runtime.RespondToQuestionAsync(
            first.Id,
            new GovernedAgentQuestionResponse.Submitted("web"),
            CancellationToken.None);
        Assert.False(stale.IsAccepted);
        Assert.Equal("question_not_found", stale.Code);
        Assert.Same(second, fixture.Runtime.Snapshot.PendingQuestion);
        Assert.True((await fixture.Runtime.RespondToQuestionAsync(
            second.Id,
            new GovernedAgentQuestionResponse.Submitted("api"),
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(
            [
                "Inspect the deployment.",
                "Which region?",
                "staging",
                "Which service?",
                "api",
                "The staging API is healthy.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
        Assert.Equal(5, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task PreClaimCancellationAndUnrelatedCommandsLeaveQuestionPending()
    {
        var provider = AskThenAnswerProvider(
            "ask-pre-claim-cancel",
            "Which region?",
            "The question was skipped.");
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);
        using var responseCancellation = new CancellationTokenSource();
        responseCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Runtime.RespondToQuestionAsync(
                    pending.Id,
                    new GovernedAgentQuestionResponse.Submitted("staging"),
                    responseCancellation.Token)
                .AsTask());
        var actionCancellation =
            await fixture.Runtime.CancelActiveActionAsync(
                CancellationToken.None);
        var competingSend = await fixture.Runtime.SendAsync(
            fixture.Prompt("This must not replace the pending question."),
            CancellationToken.None);

        Assert.False(actionCancellation.WasRequested);
        Assert.Equal(
            "agent_action_not_running",
            actionCancellation.Code);
        Assert.False(competingSend.IsSuccess);
        Assert.Equal("agent_busy", competingSend.Code);
        Assert.Equal(
            GovernedAgentState.AwaitingUserInput,
            fixture.Runtime.Snapshot.State);
        Assert.Same(pending, fixture.Runtime.Snapshot.PendingQuestion);
        Assert.True((await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Declined(),
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task InvalidSecretAnswerCannotClaimThePendingQuestion()
    {
        var provider = AskThenAnswerProvider(
            "ask-secret",
            "Which non-sensitive label?",
            "The question was skipped.");
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentQuestionResponse.Submitted(
                "token=literal-answer-secret"));

        Assert.Equal("answer", error.ParamName);
        Assert.Equal(
            GovernedAgentState.AwaitingUserInput,
            fixture.Runtime.Snapshot.State);
        Assert.Same(pending, fixture.Runtime.Snapshot.PendingQuestion);
        Assert.True((await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Declined(),
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task AskUserRejectsTargetDriftBeforePublishingAQuestion()
    {
        var provider = AskThenAnswerProvider(
            "ask-pre-drift",
            "Which region?",
            "The target changed.");
        await using var fixture = new RuntimeFixture(provider);
        fixture.Context.ReplaceSessionAfterInspection = 1;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "ask-pre-drift", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal("target_changed", toolResult.StableCode);
        Assert.Equal(
            [
                "Inspect the deployment.",
                "The target changed.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
        Assert.Equal(2, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task AskUserDiscardsAnAnswerAfterTargetDrift()
    {
        const string answer = "staging";
        var provider = AskThenAnswerProvider(
            "ask-post-drift",
            "Which region?",
            "The target changed.");
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);
        Assert.Equal(2, fixture.Context.InspectionCount);
        fixture.Context.ReplaceSessionAfterInspection = 2;

        var response = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted(answer),
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(response.IsAccepted);
        Assert.Equal("target_changed", response.Code);
        Assert.True(result.IsSuccess);
        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "ask-post-drift", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal("target_changed", toolResult.StableCode);
        Assert.DoesNotContain(
            answer,
            toolResult.Value.Content,
            StringComparison.Ordinal);
        Assert.Equal(
            [
                "Inspect the deployment.",
                "The target changed.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
        Assert.Equal(3, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task CallerCancellationClearsThePendingQuestion()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "ask-cancelled",
                IntrinsicAgentTools.AskUser,
                """{"question":"Which region?"}"""),
            _ => throw new InvalidOperationException(
                "Cancellation must prevent provider continuation."),
        });
        await using var fixture = new RuntimeFixture(provider);
        using var cancellation = new CancellationTokenSource();
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            cancellation.Token).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        cancellation.Cancel();
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Equal(
            GovernedAgentState.Cancelled,
            fixture.Runtime.Snapshot.State);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        var lateResponse = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Declined(),
            CancellationToken.None);
        Assert.False(lateResponse.IsAccepted);
        Assert.Equal("question_not_found", lateResponse.Code);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "Which region?",
                StringComparison.Ordinal));
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task StopCancelsTheQuestionAndProviderTurn()
    {
        var provider = BlockingAskProvider("ask-stop");
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        var stopped = await fixture.Runtime.StopAsync(
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped.WasRunning);
        Assert.Equal("agent_stopped", stopped.Code);
        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Equal(
            GovernedAgentState.Cancelled,
            fixture.Runtime.Snapshot.State);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        var lateResponse = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Declined(),
            CancellationToken.None);
        Assert.False(lateResponse.IsAccepted);
        Assert.Equal("question_not_found", lateResponse.Code);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "Which region?",
                StringComparison.Ordinal));
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task
        StopWithFaultingCancellationCallbackDiscardsAClaimedAnswer()
    {
        var provider = BlockingAskProvider("ask-stop-after-claim");
        await using var fixture = new RuntimeFixture(provider);
        fixture.Context.BlockInspectionNumber = 3;
        fixture.Context.IgnoreBlockedInspectionCancellation = true;
        fixture.Context.ThrowFromTurnCancellationCallback = true;
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);
        var responding = fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("staging"),
            CancellationToken.None).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var stopped = await fixture.Runtime.StopAsync(
            CancellationToken.None);
        Assert.Equal(
            1,
            fixture.Context.FaultingCancellationCallbackCount);
        Assert.False(sending.IsCompleted);
        fixture.Context.ReleaseInspection.TrySetResult();
        var response = await responding.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped.WasRunning);
        Assert.False(response.IsAccepted);
        Assert.Equal("question_cancelled", response.Code);
        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "Which region?",
                StringComparison.Ordinal)
                || message.Content.Contains(
                    "staging",
                    StringComparison.Ordinal));
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Terminal.Permits);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task DisposeCancelsTheQuestionAndProviderTurn()
    {
        var provider = BlockingAskProvider("ask-dispose");
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        await fixture.Runtime.DisposeAsync();
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Runtime.RespondToQuestionAsync(
                    pending.Id,
                    new GovernedAgentQuestionResponse.Declined(),
                    CancellationToken.None)
                .AsTask());
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "Which region?",
                StringComparison.Ordinal));
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task
        DisposeWithFaultingCancellationCallbackDiscardsAClaimedAnswer()
    {
        var provider = BlockingAskProvider("ask-dispose-after-claim");
        await using var fixture = new RuntimeFixture(provider);
        fixture.Context.BlockInspectionNumber = 3;
        fixture.Context.IgnoreBlockedInspectionCancellation = true;
        fixture.Context.ThrowFromTurnCancellationCallback = true;
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);
        var responding = fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("staging"),
            CancellationToken.None).AsTask();
        await fixture.Context.BlockedInspection.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        await fixture.Runtime.DisposeAsync();
        Assert.Equal(
            1,
            fixture.Context.FaultingCancellationCallbackCount);
        Assert.False(sending.IsCompleted);
        fixture.Context.ReleaseInspection.TrySetResult();
        var response = await responding.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(response.IsAccepted);
        Assert.Equal("question_cancelled", response.Code);
        Assert.False(result.IsSuccess);
        Assert.Equal("agent_cancelled", result.Code);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        Assert.DoesNotContain(
            fixture.Runtime.Snapshot.Messages,
            message => message.Content.Contains(
                "Which region?",
                StringComparison.Ordinal)
                || message.Content.Contains(
                    "staging",
                    StringComparison.Ordinal));
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Terminal.Permits);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task QuestionExpiresAndProviderReceivesOnlyAnExpiryReceipt()
    {
        var time = new ManualQuestionTimeProvider(QuestionTestNow);
        var provider = AskThenAnswerProvider(
            "ask-expired",
            "Which region?",
            "The question expired.");
        await using var fixture = new RuntimeFixture(
            provider,
            timeProvider: time);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);
        await WaitUntilAsync(() => time.ActiveTimerCount > 0);

        time.Advance(TimeSpan.FromMinutes(2));
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(QuestionTestNow.AddMinutes(2), pending.ExpiresAtUtc);
        Assert.Null(fixture.Runtime.Snapshot.PendingQuestion);
        var toolResult = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "ask-expired", StringComparison.Ordinal)).ToolResult;
        Assert.NotNull(toolResult);
        Assert.Equal(AgentToolResultStatus.Failed, toolResult.Status);
        Assert.Equal("user_input_expired", toolResult.StableCode);
        Assert.Equal(
            """{"ok":false,"error":{"code":"user_input_expired","retryable":false}}""",
            toolResult.Value.Content);
        var lateResponse = await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("staging"),
            CancellationToken.None);
        Assert.False(lateResponse.IsAccepted);
        Assert.Equal("question_not_found", lateResponse.Code);
        Assert.Equal(
            [
                "Inspect the deployment.",
                "The question expired.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message =>
                message.Content), StringComparer.Ordinal);
        Assert.Equal(2, fixture.Context.InspectionCount);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    [Fact]
    public async Task AskUserAndReportProgressCoexistAcrossOneProviderTurn()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Progress("progress-before-question", "Inspecting hosts", 20),
            2 => ToolCall(
                "ask-after-progress",
                IntrinsicAgentTools.AskUser,
                """{"question":"Which region?"}"""),
            3 => Progress("progress-after-question", "Inspecting staging", 80),
            4 => Answer("Staging is healthy."),
            _ => throw new InvalidOperationException(
                "The intrinsic provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(provider);
        ConcurrentQueue<GovernedAgentProgress> observed = [];
        fixture.Runtime.Changed += (_, _) =>
        {
            if (fixture.Runtime.Snapshot.CurrentProgress is { } progress)
            {
                observed.Enqueue(progress);
            }
        };

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the deployment."),
            CancellationToken.None).AsTask();
        var pending = await WaitForQuestionAsync(fixture.Runtime);

        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.True((await fixture.Runtime.RespondToQuestionAsync(
            pending.Id,
            new GovernedAgentQuestionResponse.Submitted("staging"),
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        Assert.Contains(
            observed,
            progress => progress is
            {
                Message: "Inspecting hosts",
                Percent: 20,
            });
        Assert.Contains(
            observed,
            progress => progress is
            {
                Message: "Inspecting staging",
                Percent: 80,
            });
        Assert.Null(fixture.Runtime.Snapshot.CurrentProgress);
        Assert.All(
            provider.Requests,
            request =>
            {
                Assert.Single(
                    request.Tools,
                    tool => string.Equals(tool.Name, IntrinsicAgentTools.AskUser, StringComparison.Ordinal));
                Assert.Single(
                    request.Tools,
                    tool => string.Equals(tool.Name
, IntrinsicAgentTools.ReportProgress, StringComparison.Ordinal));
            });
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    private static ProviderRound AskThenAnswerProvider(
        string callId,
        string question,
        string answer) =>
        new((call, _) => call switch
        {
            1 => ToolCall(
                callId,
                IntrinsicAgentTools.AskUser,
                JsonSerializer.Serialize(new
                {
                    question,
                })),
            2 => Answer(answer),
            _ => throw new InvalidOperationException(
                "The ask-user provider received an unexpected round."),
        });

    private static ProviderRound BlockingAskProvider(string callId) =>
        new((call, _) => call switch
        {
            1 => ToolCall(
                callId,
                IntrinsicAgentTools.AskUser,
                """{"question":"Which region?"}"""),
            _ => throw new InvalidOperationException(
                "The cancelled question must not continue the provider."),
        });

    private static async ValueTask<GovernedAgentQuestion>
        WaitForQuestionAsync(GovernedAgentRuntime runtime)
    {
        GovernedAgentQuestion? question = null;
        await WaitUntilAsync(
            () =>
            {
                question = runtime.Snapshot.PendingQuestion;
                return runtime.Snapshot.State
                        == GovernedAgentState.AwaitingUserInput
                    && question is not null;
            });
        return question!;
    }

    private sealed class ManualQuestionTimeProvider(
        DateTimeOffset initialUtc) : TimeProvider
    {
        private readonly object _timerGate = new();
        private readonly List<ManualQuestionTimer> _timers = [];
        private DateTimeOffset _utcNow = initialUtc;

        public int ActiveTimerCount
        {
            get
            {
                lock (_timerGate)
                {
                    return _timers.Count(timer => timer.IsActive);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timerGate)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualQuestionTimer(
                this,
                callback,
                state);
            lock (_timerGate)
            {
                _timers.Add(timer);
                ChangeUnsafe(timer, dueTime, period);
            }

            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                elapsed,
                TimeSpan.Zero);
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_timerGate)
            {
                _utcNow += elapsed;
                foreach (var timer in _timers.Where(
                             timer => timer.IsDue(_utcNow)).ToArray())
                {
                    callbacks.Add((timer.Callback, timer.State));
                    timer.AdvanceAfterFire(_utcNow);
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private bool Change(
            ManualQuestionTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_timerGate)
            {
                if (!_timers.Contains(timer) || timer.IsDisposed)
                {
                    return false;
                }

                ChangeUnsafe(timer, dueTime, period);
                return true;
            }
        }

        private void ChangeUnsafe(
            ManualQuestionTimer timer,
            TimeSpan dueTime,
            TimeSpan period)
        {
            timer.Change(
                dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _utcNow + dueTime,
                period);
        }

        private void Remove(ManualQuestionTimer timer)
        {
            lock (_timerGate)
            {
                timer.MarkDisposed();
                _timers.Remove(timer);
            }
        }

        private sealed class ManualQuestionTimer(
            ManualQuestionTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAtUtc;
            private TimeSpan _period;

            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public bool IsDisposed { get; private set; }

            public bool IsActive => !IsDisposed && _dueAtUtc is not null;

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                owner.Change(this, dueTime, period);

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool IsDue(DateTimeOffset now) =>
                IsActive && _dueAtUtc <= now;

            public void AdvanceAfterFire(DateTimeOffset now)
            {
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _dueAtUtc = null;
                    return;
                }

                _dueAtUtc = now + _period;
            }

            public void Change(
                DateTimeOffset? dueAtUtc,
                TimeSpan period)
            {
                _dueAtUtc = dueAtUtc;
                _period = period;
            }

            public void MarkDisposed()
            {
                IsDisposed = true;
                _dueAtUtc = null;
            }
        }
    }
}
