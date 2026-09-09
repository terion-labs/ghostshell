using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Agent;

public sealed partial class NativeAgentSession
{
    private readonly Lock _gate = new();
    private readonly Queue<AgentRunEvent> _events = [];
    private readonly AgentKernelLimits _limits;
    private readonly Dictionary<string, string> _providerToolBindings =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _changed = NewSignal();
    private CompactionLease? _activeCompaction;
    // Provider context and user-visible history have different lifecycles.
    // Compaction may replace _conversation, but it must never rewrite the
    // durable transcript shown to the user.
    private ImmutableArray<AgentMessage> _conversation;
    private ImmutableArray<AgentMessage> _transcript;
    private PendingToolTurn? _pendingToolTurn;
    private PendingToolContinuation? _pendingToolContinuation;
    private ImmutableArray<AgentToolProposal> _pendingToolProposals = [];
    private ActiveTurn? _activeTurn;
    private int _providerOperationsInFlight;
    private NativeAgentSessionState _state = NativeAgentSessionState.Ready;
    private long _conversationRevision;
    private long _generation;
    private long _revision;
    private long _sequence;
    private string? _conversationTitle;

    public NativeAgentSession(
        AgentRunId runId,
        IEnumerable<AgentMessage>? initialMessages = null,
        AgentKernelLimits? limits = null)
        : this(runId, initialMessages, limits, TimeProvider.System)
    {
    }

    internal NativeAgentSession(
        AgentRunId runId,
        IEnumerable<AgentMessage>? initialMessages,
        AgentKernelLimits? limits,
        TimeProvider timeProvider)
    {
        if (runId == default)
        {
            throw new ArgumentException("The agent run ID is required.", nameof(runId));
        }

        AgentToolDefinition.ValidateIdentifier(runId.Value, nameof(runId), 256);
        RunId = runId;
        _limits = limits ?? AgentKernelLimits.Default;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        try
        {
            _conversation = MaterializeInitialConversation(initialMessages ?? []);
            ValidateConversation(_conversation);
            _transcript = _conversation;
            RegisterProviderToolBindings(
                EnumerateProviderToolBindings(_conversation));
        }
        catch (Exception exception)
            when (exception is AgentLimitException or AgentConversationException)
        {
            throw new ArgumentException(
                "The initial conversation is not a valid bounded stable transcript.",
                nameof(initialMessages),
                exception);
        }
    }

    public AgentRunId RunId { get; }

    public bool HasGeneratedTitle
    {
        get
        {
            lock (_gate)
            {
                return _conversationTitle is not null;
            }
        }
    }

