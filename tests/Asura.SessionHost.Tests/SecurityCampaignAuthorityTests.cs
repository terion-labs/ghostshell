using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class SecurityCampaignAuthorityTests
{
    [Fact(DisplayName = "lifecycle.cancel-after-commit")]
    [Trait("SecurityCampaignCase", "lifecycle.cancel-after-commit")]
    public Task CancelAfterCommitAsync() =>
        new AgentTerminalSessionHostTests()
            .Caller_cancellation_after_dispatch_completes_the_action_exactly_once();

    [Fact(DisplayName = "lifecycle.outcome-unknown")]
    [Trait("SecurityCampaignCase", "lifecycle.outcome-unknown")]
    public Task OutcomeUnknownAsync() =>
        new AgentWorkspaceLayoutSessionHostTests()
            .Post_authorization_unknown_result_is_never_retried();

    [Fact(DisplayName = "authority.tab.create broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.tab.create")]
    public Task TabCreateAsync() => LayoutAsync(BuiltInAgentTools.TabCreate, static tests => tests.Authorized_tab_create_mutates_and_verifies_the_fresh_graph());

    [Fact(DisplayName = "authority.tab.close broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.tab.close")]
    public Task TabCloseAsync() => LayoutAsync(BuiltInAgentTools.TabClose, static tests => tests.SecurityCampaignDispatchesExactLayoutToolAsync(BuiltInAgentTools.TabClose));

    [Fact(DisplayName = "authority.panel.focus broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.panel.focus")]
    public Task PanelFocusAsync() => RunAsync(BuiltInAgentTools.PanelFocus, static () => new AgentPanelSessionHostTests().Graph_revision_drift_rejects_the_prepared_permit_without_focusing(), static () => new AgentPanelSessionHostTests().Focus_waits_for_one_action_permit_before_committing_graph_state());

    [Fact(DisplayName = "authority.panel.connect broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.panel.connect")]
    public Task PanelConnectAsync() => LayoutAsync(BuiltInAgentTools.PanelConnect, static tests => tests.SecurityCampaignDispatchesExactLayoutToolAsync(BuiltInAgentTools.PanelConnect));

    [Fact(DisplayName = "authority.panel.add broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.panel.add")]
    public Task PanelAddAsync() => LayoutAsync(BuiltInAgentTools.PanelAdd, static tests => tests.SecurityCampaignDispatchesExactLayoutToolAsync(BuiltInAgentTools.PanelAdd));

    [Fact(DisplayName = "authority.panel.split broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.panel.split")]
    public Task PanelSplitAsync() => LayoutAsync(BuiltInAgentTools.PanelSplit, static tests => tests.Later_graph_revision_that_preserves_the_split_target_is_verified());

    [Fact(DisplayName = "authority.panel.close broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.panel.close")]
    public Task PanelCloseAsync() => LayoutAsync(BuiltInAgentTools.PanelClose, static tests => tests.SecurityCampaignDispatchesExactLayoutToolAsync(BuiltInAgentTools.PanelClose));

    [Fact(DisplayName = "authority.terminal.jump_to_rendered_history broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.jump_to_rendered_history")]
    public Task TerminalJumpAsync() => TerminalAsync(BuiltInAgentTools.TerminalJumpToRenderedHistory, static tests => tests.RenderedHistorySearchAndJumpDispatchTypedPorts());

    [Fact(DisplayName = "authority.terminal.scroll_viewport broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.scroll_viewport")]
    public Task TerminalScrollAsync() => TerminalAsync(BuiltInAgentTools.TerminalScrollViewport, static tests => tests.HistoryProjectionViewportScrollAndDelayWaitDispatchTypedPorts());

    [Fact(DisplayName = "authority.terminal.send_text broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.send_text")]
    public Task TerminalTextAsync() => TerminalAsync(BuiltInAgentTools.TerminalSendText, static tests => tests.Exact_prepared_action_consumes_once_dispatches_once_and_completes_once());

    [Fact(DisplayName = "authority.terminal.paste broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.paste")]
    public Task TerminalPasteAsync() => TerminalAsync(BuiltInAgentTools.TerminalPaste, static tests => tests.Confirmed_paste_dispatches_exactly_once_with_unsafe_content_approved(AgentAuthorizationSource.HumanApproval));

    [Fact(DisplayName = "authority.terminal.submit_text broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.submit_text")]
    public Task TerminalSubmitAsync() => TerminalAsync(BuiltInAgentTools.TerminalSubmitText, static tests => tests.Confirmed_submit_text_dispatches_one_atomic_engine_operation(AgentAuthorizationSource.HumanApproval));

    [Fact(DisplayName = "authority.terminal.send_keys broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.send_keys")]
    public Task TerminalKeysAsync() => TerminalAsync(BuiltInAgentTools.TerminalSendKeys, static tests => tests.Repeated_key_is_dispatched_once_without_host_side_expansion());

    [Fact(DisplayName = "authority.terminal.send_chord broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.send_chord")]
    public Task TerminalChordAsync() => TerminalAsync(BuiltInAgentTools.TerminalSendChord, static tests => tests.Confirmed_character_chord_dispatches_exactly_once(AgentAuthorizationSource.HumanApproval));

    [Fact(DisplayName = "authority.terminal.send_mouse broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.send_mouse")]
    public Task TerminalMouseAsync() => TerminalAsync(BuiltInAgentTools.TerminalSendMouse, static tests => tests.Real_broker_authorizes_executes_and_audits_one_exact_mouse_event());

    [Fact(DisplayName = "authority.terminal.interrupt broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.interrupt")]
    public Task TerminalInterruptAsync() => TerminalAsync(BuiltInAgentTools.TerminalInterrupt, static tests => tests.SecurityCampaignDispatchesInterruptExactlyOnceAsync());

    [Fact(DisplayName = "authority.terminal.resize broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.terminal.resize")]
    public Task TerminalResizeAsync() => TerminalAsync(BuiltInAgentTools.TerminalResize, static tests => tests.Exact_resize_attachment_dispatches_once_and_updates_its_viewport());

    [Fact(DisplayName = "authority.browser.click broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.click")]
    public Task BrowserClickAsync() => BrowserAsync(BuiltInAgentTools.BrowserClick, static tests => tests.HumanApprovedClickBindsExactReferenceRevisionWithoutOriginRestriction());

    [Fact(DisplayName = "authority.browser.fill broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.fill")]
    public Task BrowserFillAsync() => BrowserAsync(BuiltInAgentTools.BrowserFill, static tests => tests.HumanApprovedFillBindsExactReferenceTextRevisionWithoutOriginRestriction());

    [Fact(DisplayName = "authority.browser.check broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.check")]
    public Task BrowserCheckAsync() => BrowserAsync(BuiltInAgentTools.BrowserCheck, static tests => tests.HumanApprovedCheckBindsExactReferenceRevisionWithoutOriginRestriction());

    [Fact(DisplayName = "authority.browser.mouse broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.mouse")]
    public Task BrowserMouseAsync() => BrowserAsync(BuiltInAgentTools.BrowserMouse, static tests => tests.LowLevelMouseDispatchBindsFreshViewportAndAdvancesInputEpoch());

    [Fact(DisplayName = "authority.browser.key broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.key")]
    public Task BrowserKeyAsync() => BrowserAsync(BuiltInAgentTools.BrowserKey, static tests => tests.SecurityCampaignDispatchesExactLowLevelInputAsync(BuiltInAgentTools.BrowserKey));

    [Fact(DisplayName = "authority.browser.scroll broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.scroll")]
    public Task BrowserScrollAsync() => BrowserAsync(BuiltInAgentTools.BrowserScroll, static tests => tests.SecurityCampaignDispatchesExactLowLevelInputAsync(BuiltInAgentTools.BrowserScroll));

    [Fact(DisplayName = "authority.browser.evaluate broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.evaluate")]
    public Task BrowserEvaluateAsync() => BrowserAsync(BuiltInAgentTools.BrowserEvaluate, static tests => tests.MainWorldEvaluationRequiresHumanApprovalAndReturnsJsonValue());

    [Fact(DisplayName = "authority.browser.navigate broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.navigate")]
    public Task BrowserNavigateAsync() => BrowserAsync(BuiltInAgentTools.BrowserNavigate, static tests => tests.Real_broker_auto_navigation_still_requires_exact_human_approval());

    [Fact(DisplayName = "authority.browser.back broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.back")]
    public Task BrowserBackAsync() => BrowserNavigationAsync(BuiltInAgentTools.BrowserBack);

    [Fact(DisplayName = "authority.browser.forward broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.forward")]
    public Task BrowserForwardAsync() => BrowserNavigationAsync(BuiltInAgentTools.BrowserForward);

    [Fact(DisplayName = "authority.browser.reload broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.reload")]
    public Task BrowserReloadAsync() => BrowserNavigationAsync(BuiltInAgentTools.BrowserReload);

    [Fact(DisplayName = "authority.browser.stop broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.browser.stop")]
    public Task BrowserStopAsync() => BrowserNavigationAsync(BuiltInAgentTools.BrowserStop);

    [Fact(DisplayName = "authority.files.mkdir broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.files.mkdir")]
    public Task FilesMkdirAsync() => FileAsync(BuiltInAgentTools.FilesCreateDirectory, static tests => tests.Create_directory_derives_must_not_exist_and_returns_a_trusted_receipt());

    [Fact(DisplayName = "authority.files.move broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.files.move")]
    public Task FilesMoveAsync() => FileAsync(BuiltInAgentTools.FilesMove, static tests => tests.Move_derives_must_not_exist_and_returns_the_verified_destination());

    [Fact(DisplayName = "authority.files.delete broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.files.delete")]
    public Task FilesDeleteAsync() => FileAsync(BuiltInAgentTools.FilesDelete, static tests => tests.Delete_derives_non_recursive_must_exist_and_returns_a_fixed_receipt());

    private static Task LayoutAsync(string toolName, Func<AgentWorkspaceLayoutSessionHostTests, Task> valid) =>
        RunAsync(toolName, static () => new AgentWorkspaceLayoutSessionHostTests().Exact_workspace_port_and_supported_kind_are_required(), () => valid(new AgentWorkspaceLayoutSessionHostTests()));

    private static Task TerminalAsync(string toolName, Func<AgentTerminalSessionHostTests, Task> valid) =>
        RunAsync(toolName, static () => new AgentTerminalSessionHostTests().Action_with_a_different_material_binding_is_denied_before_engine_dispatch(), () => valid(new AgentTerminalSessionHostTests()));

    private static Task BrowserAsync(string toolName, Func<AgentBrowserSessionHostTests, Task> valid) =>
        RunAsync(toolName, static () => new AgentBrowserSessionHostTests().Wrong_authorization_and_changed_material_cannot_dispatch(), () => valid(new AgentBrowserSessionHostTests()));

    private static Task BrowserNavigationAsync(string toolName) =>
        BrowserAsync(toolName, static tests => tests.State_read_and_all_navigation_operations_execute_and_complete_once());

    private static Task FileAsync(string toolName, Func<AgentFileSessionHostTests, Task> valid) =>
        RunAsync(toolName, static () => new AgentFileSessionHostTests().Trusted_scope_change_during_authorization_denies_provider_dispatch(), () => valid(new AgentFileSessionHostTests()));

    private static async Task RunAsync(
        string toolName,
        Func<Task> rejectsDriftedBinding,
        Func<Task> dispatchesCorrectSink)
    {
        await AssertBrokerRejectsWithoutExactAuthorityAsync(toolName);
        await rejectsDriftedBinding();
        await dispatchesCorrectSink();
    }

    private static async Task AssertBrokerRejectsWithoutExactAuthorityAsync(
        string toolName)
    {
        var descriptor = Assert.Single(
            BuiltInAgentTools.Catalog.Tools,
            tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                descriptor.Capability,
                AgentPermission.Off),
        };
        var runId = new AgentRunId("security-campaign-run");
        var target = new AgentTarget.Workspace(
            new WindowInstanceId("security-window"),
            new WorkspaceInstanceId("security-workspace"));
        var agent = new ActorDescriptor(
            new ActorId("security-agent"),
            ActorKind.Agent,
            "Security campaign agent");
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            new CampaignAuditStore(),
            TimeProvider.System);
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                runId,
                agent,
                new ClientId("security-client"),
                target,
                policy,
                policyGeneration: 1),
            default));
        var now = DateTimeOffset.UtcNow;
        var proposal = new AgentActionProposal(
            AgentActionId.New(),
            runId,
            agent,
            toolName,
            target,
            AgentActionDigest.FromUtf8("security-context"),
            AgentActionDigest.FromUtf8("security-arguments"),
            new AgentApprovalPresentation("Security case", "exact target", null),
            policyGeneration: 1,
            now,
            now.AddMinutes(1));

        var denied = Assert.IsType<AgentAuthorizationResult.Denied>(
            await broker.RequestAsync(proposal, default));

        Assert.Equal(AgentAuthorizationErrorCode.PolicyDenied, denied.Error.Code);

        var askPolicy = policy with
        {
            Permissions = policy.Permissions.SetItem(
                descriptor.Capability,
                AgentPermission.Ask),
        };
        var askRunId = new AgentRunId("security-campaign-ask-run");
        var askProposal = new AgentActionProposal(
            AgentActionId.New(),
            askRunId,
            agent,
            toolName,
            target,
            proposal.TargetFingerprint,
            proposal.ArgumentDigest,
            proposal.Presentation,
            policyGeneration: 2,
            now,
            now.AddMinutes(1));
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                askRunId,
                agent,
                new ClientId("security-client"),
                target,
                askPolicy,
                policyGeneration: 2),
            default));
        var approval = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(askProposal, default));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    approval.Approval.Id,
                    new ActorDescriptor(
                        new ActorId("security-client"),
                        ActorKind.Human,
                        "Security campaign user",
                        new ClientId("security-client")),
                    approved: true,
                    AgentApprovalDuration.Once,
                    now),
                default));
        Assert.IsType<AgentPermitResult.Granted>(await broker.ConsumeAsync(
            authorized.Authorization.Id,
            askProposal,
            default));
        Assert.IsType<AgentPermitResult.Denied>(await broker.ConsumeAsync(
            authorized.Authorization.Id,
            askProposal,
            default));
    }

    private sealed class CampaignAuditStore : IAuditStore
    {
        private readonly List<AuditEventRecord> _events = [];

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(auditEvent);
            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AuditEventRecord> matches =
            [
                .. _events.Where(item => string.Equals(
                    item.CorrelationId,
                    correlationId,
                    StringComparison.Ordinal)),
            ];
            return ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                    matches));
        }
    }
}
