using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class WebAgentToolParser
{
    public static WebAgentIntentResult Parse(AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (string.Equals(
                proposal.ToolName,
                BuiltInAgentTools.WebSearch,
                StringComparison.Ordinal))
        {
            return WebSearchAgentToolParser.Parse(proposal) switch
            {
                WebSearchAgentIntentResult.Parsed parsed =>
                    new WebAgentIntentResult.Parsed(new AgentWebSearchRequest(
                        parsed.Intent.Query,
                        parsed.Intent.ResultCount)),
                WebSearchAgentIntentResult.Rejected rejected =>
                    new WebAgentIntentResult.Rejected(rejected.StableCode, rejected.Message),
                _ => Invalid(),
            };
        }

        if (proposal.ToolName is not (BuiltInAgentTools.HttpFetch or BuiltInAgentTools.WebRead))
        {
            return new WebAgentIntentResult.Rejected(
                "unknown_tool",
                "The web tool is unavailable.");
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
            || !properties.TryGetValue("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            return Invalid();
        }

        try
        {
            var url = urlElement.GetString() ?? string.Empty;
            return proposal.ToolName switch
            {
                BuiltInAgentTools.HttpFetch => ParseFetch(properties, url),
                BuiltInAgentTools.WebRead => ParseRead(properties, url),
                _ => Invalid(),
            };
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
    }

    private static WebAgentIntentResult ParseFetch(
        IReadOnlyDictionary<string, JsonElement> properties,
        string url)
    {
        if (properties.Keys.Any(name => name is not ("url" or "method")))
        {
            return Invalid();
        }

        var method = AgentHttpFetchMethod.Get;
        if (properties.TryGetValue("method", out var element))
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return Invalid();
            }

            method = element.GetString() switch
            {
                "GET" => AgentHttpFetchMethod.Get,
                "HEAD" => AgentHttpFetchMethod.Head,
                _ => throw new ArgumentException("Unsupported HTTP method."),
            };
        }

        return new WebAgentIntentResult.Parsed(new AgentHttpFetchRequest(url, method));
    }

    private static WebAgentIntentResult ParseRead(
        IReadOnlyDictionary<string, JsonElement> properties,
        string url)
    {
        if (properties.Keys.Any(name => name is not ("url" or "format")))
        {
            return Invalid();
        }

        var format = AgentWebReadFormat.Markdown;
        if (properties.TryGetValue("format", out var element))
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return Invalid();
            }

            format = element.GetString() switch
            {
                "markdown" => AgentWebReadFormat.Markdown,
                "rendered_html" => AgentWebReadFormat.RenderedHtml,
                _ => throw new ArgumentException("Unsupported web read format."),
            };
        }

        return new WebAgentIntentResult.Parsed(new AgentWebReadRequest(url, format));
    }

    private static WebAgentIntentResult Invalid() =>
        new WebAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            "The web tool arguments are invalid or outside their bounds.");
}

internal abstract record WebAgentIntentResult
{
    private WebAgentIntentResult()
    {
    }

    public sealed record Parsed(AgentWebToolRequest Request) : WebAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message) : WebAgentIntentResult;
}
