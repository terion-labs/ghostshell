using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed class AgentToolOutcomePolicyTests
{
    public static TheoryData<string, string> Outcomes =>
        new()
        {
            { "terminal_read_failed", nameof(AgentToolOutcomeDisposition.Continue) },
            {
                BrowserAgentToolResultJson.InteractionOutcomeUnknownStableCode,
                nameof(AgentToolOutcomeDisposition.Reconcile)
            },
            {
                FileAgentToolResultJson.FileMutationOutcomeUnknownStableCode,
                nameof(AgentToolOutcomeDisposition.Reconcile)
            },
            {
                GitAgentToolResultJson.MutationOutcomeUnknownStableCode,
                nameof(AgentToolOutcomeDisposition.Reconcile)
            },
            {
                McpAgentToolResultJson.OutcomeUnknownStableCode,
                nameof(AgentToolOutcomeDisposition.Reconcile)
            },
            {
                WorkspaceLayoutAgentToolResultJson.OutcomeUnknownStableCode,
                nameof(AgentToolOutcomeDisposition.Reconcile)
            },
            {
                AgentActionFailureCodes.CompletionAuditUnavailable,
                nameof(AgentToolOutcomeDisposition.Quarantine)
            },
            {
                McpAgentToolResultJson.ManifestChangedStableCode,
                nameof(AgentToolOutcomeDisposition.Quarantine)
            },
        };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void OutcomeClassificationIsExplicitAndStable(
        string stableCode,
        string expected)
    {
        Assert.Equal(expected, AgentToolOutcomePolicy.Classify(stableCode).ToString());
    }
}
