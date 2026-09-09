using System.Text;

namespace Asura.Agent;

public sealed partial class NativeAgentSession
{
    private const string SteeringUpdateSeparator = "\n\nSteering update:\n";

    /// <summary>
    /// Replaces one active initial user generation before it commits. The
    /// original turn owner continues with the replacement, so steering never
    /// creates a second user turn or transfers provider ownership to the caller.
    /// </summary>
    public AgentSteerResult Steer(long expectedGeneration, string update)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(update);
        if (Encoding.UTF8.GetByteCount(update)
            > _limits.MaximumAssistantTextBytes)
        {
            return AgentSteerResult.Failure(AgentSteerErrorCode.LimitExceeded);
        }

        ActiveTurn superseded;
        ActiveTurn replacement;
        AgentMessage replacementUser;
        lock (_gate)
        {
            if (_activeTurn is not { } activeTurn)
            {
                return AgentSteerResult.Failure(AgentSteerErrorCode.NoActiveTurn);
            }

            if (activeTurn.Generation != expectedGeneration)
            {
                return AgentSteerResult.Failure(
                    AgentSteerErrorCode.GenerationMismatch);
            }

            if (activeTurn.Kind == ActiveTurnKind.ToolContinuation)
            {
                return AgentSteerResult.Failure(
                    AgentSteerErrorCode.NotInitialUserTurn);
            }

            if (activeTurn.Kind == ActiveTurnKind.SteeredUser)
            {
                return AgentSteerResult.Failure(AgentSteerErrorCode.AlreadySteered);
            }

            if (_conversationRevision != activeTurn.BaseConversationRevision
                || !_conversation.Equals(activeTurn.BaseConversation))
            {
                return AgentSteerResult.Failure(
                    AgentSteerErrorCode.ConversationConflict);
            }

            if (_providerOperationsInFlight >= _limits.MaximumConcurrentProviderOperations)
            {
                return AgentSteerResult.Failure(
                    AgentSteerErrorCode.ProviderOperationLimit);
            }

            var originalUser = AssertInitialUserInput(activeTurn);
            replacementUser = new AgentMessage(
                AgentMessageRole.User,
                string.Concat(
                    originalUser.Content,
                    SteeringUpdateSeparator,
                    update));
            var replacementConversation = activeTurn.BaseConversation.Add(replacementUser);
            try
            {
                ValidateMessageBounds(
                    replacementUser,
                    _limits.MaximumAssistantTextBytes);
                ValidateConversation(
                    replacementConversation,
                    ConversationTail.User);
            }
            catch (AgentLimitException)
            {
                return AgentSteerResult.Failure(AgentSteerErrorCode.LimitExceeded);
            }
            catch (AgentConversationException)
            {
                return AgentSteerResult.Failure(
                    AgentSteerErrorCode.ConversationConflict);
            }

            var replacementGeneration = checked(_generation + 1);
            replacement = new ActiveTurn(
                replacementGeneration,
                activeTurn.BaseConversationRevision,
                activeTurn.BaseConversation,
                [replacementUser],
                activeTurn.Tools,
                activeTurn.ReasoningEffort,
                ActiveTurnKind.SteeredUser);

            // Steer and commit both linearize under this gate. The extra provider slot
            // is reserved before the old generation is fenced because its adapter may
            // ignore cancellation indefinitely.
            superseded = activeTurn;
            superseded.SetSteeringReplacement(replacement);
            _generation = replacementGeneration;
            _activeTurn = replacement;
            _providerOperationsInFlight = checked(_providerOperationsInFlight + 1);
            _state = NativeAgentSessionState.Streaming;
            superseded.SignalCancellation();
            AppendEventUnsafe(
                AgentRunEventKind.TurnSteered,
                superseded.Generation);
            AppendEventUnsafe(
                AgentRunEventKind.TurnStarted,
                replacement.Generation);
        }

        superseded.TryCancel();
        return AgentSteerResult.Success(
            replacement.Generation,
            replacementUser.Content);
    }

    private static AgentMessage AssertInitialUserInput(ActiveTurn activeTurn)
    {
        if (activeTurn.InputMessages is not [var user]
            || user.Role != AgentMessageRole.User
            || user.ToolCalls.Length > 0
            || user.ToolResult is not null)
        {
            throw new InvalidOperationException(
                "An initial user generation must retain one plain user message.");
        }

        return user;
    }
}
