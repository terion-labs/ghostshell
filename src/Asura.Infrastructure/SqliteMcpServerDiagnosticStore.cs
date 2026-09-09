using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// Persists one closed, bounded diagnostic summary per MCP profile. The
/// contract has no field for raw stderr, environment values, endpoints, or
/// server-selected text.
/// </summary>
public sealed class SqliteMcpServerDiagnosticStore(
    AsuraDatabase database,
    TimeProvider timeProvider) : IMcpServerDiagnosticStore
{
    private const int MaximumPayloadBytes = 256 * 1024;
    public async ValueTask<ApplicationRunResult<McpServerDiagnosticsSnapshot>>
        ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json
                FROM mcp_server_diagnostic_summary
                WHERE singleton_id = 1;
                """;
            if (await command.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) is not string payload
                || Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
            {
                return Failure<McpServerDiagnosticsSnapshot>(
                    "The stored MCP diagnostic summary is invalid.");
            }

            var summaries = JsonSerializer.Deserialize(
                payload,
                McpDiagnosticJsonContext.Default
                    .McpServerDiagnosticSummaryArray);
            return summaries is null
                ? Failure<McpServerDiagnosticsSnapshot>(
                    "The stored MCP diagnostic summary is unreadable.")
                : ApplicationRunResult<McpServerDiagnosticsSnapshot>.Success(
                    new McpServerDiagnosticsSnapshot(
                        summaries,
                        cleanupUncertain: false,
                        cleanupUncertainAtUtc: null));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure<McpServerDiagnosticsSnapshot>(
                "Loading MCP diagnostics was cancelled.",
                ApplicationRunErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is
            JsonException or ArgumentException or SqliteException)
        {
            return Failure<McpServerDiagnosticsSnapshot>(
                "The MCP diagnostic summary could not be loaded.");
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        McpServerDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var payload = JsonSerializer.Serialize(
                (McpServerDiagnosticSummary[])[.. snapshot.Summaries],
                McpDiagnosticJsonContext.Default
                    .McpServerDiagnosticSummaryArray);
            if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
            {
                return Failure<Unit>(
                    "The MCP diagnostic summary exceeds its storage budget.");
            }

            await using var connection = await database.OpenConnectionAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mcp_server_diagnostic_summary
                SET payload_json = $payload,
                    updated_utc = $updatedUtc
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue(
                "$updatedUtc",
                timeProvider.GetUtcNow().ToUniversalTime().ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) == 1
                ? ApplicationRunResult<Unit>.Success(Unit.Value)
                : Failure<Unit>("The MCP diagnostic summary row is missing.");
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                "Saving MCP diagnostics was cancelled.",
                ApplicationRunErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is
            JsonException or ArgumentException or SqliteException)
        {
            return Failure<Unit>(
                "The MCP diagnostic summary could not be saved.");
        }
    }

    public ValueTask<ApplicationRunResult<Unit>> ClearAsync(
        CancellationToken cancellationToken) =>
        WriteAsync(
            new McpServerDiagnosticsSnapshot(
                [],
                cleanupUncertain: false,
                cleanupUncertainAtUtc: null),
            cancellationToken);

    private static ApplicationRunResult<T> Failure<T>(
        string message,
        ApplicationRunErrorCode code = ApplicationRunErrorCode.StorageFailure) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(code, message));
}

[JsonSourceGenerationOptions(MaxDepth = 8)]
[JsonSerializable(typeof(McpServerDiagnosticSummary[]))]
internal sealed partial class McpDiagnosticJsonContext : JsonSerializerContext;
