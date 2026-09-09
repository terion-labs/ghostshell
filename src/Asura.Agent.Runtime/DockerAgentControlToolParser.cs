using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.Agent.Runtime;

internal static class DockerAgentControlToolParser
{
    public static DockerAgentControlIntent Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels,
        bool requirePanelId)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!DockerAgentToolSet.IsControlTool(proposal.ToolName)
            || DockerAgentToolSet.RequiredCapability(proposal.ToolName) is not { } capability)
        {
            return new DockerAgentControlIntent.Rejected("tool_not_available");
        }

        try
        {
            if (proposal.Arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Docker mutation arguments must be an object.");
            }

            var properties = proposal.Arguments.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            AgentContextPanel panel;
            if (requirePanelId)
            {
                var panelId = ReadString(properties, "panel_id");
                panel = panels.Single(candidate =>
                    string.Equals(candidate.PanelId.Value, panelId, StringComparison.Ordinal)
                    && DockerAgentToolSet.Supports(candidate, capability));
            }
            else
            {
                panel = panels.Single(candidate =>
                    DockerAgentToolSet.Supports(candidate, capability));
            }

            var request = new AgentDockerControlRequest(
                panel.PanelId,
                new DockerResourceReferenceId(ReadString(properties, "container_ref")),
                new DockerEngineGeneration(ReadString(properties, "engine_generation")),
                new DockerContainerRevision(ReadString(properties, "container_revision")),
                Action(proposal.ToolName),
                ReadString(properties, "expected_state"));
            if (properties.Count != 0)
            {
                throw new ArgumentException("Docker mutation arguments contain unknown properties.");
            }

            return new DockerAgentControlIntent.Parsed(panel.PanelId, request);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return new DockerAgentControlIntent.Rejected("tool_arguments_invalid");
        }
    }

    private static string ReadString(
        IDictionary<string, JsonElement> properties,
        string name)
    {
        if (!properties.Remove(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text)
        {
            throw new ArgumentException($"Docker mutation argument '{name}' is required.");
        }

        return text;
    }

    private static DockerContainerAction Action(string toolName) => toolName switch
    {
        BuiltInAgentTools.DockerContainerStart => DockerContainerAction.Start,
        BuiltInAgentTools.DockerContainerStop => DockerContainerAction.Stop,
        BuiltInAgentTools.DockerContainerRestart => DockerContainerAction.Restart,
        BuiltInAgentTools.DockerContainerPause => DockerContainerAction.Pause,
        BuiltInAgentTools.DockerContainerResume => DockerContainerAction.Resume,
        BuiltInAgentTools.DockerContainerRemove => DockerContainerAction.Remove,
        _ => throw new ArgumentOutOfRangeException(nameof(toolName)),
    };
}
