using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class WorkspaceLayoutAgentToolSet
{
    public static ImmutableArray<AgentToolDefinition> For(
        AgentContextSnapshot context,
        IReadOnlySet<PanelKind> supportedPanelKinds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(supportedPanelKinds);
        if (context.Target is not AgentTarget.Workspace)
        {
            return [];
        }

        var kinds = Enum.GetValues<PanelKind>()
            .Where(AgentWorkspaceLayoutRequest.IsCreatableKind)
            .OrderBy(kind => (int)kind)
            .ToArray();
        _ = supportedPanelKinds;
        return
        [
            Tool(
                BuiltInAgentTools.ConnectionsList,
                "List bounded saved connections available to this workspace. "
                    + "Returns opaque connection_ref values and compatible panel kinds; "
                    + "never returns endpoints, usernames, paths, or credentials.",
                Schema(_ => { })),
            Tool(
                BuiltInAgentTools.TabCreate,
                "Create one tab in this run's workspace with one selected panel kind. "
                    + "The new tab becomes active. Pass a compatible connection_ref from "
                    + "connections.list to select its connection; terminal requires one.",
                Schema(
                    writer =>
                    {
                        WriteKindProperty(writer, kinds);
                        WriteIdProperty(writer, "connection_ref");
                    },
                    "kind")),
            Tool(
                BuiltInAgentTools.TabClose,
                "Close one exact tab and its sessions. Active work may be force-terminated; "
                    + "unsaved database edits require human handling.",
                Schema(
                    writer => WriteIdProperty(
                        writer,
                        "tab_id"),
                    "tab_id")),
            Tool(
                BuiltInAgentTools.PanelAdd,
                "Add one panel of the selected kind to an exact tab. Pass a compatible "
                    + "connection_ref from connections.list to select its connection; "
                    + "terminal requires one.",
                Schema(writer =>
                {
                    WriteIdProperty(writer, "tab_id");
                    WriteKindProperty(writer, kinds);
                    WriteIdProperty(writer, "connection_ref");
                }, "tab_id", "kind")),
            Tool(
                BuiltInAgentTools.PanelSplit,
                "Split one exact panel left/right or top/bottom and create the selected "
                    + "panel kind in the new cell. Pass a compatible connection_ref from "
                    + "connections.list to select its connection; terminal requires one.",
                Schema(writer =>
                {
                    WriteIdProperty(writer, "panel_id");
                    WriteEnumProperty(writer, "orientation", ["left_right", "top_bottom"]);
                    WriteKindProperty(writer, kinds);
                    WriteIdProperty(writer, "connection_ref");
                }, "panel_id", "orientation", "kind")),
            Tool(
                BuiltInAgentTools.PanelClose,
                "Close one exact panel and its session. Active work may be force-terminated; "
                    + "unsaved database edits require human handling.",
                Schema(
                    writer => WriteIdProperty(
                        writer,
                        "panel_id"),
                    "panel_id")),
            Tool(
                BuiltInAgentTools.PanelConnect,
                "Bind one exact panel to one opaque connection_ref returned by "
                    + "connections.list. No connection is selected implicitly.",
                Schema(writer =>
                {
                    WriteIdProperty(writer, "panel_id");
                    WriteIdProperty(writer, "connection_ref");
                }, "panel_id", "connection_ref")),
        ];
    }

    private static byte[] Schema(
        Action<Utf8JsonWriter> writeProperties,
        params string[] required)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writeProperties(writer);
        writer.WriteEndObject();
        WriteRequired(writer, required);
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteIdProperty(
        Utf8JsonWriter writer,
        string name)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteNumber("minLength", 1);
        writer.WriteNumber("maxLength", 128);
        writer.WriteEndObject();
    }

    private static void WriteKindProperty(
        Utf8JsonWriter writer,
        IEnumerable<PanelKind> kinds) =>
        WriteEnumProperty(
            writer,
            "kind",
            kinds.Select(PanelKindName));

    private static void WriteEnumProperty(
        Utf8JsonWriter writer,
        string name,
        IEnumerable<string> values)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRequired(
        Utf8JsonWriter writer,
        params string[] names)
    {
        writer.WriteStartArray("required");
        foreach (var name in names)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }

    private static AgentToolDefinition Tool(
        string name,
        string description,
        byte[] schema) =>
        new(name, description, schema);

    internal static string PanelKindName(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "terminal",
        PanelKind.Browser => "browser",
        PanelKind.FileViewer => "file_viewer",
        PanelKind.Statistics => "statistics",
        PanelKind.ProcessMonitor => "process_monitor",
        PanelKind.Placeholder => "placeholder",
        PanelKind.DatabaseViewer => "database_viewer",
        PanelKind.Docker => "docker",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
