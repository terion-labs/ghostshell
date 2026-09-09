using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class ProcessAgentToolResultJson
{
    internal const string ContentOrigin =
        "untrusted_local_process_metadata";

    public static ProcessAgentToolJsonProjection Project(
        AgentProcessListResult result,
        ProcessAgentIntent intent,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(intent);
        if (!IsValid(result, intent))
        {
            return Rejected("processes_result_invalid", panelId);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        writer.WriteString("content_origin", ContentOrigin);
        writer.WriteString("captured_at_utc", result.CapturedAtUtc);
        writer.WriteString("sort", intent.SortName);
        writer.WriteNumber("limit", intent.Limit);
        writer.WriteNumber("offset", intent.Offset);
        if (intent.NameContains is { } nameContains)
        {
            writer.WriteString("name_contains", nameContains);
        }

        if (intent.ProcessId is { } processId)
        {
            writer.WriteNumber("pid", processId);
        }

        writer.WriteNumber("returned", result.ReturnedCount);
        writer.WriteNumber(
            "enumerated_process_count",
            result.EnumeratedProcessCount);
        writer.WriteNumber(
            "observed_process_count",
            result.ObservedProcessCount);
        writer.WriteNumber(
            "matching_process_count",
            result.MatchingProcessCount);
        writer.WriteBoolean("truncated", result.IsTruncated);
        writer.WriteNumber(
            "redacted_name_count",
            result.RedactedNameCount);
        writer.WriteNumber(
            "truncated_name_count",
            result.TruncatedNameCount);
        writer.WriteStartArray("processes");
        foreach (var process in result.Processes)
        {
            WriteProcess(writer, process);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount
            > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("processes_result_too_large", panelId);
        }

        return new ProcessAgentToolJsonProjection(
            true,
            "processes_listed",
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Failure(
        HostError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var stableCode = ProviderStableCode(error);
        return AgentToolResultJson.Failure(
            stableCode,
            error.Retryable,
            panelId);
    }

    internal static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal)
            || error.StableCode is
                "processes_unavailable"
                or "processes_capture_failed"
                or "processes_result_invalid")
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.InvalidRequest => "target_changed",
            HostErrorCode.NotFound => "target_changed",
            HostErrorCode.RevisionConflict => "target_changed",
            HostErrorCode.UnsupportedProtocol => "processes_unavailable",
            HostErrorCode.CapabilityNotSupported => "processes_unavailable",
            HostErrorCode.SessionClosed => "processes_unavailable",
            HostErrorCode.DeadlineExceeded => "deadline_exceeded",
            HostErrorCode.Cancelled => error.StableCode is
                "authority_revoked" or "caller_cancelled"
                    ? error.StableCode
                    : "cancelled",
            _ => "processes_capture_failed",
        };
    }

    private static bool IsValid(
        AgentProcessListResult result,
        ProcessAgentIntent intent)
    {
        if (result.CapturedAtUtc.Offset != TimeSpan.Zero
            || result.Processes is null
            || result.Processes.Count > ProcessAgentToolSet.MaximumRows
            || result.Processes.Count > intent.Limit
            || result.ReturnedCount != result.Processes.Count
            || result.EnumeratedProcessCount < 0
            || result.ObservedProcessCount < 0
            || result.ObservedProcessCount > result.EnumeratedProcessCount
            || result.MatchingProcessCount < 0
            || result.MatchingProcessCount > result.ObservedProcessCount
            || result.ReturnedCount > result.MatchingProcessCount
            || result.RedactedNameCount < 0
            || result.TruncatedNameCount < 0
            || result.RedactedNameCount > result.ReturnedCount
            || result.TruncatedNameCount > result.ReturnedCount)
        {
            return false;
        }

        var redactedNames = 0;
        var truncatedNames = 0;
        var asuraRows = 0;
        foreach (var process in result.Processes)
        {
            if (!IsValid(process))
            {
                return false;
            }

            redactedNames += process.Name.Redacted ? 1 : 0;
            truncatedNames += process.Name.Truncated ? 1 : 0;
            asuraRows += process.IsAsura ? 1 : 0;
        }

        return redactedNames == result.RedactedNameCount
            && truncatedNames == result.TruncatedNameCount
            && asuraRows <= 1;
    }

    private static bool IsValid(AgentProcessListEntry process) =>
        process.ProcessId >= 0
        && !string.IsNullOrWhiteSpace(process.Name.Text)
        && !process.Name.Text.Any(character =>
            char.IsControl(character)
            || char.GetUnicodeCategory(character) is
                System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator)
        && !AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(
            process.Name.Text)
        && (process.ProcessorUsagePercent is null
            || double.IsFinite(process.ProcessorUsagePercent.Value)
            && process.ProcessorUsagePercent.Value is >= 0 and <= 100)
        && process.WorkingSetBytes is null or >= 0
        && (process.StartedAtUtc is null
            || process.StartedAtUtc.Value.Offset == TimeSpan.Zero);

    private static void WriteProcess(
        Utf8JsonWriter writer,
        AgentProcessListEntry process)
    {
        writer.WriteStartObject();
        writer.WriteNumber("pid", process.ProcessId);
        writer.WriteString("name", process.Name.Text);
        if (process.ProcessorUsagePercent is { } processorUsage)
        {
            writer.WriteNumber("cpu_percent", processorUsage);
        }
        else
        {
            writer.WriteNull("cpu_percent");
        }

        if (process.WorkingSetBytes is { } workingSetBytes)
        {
            writer.WriteNumber("working_set_bytes", workingSetBytes);
        }
        else
        {
            writer.WriteNull("working_set_bytes");
        }

        if (process.StartedAtUtc is { } startedAtUtc)
        {
            writer.WriteString("started_at_utc", startedAtUtc);
        }
        else
        {
            writer.WriteNull("started_at_utc");
        }

        writer.WriteBoolean("is_asura", process.IsAsura);
        writer.WriteBoolean("name_redacted", process.Name.Redacted);
        writer.WriteBoolean("name_truncated", process.Name.Truncated);
        writer.WriteEndObject();
    }

    private static ProcessAgentToolJsonProjection Rejected(
        string stableCode,
        PanelInstanceId? panelId) =>
        new(
            false,
            stableCode,
            AgentToolResultJson.Failure(
                stableCode,
                retryable: false,
                panelId));
}

internal sealed record ProcessAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
