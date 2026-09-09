namespace Asura.Application;

/// <summary>
/// Read-only projection of durable, secret-free evidence for one exact
/// runtime-owned agent run. This port conveys no execution authority.
/// </summary>
public interface IAgentRunAuditReader
{
    ValueTask<AuditStoreResult<AgentRunAuditPage>> ReadAsync(
        AgentRunAuditQuery query,
        CancellationToken cancellationToken);
}
