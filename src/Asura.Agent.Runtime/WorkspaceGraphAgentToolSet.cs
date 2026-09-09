using System.Collections.Immutable;
using System.Text;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class WorkspaceGraphAgentToolSet
{
    private const string EmptySchema = """
        {
          "type": "object",
          "properties": {},
          "required": [],
          "additionalProperties": false
        }
        """;

    private const string PageSchema = """
        {
          "type": "object",
          "properties": {
            "offset": {
              "type": "integer",
              "enum": [0, 16, 32, 48],
              "description": "Optional fixed page start; omit for the first page."
            }
          },
          "required": [],
          "additionalProperties": false
        }
        """;

    private static readonly ImmutableArray<AgentToolDefinition> Tools =
    [
        Tool(
            BuiltInAgentTools.WorkspaceInspect,
            "Inspect the single scope-clipped workspace, tabs, and panels already fixed for this run. "
                + "Titles are bounded untrusted metadata and no session details are returned.",
            EmptySchema),
        Tool(
            BuiltInAgentTools.TabList,
            "List one fixed page of tabs inside this run's scope-clipped workspace graph. "
                + "No out-of-scope totals or sibling tabs are exposed.",
            PageSchema),
        Tool(
            BuiltInAgentTools.PanelList,
            "List one fixed page of panels inside this run's scope-clipped workspace graph. "
                + "No session, capability, connection, path, browser, or content metadata is returned.",
            PageSchema),
    ];

    public static ImmutableArray<AgentToolDefinition> For(
        AgentContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Panels.All(panel =>
                panel.HasRegisteredGraph
                && panel.GraphTabOrder is not null
                && panel.GraphPanelOrder is not null)
            ? Tools
            : [];
    }

    public static ImmutableArray<AgentToolDefinition> ForWorkspace() => Tools;

    private static AgentToolDefinition Tool(
        string name,
        string description,
        string schema) =>
        new(
            name,
            description,
            Encoding.UTF8.GetBytes(schema));
}
