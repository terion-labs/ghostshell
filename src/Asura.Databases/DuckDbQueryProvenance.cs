using System.Data.Common;
using System.Text.Json;
using Asura.Application;

namespace Asura.Databases;

/// <summary>
/// Uses DuckDB's own parser to recover lineage for the deliberately narrow
/// table-preview shape that DuckDB.NET does not expose through DbColumn.
/// Anything beyond one SELECT * from one base table fails closed.
/// </summary>
internal static class DuckDbQueryProvenance
{
    public static async Task<DatabaseQueryPage> EnrichAsync(
        DbConnection connection,
        string sql,
        DatabaseQueryPage result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Columns.Count == 0
            || result.Columns.Any(column => column.BaseObject is not null))
        {
            return result;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT json_serialize_sql(CAST($asura_sql AS VARCHAR));";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "asura_sql";
            parameter.Value = sql;
            command.Parameters.Add(parameter);
            var serialized = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            var source = TryReadSingleStarSource(serialized);
            if (source is null)
            {
                return result;
            }

            var columns = result.Columns
                .Select(column => column with
                {
                    BaseColumnName = column.BaseColumnName ?? column.Name,
                    BaseObject = source,
                })
                .ToArray();
            return result with { Columns = columns };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException
            or InvalidOperationException
            or JsonException)
        {
            // Parser metadata is an optional safety signal. If this DuckDB
            // build cannot provide it, the result remains browse-only.
            return result;
        }
    }

    internal static DatabaseObjectId? TryReadSingleStarSource(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        if (ReadBoolean(root, "error") != false
            || !TrySingleArrayItem(root, "statements", out var statement)
            || !statement.TryGetProperty("node", out var node)
            || !HasString(node, "type", "SELECT_NODE")
            || !HasEmptyCteMap(node)
            || !HasOnlySafeModifiers(node)
            || !IsEmptyArray(node, "group_expressions")
            || !IsEmptyArray(node, "group_sets")
            || !IsNull(node, "having")
            || !IsNull(node, "sample")
            || !IsNull(node, "qualify")
            || !TrySingleArrayItem(node, "select_list", out var projection)
            || !IsPlainStar(projection)
            || !node.TryGetProperty("from_table", out var from)
            || !HasString(from, "type", "BASE_TABLE")
            || !IsBlankString(from, "alias")
            || !IsNull(from, "sample")
            || !IsEmptyArray(from, "column_name_alias")
            || !TryReadNonBlankString(from, "table_name", out var tableName))
        {
            return null;
        }

        return new DatabaseObjectId(
            ReadOptionalString(from, "catalog_name"),
            ReadOptionalString(from, "schema_name"),
            tableName);
    }

    private static bool IsPlainStar(JsonElement projection) =>
        HasString(projection, "class", "STAR")
        && HasString(projection, "type", "STAR")
        && IsBlankString(projection, "alias")
        && IsBlankString(projection, "relation_name")
        && IsEmptyArray(projection, "exclude_list")
        && IsEmptyArray(projection, "replace_list")
        && IsEmptyArray(projection, "qualified_exclude_list")
        && IsEmptyArray(projection, "rename_list")
        && ReadBoolean(projection, "columns") == false;

    private static bool HasEmptyCteMap(JsonElement node) =>
        node.TryGetProperty("cte_map", out var cteMap)
        && IsEmptyArray(cteMap, "map");

    private static bool HasOnlySafeModifiers(JsonElement node)
    {
        if (!node.TryGetProperty("modifiers", out var modifiers)
            || modifiers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var modifier in modifiers.EnumerateArray())
        {
            if (!modifier.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("ORDER_MODIFIER" or "LIMIT_MODIFIER"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySingleArrayItem(
        JsonElement owner,
        string name,
        out JsonElement item)
    {
        if (owner.TryGetProperty(name, out var array)
            && array.ValueKind == JsonValueKind.Array
            && array.GetArrayLength() == 1)
        {
            item = array[0];
            return true;
        }

        item = default;
        return false;
    }

    private static bool IsEmptyArray(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() == 0;

    private static bool IsNull(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Null;

    private static bool HasString(JsonElement owner, string name, string expected) =>
        owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool IsBlankString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.IsNullOrEmpty(value.GetString());

    private static bool TryReadNonBlankString(
        JsonElement owner,
        string name,
        out string value)
    {
        value = owner.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? ReadOptionalString(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
