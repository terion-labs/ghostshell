namespace Asura.Application;

/// <summary>
/// Stable failure codes shared by the trusted execution bridge and the
/// governed runtime. These codes describe control-plane failures, not terminal
/// process output.
/// </summary>
public static class AgentActionFailureCodes
{
    public const string CompletionAuditUnavailable =
        "agent_completion_audit_unavailable";
}
