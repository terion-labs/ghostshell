using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class WebAgentToolResultJson
{
    public static WebAgentToolJsonProjection Project(
        AgentWebToolRequest request,
        AgentWebToolResult result)
    {
        if (request is AgentWebSearchRequest searchRequest
            && result is AgentWebSearchResult searchResult)
        {
            var projected = WebSearchAgentToolResultJson.Project(
                searchRequest,
                searchResult);
            return new WebAgentToolJsonProjection(
                projected.IsSuccess,
                projected.StableCode,
                projected.Json);
        }

        if (request is AgentWebReadRequest readRequest
            && result is AgentWebReadResult readResult)
        {
            return ProjectRead(readRequest, readResult);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("content_origin", WebSearchAgentToolResultJson.ContentOrigin);
        switch (request, result)
        {
            case (AgentHttpFetchRequest fetch, AgentHttpFetchResult fetched):
                writer.WriteString("method", fetch.Method.ToString().ToUpperInvariant());
                writer.WriteString("final_url", fetched.FinalUrl);
                writer.WriteNumber("status", fetched.StatusCode);
                writer.WriteString("media_type", fetched.MediaType);
                writer.WriteString("content", fetched.Content);
                break;
            default:
                return Rejected("web_result_type_mismatch");
        }

        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Rejected("web_result_too_large");
        }

        return new WebAgentToolJsonProjection(
            true,
            CompletedCode(request.ToolName),
            Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static WebAgentToolJsonProjection ProjectRead(
        AgentWebReadRequest request,
        AgentWebReadResult result)
    {
        var full = SerializeRead(
            request,
            result,
            result.Content,
            result.Links.Count,
            result.Truncated);
        var maximumBytes = AgentKernelLimits.Default.MaximumToolResultBytes;
        if (full.ByteCount <= maximumBytes)
        {
            return Succeeded(request, full.Json);
        }

        var linkCount = result.Links.Count;
        var linksOnly = SerializeRead(
            request,
            result,
            string.Empty,
            linkCount,
            truncated: true);
        if (linksOnly.ByteCount > maximumBytes)
        {
            var low = 0;
            var high = linkCount - 1;
            linkCount = 0;
            while (low <= high)
            {
                var candidateCount = low + ((high - low) / 2);
                var candidate = SerializeRead(
                    request,
                    result,
                    string.Empty,
                    candidateCount,
                    truncated: true);
                if (candidate.ByteCount <= maximumBytes)
                {
                    linkCount = candidateCount;
                    low = candidateCount + 1;
                }
                else
                {
                    high = candidateCount - 1;
                }
            }
        }

        var runeEnds = RuneEndIndices(result.Content);
        WebAgentToolSerializedJson? best = null;
        var contentLow = 0;
        var contentHigh = runeEnds.Count - 1;
        while (contentLow <= contentHigh)
        {
            var candidateRunes = contentLow + ((contentHigh - contentLow) / 2);
            var candidateContent = result.Content[..runeEnds[candidateRunes]];
            var candidate = SerializeRead(
                request,
                result,
                candidateContent,
                linkCount,
                truncated: true);
            if (candidate.ByteCount <= maximumBytes)
            {
                best = candidate;
                contentLow = candidateRunes + 1;
            }
            else
            {
                contentHigh = candidateRunes - 1;
            }
        }

        var bounded = best ?? SerializeRead(
            request,
            result,
            string.Empty,
            linkCount,
            truncated: true);
        return bounded.ByteCount <= maximumBytes
            ? Succeeded(request, bounded.Json)
            : Rejected("web_result_too_large");
    }

    private static WebAgentToolSerializedJson SerializeRead(
        AgentWebReadRequest request,
        AgentWebReadResult result,
        string content,
        int linkCount,
        bool truncated)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        writer.WriteString("content_origin", WebSearchAgentToolResultJson.ContentOrigin);
        writer.WriteString("final_url", result.FinalUrl);
        writer.WriteString("title", result.Title);
        writer.WriteString(
            "format",
            request.Format is AgentWebReadFormat.Markdown
                ? "markdown"
                : "rendered_html");
        writer.WriteString("content", content);
        writer.WriteStartArray("links");
        for (var index = 0; index < linkCount; index++)
        {
            writer.WriteStringValue(result.Links[index]);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", truncated);
        writer.WriteEndObject();
        writer.Flush();
        return new WebAgentToolSerializedJson(
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            buffer.WrittenCount);
    }

    private static List<int> RuneEndIndices(string value)
    {
        var indices = new List<int> { 0 };
        var index = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            index += rune.Utf16SequenceLength;
            indices.Add(index);
        }

        return indices;
    }

    private static WebAgentToolJsonProjection Succeeded(
        AgentWebReadRequest request,
        string json) =>
        new(true, CompletedCode(request.ToolName), json);

    public static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (IsProviderStableCode(error.StableCode)
            || string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal))
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.DeadlineExceeded => "web_timed_out",
            HostErrorCode.Cancelled => "web_cancelled",
            HostErrorCode.InvalidRequest or HostErrorCode.RevisionConflict => "target_changed",
            _ => "web_failed",
        };
    }

    public static string Failure(HostError error) =>
        AgentToolResultJson.Failure(ProviderStableCode(error), error.Retryable);

    public static string CompletedCode(string toolName) => toolName switch
    {
        BuiltInAgentTools.HttpFetch => "http_fetch_completed",
        BuiltInAgentTools.WebRead => "web_read_completed",
        BuiltInAgentTools.WebSearch => "web_search_completed",
        _ => "web_completed",
    };

    private static bool IsProviderStableCode(string stableCode)
    {
        var prefixLength = stableCode.StartsWith(
            "http_fetch_",
            StringComparison.Ordinal)
            ? "http_fetch_".Length
            : stableCode.StartsWith("web_read_", StringComparison.Ordinal)
                ? "web_read_".Length
                : stableCode.StartsWith("web_search_", StringComparison.Ordinal)
                    ? "web_search_".Length
                    : 0;
        if (prefixLength == 0)
        {
            return false;
        }

        return stableCode[prefixLength..] is
            "invalid_url"
            or "destination_denied"
            or "dns_failed"
            or "redirect_limit"
            or "timed_out"
            or "body_too_large"
            or "unsupported_content_type"
            or "load_failed"
            or "render_process_failed"
            or "extraction_failed"
            or "converter_failed"
            or "interstitial"
            or "cancelled"
            or "unavailable";
    }

    private static WebAgentToolJsonProjection Rejected(string stableCode) =>
        new(false, stableCode, AgentToolResultJson.Failure(stableCode, retryable: false));
}

internal sealed record WebAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);

internal sealed record WebAgentToolSerializedJson(string Json, int ByteCount);
