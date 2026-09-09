using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class WorkspaceGraphAgentToolParser
{
    public static WorkspaceGraphAgentIntentResult Parse(
        AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (proposal.Arguments.ValueKind != JsonValueKind.Object
            || !TryReadUniqueProperties(
                proposal.Arguments,
                out var properties))
        {
            return Invalid(
                "Workspace graph arguments must be one object with unique fields.");
        }

        return proposal.ToolName switch
        {
            BuiltInAgentTools.WorkspaceInspect when properties.Count == 0 =>
                Parsed(new WorkspaceGraphAgentIntent.WorkspaceInspect()),
            BuiltInAgentTools.TabList =>
                ParsePage(
                    properties,
                    static offset => new WorkspaceGraphAgentIntent.TabList(offset)),
            BuiltInAgentTools.PanelList =>
                ParsePage(
                    properties,
                    static offset => new WorkspaceGraphAgentIntent.PanelList(offset)),
            _ => Invalid(
                "This workspace graph tool accepts no arguments."),
        };
    }

    private static WorkspaceGraphAgentIntentResult ParsePage(
        IReadOnlyDictionary<string, JsonElement> properties,
        Func<int, WorkspaceGraphAgentIntent> create)
    {
        if (properties.Count == 0)
        {
            return Parsed(create(0));
        }

        if (properties.Count != 1
            || !properties.TryGetValue("offset", out var offsetElement)
            || offsetElement.ValueKind != JsonValueKind.Number
            || !offsetElement.TryGetInt32(out var offset)
            || offset < 0
            || offset > AgentWorkspaceGraphRequest.MaximumOffset
            || offset % AgentWorkspaceGraphRequest.PageSize != 0)
        {
            return Invalid(
                "A graph page offset must be one of 0, 16, 32, or 48.");
        }

        return Parsed(create(offset));
    }

    private static bool TryReadUniqueProperties(
        JsonElement value,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownTool(string toolName) =>
        toolName is
            BuiltInAgentTools.WorkspaceInspect
            or BuiltInAgentTools.TabList
            or BuiltInAgentTools.PanelList;

    private static WorkspaceGraphAgentIntentResult Parsed(
        WorkspaceGraphAgentIntent intent) =>
        new WorkspaceGraphAgentIntentResult.Parsed(intent);

    private static WorkspaceGraphAgentIntentResult UnknownTool() =>
        new WorkspaceGraphAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a workspace graph tool unavailable to this run.");

    private static WorkspaceGraphAgentIntentResult Invalid(string message) =>
        new WorkspaceGraphAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}
