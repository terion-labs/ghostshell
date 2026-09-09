using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record DockerAgentControlIntent
{
    private DockerAgentControlIntent()
    {
    }

    public sealed record Parsed(
        PanelInstanceId PanelId,
        AgentDockerControlRequest Request) : DockerAgentControlIntent;

    public sealed record Rejected(string StableCode) : DockerAgentControlIntent;
}
