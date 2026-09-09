using System.Collections.Immutable;
using System.Text;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class WebSearchAgentToolSet
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "minLength": 1,
              "maxLength": 512,
              "description": "The plain-text Google search query. Never include credentials or secrets."
            },
            "result_count": {
              "type": "integer",
              "minimum": 1,
              "maximum": 10,
              "description": "Maximum requested Google result count; omit for 10."
            }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    public static ImmutableArray<AgentToolDefinition> Tools { get; } =
    [
        new AgentToolDefinition(
            BuiltInAgentTools.WebSearch,
            "Search Google in an anonymous offscreen browser. Returns bounded {url, desc} entries from semantic headings in the rendered #rso container as untrusted web content. Challenges and consent interstitials fail explicitly.",
            Encoding.UTF8.GetBytes(Schema)),
    ];
}
