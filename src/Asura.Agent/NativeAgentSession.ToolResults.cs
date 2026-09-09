using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Agent;

public sealed partial class NativeAgentSession
{
    private long _lastSubmittedToolGeneration;

    internal ValueTask<AgentTurnResult> SubmitToolResultsAsync(
        long proposalGeneration,
        ImmutableArray<AgentToolResult> results,
        ImmutableArray<AgentToolDefinition> tools,
        IAgentProvider provider,
        CancellationToken cancellationToken) =>
        SubmitToolResultsAsync(
            proposalGeneration,
            results,
            tools,
            tools,
            provider,
            cancellationToken);

    /// <summary>
    /// Commits results against the exact tool manifest that produced the
    /// pending proposals, then starts the continuation with a separately
    /// validated manifest. This is the only manifest-change boundary inside a
    /// structured provider turn.
    /// </summary>
    public async ValueTask<AgentTurnResult> SubmitToolResultsAsync(
        long proposalGeneration,
        ImmutableArray<AgentToolResult> results,
        ImmutableArray<AgentToolDefinition> proposalTools,
        ImmutableArray<AgentToolDefinition> continuationTools,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (continuationTools.IsDefault)
        {
            throw new ArgumentException(
                "The continuation tool collection is required.",
                nameof(continuationTools));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
        }

        Dictionary<string, string> continuationToolNamesByProviderName;
        try
        {
            continuationToolNamesByProviderName = ValidateTools(continuationTools);
            lock (_gate)
            {
                _ = CollectNewProviderToolBindings(
                    continuationToolNamesByProviderName);
            }
        }
        catch (AgentLimitException)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
        }
        catch (AgentConversationException exception)
        {
            throw new ArgumentException(
                "A provider tool alias cannot be rebound within a session.",
                nameof(continuationTools),
                exception);
        }

        var commitError = CommitToolResults(
            proposalGeneration,
            results,
            proposalTools);
        if (commitError is not null)
        {
            return AgentTurnResult.Failure(commitError.Value);
        }

        return await ContinueToolTurnAsync(
            continuationTools,
            provider,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles an exact provider tool batch into the durable transcript without
    /// starting another provider request. The runtime may compact and persist
    /// this stable boundary before continuing the loop.
    /// </summary>
    internal AgentTurnErrorCode? CommitToolResults(
        long proposalGeneration,
        ImmutableArray<AgentToolResult> results,
        ImmutableArray<AgentToolDefinition> proposalTools)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(proposalGeneration);
        if (results.IsDefault)
        {
            throw new ArgumentException(
                "The tool-result collection is required.",
                nameof(results));
        }

        if (proposalTools.IsDefault)
        {
            throw new ArgumentException(
                "The proposal tool collection is required.",
                nameof(proposalTools));
        }

        Dictionary<string, string> proposalToolNamesByProviderName;
        try
        {
            proposalToolNamesByProviderName = ValidateTools(proposalTools);
            ValidateToolResultBounds(results);
        }
        catch (AgentLimitException)
        {
            return AgentTurnErrorCode.LimitExceeded;
        }

        lock (_gate)
        {
            if (_activeTurn is not null)
            {
                return AgentTurnErrorCode.AlreadyRunning;
            }

            if (_pendingToolContinuation is not null)
            {
                return AgentTurnErrorCode.PendingToolDecision;
            }

            if (_pendingToolProposals.Length == 0)
            {
                var isStale = proposalGeneration <= _lastSubmittedToolGeneration
                    || proposalGeneration < _generation;
                return
                    isStale
                        ? AgentTurnErrorCode.StaleToolResults
                        : AgentTurnErrorCode.NoPendingToolDecision;
            }

            var pendingTurn = _pendingToolTurn
                ?? throw new InvalidOperationException(
                    "Pending tool proposals must retain their continuation context.");
            var pendingGeneration = _pendingToolProposals[0].Generation;
            if (proposalGeneration != pendingGeneration)
            {
                return
                    proposalGeneration < pendingGeneration
                        ? AgentTurnErrorCode.StaleToolResults
                        : AgentTurnErrorCode.ToolResultMismatch;
            }

            if (!ToolDefinitionsEqual(pendingTurn.Tools, proposalTools)
                || !ToolResultsMatchPendingProposals(results))
            {
                return AgentTurnErrorCode.ToolResultMismatch;
            }

            if (_conversation.Length
                > _limits.MaximumConversationMessages - results.Length - 1)
            {
                return AgentTurnErrorCode.LimitExceeded;
            }

            ImmutableArray<AgentMessage> resultMessages;
            ImmutableArray<AgentMessage> conversationWithResults;
            try
            {
                resultMessages = [.. results.Select(AgentMessage.FromToolResult)];
                conversationWithResults = _conversation.AddRange(resultMessages);
                ValidateConversation(
                    conversationWithResults,
                    ConversationTail.ToolResults);
            }
            catch (AgentLimitException)
            {
                return AgentTurnErrorCode.LimitExceeded;
            }
            catch (AgentConversationException)
            {
                return AgentTurnErrorCode.ToolResultMismatch;
            }

            try
            {
                RegisterProviderToolBindings(proposalToolNamesByProviderName);
            }
            catch (AgentLimitException)
            {
                return AgentTurnErrorCode.LimitExceeded;
            }
            catch (AgentConversationException exception)
            {
                throw new ArgumentException(
                    "A provider tool alias cannot be rebound within a session.",
                    nameof(proposalTools),
                    exception);
            }

            var nextConversationRevision = checked(_conversationRevision + 1);
            // This is the settlement linearization point. Once the exact results enter the
            // transcript, cancellation and provider failure may fence the continuation but
            // never erase the observed tool outcome.
            _conversation = conversationWithResults;
            _transcript = _transcript.AddRange(resultMessages);
            _conversationRevision = nextConversationRevision;
            _lastSubmittedToolGeneration = proposalGeneration;
            _pendingToolContinuation = new PendingToolContinuation(
                pendingTurn.ReasoningEffort);
            _pendingToolTurn = null;
            _pendingToolProposals = [];
            _state = NativeAgentSessionState.AwaitingProviderContinuation;
            AppendEventUnsafe(
                AgentRunEventKind.ToolResultsCommitted,
                proposalGeneration);
            return null;
        }
    }

