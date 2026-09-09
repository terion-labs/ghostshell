using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class DatabaseAgentToolSet
{
    private static readonly ToolSpec[] Specifications =
    [
        new(
            BuiltInAgentTools.DatabaseReadState,
            SessionCapabilities.DatabaseReadState,
            "Read sanitized driver, readiness, server/TLS, selected catalog/schema, and Redis feature facts from one exact hosted Database Viewer. Never returns connection strings, endpoints, usernames, or passwords.",
            WriteNoArguments),
        new(
            BuiltInAgentTools.DatabaseListObjects,
            SessionCapabilities.DatabaseListObjects,
            "List a bounded page of relational tables/views from one exact hosted Database Viewer. Returned object_ref values are opaque session-local references for later reads.",
            WriteMaximumObjects),
        new(
            BuiltInAgentTools.DatabaseDescribeObject,
            SessionCapabilities.DatabaseDescribeObject,
            "Describe bounded relational column, key, index, and relation metadata for one exact opaque object_ref.",
            WriteObjectReference),
        new(
            BuiltInAgentTools.DatabaseReadTable,
            SessionCapabilities.DatabaseReadTable,
            "Read one bounded relational table page through structured filters and sorts against an opaque object_ref. SQL text, stored procedures, DDL, and writes are not accepted.",
            WriteTableRead),
        new(
            BuiltInAgentTools.DatabaseSchemaGraph,
            SessionCapabilities.DatabaseSchemaGraph,
            "Read a bounded relational table/foreign-key graph from one exact hosted Database Viewer.",
            WriteMaximumObjects),
        new(
            BuiltInAgentTools.RedisScan,
            SessionCapabilities.RedisScan,
            "Scan a bounded Redis key page with a pattern and opaque cursor. Returned key_ref values are session-local and must be used for redis.read.",
            WriteRedisScan),
        new(
            BuiltInAgentTools.RedisRead,
            SessionCapabilities.RedisRead,
            "Read the type, TTL, size, and bounded entries for one exact opaque Redis key_ref.",
            WriteRedisRead),
        new(
            BuiltInAgentTools.RedisListIndexes,
            SessionCapabilities.RedisListIndexes,
            "List bounded Redis Search index names, definitions, attributes, and document counts. This tool is present only when the live hosted Redis server advertises Search support.",
            WriteMaximumIndexes),
        new(
            BuiltInAgentTools.RedisSearch,
            SessionCapabilities.RedisSearch,
            "Run one bounded Redis Search query against an exact index. This tool is present only when the live hosted Redis server advertises Search support.",
            WriteRedisSearch),
    ];

    public static ImmutableArray<AgentToolDefinition> For(AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (!SupportsDatabasePanel(panel))
        {
            return [];
        }

        return [.. Specifications
            .Where(specification => Supports(panel, specification.Capability))
            .Select(specification => Tool(specification, panelIds: null))];
    }

    public static ImmutableArray<AgentToolDefinition> For(
        IReadOnlyList<AgentContextPanel> panels)
    {
        var eligible = ActiveDatabasePanels(panels);
        if (eligible.Length == 0)
        {
            return [];
        }

        var tools = ImmutableArray.CreateBuilder<AgentToolDefinition>();
        foreach (var specification in Specifications)
        {
            var panelIds = eligible
                .Where(panel => Supports(panel, specification.Capability))
                .Select(panel => panel.PanelId)
                .ToArray();
            if (panelIds.Length > 0)
            {
                tools.Add(Tool(specification, panelIds));
            }
        }

        return tools.ToImmutable();
    }

    public static ImmutableArray<AgentToolDefinition> ForWorkspace() =>
        [.. Specifications
            .Select(specification => AgentToolScopeSchema.WithRequiredPanelId(
                Tool(specification, panelIds: null)))];

    internal static ImmutableArray<AgentContextPanel> ActiveDatabasePanels(
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if (panels.Count is < 1 or > AgentContextRequest.MaximumAllowedPanelCount)
        {
            throw new ArgumentException(
                "A database tool scope requires a bounded panel collection.",
                nameof(panels));
        }

        if (panels.Select(panel => panel.PanelId).Distinct().Count() != panels.Count)
        {
            throw new ArgumentException(
                "A database tool scope cannot contain duplicate panel IDs.",
                nameof(panels));
        }

        return [.. panels.Where(SupportsDatabasePanel)];
    }

    internal static bool Supports(AgentContextPanel panel, string capability) =>
        SupportsDatabasePanel(panel)
        && panel.Capabilities.Contains(capability, StringComparer.Ordinal);

    internal static string? RequiredCapability(string toolName) =>
        Specifications.FirstOrDefault(specification => string.Equals(
            specification.Name,
            toolName,
            StringComparison.Ordinal))?.Capability;

    private static bool SupportsDatabasePanel(AgentContextPanel panel) =>
        panel.Kind == PanelKind.DatabaseViewer
        && panel.HasRegisteredGraph
        && panel.IsCurrentPanelSession
        && panel.SessionId is not null
        && panel.Lifecycle == SessionLifecycle.Active;

    private static AgentToolDefinition Tool(
        ToolSpec specification,
        IReadOnlyList<PanelInstanceId>? panelIds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        if (panelIds is not null)
        {
            WritePanelId(writer, panelIds);
        }

        specification.WriteArguments(writer);
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        if (panelIds is not null)
        {
            writer.WriteStringValue("panel_id");
        }

        WriteRequired(writer, specification.Name);
        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.Flush();
        return new AgentToolDefinition(
            specification.Name,
            panelIds is null
                ? specification.Description
                : specification.Description.Replace(
                    "one exact",
                    "the exact panel selected by panel_id from one",
                    StringComparison.Ordinal),
            buffer.WrittenSpan.ToArray());
    }

    private static void WritePanelId(
        Utf8JsonWriter writer,
        IReadOnlyList<PanelInstanceId> panelIds)
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

    private static void WriteNoArguments(Utf8JsonWriter writer)
    {
        _ = writer;
    }

    private static void WriteMaximumObjects(Utf8JsonWriter writer) =>
        WriteInteger(writer, "maximum_objects", 1, 500, 100);

    private static void WriteObjectReference(Utf8JsonWriter writer) =>
        WriteString(writer, "object_ref", 1, 128);

    private static void WriteTableRead(Utf8JsonWriter writer)
    {
        WriteString(writer, "object_ref", 1, 128);
        WriteInteger(writer, "offset", 0, 1_000_000, 0);
        WriteInteger(writer, "limit", 1, 200, 100);
        WriteStringArray(writer, "columns", 64, 256);
        WriteStringArray(writer, "exclude_columns", 64, 256);
        WriteInteger(
            writer,
            "maximum_cell_bytes",
            128,
            AgentDatabaseReadRequest.ReadTable.DefaultMaximumCellBytes,
            AgentDatabaseReadRequest.ReadTable.DefaultMaximumCellBytes);
        writer.WriteStartObject("filters");
        writer.WriteString("type", "array");
        writer.WriteNumber("maxItems", 16);
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        WriteString(writer, "column", 1, 256);
        writer.WriteStartObject("operator");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var value in DatabaseAgentToolParser.FilterOperatorNames)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("value");
        writer.WriteStartArray("oneOf");
        WriteFilterScalarSchema(writer);
        writer.WriteStartObject();
        writer.WriteString("type", "array");
        writer.WriteNumber("minItems", 1);
        writer.WriteNumber("maxItems", 64);
        writer.WritePropertyName("items");
        WriteFilterScalarSchema(writer);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("column");
        writer.WriteStringValue("operator");
        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject("sorts");
        writer.WriteString("type", "array");
        writer.WriteNumber("maxItems", 8);
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        WriteString(writer, "column", 1, 256);
        writer.WriteStartObject("direction");
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        writer.WriteStringValue("asc");
        writer.WriteStringValue("desc");
        writer.WriteEndArray();
        writer.WriteString("default", "asc");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("column");
        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRedisScan(Utf8JsonWriter writer)
    {
        WriteString(writer, "pattern", 0, 512, "*");
        WriteString(writer, "cursor", 0, 256);
        WriteInteger(writer, "count", 1, 500, 100);
    }

    private static void WriteRedisRead(Utf8JsonWriter writer)
    {
        WriteString(writer, "key_ref", 1, 128);
        WriteInteger(writer, "maximum_entries", 1, 500, 100);
    }

    private static void WriteRedisSearch(Utf8JsonWriter writer)
    {
        WriteString(writer, "index", 1, 256);
        WriteString(writer, "query", 1, 4_096);
        WriteInteger(writer, "limit", 1, 100, 50);
    }

    private static void WriteMaximumIndexes(Utf8JsonWriter writer) =>
        WriteInteger(writer, "maximum_indexes", 1, 100, 50);

    private static void WriteRequired(Utf8JsonWriter writer, string toolName)
    {
        switch (toolName)
        {
            case BuiltInAgentTools.DatabaseDescribeObject:
                writer.WriteStringValue("object_ref");
                break;
            case BuiltInAgentTools.DatabaseReadTable:
                writer.WriteStringValue("object_ref");
                break;
            case BuiltInAgentTools.RedisRead:
                writer.WriteStringValue("key_ref");
                break;
            case BuiltInAgentTools.RedisSearch:
                writer.WriteStringValue("index");
                writer.WriteStringValue("query");
                break;
        }
    }

    private static void WriteFilterScalarSchema(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("type");
        writer.WriteStringValue("string");
        writer.WriteStringValue("number");
        writer.WriteStringValue("boolean");
        writer.WriteEndArray();
        writer.WriteNumber("maxLength", 4_096);
        writer.WriteEndObject();
    }

    private static void WriteString(
        Utf8JsonWriter writer,
        string name,
        int minimumLength,
        int maximumLength,
        string? defaultValue = null)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteNumber("minLength", minimumLength);
        writer.WriteNumber("maxLength", maximumLength);
        if (defaultValue is not null)
        {
            writer.WriteString("default", defaultValue);
        }

        writer.WriteEndObject();
    }

    private static void WriteInteger(
        Utf8JsonWriter writer,
        string name,
        int minimum,
        int maximum,
        int defaultValue)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", minimum);
        writer.WriteNumber("maximum", maximum);
        writer.WriteNumber("default", defaultValue);
        writer.WriteEndObject();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string name,
        int maximumItems,
        int maximumLength)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "array");
        writer.WriteNumber("maxItems", maximumItems);
        writer.WriteBoolean("uniqueItems", true);
        writer.WriteStartObject("items");
        writer.WriteString("type", "string");
        writer.WriteNumber("minLength", 1);
        writer.WriteNumber("maxLength", maximumLength);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private sealed record ToolSpec(
        string Name,
        string Capability,
        string Description,
        Action<Utf8JsonWriter> WriteArguments);
}
