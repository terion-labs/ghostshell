using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Governed read-only execution against one exact hosted Database Viewer.
/// The host re-resolves graph/session identity before consuming authorization.
/// </summary>
public interface IAgentDatabaseSessionHost
{
    ValueTask<HostResult<AgentDatabaseReadResult>> RunAgentDatabaseReadAsync(
        AgentAuthorizationId authorizationId,
        AgentDatabaseReadAction action,
        CancellationToken cancellationToken);
}
