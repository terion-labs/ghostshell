using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteReportProgressAsync(
        AgentToolProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = AgentReportProgressIntrinsic.Parse(proposal);
        if (parsed is AgentReportProgressParseResult.Rejected rejected)
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

        var progress =
            ((AgentReportProgressParseResult.Parsed)parsed).Progress;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnCancellation is null
                || _snapshot.State != GovernedAgentState.StreamingProvider)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _snapshot = _snapshot with
            {
                CurrentProgress = progress,
            };
        }

        NotifyChanged();
        return new AgentToolResult(
            proposal,
            AgentToolResultStatus.Succeeded,
            "tool_succeeded",
            JsonValue("""{"ok":true}"""));
    }

    private static AgentToolResult CreateIntrinsicFailureResult(
        AgentToolProposal proposal,
        string stableCode) =>
        new(
            proposal,
            AgentToolResultStatus.Failed,
            stableCode,
            JsonValue(AgentToolResultJson.Failure(
                stableCode,
                retryable: false)));
}
