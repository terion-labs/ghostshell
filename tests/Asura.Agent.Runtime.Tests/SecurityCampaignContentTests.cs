namespace Asura.Agent.Runtime.Tests;

/// <summary>
/// Stable registry-facing names for the full hostile-content continuation
/// fixtures. The delegated fixture methods retain their domain-local setup and
/// assertions while this file remains the machine-readable campaign boundary.
/// </summary>
public sealed class SecurityCampaignContentTests
{
    [Fact(DisplayName = "content.terminal-continuation")]
    [Trait("SecurityCampaignCase", "content.terminal-continuation")]
    public Task TerminalContinuation() =>
        new GovernedAgentRuntimeTests()
            .MaliciousTerminalInstructionsCannotAuthorizeTheirOwnMutation();

    [Fact(DisplayName = "content.browser-continuation")]
    [Trait("SecurityCampaignCase", "content.browser-continuation")]
    public Task BrowserContinuation() =>
        new GovernedAgentRuntimeBrowserTests()
            .BrowserSnapshotAutoReturnsBoundedUntrustedNodes();

    [Fact(DisplayName = "content.file-continuation")]
    [Trait("SecurityCampaignCase", "content.file-continuation")]
    public Task FileContinuation() =>
        new GovernedAgentRuntimeFileTests()
            .FilePromptInjectionIsLabeledUntrustedRedactedAndCannotDispatchTerminalInput();

    [Fact(DisplayName = "secrecy.provider-tool-continuation real provider capture excludes shared canaries")]
    [Trait("SecurityCampaignCase", "secrecy.provider-tool-continuation")]
    public Task ProviderToolContinuationSecrecy() =>
        new GovernedAgentRuntimeFileTests()
            .FilePromptInjectionIsLabeledUntrustedRedactedAndCannotDispatchTerminalInput();

    [Fact(DisplayName = "content.workspace-label-continuation")]
    [Trait("SecurityCampaignCase", "content.workspace-label-continuation")]
    public Task WorkspaceLabelContinuation() =>
        new GovernedAgentRuntimeTests()
            .WorkspaceWithOnlyLauncherCanAnswerAndInspectItsGraph();

    [Fact(DisplayName = "content.process-name-continuation")]
    [Trait("SecurityCampaignCase", "content.process-name-continuation")]
    public Task ProcessNameContinuation() =>
        new GovernedAgentRuntimeProcessTests()
            .AutoProcessObservationUsesBrokerHostAndContinuesProvider();

    [Fact(DisplayName = "content.provider-continuation")]
    [Trait("SecurityCampaignCase", "content.provider-continuation")]
    public Task ProviderContinuation() =>
        new GovernedAgentRuntimeTests()
            .MaliciousTerminalPasteCannotSelfAuthorizeOrSubmitSecrets();

    [Fact(DisplayName = "content.mcp-result-continuation")]
    [Trait("SecurityCampaignCase", "content.mcp-result-continuation")]
    public Task McpResultContinuation() =>
        new GovernedAgentRuntimeProcessTests()
            .AskModeAdvertisesFrozenAliasAndExecutesOnlyAfterExactApproval();
}
