using Asura.Application;

namespace Asura.Browser.Tests;

public sealed class CefAgentWebReaderTests
{
    [Fact]
    public async Task GovernedCefReaderFailsBeforeNativeDispatchWithoutPeerBinding()
    {
        var result = await new CefAgentWebReader().ReadAsync(
            new AgentWebReadRequest(
                "https://example.test/article",
                AgentWebReadFormat.Markdown),
            CancellationToken.None);

        var failure = Assert.IsType<AgentWebToolExecutionResult.Failed>(result);
        Assert.Equal(AgentWebToolErrorCode.DestinationDenied, failure.Code);
    }

    [Fact]
    public async Task MarkdownModeConvertsReadableArticleHtml()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat(
            "Asura renders documentation in an isolated Chromium page.",
            20));
        var json = $$"""
            {
              "title": "Reader guide",
              "html": "<div><h1>Reader guide</h1><p>{{paragraph}}</p><script>secret()</script></div>",
              "links": [
                "https://docs.example.test/guide",
                "https://docs.example.test/reference",
                "https://docs.example.test/guide"
              ],
              "truncated": false
            }
            """;

        var result = await new CefAgentWebReader().ConvertAsync(
            new BrowserAddress(new Uri("https://docs.example.test/guide")),
            AgentWebReadFormat.Markdown,
            json,
            CancellationToken.None);

        var succeeded = Assert.IsType<AgentWebToolExecutionResult.Succeeded>(result);
        var read = Assert.IsType<AgentWebReadResult>(succeeded.Result);
        Assert.Equal(AgentWebReadFormat.Markdown, read.Format);
        Assert.Equal("Reader guide", read.Title);
        Assert.Contains(
            "Asura renders documentation",
            read.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret()", read.Content, StringComparison.Ordinal);
        Assert.Equal(
            [
                "https://docs.example.test/guide",
                "https://docs.example.test/reference",
            ],
            read.Links,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task RenderedHtmlModeReturnsExplicitBoundedDom()
    {
        const string json = """
            {
              "title": "Dynamic page",
              "html": "<html><body><main>Rendered client content</main></body></html>",
              "links": ["https://app.example.test/account"],
              "truncated": true
            }
            """;

        var result = await new CefAgentWebReader().ConvertAsync(
            new BrowserAddress(new Uri("https://app.example.test/")),
            AgentWebReadFormat.RenderedHtml,
            json,
            CancellationToken.None);

        var succeeded = Assert.IsType<AgentWebToolExecutionResult.Succeeded>(result);
        var read = Assert.IsType<AgentWebReadResult>(succeeded.Result);
        Assert.Equal("Dynamic page", read.Title);
        Assert.Contains("Rendered client content", read.Content, StringComparison.Ordinal);
        Assert.Equal("https://app.example.test/account", Assert.Single(read.Links));
        Assert.True(read.Truncated);
    }
}
