using System.Collections.Concurrent;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class AgentTerminalSessionHostTests
{
    [Fact]
    public async Task Real_broker_authorizes_executes_and_audits_one_exact_screen_read()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentTerminalHostFixture.CreateAsync(
            clock,
            broker);
        var registrationError = await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                AgentPolicy.Default,
                policyGeneration: 0),
            default);
        Assert.Null(registrationError);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "brokered screen";
        terminal.ScreenContentRevision = 11;
        terminal.ScreenWorkingDirectory = "/srv/current";
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.ReadScreen(fixture.SessionId));
        var requested = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(action.Proposal, default));

        var result = (AgentTerminalActionResult.Screen)(
            await fixture.Client.RunAgentTerminalActionAsync(
                requested.Authorization.Id,
                action,
                default)).Value();

        Assert.Equal("brokered screen", result.Snapshot.PlainText);
        Assert.Equal(11, result.Snapshot.ContentRevision);
        var nextAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.ReadScreen(fixture.SessionId));
        Assert.Equal(
            "/srv/current",
            nextAction.Proposal.Presentation.WorkingDirectory);
        var events = audit.Events
            .Where(item => string.Equals(item.CorrelationId, action.Proposal.Id.Value, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            events.Select(item => item.Outcome));
        Assert.All(
            events,
            item => Assert.Equal(
                BuiltInAgentTools.TerminalReadScreen,
                item.Action));
        Assert.Single(events, item => item.Outcome == AuditOutcome.Started);
        Assert.Single(events, item => item.Outcome == AuditOutcome.Succeeded);
    }

    [Fact]
    public async Task Real_broker_authorizes_executes_and_audits_one_exact_mouse_event()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentTerminalHostFixture.CreateAsync(
            clock,
            broker);
        var registrationError = await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                AgentPolicy.Default,
                policyGeneration: 0),
            default);
        Assert.Null(registrationError);
        var mouseInput = new TerminalMouseInput(
            TerminalMouseButton.Right,
            TerminalMouseEventKind.Drag,
            Column: 47,
            Row: 13,
            TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                mouseInput,
                ExpectedContentRevision: 0));
        var requested = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(action.Proposal, default));
        var approved = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    requested.Approval.Id,
                    fixture.HumanContext().Actor,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                default));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            approved.Authorization.Id,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(1, terminal.MouseCount);
        Assert.Equal(mouseInput, terminal.LastMouseInput);
        var events = audit.Events
            .Where(item => string.Equals(item.CorrelationId, action.Proposal.Id.Value, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            events.Select(item => item.Outcome));
        Assert.All(
            events,
            item => Assert.Equal(
                BuiltInAgentTools.TerminalSendMouse,
                item.Action));
        var completed = Assert.IsType<AuditDetails.AgentActionDetails>(
            events[^1].Details);
        Assert.Equal("ok", completed.ResultCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact(DisplayName = "lifecycle.start-audit-failure")]
    [Trait("SecurityCampaignCase", "lifecycle.start-audit-failure")]
    public async Task SecurityCampaignStartedAuditFailureDeniesNativeDispatchAsync()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentTerminalHostFixture.CreateAsync(
            clock,
            broker);
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                AgentPolicy.Default,
                policyGeneration: 0),
            default));
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                new TerminalMouseInput(
                    TerminalMouseButton.Left,
                    TerminalMouseEventKind.Down,
                    Column: 4,
                    Row: 3),
                ExpectedContentRevision: 0));
        var requested = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(action.Proposal, default));
        var approved = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    requested.Approval.Id,
                    fixture.HumanContext().Actor,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                default));
        audit.FailurePredicate = item => string.Equals(
            item.CorrelationId,
            action.Proposal.Id.Value,
            StringComparison.Ordinal) && item.Outcome == AuditOutcome.Started;

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            approved.Authorization.Id,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].MouseCount);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Approved],
            audit.Events.Where(item => string.Equals(
                item.CorrelationId,
                action.Proposal.Id.Value,
                StringComparison.Ordinal)).Select(item => item.Outcome));
    }

    [Fact]
    public async Task StaleContentRevisionRejectsMouseBeforePtyDispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenContentRevision = 8;
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                new TerminalMouseInput(
                    TerminalMouseButton.Left,
                    TerminalMouseEventKind.Down,
                    Column: 3,
                    Row: 2),
                ExpectedContentRevision: 7));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.RevisionConflict, result.Error().Code);
        Assert.Equal("terminal_content_revision_changed", result.Error().StableCode);
        Assert.Equal(0, terminal.MouseCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task Mouse_completion_audit_failure_quarantines_the_run_without_redispatch()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentTerminalHostFixture.CreateAsync(
            clock,
            broker);
        var registrationError = await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                AgentPolicy.Default,
                policyGeneration: 0),
            default);
        Assert.Null(registrationError);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                new TerminalMouseInput(
                    TerminalMouseButton.WheelDown,
                    TerminalMouseEventKind.WheelDown,
                    Column: 12,
                    Row: 5),
                ExpectedContentRevision: 0));
        var requested = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(action.Proposal, default));
        var approved = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    requested.Approval.Id,
                    fixture.HumanContext().Actor,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                default));
        audit.FailurePredicate = item => string.Equals(item.CorrelationId, action.Proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded;

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            approved.Authorization.Id,
            action,
            default);
        var nextAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.ReadScreen(fixture.SessionId));
        var nextRequest = await broker.RequestAsync(
            nextAction.Proposal,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Factory[fixture.SessionId].MouseCount);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(nextRequest).Error.Code);
        Assert.DoesNotContain(
            audit.Events,
            item => string.Equals(item.CorrelationId, action.Proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.HumanApproval)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task Confirmed_paste_dispatches_exactly_once_with_unsafe_content_approved(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        const string text = "printf 'first\\nsecond'\n";
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Paste(
                fixture.SessionId,
                text));
        var authorizationId = fixture.Authorization.Arm(
            action,
            source,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(1, terminal.PasteCount);
        Assert.Equal(
            new TerminalPasteInput(text),
            terminal.LastPasteInput);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.HumanApproval)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task Confirmed_submit_text_dispatches_one_atomic_engine_operation(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        const string text = "echo one operation";
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SubmitText(
                fixture.SessionId,
                text));
        var authorizationId = fixture.Authorization.Arm(
            action,
            source,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(1, terminal.SubmitTextCount);
        Assert.Equal(0, terminal.PasteCount);
        Assert.Equal(0, terminal.EnterCount);
        Assert.Equal(
            new TerminalPasteInput(text),
            terminal.LastPasteInput);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Repeated_key_is_dispatched_once_without_host_side_expansion()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var keyStroke = new TerminalKeyStroke(
            TerminalKey.Backspace,
            TerminalKeyModifiers.None,
            RepeatCount: 12);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendKey(
                fixture.SessionId,
                keyStroke));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(keyStroke, terminal.LastKeyStroke);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task Auto_policy_paste_fails_closed_before_engine_dispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Paste(
                fixture.SessionId,
                "echo must-not-paste\n"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.AutoPolicy,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.ConfirmationRequired, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].PasteCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("confirmation_required", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(SessionCapabilities.TerminalPaste)]
    [InlineData(SessionCapabilities.TerminalAgentInputBarrier)]
    public async Task Removed_paste_safety_capability_denies_before_consuming_or_dispatching(
        string capability)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Paste(
                fixture.SessionId,
                "echo stale capability\n"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval,
            fixture.ClientId);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.RemoveCapability(capability);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.CapabilityNotSupported, result.Error().Code);
        Assert.Equal(0, terminal.PasteCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(SessionCapabilities.TerminalPaste)]
    [InlineData(SessionCapabilities.TerminalAgentInputBarrier)]
    public async Task Paste_safety_capability_removed_during_authorization_denies_dispatch(
        string capability)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Paste(
                fixture.SessionId,
                "echo capability-race\n"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval,
            fixture.ClientId);
        fixture.Authorization.BlockConsumes = true;

        var execution = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.RemoveCapability(capability);
        fixture.Authorization.ReleaseConsume.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostErrorCode.CapabilityNotSupported, result.Error().Code);
        Assert.Equal(0, terminal.PasteCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("capability_not_supported", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Engine_paste_confirmation_result_is_not_reported_as_success()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Paste(
                fixture.SessionId,
                "echo engine-still-refuses\n"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval,
            fixture.ClientId);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.PasteResultOverride =
            TerminalPasteResult.ConfirmationRequired(bracketed: true);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.ConfirmationRequired, result.Error().Code);
        Assert.Equal(1, terminal.PasteCount);
        Assert.Equal("echo engine-still-refuses\n", terminal.LastPasteInput?.Text);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("confirmation_required", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Paste_completion_audit_failure_does_not_redispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Paste(
                fixture.SessionId,
                "echo once-only\n"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.YoloPolicy,
            fixture.ClientId);
        fixture.Authorization.CompletionFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "The completion audit failed.");

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Factory[fixture.SessionId].PasteCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.HumanApproval)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task Confirmed_character_chord_dispatches_exactly_once(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var chord = new TerminalCharacterChord(
            'd',
            TerminalCharacterChordModifier.Control);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                chord));
        var authorizationId = fixture.Authorization.Arm(
            action,
            source,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(1, terminal.ChordCount);
        Assert.Equal(chord, terminal.LastChord);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Auto_policy_character_chord_fails_closed_before_engine_dispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                new TerminalCharacterChord(
                    'r',
                    TerminalCharacterChordModifier.Control)));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.AutoPolicy,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.ConfirmationRequired, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].ChordCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("confirmation_required", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(SessionCapabilities.TerminalSendChord)]
    [InlineData(SessionCapabilities.TerminalAgentInputBarrier)]
    public async Task Removed_chord_safety_capability_denies_before_consuming_or_dispatching(
        string capability)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                new TerminalCharacterChord(
                    'z',
                    TerminalCharacterChordModifier.Control)));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval,
            fixture.ClientId);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.RemoveCapability(capability);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.CapabilityNotSupported, result.Error().Code);
        Assert.Equal(0, terminal.ChordCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(SessionCapabilities.TerminalSendChord)]
    [InlineData(SessionCapabilities.TerminalAgentInputBarrier)]
    public async Task Chord_safety_capability_removed_during_authorization_denies_dispatch(
        string capability)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                new TerminalCharacterChord(
                    'l',
                    TerminalCharacterChordModifier.Control)));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.YoloPolicy,
            fixture.ClientId);
        fixture.Authorization.BlockConsumes = true;

        var execution = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.RemoveCapability(capability);
        fixture.Authorization.ReleaseConsume.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostErrorCode.CapabilityNotSupported, result.Error().Code);
        Assert.Equal(0, terminal.ChordCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("capability_not_supported", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Character_chord_completion_audit_failure_does_not_redispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                new TerminalCharacterChord(
                    'x',
                    TerminalCharacterChordModifier.Alt)));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.YoloPolicy,
            fixture.ClientId);
        fixture.Authorization.CompletionFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "The completion audit failed.");

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Factory[fixture.SessionId].ChordCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Human_lease_preemption_cancels_blocked_character_chord()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var chord = new TerminalCharacterChord(
            'd',
            TerminalCharacterChordModifier.Control);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                chord));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval,
            fixture.ClientId);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockChords = true;

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.ChordStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var humanLease = (await fixture.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                fixture.SessionId,
                fixture.Attachment.Id,
                TimeSpan.FromMinutes(1)),
            fixture.HumanContext(),
            default)).Value();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(humanLease.PreemptedAnotherHolder);
        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("input_lease_revoked", result.Error().StableCode);
        Assert.Equal(1, terminal.ChordCount);
        Assert.Null(terminal.LastChord);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("input_lease_revoked", completion.StableCode);
        Assert.Equal(
            humanLease.Lease!.Id,
            (await fixture.SnapshotAsync()).InputLease?.Id);
    }

    [Fact]
    public async Task Caller_cancellation_after_committed_character_chord_preserves_success()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var chord = new TerminalCharacterChord(
            'x',
            TerminalCharacterChordModifier.Alt);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendChord(
                fixture.SessionId,
                chord));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.YoloPolicy,
            fixture.ClientId);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockChords = true;
        terminal.IgnoreChordCancellationAfterStart = true;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            cancellation.Token).AsTask();
        await terminal.ChordStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        terminal.ReleaseChord.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        Assert.Equal(1, terminal.ChordCount);
        Assert.Equal(chord, terminal.LastChord);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
        Assert.False(fixture.Authorization.LastCompletionTokenWasCancelled);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Exact_prepared_action_consumes_once_dispatches_once_and_completes_once()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "echo ready"));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(1, terminal.WriteCount);
        Assert.Equal("echo ready", terminal.LastWrittenText);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Theory]
    [InlineData(AgentAuthorizationSource.HumanApproval)]
    [InlineData(AgentAuthorizationSource.YoloPolicy)]
    public async Task Confirmed_authority_hands_the_approving_clients_lease_to_one_agent_action(
        AgentAuthorizationSource source)
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        _ = await fixture.AcquireHumanLeaseAsync(fixture.ClientId);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "approved handoff"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            source,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        Assert.Equal("approved handoff", fixture.Factory[fixture.SessionId].LastWrittenText);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
        Assert.Single(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Auto_policy_cannot_handoff_a_human_lease()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var humanLease = await fixture.AcquireHumanLeaseAsync(fixture.ClientId);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "must not handoff"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.AutoPolicy,
            fixture.ClientId);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].WriteCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("lease_denied", completion.StableCode);
        Assert.Equal(humanLease.Id, (await fixture.SnapshotAsync()).InputLease?.Id);
    }

    [Fact]
    public async Task Approval_for_a_different_client_cannot_handoff_the_human_lease()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var humanLease = await fixture.AcquireHumanLeaseAsync(fixture.ClientId);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "wrong client"));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval,
            new ClientId("different-client"));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].WriteCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("lease_denied", completion.StableCode);
        Assert.Equal(humanLease.Id, (await fixture.SnapshotAsync()).InputLease?.Id);
    }

    [Fact]
    public async Task Same_agent_lease_is_replaced_for_exactly_one_auto_action()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        _ = await fixture.AcquireAgentLeaseAsync(fixture.Agent);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "replace my lease"));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Another_agent_lease_cannot_be_used_or_handed_off()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var otherAgent = new ActorDescriptor(
            new ActorId("agent-2"),
            ActorKind.Agent,
            "Other agent");
        var otherLease = await fixture.AcquireAgentLeaseAsync(otherAgent);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "must not preempt"));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.LeaseDenied, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Equal(otherLease.Id, (await fixture.SnapshotAsync()).InputLease?.Id);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
    }

    [Fact]
    public async Task Action_with_a_different_material_binding_is_denied_before_engine_dispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var actionId = AgentActionId.New();
        var approved = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "echo approved"),
            actionId);
        var mutated = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "echo different"),
            actionId);
        var authorizationId = fixture.Authorization.Arm(approved);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            mutated,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Concurrent_reuse_of_one_authorization_dispatches_at_most_once()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "long-running"));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockWrites = true;

        var first = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);
        terminal.ReleaseWrite.TrySetResult();
        var firstResult = await first;

        Assert.IsType<AgentTerminalActionResult.Completed>(firstResult.Value());
        Assert.Equal(HostErrorCode.InvalidRequest, second.Error().Code);
        Assert.Equal(1, terminal.WriteCount);
        Assert.Equal(2, fixture.Authorization.ConsumeCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Replacing_the_approved_panel_session_denies_without_consuming_or_dispatching()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "echo stale"));
        var authorizationId = fixture.Authorization.Arm(action);
        var replacementId = new SessionId("replacement-session");

        _ = await fixture.OpenActiveSessionAsync(replacementId);
        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Equal(0, fixture.Factory[replacementId].WriteCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Replacing_the_approved_mouse_target_denies_before_consuming_or_dispatching()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var mouseInput = new TerminalMouseInput(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Down,
            Column: 3,
            Row: 9);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                mouseInput,
                ExpectedContentRevision: 0));
        var authorizationId = fixture.Authorization.Arm(action);
        var replacementId = new SessionId("replacement-mouse-session");

        _ = await fixture.OpenActiveSessionAsync(replacementId);
        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].MouseCount);
        Assert.Equal(0, fixture.Factory[replacementId].MouseCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Exact_resize_attachment_dispatches_once_and_updates_its_viewport()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var viewport = new ViewportDescriptor(
            LogicalWidth: 800,
            LogicalHeight: 600,
            RenderScale: 2,
            Columns: 132,
            Rows: 43);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    viewport)));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        var terminal = fixture.Factory[fixture.SessionId];
        Assert.Equal(1, terminal.ResizeCount);
        Assert.Equal(viewport, terminal.LastResizeViewport);
        var snapshot = await fixture.SnapshotAsync();
        var attachment = Assert.Single(
            snapshot.Attachments,
            item => item.Id == fixture.Attachment.Id);
        Assert.Equal(viewport, attachment.Viewport);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
        Assert.Null(snapshot.InputLease);
    }

    [Fact]
    public async Task Unrelated_session_revision_during_resize_does_not_split_process_and_metadata()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var viewport = new ViewportDescriptor(800, 600, 2, 132, 43);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    viewport)));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockResizes = true;

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.ResizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        _ = await fixture.AcquireHumanLeaseAsync(fixture.ClientId);
        terminal.ReleaseResize.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        Assert.Equal(viewport, terminal.LastResizeViewport);
        Assert.Equal(
            viewport,
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
    }

    [Fact]
    public async Task Caller_cancellation_after_agent_resize_dispatch_still_commits_metadata()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var viewport = new ViewportDescriptor(800, 600, 2, 132, 43);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    viewport)));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockResizes = true;
        terminal.IgnoreResizeCancellationAfterStart = true;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            cancellation.Token).AsTask();
        await terminal.ResizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        terminal.ReleaseResize.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        Assert.Equal(viewport, terminal.LastResizeViewport);
        Assert.Equal(
            viewport,
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("ok", completion.StableCode);
    }

    [Fact]
    public async Task Caller_cancellation_after_human_resize_dispatch_still_commits_metadata()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var viewport = new ViewportDescriptor(1_024, 768, 2, 100, 30);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockResizes = true;
        terminal.IgnoreResizeCancellationAfterStart = true;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Client.ResizeTerminalAsync(
            new TerminalResizeRequest(
                fixture.SessionId,
                fixture.Attachment.Id,
                viewport),
            fixture.HumanContext(),
            cancellation.Token).AsTask();
        await terminal.ResizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        terminal.ReleaseResize.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        _ = result.Value();
        Assert.Equal(viewport, terminal.LastResizeViewport);
        Assert.Equal(
            viewport,
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
    }

    [Fact]
    public async Task Lost_resize_capability_denies_before_consuming_or_dispatching()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(800, 600, 2, 132, 43))));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.RemoveCapability(SessionCapabilities.TerminalResize);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(
            HostErrorCode.CapabilityNotSupported,
            result.Error().Code);
        Assert.Equal(0, terminal.ResizeCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        Assert.Equal(
            new ViewportDescriptor(800, 600, 2),
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
    }

    [Fact]
    public async Task Human_resize_rotates_authority_before_a_waiting_agent_can_dispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var agentViewport = new ViewportDescriptor(800, 600, 2, 132, 43);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    agentViewport)));
        var authorizationId = fixture.Authorization.Arm(action);
        var humanViewport = new ViewportDescriptor(1_024, 768, 2, 100, 30);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockResizes = true;

        var humanResize = fixture.Client.ResizeTerminalAsync(
            new TerminalResizeRequest(
                fixture.SessionId,
                fixture.Attachment.Id,
                humanViewport),
            fixture.HumanContext(),
            default).AsTask();
        await terminal.ResizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var agentResize = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.Equal(1, terminal.ResizeCount);

        terminal.ReleaseResize.TrySetResult();
        _ = (await humanResize.WaitAsync(TimeSpan.FromSeconds(1))).Value();
        var result = await agentResize.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("attachment_revoked", result.Error().StableCode);
        Assert.Equal(1, terminal.ResizeCount);
        Assert.Equal(humanViewport, terminal.LastResizeViewport);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("attachment_revoked", completion.StableCode);
        Assert.Equal(
            humanViewport,
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
    }

    [Fact]
    public async Task Revoking_resize_attachment_while_authorization_is_consumed_fails_closed()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(800, 600, 2, 132, 43))));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.BlockConsumes = true;
        var terminal = fixture.Factory[fixture.SessionId];

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        _ = (await fixture.Client.DetachAsync(
            new DetachSessionRequest(
                fixture.Attachment.Id,
                fixture.SessionId),
            fixture.HumanContext(),
            default)).Value();
        fixture.Authorization.ReleaseConsume.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("attachment_revoked", result.Error().StableCode);
        Assert.Equal(0, terminal.ResizeCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("attachment_revoked", completion.StableCode);
        Assert.Empty((await fixture.SnapshotAsync()).Attachments);
    }

    [Fact]
    public async Task Resize_denies_when_the_same_attachments_trusted_viewport_changed()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(800, 600, 2, 132, 43))));
        var authorizationId = fixture.Authorization.Arm(action);
        var humanViewport = new ViewportDescriptor(1_024, 768, 2, 100, 30);
        _ = (await fixture.Client.ResizeTerminalAsync(
            new TerminalResizeRequest(
                fixture.SessionId,
                fixture.Attachment.Id,
                humanViewport),
            fixture.HumanContext(),
            default)).Value();

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.NotFound, result.Error().Code);
        Assert.Equal(1, fixture.Factory[fixture.SessionId].ResizeCount);
        Assert.Equal(humanViewport, fixture.Factory[fixture.SessionId].LastResizeViewport);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        Assert.Equal(
            humanViewport,
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
    }

    [Fact]
    public async Task Resize_denies_an_attachment_owned_by_another_approving_client()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(800, 600, 2, 132, 43))));
        var authorizationId = fixture.Authorization.Arm(
            action,
            approvingClientId: new ClientId("other-client"));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].ResizeCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("invalid_request", completion.StableCode);
        Assert.Equal(
            new ViewportDescriptor(800, 600, 2),
            Assert.Single((await fixture.SnapshotAsync()).Attachments).Viewport);
    }

    [Fact]
    public async Task Changed_resize_material_cannot_use_the_approved_authorization()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var actionId = AgentActionId.New();
        var approved = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(800, 600, 2, 132, 43))),
            actionId);
        var changed = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(800, 600, 2, 133, 43))),
            actionId);
        var authorizationId = fixture.Authorization.Arm(approved);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            changed,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].ResizeCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        var attachment = Assert.Single((await fixture.SnapshotAsync()).Attachments);
        Assert.Equal(new ViewportDescriptor(800, 600, 2), attachment.Viewport);
    }

    [Fact]
    public async Task Unknown_resize_attachment_denies_without_inference_or_dispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    new AttachmentId("missing-attachment"),
                    new ViewportDescriptor(900, 500, 1, 90, 25))));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.NotFound, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].ResizeCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        var attachment = Assert.Single((await fixture.SnapshotAsync()).Attachments);
        Assert.Equal(fixture.Attachment.Id, attachment.Id);
        Assert.Equal(new ViewportDescriptor(800, 600, 2), attachment.Viewport);
    }

    [Fact]
    public async Task Multiple_attachments_do_not_make_resize_infer_an_interactive_target()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var firstReader = (await fixture.Client.AttachAsync(
            new AttachSessionRequest(
                fixture.SessionId,
                new ClientId("reader-1"),
                AttachmentKind.ReadOnly,
                new ViewportDescriptor(640, 480, 1),
                new CapabilitySet([SessionCapabilities.AttachRead])),
            fixture.HumanContext(new ClientId("reader-1")),
            default)).Value().Attachment;
        var secondReader = (await fixture.Client.AttachAsync(
            new AttachSessionRequest(
                fixture.SessionId,
                new ClientId("reader-2"),
                AttachmentKind.ReadOnly,
                new ViewportDescriptor(320, 240, 1),
                new CapabilitySet([SessionCapabilities.AttachRead])),
            fixture.HumanContext(new ClientId("reader-2")),
            default)).Value().Attachment;
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    secondReader.Id,
                    new ViewportDescriptor(1_024, 768, 2, 100, 30))));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.NotFound, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].ResizeCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        var attachments = (await fixture.SnapshotAsync()).Attachments;
        Assert.Equal(3, attachments.Count);
        Assert.Contains(attachments, item => item.Id == fixture.Attachment.Id);
        Assert.Contains(attachments, item => item.Id == firstReader.Id);
        Assert.Contains(attachments, item => item.Id == secondReader.Id);
    }

    [Fact]
    public async Task Replaced_resize_attachment_denies_before_consuming_or_dispatching()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    new ViewportDescriptor(1_024, 768, 2, 100, 30))));
        var authorizationId = fixture.Authorization.Arm(action);

        var replacement = await fixture.OpenActiveSessionAsync(fixture.SessionId);
        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.NotEqual(fixture.Attachment.Id, replacement.Id);
        Assert.Equal(HostErrorCode.NotFound, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].ResizeCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        var attachment = Assert.Single((await fixture.SnapshotAsync()).Attachments);
        Assert.Equal(replacement.Id, attachment.Id);
        Assert.Equal(new ViewportDescriptor(800, 600, 2), attachment.Viewport);
    }

    [Fact]
    public async Task Replacing_attachment_during_resize_cancels_one_dispatch_and_completes_once()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var viewport = new ViewportDescriptor(800, 600, 2, 160, 50);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Resize(
                new TerminalResizeRequest(
                    fixture.SessionId,
                    fixture.Attachment.Id,
                    viewport)));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockResizes = true;

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.ResizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var replacement = await fixture.OpenActiveSessionAsync(fixture.SessionId);
        terminal.ReleaseResize.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("attachment_revoked", result.Error().StableCode);
        Assert.Equal(1, terminal.ResizeCount);
        Assert.Equal(viewport, terminal.LastResizeViewport);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("attachment_revoked", completion.StableCode);
        var attachment = Assert.Single((await fixture.SnapshotAsync()).Attachments);
        Assert.Equal(replacement.Id, attachment.Id);
        Assert.Equal(new ViewportDescriptor(800, 600, 2), attachment.Viewport);
    }

    [Fact]
    public async Task Missing_mouse_input_barrier_denies_before_consuming_or_dispatching()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync(
            excludedCapability:
                SessionCapabilities.TerminalAgentInputBarrier);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                new TerminalMouseInput(
                    TerminalMouseButton.Middle,
                    TerminalMouseEventKind.Down,
                    Column: 4,
                    Row: 6),
                ExpectedContentRevision: 0));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(
            HostErrorCode.CapabilityNotSupported,
            result.Error().Code);
        Assert.Equal(0, terminal.MouseCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Lost_mouse_capability_denies_before_consuming_or_dispatching()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                new TerminalMouseInput(
                    TerminalMouseButton.Middle,
                    TerminalMouseEventKind.Down,
                    Column: 4,
                    Row: 6),
                ExpectedContentRevision: 0));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.RemoveCapability(SessionCapabilities.TerminalMouse);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(
            HostErrorCode.CapabilityNotSupported,
            result.Error().Code);
        Assert.Equal(0, terminal.MouseCount);
        Assert.Equal(0, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Human_lease_preemption_cancels_a_blocked_agent_write_and_completes_once()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "blocked write"));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockWrites = true;

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var humanLease = (await fixture.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                fixture.SessionId,
                fixture.Attachment.Id,
                TimeSpan.FromMinutes(1)),
            fixture.HumanContext(),
            default)).Value();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(humanLease.PreemptedAnotherHolder);
        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal(1, terminal.WriteCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("input_lease_revoked", completion.StableCode);
        Assert.Equal(humanLease.Lease!.Id, (await fixture.SnapshotAsync()).InputLease?.Id);
    }

    [Fact]
    public async Task Human_lease_preemption_cancels_blocked_agent_mouse_input_and_audits_the_cause()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var mouseInput = new TerminalMouseInput(
            TerminalMouseButton.Left,
            TerminalMouseEventKind.Drag,
            Column: 21,
            Row: 8,
            TerminalKeyModifiers.Alt);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendMouse(
                fixture.SessionId,
                mouseInput,
                ExpectedContentRevision: 0));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockMouse = true;

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.MouseStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var humanLease = (await fixture.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(
                fixture.SessionId,
                fixture.Attachment.Id,
                TimeSpan.FromMinutes(1)),
            fixture.HumanContext(),
            default)).Value();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(humanLease.PreemptedAnotherHolder);
        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("input_lease_revoked", result.Error().StableCode);
        Assert.Equal(1, terminal.MouseCount);
        Assert.Equal(mouseInput, terminal.LastMouseInput);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("input_lease_revoked", completion.StableCode);
        Assert.Equal(
            humanLease.Lease!.Id,
            (await fixture.SnapshotAsync()).InputLease?.Id);
    }

    [Fact]
    public async Task Screen_reads_and_waits_preserve_their_typed_results()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "deployment ready";
        terminal.ScreenContentRevision = 7;
        var read = await fixture.PrepareAsync(
            new AgentTerminalRequest.ReadScreen(fixture.SessionId));
        var readAuthorization = fixture.Authorization.Arm(read);

        var readResult = (AgentTerminalActionResult.Screen)(
            await fixture.Client.RunAgentTerminalActionAsync(
                readAuthorization,
                read,
                default)).Value();

        var wait = await fixture.PrepareAsync(
            new AgentTerminalRequest.WaitForText(
                new TerminalWaitForTextRequest(
                    fixture.SessionId,
                    new TerminalWaitForTextInput(
                        "ready",
                        TimeSpan.FromSeconds(1)))));
        var waitAuthorization = fixture.Authorization.Arm(wait);
        var waitResult = (AgentTerminalActionResult.Wait)(
            await fixture.Client.RunAgentTerminalActionAsync(
                waitAuthorization,
                wait,
                default)).Value();

        Assert.Equal("deployment ready", readResult.Snapshot.PlainText);
        Assert.Equal(7, readResult.Snapshot.ContentRevision);
        Assert.Equal(TerminalWaitOutcomeKind.Matched, waitResult.Outcome.Kind);
        Assert.Equal(7, waitResult.Outcome.ObservedContentRevision);
        Assert.Equal(2, fixture.Authorization.ConsumeCount);
        Assert.Collection(
            fixture.Authorization.Completions,
            completion => Assert.Equal("screen_read", completion.StableCode),
            completion => Assert.Equal("wait_matched", completion.StableCode));
    }

    [Fact]
    public async Task CommandFinishedWaitPreservesBaselineEventAndExitCode()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "command done";
        terminal.ScreenContentRevision = 9;
        terminal.SemanticWaitExitCode = 23;
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.WaitForCommandFinished(
                new TerminalWaitForCommandFinishedRequest(
                    fixture.SessionId,
                    new TerminalWaitForCommandFinishedInput(
                        AfterShellEventSequence: 7,
                        TimeSpan.FromSeconds(1)))));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        var wait = Assert.IsType<AgentTerminalActionResult.Wait>(result.Value());
        Assert.Equal(TerminalWaitOutcomeKind.CommandFinished, wait.Outcome.Kind);
        Assert.Equal(8, wait.Outcome.ObservedShellEvent?.Sequence);
        Assert.Equal(23, wait.Outcome.ObservedShellEvent?.ExitCode);
        Assert.Equal(7, terminal.LastCommandFinishedWait?.AfterShellEventSequence);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("wait_command_finished", completion.StableCode);
    }

    [Fact]
    public async Task SemanticWaitFailsClosedWhenShellEventSupportIsUnavailable()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.SupportsShellIntegrationEvents = false;
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.WaitForPromptReady(
                new TerminalWaitForPromptReadyRequest(
                    fixture.SessionId,
                    new TerminalWaitForPromptReadyInput(
                        AfterShellEventSequence: 0,
                        TimeSpan.FromSeconds(1)))));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.CapabilityNotSupported, result.Error().Code);
        Assert.Equal(
            "terminal_shell_integration_unavailable",
            result.Error().StableCode);
        Assert.Equal(0, terminal.LastPromptReadyWait?.AfterShellEventSequence);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal(
            "terminal_shell_integration_unavailable",
            completion.StableCode);
    }

    [Fact]
    public async Task CancelledWaitPreservesItsFinalFreshScreenWhileAuditingCancellation()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "final terminal state";
        terminal.ScreenContentRevision = 11;
        var finalScreen = await terminal.ReadScreenAsync(default);
        terminal.WaitOutcomeOverride = TerminalWaitOutcome.Cancelled(
            finalScreen,
            initialContentRevision: 7);
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.WaitForText(
                new TerminalWaitForTextRequest(
                    fixture.SessionId,
                    new TerminalWaitForTextInput(
                        "never",
                        TimeSpan.FromSeconds(1)))));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        var wait = Assert.IsType<AgentTerminalActionResult.Wait>(result.Value());
        Assert.Equal(TerminalWaitOutcomeKind.Cancelled, wait.Outcome.Kind);
        Assert.Same(finalScreen, wait.Outcome.Snapshot);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("operation_cancelled", completion.StableCode);
    }

    [Fact]
    public async Task HistoryProjectionViewportScrollAndDelayWaitDispatchTypedPorts()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "needle in history";
        terminal.ScreenContentRevision = 5;

        var readAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.ReadScrollback(
                fixture.SessionId,
                new TerminalScrollbackReadInput(
                    TerminalScrollbackReadOrigin.Bottom,
                    TerminalScrollbackReadInput.SmallRead)));
        var read = Assert.IsType<AgentTerminalActionResult.Scrollback>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(readAction),
                readAction,
                default)).Value());

        var findAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.FindScrollback(
                fixture.SessionId,
                new TerminalScrollbackFindInput(
                    "needle",
                    TerminalScrollbackFindDirection.Forward,
                    MaximumMatchCount: 4)));
        var find = Assert.IsType<AgentTerminalActionResult.Find>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(findAction),
                findAction,
                default)).Value());

        var scrollInput = new TerminalViewportScrollInput(
            TerminalViewportScrollDirection.Up,
            TerminalViewportScrollUnit.Page,
            Amount: 2);
        var scrollAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.ScrollViewport(
                fixture.SessionId,
                scrollInput));
        var scroll = Assert.IsType<AgentTerminalActionResult.Screen>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(scrollAction),
                scrollAction,
                default)).Value());

        var delayAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.WaitForDelay(
                new TerminalWaitForDelayRequest(
                    fixture.SessionId,
                    new TerminalWaitForDelayInput(TimeSpan.FromHours(1)))));
        var delay = Assert.IsType<AgentTerminalActionResult.Wait>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(delayAction),
                delayAction,
                default)).Value());

        Assert.Equal("needle in history", Assert.Single(read.Snapshot.Rows).Text);
        Assert.Equal(5, read.Snapshot.ContentRevision);
        Assert.Single(find.Result.Matches);
        Assert.Same(scrollInput, terminal.LastScrollInput);
        Assert.Equal(5, scroll.Snapshot.ContentRevision);
        Assert.Equal(TerminalWaitOutcomeKind.Elapsed, delay.Outcome.Kind);
        Assert.Equal(TimeSpan.FromHours(1), terminal.LastDelayWait?.Delay);
        Assert.Equal(4, fixture.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task RenderedHistorySearchAndJumpDispatchTypedPorts()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "offscreen TUIHISTORY_START row";
        terminal.ScreenContentRevision = 12;

        var findInput = new TerminalRenderedHistoryFindInput(
            "TUIHISTORY_START",
            TerminalScrollbackFindDirection.Backward,
            MaximumMatchCount: 4);
        var findAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.FindRenderedHistory(
                fixture.SessionId,
                findInput));
        var find = Assert.IsType<AgentTerminalActionResult.RenderedHistoryFind>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(findAction),
                findAction,
                default)).Value());
        var match = Assert.Single(find.Result.Matches);

        var jumpAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.JumpToRenderedHistory(
                fixture.SessionId,
                match.Anchor));
        var jump = Assert.IsType<AgentTerminalActionResult.Screen>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(jumpAction),
                jumpAction,
                default)).Value());

        Assert.Same(findInput, terminal.LastRenderedHistoryFind);
        Assert.Equal(match.Anchor, terminal.LastRenderedHistoryJump);
        Assert.Equal(12, jump.Snapshot.ContentRevision);
        Assert.Equal(2, fixture.Authorization.ConsumeCount);
    }

    [Fact]
    public async Task StaleRenderedHistoryJumpReturnsTypedRetryableFailure()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenContentRevision = 13;
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.JumpToRenderedHistory(
                fixture.SessionId,
                new TerminalRenderedHistoryRowAnchor(
                    ContentRevision: 12,
                    RowIndex: 2)));

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.Equal(HostErrorCode.RevisionConflict, result.Error().Code);
        Assert.Equal("terminal_rendered_history_anchor_stale", result.Error().StableCode);
        Assert.True(result.Error().Retryable);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("terminal_rendered_history_anchor_stale", completion.StableCode);
    }

    [Fact]
    public async Task Rendered_screen_diff_and_search_dispatch_as_observations()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.ScreenText = "header\nEDIT_PASS remains visible";
        terminal.ScreenContentRevision = 9;
        terminal.ScreenDiffResultOverride = new TerminalScreenDiffResult(
            InitialContentRevision: 8,
            CurrentContentRevision: 9,
            BaselineAvailable: true,
            [
                new TerminalScreenDiffResult.RowChange(
                    Row: 1,
                    Text: "EDIT_PASS remains visible",
                    IsWrapped: false,
                    IsTextTruncated: false),
            ],
            IsTruncated: false,
            CursorRow: 1,
            CursorColumn: 25,
            IsCursorVisible: true,
            InteractiveState: null);

        var diffInput = new TerminalScreenDiffInput(
            AfterContentRevision: 8,
            MaximumRowCount: 24);
        var diffAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.ReadScreenDiff(
                fixture.SessionId,
                diffInput));
        var diff = Assert.IsType<AgentTerminalActionResult.ScreenDiff>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(diffAction),
                diffAction,
                default)).Value());
        var findAction = await fixture.PrepareAsync(
            new AgentTerminalRequest.FindOnScreen(
                fixture.SessionId,
                new TerminalScreenFindInput(
                    "EDIT_PASS",
                    MaximumMatchCount: 4)));
        var find = Assert.IsType<AgentTerminalActionResult.ScreenFind>(
            (await fixture.Client.RunAgentTerminalActionAsync(
                fixture.Authorization.Arm(findAction),
                findAction,
                default)).Value());

        Assert.Same(diffInput, terminal.LastScreenDiffInput);
        Assert.Equal(
            "EDIT_PASS remains visible",
            Assert.Single(diff.Result.ChangedRows).Text);
        Assert.Equal(9, find.Result.ContentRevision);
        Assert.Equal(1, Assert.Single(find.Result.Matches).Line);
    }

    [Fact]
    public async Task Engine_failure_still_completes_the_consumed_action_exactly_once()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "fail after dispatch"));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockWrites = true;

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await terminal.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        terminal.ReleaseWrite.TrySetException(
            new InvalidOperationException("simulated terminal failure"));
        var result = await running;

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("engine_failed", completion.StableCode);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Caller_cancellation_after_dispatch_completes_the_action_exactly_once()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "cancel after dispatch"));
        var authorizationId = fixture.Authorization.Arm(action);
        var terminal = fixture.Factory[fixture.SessionId];
        terminal.BlockWrites = true;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            cancellation.Token).AsTask();
        await terminal.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        var result = await running;

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("caller_cancelled", result.Error().StableCode);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("caller_cancelled", completion.StableCode);
        Assert.False(fixture.Authorization.LastCompletionTokenWasCancelled);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Completion_audit_failure_still_releases_the_one_action_lease()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "audit failure"));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.CompletionFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "The completion audit failed.");

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Transient_completion_audit_failure_retries_without_redispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "audit retry"));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.NextCompletionFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "The first completion audit attempt failed.");

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.IsType<HostResult<AgentTerminalActionResult>.Success>(result);
        Assert.Equal(1, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
        Assert.Null((await fixture.SnapshotAsync()).InputLease);
    }

    [Fact]
    public async Task Authorization_started_failure_denies_before_engine_dispatch()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.SendText(
                fixture.SessionId,
                "must not execute"));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.ConsumeFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "The Started audit transition failed.");

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].WriteCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    internal async Task SecurityCampaignDispatchesInterruptExactlyOnceAsync()
    {
        await using var fixture = await AgentTerminalHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentTerminalRequest.Interrupt(fixture.SessionId));

        var forged = await fixture.Client.RunAgentTerminalActionAsync(
            AgentAuthorizationId.New(),
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, forged.Error().Code);
        Assert.Equal(0, fixture.Factory[fixture.SessionId].InterruptCount);

        var result = await fixture.Client.RunAgentTerminalActionAsync(
            fixture.Authorization.Arm(action),
            action,
            default);

        Assert.IsType<AgentTerminalActionResult.Completed>(result.Value());
        Assert.Equal(1, fixture.Factory[fixture.SessionId].InterruptCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    private sealed class AgentTerminalHostFixture : IAsyncDisposable
    {
        private AgentTerminalHostFixture(
            ManualTimeProvider? clock = null,
            IAgentAuthorizationConsumer? authorizationConsumer = null,
            string? excludedCapability = null)
        {
            Clock = clock ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            Factory = new FakeTerminalSessionFactory
            {
                ExcludedCapabilityForNewSessions = excludedCapability
            };
            Composer = new AgentTerminalActionComposer();
            Authorization = new FakeAuthorizationConsumer(Clock);
            Client = new InMemorySessionHostClient(
                Factory,
                new DesktopLifecyclePolicy(),
                Clock,
                agentActionComposer: Composer,
                agentAuthorizationConsumer:
                    authorizationConsumer ?? Authorization);
        }

        public ManualTimeProvider Clock { get; }

        public FakeTerminalSessionFactory Factory { get; }

        public AgentTerminalActionComposer Composer { get; }

        public FakeAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("test-client");

        public WindowInstanceId WindowId { get; } = new("window-1");

        public WorkspaceInstanceId WorkspaceId { get; } = new("workspace-1");

        public TabInstanceId TabId { get; } = new("tab-1");

        public PanelInstanceId PanelId { get; } = new("panel-1");

        public SessionId SessionId { get; } = new("session-1");

        public AgentRunId RunId { get; } = new("run-1");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("agent-1"),
            ActorKind.Agent,
            "Test agent");

        public AttachmentPresence Attachment { get; private set; } = null!;

        public static async ValueTask<AgentTerminalHostFixture> CreateAsync(
            ManualTimeProvider? clock = null,
            IAgentAuthorizationConsumer? authorizationConsumer = null,
            string? excludedCapability = null)
        {
            var fixture = new AgentTerminalHostFixture(
                clock,
                authorizationConsumer,
                excludedCapability);
            var panel = new PanelInstance(
                fixture.PanelId,
                PanelKind.Terminal,
                "Terminal");
            var tab = new TabInstance(
                fixture.TabId,
                "Primary",
                [panel],
                panel.Id);
            var workspace = new WorkspaceInstance(
                fixture.WorkspaceId,
                "Workspace",
                [tab],
                tab.Id);
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    workspace),
                fixture.HumanContext(),
                default)).Value();
            fixture.Attachment = await fixture.OpenActiveSessionAsync(
                fixture.SessionId);
            return fixture;
        }

        public async ValueTask<AttachmentPresence> OpenActiveSessionAsync(
            SessionId sessionId)
        {
            _ = (await Client.EnsureTerminalSessionAsync(
                new EnsureTerminalSessionRequest(
                    sessionId,
                    new SessionOwner(
                        HostMode.Desktop,
                        WindowId,
                        WorkspaceId,
                        TabId,
                        PanelId),
                    "test terminal",
                    new TerminalLaunchRequest("/tmp")),
                HumanContext(),
                default)).Value();
            var attachment = (await Client.AttachAsync(
                new AttachSessionRequest(
                    sessionId,
                    ClientId,
                    AttachmentKind.Interactive,
                    new ViewportDescriptor(800, 600, 2),
                    AllCapabilities()),
                HumanContext(),
                default)).Value().Attachment;
            _ = (await Client.AttachTerminalRendererAsync(
                new AttachTerminalRendererRequest(
                    sessionId,
                    attachment.Id,
                    new NativeRendererHost(
                        "test-renderer",
                        0,
                        new ViewportDescriptor(800, 600, 2))),
                HumanContext(),
                default)).Value();
            return attachment;
        }

        public async ValueTask<InputLease> AcquireHumanLeaseAsync(
            ClientId clientId)
        {
            var lease = (await Client.AcquireInputLeaseAsync(
                new AcquireInputLeaseRequest(
                    SessionId,
                    Attachment.Id,
                    TimeSpan.FromMinutes(5)),
                HumanContext(clientId),
                default)).Value();
            Assert.True(lease.Granted);
            return lease.Lease!;
        }

        public async ValueTask<InputLease> AcquireAgentLeaseAsync(
            ActorDescriptor agent)
        {
            var lease = (await Client.AcquireInputLeaseAsync(
                new AcquireInputLeaseRequest(
                    SessionId,
                    null,
                    TimeSpan.FromMinutes(5)),
                AgentContext(agent),
                default)).Value();
            Assert.True(lease.Granted);
            return lease.Lease!;
        }

        public async ValueTask<SessionSnapshot> SnapshotAsync() =>
            (await Client.GetSnapshotAsync(
                SessionId,
                HumanContext(),
                default)).Value();

        public async ValueTask<AgentTerminalAction> PrepareAsync(
            AgentTerminalRequest request,
            AgentActionId? actionId = null)
        {
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Panel(
                        WindowId,
                        WorkspaceId,
                        TabId,
                        PanelId)),
                AgentContext(),
                default)).Value();
            var now = Clock.GetUtcNow();
            return Composer.Prepare(
                new AgentActionEnvelope(
                    actionId ?? AgentActionId.New(),
                    RunId,
                    Agent,
                    policyGeneration: 0,
                    now,
                    now.AddMinutes(1)),
                context,
                request);
        }

        public OperationContext HumanContext(ClientId? clientId = null)
        {
            var authenticatedClientId = clientId ?? ClientId;
            return new(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(authenticatedClientId.Value),
                    ActorKind.Human,
                    "Test user",
                    authenticatedClientId),
                CancellationId: CancellationId.New());
        }

        private OperationContext AgentContext(
            ActorDescriptor? agent = null) =>
            new(
                RequestId.New(),
                agent ?? Agent,
                CancellationId: CancellationId.New());

        public ValueTask DisposeAsync() => Client.DisposeAsync();

        private static CapabilitySet AllCapabilities() => new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.AttachInteractive,
            SessionCapabilities.InputLease,
            SessionCapabilities.NativeRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalFocus,
            SessionCapabilities.TerminalMouse,
            SessionCapabilities.TerminalRevisionBoundMouse,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalResize,
            SessionCapabilities.TerminalWrite,
            SessionCapabilities.TerminalSendKeys,
            SessionCapabilities.TerminalSendChord,
            SessionCapabilities.TerminalEnter,
            SessionCapabilities.TerminalInterrupt,
            SessionCapabilities.TerminalWait,
        ]);
    }

    private sealed class InMemoryAuditStore : IAuditStore
    {
        private readonly object _gate = new();
        private readonly List<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public Func<AuditEventRecord, bool>? FailurePredicate { get; set; }

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            cancellationToken.ThrowIfCancellationRequested();
            if (FailurePredicate?.Invoke(auditEvent) == true)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<Unit>.Failure(
                        new AuditStoreError(
                            AuditStoreErrorCode.StorageUnavailable,
                            "The audit store is unavailable.")));
            }

            lock (_gate)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                        [.. _events
                            .Where(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal))]));
            }
        }
    }

    private sealed class FakeAuthorizationConsumer(TimeProvider timeProvider)
        : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentTerminalAction? _authorizedAction;
        private AgentAuthorizationId _authorizationId;
        private AgentAuthorizationSource _source;
        private ClientId _approvingClientId = new("test-client");
        private int _consumed;
        private int _consumeCount;

        public int ConsumeCount => Volatile.Read(ref _consumeCount);

        public AgentAuthorizationError? ConsumeFailure { get; set; }

        public AgentAuthorizationError? CompletionFailure { get; set; }

        public AgentAuthorizationError? NextCompletionFailure { get; set; }

        public bool BlockConsumes { get; set; }

        public TaskCompletionSource ConsumeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseConsume { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool LastCompletionTokenWasCancelled { get; private set; }

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(
            AgentTerminalAction action,
            AgentAuthorizationSource source = AgentAuthorizationSource.AutoPolicy,
            ClientId? approvingClientId = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            _authorizedAction = action;
            _authorizationId = AgentAuthorizationId.New();
            _source = source;
            _approvingClientId = approvingClientId ?? new ClientId("test-client");
            Volatile.Write(ref _consumed, 0);
            ConsumeFailure = null;
            CompletionFailure = null;
            NextCompletionFailure = null;
            return _authorizationId;
        }

        public async ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _consumeCount);
            ConsumeStarted.TrySetResult();
            if (BlockConsumes)
            {
                await ReleaseConsume.Task.WaitAsync(cancellationToken);
            }

            if (ConsumeFailure is { } failure)
            {
                return new AgentPermitResult.Denied(failure);
            }

            var action = _authorizedAction
                ?? throw new InvalidOperationException(
                    "An action must be armed before consuming authorization.");
            var expected = AgentActionExecutionBinding.FromProposal(
                action.Proposal);
            if (authorizationId != _authorizationId
                || !Matches(expected, currentBinding))
            {
                return Denied(
                    AgentAuthorizationErrorCode.AuthorizationMismatch,
                    "The execution binding differs from the approved action.");
            }

            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return Denied(
                    AgentAuthorizationErrorCode.AuthorizationNotFound,
                    "The one-action authorization has already been consumed.");
            }

            if (!BuiltInAgentTools.Catalog.TryGet(
                    action.Proposal.ToolName,
                    out var tool))
            {
                throw new InvalidOperationException(
                    "The prepared action is missing from the built-in catalog.");
            }

            var now = timeProvider.GetUtcNow();
            var authorization = new AgentActionAuthorization(
                authorizationId,
                action.Proposal,
                tool!,
                _source,
                _approvingClientId,
                now.AddMinutes(1));
            var permit = new AgentActionPermit(
                authorization,
                now,
                CancellationToken.None);
            return new AgentPermitResult.Granted(permit);
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(permit);
            ArgumentNullException.ThrowIfNull(completion);
            LastCompletionTokenWasCancelled = cancellationToken.IsCancellationRequested;
            _completions.Enqueue(completion);
            var nextFailure = NextCompletionFailure;
            NextCompletionFailure = null;
            return ValueTask.FromResult(nextFailure ?? CompletionFailure);
        }

        private static AgentPermitResult.Denied Denied(
            AgentAuthorizationErrorCode code,
            string message) =>
            new(new AgentAuthorizationError(code, message));

        private static bool Matches(
            AgentActionExecutionBinding expected,
            AgentActionExecutionBinding actual) =>
            expected.ActionId == actual.ActionId
            && expected.RunId == actual.RunId
            && expected.ActorId == actual.ActorId
            && string.Equals(
                expected.ToolName,
                actual.ToolName,
                StringComparison.Ordinal)
            && expected.Target == actual.Target
            && expected.TargetIdentity == actual.TargetIdentity
            && expected.TargetFingerprint == actual.TargetFingerprint
            && expected.ArgumentDigest == actual.ArgumentDigest
            && expected.PolicyGeneration == actual.PolicyGeneration;
    }
}
