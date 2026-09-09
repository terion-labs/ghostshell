using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class WebSearchAgentToolParser
{
    public static WebSearchAgentIntentResult Parse(AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(
                proposal.ToolName,
                BuiltInAgentTools.WebSearch,
                StringComparison.Ordinal))
        {
            return Rejected("unknown_tool", "The web search tool is unavailable.");
        }

        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid();
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in proposal.Arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return Invalid();
            }
        }

        if (properties.Count is < 1 or > 2
            || !properties.TryGetValue("query", out var queryElement)
            || queryElement.ValueKind != JsonValueKind.String
            || properties.Keys.Any(name => name is not ("query" or "result_count")))
        {
            return Invalid();
        }

        var resultCount = AgentWebSearchRequest.DefaultResultCount;
        if (properties.TryGetValue("result_count", out var countElement)
            && (countElement.ValueKind != JsonValueKind.Number
                || !countElement.TryGetInt32(out resultCount)))
        {
            return Invalid();
        }

        try
        {
            var request = new AgentWebSearchRequest(
                queryElement.GetString() ?? string.Empty,
                resultCount);
            return new WebSearchAgentIntentResult.Parsed(
                new WebSearchAgentIntent(request.Query, request.ResultCount));
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static WebSearchAgentIntentResult Rejected(
        string stableCode,
        string message) =>
        new WebSearchAgentIntentResult.Rejected(stableCode, message);

    private static WebSearchAgentIntentResult Invalid() =>
        Rejected(
            "invalid_tool_arguments",
            "A web search requires one bounded query and an optional result_count from 1 to 10.");
}
