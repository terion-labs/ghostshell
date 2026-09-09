using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class DatabaseAgentToolResultJson
{
    internal const string ContentOrigin = "untrusted_database";
    private const string SecretRedaction = "[REDACTED SECRET VALUE]";

    public static DatabaseAgentToolJsonProjection Project(
        AgentDatabaseReadResult result,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        writer.WriteString("content_origin", ContentOrigin);
        writer.WriteString("operation", result.ToolName);
        var redactionCount = WriteResult(writer, result);
        writer.WriteNumber("redaction_count", redactionCount);
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("database_result_too_large", panelId);
        }

        return new DatabaseAgentToolJsonProjection(
            true,
            SuccessStableCode(result),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Failure(
        HostError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AgentToolResultJson.Failure(
            ProviderStableCode(error),
            error.Retryable,
            panelId);
    }

    internal static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.StableCode is
            "database_reference_expired"
            or "database_operation_unavailable"
            or "database_read_rejected"
            or "database_result_invalid"
            or "database_read_failed"
            or "database_authorization_expired"
            or "database_audit_unavailable"
            or "database_action_invalid"
            or "authority_revoked"
            or "session_revoked"
            or "caller_cancelled"
            || string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal))
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.InvalidRequest
                or HostErrorCode.NotFound
                or HostErrorCode.RevisionConflict => "target_changed",
            HostErrorCode.UnsupportedProtocol
                or HostErrorCode.CapabilityNotSupported
                or HostErrorCode.SessionClosed => "database_operation_unavailable",
            HostErrorCode.DeadlineExceeded => "deadline_exceeded",
            HostErrorCode.Cancelled => "cancelled",
            _ => "database_read_failed",
        };
    }

    private static int WriteResult(
        Utf8JsonWriter writer,
        AgentDatabaseReadResult result)
    {
        switch (result)
        {
            case AgentDatabaseReadResult.State state:
                WriteState(writer, state.Value);
                return 0;
            case AgentDatabaseReadResult.Objects objects:
                return WriteObjects(writer, objects.Value);
            case AgentDatabaseReadResult.ObjectDescription description:
                return WriteDescription(writer, description.Value);
            case AgentDatabaseReadResult.Table table:
                return WriteTable(writer, table.Value);
            case AgentDatabaseReadResult.Schema schema:
                return WriteSchema(writer, schema.Value);
            case AgentDatabaseReadResult.RedisKeys keys:
                return WriteRedisKeys(writer, keys.Value);
            case AgentDatabaseReadResult.RedisValue value:
                return WriteRedisValue(writer, value.Value);
            case AgentDatabaseReadResult.RedisSearch search:
                return WriteRedisSearch(writer, search.Value);
            case AgentDatabaseReadResult.RedisIndexes indexes:
                return WriteRedisIndexes(writer, indexes.Value);
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static void WriteState(
        Utf8JsonWriter writer,
        DatabasePanelSessionState state)
    {
        writer.WriteString("backend", state.Backend == DatabasePanelBackend.Redis
            ? "redis"
            : "relational");
        writer.WriteString("driver_id", state.DriverId);
        writer.WriteString("display_name", state.DisplayName);
        writer.WriteBoolean("ready", state.IsReady);
        WriteOptionalString(writer, "server_version", state.ServerVersion);
        WriteOptionalString(writer, "tls_protocol", state.TlsProtocol);
        WriteOptionalString(writer, "selected_catalog", state.SelectedCatalog);
        WriteOptionalString(writer, "selected_schema", state.SelectedSchema);
        if (state.Redis is { } redis)
        {
            writer.WriteStartObject("redis");
            WriteOptionalString(writer, "version", redis.Version);
            WriteOptionalString(writer, "protocol", redis.Protocol);
            writer.WriteString("topology", redis.Topology.ToString().ToLowerInvariant());
            writer.WriteString(
                "logical_databases",
                redis.LogicalDatabases.ToString().ToLowerInvariant());
            writer.WriteNumber("selected_database", redis.SelectedDatabase);
            WriteOptionalNumber(
                writer,
                "configured_database_count",
                redis.ConfiguredDatabaseCount);
            writer.WriteBoolean("search_available", redis.SearchAvailable);
            writer.WriteBoolean("json_available", redis.JsonAvailable);
            writer.WriteBoolean("time_series_available", redis.TimeSeriesAvailable);
            writer.WriteBoolean(
                "sharded_pubsub_available",
                redis.ShardedPubSubAvailable);
            WriteOptionalString(writer, "limitation", redis.Limitation);
            writer.WriteEndObject();
        }
    }

    private static int WriteObjects(
        Utf8JsonWriter writer,
        DatabaseObjectPage page)
    {
        var redactionCount = 0;
        writer.WriteStartArray("objects");
        foreach (var value in page.Objects)
        {
            WriteObject(writer, value, ref redactionCount);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", page.IsTruncated);
        return redactionCount;
    }

    private static int WriteDescription(
        Utf8JsonWriter writer,
        DatabaseObjectSnapshot snapshot)
    {
        var redactionCount = 0;
        writer.WritePropertyName("object");
        WriteObject(writer, snapshot.Object, ref redactionCount);
        writer.WriteStartArray("columns");
        foreach (var column in snapshot.Columns)
        {
            WriteColumn(writer, column, ref redactionCount);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("indexes");
        foreach (var index in snapshot.Indexes)
        {
            writer.WriteStartObject();
            writer.WriteString("name", index.Name);
            writer.WriteString("kind", index.Kind);
            writer.WriteBoolean("unique", index.IsUnique);
            writer.WriteBoolean("primary", index.IsPrimary);
            writer.WriteBoolean("valid", index.IsValid);
            WriteOptionalString(writer, "predicate", index.Predicate);
            writer.WriteStartArray("columns");
            foreach (var column in index.Columns)
            {
                writer.WriteStartObject();
                WriteOptionalString(writer, "name", column.Name);
                writer.WriteNumber("ordinal", column.Ordinal);
                writer.WriteBoolean("descending", column.IsDescending);
                writer.WriteBoolean("included", column.IsIncluded);
                WriteOptionalString(writer, "expression", column.Expression);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("can_edit_in_human_panel", snapshot.CanEdit);
        WriteOptionalString(writer, "read_only_reason", snapshot.ReadOnlyReason);
        writer.WriteBoolean("truncated", snapshot.IsTruncated);
        return redactionCount;
    }

    private static int WriteTable(
        Utf8JsonWriter writer,
        DatabaseTableSnapshot snapshot)
    {
        var redactionCount = 0;
        writer.WritePropertyName("object");
        WriteObject(writer, snapshot.Object, ref redactionCount);
        writer.WriteNumber("offset", snapshot.Page.Offset);
        writer.WriteNumber("limit", snapshot.Page.Limit);
        writer.WriteBoolean("has_more", snapshot.Page.HasMore);
        writer.WriteNumber("filtered_row_count", snapshot.Page.TotalRows);
        writer.WriteNumber("table_row_count", snapshot.Page.TableRows ?? snapshot.Page.TotalRows);
        writer.WriteBoolean("truncated", snapshot.Page.Result.Truncated);
        writer.WriteStartArray("columns");
        foreach (var column in snapshot.Page.Result.Columns)
        {
            writer.WriteStartObject();
            writer.WriteString("name", column.Name);
            writer.WriteString("data_type", column.DataTypeName);
            writer.WriteString("value_kind", column.ValueKind.ToString().ToLowerInvariant());
            WriteOptionalBoolean(writer, "nullable", column.IsNullable);
            writer.WriteBoolean("key", column.IsKey);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        foreach (var row in snapshot.Page.Result.Rows)
        {
            writer.WriteStartArray();
            for (var index = 0; index < row.Count; index++)
            {
                var cell = row[index];
                if (cell is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    var columnName = index < snapshot.Page.Result.Columns.Count
                        ? snapshot.Page.Result.Columns[index].Name
                        : string.Empty;
                    writer.WriteStringValue(Redact(
                        cell,
                        IsSecretName(columnName),
                        ref redactionCount));
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        return redactionCount;
    }

    private static int WriteSchema(
        Utf8JsonWriter writer,
        DatabaseSchemaGraphSnapshot graph)
    {
        var redactionCount = 0;
        writer.WriteStartArray("tables");
        foreach (var table in graph.Tables)
        {
            writer.WriteStartObject();
            WriteDescriptorProperties(writer, table.Object);
            writer.WriteStartArray("columns");
            foreach (var column in table.Columns)
            {
                WriteColumn(writer, column, ref redactionCount);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("foreign_keys");
            foreach (var key in table.ForeignKeys)
            {
                writer.WriteStartObject();
                writer.WriteString("name", key.Name);
                writer.WriteStartObject("referenced_object");
                WriteObjectIdProperties(writer, key.ReferencedObject);
                writer.WriteEndObject();
                writer.WriteStartArray("columns");
                foreach (var column in key.Columns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("column", column.ColumnName);
                    writer.WriteString("referenced_column", column.ReferencedColumnName);
                    writer.WriteNumber("ordinal", column.Ordinal);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", graph.IsTruncated);
        return redactionCount;
    }

    private static int WriteRedisKeys(Utf8JsonWriter writer, RedisKeyPage page)
    {
        var redactionCount = 0;
        writer.WriteStartArray("keys");
        foreach (var key in page.Keys)
        {
            WriteRedisKey(writer, key, ref redactionCount);
        }

        writer.WriteEndArray();
        WriteOptionalString(writer, "next_cursor", page.NextCursor);
        writer.WriteBoolean("complete", page.IsComplete);
        return redactionCount;
    }

    private static int WriteRedisValue(
        Utf8JsonWriter writer,
        RedisKeyValueSnapshot snapshot)
    {
        var redactionCount = 0;
        writer.WritePropertyName("key");
        WriteRedisKey(writer, snapshot.Key, ref redactionCount);
        WriteOptionalNumber(writer, "length", snapshot.Length);
        WriteRedisEntries(
            writer,
            snapshot.Entries,
            snapshot.Key.DisplayName,
            ref redactionCount);
        writer.WriteBoolean("truncated", snapshot.IsTruncated);
        WriteOptionalString(writer, "limitation", snapshot.Limitation);
        return redactionCount;
    }

    private static int WriteRedisSearch(
        Utf8JsonWriter writer,
        RedisSearchResult result)
    {
        var redactionCount = 0;
        writer.WriteNumber("total", result.Total);
        WriteRedisEntries(writer, result.Values, keyName: null, ref redactionCount);
        writer.WriteBoolean("truncated", result.Truncated);
        return redactionCount;
    }

    private static int WriteRedisIndexes(
        Utf8JsonWriter writer,
        RedisSearchIndexPage page)
    {
        var redactionCount = 0;
        writer.WriteStartArray("indexes");
        foreach (var index in page.Indexes)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "name",
                Redact(index.Name, IsSecretName(index.Name), ref redactionCount));
            WriteRedactedOptionalString(
                writer,
                "definition",
                index.Definition,
                ref redactionCount);
            WriteRedactedOptionalString(
                writer,
                "attributes",
                index.Attributes,
                ref redactionCount);
            WriteOptionalNumber(writer, "document_count", index.DocumentCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", page.IsTruncated);
        return redactionCount;
    }

    private static void WriteRedactedOptionalString(
        Utf8JsonWriter writer,
        string name,
        string? value,
        ref int redactionCount)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, Redact(value, force: false, ref redactionCount));
    }

    private static void WriteRedisEntries(
        Utf8JsonWriter writer,
        IReadOnlyList<RedisValueEntry> entries,
        string? keyName,
        ref int redactionCount)
    {
        writer.WriteStartArray("entries");
        foreach (var entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteString(
                "identity",
                Redact(
                    entry.Identity,
                    IsSecretName(entry.Identity),
                    ref redactionCount));
            if (entry.Field is { } field)
            {
                writer.WriteString(
                    "field",
                    Redact(field, IsSecretName(field), ref redactionCount));
            }
            else
            {
                writer.WriteNull("field");
            }
            writer.WriteString(
                "value",
                Redact(
                    entry.Value,
                    IsSecretName(keyName)
                        || IsSecretName(entry.Field)
                        || IsSecretName(entry.Identity),
                    ref redactionCount));
            if (entry.Score is { } score)
            {
                writer.WriteNumber("score", score);
            }
            else
            {
                writer.WriteNull("score");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteObject(
        Utf8JsonWriter writer,
        DatabaseObjectSummary value,
        ref int redactionCount)
    {
        writer.WriteStartObject();
        writer.WriteString("object_ref", value.Reference.Value);
        writer.WriteString(
            "name",
            Redact(value.Name, force: false, ref redactionCount));
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        WriteOptionalString(writer, "catalog", value.Catalog);
        WriteOptionalString(writer, "schema", value.Schema);
        writer.WriteEndObject();
    }

    private static void WriteColumn(
        Utf8JsonWriter writer,
        DatabaseColumnSchema column,
        ref int redactionCount)
    {
        writer.WriteStartObject();
        writer.WriteString("name", column.Name);
        writer.WriteNumber("ordinal", column.Ordinal);
        writer.WriteString("data_type", column.DataTypeName);
        writer.WriteString("value_kind", column.ValueKind.ToString().ToLowerInvariant());
        WriteOptionalBoolean(writer, "nullable", column.IsNullable);
        writer.WriteBoolean("primary_key", column.IsPrimaryKey);
        writer.WriteBoolean("identity", column.IsIdentity);
        writer.WriteBoolean("generated", column.IsGenerated);
        writer.WriteBoolean("read_only", column.IsReadOnly);
        if (column.DefaultExpression is { } defaultExpression)
        {
            writer.WriteString(
                "default_expression",
                Redact(
                    defaultExpression,
                    IsSecretName(column.Name),
                    ref redactionCount));
        }
        else
        {
            writer.WriteNull("default_expression");
        }
        writer.WriteEndObject();
    }

    private static void WriteDescriptorProperties(
        Utf8JsonWriter writer,
        DatabaseTableDescriptor value)
    {
        writer.WriteString("name", value.Name);
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        WriteOptionalString(writer, "catalog", value.Catalog);
        WriteOptionalString(writer, "schema", value.Schema);
    }

    private static void WriteObjectIdProperties(
        Utf8JsonWriter writer,
        DatabaseObjectId value)
    {
        writer.WriteString("name", value.Name);
        WriteOptionalString(writer, "catalog", value.Catalog);
        WriteOptionalString(writer, "schema", value.Schema);
    }

    private static void WriteRedisKey(
        Utf8JsonWriter writer,
        RedisKeyItem key,
        ref int redactionCount)
    {
        writer.WriteStartObject();
        writer.WriteString("key_ref", key.Reference.Value);
        writer.WriteString(
            "display_name",
            Redact(
                key.DisplayName,
                IsSecretName(key.DisplayName),
                ref redactionCount));
        writer.WriteString("type", key.Type);
        if (key.TimeToLive is { } timeToLive)
        {
            writer.WriteNumber("ttl_seconds", timeToLive.TotalSeconds);
        }
        else
        {
            writer.WriteNull("ttl_seconds");
        }

        WriteOptionalNumber(writer, "memory_bytes", key.MemoryBytes);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string name,
        long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string name,
        int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteOptionalBoolean(
        Utf8JsonWriter writer,
        string name,
        bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string Redact(
        string value,
        bool force,
        ref int redactionCount)
    {
        if (force)
        {
            redactionCount++;
            return SecretRedaction;
        }

        var redacted = TerminalContentRedactor.Redact(value);
        redactionCount += redacted.RedactionCount;
        return redacted.Text;
    }

    private static bool IsSecretName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("api_key", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("private_key", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal);
    }

    private static string SuccessStableCode(AgentDatabaseReadResult result) =>
        result switch
        {
            AgentDatabaseReadResult.State => "database_state_read",
            AgentDatabaseReadResult.Objects => "database_objects_listed",
            AgentDatabaseReadResult.ObjectDescription => "database_object_described",
            AgentDatabaseReadResult.Table => "database_table_read",
            AgentDatabaseReadResult.Schema => "database_schema_read",
            AgentDatabaseReadResult.RedisKeys => "redis_keys_scanned",
            AgentDatabaseReadResult.RedisValue => "redis_key_read",
            AgentDatabaseReadResult.RedisSearch => "redis_search_completed",
            AgentDatabaseReadResult.RedisIndexes => "redis_indexes_listed",
            _ => "database_read_completed",
        };

    private static DatabaseAgentToolJsonProjection Rejected(
        string stableCode,
        PanelInstanceId? panelId) =>
        new(
            false,
            stableCode,
            AgentToolResultJson.Failure(stableCode, retryable: false, panelId));
}

internal sealed record DatabaseAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
