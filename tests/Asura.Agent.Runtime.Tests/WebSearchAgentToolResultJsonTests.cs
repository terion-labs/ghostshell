using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class WebSearchAgentToolResultJsonTests
{
    [Fact]
    public void SuccessIsLabeledUntrustedAndReturnsStructuredResults()
    {
        var request = new AgentWebSearchRequest("cef offscreen", 2);
        var result = new AgentWebSearchResult(
            "https://www.google.com/search?q=cef%20offscreen",
            "cef offscreen - Google Search",
            [
                new AgentWebSearchEntry(
                    "https://example.test/first",
                    "First result",
                    "First result description"),
                new AgentWebSearchEntry(
                    "https://example.test/second",
                    "Second result",
                    "Second result description"),
            ],
            truncated: true);

        var projection = WebSearchAgentToolResultJson.Project(request, result);

        Assert.True(projection.IsSuccess);
        Assert.Equal("web_search_completed", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("untrusted_web", root.GetProperty("content_origin").GetString());
        Assert.Equal("google", root.GetProperty("provider").GetString());
        Assert.Equal(request.Query, root.GetProperty("query").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        var results = root.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        var resultEntries = results.EnumerateArray().ToArray();
        Assert.Equal(2, resultEntries.Length);
        var resultEntry = resultEntries[0];
        Assert.Equal("First result", resultEntry.GetProperty("title").GetString());
        Assert.Equal(
            "https://example.test/first",
            resultEntry.GetProperty("url").GetString());
        Assert.Equal(
            "First result description",
            resultEntry.GetProperty("desc").GetString());
        Assert.Equal(
            "https://example.test/second",
            resultEntries[1].GetProperty("url").GetString());
        Assert.False(root.TryGetProperty("content", out _));
        Assert.False(root.TryGetProperty("text", out _));
    }

    [Theory]
    [InlineData(HostErrorCode.DeadlineExceeded, null, "web_search_timed_out")]
    [InlineData(HostErrorCode.Cancelled, null, "web_search_cancelled")]
    [InlineData(HostErrorCode.InvalidRequest, null, "target_changed")]
    [InlineData(HostErrorCode.EngineFailed, "web_search_interstitial", "web_search_interstitial")]
    [InlineData(HostErrorCode.EngineFailed, "engine-secret", "web_search_failed")]
    public void FailureUsesOnlyStablePublicCodes(
        HostErrorCode code,
        string? stableCode,
        string expected)
    {
        var error = stableCode is null
            ? HostError.Create(code, "engine-secret")
            : new HostError(code, stableCode, "engine-secret");

        var json = WebSearchAgentToolResultJson.Failure(error);

        Assert.DoesNotContain("engine-secret", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public void HttpFetchProjectionIsBoundedAndUntrusted()
    {
        var request = new AgentHttpFetchRequest("https://api.example.test/v1");
        var result = new AgentHttpFetchResult(
            request.Address.AbsoluteUri,
            200,
            "application/json",
            "{\"ok\":true}");

        var projection = WebAgentToolResultJson.Project(request, result);

        Assert.True(projection.IsSuccess);
        Assert.Equal("http_fetch_completed", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        Assert.Equal(
            "untrusted_web",
            document.RootElement.GetProperty("content_origin").GetString());
        Assert.Equal(200, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public void WebReadProjectionDeclaresRequestedFormat()
    {
        var request = new AgentWebReadRequest(
            "https://docs.example.test/guide",
            AgentWebReadFormat.RenderedHtml);
        var result = new AgentWebReadResult(
            request.Address.AbsoluteUri,
            "Guide",
            request.Format,
            "<main>Guide</main>",
            [
                "https://docs.example.test/guide",
                "https://docs.example.test/reference",
            ],
            truncated: false);

        var projection = WebAgentToolResultJson.Project(request, result);

        using var document = JsonDocument.Parse(projection.Json);
        Assert.Equal(
            "rendered_html",
            document.RootElement.GetProperty("format").GetString());
        Assert.Equal("<main>Guide</main>", document.RootElement.GetProperty("content").GetString());
        Assert.Equal(
            [
                "https://docs.example.test/guide",
                "https://docs.example.test/reference",
            ],
            document.RootElement.GetProperty("links")
                .EnumerateArray()
                .Select(link => link.GetString()),
            StringComparer.Ordinal);
    }

    [Fact]
    public void WebReadProjectionPreservesLinksWhenContentNeedsTruncation()
    {
        var request = new AgentWebReadRequest("https://docs.example.test/guide");
        var links = Enumerable.Range(0, 100)
            .Select(index => $"https://docs.example.test/page/{index}")
            .ToArray();
        var result = new AgentWebReadResult(
            request.Address.AbsoluteUri,
            "Guide",
            request.Format,
            new string('x', AgentWebReadResult.MaximumContentBytes),
            links,
            truncated: false);

        var projection = WebAgentToolResultJson.Project(request, result);

        Assert.True(projection.IsSuccess);
        Assert.True(
            Encoding.UTF8.GetByteCount(projection.Json)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            links,
            root.GetProperty("links")
                .EnumerateArray()
                .Select(link => link.GetString()),
            StringComparer.Ordinal);
        Assert.NotEmpty(root.GetProperty("content").GetString()!);
    }

    [Fact]
    public void WebFailureDoesNotExposeAnUnknownPrefixedEngineCode()
    {
        var error = new HostError(
            HostErrorCode.EngineFailed,
            "web_read_internal_detail",
            "private engine detail");

        var stableCode = WebAgentToolResultJson.ProviderStableCode(error);

        Assert.Equal("web_failed", stableCode);
    }
}
