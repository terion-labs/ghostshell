using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record DatabaseAgentIntentResult
{
    private DatabaseAgentIntentResult()
    {
    }

    public sealed record Parsed(
        PanelInstanceId PanelId,
        AgentDatabaseReadRequest Request) : DatabaseAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : DatabaseAgentIntentResult;
}
