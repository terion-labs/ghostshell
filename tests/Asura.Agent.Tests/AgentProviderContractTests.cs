using Asura.Agent;

namespace Asura.Agent.Tests;

public sealed class AgentProviderContractTests
{
    [Fact]
    public void ProviderNamePreservesSafeInternalNames()
    {
        var tool = new AgentToolDefinition(
            "mcp_read-file_42",
            "Read a bounded file.",
            """{"type":"object"}"""u8.ToArray());

        Assert.Equal(tool.Name, tool.ProviderName);
    }

    [Fact]
    public void ProviderNameDeterministicallyAliasesInternalOperationNames()
    {
        var first = new AgentToolDefinition(
            "terminal.read_screen",
            "Read a bounded terminal snapshot.",
            """{"type":"object"}"""u8.ToArray());
        var repeated = new AgentToolDefinition(
            "terminal.read_screen",
            "Read a bounded terminal snapshot.",
            """{"type":"object"}"""u8.ToArray());
        var different = new AgentToolDefinition(
            "terminal.send_text",
            "Send bounded terminal input.",
            """{"type":"object"}"""u8.ToArray());

        Assert.Equal(first.ProviderName, repeated.ProviderName);
        Assert.NotEqual(first.Name, first.ProviderName, StringComparer.Ordinal);
        Assert.NotEqual(first.ProviderName, different.ProviderName, StringComparer.Ordinal);
        Assert.Matches(
            "^[A-Za-z0-9_-]{1,64}$",
            first.ProviderName);
    }

    [Fact]
    public void ToolTextRejectsUnpairedUtf16Surrogates()
    {
        var unpairedHighSurrogate = new string('\uD800', 1);
        var unpairedLowSurrogate = new string('\uDC00', 1);

        Assert.Throws<ArgumentException>(() => new AgentToolDefinition(
            $"terminal.{unpairedHighSurrogate}",
            "Read a terminal snapshot.",
            """{"type":"object"}"""u8.ToArray()));
        Assert.Throws<ArgumentException>(() => new AgentToolDefinition(
            $"terminal.{unpairedLowSurrogate}",
            "Read a terminal snapshot.",
            """{"type":"object"}"""u8.ToArray()));
        Assert.Throws<ArgumentException>(() => new AgentToolDefinition(
            "terminal.read_screen",
            $"Read a terminal {unpairedHighSurrogate} snapshot.",
            """{"type":"object"}"""u8.ToArray()));

        var validSupplementaryScalar = new AgentToolDefinition(
            "terminal.\U0001F47B",
            "Read a terminal snapshot.",
            """{"type":"object"}"""u8.ToArray());
        Assert.Matches(
            "^[A-Za-z0-9_-]{1,64}$",
            validSupplementaryScalar.ProviderName);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"string\"}")]
    [InlineData("{\"type\":\"object\",\"type\":\"object\"}")]
    [InlineData("{\"type\":\"object\",}")]
    public void ToolSchemaMustBeAnUnambiguousObjectSchema(string schema)
    {
        Assert.Throws<ArgumentException>(
            () => new AgentToolDefinition(
                "terminal.read_screen",
                "Read a bounded terminal snapshot.",
                System.Text.Encoding.UTF8.GetBytes(schema)));
    }

    [Fact]
    public void ToolSchemaIsBoundedBeforeParsingOrCloning()
    {
        var oversized = new byte[(1024 * 1024) + 1];

        Assert.Throws<ArgumentException>(
            () => new AgentToolDefinition(
                "terminal.read_screen",
                "Read a bounded terminal snapshot.",
                oversized));
    }

    [Fact]
    public void ToolSchemaDoesNotRetainCallerOwnedMemory()
    {
        var schema = "{\"type\":\"object\"}"u8.ToArray();
        var tool = new AgentToolDefinition(
            "terminal.read_screen",
            "Read a bounded terminal snapshot.",
            schema);

        Array.Fill(schema, (byte)'x');

        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void ToolResultValuesRejectAmbiguousOrOversizedContent()
    {
        var oversized = new string(
            'x',
            AgentToolResultValue.MaximumContentBytes + 1);

        Assert.Throws<ArgumentException>(
            () => AgentToolResultValue.FromText(oversized));
        Assert.Throws<ArgumentException>(
            () => AgentToolResultValue.FromJson(
                "{\"one\":1,\"one\":2}"u8.ToArray()));
        Assert.Throws<ArgumentException>(
            () => AgentToolResultValue.FromJson("not-json"u8.ToArray()));
    }

    [Fact]
    public void ToolResultCopiesExactProposalCorrelationIntoInertData()
    {
        using var arguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"/tmp/a\"}");
        var proposal = new AgentToolProposal(
            "run:1:0",
            1,
            "provider-call-1",
            "read_file",
            arguments.RootElement);
        var value = AgentToolResultValue.FromText("not found");

        var result = new AgentToolResult(
            proposal,
            AgentToolResultStatus.Failed,
            "file_not_found",
            value);

        Assert.Equal(proposal.Id, result.ProposalId);
        Assert.Equal(proposal.Generation, result.Generation);
        Assert.Equal(proposal.ProviderCallId, result.ProviderCallId);
        Assert.Equal(AgentToolResultStatus.Failed, result.Status);
        Assert.Equal("file_not_found", result.StableCode);
        Assert.Same(value, result.Value);
        Assert.True(result.ContainsUntrustedContent);
        Assert.True(value.ContainsUntrustedContent);
    }
}
