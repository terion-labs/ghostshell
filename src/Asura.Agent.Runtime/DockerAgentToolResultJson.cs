using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.Agent.Runtime;

internal static class DockerAgentToolResultJson
{
    internal const string ContentOrigin = "untrusted_docker";
    private const string SecretRedaction = "[REDACTED SECRET VALUE]";

    public static DockerAgentToolJsonProjection Project(
        AgentDockerReadResult result,
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
            return Rejected("docker_result_too_large", panelId);
        }

        return new DockerAgentToolJsonProjection(
            true,
            SuccessStableCode(result),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Failure(HostError error, PanelInstanceId? panelId = null)
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
            "docker_reference_expired"
            or "docker_filesystem_unavailable"
            or "docker_operation_unavailable"
            or "docker_read_rejected"
            or "docker_result_invalid"
            or "docker_read_failed"
            or "docker_authorization_expired"
            or "docker_audit_unavailable"
            or "docker_action_invalid"
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
                or HostErrorCode.SessionClosed => "docker_operation_unavailable",
            HostErrorCode.DeadlineExceeded => "deadline_exceeded",
            HostErrorCode.Cancelled => "cancelled",
            _ => "docker_read_failed",
        };
    }

    private static int WriteResult(Utf8JsonWriter writer, AgentDockerReadResult result)
    {
        var redactionCount = 0;
        switch (result)
        {
            case AgentDockerReadResult.State state:
                WriteState(writer, state.Value, ref redactionCount);
                break;
            case AgentDockerReadResult.Inspection inspection:
                WriteInspection(writer, inspection.Value, ref redactionCount);
                break;
            case AgentDockerReadResult.Logs logs:
                WriteLogs(writer, logs.Value, ref redactionCount);
                break;
            case AgentDockerReadResult.Files files:
                WriteFiles(writer, files.Value, ref redactionCount);
                break;
            case AgentDockerReadResult.FileStat stat:
                writer.WritePropertyName("entry");
                WriteFileEntry(writer, stat.Value, ref redactionCount);
                break;
            case AgentDockerReadResult.FileText file:
                WriteFileText(writer, file.Value, ref redactionCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }

        return redactionCount;
    }

    private static void WriteState(
        Utf8JsonWriter writer,
        AgentDockerStateSnapshot value,
        ref int redactionCount)
    {
        writer.WriteString("engine_generation", value.EngineGeneration.Value);
        writer.WriteStartObject("engine");
        writer.WriteString("version", Redact(value.Snapshot.Engine.Version, false, ref redactionCount));
        writer.WriteString("operating_system", Redact(value.Snapshot.Engine.OperatingSystem, false, ref redactionCount));
        writer.WriteString("architecture", value.Snapshot.Engine.Architecture);
        writer.WriteString("api_version", value.Snapshot.Engine.ApiVersion);
        writer.WriteEndObject();
        WriteResources(writer, "containers", value.Snapshot.Containers, WriteContainer, ref redactionCount);
        WriteResources(writer, "images", value.Snapshot.Images, WriteImage, ref redactionCount);
        WriteResources(writer, "volumes", value.Snapshot.Volumes, WriteVolume, ref redactionCount);
        WriteResources(writer, "networks", value.Snapshot.Networks, WriteNetwork, ref redactionCount);
        writer.WriteString("captured_at_utc", value.Snapshot.CapturedAtUtc);
        writer.WriteBoolean("truncated", value.Snapshot.IsTruncated);
    }

    private static void WriteInspection(
        Utf8JsonWriter writer,
        DockerInspectionSnapshot value,
        ref int redactionCount)
    {
        writer.WritePropertyName("resource");
        WriteResource(writer, value.Resource, ref redactionCount);
        writer.WriteStartArray("properties");
        foreach (var property in value.Properties)
        {
            writer.WriteStartObject();
            writer.WriteString("name", property.Name);
            writer.WriteString(
                "value",
                Redact(property.Value, IsSecretName(property.Name), ref redactionCount));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", value.IsTruncated);
    }

    private static void WriteLogs(
        Utf8JsonWriter writer,
        DockerContainerLogPage value,
        ref int redactionCount)
    {
        writer.WriteStartArray("lines");
        foreach (var line in value.Lines)
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", line.Timestamp);
            writer.WriteString("message", Redact(line.Message, false, ref redactionCount));
            writer.WriteBoolean("starts_context_block", line.StartsContextBlock);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("has_older", value.HasOlder);
        WriteOptionalString(writer, "oldest_timestamp", value.OldestTimestamp);
        WriteOptionalString(writer, "newest_timestamp", value.NewestTimestamp);
    }

    private static void WriteFiles(
        Utf8JsonWriter writer,
        DockerFilePage value,
        ref int redactionCount)
    {
        writer.WritePropertyName("resource");
        WriteResource(writer, value.Resource, ref redactionCount);
        writer.WriteString("path", Redact(value.Path, IsSecretName(value.Path), ref redactionCount));
        writer.WriteStartArray("entries");
        foreach (var entry in value.Entries)
        {
            WriteFileEntry(writer, entry, ref redactionCount);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", value.IsTruncated);
    }

    private static void WriteFileText(
        Utf8JsonWriter writer,
        AgentDockerTextFileSnapshot value,
        ref int redactionCount)
    {
        writer.WritePropertyName("resource");
        WriteResource(writer, value.Resource, ref redactionCount);
        writer.WriteString("path", Redact(value.Path, IsSecretName(value.Path), ref redactionCount));
        writer.WriteString("text", Redact(value.Text, false, ref redactionCount));
        writer.WriteBoolean("truncated", value.IsTruncated);
    }

    private static void WriteContainer(
        Utf8JsonWriter writer,
        DockerContainerItem value,
        ref int redactionCount)
    {
        WriteResourceProperties(writer, value.Resource, ref redactionCount);
        writer.WriteString("image", Redact(value.Image, false, ref redactionCount));
        writer.WriteString("state", value.State);
        writer.WriteString("status", Redact(value.Status, false, ref redactionCount));
        writer.WriteString("ports", value.Ports);
        writer.WriteString("created", value.Created);
        writer.WriteString("cpu", value.Cpu);
        writer.WriteString("memory", value.Memory);
        writer.WriteString("network_io", value.NetworkIo);
        writer.WriteString("block_io", value.BlockIo);
        WriteOptionalString(
            writer,
            "container_revision",
            value.ControlRevision?.Value);
        WriteOptionalRedacted(writer, "compose_project", value.ComposeProject, ref redactionCount);
        WriteOptionalRedacted(writer, "compose_service", value.ComposeService, ref redactionCount);
    }

    private static void WriteImage(
        Utf8JsonWriter writer,
        DockerImageItem value,
        ref int redactionCount)
    {
        WriteResourceProperties(writer, value.Resource, ref redactionCount);
        writer.WriteString("repository", Redact(value.Repository, false, ref redactionCount));
        writer.WriteString("tag", Redact(value.Tag, false, ref redactionCount));
        writer.WriteString("size", value.Size);
        writer.WriteString("created", value.Created);
    }

    private static void WriteVolume(
        Utf8JsonWriter writer,
        DockerVolumeItem value,
        ref int redactionCount)
    {
        WriteResourceProperties(writer, value.Resource, ref redactionCount);
        writer.WriteString("driver", value.Driver);
        writer.WriteString("scope", value.Scope);
        writer.WriteString("size", value.Size);
        if (value.SizeBytes is { } sizeBytes)
        {
            writer.WriteNumber("size_bytes", sizeBytes);
        }
        else
        {
            writer.WriteNull("size_bytes");
        }
    }

    private static void WriteNetwork(
        Utf8JsonWriter writer,
        DockerNetworkItem value,
        ref int redactionCount)
    {
        WriteResourceProperties(writer, value.Resource, ref redactionCount);
        writer.WriteString("driver", value.Driver);
        writer.WriteString("scope", value.Scope);
        writer.WriteString("created", value.Created);
    }

    private static void WriteResource(
        Utf8JsonWriter writer,
        DockerResourceItem value,
        ref int redactionCount)
    {
        writer.WriteStartObject();
        WriteResourceProperties(writer, value, ref redactionCount);
        writer.WriteEndObject();
    }

    private static void WriteResourceProperties(
        Utf8JsonWriter writer,
        DockerResourceItem value,
        ref int redactionCount)
    {
        writer.WriteString("resource_ref", value.Reference.Value);
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        writer.WriteString(
            "display_name",
            Redact(value.DisplayName, IsSecretName(value.DisplayName), ref redactionCount));
    }

    private static void WriteFileEntry(
        Utf8JsonWriter writer,
        DockerFileEntry value,
        ref int redactionCount)
    {
        writer.WriteStartObject();
        writer.WriteString("name", Redact(value.Name, IsSecretName(value.Name), ref redactionCount));
        writer.WriteString("path", Redact(value.Path, IsSecretName(value.Path), ref redactionCount));
        writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        if (value.Size is { } size)
        {
            writer.WriteNumber("size", size);
        }
        else
        {
            writer.WriteNull("size");
        }

        if (value.ModifiedAt is { } modifiedAt)
        {
            writer.WriteString("modified_at_utc", modifiedAt);
        }
        else
        {
            writer.WriteNull("modified_at_utc");
        }

        writer.WriteEndObject();
    }

    private static void WriteResources<T>(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<T> values,
        WriteResourceItem<T> write,
        ref int redactionCount)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStartObject();
            write(writer, value, ref redactionCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
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

    private static void WriteOptionalRedacted(
        Utf8JsonWriter writer,
        string name,
        string? value,
        ref int redactionCount)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, Redact(value, IsSecretName(name), ref redactionCount));
        }
    }

    private static string Redact(string value, bool force, ref int redactionCount)
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

        var normalized = value.Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
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

    private static string SuccessStableCode(AgentDockerReadResult result) => result switch
    {
        AgentDockerReadResult.State => "docker_state_read",
        AgentDockerReadResult.Inspection => "docker_resource_inspected",
        AgentDockerReadResult.Logs => "docker_logs_read",
        AgentDockerReadResult.Files => "docker_files_listed",
        AgentDockerReadResult.FileStat => "docker_file_inspected",
        AgentDockerReadResult.FileText => "docker_file_read",
        _ => "docker_read_completed",
    };

    private static DockerAgentToolJsonProjection Rejected(
        string stableCode,
        PanelInstanceId? panelId) =>
        new(false, stableCode, AgentToolResultJson.Failure(stableCode, false, panelId));

    private delegate void WriteResourceItem<T>(
        Utf8JsonWriter writer,
        T value,
        ref int redactionCount);
}

internal sealed record DockerAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
