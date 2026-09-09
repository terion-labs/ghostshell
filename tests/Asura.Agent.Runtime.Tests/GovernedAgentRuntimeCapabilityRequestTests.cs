using System.Collections.Immutable;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task CapabilityRequestSchemaContainsOnlyActualOffCapabilities()
    {
        var provider = new ProviderRound((_, _) => Answer("Done."));
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Prepare the terminal."),
            CancellationToken.None)).IsSuccess);

        var request = Assert.Single(provider.Requests);
        var intrinsic = Assert.Single(
            request.Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
        var schema = intrinsic.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["capability"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["capability"],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            [AgentCapabilityProtocol.RunCommands],
            properties.GetProperty("capability")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        Assert.Contains(
            request.Tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalSendText, StringComparison.Ordinal));
        Assert.DoesNotContain(
            AgentCapabilityProtocol.ProcessControl,
            schema.GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityRequestIsOmittedWithoutAnActuallyAdvertisedOffTool()
    {
        var provider = new ProviderRound((_, _) => Answer("Done."));
        await using var fixture = new RuntimeFixture(provider);

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the terminal."),
            CancellationToken.None)).IsSuccess);

        Assert.DoesNotContain(
            Assert.Single(provider.Requests).Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"capability":null}""")]
    [InlineData("""{"capability":"RunCommands"}""")]
    [InlineData("""{"capability":"run_commands","reason":"please"}""")]
    [InlineData("""{"capability":"run_commands","capability":"run_commands"}""")]
    public void CapabilityRequestParserRejectsAnythingButOneStableCandidateToken(
        string json)
    {
        using var document = JsonDocument.Parse(json);

        var result = AgentRequestCapabilityIntrinsic.Parse(
            document.RootElement,
            ImmutableHashSet.Create(AgentCapability.RunCommands));

        Assert.IsType<AgentRequestCapabilityParseResult.Rejected>(result);
    }

    [Fact]
    public void StableButUnadvertisedCapabilityTokenIsUnavailable()
    {
        using var document = JsonDocument.Parse(
            """{"capability":"process_control"}""");

        var result = AgentRequestCapabilityIntrinsic.Parse(
            document.RootElement,
            ImmutableHashSet.Create(AgentCapability.RunCommands));

        Assert.IsType<AgentRequestCapabilityParseResult.Unavailable>(result);
    }

    [Fact(DisplayName = "lifecycle.capability-request-correlation closes request before ordinary approval")]
    [Trait("SecurityCampaignCase", "lifecycle.capability-request-correlation")]
    public async Task AllowAskCommitsAuditedRunPolicyBeforeOrdinaryActionApproval()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "request-run-commands",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}"""),
            2 => ToolCall(
                "send-after-grant",
                BuiltInAgentTools.TerminalSendText,
                """{"text":"status"}"""),
            3 => Answer("Status was requested."),
            _ => throw new InvalidOperationException(
                "The capability provider received an unexpected round."),
        })
        {
            BlockOnCall = 2,
        };
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));
        fixture.Context.PanelTitle = "password=secret-canary";

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        var baselinePolicy = fixture.Runtime.Snapshot.EffectivePolicy!;

        Assert.Equal(AgentCapability.RunCommands, pending.Capability);
        Assert.Equal(AgentCapabilityProtocol.RunCommands, pending.CapabilityToken);
        Assert.Equal("Terminal commands", pending.DisplayTitle);
        Assert.Equal("Terminal", pending.TargetTitle);
        Assert.DoesNotContain(
            "secret-canary",
            JsonSerializer.Serialize(pending),
            StringComparison.Ordinal);
        Assert.Contains("Send terminal text", pending.AffectedToolTitles, StringComparer.Ordinal);
        Assert.Equal(1, pending.PolicyGeneration);
        Assert.Equal(
            pending.ExpiresAtUtc,
            pending.ExpiresAtUtc.ToUniversalTime());

        var decision = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);
        Assert.True(decision.IsAccepted);
        Assert.Equal("capability_request_allowed", decision.Code);
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            AgentPermission.Ask,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        var grantedPolicy = fixture.Runtime.Snapshot.EffectivePolicy!;
        Assert.Equal(baselinePolicy.Provider, grantedPolicy.Provider);
        Assert.Equal(baselinePolicy.Model, grantedPolicy.Model);
        Assert.All(
            AgentPolicy.Capabilities.Where(capability =>
                capability != AgentCapability.RunCommands),
            capability => Assert.Equal(
                baselinePolicy.GetPermission(capability),
                grantedPolicy.GetPermission(capability)));
        var policyAudit = Assert.Single(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action, "agent.run.policy", StringComparison.Ordinal));
        Assert.Equal(AuditOutcome.Succeeded, policyAudit.Outcome);
        var policyDetails = Assert.IsType<AuditDetails.AgentRunPolicyTransitionDetails>(
            policyAudit.Details);
        Assert.Equal(pending.Id, policyDetails.CapabilityRequestId);
        var capabilityEvents = fixture.Audit.Events
            .Where(auditEvent => string.Equals(
                auditEvent.Action,
                "agent.capability.request",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, capabilityEvents.Length);
        Assert.All(
            capabilityEvents,
            auditEvent => Assert.Equal(pending.Id.Value, auditEvent.CorrelationId));
        Assert.Equal(AuditOutcome.Requested, capabilityEvents[0].Outcome);
        Assert.Equal(AuditOutcome.Approved, capabilityEvents[1].Outcome);
        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action
, BuiltInAgentTools.TerminalSendText, StringComparison.Ordinal));
        Assert.Empty(fixture.Terminal.Actions);

        var receipt = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(message.ToolResult?.ProviderCallId
, "request-run-commands", StringComparison.Ordinal)).ToolResult!;
        Assert.Equal(AgentToolResultStatus.Succeeded, receipt.Status);
        Assert.Equal(
            """{"ok":true,"capability":"run_commands","permission":"ask","scope":"run","action_approval_required":true}""",
            receipt.Value.Content);
        Assert.DoesNotContain(
            provider.Requests.ToArray()[1].Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));

        provider.ReleaseBlockedCall.TrySetResult();
        await WaitUntilAsync(() =>
            fixture.Runtime.Snapshot.PendingApproval is not null
            || sending.IsCompleted);
        Assert.False(
            sending.IsCompleted,
            $"Turn ended before ordinary approval: {fixture.Runtime.Snapshot.State} · "
            + fixture.Runtime.Snapshot.Status
            + " · tool="
            + (provider.Requests.Count >= 3
                ? ToolResultForCall(
                    provider,
                    "send-after-grant").StableCode
                : "<no-result>"));
        var approval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.Equal(BuiltInAgentTools.TerminalSendText, approval.ToolName);
        Assert.Equal(AgentPermission.Ask, approval.Permission);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Single(fixture.Terminal.Actions);
    }

    [Fact]
    public async Task KeepOffReturnsFixedFailureAndAllowsNoActionAuthority()
    {
        var provider = CapabilityRequestThenAnswerProvider(
            "keep-off",
            "The capability remained disabled.");
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        var decision = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.KeepOff(),
            CancellationToken.None);

        Assert.True(decision.IsAccepted);
        Assert.Equal("capability_request_denied", decision.Code);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        var result = ToolResultForCall(provider, "keep-off");
        Assert.Equal(AgentToolResultStatus.Failed, result.Status);
        Assert.Equal("capability_request_denied", result.StableCode);
        Assert.Equal(
            """{"ok":false,"error":{"code":"capability_request_denied","retryable":false}}""",
            result.Value.Content);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Equal(2, fixture.Audit.Events.Count);
        Assert.Equal(AuditOutcome.Requested, fixture.Audit.Events[0].Outcome);
        Assert.Equal(AuditOutcome.Denied, fixture.Audit.Events[1].Outcome);
    }

    [Fact]
    public async Task ExpiredCapabilityRequestReturnsFixedFailureWithoutAuthority()
    {
        var time = new ManualQuestionTimeProvider(QuestionTestNow);
        var provider = CapabilityRequestThenAnswerProvider(
            "expired-capability",
            "The capability request expired.");
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)),
            time);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        await WaitUntilAsync(() => time.ActiveTimerCount > 0);
        time.Advance(GovernedAgentCapabilityRequest.DecisionLifetime);

        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(
            QuestionTestNow + GovernedAgentCapabilityRequest.DecisionLifetime,
            pending.ExpiresAtUtc);
        var result = ToolResultForCall(provider, "expired-capability");
        Assert.Equal("capability_request_expired", result.StableCode);
        Assert.Equal(
            """{"ok":false,"error":{"code":"capability_request_expired","retryable":false}}""",
            result.Value.Content);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Equal(2, fixture.Audit.Events.Count);
        Assert.Equal(AuditOutcome.Requested, fixture.Audit.Events[0].Outcome);
        Assert.Equal(AuditOutcome.Cancelled, fixture.Audit.Events[1].Outcome);
        Assert.Equal(
            AgentCapabilityRequestAuditDecision.Expired,
            Assert.IsType<AuditDetails.AgentCapabilityRequestDetails>(
                fixture.Audit.Events[1].Details).Decision);
        var late = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);
        Assert.False(late.IsAccepted);
        Assert.Equal("capability_request_not_found", late.Code);
    }

    [Fact]
    public async Task OneTopLevelTurnAcceptsOnlyOneCapabilityDecision()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "first-capability-request",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}"""),
            2 => ToolCall(
                "second-capability-request",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}"""),
            3 => Answer("No capability was enabled."),
            _ => throw new InvalidOperationException(
                "The request-limit provider received an unexpected round."),
        });
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        Assert.True((await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.KeepOff(),
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        Assert.Equal(
            "capability_request_limit_reached",
            ToolResultForCall(
                provider,
                "second-capability-request").StableCode);
        Assert.Equal(
            "capability_request_denied",
            ToolResultForCall(
                provider,
                "first-capability-request").StableCode);
        Assert.Equal(2, fixture.Audit.Events.Count);
        Assert.Equal(AuditOutcome.Requested, fixture.Audit.Events[0].Outcome);
        Assert.Equal(AuditOutcome.Denied, fixture.Audit.Events[1].Outcome);
    }

    [Fact]
    public async Task TargetOrAdvertisedToolDriftDiscardsTheHumanDecision()
    {
        var provider = CapabilityRequestThenAnswerProvider(
            "tool-drift",
            "The request was discarded.");
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        fixture.Context.Capabilities = new CapabilitySet(
        [
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalAgentInputBarrier,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
            SessionCapabilities.TerminalWrite,
        ]);

        var decision = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);

        Assert.False(decision.IsAccepted);
        Assert.Equal("capability_request_unavailable", decision.Code);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(
            "capability_request_unavailable",
            ToolResultForCall(provider, "tool-drift").StableCode);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Failed],
            fixture.Audit.Events.Select(item => item.Outcome));
        Assert.Equal(
            AgentCapabilityRequestAuditDecision.CapabilityUnavailable,
            Assert.IsType<AuditDetails.AgentCapabilityRequestDetails>(
                fixture.Audit.Events[1].Details).Decision);
    }

    [Fact]
    public async Task PinnedTargetDriftAfterDecisionPreventsPolicyUpdate()
    {
        var provider = CapabilityRequestThenAnswerProvider(
            "target-drift",
            "The target changed.");
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));
        fixture.Context.ReplaceSessionAfterInspection = 2;

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        var decision = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);

        Assert.False(decision.IsAccepted);
        Assert.Equal("target_changed", decision.Code);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(
            "target_changed",
            ToolResultForCall(provider, "target-drift").StableCode);
        Assert.Equal(
            [AuditOutcome.Requested, AuditOutcome.Failed],
            fixture.Audit.Events.Select(item => item.Outcome));
        Assert.Equal(
            AgentCapabilityRequestAuditDecision.TargetChanged,
            Assert.IsType<AuditDetails.AgentCapabilityRequestDetails>(
                fixture.Audit.Events[1].Details).Decision);
    }

    [Fact]
    public async Task PolicyAuditFailureQuarantinesWithoutProviderReceipt()
    {
        var provider = new ProviderRound((call, _) => call == 1
            ? ToolCall(
                "audit-failure",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}""")
            : throw new InvalidOperationException(
                "A quarantined request must not continue the provider."));
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));
        fixture.Audit.FailurePredicate = auditEvent => string.Equals(auditEvent.Action, "agent.run.policy", StringComparison.Ordinal);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        var decision = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(decision.IsAccepted);
        Assert.Equal("audit_unavailable", decision.Code);
        Assert.False(result.IsSuccess);
        Assert.Equal(GovernedAgentState.Cancelled, fixture.Runtime.Snapshot.State);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Terminal.Actions);
    }

    [Fact]
    public async Task RunGrantSurvivesPromptsAndYoloButStopAndClearDiscardIt()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ToolCall(
                "persistent-grant",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}"""),
            2 or 3 => Answer("Done."),
            4 => ToolCall(
                "request-after-clear",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}"""),
            5 => Answer("Kept off."),
            _ => throw new InvalidOperationException(
                "The persistence provider received an unexpected round."),
        });
        var baseline = CapabilityPolicy(
            (AgentCapability.RunCommands, AgentPermission.Off));
        await using var fixture = new RuntimeFixture(provider, baseline);

        var first = fixture.Runtime.SendAsync(
            fixture.Prompt("First turn."),
            CancellationToken.None).AsTask();
        var request = await WaitForCapabilityRequestAsync(fixture.Runtime);
        Assert.True((await fixture.Runtime.DecideCapabilityRequestAsync(
            request.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None)).IsAccepted);
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Second turn."),
            CancellationToken.None)).IsSuccess);
        Assert.Contains(
            provider.Requests.ToArray()[2].Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
        Assert.Equal(
            AgentPermission.Ask,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));

        Assert.True((await fixture.Runtime.EnableYoloAsync(
            TimeSpan.FromMinutes(1),
            CancellationToken.None)).IsAccepted);
        Assert.Equal(
            AgentPermission.Yolo,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.True((await fixture.Runtime.DisableYoloAsync(
            CancellationToken.None)).IsAccepted);
        Assert.Equal(
            AgentPermission.Ask,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));

        Assert.True((await fixture.Runtime.StopAsync(
            CancellationToken.None)).WasRunning);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.True(await fixture.Runtime.ClearAsync(CancellationToken.None));

        var afterClear = fixture.Runtime.SendAsync(
            fixture.Prompt("After clear."),
            CancellationToken.None).AsTask();
        var nextRequest = await WaitForCapabilityRequestAsync(fixture.Runtime);
        Assert.True((await fixture.Runtime.DecideCapabilityRequestAsync(
            nextRequest.Id,
            new GovernedAgentCapabilityDecision.KeepOff(),
            CancellationToken.None)).IsAccepted);
        Assert.True((await afterClear.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Contains(
            provider.Requests.ToArray()[3].Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
    }

    [Fact]
    public async Task YoloChangesAuthorityWithoutChangingTheRunToolManifest()
    {
        var provider = new ProviderRound((_, _) => Answer("Done."));
        var baseline = CapabilityPolicy(
            (AgentCapability.TerminalRead, AgentPermission.Off));
        await using var fixture = new RuntimeFixture(provider, baseline);

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Bind the run."),
            CancellationToken.None)).IsSuccess);
        Assert.Contains(
            provider.Requests.ToArray()[0].Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
        Assert.True((await fixture.Runtime.EnableYoloAsync(
            TimeSpan.FromMinutes(1),
            CancellationToken.None)).IsAccepted);

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("YOLO turn."),
            CancellationToken.None)).IsSuccess);
        var initialSchema = provider.Requests.ToArray()[0].Tools
            .Single(tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal))
            .InputSchema.GetRawText();
        Assert.Equal(
            initialSchema,
            provider.Requests.ToArray()[1].Tools
                .Single(tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal))
                .InputSchema.GetRawText());
        Assert.True((await fixture.Runtime.DisableYoloAsync(
            CancellationToken.None)).IsAccepted);

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Restored turn."),
            CancellationToken.None)).IsSuccess);
        Assert.Contains(
            provider.Requests.ToArray()[2].Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
        Assert.Equal(
            initialSchema,
            provider.Requests.ToArray()[2].Tools
                .Single(tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal))
                .InputSchema.GetRawText());
    }

    [Fact]
    public async Task StopCompletesPendingCapabilityWaitersAndDiscardsRunGrant()
    {
        var provider = new ProviderRound((call, _) => call == 1
            ? ToolCall(
                "stop-pending-request",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}""")
            : throw new InvalidOperationException(
                "A stopped request must not continue the provider."));
        await using var fixture = new RuntimeFixture(
            provider,
            CapabilityPolicy(
                (AgentCapability.RunCommands, AgentPermission.Off)));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        Assert.True((await fixture.Runtime.StopAsync(
            CancellationToken.None)).WasRunning);
        Assert.False((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);

        var stale = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);
        Assert.False(stale.IsAccepted);
        Assert.Equal("capability_request_not_found", stale.Code);
        Assert.Null(fixture.Runtime.Snapshot.PendingCapabilityRequest);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleWinPreventsLatePolicyUpdatePublication(
        bool dispose)
    {
        var provider = new ProviderRound((call, _) => call == 1
            ? ToolCall(
                "held-policy-update",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}""")
            : throw new InvalidOperationException(
                "A lifecycle-cancelled request must not continue."));
        await using var fixture = new CapabilityRaceFixture(
            provider,
            applyUpdateToInnerBroker: false);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        var deciding = fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None).AsTask();
        await fixture.Broker.UpdateEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        if (dispose)
        {
            await fixture.Runtime.DisposeAsync();
        }
        else
        {
            Assert.True((await fixture.Runtime.StopAsync(
                CancellationToken.None)).WasRunning);
        }

        fixture.Broker.ReleaseUpdate.TrySetResult();
        var decision = await deciding.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(decision.IsAccepted);
        Assert.Equal(
            "capability_request_cancelled",
            decision.Code);
        Assert.False((await sending.WaitAsync(
            TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(GovernedAgentState.Cancelled, fixture.Runtime.Snapshot.State);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.TerminalMutationPermission);
        Assert.Null(fixture.Runtime.Snapshot.YoloAuthority);
        Assert.Null(fixture.Runtime.Snapshot.PendingCapabilityRequest);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action, "agent.run.policy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellationAfterDecisionClaimCannotReopenRequest()
    {
        var provider = CapabilityRequestThenAnswerProvider(
            "caller-cancellation",
            "The run capability was updated.");
        await using var fixture = new CapabilityRaceFixture(
            provider,
            applyUpdateToInnerBroker: true);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        using var cancelledBeforeClaim = new CancellationTokenSource();
        cancelledBeforeClaim.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Runtime.DecideCapabilityRequestAsync(
                pending.Id,
                new GovernedAgentCapabilityDecision.AllowAsk(),
                cancelledBeforeClaim.Token).AsTask());
        Assert.Equal(
            pending.Id,
            fixture.Runtime.Snapshot.PendingCapabilityRequest?.Id);
        Assert.False(fixture.Broker.UpdateEntered.Task.IsCompleted);

        using var cancelledAfterClaim = new CancellationTokenSource();
        var deciding = fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            cancelledAfterClaim.Token).AsTask();
        await fixture.Broker.UpdateEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        cancelledAfterClaim.Cancel();
        fixture.Broker.ReleaseUpdate.TrySetResult();

        var decision = await deciding.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(decision.IsAccepted);
        Assert.Equal("capability_request_allowed", decision.Code);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(
            AgentPermission.Ask,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        var staleRetry = await fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None);
        Assert.False(staleRetry.IsAccepted);
        Assert.Equal("capability_request_not_found", staleRetry.Code);
    }

    [Fact]
    public async Task BrokerGenerationDriftAfterClaimFailsClosed()
    {
        var provider = new ProviderRound((call, _) => call == 1
            ? ToolCall(
                "generation-drift",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}""")
            : throw new InvalidOperationException(
                "A stale policy decision must not continue."));
        await using var fixture = new CapabilityRaceFixture(
            provider,
            applyUpdateToInnerBroker: true);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run status."),
            CancellationToken.None).AsTask();
        var pending = await WaitForCapabilityRequestAsync(fixture.Runtime);
        var deciding = fixture.Runtime.DecideCapabilityRequestAsync(
            pending.Id,
            new GovernedAgentCapabilityDecision.AllowAsk(),
            CancellationToken.None).AsTask();
        await fixture.Broker.UpdateEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        var driftedPolicy = fixture.Runtime.Snapshot.EffectivePolicy! with
        {
            Permissions = fixture.Runtime.Snapshot.EffectivePolicy!.Permissions
                .SetItem(AgentCapability.Search, AgentPermission.Ask),
        };
        var actor = new ActorDescriptor(
            new ActorId(fixture.ClientId.Value),
            ActorKind.Human,
            "Test user",
            fixture.ClientId);
        Assert.Null(await fixture.InnerBroker.UpdateRunPolicyAsync(
            new AgentRunPolicyUpdate(
                pending.RunId,
                driftedPolicy,
                pending.PolicyGeneration + 1,
                actor),
            CancellationToken.None));
        fixture.Broker.ReleaseUpdate.TrySetResult();

        var decision = await deciding.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(decision.IsAccepted);
        Assert.Equal("policy_changed", decision.Code);
        Assert.False((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(GovernedAgentState.Cancelled, fixture.Runtime.Snapshot.State);
        Assert.Equal(
            AgentPermission.Off,
            fixture.Runtime.Snapshot.EffectivePolicy!
                .GetPermission(AgentCapability.RunCommands));
        Assert.Single(provider.Requests);
    }

    private static AgentPolicy CapabilityPolicy(
        params (AgentCapability Capability, AgentPermission Permission)[] values)
    {
        var permissions = AgentPolicy.Default.Permissions;
        foreach (var (capability, permission) in values)
        {
            permissions = permissions.SetItem(capability, permission);
        }

        return AgentPolicy.Default with
        {
            Permissions = permissions,
        };
    }

    private static ProviderRound CapabilityRequestThenAnswerProvider(
        string callId,
        string answer) =>
        new((call, _) => call switch
        {
            1 => ToolCall(
                callId,
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"run_commands"}"""),
            2 => Answer(answer),
            _ => throw new InvalidOperationException(
                "The capability provider received an unexpected round."),
        });

    private static async ValueTask<GovernedAgentCapabilityRequest>
        WaitForCapabilityRequestAsync(GovernedAgentRuntime runtime)
    {
        GovernedAgentCapabilityRequest? request = null;
        await WaitUntilAsync(
            () =>
            {
                request = runtime.Snapshot.PendingCapabilityRequest;
                return runtime.Snapshot.State
                        == GovernedAgentState.AwaitingCapabilityDecision
                    && request is not null;
            });
        return request!;
    }

    private static AgentToolResult ToolResultForCall(
        ProviderRound provider,
        string providerCallId) =>
        provider.Requests
            .SelectMany(request => request.Messages)
            .Where(message => message.Role == AgentMessageRole.Tool)
            .Select(message => message.ToolResult)
            .OfType<AgentToolResult>()
            .Last(result => string.Equals(result.ProviderCallId, providerCallId, StringComparison.Ordinal));

    private sealed class CapabilityRaceFixture : IAsyncDisposable
    {
        public CapabilityRaceFixture(
            ProviderRound provider,
            bool applyUpdateToInnerBroker)
        {
            Context = new ContextClient();
            ClientId = new ClientId("desktop-client");
            Audit = new RecordingAuditStore();
            InnerBroker = new AgentCapabilityBroker(
                BuiltInAgentTools.Catalog,
                Audit,
                TimeProvider.System);
            Broker = new GatePolicyUpdateBroker(
                InnerBroker,
                applyUpdateToInnerBroker);
            var composer = new AgentTerminalActionComposer();
            Terminal = new ConsumingTerminalHost(
                Broker,
                composer,
                Context);
            Runtime = new GovernedAgentRuntime(
                Context,
                Broker,
                Terminal,
                agentBrowserHost: null,
                composer,
                browserComposer: null,
                BuiltInAgentTools.Catalog,
                new FixedProviderResolver(provider),
                new TestApprovalPrincipal(ClientId),
                TimeProvider.System,
                CapabilityPolicy(
                    (AgentCapability.RunCommands, AgentPermission.Off)));
        }

        public ContextClient Context { get; }

        public ClientId ClientId { get; }

        public RecordingAuditStore Audit { get; }

        public AgentCapabilityBroker InnerBroker { get; }

        public GatePolicyUpdateBroker Broker { get; }

        public ConsumingTerminalHost Terminal { get; }

        public GovernedAgentRuntime Runtime { get; }

        public GovernedAgentPrompt Prompt(string message) =>
            new(
                new AiProviderProfileId("provider-1"),
                message,
                Context.Target,
                Runtime.Snapshot.EffectivePolicy!.SelectPrimaryModel(
                    "provider-1",
                    "provider-default-model"));

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await InnerBroker.DisposeAsync();
            Context.DisposeCancellationRegistration();
        }
    }

    private sealed class GatePolicyUpdateBroker(
        IAgentCapabilityBroker inner,
        bool applyUpdateToInnerBroker)
        : IAgentCapabilityBroker
    {
        public TaskCompletionSource UpdateEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseUpdate { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AgentAuthorizationError?> RegisterRunAsync(
            AgentRunRegistration registration,
            CancellationToken cancellationToken) =>
            inner.RegisterRunAsync(registration, cancellationToken);

        public async ValueTask<AgentAuthorizationError?> UpdateRunPolicyAsync(
            AgentRunPolicyUpdate update,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            UpdateEntered.TrySetResult();
            await ReleaseUpdate.Task.ConfigureAwait(false);
            return applyUpdateToInnerBroker
                ? await inner.UpdateRunPolicyAsync(
                    update,
                    CancellationToken.None)
                : null;
        }

        public ValueTask<AgentAuthorizationError?> RecordCapabilityRequestAuditAsync(
            AgentCapabilityRequestAuditEvent auditEvent,
            CancellationToken cancellationToken) =>
            inner.RecordCapabilityRequestAuditAsync(auditEvent, cancellationToken);

        public ValueTask<AgentAuthorizationError?> CancelRunAsync(
            AgentRunCancellation cancellation,
            CancellationToken cancellationToken) =>
            inner.CancelRunAsync(cancellation, cancellationToken);

        public ValueTask<AgentAuthorizationResult> RequestAsync(
            AgentActionProposal proposal,
            CancellationToken cancellationToken) =>
            inner.RequestAsync(proposal, cancellationToken);

        public ValueTask<AgentAuthorizationResult> DecideAsync(
            AgentApprovalDecision decision,
            CancellationToken cancellationToken) =>
            inner.DecideAsync(decision, cancellationToken);

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken) =>
            inner.ConsumeAsync(
                authorizationId,
                currentBinding,
                cancellationToken);

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken) =>
            inner.CompleteAsync(
                permit,
                completion,
                cancellationToken);
    }
}
