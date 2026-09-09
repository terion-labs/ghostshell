using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record StatisticsAgentIntentResult
{
    private StatisticsAgentIntentResult()
    {
    }

    public sealed record Parsed(PanelInstanceId PanelId)
        : StatisticsAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : StatisticsAgentIntentResult;
}
