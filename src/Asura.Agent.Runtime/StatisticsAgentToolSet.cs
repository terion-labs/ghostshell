using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class StatisticsAgentToolSet
{
    private const string Description =
        "Read one numeric local-host resource snapshot from the exact "
        + "Statistics panel pinned to this run. The result contains bounded "
        + "aggregate CPU, memory, process-count, uptime, processor-count, and "
        + "network-rate observations only; it never exposes process identities, "
        + "database records, Docker state, command lines, or other mutations.";

    private static readonly AgentToolDefinition Read = Tool(panelIds: null);

    public static ImmutableArray<AgentToolDefinition> For(
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return Supports(panel) ? [Read] : [];
    }

    public static ImmutableArray<AgentToolDefinition> For(
        IReadOnlyList<AgentContextPanel> panels)
    {
        var eligible = ActiveStatisticsPanels(panels);
        return eligible.Length == 0
            ? []
            : [Tool(eligible.Select(panel => panel.PanelId).ToArray())];
    }

    public static ImmutableArray<AgentToolDefinition> ForWorkspace() => [
        AgentToolScopeSchema.WithRequiredPanelId(Read),
    ];

    internal static ImmutableArray<AgentContextPanel> ActiveStatisticsPanels(
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if (panels.Count is < 1 or > AgentContextRequest.MaximumAllowedPanelCount)
        {
            throw new ArgumentException(
                $"A statistics tool scope must contain between 1 and "
                + $"{AgentContextRequest.MaximumAllowedPanelCount} panels.",
                nameof(panels));
        }

        var panelIds = new HashSet<string>(StringComparer.Ordinal);
        var eligible = ImmutableArray.CreateBuilder<AgentContextPanel>(
            panels.Count);
        foreach (var panel in panels)
        {
            ArgumentNullException.ThrowIfNull(panel);
            if (!panelIds.Add(panel.PanelId.Value))
            {
                throw new ArgumentException(
                    "A statistics tool scope cannot contain duplicate panel IDs.",
                    nameof(panels));
            }

            if (Supports(panel))
            {
                eligible.Add(panel);
            }
        }

        return eligible.ToImmutable();
    }

    internal static bool Supports(AgentContextPanel panel) =>
        panel.Kind == PanelKind.Statistics
        && panel.HasRegisteredGraph
        && panel.IsCurrentPanelSession
        && panel.SessionId is not null
        && panel.Lifecycle == SessionLifecycle.Active
        && panel.Capabilities.Contains(
            SessionCapabilities.StatisticsRead,
            StringComparer.Ordinal);

    private static AgentToolDefinition Tool(
        IReadOnlyList<PanelInstanceId>? panelIds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        if (panelIds is not null)
        {
            writer.WriteStartObject("panel_id");
            writer.WriteString("type", "string");
            writer.WriteStartArray("enum");
            foreach (var panelId in panelIds)
            {
                writer.WriteStringValue(panelId.Value);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteStartArray("required");
        if (panelIds is not null)
        {
            writer.WriteStringValue("panel_id");
        }

        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.Flush();
        return new AgentToolDefinition(
            BuiltInAgentTools.StatisticsRead,
            panelIds is null
                ? Description
                : Description.Replace(
                    "exact Statistics panel pinned to this run",
                    "Statistics panel selected by panel_id",
                    StringComparison.Ordinal),
            buffer.WrittenSpan.ToArray());
    }
}
