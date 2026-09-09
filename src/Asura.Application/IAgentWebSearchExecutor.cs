namespace Asura.Application;

/// <summary>
/// Runs one anonymous browser-backed search. The implementation owns browser
/// isolation, navigation policy, timeout, and fixed result extraction.
/// </summary>
public interface IAgentWebSearchExecutor
{
    ValueTask<AgentWebSearchExecutionResult> SearchAsync(
        AgentWebSearchRequest request,
        CancellationToken cancellationToken);
}
