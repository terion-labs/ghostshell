using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Governed local Process Monitor execution. The host re-resolves the exact
/// graph panel before consuming one-action authorization.
/// </summary>
public interface IAgentProcessSessionHost
{
    ValueTask<HostResult<AgentProcessListResult>> RunAgentProcessListAsync(
        AgentAuthorizationId authorizationId,
        AgentProcessListAction action,
        CancellationToken cancellationToken);
}
