namespace Asura.Application;

/// <summary>
/// The narrow broker-backed capability used by the MCP execution bridge to
/// prove that one exact registered agent run may launch MCP servers.
/// </summary>
public interface IAgentMcpRunAuthorityVerifier
{
    ValueTask<AgentMcpRunAuthorityResult> AcquireAsync(
        AgentMcpRunAuthorityRequest request,
        CancellationToken cancellationToken);
}
