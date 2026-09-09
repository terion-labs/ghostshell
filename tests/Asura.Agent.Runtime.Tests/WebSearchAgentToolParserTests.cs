using System.Runtime.CompilerServices;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class WebSearchAgentToolParserTests
{
    [Theory]
    [InlineData("{\"query\":\"CEF offscreen browser\"}", "CEF offscreen browser", 10)]
    [InlineData("{\"query\":\"  Mozilla Readability  \",\"result_count\":3}", "Mozilla Readability", 3)]
    public async Task ParsesBoundedSearchRequest(
        string arguments,
        string expectedQuery,
        int expectedCount)
    {
        var proposal = await ProposalAsync(BuiltInAgentTools.WebSearch, arguments);

        var parsed = Assert.IsType<WebSearchAgentIntentResult.Parsed>(
            WebSearchAgentToolParser.Parse(proposal));

        Assert.Equal(expectedQuery, parsed.Intent.Query);
        Assert.Equal(expectedCount, parsed.Intent.ResultCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("{\"query\":\"line\\nfeed\"}")]
    [InlineData("{\"query\":\"cef\",\"result_count\":0}")]
    [InlineData("{\"query\":\"cef\",\"result_count\":11}")]
    [InlineData("{\"query\":\"cef\",\"extra\":true}")]
    public async Task RejectsMissingUnsafeOrUnboundedArguments(string arguments)
    {
        var proposal = await ProposalAsync(BuiltInAgentTools.WebSearch, arguments);

        var rejected = Assert.IsType<WebSearchAgentIntentResult.Rejected>(
            WebSearchAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task RejectsAnotherToolName()
    {
        var proposal = await ProposalAsync("web.provider_extension", "{\"query\":\"cef\"}");

        var rejected = Assert.IsType<WebSearchAgentIntentResult.Rejected>(
            WebSearchAgentToolParser.Parse(proposal));

        Assert.Equal("unknown_tool", rejected.StableCode);
    }

    [Fact]
    public void ToolSchemaIsClosedAndLabelsReturnedContentAsUntrusted()
    {
        var tool = Assert.Single(WebSearchAgentToolSet.Tools);

        Assert.Equal(BuiltInAgentTools.WebSearch, tool.Name);
        Assert.False(
            tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("untrusted web content", tool.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http.fetch", "{\"url\":\"https://api.example.test/v1\"}", typeof(AgentHttpFetchRequest))]
    [InlineData("http.fetch", "{\"url\":\"https://api.example.test/v1\",\"method\":\"HEAD\"}", typeof(AgentHttpFetchRequest))]
    [InlineData("web.read", "{\"url\":\"https://docs.example.test/guide\"}", typeof(AgentWebReadRequest))]
    [InlineData("web.read", "{\"url\":\"https://docs.example.test/guide\",\"format\":\"rendered_html\"}", typeof(AgentWebReadRequest))]
    public async Task ParsesClosedFetchAndReadRequests(
        string toolName,
        string arguments,
        Type expectedType)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var parsed = Assert.IsType<WebAgentIntentResult.Parsed>(
            WebAgentToolParser.Parse(proposal));

        Assert.IsType(expectedType, parsed.Request);
    }

    [Theory]
    [InlineData("http.fetch", "{\"url\":\"file:///tmp/private\"}")]
    [InlineData("http.fetch", "{\"url\":\"https://example.test\",\"method\":\"POST\"}")]
    [InlineData("web.read", "{\"url\":\"https://example.test\",\"format\":\"text\"}")]
    [InlineData("web.read", "{\"url\":\"https://example.test\",\"extra\":true}")]
    public async Task RejectsUnsafeFetchAndReadRequests(
        string toolName,
        string arguments)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var rejected = Assert.IsType<WebAgentIntentResult.Rejected>(
            WebAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void WebToolManifestIncludesAllThreeClosedSchemas()
    {
        Assert.Equal(
            [BuiltInAgentTools.HttpFetch, BuiltInAgentTools.WebRead, BuiltInAgentTools.WebSearch],
            WebAgentToolSet.Tools.Select(tool => tool.Name),
            StringComparer.Ordinal);
        Assert.All(
            WebAgentToolSet.Tools,
            tool => Assert.False(
                tool.InputSchema.GetProperty("additionalProperties").GetBoolean()));
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("web-search-run"));
        var result = await session.RunTurnAsync(
            "Search the web.",
            [new AgentToolDefinition(
                name,
                "Test tool.",
                "{\"type\":\"object\",\"additionalProperties\":true}"u8.ToArray())],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        return Assert.Single(result.ToolProposals);
    }

    private sealed class ToolProvider(string name, string arguments) : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.ToolCallStarted(
                0,
                "web-search-call",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}
