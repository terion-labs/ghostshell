using Asura.Agent.Providers;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Asura.Desktop;

internal sealed class DesktopAgentWorkspaceRuntimeFactory(
    IServiceProvider services) : IAgentWorkspaceRuntimeFactory
{
    public IGovernedAgentRuntime Create(
        WorkspaceInstanceId workspaceId,
        AgentConversationScopeId conversationScopeId,
        AgentPolicy policy) =>
        Create(workspaceId, conversationScopeId, policy, networkProxy: null);

    public IGovernedAgentRuntime Create(
        WorkspaceInstanceId workspaceId,
        AgentConversationScopeId conversationScopeId,
        AgentPolicy policy,
        Uri? networkProxy)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException(
                "A live workspace identity is required.",
                nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "A complete agent policy is required to create a workspace runtime.",
                nameof(policy));
        }

        var explicitArguments = networkProxy is null
            ? new object[] { workspaceId, conversationScopeId, policy }
            :
            [
                workspaceId,
                conversationScopeId,
                policy,
                new CatalogAgentProviderResolver(
                    services.GetRequiredService<CatalogAiProviderRuntime>(),
                    networkProxy),
            ];
        var runtime = ActivatorUtilities.CreateInstance<GovernedAgentRuntime>(
            services,
            explicitArguments);
        services.GetRequiredService<Asura.Mcp.Server.WorkspaceMcpServer>().Register(runtime);
        return runtime;
    }
}
