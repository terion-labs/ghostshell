using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class PanelAgentToolParser
{
    public static PanelAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
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
                "Panel tool arguments must be one object with unique fields.");
        }

        var eligible = context.Panels
            .Where(PanelAgentToolSet.IsEligible)
            .ToArray();
        if (eligible.Length == 0)
        {
            return UnavailableTool();
        }

        AgentContextPanel selected;
        if (context.Target is
                AgentTarget.Panel or AgentTarget.ConnectionSession)
        {
            if (context.Panels.Count != 1
                || eligible.Length != 1
                || properties.Count != 0)
            {
                return Invalid(
                    "An exact panel tool accepts no arguments.");
            }

            selected = eligible[0];
        }
        else
        {
            if (properties.Count != 1
                || !properties.TryGetValue(
                    "panel_id",
                    out var panelIdElement)
                || panelIdElement.ValueKind != JsonValueKind.String
                || panelIdElement.GetString() is not { } panelId)
            {
                return Invalid(
                    "A broad panel tool requires one exact panel_id.");
            }

            selected = eligible.FirstOrDefault(panel =>
                string.Equals(
                    panel.PanelId.Value,
                    panelId,
                    StringComparison.Ordinal))!;
            if (selected is null)
            {
                return Invalid(
                    "The selected panel_id is outside the resolved run scope.");
            }
        }

        return new PanelAgentIntentResult.Parsed(
            proposal.ToolName switch
            {
                BuiltInAgentTools.PanelInspect =>
                    new PanelAgentIntent.Inspect(),
                BuiltInAgentTools.PanelFocus =>
                    new PanelAgentIntent.Focus(),
                _ => throw new InvalidOperationException(
                    "A known panel tool was not mapped."),
            },
            selected.PanelId);
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
            BuiltInAgentTools.PanelInspect
            or BuiltInAgentTools.PanelFocus;

    private static PanelAgentIntentResult UnknownTool() =>
        new PanelAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a panel tool unavailable to this run.");

    private static PanelAgentIntentResult UnavailableTool() =>
        new PanelAgentIntentResult.Rejected(
            "tool_not_available",
            "No current live graph panel is available to this tool.");

    private static PanelAgentIntentResult Invalid(string message) =>
        new PanelAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}
