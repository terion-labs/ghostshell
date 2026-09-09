using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class DockerAgentToolParser
{
    public static DockerAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        var capability = DockerAgentToolSet.RequiredCapability(proposal.ToolName);
        if (capability is null)
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        return DockerAgentToolSet.Supports(panel, capability)
            ? ParseRequest(proposal.ToolName, panel.PanelId, properties)
            : UnavailableTool();
    }

    public static DockerAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var capability = DockerAgentToolSet.RequiredCapability(proposal.ToolName);
        if (capability is null)
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        var eligible = DockerAgentToolSet.ActiveDockerPanels(panels)
            .Where(panel => DockerAgentToolSet.Supports(panel, capability))
            .ToArray();
        if (eligible.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.Remove("panel_id", out var panelElement)
            || !TryGetString(panelElement, out var panelId))
        {
            return Invalid("A broad Docker tool requires one exact panel_id.");
        }

        var selected = eligible.FirstOrDefault(panel => string.Equals(
            panel.PanelId.Value,
            panelId,
            StringComparison.Ordinal));
        return selected is null
            ? Invalid("The selected panel_id is unavailable for this Docker tool.")
            : ParseRequest(proposal.ToolName, selected.PanelId, properties);
    }

    private static DockerAgentIntentResult ParseRequest(
        string toolName,
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        try
        {
            AgentDockerReadRequest request = toolName switch
            {
                BuiltInAgentTools.DockerReadState => new AgentDockerReadRequest.ReadState(
                    panelId,
                    ReadOptionalInteger(properties, "maximum_resources_per_kind", 50)),
                BuiltInAgentTools.DockerInspect => new AgentDockerReadRequest.Inspect(
                    panelId,
                    Reference(ReadRequiredString(properties, "resource_ref"))),
                BuiltInAgentTools.DockerLogs => ParseLogs(panelId, properties),
                BuiltInAgentTools.DockerFilesList =>
                    new AgentDockerReadRequest.FilesList(
                        panelId,
                        Reference(ReadRequiredString(properties, "resource_ref")),
                        ReadRequiredString(properties, "path"),
                        ReadOptionalInteger(properties, "maximum_entries", 100)),
                BuiltInAgentTools.DockerFilesStat =>
                    new AgentDockerReadRequest.FilesStat(
                        panelId,
                        Reference(ReadRequiredString(properties, "resource_ref")),
                        ReadRequiredString(properties, "path")),
                BuiltInAgentTools.DockerFileRead =>
                    new AgentDockerReadRequest.FileRead(
                        panelId,
                        Reference(ReadRequiredString(properties, "resource_ref")),
                        ReadRequiredString(properties, "path"),
                        ReadOptionalInteger(properties, "maximum_bytes", 8 * 1_024)),
                _ => throw new InvalidOperationException("Unknown Docker tool."),
            };
            RequireNoProperties(properties);
            return new DockerAgentIntentResult.Parsed(panelId, request);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or OverflowException)
        {
            return Invalid("Docker tool arguments do not match the closed schema.");
        }
    }

    private static AgentDockerReadRequest ParseLogs(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties) =>
        new AgentDockerReadRequest.Logs(
            panelId,
            Reference(ReadRequiredString(properties, "container_ref")),
            ReadOptionalInteger(properties, "limit", 100),
            ReadOptionalString(properties, "before_timestamp"),
            ReadOptionalString(properties, "since_timestamp"),
            ReadOptionalString(properties, "search"),
            ReadOptionalInteger(properties, "context_lines", 0));

    private static DockerResourceReferenceId Reference(string value) => new(value);

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out DockerAgentIntentResult rejection)
    {
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            properties = [];
            rejection = Invalid("Docker tool arguments must be one object.");
            return false;
        }

        try
        {
            properties = ReadObject(proposal.Arguments);
            rejection = null!;
            return true;
        }
        catch (ArgumentException exception)
        {
            properties = [];
            rejection = Invalid(exception.Message);
            return false;
        }
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement element)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new ArgumentException("Docker tool arguments contain a duplicate property.");
            }
        }

        return properties;
    }

    private static string ReadRequiredString(
        Dictionary<string, JsonElement> properties,
        string name)
    {
        if (!properties.Remove(name, out var element)
            || !TryGetString(element, out var value))
        {
            throw new ArgumentException($"Docker tool argument '{name}' is required.");
        }

        return value;
    }

    private static string? ReadOptionalString(
        Dictionary<string, JsonElement> properties,
        string name)
    {
        if (!properties.Remove(name, out var element))
        {
            return null;
        }

        if (!TryGetString(element, out var value))
        {
            throw new ArgumentException($"Docker tool argument '{name}' must be a string.");
        }

        return value;
    }

    private static int ReadOptionalInteger(
        Dictionary<string, JsonElement> properties,
        string name,
        int defaultValue)
    {
        if (!properties.Remove(name, out var element))
        {
            return defaultValue;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value))
        {
            throw new ArgumentException($"Docker tool argument '{name}' must be an integer.");
        }

        return value;
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.String
            && element.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void RequireNoProperties(
        Dictionary<string, JsonElement> properties)
    {
        if (properties.Count != 0)
        {
            throw new ArgumentException("Docker tool arguments contain unknown properties.");
        }
    }

    private static DockerAgentIntentResult UnknownTool() =>
        new DockerAgentIntentResult.Rejected(
            "tool_not_available",
            "The requested Docker tool is not available.");

    private static DockerAgentIntentResult UnavailableTool() =>
        new DockerAgentIntentResult.Rejected(
            "tool_not_available",
            "The live Docker session does not advertise this capability.");

    private static DockerAgentIntentResult Invalid(string message) =>
        new DockerAgentIntentResult.Rejected("tool_arguments_invalid", message);
}
