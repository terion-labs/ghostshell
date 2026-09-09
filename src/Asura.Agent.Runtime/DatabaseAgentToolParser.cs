using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class DatabaseAgentToolParser
{
    internal static readonly string[] FilterOperatorNames =
    [
        "equal",
        "not_equal",
        "less_than",
        "less_than_or_equal",
        "greater_than",
        "greater_than_or_equal",
        "contains",
        "not_contains",
        "starts_with",
        "ends_with",
        "in",
        "not_in",
        "is_null",
        "is_not_null",
    ];

    public static DatabaseAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        var capability = DatabaseAgentToolSet.RequiredCapability(proposal.ToolName);
        if (capability is null)
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        if (!DatabaseAgentToolSet.Supports(panel, capability))
        {
            return UnavailableTool();
        }

        return ParseRequest(proposal.ToolName, panel.PanelId, properties);
    }

    public static DatabaseAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var capability = DatabaseAgentToolSet.RequiredCapability(proposal.ToolName);
        if (capability is null)
        {
            return UnknownTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        var eligible = DatabaseAgentToolSet.ActiveDatabasePanels(panels)
            .Where(panel => DatabaseAgentToolSet.Supports(panel, capability))
            .ToArray();
        if (eligible.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.Remove("panel_id", out var panelElement)
            || !TryGetString(panelElement, out var panelId))
        {
            return Invalid("A broad database tool requires one exact panel_id.");
        }

        var selected = eligible.FirstOrDefault(panel => string.Equals(
            panel.PanelId.Value,
            panelId,
            StringComparison.Ordinal));
        return selected is null
            ? Invalid("The selected panel_id is unavailable for this database tool.")
            : ParseRequest(proposal.ToolName, selected.PanelId, properties);
    }

    private static DatabaseAgentIntentResult ParseRequest(
        string toolName,
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        try
        {
            AgentDatabaseReadRequest request = toolName switch
            {
                BuiltInAgentTools.DatabaseReadState => ParseReadState(panelId, properties),
                BuiltInAgentTools.DatabaseListObjects => ParseListObjects(panelId, properties),
                BuiltInAgentTools.DatabaseDescribeObject => ParseDescribeObject(panelId, properties),
                BuiltInAgentTools.DatabaseReadTable => ParseReadTable(panelId, properties),
                BuiltInAgentTools.DatabaseSchemaGraph => ParseSchemaGraph(panelId, properties),
                BuiltInAgentTools.RedisScan => ParseRedisScan(panelId, properties),
                BuiltInAgentTools.RedisRead => ParseRedisRead(panelId, properties),
                BuiltInAgentTools.RedisListIndexes => ParseRedisListIndexes(panelId, properties),
                BuiltInAgentTools.RedisSearch => ParseRedisSearch(panelId, properties),
                _ => throw new InvalidOperationException("Unknown database tool."),
            };
            return new DatabaseAgentIntentResult.Parsed(panelId, request);
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return Invalid("Database tool arguments do not match the closed schema.");
        }
    }

    private static AgentDatabaseReadRequest ParseReadState(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.ReadState(panelId);
    }

    private static AgentDatabaseReadRequest ParseListObjects(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var maximumObjects = ReadOptionalInteger(
            properties,
            "maximum_objects",
            100);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.ListObjects(panelId, maximumObjects);
    }

    private static AgentDatabaseReadRequest ParseDescribeObject(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var reference = new DatabaseObjectReference(
            ReadRequiredString(properties, "object_ref"));
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.DescribeObject(panelId, reference);
    }

    private static AgentDatabaseReadRequest ParseReadTable(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var reference = new DatabaseObjectReference(
            ReadRequiredString(properties, "object_ref"));
        var offset = ReadOptionalInteger(properties, "offset", 0);
        var limit = ReadOptionalInteger(properties, "limit", 100);
        var filters = properties.Remove("filters", out var filtersElement)
            ? ParseFilters(filtersElement)
            : [];
        var sorts = properties.Remove("sorts", out var sortsElement)
            ? ParseSorts(sortsElement)
            : [];
        var columns = ReadOptionalStringArray(properties, "columns", 64);
        var excludeColumns = ReadOptionalStringArray(properties, "exclude_columns", 64);
        var maximumCellBytes = ReadOptionalInteger(
            properties,
            "maximum_cell_bytes",
            AgentDatabaseReadRequest.ReadTable.DefaultMaximumCellBytes);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.ReadTable(
            panelId,
            reference,
            filters,
            sorts,
            offset,
            limit,
            columns,
            excludeColumns,
            maximumCellBytes);
    }

    private static AgentDatabaseReadRequest ParseSchemaGraph(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var maximumObjects = ReadOptionalInteger(
            properties,
            "maximum_objects",
            100);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.SchemaGraph(panelId, maximumObjects);
    }

    private static AgentDatabaseReadRequest ParseRedisScan(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var pattern = ReadOptionalString(properties, "pattern") ?? "*";
        var cursor = ReadOptionalString(properties, "cursor");
        var count = ReadOptionalInteger(properties, "count", 100);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.RedisScan(panelId, pattern, cursor, count);
    }

    private static AgentDatabaseReadRequest ParseRedisRead(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var reference = new RedisKeyReferenceId(
            ReadRequiredString(properties, "key_ref"));
        var maximumEntries = ReadOptionalInteger(
            properties,
            "maximum_entries",
            100);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.RedisRead(
            panelId,
            reference,
            maximumEntries);
    }

    private static AgentDatabaseReadRequest ParseRedisSearch(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var index = ReadRequiredString(properties, "index");
        var query = ReadRequiredString(properties, "query");
        var limit = ReadOptionalInteger(properties, "limit", 50);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.RedisSearch(panelId, index, query, limit);
    }

    private static AgentDatabaseReadRequest ParseRedisListIndexes(
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        var maximumIndexes = ReadOptionalInteger(
            properties,
            "maximum_indexes",
            50);
        RequireNoProperties(properties);
        return new AgentDatabaseReadRequest.RedisListIndexes(
            panelId,
            maximumIndexes);
    }

    private static IReadOnlyList<AgentDatabaseFilter> ParseFilters(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > 16)
        {
            throw new ArgumentException("Database filters must be a bounded array.");
        }

        var filters = new List<AgentDatabaseFilter>();
        foreach (var item in element.EnumerateArray())
        {
            var properties = ReadObject(item, "database filter");
            var column = ReadRequiredString(properties, "column");
            var operatorName = ReadRequiredString(properties, "operator");
            var @operator = ParseFilterOperator(operatorName);
            var value = properties.Remove("value", out var valueElement)
                ? ParseFilterValue(valueElement)
                : null;
            RequireNoProperties(properties);
            filters.Add(new AgentDatabaseFilter(column, @operator, value));
        }

        return filters;
    }

    private static IReadOnlyList<AgentDatabaseSort> ParseSorts(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > 8)
        {
            throw new ArgumentException("Database sorts must be a bounded array.");
        }

        var sorts = new List<AgentDatabaseSort>();
        foreach (var item in element.EnumerateArray())
        {
            var properties = ReadObject(item, "database sort");
            var column = ReadRequiredString(properties, "column");
            var direction = ReadOptionalString(properties, "direction") ?? "asc";
            if (direction is not ("asc" or "desc"))
            {
                throw new ArgumentException("A database sort direction is invalid.");
            }

            RequireNoProperties(properties);
            sorts.Add(new AgentDatabaseSort(column, string.Equals(direction, "desc", StringComparison.Ordinal)));
        }

        return sorts;
    }

    private static AgentDatabaseFilterValue ParseFilterValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => new AgentDatabaseFilterValue.Text(
                element.GetString() ?? string.Empty),
            JsonValueKind.True => new AgentDatabaseFilterValue.Boolean(true),
            JsonValueKind.False => new AgentDatabaseFilterValue.Boolean(false),
            JsonValueKind.Number when element.TryGetInt64(out var integer) =>
                new AgentDatabaseFilterValue.Integer(integer),
            JsonValueKind.Number when element.TryGetDecimal(out var number) =>
                new AgentDatabaseFilterValue.Decimal(number),
            JsonValueKind.Array => new AgentDatabaseFilterValue.List(
                [.. element.EnumerateArray().Select(ParseScalarFilterValue)]),
            _ => throw new ArgumentException("A database filter value is invalid."),
        };
    }

    private static AgentDatabaseFilterValue ParseScalarFilterValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? throw new ArgumentException("Database filter lists cannot be nested.")
            : ParseFilterValue(element);

    private static DatabaseFilterOperator ParseFilterOperator(string value) => value switch
    {
        "equal" => DatabaseFilterOperator.Equal,
        "not_equal" => DatabaseFilterOperator.NotEqual,
        "less_than" => DatabaseFilterOperator.LessThan,
        "less_than_or_equal" => DatabaseFilterOperator.LessThanOrEqual,
        "greater_than" => DatabaseFilterOperator.GreaterThan,
        "greater_than_or_equal" => DatabaseFilterOperator.GreaterThanOrEqual,
        "contains" => DatabaseFilterOperator.Contains,
        "not_contains" => DatabaseFilterOperator.NotContains,
        "starts_with" => DatabaseFilterOperator.StartsWith,
        "ends_with" => DatabaseFilterOperator.EndsWith,
        "in" => DatabaseFilterOperator.In,
        "not_in" => DatabaseFilterOperator.NotIn,
        "is_null" => DatabaseFilterOperator.IsNull,
        "is_not_null" => DatabaseFilterOperator.IsNotNull,
        _ => throw new ArgumentException("A database filter operator is invalid."),
    };

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out DatabaseAgentIntentResult rejection)
    {
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            properties = [];
            rejection = Invalid("Database tool arguments must be one object.");
            return false;
        }

        try
        {
            properties = ReadObject(proposal.Arguments, "database tool");
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

    private static Dictionary<string, JsonElement> ReadObject(
        JsonElement element,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"A {description} must be one object.");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new ArgumentException(
                    $"A {description} cannot contain duplicate fields.");
            }
        }

        return properties;
    }

    private static string ReadRequiredString(
        Dictionary<string, JsonElement> properties,
        string name) =>
        properties.Remove(name, out var element) && TryGetString(element, out var value)
            ? value
            : throw new ArgumentException($"Database tool field '{name}' is required.");

    private static string? ReadOptionalString(
        Dictionary<string, JsonElement> properties,
        string name)
    {
        if (!properties.Remove(name, out var element))
        {
            return null;
        }

        return TryGetString(element, out var value)
            ? value
            : throw new ArgumentException($"Database tool field '{name}' must be a string.");
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

        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : throw new ArgumentException(
                    $"Database tool field '{name}' must be an integer.");
    }

    private static IReadOnlyList<string> ReadOptionalStringArray(
        Dictionary<string, JsonElement> properties,
        string name,
        int maximumItems)
    {
        if (!properties.Remove(name, out var element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > maximumItems)
        {
            throw new ArgumentException(
                $"Database tool field '{name}' must be a bounded string array.");
        }

        return [.. element.EnumerateArray()
            .Select(item => TryGetString(item, out var value)
                ? value
                : throw new ArgumentException(
                    $"Database tool field '{name}' must contain only strings."))];
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static void RequireNoProperties(
        Dictionary<string, JsonElement> properties)
    {
        if (properties.Count != 0)
        {
            throw new ArgumentException(
                "Database tool arguments contain an unknown field.");
        }
    }

    private static DatabaseAgentIntentResult UnknownTool() =>
        new DatabaseAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a database tool unavailable to this run.");

    private static DatabaseAgentIntentResult UnavailableTool() =>
        new DatabaseAgentIntentResult.Rejected(
            "tool_not_available",
            "No eligible hosted Database Viewer supports this live operation.");

    private static DatabaseAgentIntentResult Invalid(string message) =>
        new DatabaseAgentIntentResult.Rejected("invalid_tool_arguments", message);
}
