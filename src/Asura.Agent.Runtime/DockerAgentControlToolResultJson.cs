using System.Buffers;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class DockerAgentControlToolResultJson
{
    public const string OutcomeUnknownStableCode = "docker_mutation_outcome_unknown";

    public static string Write(
        AgentDockerControlResult result,
        PanelInstanceId? panelId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>(512);
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean(
            "ok",
            result.Outcome == DockerContainerControlOutcome.Applied);
        writer.WriteString("stable_code", result.StableCode);
        writer.WriteString("tool_name", result.ToolName);
        writer.WriteString("outcome", result.Outcome switch
        {
            DockerContainerControlOutcome.Applied => "applied",
            DockerContainerControlOutcome.NotDispatched => "not_dispatched",
            DockerContainerControlOutcome.OutcomeUnknown => "outcome_unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        });
        writer.WriteBoolean("retryable", result.Retryable);
        if (panelId is { } selectedPanel)
        {
            writer.WriteString("panel_id", selectedPanel.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
