using System.Collections.ObjectModel;
using System.Globalization;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Closed, typed database observations accepted by the governed execution
/// boundary. SQL text and write operations intentionally have no variant.
/// </summary>
public abstract record AgentDatabaseReadRequest
{
    private AgentDatabaseReadRequest(
        PanelInstanceId panelId,
        string toolName,
        string requiredSessionCapability)
    {
        if (string.IsNullOrWhiteSpace(panelId.Value)
            || panelId.Value.Length > 256
            || panelId.Value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A database observation requires a bounded panel identifier.",
                nameof(panelId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredSessionCapability);
        PanelId = panelId;
        ToolName = toolName;
        RequiredSessionCapability = requiredSessionCapability;
    }

    public PanelInstanceId PanelId { get; }

    public string ToolName { get; }

    public string RequiredSessionCapability { get; }

    public sealed record ReadState : AgentDatabaseReadRequest
    {
        public ReadState(PanelInstanceId panelId)
            : base(
                panelId,
                BuiltInAgentTools.DatabaseReadState,
                SessionCapabilities.DatabaseReadState)
        {
        }
    }

    public sealed record ListObjects : AgentDatabaseReadRequest
    {
        public ListObjects(PanelInstanceId panelId, int maximumObjects)
            : base(
                panelId,
                BuiltInAgentTools.DatabaseListObjects,
                SessionCapabilities.DatabaseListObjects)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumObjects, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumObjects, 500);
            MaximumObjects = maximumObjects;
        }