    public bool TrySetConversationTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = NormalizeConversationTitle(title);
        lock (_gate)
        {
            if (_state != NativeAgentSessionState.Ready
                || _activeTurn is not null
                || _pendingToolProposals.Length > 0
                || _activeCompaction is not null)
            {
                return false;
            }

            if (string.Equals(_conversationTitle, normalized, StringComparison.Ordinal))
            {
                return true;
            }

            _conversationTitle = normalized;
            AppendEventUnsafe(AgentRunEventKind.ConversationTitleChanged, _generation);
            return true;
        }
    }

    private static string NormalizeConversationTitle(string title)
    {
        const int maximumLength = 72;
        var normalized = string.Join(
            ' ',
            title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A conversation title must contain bounded printable text.",
                nameof(title));
        }

        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumLength - 1), "…");
    }

    public AgentSessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    public async ValueTask<AgentTurnResult> RunTurnAsync(
        string userMessage,
        ImmutableArray<AgentToolDefinition> tools,
        IAgentProvider provider,
        CancellationToken cancellationToken) =>
        await RunTurnAsync(
            userMessage,
            [],
            tools,
            AgentReasoningEffort.Automatic,
            provider,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentTurnResult> RunTurnAsync(
        string userMessage,
        ImmutableArray<AgentToolDefinition> tools,
        AgentReasoningEffort reasoningEffort,
        IAgentProvider provider,
        CancellationToken cancellationToken) =>
        await RunTurnAsync(
            userMessage,
            [],
            tools,
            reasoningEffort,
            provider,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentTurnResult> RunTurnAsync(
        string userMessage,
        ImmutableArray<AgentImageAttachment> images,
        ImmutableArray<AgentToolDefinition> tools,
        AgentReasoningEffort reasoningEffort,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userMessage);
        if (images.IsDefault)
        {
            throw new ArgumentException(
                "The image collection is required.",
                nameof(images));
        }

        if (string.IsNullOrWhiteSpace(userMessage) && images.Length == 0)
        {
            throw new ArgumentException(
                "A turn requires text or an image.",
                nameof(userMessage));
        }

        ArgumentNullException.ThrowIfNull(provider);
        if (tools.IsDefault)
        {
            throw new ArgumentException("The tool collection is required.", nameof(tools));
        }


        if (!Enum.IsDefined(reasoningEffort))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningEffort));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
        }

        var user = new AgentMessage(AgentMessageRole.User, userMessage, images);
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

            if (_pendingToolProposals.Length > 0)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.PendingToolDecision);
            }

            if (_pendingToolContinuation is not null)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.PendingToolDecision);
            }

            if (_providerOperationsInFlight >= _limits.MaximumConcurrentProviderOperations)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.ProviderOperationLimit);
            }

            try
            {
                ValidateConversation(_conversation);
            }
            catch (AgentConversationException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.ConversationConflict);
            }
            catch (AgentLimitException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }

            if (_conversation.Length > _limits.MaximumConversationMessages - 2)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }

            var requestConversation = _conversation.Add(user);
            try
            {
                ValidateConversation(requestConversation, ConversationTail.User);
            }
            catch (Exception exception)
                when (exception is AgentLimitException or AgentConversationException)
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
            }

            try
            {
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
                ActiveTurnKind.InitialUser);
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

    private async ValueTask<AgentTurnResult> ExecuteProviderTurnAsync(
        ActiveTurn activeTurn,
        AgentProviderRequest request,
        IReadOnlyDictionary<string, string> toolNamesByProviderName,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await ExecuteProviderGenerationAsync(
                activeTurn,
                request,
                toolNamesByProviderName,
                provider,
                cancellationToken).ConfigureAwait(false);
            var replacement = GetSteeringReplacement(activeTurn);
            if (replacement is null)
            {
                return result;
            }

            activeTurn = replacement;
            request = new AgentProviderRequest(
                RunId,
                replacement.Generation,
                replacement.BaseConversation.AddRange(replacement.InputMessages),
                replacement.Tools,
                replacement.ReasoningEffort);
        }
    }

    private async ValueTask<AgentTurnResult> ExecuteProviderGenerationAsync(
        ActiveTurn activeTurn,
        AgentProviderRequest request,
        IReadOnlyDictionary<string, string> toolNamesByProviderName,
        IAgentProvider provider,
        CancellationToken cancellationToken)
    {
        var reducer = new ProviderTurnReducer(toolNamesByProviderName, _limits);
        using var externalCancellation = cancellationToken.Register(
            () =>
            {
                CancelGeneration(activeTurn);
                activeTurn.TryCancel();
            });
        var providerTask = Task.Run(
            () => ConsumeProviderAsync(
                provider,
                request,
                activeTurn,
                reducer));
        try
        {
            var completedTask = await Task
                .WhenAny(providerTask, activeTurn.Cancellation)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, providerTask))
            {
                _ = ObserveProviderTaskAsync(providerTask);
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            var reducedTurn = await providerTask.ConfigureAwait(false);
            var proposals = CreateProposals(activeTurn.Generation, reducedTurn.ToolCalls);
            return CommitTurn(
                activeTurn,
                reducedTurn.AssistantText,
                reducedTurn.ReasoningSummary,
                reducedTurn.Usage,
                reducedTurn.StopReason,
                proposals,
                reducedTurn.ProviderReplayState);
        }
        catch (OperationCanceledException)
            when (activeTurn.Token.IsCancellationRequested || !IsActive(activeTurn))
        {
            CancelGeneration(activeTurn);
            _ = ObserveProviderTaskAsync(providerTask);
            return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
        }
        catch (ProviderStreamException exception)
        {
            var errorCode = exception.Code == ProviderStreamErrorCode.LimitExceeded
                ? AgentTurnErrorCode.LimitExceeded
                : AgentTurnErrorCode.InvalidProviderStream;
            if (!FailGeneration(activeTurn, errorCode))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            activeTurn.TryCancel();
            return AgentTurnResult.Failure(errorCode);
        }
        catch (AgentProviderException exception)
        {
            if (!FailGeneration(activeTurn, AgentTurnErrorCode.ProviderFailure))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            return AgentTurnResult.Failure(
                AgentTurnErrorCode.ProviderFailure,
                exception);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!FailGeneration(activeTurn, AgentTurnErrorCode.ProviderFailure))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            return AgentTurnResult.Failure(AgentTurnErrorCode.ProviderFailure);
        }
    }

    public bool Cancel()
    {
        ActiveTurn? cancelled;
        lock (_gate)
        {
            cancelled = _activeTurn;
            if (cancelled is not null)
            {
                _activeTurn = null;
                _generation = checked(_generation + 1);
                _state = NativeAgentSessionState.Cancelled;
                cancelled.SignalCancellation();
                AppendEventUnsafe(AgentRunEventKind.TurnCancelled, cancelled.Generation);
            }
            else if (_pendingToolProposals.Length > 0)
            {
                var pendingTurn = _pendingToolTurn
                    ?? throw new InvalidOperationException(
                        "Pending tool proposals must retain their base conversation.");
                var proposalGeneration = _pendingToolProposals[0].Generation;
                var nextConversationRevision = checked(_conversationRevision + 1);
                _conversation = pendingTurn.BaseConversation;
                if (_transcript.Length > 0
                    && _transcript[^1] is
                    {
                        Role: AgentMessageRole.Assistant,
                        ToolCalls.Length: > 0,
                    })
                {
                    _transcript = _transcript[..^1]
                        .Add(InterruptedAssistantMessage());
                }
                _conversationRevision = nextConversationRevision;
                _pendingToolTurn = null;
                _pendingToolProposals = [];
                _generation = checked(_generation + 1);
                _state = NativeAgentSessionState.Cancelled;
                AppendEventUnsafe(
                    AgentRunEventKind.ToolProposalsDiscarded,
                    proposalGeneration);
            }
            else if (_pendingToolContinuation is not null)
            {
                var continuationGeneration = _generation;
                _pendingToolContinuation = null;
                _generation = checked(_generation + 1);
                _state = NativeAgentSessionState.Cancelled;
                AppendEventUnsafe(
                    AgentRunEventKind.TurnCancelled,
                    continuationGeneration);
            }
            else
            {
                return false;
            }
        }

        cancelled?.TryCancel();
        return true;
    }

    public ValueTask<AgentCompactionResult> CompactAsync(
        int minimumRetainedTurns,
        IAgentConversationCompactor compactor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumRetainedTurns);
        ArgumentNullException.ThrowIfNull(compactor);
        return CompactSelectedAsync(
            CompactionSelection.ForRetainedTurns(minimumRetainedTurns),
            compactor,
            cancellationToken);
    }

    public AgentContextUsage EstimateContextUsage()
    {
        lock (_gate)
        {
            return EstimateContextUsage(_conversation);
        }
    }

    public ValueTask<AgentCompactionResult> CompactAsync(
        int contextWindowTokens,
        AgentCompactionSettings settings,
        IAgentConversationCompactor compactor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(compactor);
        if (contextWindowTokens <= settings.ReserveTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextWindowTokens),
                "The context window must exceed the reserved completion budget.");
        }

        if (!settings.Enabled)
        {
            return ValueTask.FromResult(AgentCompactionResult.Failure(
                AgentCompactionErrorCode.NothingToCompact));
        }

        return CompactSelectedAsync(
            CompactionSelection.ForTokenBudget(contextWindowTokens, settings),
            compactor,
            cancellationToken);
    }

    private async ValueTask<AgentCompactionResult> CompactSelectedAsync(
        CompactionSelection selection,
        IAgentConversationCompactor compactor,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.Cancelled);
        }

        CompactionCapture capture;
        CompactionLease lease;
        lock (_gate)
        {
            if (_activeTurn is not null
                || _pendingToolProposals.Length > 0
                || _activeCompaction is not null)
            {
                return AgentCompactionResult.Failure(AgentCompactionErrorCode.Busy);
            }

            try
            {
                ValidateConversation(
                    _conversation,
                    _pendingToolContinuation is null
                        ? ConversationTail.Complete
                        : ConversationTail.ToolResults);
            }
            catch (Exception exception)
                when (exception is AgentLimitException or AgentConversationException)
            {
                return AgentCompactionResult.Failure(AgentCompactionErrorCode.Busy);
            }

            var cut = selection.Kind switch
            {
                CompactionSelectionKind.RetainedTurns => SelectRetainedTurnCut(
                    _conversation,
                    selection.Value),
                CompactionSelectionKind.TokenBudget => SelectTokenBudgetCut(
                    _conversation,
                    selection.Value,
                    selection.Settings!),
                _ => null,
            };
            if (cut is null)
            {
                return AgentCompactionResult.Failure(AgentCompactionErrorCode.NothingToCompact);
            }

            var systemMessageCount = CountLeadingSystemMessages(_conversation);
            capture = new CompactionCapture(
                _conversation,
                _conversationRevision,
                _generation,
                systemMessageCount,
                cut.CutIndex,
                cut.TurnStartIndex);
            lease = new CompactionLease();
            _activeCompaction = lease;
        }

        var cancellationSignal = NewSignal();
        using var compactionCancellation = cancellationToken.Register(
            () =>
            {
                lease.TryCancel();
                cancellationSignal.TrySetResult();
            });
        Task<AgentMessage> compactionTask;
        try
        {
            var historyEnd = capture.TurnStartIndex ?? capture.CutIndex;
            var turnPrefix = capture.TurnStartIndex is { } turnStartIndex
                ? capture.Conversation[turnStartIndex..capture.CutIndex]
                : [];
            var compactionRequest = new AgentCompactionRequest(
                RunId,
                capture.Generation,
                capture.Conversation[
                    capture.SystemMessageCount..historyEnd],
                turnPrefix);
            compactionTask = Task.Run(
                async () =>
                {
                    if (!lease.TryBeginInvocation())
                    {
                        throw new OperationCanceledException(lease.Token);
                    }

                    return await compactor
                        .CompactAsync(compactionRequest, lease.Token)
                        .ConfigureAwait(false);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            ReleaseCompaction(lease);
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lease.Dispose();
            ReleaseCompaction(lease);
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.CompactorFailure);
        }

        _ = TrackCompactionCompletionAsync(lease, compactionTask);
        AgentMessage summary;
        try
        {
            var completedTask = await Task
                .WhenAny(compactionTask, cancellationSignal.Task)
                .ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, compactionTask))
            {
                return AgentCompactionResult.Failure(AgentCompactionErrorCode.Cancelled);
            }

            summary = await compactionTask.ConfigureAwait(false);
            ReleaseCompaction(lease);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (compactionTask.IsCompleted)
            {
                ReleaseCompaction(lease);
            }

            return AgentCompactionResult.Failure(AgentCompactionErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReleaseCompaction(lease);
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.CompactorFailure);
        }

        if (summary is null
            || summary.Role != AgentMessageRole.Summary
            || string.IsNullOrWhiteSpace(summary.Content))
        {
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.InvalidSummary);
        }

        ImmutableArray<AgentMessage> replacement;
        try
        {
            ValidateMessageBounds(summary, _limits.MaximumAssistantTextBytes);
            var retainedTail = capture.Conversation[capture.CutIndex..]
                .Select(message => message.WithoutUsage())
                .ToImmutableArray();
            replacement =
            [
                .. capture.Conversation[..capture.SystemMessageCount],
                summary,
                .. retainedTail,
            ];
            ValidateConversation(
                replacement,
                _pendingToolContinuation is null
                    ? ConversationTail.Complete
                    : ConversationTail.ToolResults);
        }
        catch (AgentLimitException)
        {
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.LimitExceeded);
        }
        catch (AgentConversationException)
        {
            return AgentCompactionResult.Failure(AgentCompactionErrorCode.InvalidSummary);
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return AgentCompactionResult.Failure(AgentCompactionErrorCode.Cancelled);
            }

            if (_conversationRevision != capture.ConversationRevision
                || _generation != capture.Generation
                || !_conversation.Equals(capture.Conversation))
            {
                return AgentCompactionResult.Failure(
                    AgentCompactionErrorCode.ConversationConflict);
            }

            var nextConversationRevision = checked(_conversationRevision + 1);
            _conversation = replacement;
            _conversationRevision = nextConversationRevision;
            AppendEventUnsafe(AgentRunEventKind.ConversationCompacted, _generation);
            return AgentCompactionResult.Success();
        }
    }

    private static CompactionCut? SelectRetainedTurnCut(
        ImmutableArray<AgentMessage> conversation,
        int minimumRetainedTurns)
    {
        var bodyStart = ConversationBodyStart(conversation);
        var turnStarts = conversation
            .Select((message, index) => (message, index))
            .Where(item => item.index >= bodyStart
                && item.message.Role == AgentMessageRole.User)
            .Select(item => item.index)
            .ToImmutableArray();
        if (minimumRetainedTurns >= turnStarts.Length)
        {
            return null;
        }

        var compactedTurnCount = turnStarts.Length - minimumRetainedTurns;
        var cutIndex = compactedTurnCount == turnStarts.Length
            ? conversation.Length
            : turnStarts[compactedTurnCount];
        return new CompactionCut(cutIndex, TurnStartIndex: null);
    }

    private static CompactionCut? SelectTokenBudgetCut(
        ImmutableArray<AgentMessage> conversation,
        int contextWindowTokens,
        AgentCompactionSettings settings)
    {
        var usage = EstimateContextUsage(conversation);
        if (usage.EstimatedTokens
            <= contextWindowTokens - settings.ReserveTokens)
        {
            return null;
        }

        var bodyStart = ConversationBodyStart(conversation);
        var cutPoints = conversation
            .Select((message, index) => (message, index))
            .Where(item => item.index >= bodyStart
                && item.message.Role is AgentMessageRole.User
                    or AgentMessageRole.Assistant)
            .Select(item => item.index)
            .ToImmutableArray();
        if (cutPoints.IsDefaultOrEmpty)
        {
            return null;
        }

        long recentTokens = 0;
        var cutIndex = cutPoints[0];
        for (var index = conversation.Length - 1; index >= bodyStart; index--)
        {
            recentTokens = checked(
                recentTokens + EstimateMessageTokens(conversation[index]));
            if (recentTokens < settings.KeepRecentTokens)
            {
                continue;
            }

            var foundCutPoint = false;
            foreach (var candidate in cutPoints)
            {
                if (candidate >= index)
                {
                    cutIndex = candidate;
                    foundCutPoint = true;
                    break;
                }
            }

            if (foundCutPoint)
            {
                break;
            }
        }

        // Provider-reported usage also includes fixed prompt/tool-schema cost,
        // so the textual tail can be smaller than KeepRecentTokens even while
        // the real context is over threshold. PI defaults to the first cut in
        // that case; advance it to the next turn (or split the only turn) so
        // compaction is guaranteed to remove some transcript instead of
        // inserting a summary in front of an unchanged conversation.
        if (cutIndex == bodyStart)
        {
            var nextTurn = cutPoints.FirstOrDefault(candidate =>
                candidate > bodyStart
                && conversation[candidate].Role == AgentMessageRole.User);
            if (nextTurn > bodyStart)
            {
                cutIndex = nextTurn;
            }
            else
            {
                var splitPoint = cutPoints.FirstOrDefault(candidate =>
                    candidate > bodyStart
                    && conversation[candidate].Role == AgentMessageRole.Assistant);
                if (splitPoint > bodyStart)
                {
                    cutIndex = splitPoint;
                }
            }
        }

        if (conversation[cutIndex].Role == AgentMessageRole.User)
        {
            if (cutIndex == CountLeadingSystemMessages(conversation))
            {
                return null;
            }

            return new CompactionCut(cutIndex, TurnStartIndex: null);
        }

        var turnStartIndex = FindTurnStartIndex(conversation, cutIndex, bodyStart);
        return turnStartIndex < 0
            ? null
            : new CompactionCut(cutIndex, turnStartIndex);
    }

    private static int ConversationBodyStart(
        ImmutableArray<AgentMessage> conversation)
    {
        var bodyStart = CountLeadingSystemMessages(conversation);
        if (bodyStart < conversation.Length
            && conversation[bodyStart].Role == AgentMessageRole.Summary)
        {
            bodyStart++;
        }

        return bodyStart;
    }

    private static int FindTurnStartIndex(
        ImmutableArray<AgentMessage> conversation,
        int index,
        int bodyStart)
    {
        for (var candidate = index; candidate >= bodyStart; candidate--)
        {
            if (conversation[candidate].Role == AgentMessageRole.User)
            {
                return candidate;
            }
        }

        return -1;
    }

    private static AgentContextUsage EstimateContextUsage(
        ImmutableArray<AgentMessage> conversation)
    {
        for (var index = conversation.Length - 1; index >= 0; index--)
        {
            if (conversation[index].Usage is not { } usage)
            {
                continue;
            }

            long trailing = 0;
            for (var trailingIndex = index + 1;
                 trailingIndex < conversation.Length;
                 trailingIndex++)
            {
                trailing = checked(
                    trailing + EstimateMessageTokens(conversation[trailingIndex]));
            }

            return new AgentContextUsage(
                checked(usage.TotalTokens + trailing),
                UsesProviderReportedUsage: true);
        }

        long estimated = 0;
        foreach (var message in conversation)
        {
            estimated = checked(estimated + EstimateMessageTokens(message));
        }

        return new AgentContextUsage(estimated, UsesProviderReportedUsage: false);
    }

    private static long EstimateMessageTokens(AgentMessage message)
    {
        long characters = message.Content.Length;
        characters += message.ReasoningSummary?.Length ?? 0;
        characters += message.Images.Length * 4_800L;
        foreach (var toolCall in message.ToolCalls)
        {
            characters += toolCall.ToolName.Length;
            characters += toolCall.Arguments.GetRawText().Length;
        }

        if (message.ToolResult is { } result)
        {
            characters += result.Value.Content.Length;
        }

        // PI uses a deliberately conservative four-characters-per-token
        // fallback when the provider has not reported an exact context size.
        return Math.Max(1, (characters + 3) / 4);
    }

    public async IAsyncEnumerable<AgentRunStreamItem> WatchAsync(
        AgentEventWatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumBatchSize > _limits.MaximumEventBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The requested event batch exceeds the session limit.");
        }

        var afterSequence = request.AfterSequence;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<AgentRunEvent> pending;
            AgentSessionSnapshot? resynchronization = null;
            Task waitTask;
            lock (_gate)
            {
                var oldestSequence = _events.TryPeek(out var oldestEvent)
                    ? oldestEvent.Sequence
                    : _sequence + 1;
                if (afterSequence > _sequence || afterSequence < oldestSequence - 1)
                {
                    resynchronization = SnapshotUnsafe();
                    pending = [];
                    waitTask = Task.CompletedTask;
                }
                else
                {
                    pending = [.. _events
                        .Where(agentEvent => agentEvent.Sequence > afterSequence)
                        .Take(request.MaximumBatchSize)];
                    waitTask = _changed.Task;
                }
            }

            if (resynchronization is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AgentRunStreamItem.ResynchronizationRequired(
                    resynchronization,
                    resynchronization.LastSequence);
                yield break;
            }

            if (pending.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AgentRunStreamItem.EventBatch(pending);
                afterSequence = pending[^1].Sequence;
                continue;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ReducedProviderTurn> ConsumeProviderAsync(
        IAgentProvider provider,
        AgentProviderRequest request,
        ActiveTurn activeTurn,
        ProviderTurnReducer reducer)
    {
        try
        {
            lock (_gate)
            {
                if (!IsActiveUnsafe(activeTurn))
                {
                    throw new OperationCanceledException(activeTurn.Token);
                }

                // This is the provider-invocation linearization point. Cancellation that wins
                // this gate prevents the provider entrypoint from being called at all.
            }

            await foreach (var providerEvent in provider
                               .StreamAsync(request, activeTurn.Token)
                               .WithCancellation(activeTurn.Token)
                               .ConfigureAwait(false))
            {
                if (!IsActive(activeTurn))
                {
                    throw new OperationCanceledException(activeTurn.Token);
                }

                reducer.Apply(providerEvent);
                if (providerEvent is AgentProviderEvent.TextDelta textDelta)
                {
                    lock (_gate)
                    {
                        if (!IsActiveUnsafe(activeTurn))
                        {
                            throw new OperationCanceledException(activeTurn.Token);
                        }

                        AppendEventUnsafe(
                            AgentRunEventKind.ProvisionalText,
                            activeTurn.Generation,
                            provisionalText: textDelta.Value);
                    }
                }
                else if (providerEvent
                    is AgentProviderEvent.ReasoningSummaryDelta reasoningDelta)
                {
                    lock (_gate)
                    {
                        if (!IsActiveUnsafe(activeTurn))
                        {
                            throw new OperationCanceledException(activeTurn.Token);
                        }

                        AppendEventUnsafe(
                            AgentRunEventKind.ProvisionalReasoningSummary,
                            activeTurn.Generation,
                            provisionalReasoningSummary: reasoningDelta.Value);
                    }
                }
            }

            if (!IsActive(activeTurn))
            {
                throw new OperationCanceledException(activeTurn.Token);
            }

            return reducer.Build();
        }
        finally
        {
            activeTurn.Dispose();
            lock (_gate)
            {
                _providerOperationsInFlight--;
            }
        }
    }

    private static async Task ObserveProviderTaskAsync(Task providerTask)
    {
        try
        {
            await providerTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The generation was already fenced. Observing the detached provider task prevents
            // a late fault from becoming unobserved; it cannot publish or commit session state.
        }
    }

    private async Task TrackCompactionCompletionAsync(
        CompactionLease lease,
        Task<AgentMessage> compactionTask)
    {
        try
        {
            await compactionTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The caller maps the failure to a stable error. This observer only owns the lease.
        }
        finally
        {
            lease.Dispose();
            ReleaseCompaction(lease);
        }
    }

    private void ReleaseCompaction(CompactionLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeCompaction, lease))
            {
                _activeCompaction = null;
            }
        }
    }

    private AgentTurnResult CommitTurn(
        ActiveTurn activeTurn,
        string assistantText,
        string? reasoningSummary,
        AgentTokenUsage? usage,
        AgentProviderStopReason stopReason,
        ImmutableArray<AgentToolProposal> proposals,
        AgentProviderReplayState? providerReplayState)
    {
        var assistant = AgentMessage.Assistant(
            assistantText,
            proposals,
            reasoningSummary,
            usage,
            providerReplayState,
            activeTurn.ReasoningEffort);
        ImmutableArray<AgentMessage> replacement;
        try
        {
            replacement = activeTurn.BaseConversation
                .AddRange(activeTurn.InputMessages)
                .Add(assistant);
            ValidateConversation(
                replacement,
                proposals.Length > 0
                    ? ConversationTail.AssistantToolCalls
                    : ConversationTail.Complete);
        }
        catch (AgentLimitException)
        {
            if (!FailGeneration(activeTurn, AgentTurnErrorCode.LimitExceeded))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            return AgentTurnResult.Failure(AgentTurnErrorCode.LimitExceeded);
        }
        catch (AgentConversationException)
        {
            if (!FailGeneration(activeTurn, AgentTurnErrorCode.InvalidProviderStream))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            return AgentTurnResult.Failure(AgentTurnErrorCode.InvalidProviderStream);
        }

        lock (_gate)
        {
            if (!IsActiveUnsafe(activeTurn))
            {
                return AgentTurnResult.Failure(AgentTurnErrorCode.Cancelled);
            }

            if (_conversationRevision != activeTurn.BaseConversationRevision
                || !_conversation.Equals(activeTurn.BaseConversation))
            {
                _activeTurn = null;
                _state = NativeAgentSessionState.Failed;
                AppendEventUnsafe(
                    AgentRunEventKind.TurnFailed,
                    activeTurn.Generation,
                    errorCode: AgentTurnErrorCode.ConversationConflict);
                return AgentTurnResult.Failure(AgentTurnErrorCode.ConversationConflict);
            }

            var nextConversationRevision = checked(_conversationRevision + 1);
            _conversation = replacement;
            _transcript = _transcript
                .AddRange(activeTurn.InputMessages)
                .Add(assistant);
            _conversationRevision = nextConversationRevision;
            _pendingToolTurn = proposals.Length > 0
                ? new PendingToolTurn(
                    activeTurn.BaseConversation,
                    activeTurn.Tools,
                    activeTurn.ReasoningEffort)
                : null;
            _pendingToolProposals = proposals;
            _activeTurn = null;
            _state = proposals.Length > 0
                ? NativeAgentSessionState.AwaitingToolDecision
                : NativeAgentSessionState.Ready;
            AppendEventUnsafe(
                AgentRunEventKind.TurnCommitted,
                activeTurn.Generation,
                toolProposalCount: proposals.Length);
            return AgentTurnResult.Success(stopReason, proposals);
        }
    }

    private ImmutableArray<AgentToolProposal> CreateProposals(
        long generation,
        ImmutableArray<ReducedToolCall> toolCalls) =>
        [.. toolCalls
            .Select(toolCall => new AgentToolProposal(
                $"{RunId.Value}:{generation}:{toolCall.Index}",
                generation,
                toolCall.ProviderCallId,
                toolCall.Name,
                toolCall.Arguments))];

    private bool IsActive(ActiveTurn activeTurn)
    {
        lock (_gate)
        {
            return IsActiveUnsafe(activeTurn);
        }
    }

    private bool IsActiveUnsafe(ActiveTurn activeTurn) =>
        ReferenceEquals(_activeTurn, activeTurn)
        && _generation == activeTurn.Generation;

    private ActiveTurn? GetSteeringReplacement(ActiveTurn activeTurn)
    {
        lock (_gate)
        {
            return activeTurn.SteeringReplacement;
        }
    }

    private void CancelGeneration(ActiveTurn activeTurn)
    {
        lock (_gate)
        {
            if (!IsActiveUnsafe(activeTurn))
            {
                return;
            }

            _activeTurn = null;
            _generation = checked(_generation + 1);
            _state = NativeAgentSessionState.Cancelled;
            activeTurn.SignalCancellation();
            AppendEventUnsafe(AgentRunEventKind.TurnCancelled, activeTurn.Generation);
        }
    }

    private bool FailGeneration(ActiveTurn activeTurn, AgentTurnErrorCode errorCode)
    {
        lock (_gate)
        {
            if (!IsActiveUnsafe(activeTurn))
            {
                return false;
            }

            _activeTurn = null;
            _state = NativeAgentSessionState.Failed;
            AppendEventUnsafe(
                AgentRunEventKind.TurnFailed,
                activeTurn.Generation,
                errorCode: errorCode);
            return true;
        }
    }

    private Dictionary<string, string> ValidateTools(
        ImmutableArray<AgentToolDefinition> tools)
    {
        if (tools.Length > _limits.MaximumToolDefinitions)
        {
            throw new AgentLimitException();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var namesByProviderName = new Dictionary<string, string>(
            StringComparer.Ordinal);
        long totalSchemaBytes = 0;
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!names.Add(tool.Name))
            {
                throw new ArgumentException("Tool names must be unique.", nameof(tools));
            }

            AgentToolDefinition.ValidateProviderName(
                tool.ProviderName,
                nameof(tools));
            if (!namesByProviderName.TryAdd(tool.ProviderName, tool.Name))
            {
                throw new ArgumentException(
                    "Provider tool names must be unique.",
                    nameof(tools));
            }

            var schemaBytes = Encoding.UTF8.GetByteCount(tool.InputSchema.GetRawText());
            totalSchemaBytes += schemaBytes;
            if (schemaBytes > _limits.MaximumToolSchemaBytes
                || totalSchemaBytes > _limits.MaximumTotalToolSchemaBytes)
            {
                throw new AgentLimitException();
            }

            var remainingNodes = _limits.MaximumJsonNodes;
            ValidateToolSchemaWithinLimits(tool.InputSchema, 1, ref remainingNodes);
        }

        return namesByProviderName;
    }

    private void RegisterProviderToolBindings(
        IEnumerable<KeyValuePair<string, string>> bindings)
    {
        var newBindings = CollectNewProviderToolBindings(bindings);
        foreach (var (providerName, internalName) in newBindings)
        {
            _providerToolBindings.Add(providerName, internalName);
        }
    }

    private Dictionary<string, string> CollectNewProviderToolBindings(
        IEnumerable<KeyValuePair<string, string>> bindings)
    {
        var newBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (providerName, internalName) in bindings)
        {
            AgentToolDefinition.ValidateProviderName(
                providerName,
                nameof(bindings));
            AgentToolDefinition.ValidateIdentifier(
                internalName,
                nameof(bindings),
                AgentToolDefinition.MaximumNameLength);
            if (!string.Equals(
                    AgentToolDefinition.GetProviderName(internalName),
                    providerName,
                    StringComparison.Ordinal))
            {
                throw new AgentConversationException();
            }

            if (_providerToolBindings.TryGetValue(
                    providerName,
                    out var retainedInternalName))
            {
                if (!string.Equals(
                        retainedInternalName,
                        internalName,
                        StringComparison.Ordinal))
                {
                    throw new AgentConversationException();
                }

                continue;
            }

            if (newBindings.TryGetValue(providerName, out var pendingInternalName))
            {
                if (!string.Equals(
                        pendingInternalName,
                        internalName,
                        StringComparison.Ordinal))
                {
                    throw new AgentConversationException();
                }

                continue;
            }

            newBindings.Add(providerName, internalName);
        }

        if (_providerToolBindings.Count
            > _limits.MaximumToolDefinitions - newBindings.Count)
        {
            throw new AgentLimitException();
        }

        return newBindings;
    }

    private static IEnumerable<KeyValuePair<string, string>>
        EnumerateProviderToolBindings(ImmutableArray<AgentMessage> conversation)
    {
        foreach (var message in conversation)
        {
            foreach (var toolCall in message.ToolCalls)
            {
                yield return new KeyValuePair<string, string>(
                    toolCall.ProviderName,
                    toolCall.ToolName);
            }
        }
    }

    private void ValidateToolSchemaWithinLimits(
        JsonElement element,
        int depth,
        ref int remainingNodes)
    {
        if (depth > _limits.MaximumJsonDepth || --remainingNodes < 0)
        {
            throw new AgentLimitException();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateToolSchemaWithinLimits(
                    property.Value,
                    depth + 1,
                    ref remainingNodes);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateToolSchemaWithinLimits(item, depth + 1, ref remainingNodes);
            }
        }
    }

    private ImmutableArray<AgentMessage> MaterializeInitialConversation(
        IEnumerable<AgentMessage> messages)
    {
        var builder = ImmutableArray.CreateBuilder<AgentMessage>();
        long byteCount = 0;
        foreach (var message in messages)
        {
            if (builder.Count == _limits.MaximumConversationMessages)
            {
                throw new AgentLimitException();
            }

            if (message is null)
            {
                throw new AgentConversationException();
            }

            ValidateImageBounds(message);

            byteCount += MessageByteCount(message);
            if (byteCount > _limits.MaximumConversationBytes)
            {
                throw new AgentLimitException();
            }

            builder.Add(message);
        }

        return builder.ToImmutable();
    }

    private void ValidateConversation(
        ImmutableArray<AgentMessage> conversation,
        ConversationTail tail = ConversationTail.Complete,
        bool enforceProviderContextLimits = true)
    {
        if (conversation.IsDefault
            || (enforceProviderContextLimits
                && conversation.Length > _limits.MaximumConversationMessages))
        {
            throw new AgentLimitException();
        }

        long byteCount = 0;
        var proposalIds = new HashSet<string>(StringComparer.Ordinal);
        var providerCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in conversation)
        {
            if (message is null)
            {
                throw new AgentConversationException();
            }

            ValidateImageBounds(message);
            var messageBytes = MessageByteCount(message);
            if (message.ReasoningSummary is { } reasoningSummary
                && Encoding.UTF8.GetByteCount(reasoningSummary)
                    > _limits.MaximumReasoningSummaryBytes)
            {
                throw new AgentLimitException();
            }

            if (message.ProviderReplayState is { } replayState)
            {
                var replayToolCount = replayState.Items.Count(item =>
                    item.ToolIndex.HasValue);
                if (replayToolCount != message.ToolCalls.Length
                    || !replayState.MatchesMessage(message))
                {
                    throw new AgentConversationException();
                }
            }

            byteCount += messageBytes;
            if (enforceProviderContextLimits
                && byteCount > _limits.MaximumConversationBytes)
            {
                throw new AgentLimitException();
            }
        }

        var index = CountLeadingSystemMessages(conversation);
        for (var systemIndex = 0; systemIndex < index; systemIndex++)
        {
            var system = conversation[systemIndex];
            if (string.IsNullOrWhiteSpace(system.Content)
                || !IsPlainMessage(system))
            {
                throw new AgentConversationException();
            }
        }

        var summaryStartsRetainedTurn = false;
        if (index < conversation.Length
            && conversation[index].Role == AgentMessageRole.Summary)
        {
            var summary = conversation[index];
            if (string.IsNullOrWhiteSpace(summary.Content)
                || !IsPlainMessage(summary))
            {
                throw new AgentConversationException();
            }

            index++;
            summaryStartsRetainedTurn = index < conversation.Length
                && conversation[index].Role == AgentMessageRole.Assistant;
        }

        while (index < conversation.Length)
        {
            if (!summaryStartsRetainedTurn)
            {
                var user = conversation[index];
                if (user.Role != AgentMessageRole.User
                    || (string.IsNullOrWhiteSpace(user.Content)
                        && user.Images.Length == 0)
                    || !IsPlainMessage(user))
                {
                    throw new AgentConversationException();
                }

                index++;
                if (index == conversation.Length && tail == ConversationTail.User)
                {
                    return;
                }
            }

            summaryStartsRetainedTurn = false;
            while (true)
            {
                if (index == conversation.Length
                    || conversation[index].Role != AgentMessageRole.Assistant)
                {
                    throw new AgentConversationException();
                }

                var assistant = conversation[index];
                if (assistant.ToolResult is not null)
                {
                    throw new AgentConversationException();
                }

                index++;
                if (assistant.ToolCalls.Length == 0)
                {
                    break;
                }

                ValidateAssistantToolCalls(
                    assistant.ToolCalls,
                    proposalIds,
                    providerCallIds);
                if (index == conversation.Length
                    && tail == ConversationTail.AssistantToolCalls)
                {
                    return;
                }

                foreach (var toolCall in assistant.ToolCalls)
                {
                    if (index == conversation.Length)
                    {
                        throw new AgentConversationException();
                    }

                    var toolMessage = conversation[index];
                    if (toolMessage.Role != AgentMessageRole.Tool
                        || toolMessage.ToolCalls.Length > 0
                        || toolMessage.ToolResult is not { } result
                        || !string.Equals(
                            toolMessage.Content,
                            result.Value.Content,
                            StringComparison.Ordinal)
                        || !ResultMatches(toolCall, result))
                    {
                        throw new AgentConversationException();
                    }

                    index++;
                }

                if (index == conversation.Length
                    && tail == ConversationTail.ToolResults)
                {
                    return;
                }

                // PI-style steering is a new local-human message inserted only
                // after the complete current tool batch has settled. It begins
                // a new turn without rewriting the preceding assistant/tool
                // exchange.
                if (index < conversation.Length
                    && conversation[index].Role == AgentMessageRole.User)
                {
                    break;
                }
            }
        }

        if (tail != ConversationTail.Complete)
        {
            throw new AgentConversationException();
        }
    }

    private void ValidateTranscript(
        ImmutableArray<AgentMessage> transcript,
        ConversationTail tail = ConversationTail.Complete) =>
        ValidateConversation(
            transcript,
            tail,
            enforceProviderContextLimits: false);

    private void ValidateAssistantToolCalls(
        ImmutableArray<AgentToolProposal> toolCalls,
        ISet<string> proposalIds,
        ISet<string> providerCallIds)
    {
        if (toolCalls.IsDefaultOrEmpty
            || toolCalls.Length > _limits.MaximumToolCallsPerTurn)
        {
            throw new AgentConversationException();
        }

        var generation = toolCalls[0].Generation;
        foreach (var toolCall in toolCalls)
        {
            if (toolCall is null
                || toolCall.Generation != generation
                || !proposalIds.Add(toolCall.Id)
                || !providerCallIds.Add(toolCall.ProviderCallId))
            {
                throw new AgentConversationException();
            }
        }
    }

    private static bool ResultMatches(
        AgentToolProposal proposal,
        AgentToolResult result) =>
        result.Generation == proposal.Generation
        && string.Equals(result.ProposalId, proposal.Id, StringComparison.Ordinal)
        && string.Equals(
            result.ProviderCallId,
            proposal.ProviderCallId,
            StringComparison.Ordinal);

    private static bool IsPlainMessage(AgentMessage message) =>
        message.ToolCalls.Length == 0 && message.ToolResult is null;

    private static long MessageByteCount(AgentMessage message)
    {
        long byteCount = Encoding.UTF8.GetByteCount(message.Content);
        foreach (var toolCall in message.ToolCalls)
        {
            byteCount = checked(
                byteCount
                + Encoding.UTF8.GetByteCount(toolCall.Id)
                + Encoding.UTF8.GetByteCount(toolCall.ProviderCallId)
                + Encoding.UTF8.GetByteCount(toolCall.ProviderName)
                + Encoding.UTF8.GetByteCount(toolCall.ToolName)
                + Encoding.UTF8.GetByteCount(toolCall.Arguments.GetRawText()));
        }

        if (message.ToolResult is { } result)
        {
            byteCount = checked(
                byteCount
                + Encoding.UTF8.GetByteCount(result.ProposalId)
                + Encoding.UTF8.GetByteCount(result.ProviderCallId)
                + Encoding.UTF8.GetByteCount(result.StableCode));
        }

        if (message.ReasoningSummary is { } reasoningSummary)
        {
            byteCount = checked(
                byteCount + Encoding.UTF8.GetByteCount(reasoningSummary));
        }

        if (message.ProviderReplayState is { } replayState)
        {
            byteCount = checked(
                byteCount
                + Encoding.UTF8.GetByteCount(replayState.Binding.ProfileId.Value)
                + Encoding.UTF8.GetByteCount(replayState.Binding.Model)
                + Encoding.UTF8.GetByteCount(
                    replayState.Binding.Endpoint.AbsoluteUri)
                + Encoding.UTF8.GetByteCount(
                    replayState.Binding.RouteIdentity));
            foreach (var item in replayState.Items)
            {
                byteCount = checked(
                    byteCount + Encoding.UTF8.GetByteCount(item.PayloadJson));
            }
        }

        foreach (var image in message.Images)
        {
            byteCount = checked(
                byteCount
                + Encoding.UTF8.GetByteCount(image.FileName)
                + Encoding.UTF8.GetByteCount(image.MediaType)
                + image.Content.Length);
        }

        return byteCount;
    }

    private static int CountLeadingSystemMessages(
        ImmutableArray<AgentMessage> conversation)
    {
        var count = 0;
        while (count < conversation.Length
               && conversation[count].Role == AgentMessageRole.System)
        {
            count++;
        }

        return count;
    }

    private void ValidateMessageBounds(AgentMessage message, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(message.Content) > maximumBytes)
        {
            throw new AgentLimitException();
        }

        ValidateImageBounds(message);
    }

    private void ValidateImageBounds(AgentMessage message)
    {
        if (message.Images.Length > _limits.MaximumImagesPerMessage)
        {
            throw new AgentLimitException();
        }

        long totalImageBytes = 0;
        foreach (var image in message.Images)
        {
            totalImageBytes = checked(totalImageBytes + image.Content.Length);
            if (totalImageBytes > _limits.MaximumTotalImageBytesPerMessage)
            {
                throw new AgentLimitException();
            }
        }
    }

    private AgentSessionSnapshot SnapshotUnsafe() =>
        new(
            RunId,
            _state,
            _revision,
            _sequence,
            _generation,
            _conversation,
            _transcript,
            _pendingToolProposals);

    internal NativeAgentSession CreateReadyContinuation(AgentRunId runId)
    {
        lock (_gate)
        {
            var continuation = new NativeAgentSession(
                runId,
                _conversation,
                _limits,
                _timeProvider)
            {
                _transcript = _transcript,
                _conversationTitle = _conversationTitle,
                _conversationProviderId = _conversationProviderId,
                _conversationModel = _conversationModel,
            };
            continuation.RegisterProviderToolBindings(_providerToolBindings);
            return continuation;
        }
    }

    private void AppendEventUnsafe(
        AgentRunEventKind kind,
        long generation,
        string? provisionalText = null,
        string? provisionalReasoningSummary = null,
        AgentTurnErrorCode? errorCode = null,
        int toolProposalCount = 0)
    {
        var nextRevision = checked(_revision + 1);
        var nextSequence = checked(_sequence + 1);
        _revision = nextRevision;
        _sequence = nextSequence;
        _events.Enqueue(new AgentRunEvent(
            RunId,
            _sequence,
            _revision,
            generation,
            kind,
            _timeProvider.GetUtcNow(),
            provisionalText,
            provisionalReasoningSummary,
            errorCode,
            toolProposalCount));
        if (_events.Count > _limits.MaximumRetainedEvents)
        {
            _events.Dequeue();
        }

        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ActiveTurn : IDisposable
    {
        private readonly CancellationLifetime _cancellation = new();
        private readonly TaskCompletionSource _cancelled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveTurn(
            long generation,
            long baseConversationRevision,
            ImmutableArray<AgentMessage> baseConversation,
            ImmutableArray<AgentMessage> inputMessages,
            ImmutableArray<AgentToolDefinition> tools,
            AgentReasoningEffort reasoningEffort,
            ActiveTurnKind kind)
        {
            Generation = generation;
            BaseConversationRevision = baseConversationRevision;
            BaseConversation = baseConversation;
            InputMessages = inputMessages;
            Tools = tools;
            ReasoningEffort = reasoningEffort;
            Kind = kind;
            Token = _cancellation.Token;
        }

        public long Generation { get; }

        public long BaseConversationRevision { get; }

        public ImmutableArray<AgentMessage> BaseConversation { get; }

        public ImmutableArray<AgentMessage> InputMessages { get; }

        public ImmutableArray<AgentToolDefinition> Tools { get; }

        public AgentReasoningEffort ReasoningEffort { get; }

        public ActiveTurnKind Kind { get; }

        public CancellationToken Token { get; }

        public Task Cancellation => _cancelled.Task;

        public ActiveTurn? SteeringReplacement { get; private set; }

        public bool TryCancel() => _cancellation.TryCancel();

        public void Dispose() => _cancellation.Dispose();

        public void SignalCancellation() => _cancelled.TrySetResult();

        public void SetSteeringReplacement(ActiveTurn replacement)
        {
            if (SteeringReplacement is not null)
            {
                throw new InvalidOperationException(
                    "An agent generation can only be steered once.");
            }

            SteeringReplacement = replacement
                ?? throw new ArgumentNullException(nameof(replacement));
        }
    }

    private sealed record CompactionCapture(
        ImmutableArray<AgentMessage> Conversation,
        long ConversationRevision,
        long Generation,
        int SystemMessageCount,
        int CutIndex,
        int? TurnStartIndex);

    private sealed record CompactionCut(int CutIndex, int? TurnStartIndex);

    private enum CompactionSelectionKind
    {
        RetainedTurns,
        TokenBudget,
    }

    private readonly record struct CompactionSelection(
        CompactionSelectionKind Kind,
        int Value,
        AgentCompactionSettings? Settings)
    {
        public static CompactionSelection ForRetainedTurns(int minimumRetainedTurns) =>
            new(CompactionSelectionKind.RetainedTurns, minimumRetainedTurns, null);

        public static CompactionSelection ForTokenBudget(
            int contextWindowTokens,
            AgentCompactionSettings settings) =>
            new(CompactionSelectionKind.TokenBudget, contextWindowTokens, settings);
    }

    private sealed record PendingToolTurn(
        ImmutableArray<AgentMessage> BaseConversation,
        ImmutableArray<AgentToolDefinition> Tools,
        AgentReasoningEffort ReasoningEffort);

    private sealed record PendingToolContinuation(
        AgentReasoningEffort ReasoningEffort);

    private enum ConversationTail
    {
        Complete,
        User,
        AssistantToolCalls,
        ToolResults,
    }

    private enum ActiveTurnKind
    {
        InitialUser,
        SteeredUser,
        ToolContinuation,
        QueuedSteering,
    }

    private sealed class CompactionLease : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationLifetime _cancellation = new();
        private bool _cancellationRequested;
        private bool _invocationStarted;

        public CancellationToken Token => _cancellation.Token;

        public bool TryBeginInvocation()
        {
            lock (_gate)
            {
                if (_cancellationRequested || _invocationStarted)
                {
                    return false;
                }

                _invocationStarted = true;
                return true;
            }
        }

        public bool TryCancel()
        {
            lock (_gate)
            {
                _cancellationRequested = true;
            }

            return _cancellation.TryCancel();
        }

        public void Dispose() => _cancellation.Dispose();
    }

    private sealed class CancellationLifetime : IDisposable
    {
        private readonly Lock _gate = new();
        private Task? _cancellationTask;
        private CancellationTokenSource? _source = new();
        private bool _disposeRequested;

        public CancellationToken Token
        {
            get
            {
                lock (_gate)
                {
                    return _source?.Token ?? new CancellationToken(canceled: true);
                }
            }
        }

        public bool TryCancel()
        {
            CancellationTokenSource source;
            Task cancellationTask;
            lock (_gate)
            {
                if (_source is null)
                {
                    return false;
                }

                source = _source;
                if (_cancellationTask is not null)
                {
                    return true;
                }

                try
                {
                    cancellationTask = source.CancelAsync();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return false;
                }

                _cancellationTask = cancellationTask;
            }

            _ = ObserveCancellationAsync(source, cancellationTask);
            return true;
        }

        public void Dispose()
        {
            CancellationTokenSource? dispose;
            lock (_gate)
            {
                if (_cancellationTask is { IsCompleted: false })
                {
                    _disposeRequested = true;
                    return;
                }

                dispose = _source;
                _source = null;
            }

            dispose?.Dispose();
        }

        private async Task ObserveCancellationAsync(
            CancellationTokenSource source,
            Task cancellationTask)
        {
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Dependency-owned cancellation callbacks are isolated from the session's
                // already-fenced public cancellation path.
            }
            finally
            {
                CancellationTokenSource? dispose = null;
                lock (_gate)
                {
                    if (ReferenceEquals(_cancellationTask, cancellationTask))
                    {
                        _cancellationTask = null;
                    }

                    if (_disposeRequested && ReferenceEquals(_source, source))
                    {
                        _source = null;
                        dispose = source;
                    }
                }

                dispose?.Dispose();
            }
        }
    }

    private sealed class AgentLimitException : Exception;

    private sealed class AgentConversationException : Exception;
}
