using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record PanelAgentIntent
{
    private PanelAgentIntent()
    {
    }

    public sealed record Inspect : PanelAgentIntent;

    public sealed record Focus : PanelAgentIntent;
}

internal abstract record PanelAgentIntentResult
{
    private PanelAgentIntentResult()
    {
    }

    public sealed record Parsed(
        PanelAgentIntent Intent,
        PanelInstanceId PanelId)
        : PanelAgentIntentResult;

    public sealed record Rejected(
        string StableCode,
        string Message)
        : PanelAgentIntentResult;
}
