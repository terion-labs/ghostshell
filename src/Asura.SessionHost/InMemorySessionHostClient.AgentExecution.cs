using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private HostResult<AgentContextSnapshot> ResolveExactAgentContext(
        AgentTarget target)
    {
        HostedSession[] hostedSessions;
        lock (_gate)
        {
            hostedSessions = [.. _sessions.Values];
        }

        var sessions = hostedSessions
            .Select(session => session.Snapshot().Descriptor)
            .ToDictionary(session => session.Id);
        return ResolveAgentContext(
            new AgentContextRequest(target, maximumPanelCount: 1),
            sessions);
    }

    private async ValueTask<HostResult<T>> CompleteConsumedAgentActionAsync<T>(
        AgentActionPermit permit,
        AgentActionCompletion completion,
        HostResult<T> result,
        long revision)
    {
        if (await ConfirmConsumedAgentActionAsync(permit, completion)
                .ConfigureAwait(false))
        {
            return result;
        }

        return HostResult<T>.Fail(
            AgentCompletionAuditError(),
            revision);
    }

    private AgentActionCompletion Completion(
        AgentActionPermit permit,
        AgentActionOutcome outcome,
        string stableCode,
        int? resultCount = null)
    {
        var finishedAt = _timeProvider.GetUtcNow();
        if (finishedAt < permit.StartedAtUtc)
        {
            finishedAt = permit.StartedAtUtc;
        }

        return new AgentActionCompletion(
            outcome,
            stableCode,
            finishedAt,
            resultCount);
    }
}
