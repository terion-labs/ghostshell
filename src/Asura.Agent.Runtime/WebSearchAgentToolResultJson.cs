using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class WebSearchAgentToolResultJson
{
    internal const string ContentOrigin = "untrusted_web";

    public static WebSearchAgentToolJsonProjection Project(
        AgentWebSearchRequest request,
        AgentWebSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("content_origin", ContentOrigin);
        writer.WriteString("provider", "google");
        writer.WriteString("query", request.Query);
        writer.WriteString("final_url", result.FinalUrl);
        writer.WriteString("title", result.Title);
        writer.WriteStartArray("results");
        foreach (var entry in result.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("title", entry.Title);
            writer.WriteString("url", entry.Url);
            writer.WriteString("desc", entry.Description);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", result.Truncated);
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("web_search_result_too_large");
        }

        return new WebSearchAgentToolJsonProjection(
            true,
            "web_search_completed",
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Failure(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AgentToolResultJson.Failure(
            ProviderStableCode(error),
            error.Retryable);
    }

    public static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.StableCode is
            "web_search_unavailable"
            or "web_search_navigation_denied"
            or "web_search_load_failed"
            or "web_search_interstitial"
            or "web_search_extraction_failed"
            or "web_search_timed_out"
            or "web_search_cancelled"
            or AgentActionFailureCodes.CompletionAuditUnavailable)
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.DeadlineExceeded => "web_search_timed_out",
            HostErrorCode.Cancelled => "web_search_cancelled",
            HostErrorCode.InvalidRequest or HostErrorCode.RevisionConflict =>
                "target_changed",
            _ => "web_search_failed",
        };
    }

    private static WebSearchAgentToolJsonProjection Rejected(string stableCode) =>
        new(
            false,
            stableCode,
            AgentToolResultJson.Failure(stableCode, retryable: false));
}

internal sealed record WebSearchAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);
