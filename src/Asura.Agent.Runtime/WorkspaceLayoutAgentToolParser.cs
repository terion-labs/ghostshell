using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class WorkspaceLayoutAgentToolParser
{
    public static WorkspaceLayoutAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextSnapshot context,
        IReadOnlySet<PanelKind> supportedPanelKinds)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(supportedPanelKinds);
        if (!IsKnownTool(proposal.ToolName))
        {
            return Rejected("unknown_tool", "The workspace layout tool is unknown.");
        }

        if (context.Target is not AgentTarget.Workspace)
        {
            return Rejected(
                "tool_not_available",
                "Workspace layout tools require the run's complete workspace scope.");
        }

        if (proposal.Arguments.ValueKind != JsonValueKind.Object
            || !TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Layout arguments must be one object with unique fields.");
        }

        return proposal.ToolName switch
        {
            BuiltInAgentTools.ConnectionsList => properties.Count == 0
                ? Parsed(new WorkspaceLayoutAgentIntent.ConnectionList())
                : Invalid("connections.list accepts no arguments."),
            BuiltInAgentTools.TabCreate => ParseTabCreate(
                properties,
                supportedPanelKinds),
            BuiltInAgentTools.TabClose => ParseTabClose(properties, context),
            BuiltInAgentTools.PanelAdd => ParsePanelAdd(
                properties,
                context,
                supportedPanelKinds),
            BuiltInAgentTools.PanelSplit => ParsePanelSplit(
                properties,
                context,
                supportedPanelKinds),
            BuiltInAgentTools.PanelClose => ParsePanelClose(properties, context),
            BuiltInAgentTools.PanelConnect => ParsePanelConnect(properties, context),
            _ => throw new InvalidOperationException(
                "A known workspace layout tool was not mapped."),
        };
    }

    public static bool IsKnownTool(string toolName) => toolName is
        BuiltInAgentTools.ConnectionsList
        or BuiltInAgentTools.TabCreate
        or BuiltInAgentTools.TabClose
        or BuiltInAgentTools.PanelAdd
        or BuiltInAgentTools.PanelSplit
        or BuiltInAgentTools.PanelClose
        or BuiltInAgentTools.PanelConnect;

    private static WorkspaceLayoutAgentIntentResult ParsePanelConnect(
        IReadOnlyDictionary<string, JsonElement> properties,
        AgentContextSnapshot context)
    {
        if (properties.Count != 2
            || !TryString(properties, "panel_id", out var panelId)
            || context.Panels.All(panel => !string.Equals(panel.PanelId.Value, panelId, StringComparison.Ordinal))
            || !TryString(properties, "connection_ref", out var connectionRef)
            || connectionRef.Length > 128)
        {
            return Invalid(
                "panel.connect requires one in-scope panel_id and one bounded connection_ref.");
        }

        return Parsed(new WorkspaceLayoutAgentIntent.PanelConnect(
            new PanelInstanceId(panelId),
            connectionRef));
    }

    private static WorkspaceLayoutAgentIntentResult ParseTabCreate(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlySet<PanelKind> supportedKinds)
    {
        if (!TryCreation(
                properties,
                supportedKinds,
                fixedPropertyCount: 1,
                out var kind,
                out var connectionRef))
        {
            return Invalid(
                "tab.create requires one supported kind; terminal also requires connection_ref.");
        }

        return Parsed(new WorkspaceLayoutAgentIntent.TabCreate(
            kind,
            connectionRef));
    }

    private static WorkspaceLayoutAgentIntentResult ParseTabClose(
        IReadOnlyDictionary<string, JsonElement> properties,
        AgentContextSnapshot context)
    {
        if (properties.Count != 1
            || !TryString(properties, "tab_id", out var value)
            || context.Panels.All(panel => !string.Equals(panel.TabId.Value, value, StringComparison.Ordinal)))
        {
            return Invalid("tab.close requires one in-scope tab_id.");
        }

        return Parsed(new WorkspaceLayoutAgentIntent.TabClose(
            new TabInstanceId(value)));
    }

    private static WorkspaceLayoutAgentIntentResult ParsePanelAdd(
        IReadOnlyDictionary<string, JsonElement> properties,
        AgentContextSnapshot context,
        IReadOnlySet<PanelKind> supportedKinds)
    {
        if (!TryString(properties, "tab_id", out var tabId)
            || context.Panels.All(panel => !string.Equals(panel.TabId.Value, tabId, StringComparison.Ordinal))
            || !TryCreation(
                properties,
                supportedKinds,
                fixedPropertyCount: 2,
                out var kind,
                out var connectionRef))
        {
            return Invalid(
                "panel.add requires one in-scope tab_id and one supported kind.");
        }

        return Parsed(new WorkspaceLayoutAgentIntent.PanelAdd(
            new TabInstanceId(tabId),
            kind,
            connectionRef));
    }

    private static WorkspaceLayoutAgentIntentResult ParsePanelSplit(
        IReadOnlyDictionary<string, JsonElement> properties,
        AgentContextSnapshot context,
        IReadOnlySet<PanelKind> supportedKinds)
    {
        if (!TryString(properties, "panel_id", out var panelId)
            || context.Panels.All(panel => !string.Equals(panel.PanelId.Value, panelId, StringComparison.Ordinal))
            || !TryString(properties, "orientation", out var orientationValue)
            || !TryOrientation(orientationValue, out var orientation)
            || !TryCreation(
                properties,
                supportedKinds,
                fixedPropertyCount: 3,
                out var kind,
                out var connectionRef))
        {
            return Invalid(
                "panel.split requires one in-scope panel_id, orientation, and supported kind.");
        }

        return Parsed(new WorkspaceLayoutAgentIntent.PanelSplit(
            new PanelInstanceId(panelId),
            orientation,
            kind,
            connectionRef));
    }

    private static WorkspaceLayoutAgentIntentResult ParsePanelClose(
        IReadOnlyDictionary<string, JsonElement> properties,
        AgentContextSnapshot context)
    {
        if (properties.Count != 1
            || !TryString(properties, "panel_id", out var value)
            || context.Panels.All(panel => !string.Equals(panel.PanelId.Value, value, StringComparison.Ordinal)))
        {
            return Invalid("panel.close requires one in-scope panel_id.");
        }

        return Parsed(new WorkspaceLayoutAgentIntent.PanelClose(
            new PanelInstanceId(value)));
    }

    private static bool TryKind(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlySet<PanelKind> supportedKinds,
        out PanelKind kind)
    {
        kind = default;
        return TryString(properties, "kind", out var value)
            && TryPanelKind(value, out kind)
            && supportedKinds.Contains(kind)
            && AgentWorkspaceLayoutRequest.IsCreatableKind(kind);
    }

    private static bool TryCreation(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlySet<PanelKind> supportedKinds,
        int fixedPropertyCount,
        out PanelKind kind,
        out string? connectionRef)
    {
        connectionRef = null;
        if (!TryKind(properties, supportedKinds, out kind))
        {
            return false;
        }

        if (properties.TryGetValue("connection_ref", out _))
        {
            if (!TryString(properties, "connection_ref", out var parsed)
                || parsed.Length > 128)
            {
                return false;
            }

            connectionRef = parsed;
        }

        return properties.Count == fixedPropertyCount
                + (connectionRef is null ? 0 : 1)
            && (kind != PanelKind.Terminal || connectionRef is not null);
    }

    private static bool TryPanelKind(string value, out PanelKind kind)
    {
        switch (value)
        {
            case "terminal": kind = PanelKind.Terminal; return true;
            case "browser": kind = PanelKind.Browser; return true;
            case "file_viewer": kind = PanelKind.FileViewer; return true;
            case "statistics": kind = PanelKind.Statistics; return true;
            case "process_monitor": kind = PanelKind.ProcessMonitor; return true;
            case "placeholder": kind = PanelKind.Placeholder; return true;
            case "database_viewer": kind = PanelKind.DatabaseViewer; return true;
            case "docker": kind = PanelKind.Docker; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryOrientation(
        string value,
        out AgentPanelSplitOrientation orientation)
    {
        switch (value)
        {
            case "left_right":
                orientation = AgentPanelSplitOrientation.LeftRight;
                return true;
            case "top_bottom":
                orientation = AgentPanelSplitOrientation.TopBottom;
                return true;
            default:
                orientation = default;
                return false;
        }
    }

    private static bool TryString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!properties.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: > 0 } parsed)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadUniqueProperties(
        JsonElement value,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static WorkspaceLayoutAgentIntentResult Parsed(
        WorkspaceLayoutAgentIntent intent) =>
        new WorkspaceLayoutAgentIntentResult.Parsed(intent);

    private static WorkspaceLayoutAgentIntentResult Invalid(string message) =>
        Rejected("invalid_tool_arguments", message);

    private static WorkspaceLayoutAgentIntentResult Rejected(
        string stableCode,
        string message) =>
        new WorkspaceLayoutAgentIntentResult.Rejected(stableCode, message);
}
