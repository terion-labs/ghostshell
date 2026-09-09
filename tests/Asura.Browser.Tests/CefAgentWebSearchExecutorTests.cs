using Asura.Application;

namespace Asura.Browser.Tests;

public sealed class CefAgentWebSearchExecutorTests
{
    [Fact]
    public async Task GovernedCefSearchFailsBeforeNativeDispatchWithoutPeerBinding()
    {
        var result = await new CefAgentWebSearchExecutor().SearchAsync(
            new AgentWebSearchRequest("bounded query", 3),
            CancellationToken.None);

        var failure = Assert.IsType<AgentWebSearchExecutionResult.Failed>(result);
        Assert.Equal(AgentWebSearchErrorCode.NavigationDenied, failure.Code);
    }

    [Fact]
    public void SearchAddressUsesOnlyFixedGoogleParametersAndEncodedQuery()
    {
        var address = CefAgentWebSearchExecutor.CreateSearchAddress(
            new AgentWebSearchRequest("cef search & safety", 4));

        Assert.Equal(Uri.UriSchemeHttps, address.Value.Scheme);
        Assert.Equal("www.google.com", address.Value.IdnHost);
        Assert.Equal("/search", address.Value.AbsolutePath);
        Assert.Equal(
            "?q=cef%20search%20%26%20safety&num=4&hl=en&pws=0",
            address.Value.Query);
        Assert.Empty(address.Value.UserInfo);
        Assert.Empty(address.Value.Fragment);
    }

    [Fact]
    public void ExtractionValidatesAndDeduplicatesSemanticResults()
    {
        const string json = """
            {
              "title": "Search results",
              "pageText": "Google navigation Useful result Sign in",
              "results": [
                {
                  "url": "https://example.test/docs",
                  "title": "Useful result",
                  "desc": "Useful result Example docs"
                },
                {
                  "url": "https://example.test/docs",
                  "title": "Duplicate",
                  "desc": "Duplicate"
                },
                {
                  "url": "javascript:alert(1)",
                  "title": "Invalid destination",
                  "desc": "Invalid destination"
                },
                {
                  "url": "https://example.test/guide",
                  "title": "Second useful result",
                  "desc": "Second useful result"
                }
              ],
              "truncated": false
            }
            """;

        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri("https://www.google.com/search?q=cef")),
            json);

        var succeeded = Assert.IsType<AgentWebSearchExecutionResult.Succeeded>(
            result);
        Assert.Equal("Search results", succeeded.Result.Title);
        Assert.Collection(
            succeeded.Result.Entries,
            entry =>
            {
                Assert.Equal("https://example.test/docs", entry.Url);
                Assert.Equal("Useful result", entry.Title);
                Assert.Equal("Useful result Example docs", entry.Description);
            },
            entry =>
            {
                Assert.Equal("https://example.test/guide", entry.Url);
                Assert.Equal("Second useful result", entry.Title);
                Assert.Equal("Second useful result", entry.Description);
            });
    }

    [Theory]
    [InlineData("https://www.google.com/sorry/index", "Search", "")]
    [InlineData("https://consent.google.com/", "Google", "Before you continue to Google")]
    [InlineData("https://www.google.com/search?q=cef", "Unusual traffic", "")]
    public void GoogleInterstitialIsNotReturnedAsSearchContent(
        string address,
        string title,
        string text)
    {
        var json = $$"""
            {
              "title": "{{title}}",
              "pageText": "{{text}}",
              "results": [],
              "truncated": false
            }
            """;

        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri(address)),
            json);

        var failed = Assert.IsType<AgentWebSearchExecutionResult.Failed>(result);
        Assert.Equal(AgentWebSearchErrorCode.Interstitial, failed.Code);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"title\":\"Search\",\"pageText\":\"x\",\"results\":[],\"truncated\":false}")]
    public void MalformedOrMissingRsoExtractionFailsClosed(string json)
    {
        var result = CefAgentWebSearchExecutor.ParseExtraction(
            new BrowserAddress(new Uri("https://www.google.com/search?q=cef")),
            json);

        var failed = Assert.IsType<AgentWebSearchExecutionResult.Failed>(result);
        Assert.Equal(AgentWebSearchErrorCode.ExtractionFailed, failed.Code);
    }

    [Theory]
    [InlineData("{\"method\":\"DOM.childNodeInserted\",\"params\":{}}", true)]
    [InlineData("{\"method\":\"DOM.characterDataModified\",\"params\":{}}", true)]
    [InlineData("{\"method\":\"DOM.documentUpdated\",\"params\":{}}", true)]
    [InlineData("{\"method\":\"Network.requestWillBeSent\",\"params\":{}}", false)]
    [InlineData("not-json", false)]
    public void DomActivityDetectionUsesOnlyCdpDomEvents(
        string json,
        bool expected)
    {
        Assert.Equal(expected, CefBrowserDomActivityTracker.IsDomActivityMessage(json));
    }

}
