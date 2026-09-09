using System.Collections.Immutable;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private const int MaximumQueuedFollowUps = 8;
    private const int MaximumQueuedFollowUpBytes = 256 * 1024;

    private readonly List<GovernedAgentQueuedFollowUp> _queuedFollowUps = [];
    private int _queuedFollowUpBytes;
    private int _acceptedFollowUpsThisTurn;
    private GovernedAgentFollowUp? _activeFollowUp;
    private bool _activeFollowUpPromptCommitted;
    private bool _initialPromptCommittedThisTurn;

    public ValueTask<GovernedAgentFollowUpResult> QueueFollowUpAsync(
        GovernedAgentFollowUp request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        GovernedAgentFollowUpResult result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnCancellation is null
                || _session is null
                || !_snapshot.CanQueueFollowUp)
            {
                result = UnavailableFollowUpResultUnsafe();
            }
            else
            {
                var byteCount = Encoding.UTF8.GetByteCount(request.Message);
                if (_acceptedFollowUpsThisTurn >= MaximumQueuedFollowUps
                    || _queuedFollowUps.Count >= MaximumQueuedFollowUps
                    || byteCount > MaximumQueuedFollowUpBytes - _queuedFollowUpBytes)
                {
                    result = new GovernedAgentFollowUpResult(
                        false,
                        "agent_follow_up_queue_full",
                        "The bounded follow-up queue is full.",
                        _queuedFollowUps.Count);
                }
                else
                {
                    var queued = new GovernedAgentQueuedFollowUp(
                        AgentQueuedFollowUpId.New(),
                        request.Message,
                        request.ReasoningEffort,
                        request.Delivery);
                    if (request.Delivery == GovernedAgentFollowUpDelivery.Steering)
                    {
                        _queuedFollowUps.Insert(
                            CountLeadingSteeringFollowUpsUnsafe(),
                            queued);
                    }
                    else
                    {
                        _queuedFollowUps.Add(queued);
                    }

                    _queuedFollowUpBytes = checked(_queuedFollowUpBytes + byteCount);
                    _acceptedFollowUpsThisTurn++;
                    UpdateQueuedFollowUpsSnapshotUnsafe();
                    result = new GovernedAgentFollowUpResult(
                        true,
                        request.Delivery == GovernedAgentFollowUpDelivery.Steering
                            ? "agent_steering_queued"
                            : "agent_follow_up_queued",
                        request.Delivery == GovernedAgentFollowUpDelivery.Steering
                            ? "The message will be sent at the next safe agent boundary."
                            : "The follow-up will run when the agent would otherwise stop.",
                        _queuedFollowUps.Count,
                        queued.Id);
                }
            }
        }

        NotifyChangedWhenAccepted(result);
        return ValueTask.FromResult(result);
    }

    public ValueTask<GovernedAgentFollowUpResult> UpdateQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        GovernedAgentFollowUp request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        GovernedAgentFollowUpResult result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var index = FindQueuedFollowUpUnsafe(id);
            if (index < 0)
            {
                result = MissingFollowUpResultUnsafe();
            }
            else
            {
                var current = _queuedFollowUps[index];
                var oldBytes = Encoding.UTF8.GetByteCount(current.Message);
                var newBytes = Encoding.UTF8.GetByteCount(request.Message);
                if (newBytes > MaximumQueuedFollowUpBytes
                    - (_queuedFollowUpBytes - oldBytes))
                {
                    result = new GovernedAgentFollowUpResult(
                        false,
                        "agent_follow_up_queue_full",
                        "The edited message exceeds the bounded queue size.",
                        _queuedFollowUps.Count,
                        id);
                }
                else
                {
                    _queuedFollowUps[index] = current with
                    {
                        Message = request.Message,
                        ReasoningEffort = request.ReasoningEffort,
                    };
                    _queuedFollowUpBytes = checked(
                        _queuedFollowUpBytes - oldBytes + newBytes);
                    UpdateQueuedFollowUpsSnapshotUnsafe();
                    result = AcceptedQueueMutation(
                        id,
                        "agent_follow_up_updated",
                        "The queued message was updated.");
                }
            }
        }

        NotifyChangedWhenAccepted(result);
        return ValueTask.FromResult(result);
    }

    public ValueTask<GovernedAgentFollowUpResult> RemoveQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GovernedAgentFollowUpResult result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var index = FindQueuedFollowUpUnsafe(id);
            if (index < 0)
            {
                result = MissingFollowUpResultUnsafe();
            }
            else
            {
                RemoveQueuedFollowUpAtUnsafe(index);
                UpdateQueuedFollowUpsSnapshotUnsafe();
                result = AcceptedQueueMutation(
                    id,
                    "agent_follow_up_removed",
                    "The queued message was removed.");
            }
        }

        NotifyChangedWhenAccepted(result);
        return ValueTask.FromResult(result);
    }

    public ValueTask<GovernedAgentFollowUpResult> MoveQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        int newIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GovernedAgentFollowUpResult result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var index = FindQueuedFollowUpUnsafe(id);
            if (index < 0)
            {
                result = MissingFollowUpResultUnsafe();
            }
            else if (newIndex < 0 || newIndex >= _queuedFollowUps.Count)
            {
                result = new GovernedAgentFollowUpResult(
                    false,
                    "agent_follow_up_position_invalid",
                    "The requested queue position is no longer available.",
                    _queuedFollowUps.Count,
                    id);
            }
            else if (!IsValidQueuePositionUnsafe(
                         _queuedFollowUps[index],
                         newIndex))
            {
                result = new GovernedAgentFollowUpResult(
                    false,
                    "agent_follow_up_position_invalid",
                    "Steering messages remain ahead of ordinary follow-ups.",
                    _queuedFollowUps.Count,
                    id);
            }
            else
            {
                var item = _queuedFollowUps[index];
                _queuedFollowUps.RemoveAt(index);
                _queuedFollowUps.Insert(newIndex, item);
                UpdateQueuedFollowUpsSnapshotUnsafe();
                result = AcceptedQueueMutation(
                    id,
                    "agent_follow_up_moved",
                    "The queued message was moved.");
            }
        }

        NotifyChangedWhenAccepted(result);
        return ValueTask.FromResult(result);
    }

    public ValueTask<GovernedAgentFollowUpResult> SteerQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GovernedAgentFollowUpResult result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var index = FindQueuedFollowUpUnsafe(id);
            if (index < 0)
            {
                result = MissingFollowUpResultUnsafe();
            }
            else
            {
                var item = _queuedFollowUps[index] with
                {
                    Delivery = GovernedAgentFollowUpDelivery.Steering,
                };
                _queuedFollowUps.RemoveAt(index);
                _queuedFollowUps.Insert(0, item);
                UpdateQueuedFollowUpsSnapshotUnsafe();
                result = AcceptedQueueMutation(
                    id,
                    "agent_steering_queued",
                    "The message will be sent at the next safe agent boundary.");
            }
        }

        NotifyChangedWhenAccepted(result);
        return ValueTask.FromResult(result);
    }

    private GovernedAgentFollowUp? TakeNextSteeringFollowUp()
    {
        lock (_gate)
        {
            var index = _queuedFollowUps.FindIndex(item =>
                item.Delivery == GovernedAgentFollowUpDelivery.Steering);
            if (index < 0)
            {
                return null;
            }

            var queued = RemoveQueuedFollowUpAtUnsafe(index);
            _activeFollowUp = ToFollowUp(queued);
            _activeFollowUpPromptCommitted = false;
            UpdateQueuedFollowUpsSnapshotUnsafe();
            return _activeFollowUp;
        }
    }

    private void MarkCurrentPromptCommitted()
    {
        lock (_gate)
        {
            if (_activeFollowUp is null)
            {
                _initialPromptCommittedThisTurn = true;
            }
            else
            {
                _activeFollowUpPromptCommitted = true;
            }
        }
    }

    private FollowUpTurnTransition CompleteTurnOrTakeNextFollowUp(
        CancellationTokenSource turnCancellation,
        IReadOnlyList<AgentChatMessage> committedMessages)
    {
        lock (_gate)
        {
            if (_disposed
                || !ReferenceEquals(_turnCancellation, turnCancellation)
                || _snapshot.State == GovernedAgentState.Cancelled)
            {
                return FollowUpTurnTransition.Interrupted;
            }

            _activeFollowUp = null;
            _activeFollowUpPromptCommitted = false;
            if (_queuedFollowUps.Count == 0)
            {
                _snapshot = _snapshot with
                {
                    State = GovernedAgentState.Ready,
                    Messages = CopyMessages(committedMessages),
                    ProvisionalAssistantText = string.Empty,
                    ProvisionalReasoningSummary = string.Empty,
                    PendingApproval = null,
                    PendingQuestion = null,
                    PendingCapabilityRequest = null,
                    ActiveTool = null,
                    PanelActivity = null,
                    CurrentProgress = null,
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                    Status = string.Empty,
                };
                return FollowUpTurnTransition.Completed;
            }

            var queued = RemoveQueuedFollowUpAtUnsafe(0);
            var followUp = ToFollowUp(queued);
            _activeFollowUp = followUp;
            UpdateQueuedFollowUpsSnapshotUnsafe();
            _snapshot = _snapshot with
            {
                State = GovernedAgentState.StreamingProvider,
                Messages = CopyMessages(
                    committedMessages.Append(
                        new AgentChatMessage(
                            AgentChatMessageRole.User,
                            queued.Message))),
                ProvisionalAssistantText = string.Empty,
                ProvisionalReasoningSummary = string.Empty,
                Status = queued.Delivery == GovernedAgentFollowUpDelivery.Steering
                    ? "Applying steering…"
                    : "Preparing the queued follow-up…",
            };
            return FollowUpTurnTransition.Start(followUp);
        }
    }

    private IReadOnlyList<GovernedAgentFollowUp>
        CaptureRecoverableFollowUpsUnsafe()
    {
        var recoverable = new List<GovernedAgentFollowUp>(
            _queuedFollowUps.Count + 1);
        if (_activeFollowUp is not null && !_activeFollowUpPromptCommitted)
        {
            recoverable.Add(_activeFollowUp);
        }

        recoverable.AddRange(_queuedFollowUps.Select(ToFollowUp));
        return Array.AsReadOnly(recoverable.ToArray());
    }

    private void ClearQueuedFollowUpsUnsafe()
    {
        _queuedFollowUps.Clear();
        _queuedFollowUpBytes = 0;
        if (!_disposed)
        {
            UpdateQueuedFollowUpsSnapshotUnsafe();
        }
    }

    private void DiscardFollowUpsUnsafe()
    {
        _activeFollowUp = null;
        _activeFollowUpPromptCommitted = false;
        ClearQueuedFollowUpsUnsafe();
    }

    private int FindQueuedFollowUpUnsafe(AgentQueuedFollowUpId id) =>
        _queuedFollowUps.FindIndex(item => item.Id == id);

    private int CountLeadingSteeringFollowUpsUnsafe()
    {
        var count = 0;
        while (count < _queuedFollowUps.Count
               && _queuedFollowUps[count].Delivery
                   == GovernedAgentFollowUpDelivery.Steering)
        {
            count++;
        }

        return count;
    }

    private bool IsValidQueuePositionUnsafe(
        GovernedAgentQueuedFollowUp item,
        int newIndex)
    {
        var steeringCount = CountLeadingSteeringFollowUpsUnsafe();
        return item.Delivery == GovernedAgentFollowUpDelivery.Steering
            ? newIndex < steeringCount
            : newIndex >= steeringCount;
    }

    private GovernedAgentQueuedFollowUp RemoveQueuedFollowUpAtUnsafe(int index)
    {
        var queued = _queuedFollowUps[index];
        _queuedFollowUps.RemoveAt(index);
        _queuedFollowUpBytes = checked(
            _queuedFollowUpBytes - Encoding.UTF8.GetByteCount(queued.Message));
        return queued;
    }

    private void UpdateQueuedFollowUpsSnapshotUnsafe() =>
        _snapshot = _snapshot with
        {
            QueuedFollowUpCount = _queuedFollowUps.Count,
            QueuedFollowUps = [.. _queuedFollowUps],
        };

    private GovernedAgentFollowUpResult AcceptedQueueMutation(
        AgentQueuedFollowUpId id,
        string code,
        string message) =>
        new(true, code, message, _queuedFollowUps.Count, id);

    private GovernedAgentFollowUpResult MissingFollowUpResultUnsafe() =>
        new(
            false,
            "agent_follow_up_not_found",
            "The queued message is no longer available.",
            _queuedFollowUps.Count);

    private GovernedAgentFollowUpResult UnavailableFollowUpResultUnsafe() =>
        new(
            false,
            "agent_follow_up_unavailable",
            "Messages can be queued only while a governed turn is active.",
            _queuedFollowUps.Count);

    private static GovernedAgentFollowUp ToFollowUp(
        GovernedAgentQueuedFollowUp queued) =>
        new(queued.Message, queued.ReasoningEffort, queued.Delivery);

    private void NotifyChangedWhenAccepted(GovernedAgentFollowUpResult result)
    {
        if (result.IsAccepted)
        {
            NotifyChanged();
        }
    }

    private sealed record FollowUpTurnTransition(
        bool IsCompleted,
        GovernedAgentFollowUp? Next)
    {
        public static FollowUpTurnTransition Interrupted { get; } =
            new(false, null);

        public static FollowUpTurnTransition Completed { get; } =
            new(true, null);

        public static FollowUpTurnTransition Start(
            GovernedAgentFollowUp followUp) =>
            new(false, followUp);
    }
}