    internal async ValueTask<AgentTurnResult> ContinueToolTurnAsync(
        ImmutableArray<AgentToolDefinition> continuationTools,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (continuationTools.IsDefault)
        {
            throw new ArgumentException(
                "The continuation tool collection is required.",
                nameof(continuationTools));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Cancel();
            return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
        }

        Dictionary<string, string> continuationToolNamesByProviderName;
        try
        {
            continuationToolNamesByProviderName = ValidateTools(continuationTools);
        }
        catch (AgentLimitException)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
        }

        ActiveTurn activeTurn;
        AgentProviderRequest request;
        lock (_gate)
        {
            if (_activeTurn is not null)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.AlreadyRunning);
            }

            var pendingContinuation = _pendingToolContinuation;
            if (pendingContinuation is null)
            {
                return AgentTurnResult.Failure(
                    AgentTurnErrorCode.NoPendingToolDecision);
            }

            if (_providerOperationsInFlight >= _limits.MaximumConcurrentProviderOperations)
            {
                return AgentTurnResult.Failure(
                    AgentTurnErrorCode.ProviderOperationLimit);
            }

            try
            {
                ValidateConversation(_conversation, ConversationTail.ToolResults);
                RegisterProviderToolBindings(continuationToolNamesByProviderName);
            }
            catch (AgentLimitException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }
            catch (AgentConversationException exception)
            {
                throw new ArgumentException(
                    "A provider tool alias cannot be rebound within a session.",
                    nameof(continuationTools),
                    exception);
            }

            var generation = checked(_generation + 1);
            _generation = generation;
            activeTurn = new ActiveTurn(
                generation,
                _conversationRevision,
                _conversation,
                [],
                continuationTools,
                pendingContinuation.ReasoningEffort,
                ActiveTurnKind.ToolContinuation);
            _pendingToolContinuation = null;
            _activeTurn = activeTurn;
            _providerOperationsInFlight = checked(_providerOperationsInFlight + 1);
            _state = NativeAgentSessionState.Streaming;
            request = new AgentProviderRequest(
                RunId,
                generation,
                _conversation,
                continuationTools,
                pendingContinuation.ReasoningEffort);
            AppendEventUnsafe(AgentRunEventKind.TurnStarted, generation);
        }

        return await ExecuteProviderTurnAsync(
            activeTurn,
            request,
            continuationToolNamesByProviderName,
            provider,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a queued local-human message after a complete tool-result batch.
    /// The current assistant step is already settled; this message becomes the
    /// next provider input without cancelling or rewriting committed history.
    /// </summary>
    internal async ValueTask<AgentTurnResult> RunSteeringTurnAsync(
        string userMessage,
        ImmutableArray<AgentToolDefinition> tools,
        AgentReasoningEffort reasoningEffort,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(provider);
        if (tools.IsDefault)
        {
            throw new ArgumentException(
                "The tool collection is required.",
                nameof(tools));
        }

        if (!Enum.IsDefined(reasoningEffort))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningEffort));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Cancel();
            return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
        }

        var user = new AgentMessage(AgentMessageRole.User, userMessage);
        Dictionary<string, string> toolNamesByProviderName;
        try
        {
            ValidateMessageBounds(user, _limits.MaximumUserTextBytes);
            toolNamesByProviderName = ValidateTools(tools);
        }
        catch (AgentLimitException)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
        }

        ActiveTurn activeTurn;
        AgentProviderRequest request;
        lock (_gate)
        {
            if (_activeTurn is not null)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.AlreadyRunning);
            }

            if (_pendingToolContinuation is null)
            {
                return AgentTurnResult.Failure(
                    AgentTurnErrorCode.NoPendingToolDecision);
            }

            if (_providerOperationsInFlight >= _limits.MaximumConcurrentProviderOperations)
            {
                return AgentTurnResult.Failure(
                    AgentTurnErrorCode.ProviderOperationLimit);
            }

            if (_conversation.Length > _limits.MaximumConversationMessages - 2)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }

            var requestConversation = _conversation.Add(user);
            try
            {
                ValidateConversation(_conversation, ConversationTail.ToolResults);
                ValidateConversation(requestConversation, ConversationTail.User);
                RegisterProviderToolBindings(toolNamesByProviderName);
            }
            catch (AgentLimitException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }
            catch (AgentConversationException exception)
            {
                throw new ArgumentException(
                    "A provider tool alias cannot be rebound within a session.",
                    nameof(tools),
                    exception);
            }

            var generation = checked(_generation + 1);
            _generation = generation;
            activeTurn = new ActiveTurn(
                generation,
                _conversationRevision,
                _conversation,
                [user],
                tools,
                reasoningEffort,
                ActiveTurnKind.QueuedSteering);
            _pendingToolContinuation = null;
            _activeTurn = activeTurn;
            _providerOperationsInFlight = checked(_providerOperationsInFlight + 1);
            _state = NativeAgentSessionState.Streaming;
            request = new AgentProviderRequest(
                RunId,
                generation,
                requestConversation,
                tools,
                reasoningEffort);
            AppendEventUnsafe(AgentRunEventKind.TurnStarted, generation);
        }

        return await ExecuteProviderTurnAsync(
            activeTurn,
            request,
            toolNamesByProviderName,
            provider,
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidateToolResultBounds(ImmutableArray<AgentToolResult> results)
    {
        if (results.Length > _limits.MaximumToolResultsPerTurn)
        {
            throw new AgentLimitException();
        }

        long totalBytes = 0;
        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            var byteCount = Encoding.UTF8.GetByteCount(result.Value.Content);
            totalBytes += byteCount;
            if (byteCount > _limits.MaximumToolResultBytes
                || totalBytes > _limits.MaximumTotalToolResultBytesPerTurn)
            {
                throw new AgentLimitException();
            }

            if (result.Value.Kind == AgentToolResultValueKind.Json)
            {
                ValidateToolResultJson(result.Value.Content);
            }
        }
    }

    private void ValidateToolResultJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowDuplicateProperties = false,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = _limits.MaximumJsonDepth,
                });
            var remainingNodes = _limits.MaximumJsonNodes;
            ValidateToolResultJsonNodes(document.RootElement, ref remainingNodes);
        }
        catch (JsonException)
        {
            throw new AgentLimitException();
        }
    }

    private static void ValidateToolResultJsonNodes(
        JsonElement element,
        ref int remainingNodes)
    {
        if (--remainingNodes < 0)
        {
            throw new AgentLimitException();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateToolResultJsonNodes(property.Value, ref remainingNodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateToolResultJsonNodes(item, ref remainingNodes);
            }
        }
    }

    private bool ToolResultsMatchPendingProposals(
        ImmutableArray<AgentToolResult> results)
    {
        if (results.Length != _pendingToolProposals.Length)
        {
            return false;
        }

        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            if (result is null || !ResultMatches(_pendingToolProposals[index], result))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ToolDefinitionsEqual(
        ImmutableArray<AgentToolDefinition> expected,
        ImmutableArray<AgentToolDefinition> actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var expectedTool = expected[index];
            var actualTool = actual[index];
            if (!string.Equals(expectedTool.Name, actualTool.Name, StringComparison.Ordinal)
                || !string.Equals(
                    expectedTool.Description,
                    actualTool.Description,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedTool.InputSchema.GetRawText(),
                    actualTool.InputSchema.GetRawText(),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
