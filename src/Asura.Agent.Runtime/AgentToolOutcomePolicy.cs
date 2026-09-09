using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal enum AgentToolOutcomeDisposition
{
    Continue,
    Reconcile,
    Quarantine,
}

/// <summary>
/// Classifies a completed tool call at the provider-continuation boundary.
/// Tool adapters describe what happened; this policy decides whether the
/// remaining proposals were based on state that is still safe to use.
/// </summary>
internal static class AgentToolOutcomePolicy
{
    public static AgentToolOutcomeDisposition Classify(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Classify(result.StableCode);
    }

    internal static AgentToolOutcomeDisposition Classify(string stableCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        return stableCode switch
        {
            AgentActionFailureCodes.CompletionAuditUnavailable =>
                AgentToolOutcomeDisposition.Quarantine,
            BrowserAgentToolResultJson.InteractionOutcomeUnknownStableCode =>
                AgentToolOutcomeDisposition.Reconcile,
            FileAgentToolResultJson.FileMutationOutcomeUnknownStableCode =>
                AgentToolOutcomeDisposition.Reconcile,
            DockerAgentControlToolResultJson.OutcomeUnknownStableCode =>
                AgentToolOutcomeDisposition.Reconcile,
            GitAgentToolResultJson.MutationOutcomeUnknownStableCode =>
                AgentToolOutcomeDisposition.Reconcile,
            McpAgentToolResultJson.ManifestChangedStableCode =>
                AgentToolOutcomeDisposition.Quarantine,
            McpAgentToolResultJson.OutcomeUnknownStableCode =>
                AgentToolOutcomeDisposition.Reconcile,
            WorkspaceLayoutAgentToolResultJson.OutcomeUnknownStableCode =>
                AgentToolOutcomeDisposition.Reconcile,
            _ => AgentToolOutcomeDisposition.Continue,
        };
    }
}
