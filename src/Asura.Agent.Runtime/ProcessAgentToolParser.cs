using System.Text.Json;
using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime;

internal static class ProcessAgentToolParser
{
    public static ProcessAgentIntentResult Parse(
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

        if (properties.ContainsKey("panel_id"))
        {
            return Invalid(
                "An exact process tool does not accept a panel ID.");
        }

        return ProcessAgentToolSet.Supports(panel)
            ? ParseOptions(properties, panel.PanelId)
            : UnavailableTool();
    }

    public static ProcessAgentIntentResult Parse(
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

        var eligible = ProcessAgentToolSet.ActiveProcessPanels(panels);
        if (eligible.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || !TryGetString(panelIdElement, out var panelId))
        {
            return Invalid(
                "A broad process tool requires one exact panel_id.");
        }

        var selected = eligible.FirstOrDefault(panel =>
            string.Equals(
                panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null)
        {
            return Invalid(
                "The selected panel_id is not available for this process tool.");
        }

        properties.Remove("panel_id");
        return ParseOptions(properties, selected.PanelId);
    }

    private static ProcessAgentIntentResult ParseOptions(
        IReadOnlyDictionary<string, JsonElement> properties,
        Asura.Core.PanelInstanceId panelId)
    {
        if (properties.Keys.Any(name => name is not (
                "sort" or "limit" or "offset" or "name_contains" or "pid")))
        {
            return Invalid(
                "Process tools accept only sort, limit, offset, name_contains, and pid options.");
        }

        var sort = ProcessMonitorSort.CpuDescending;
        if (properties.TryGetValue("sort", out var sortElement)
            && !TryParseSort(sortElement, out sort))
        {
            return Invalid(
                "Process sort must be cpu_desc, memory_desc, name_asc, or pid_asc.");
        }

        var limit = ProcessAgentToolSet.DefaultLimit;
        if (properties.TryGetValue("limit", out var limitElement)
            && (limitElement.ValueKind != JsonValueKind.Number
                || !limitElement.TryGetInt32(out limit)
                || !ProcessAgentToolSet.IsAllowedLimit(limit)))
        {
            return Invalid(
                "Process limit must be 16, 32, or 64.");
        }

        var offset = 0;
        if (properties.TryGetValue("offset", out var offsetElement)
            && (offsetElement.ValueKind != JsonValueKind.Number
                || !offsetElement.TryGetInt32(out offset)
                || offset is < 0 or > 1_000_000))
        {
            return Invalid("Process offset must be between 0 and 1000000.");
        }

        string? nameContains = null;
        if (properties.TryGetValue("name_contains", out var nameElement)
            && (!TryGetString(nameElement, out nameContains)
                || string.IsNullOrWhiteSpace(nameContains)
                || nameContains.Length > 128
                || nameContains.Any(char.IsControl)))
        {
            return Invalid("Process name_contains must be bounded printable text.");
        }

        int? processId = null;
        if (properties.TryGetValue("pid", out var pidElement))
        {
            if (pidElement.ValueKind != JsonValueKind.Number
                || !pidElement.TryGetInt32(out var parsedPid)
                || parsedPid <= 0)
            {
                return Invalid("Process pid must be a positive integer.");
            }

            processId = parsedPid;
        }

        return new ProcessAgentIntentResult.Parsed(
            new ProcessAgentIntent(limit, sort, offset, nameContains, processId),
            panelId);
    }

    private static bool TryParseSort(
        JsonElement element,
        out ProcessMonitorSort sort)
    {
        sort = default;
        if (element.ValueKind != JsonValueKind.String
            || !TryGetString(element, out var value))
        {
            return false;
        }

        switch (value)
        {
            case "cpu_desc":
                sort = ProcessMonitorSort.CpuDescending;
                return true;
            case "memory_desc":
                sort = ProcessMonitorSort.MemoryDescending;
                return true;
            case "name_asc":
                sort = ProcessMonitorSort.NameAscending;
                return true;
            case "pid_asc":
                sort = ProcessMonitorSort.ProcessIdAscending;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out ProcessAgentIntentResult rejection)
    {
        properties = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            rejection = Invalid(
                "Process tool arguments must be one object.");
            return false;
        }

        foreach (var property in proposal.Arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                rejection = Invalid(
                    "Process tool arguments cannot contain duplicate fields.");
                return false;
            }
        }

        rejection = null!;
        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        out string value)
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
            BuiltInAgentTools.ProcessesList,
            StringComparison.Ordinal);

    private static ProcessAgentIntentResult UnknownTool() =>
        new ProcessAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a process tool unavailable to this run.");

    private static ProcessAgentIntentResult UnavailableTool() =>
        new ProcessAgentIntentResult.Rejected(
            "tool_not_available",
            "No eligible Process Monitor panel is available in the fresh scope.");

    private static ProcessAgentIntentResult Invalid(string message) =>
        new ProcessAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}
