using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record DockerAgentIntentResult
{
    private DockerAgentIntentResult()
    {
    }

    public sealed record Parsed(
        PanelInstanceId PanelId,
        AgentDockerReadRequest Request) : DockerAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : DockerAgentIntentResult;
}
