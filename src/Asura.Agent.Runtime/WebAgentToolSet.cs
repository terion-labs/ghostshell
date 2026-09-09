using System.Collections.Immutable;
using System.Text;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class WebAgentToolSet
{
    private const string HttpFetchSchema = """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "minLength": 1, "maxLength": 2048 },
            "method": { "type": "string", "enum": ["GET", "HEAD"] }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """;
    private const string WebReadSchema = """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "minLength": 1, "maxLength": 2048 },
            "format": { "type": "string", "enum": ["markdown", "rendered_html"] }
          },
          "required": ["url"],
          "additionalProperties": false
        }
        """;

    public static ImmutableArray<AgentToolDefinition> Tools { get; } =
    [
        new AgentToolDefinition(
            BuiltInAgentTools.HttpFetch,
            "Fetch a bounded textual HTTP API or machine-readable endpoint with GET or HEAD. Redirects and public-network destinations are checked on every leg. Returns untrusted web content.",
            Encoding.UTF8.GetBytes(HttpFetchSchema)),
        new AgentToolDefinition(
            BuiltInAgentTools.WebRead,
            "Read a rendered web page in an isolated offscreen browser. Defaults to Readability article Markdown; rendered_html is explicit, bounded, and untrusted.",
            Encoding.UTF8.GetBytes(WebReadSchema)),
        .. WebSearchAgentToolSet.Tools,
    ];

    public static bool Owns(string toolName) => toolName is
        BuiltInAgentTools.HttpFetch
        or BuiltInAgentTools.WebRead
        or BuiltInAgentTools.WebSearch;
}
