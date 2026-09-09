using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Stores the local profile's default governed-agent policy. Workspace and
/// saved-screen definitions remain separate, portable override layers.
/// </summary>
public interface IAgentPolicyPreferenceStore
{
    ValueTask<ApplicationRunResult<AgentPolicy?>> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        AgentPolicy policy,
        CancellationToken cancellationToken);
}
