using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Browser;

public sealed class AgentWebToolExecutor(
    IWorkspaceNetworkRouteResolver routeResolver) : IAgentWebToolExecutor
{
    public async ValueTask<AgentWebToolExecutionResult> ExecuteAsync(
        WorkspaceInstanceId workspaceId,
        AgentWebToolRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connector = routeResolver.ConnectorFor(workspaceId);
        if (connector is null)
        {
            return new AgentWebToolExecutionResult.Failed(
                AgentWebToolErrorCode.Unavailable);
        }

        return request switch
        {
            AgentHttpFetchRequest fetch =>
                await new PeerBoundHttpFetcher(connector)
                    .FetchAsync(fetch, cancellationToken)
                    .ConfigureAwait(false),
            AgentWebReadRequest read =>
                await new CefAgentWebReader(connector)
                    .ReadAsync(read, cancellationToken)
                    .ConfigureAwait(false),
            AgentWebSearchRequest search =>
                Map(await new CefAgentWebSearchExecutor(connector)
                    .SearchAsync(search, cancellationToken)
                    .ConfigureAwait(false)),
            _ => new AgentWebToolExecutionResult.Failed(
                AgentWebToolErrorCode.Unavailable),
        };
    }

    private static AgentWebToolExecutionResult Map(
        AgentWebSearchExecutionResult result) => result switch
        {
            AgentWebSearchExecutionResult.Succeeded succeeded =>
                new AgentWebToolExecutionResult.Succeeded(succeeded.Result),
            AgentWebSearchExecutionResult.Failed failed =>
                new AgentWebToolExecutionResult.Failed(Map(failed.Code)),
            _ => new AgentWebToolExecutionResult.Failed(AgentWebToolErrorCode.Unavailable),
        };

    private static AgentWebToolErrorCode Map(AgentWebSearchErrorCode code) => code switch
    {
        AgentWebSearchErrorCode.NavigationDenied => AgentWebToolErrorCode.DestinationDenied,
        AgentWebSearchErrorCode.LoadFailed => AgentWebToolErrorCode.LoadFailed,
        AgentWebSearchErrorCode.Interstitial => AgentWebToolErrorCode.SearchInterstitial,
        AgentWebSearchErrorCode.ExtractionFailed => AgentWebToolErrorCode.ExtractionFailed,
        AgentWebSearchErrorCode.TimedOut => AgentWebToolErrorCode.TimedOut,
        AgentWebSearchErrorCode.Cancelled => AgentWebToolErrorCode.Cancelled,
        _ => AgentWebToolErrorCode.Unavailable,
    };
}