        public int MaximumObjects { get; }
    }

    public sealed record DescribeObject : AgentDatabaseReadRequest
    {
        public DescribeObject(
            PanelInstanceId panelId,
            DatabaseObjectReference reference)
            : base(
                panelId,
                BuiltInAgentTools.DatabaseDescribeObject,
                SessionCapabilities.DatabaseDescribeObject)
        {
            Reference = reference;
        }

        public DatabaseObjectReference Reference { get; }
    }

    public sealed record ReadTable : AgentDatabaseReadRequest
    {
        public const int DefaultMaximumCellBytes = 8_192;

        public ReadTable(
            PanelInstanceId panelId,
            DatabaseObjectReference reference,
            IReadOnlyList<AgentDatabaseFilter> filters,
            IReadOnlyList<AgentDatabaseSort> sorts,
            int offset,
            int limit,
            IReadOnlyList<string>? columns = null,
            IReadOnlyList<string>? excludeColumns = null,
            int maximumCellBytes = DefaultMaximumCellBytes)
            : base(
                panelId,
                BuiltInAgentTools.DatabaseReadTable,
                SessionCapabilities.DatabaseReadTable)
        {
            ArgumentNullException.ThrowIfNull(filters);
            ArgumentNullException.ThrowIfNull(sorts);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 1_000_000);
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
            if (filters.Count > 16 || sorts.Count > 8)
            {
                throw new ArgumentException(
                    "A database table observation exceeds its filter or sort bound.");
            }

            var normalizedColumns = NormalizeColumnSelection(columns, nameof(columns));
            var normalizedExcludedColumns = NormalizeColumnSelection(
                excludeColumns,
                nameof(excludeColumns));
            if (normalizedColumns.Count > 0 && normalizedExcludedColumns.Count > 0)
            {
                throw new ArgumentException(
                    "A database table observation cannot include and exclude columns together.");
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCellBytes, 128);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCellBytes, DefaultMaximumCellBytes);

            Reference = reference;
            Filters = Array.AsReadOnly(filters
                .Select(item => item ?? throw new ArgumentException(
                    "Database filters cannot contain null entries.",
                    nameof(filters)))
                .ToArray());
            Sorts = Array.AsReadOnly(sorts
                .Select(item => item ?? throw new ArgumentException(
                    "Database sorts cannot contain null entries.",
                    nameof(sorts)))
                .ToArray());
            Offset = offset;
            Limit = limit;
            Columns = normalizedColumns;
            ExcludeColumns = normalizedExcludedColumns;
            MaximumCellBytes = maximumCellBytes;
        }

        public DatabaseObjectReference Reference { get; }

        public IReadOnlyList<AgentDatabaseFilter> Filters { get; }

        public IReadOnlyList<AgentDatabaseSort> Sorts { get; }

        public int Offset { get; }

        public int Limit { get; }

        public IReadOnlyList<string> Columns { get; }

        public IReadOnlyList<string> ExcludeColumns { get; }

        public int MaximumCellBytes { get; }

        public DatabaseTableReadRequest ToSessionRequest() => new(
            Reference,
            new DatabaseTableQuery(
                [.. Filters.Select(filter => filter.ToSessionFilter())],
                [.. Sorts.Select(sort => new DatabaseSort(
                    sort.ColumnName,
                    sort.Descending))],
                Offset,
                Limit,
                Columns,
                ExcludeColumns));

        private static IReadOnlyList<string> NormalizeColumnSelection(
            IReadOnlyList<string>? values,
            string parameterName)
        {
            if (values is null || values.Count == 0)
            {
                return [];
            }

            if (values.Count > 64)
            {
                throw new ArgumentException(
                    "A database column selection cannot exceed 64 entries.",
                    parameterName);
            }

            var normalized = values.Select(value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
                if (value.Length > 256 || value.Any(char.IsControl))
                {
                    throw new ArgumentException(
                        "A database column name must be bounded text.",
                        parameterName);
                }

                return string.Concat(value);
            }).ToArray();
            if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            {
                throw new ArgumentException(
                    "A database column selection cannot contain duplicates.",
                    parameterName);
            }

            return Array.AsReadOnly(normalized);
        }
    }

    public sealed record SchemaGraph : AgentDatabaseReadRequest
    {
        public SchemaGraph(PanelInstanceId panelId, int maximumObjects)
            : base(
                panelId,
                BuiltInAgentTools.DatabaseSchemaGraph,
                SessionCapabilities.DatabaseSchemaGraph)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumObjects, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumObjects, 500);
            MaximumObjects = maximumObjects;
        }

        public int MaximumObjects { get; }
    }

    public sealed record RedisScan : AgentDatabaseReadRequest
    {
        public RedisScan(
            PanelInstanceId panelId,
            string pattern,
            string? cursor,
            int count)
            : base(
                panelId,
                BuiltInAgentTools.RedisScan,
                SessionCapabilities.RedisScan)
        {
            Pattern = RequireText(pattern, nameof(pattern), 512, allowEmpty: true);
            Cursor = cursor is null
                ? null
                : RequireText(cursor, nameof(cursor), 256, allowEmpty: true);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 500);
            Count = count;
        }

        public string Pattern { get; }

        public string? Cursor { get; }

        public int Count { get; }
    }

    public sealed record RedisRead : AgentDatabaseReadRequest
    {
        public RedisRead(
            PanelInstanceId panelId,
            RedisKeyReferenceId reference,
            int maximumEntries)
            : base(
                panelId,
                BuiltInAgentTools.RedisRead,
                SessionCapabilities.RedisRead)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntries, 500);
            Reference = reference;
            MaximumEntries = maximumEntries;
        }

        public RedisKeyReferenceId Reference { get; }

        public int MaximumEntries { get; }
    }

    public sealed record RedisSearch : AgentDatabaseReadRequest
    {
        public RedisSearch(
            PanelInstanceId panelId,
            string index,
            string query,
            int limit)
            : base(
                panelId,
                BuiltInAgentTools.RedisSearch,
                SessionCapabilities.RedisSearch)
        {
            Index = RequireText(index, nameof(index), 256, allowEmpty: false);
            Query = RequireText(query, nameof(query), 4_096, allowEmpty: false);
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
            Limit = limit;
        }

        public string Index { get; }

        public string Query { get; }

        public int Limit { get; }
    }

    public sealed record RedisListIndexes : AgentDatabaseReadRequest
    {
        public RedisListIndexes(
            PanelInstanceId panelId,
            int maximumIndexes)
            : base(
                panelId,
                BuiltInAgentTools.RedisListIndexes,
                SessionCapabilities.RedisListIndexes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumIndexes, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumIndexes, 100);
            MaximumIndexes = maximumIndexes;
        }

        public int MaximumIndexes { get; }
    }

    private static string RequireText(
        string value,
        string parameterName,
        int maximumLength,
        bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value))
            || value.Length > maximumLength
            || value.Any(char.IsControl)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "A database observation argument is invalid.",
                parameterName);
        }

        return value;
    }
}

