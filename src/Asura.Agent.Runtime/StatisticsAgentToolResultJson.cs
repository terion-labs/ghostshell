using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class StatisticsAgentToolResultJson
{
    internal const string ContentOrigin =
        "untrusted_local_system_statistics";

    public static StatisticsAgentToolJsonProjection Project(
        AgentStatisticsReadResult result,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!IsValid(result))
        {
            return Rejected("statistics_result_invalid", panelId);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        writer.WriteString("content_origin", ContentOrigin);
        writer.WriteString("captured_at_utc", result.CapturedAtUtc);
        writer.WriteNumber(
            "host_uptime_seconds",
            result.HostUptime.TotalSeconds);
        writer.WriteNumber(
            "logical_processor_count",
            result.LogicalProcessorCount);
        writer.WriteNumber(
            "enumerated_process_count",
            result.EnumeratedProcessCount);
        writer.WriteNumber(
            "observed_process_count",
            result.ObservedProcessCount);
        WriteNullableNumber(
            writer,
            "observed_cpu_percent",
            result.ObservedCpuPercent);
        writer.WriteNumber(
            "observed_working_set_bytes",
            result.ObservedWorkingSetBytes);
        WriteNullableNumber(
            writer,
            "network_received_bytes_per_second",
            result.NetworkReceivedBytesPerSecond);
        WriteNullableNumber(
            writer,
            "network_sent_bytes_per_second",
            result.NetworkSentBytesPerSecond);
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount
            > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("statistics_result_too_large", panelId);
        }

        return new StatisticsAgentToolJsonProjection(
            true,
            "statistics_read",
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
        if (string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal)
            || error.StableCode is
                "statistics_unavailable"
                or "statistics_capture_failed"
                or "statistics_result_invalid")
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.InvalidRequest => "target_changed",
            HostErrorCode.NotFound => "target_changed",
            HostErrorCode.RevisionConflict => "target_changed",
            HostErrorCode.UnsupportedProtocol => "statistics_unavailable",
            HostErrorCode.CapabilityNotSupported => "statistics_unavailable",
            HostErrorCode.SessionClosed => "statistics_unavailable",
            HostErrorCode.DeadlineExceeded => "deadline_exceeded",
            HostErrorCode.Cancelled => error.StableCode is
                "authority_revoked" or "caller_cancelled"
                    ? error.StableCode
                    : "cancelled",
            _ => "statistics_capture_failed",
        };
    }

    private static bool IsValid(AgentStatisticsReadResult result) =>
        result.CapturedAtUtc.Offset == TimeSpan.Zero
        && result.HostUptime >= TimeSpan.Zero
        && double.IsFinite(result.HostUptime.TotalSeconds)
        && result.LogicalProcessorCount >= 1
        && result.EnumeratedProcessCount >= 0
        && result.ObservedProcessCount >= 0
        && result.ObservedProcessCount <= result.EnumeratedProcessCount
        && IsPercentage(result.ObservedCpuPercent)
        && result.ObservedWorkingSetBytes >= 0
        && IsRate(result.NetworkReceivedBytesPerSecond)
        && IsRate(result.NetworkSentBytesPerSecond);

    private static bool IsPercentage(double? value) =>
        value is null
        || double.IsFinite(value.Value)
        && value.Value is >= 0 and <= 100;

    private static bool IsRate(double? value) =>
        value is null
        || double.IsFinite(value.Value)
        && value.Value >= 0;

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string name,
        double? value)
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

    private static StatisticsAgentToolJsonProjection Rejected(
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

internal sealed record StatisticsAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
