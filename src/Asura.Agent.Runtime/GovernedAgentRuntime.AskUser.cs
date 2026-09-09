using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private static readonly TimeSpan QuestionLifetime =
        TimeSpan.FromMinutes(2);

    private QuestionAwaiter? _questionAwaiter;

    public async ValueTask<GovernedAgentQuestionResponseResult>
        RespondToQuestionAsync(
            AgentQuestionId questionId,
            GovernedAgentQuestionResponse response,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();

        QuestionAwaiter? awaiter;
        var expired = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            awaiter = _questionAwaiter;
            if (awaiter is null || awaiter.Question.Id != questionId)
            {
                return QuestionResponseFailure(
                    "question_not_found",
                    "That agent question is no longer pending.");
            }

            if (awaiter.ResponseStarted)
            {
                return QuestionResponseFailure(
                    "question_response_pending",
                    "A response to that agent question is already being applied.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            awaiter.ResponseStarted = true;
            expired = _timeProvider.GetUtcNow().ToUniversalTime()
                >= awaiter.Question.ExpiresAtUtc;
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.StreamingProvider,
                PendingQuestion = null,
                Status = expired
                    ? "That question expired before the response was accepted."
                    : response is GovernedAgentQuestionResponse.Declined
                        ? "Question skipped; continuing the provider turn…"
                        : "Answer accepted; checking the run target…",
            };
            awaiter.Response.TrySetResult(expired ? null : response);
        }

        NotifyChanged();
        if (expired)
        {
            return QuestionResponseFailure(
                "question_expired",
                "That agent question expired.");
        }

        // The response claim above is a one-way local commit. A caller-token
        // cancellation after it cannot make a retry safe, so only the
        // whole-turn lifecycle may complete this application result.
        return await awaiter.Applied.Task.ConfigureAwait(false);
    }

    private async ValueTask<AgentToolResult> ExecuteAskUserAsync(
        AgentToolProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var parsed = AgentAskUserIntrinsic.Parse(
            proposal,
            AgentQuestionId.New(),
            now + QuestionLifetime);
        if (parsed is AgentAskUserParseResult.Rejected rejected)
        {
            return CreateIntrinsicFailureResult(
                proposal,
                rejected.StableCode);
        }

        var contexts = await InspectRunTargetContextAsync(
                GetPinnedTarget(),
                GetOrCreateAgent(),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (contexts is null || !MatchesPinnedScope(contexts))
        {
            return CreateIntrinsicFailureResult(proposal, "target_changed");
        }

        var question =
            ((AgentAskUserParseResult.Parsed)parsed).Question;
        var awaiter = new QuestionAwaiter(question);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnCancellation is null
                || _snapshot.State != GovernedAgentState.StreamingProvider
                || _questionAwaiter is not null)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _questionAwaiter = awaiter;
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.AwaitingUserInput,
                PendingQuestion = question,
                PendingApproval = null,
                ActiveTool = null,
                CurrentProgress = null,
                ProvisionalAssistantText = string.Empty,
                ProvisionalReasoningSummary = string.Empty,
                Status = "Waiting for your non-sensitive clarification…",
            };
        }

        NotifyChanged();

        GovernedAgentQuestionResponse? response;
        try
        {
            response = await AwaitQuestionResponseAsync(
                    awaiter,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CancelQuestionAwaiter(
                awaiter,
                "question_cancelled",
                "The agent question was cancelled.");
            throw;
        }

        if (response is null)
        {
            CompleteQuestionAwaiter(
                awaiter,
                QuestionResponseFailure(
                    "question_expired",
                    "That agent question expired."),
                "Question expired; returning that result to the provider…");
            return CreateIntrinsicFailureResult(
                proposal,
                "user_input_expired");
        }

        contexts = await InspectRunTargetContextAsync(
                GetPinnedTarget(),
                GetOrCreateAgent(),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (contexts is null || !MatchesPinnedScope(contexts))
        {
            CompleteQuestionAwaiter(
                awaiter,
                QuestionResponseFailure(
                    "target_changed",
                    "The run target changed before the response could be applied."),
                "The run target changed; the response was discarded.");
            return CreateIntrinsicFailureResult(proposal, "target_changed");
        }

        if (response is GovernedAgentQuestionResponse.Declined)
        {
            CompleteQuestionAwaiter(
                awaiter,
                new GovernedAgentQuestionResponseResult(
                    true,
                    "question_declined",
                    "The question was skipped."),
                "Question skipped; returning that result to the provider…");
            return CreateIntrinsicFailureResult(
                proposal,
                "user_input_declined");
        }

        var submitted =
            (GovernedAgentQuestionResponse.Submitted)response;
        var result = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "tool_succeeded",
            JsonValue(SuccessJson(submitted.Answer)));
        CompleteQuestionAwaiter(
            awaiter,
            new GovernedAgentQuestionResponseResult(
                true,
                "question_answered",
                "The answer was accepted; the agent is continuing."),
            "Answer accepted; returning it to the provider…");
        return result;
    }

    private async ValueTask<GovernedAgentQuestionResponse?>
        AwaitQuestionResponseAsync(
            QuestionAwaiter awaiter,
            CancellationToken cancellationToken)
    {
        var remaining = awaiter.Question.ExpiresAtUtc
            - _timeProvider.GetUtcNow().ToUniversalTime();
        if (remaining <= TimeSpan.Zero)
        {
            ExpireQuestionAwaiter(awaiter);
            return null;
        }

        try
        {
            return await awaiter.Response.Task
                .WaitAsync(
                    remaining,
                    _timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ExpireQuestionAwaiter(awaiter);
            return await awaiter.Response.Task.ConfigureAwait(false);
        }
    }

    private void ExpireQuestionAwaiter(QuestionAwaiter awaiter)
    {
        var notify = false;
        lock (_gate)
        {
            if (ReferenceEquals(_questionAwaiter, awaiter)
                && !awaiter.ResponseStarted)
            {
                awaiter.ResponseStarted = true;
                awaiter.Response.TrySetResult(null);
                _snapshot = _snapshot with
                {
                    State = GovernedAgentState.StreamingProvider,
                    PendingQuestion = null,
                    Status =
                        "Question expired; returning that result to the provider…",
                };
                notify = true;
            }
        }

        if (notify)
        {
            NotifyChanged();
        }
    }

    private void CompleteQuestionAwaiter(
        QuestionAwaiter awaiter,
        GovernedAgentQuestionResponseResult result,
        string status)
    {
        var notify = false;
        lock (_gate)
        {
            if (ReferenceEquals(_questionAwaiter, awaiter))
            {
                _questionAwaiter = null;
                _snapshot = _snapshot with
                {
                    State = GovernedAgentState.StreamingProvider,
                    PendingQuestion = null,
                    Status = status,
                };
                notify = true;
            }
        }

        awaiter.Applied.TrySetResult(result);
        if (notify)
        {
            NotifyChanged();
        }
    }

    private void CancelQuestionAwaiter(
        QuestionAwaiter awaiter,
        string stableCode,
        string message)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_questionAwaiter, awaiter))
            {
                _questionAwaiter = null;
                _snapshot = _snapshot with
                {
                    PendingQuestion = null,
                };
            }
        }

        awaiter.Response.TrySetCanceled();
        awaiter.Applied.TrySetResult(
            QuestionResponseFailure(stableCode, message));
    }

    private QuestionAwaiter? DetachQuestionAwaiterUnsafe()
    {
        var awaiter = _questionAwaiter;
        _questionAwaiter = null;
        return awaiter;
    }

    private static void CancelDetachedQuestionAwaiter(
        QuestionAwaiter? awaiter,
        string stableCode,
        string message)
    {
        if (awaiter is null)
        {
            return;
        }

        awaiter.Response.TrySetCanceled();
        awaiter.Applied.TrySetResult(
            QuestionResponseFailure(stableCode, message));
    }

    private static GovernedAgentQuestionResponseResult
        QuestionResponseFailure(
            string stableCode,
            string message) =>
        new(false, stableCode, message);

    private static string SuccessJson(string answer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString(
            "content_origin",
            GovernedAgentQuestionResponse.UserContentOrigin);
        writer.WriteString("answer", answer);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class QuestionAwaiter(GovernedAgentQuestion question)
    {
        public GovernedAgentQuestion Question { get; } = question;

        public TaskCompletionSource<GovernedAgentQuestionResponse?> Response
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<GovernedAgentQuestionResponseResult> Applied
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ResponseStarted { get; set; }
    }
}