public sealed record AgentDatabaseFilter
{
    public AgentDatabaseFilter(
        string columnName,
        DatabaseFilterOperator @operator,
        AgentDatabaseFilterValue? value = null)
    {
        ColumnName = RequireColumnName(columnName);
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator));
        }

        var nullOperator = @operator is DatabaseFilterOperator.IsNull
            or DatabaseFilterOperator.IsNotNull;
        if (nullOperator != (value is null))
        {
            throw new ArgumentException(
                nullOperator
                    ? "A null-test filter cannot carry a value."
                    : "This database filter requires a typed value.",
                nameof(value));
        }

        var listOperator = @operator is DatabaseFilterOperator.In
            or DatabaseFilterOperator.NotIn;
        if (listOperator != (value is AgentDatabaseFilterValue.List))
        {
            throw new ArgumentException(
                listOperator
                    ? "An IN filter requires a bounded list value."
                    : "Only an IN filter can carry a list value.",
                nameof(value));
        }

        Operator = @operator;
        Value = value;
    }

    public string ColumnName { get; }

    public DatabaseFilterOperator Operator { get; }

    public AgentDatabaseFilterValue? Value { get; }

    internal DatabaseFilterCondition ToSessionFilter() => new(
        ColumnName,
        Operator,
        Value?.ToProviderValue());

    private static string RequireColumnName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A database filter column name is invalid.",
                nameof(value));
        }

        return value;
    }
}

public sealed record AgentDatabaseSort
{
    public AgentDatabaseSort(string columnName, bool descending = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (columnName.Length > 256 || columnName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A database sort column name is invalid.",
                nameof(columnName));
        }

        ColumnName = columnName;
        Descending = descending;
    }

    public string ColumnName { get; }

    public bool Descending { get; }
}

public abstract record AgentDatabaseFilterValue
{
    private AgentDatabaseFilterValue()
    {
    }

    internal abstract object ToProviderValue();

    internal abstract string CanonicalValue { get; }

    public sealed record Text : AgentDatabaseFilterValue
    {
        public Text(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > 4_096
                || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A database text filter value is invalid.",
                    nameof(value));
            }

            if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
            {
                throw new ArgumentException(
                    "A database text filter cannot contain literal secret material.",
                    nameof(value));
            }

            Value = value;
        }

        public string Value { get; }

        internal override object ToProviderValue() => Value;

        internal override string CanonicalValue => $"text:{Value}";
    }

    public sealed record Boolean(bool Value) : AgentDatabaseFilterValue
    {
        internal override object ToProviderValue() => Value;

        internal override string CanonicalValue => Value ? "bool:true" : "bool:false";
    }

    public sealed record Integer(long Value) : AgentDatabaseFilterValue
    {
        internal override object ToProviderValue() => Value;

        internal override string CanonicalValue =>
            $"integer:{Value.ToString(CultureInfo.InvariantCulture)}";
    }

    public sealed record Decimal : AgentDatabaseFilterValue
    {
        public Decimal(decimal value)
        {
            Value = value;
        }

        public decimal Value { get; }

        internal override object ToProviderValue() => Value;

        internal override string CanonicalValue =>
            $"decimal:{Value.ToString(CultureInfo.InvariantCulture)}";
    }

    public sealed record List : AgentDatabaseFilterValue
    {
        public List(IReadOnlyList<AgentDatabaseFilterValue> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values.Count is < 1 or > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(values));
            }

            Values = new ReadOnlyCollection<AgentDatabaseFilterValue>([.. values
                .Select(value => value switch
                {
                    null => throw new ArgumentException(
                        "A database filter list cannot contain null.",
                        nameof(values)),
                    List => throw new ArgumentException(
                        "A database filter list cannot be nested.",
                        nameof(values)),
                    _ => value,
                })]);
        }

        public IReadOnlyList<AgentDatabaseFilterValue> Values { get; }

        internal override object ToProviderValue() =>
            Values.Select(value => value.ToProviderValue()).ToArray();

        internal override string CanonicalValue => string.Join(
            "|",
            Values.Select(value => value.CanonicalValue));
    }
}
