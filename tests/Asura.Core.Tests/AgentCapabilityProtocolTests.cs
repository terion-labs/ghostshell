namespace Asura.Core.Tests;

public sealed class AgentCapabilityProtocolTests
{
    [Fact]
    public void Capability_request_id_preserves_a_valid_runtime_identity()
    {
        var id = new AgentCapabilityRequestId("capability-request-1");

        Assert.Equal("capability-request-1", id.Value);
        Assert.Equal("capability-request-1", id.ToString());
        Assert.NotEqual(
            AgentCapabilityRequestId.New(),
            AgentCapabilityRequestId.New());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\u00A0")]
    public void Capability_request_id_rejects_a_missing_identity(string? value)
    {
        var error = Assert.ThrowsAny<ArgumentException>(
            () => new AgentCapabilityRequestId(value!));

        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Every_capability_has_one_reversible_stable_token()
    {
        var capabilities = Enum.GetValues<AgentCapability>();
        var tokens = capabilities
            .Select(AgentCapabilityProtocol.GetToken)
            .ToArray();

        Assert.Equal(capabilities.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        foreach (var (capability, token) in capabilities.Zip(tokens))
        {
            Assert.Matches("^[a-z]+(?:_[a-z]+)*$", token);
            Assert.True(AgentCapabilityProtocol.TryParseToken(
                token,
                out var parsed));
            Assert.Equal(capability, parsed);
        }
    }

    [Theory]
    [InlineData(AgentCapability.TerminalRead, "terminal_read")]
    [InlineData(AgentCapability.RunCommands, "run_commands")]
    [InlineData(AgentCapability.EditFiles, "edit_files")]
    [InlineData(AgentCapability.ReadFiles, "read_files")]
    [InlineData(AgentCapability.Search, "search")]
    [InlineData(AgentCapability.Git, "git")]
    [InlineData(AgentCapability.WebFetch, "web_fetch")]
    [InlineData(AgentCapability.Docker, "docker")]
    [InlineData(
        AgentCapability.DestructiveTerminalActions,
        "destructive_terminal_actions")]
    [InlineData(AgentCapability.BrowserNavigation, "browser_navigation")]
    [InlineData(AgentCapability.BrowserData, "browser_data")]
    [InlineData(AgentCapability.ProcessControl, "process_control")]
    [InlineData(AgentCapability.McpTools, "mcp_tools")]
    [InlineData(AgentCapability.SecretUse, "secret_use")]
    [InlineData(AgentCapability.BrowserInteraction, "browser_interaction")]
    [InlineData(AgentCapability.BrowserScripting, "browser_scripting")]
    [InlineData(AgentCapability.BrowserDiagnostics, "browser_diagnostics")]
    [InlineData(AgentCapability.DatabaseRead, "database_read")]
    [InlineData(AgentCapability.DatabaseWrite, "database_write")]
    [InlineData(AgentCapability.DockerData, "docker_data")]
    [InlineData(AgentCapability.SystemData, "system_data")]
    [InlineData(AgentCapability.ProcessData, "process_data")]
    [InlineData(AgentCapability.ArtifactTransfer, "artifact_transfer")]
    [InlineData(AgentCapability.WorkspaceLayout, "workspace_layout")]
    [InlineData(AgentCapability.GitData, "git_data")]
    public void Capability_tokens_are_stable_protocol_values(
        AgentCapability capability,
        string expectedToken)
    {
        Assert.Equal(
            expectedToken,
            AgentCapabilityProtocol.GetToken(capability));
        Assert.True(AgentCapabilityProtocol.TryParseToken(
            expectedToken,
            out var parsed));
        Assert.Equal(capability, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TerminalRead")]
    [InlineData("terminal-read")]
    [InlineData("terminal_read ")]
    [InlineData("unknown")]
    public void Capability_tokens_are_exact_and_fail_closed(string? token)
    {
        Assert.False(AgentCapabilityProtocol.TryParseToken(
            token,
            out _));
    }

    [Fact]
    public void Undefined_capability_has_no_protocol_token()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgentCapabilityProtocol.GetToken(
                (AgentCapability)int.MaxValue));
    }
}
