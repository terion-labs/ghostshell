using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class StatisticsAgentToolParser
{
    public static StatisticsAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        if (properties.Count != 0)
        {
            return Invalid(
                "An exact statistics tool does not accept arguments.");
        }

        return StatisticsAgentToolSet.Supports(panel)
            ? new StatisticsAgentIntentResult.Parsed(panel.PanelId)
            : UnavailableTool();
    }

    public static StatisticsAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        var eligible = StatisticsAgentToolSet.ActiveStatisticsPanels(panels);
        if (eligible.Length == 0)
        {
            return UnavailableTool();
        }

        if (properties.Count != 1
            || !properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || !TryGetString(panelIdElement, out var panelId))
        {
            return Invalid(
                "A broad statistics tool requires only one exact panel_id.");
        }

        var selected = eligible.FirstOrDefault(panel =>
            string.Equals(panel.PanelId.Value, panelId, StringComparison.Ordinal));
        return selected is null
            ? Invalid(
                "The selected panel_id is not available for this statistics tool.")
            : new StatisticsAgentIntentResult.Parsed(selected.PanelId);
    }

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out StatisticsAgentIntentResult rejection)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            rejection = Invalid(
                "Statistics tool arguments must be one object.");
            return false;
        }

        foreach (var property in proposal.Arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                rejection = Invalid(
                    "Statistics tool arguments cannot contain duplicate fields.");
                return false;
            }
        }

        rejection = null!;
        return true;
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        value = string.Empty;
        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsKnownTool(string toolName) =>
        string.Equals(
            toolName,
            BuiltInAgentTools.StatisticsRead,
            StringComparison.Ordinal);

    private static StatisticsAgentIntentResult UnknownTool() =>
        new StatisticsAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a statistics tool unavailable to this run.");

    private static StatisticsAgentIntentResult UnavailableTool() =>
        new StatisticsAgentIntentResult.Rejected(
            "tool_not_available",
            "No eligible Statistics panel is available in the fresh scope.");

    private static StatisticsAgentIntentResult Invalid(string message) =>
        new StatisticsAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}
