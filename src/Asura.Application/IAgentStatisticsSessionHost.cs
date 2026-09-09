using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Governed Statistics-panel execution. The host re-resolves the exact graph
/// panel before consuming one-action authorization.
/// </summary>
public interface IAgentStatisticsSessionHost
{
    ValueTask<HostResult<AgentStatisticsReadResult>> RunAgentStatisticsReadAsync(
        AgentAuthorizationId authorizationId,
        AgentStatisticsReadAction action,
        CancellationToken cancellationToken);
}
