namespace Asura.Agent.Runtime;

internal sealed record WebSearchAgentIntent(string Query, int ResultCount);

internal abstract record WebSearchAgentIntentResult
{
    private WebSearchAgentIntentResult()
    {
    }

    public sealed record Parsed(WebSearchAgentIntent Intent)
        : WebSearchAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : WebSearchAgentIntentResult;
}
