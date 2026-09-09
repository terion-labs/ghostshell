using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private SteeringLease? _steeringLease;

    public async ValueTask<GovernedAgentSteeringResult> SteerAsync(
        GovernedAgentSteering request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SteeringLease lease;
        CancellationTokenSource inspectionCancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return SteeringFailure(
                    "agent_steering_cancelled",
                    "The steering update was cancelled.");
            }

            if (_snapshot.RunId != request.RunId)
            {
                return SteeringFailure(
                    "agent_steering_run_changed",
                    "That agent run is no longer active.");
            }

            if (_steeringLease is not { } available)
            {
                return SteeringFailure(
                    "agent_steering_not_available",
                    "Steering is available only while the initial provider response is streaming.");
            }

            if (available.AttemptInFlight)
            {
                if (request.ExpectedGeneration
                    != available.Generation.Generation)
                {
                    return SteeringFailure(
                        "agent_steering_generation_changed",
                        "That steering update belongs to an earlier provider response.");
                }

                return SteeringFailure(
                    "agent_steering_in_progress",
                    "Another steering update is already being checked.");
            }

            if (request.ExpectedGeneration
                    != available.Generation.Generation
                || _snapshot.SteeringGeneration
                    != request.ExpectedGeneration)
            {
                return SteeringFailure(
                    "agent_steering_generation_changed",
                    "That steering update belongs to an earlier provider response.");
            }

            if (!_snapshot.CanSteer)
            {
                return SteeringFailure(
                    "agent_steering_not_available",
                    "Steering is available only while the initial provider response is streaming.");
            }

            inspectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    available.TurnCancellation.Token);
            available.AttemptInFlight = true;
            lease = available;
            _snapshot = _snapshot with
            {
                SteeringAvailable = false,
                SteeringGeneration = null,
                Status = "Checking the steering update against the current run…",
            };
        }

        NotifyChanged();

        AgentContextSnapshot? contexts;
        using var ownedInspectionCancellation = inspectionCancellation;
        try
        {
            contexts = await InspectRunTargetContextAsync(
                    lease.Target,
                    GetOrCreateAgent(),
                    inspectionCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (lease.TurnToken.IsCancellationRequested)
        {
            CloseSteeringLease(lease);
            return SteeringFailure(
                "agent_steering_not_available",
                "The run lifecycle changed before steering could be applied.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            RestoreSteeringAfterCancelledAttempt(lease);
            return SteeringFailure(
                "agent_steering_cancelled",
                "The steering update was cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            CloseSteeringLease(
                lease,
                "The exact agent target could not be rechecked.");
            return SteeringFailure(
                "agent_steering_target_unavailable",
                "The exact agent target could not be rechecked.");
        }

        if (lease.TurnToken.IsCancellationRequested)
        {
            CloseSteeringLease(lease);
            return SteeringFailure(
                "agent_steering_not_available",
                "The run lifecycle changed before steering could be applied.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            RestoreSteeringAfterCancelledAttempt(lease);
            return SteeringFailure(
                "agent_steering_cancelled",
                "The steering update was cancelled.");
        }

        if (contexts is null)
        {
            CloseSteeringLease(
                lease,
                "The exact agent target is no longer available.");
            return SteeringFailure(
                "agent_steering_target_unavailable",
                "The exact agent target is no longer available.");
        }

        if (!MatchesPinnedScope(contexts))
        {
            CloseSteeringLease(
                lease,
                "The agent target changed before steering could be applied.");
            return SteeringFailure(
                "agent_steering_target_changed",
                "The agent target changed before steering could be applied.");
        }

        GovernedAgentSteeringResult outcome;
        var changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_steeringLease, lease))
            {
                return SteeringFailure(
                    "agent_steering_not_available",
                    "The initial provider response finished before steering could be applied.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                lease.AttemptInFlight = false;
                var canRetry = SteeringLifecycleMatchesUnsafe(lease)
                    && SteeringProviderMatchesUnsafe(lease)
                    && SteeringPolicyMatchesUnsafe(lease);
                if (!canRetry)
                {
                    _steeringLease = null;
                }

                _snapshot = _snapshot with
                {
                    SteeringAvailable = canRetry,
                    SteeringGeneration = canRetry
                        ? lease.Generation.Generation
                        : null,
                    Status = canRetry
                        ? "Waiting for the provider…"
                        : _snapshot.Status,
                };
                changed = true;
                outcome = SteeringFailure(
                    "agent_steering_cancelled",
                    "The steering update was cancelled.");
            }
            else if (!SteeringLifecycleMatchesUnsafe(lease))
            {
                _steeringLease = null;
                _snapshot = _snapshot with
                {
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                    Status =
                        "The run lifecycle changed before steering could be applied.",
                };
                changed = true;
                outcome = SteeringFailure(
                    "agent_steering_not_available",
                    "The run lifecycle changed before steering could be applied.");
            }
            else if (!SteeringProviderMatchesUnsafe(lease))
            {
                _steeringLease = null;
                _snapshot = _snapshot with
                {
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                    Status =
                        "The pinned AI-provider profile changed before steering could be applied.",
                };
                changed = true;
                outcome = SteeringFailure(
                    "agent_steering_provider_changed",
                    "The pinned AI-provider profile changed before steering could be applied.");
            }
            else if (!SteeringPolicyMatchesUnsafe(lease))
            {
                _steeringLease = null;
                _snapshot = _snapshot with
                {
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                    Status =
                        "The governed run policy changed before steering could be applied.",
                };
                changed = true;
                outcome = SteeringFailure(
                    "agent_steering_policy_changed",
                    "The governed run policy changed before steering could be applied.");
            }
            else
            {
                // Holding the runtime gate makes this kernel call the single
                // linearization point against Stop, Clear, Dispose, and policy
                // changes. The kernel independently races the old provider's
                // commit and preserves its provider and exact tool manifest.
                var result = lease.Session.Steer(
                    request.ExpectedGeneration,
                    request.Update);
                outcome = ApplyKernelSteeringResultUnsafe(lease, result);
                changed = true;
            }
        }

        if (changed)
        {
            NotifyChanged();
        }

        return outcome;
    }

    private SteeringLease? OpenInitialSteering(
        NativeAgentSession session,
        CancellationTokenSource turnCancellation,
        ProviderGeneration generation)
    {
        SteeringLease? lease = null;
        lock (_gate)
        {
            var sessionSnapshot = session.Snapshot();
            var providerBinding = _providerBinding;
            if (_disposed
                || _clearing
                || _policyChangeInFlight
                || !ReferenceEquals(_turnCancellation, turnCancellation)
                || turnCancellation.IsCancellationRequested
                || !ReferenceEquals(_session, session)
                || !_runRegistered
                || _snapshot.State != GovernedAgentState.StreamingProvider
                || _snapshot.RunId != session.RunId
                || _snapshot.Target is not { } target
                || providerBinding is null
                || !providerBinding.IsCurrent
                || sessionSnapshot.State != NativeAgentSessionState.Streaming
                || sessionSnapshot.Generation != generation.Generation
                || _snapshot.Messages.Count == 0)
            {
                return null;
            }

            lease = new SteeringLease(
                session,
                turnCancellation,
                providerBinding,
                providerBinding.Revision,
                target,
                _baselinePolicy,
                _runPolicy,
                _effectivePolicy,
                _policyGeneration,
                generation,
                _snapshot.Messages.Count - 1);
            _steeringLease = lease;
            _snapshot = _snapshot with
            {
                SteeringAvailable = true,
                SteeringGeneration = generation.Generation,
            };
        }

        NotifyChanged();
        return lease;
    }

    private void CloseInitialSteering(SteeringLease? lease)
    {
        if (lease is null)
        {
            return;
        }

        var changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_steeringLease, lease))
            {
                _steeringLease = null;
                changed = _snapshot.SteeringAvailable;
                _snapshot = _snapshot with
                {
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                };
            }
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    private void CloseSteeringLease(
        SteeringLease lease,
        string? status = null)
    {
        var changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_steeringLease, lease))
            {
                _steeringLease = null;
                changed = true;
                _snapshot = _snapshot with
                {
                    SteeringAvailable = false,
                    SteeringGeneration = null,
                    Status = status ?? _snapshot.Status,
                };
            }
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    private void RestoreSteeringAfterCancelledAttempt(SteeringLease lease)
    {
        var changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_steeringLease, lease)
                && SteeringLifecycleMatchesUnsafe(lease)
                && SteeringProviderMatchesUnsafe(lease)
                && SteeringPolicyMatchesUnsafe(lease))
            {
                lease.AttemptInFlight = false;
                _snapshot = _snapshot with
                {
                    SteeringAvailable = true,
                    SteeringGeneration = lease.Generation.Generation,
                    Status = "Waiting for the provider…",
                };
                changed = true;
            }
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    private bool SteeringLifecycleMatchesUnsafe(SteeringLease lease)
    {
        var sessionSnapshot = lease.Session.Snapshot();
        return !_disposed
            && !_clearing
            && !_policyChangeInFlight
            && _runRegistered
            && ReferenceEquals(_turnCancellation, lease.TurnCancellation)
            && !lease.TurnToken.IsCancellationRequested
            && ReferenceEquals(_session, lease.Session)
            && _snapshot.State == GovernedAgentState.StreamingProvider
            && _snapshot.RunId == lease.Session.RunId
            && _snapshot.Target == lease.Target
            && _snapshot.PendingApproval is null
            && _snapshot.PendingQuestion is null
            && _snapshot.PendingCapabilityRequest is null
            && _snapshot.ActiveTool is null
            && sessionSnapshot.State == NativeAgentSessionState.Streaming
            && sessionSnapshot.Generation == lease.Generation.Generation;
    }

    private bool SteeringProviderMatchesUnsafe(SteeringLease lease) =>
        ReferenceEquals(_providerBinding, lease.ProviderBinding)
        && lease.ProviderBinding.IsCurrent
        && lease.ProviderBinding.Revision == lease.ProviderRevision
        && _snapshot.ProviderId == lease.ProviderBinding.ProfileId;

    private bool SteeringPolicyMatchesUnsafe(SteeringLease lease) =>
        _policyGeneration == lease.PolicyGeneration
        && PoliciesEqual(_baselinePolicy, lease.BaselinePolicy)
        && PoliciesEqual(_runPolicy, lease.RunPolicy)
        && PoliciesEqual(_effectivePolicy, lease.EffectivePolicy);

    private GovernedAgentSteeringResult ApplyKernelSteeringResultUnsafe(
        SteeringLease lease,
        AgentSteerResult result)
    {
        _steeringLease = null;
        if (result.Succeeded
            && result.ReplacementGeneration is { } replacementGeneration
            && result.ReplacementUserMessage is { } replacementUserMessage)
        {
            lease.Generation.Replace(replacementGeneration);
            _snapshot = _snapshot with
            {
                Messages = CopyMessages(
                    _snapshot.Messages
                        .Take(lease.BaseMessageCount)
                        .Append(
                            new AgentChatMessage(
                                AgentChatMessageRole.User,
                                replacementUserMessage))),
                ProvisionalAssistantText = string.Empty,
                ProvisionalReasoningSummary = string.Empty,
                SteeringAvailable = false,
                SteeringGeneration = null,
                Status = "Steering applied · waiting for the revised provider response…",
            };
            return new GovernedAgentSteeringResult(
                true,
                "agent_steering_applied",
                "The steering update was applied to this provider response.");
        }

        _snapshot = _snapshot with
        {
            SteeringAvailable = false,
            SteeringGeneration = null,
        };
        var failure = result.ErrorCode switch
        {
            AgentSteerErrorCode.LimitExceeded => SteeringFailure(
                "agent_steering_limit_exceeded",
                "The combined user input exceeds the provider-turn limit."),
            AgentSteerErrorCode.ProviderOperationLimit => SteeringFailure(
                "agent_steering_provider_busy",
                "The provider cannot start the revised response."),
            AgentSteerErrorCode.ConversationConflict => SteeringFailure(
                "agent_steering_transcript_changed",
                "The run transcript changed before steering could be applied."),
            _ => SteeringFailure(
                "agent_steering_not_available",
                "The initial provider response finished before steering could be applied."),
        };
        _snapshot = _snapshot with
        {
            Status = failure.Message,
        };
        return failure;
    }

    private static GovernedAgentSteeringResult SteeringFailure(
        string code,
        string message) =>
        new(false, code, message);

    private sealed class ProviderGeneration(long generation)
    {
        private long _generation = generation;

        public long Generation => Interlocked.Read(ref _generation);

        public void Replace(long replacementGeneration)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                replacementGeneration);
            Interlocked.Exchange(ref _generation, replacementGeneration);
        }
    }

    private sealed class SteeringLease(
        NativeAgentSession session,
        CancellationTokenSource turnCancellation,
        IAgentProviderBinding providerBinding,
        long providerRevision,
        AgentTarget target,
        AgentPolicy baselinePolicy,
        AgentPolicy runPolicy,
        AgentPolicy effectivePolicy,
        long policyGeneration,
        ProviderGeneration generation,
        int baseMessageCount)
    {
        public NativeAgentSession Session { get; } = session;

        public CancellationTokenSource TurnCancellation { get; } =
            turnCancellation;

        public CancellationToken TurnToken { get; } =
            turnCancellation.Token;

        public IAgentProviderBinding ProviderBinding { get; } =
            providerBinding;

        public long ProviderRevision { get; } = providerRevision;

        public AgentTarget Target { get; } = target;

        public AgentPolicy BaselinePolicy { get; } = baselinePolicy;

        public AgentPolicy RunPolicy { get; } = runPolicy;

        public AgentPolicy EffectivePolicy { get; } = effectivePolicy;

        public long PolicyGeneration { get; } = policyGeneration;

        public ProviderGeneration Generation { get; } = generation;

        public int BaseMessageCount { get; } = baseMessageCount;

        public bool AttemptInFlight { get; set; }
    }
}
