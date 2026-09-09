using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class GovernedAgentCapabilityRequestTests
{
    private static readonly DateTimeOffset Expiry =
        new(2026, 7, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Request_preserves_only_trusted_bounded_metadata()
    {
        var target = Target();

        var request = Create(target: target);

        Assert.Equal(
            new AgentCapabilityRequestId("capability-request-1"),
            request.Id);
        Assert.Equal(new AgentRunId("run-1"), request.RunId);
        Assert.Equal(AgentCapability.ProcessControl, request.Capability);
        Assert.Equal("process_control", request.CapabilityToken);
        Assert.Equal("Process control", request.DisplayTitle);
        Assert.True(request.AffectedToolTitles.SequenceEqual(
            ["List processes", "Terminate process"]));
        Assert.Same(target, request.Target);
        Assert.Equal("Production terminal", request.TargetTitle);
        Assert.Equal(7, request.PolicyGeneration);
        Assert.Equal(Expiry, request.ExpiresAtUtc);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            GovernedAgentCapabilityRequest.DecisionLifetime);
    }

    [Fact]
    public void Request_copies_affected_tool_titles()
    {
        var titles = new[]
        {
            "List processes",
            "Terminate process",
        };

        var request = Create(affectedToolTitles: titles);
        titles[0] = "Changed after construction";

        Assert.True(request.AffectedToolTitles.SequenceEqual(
            ["List processes", "Terminate process"]));
        Assert.IsType<ImmutableArray<string>>(
            request.AffectedToolTitles);
    }

    [Fact]
    public void Request_rejects_invalid_identities_capability_and_generation()
    {
        var missingId = Assert.Throws<ArgumentException>(
            () => new GovernedAgentCapabilityRequest(
                default,
                new AgentRunId("run-1"),
                AgentCapability.ProcessControl,
                "Process control",
                ["List processes"],
                Target(),
                "Production terminal",
                7,
                Expiry));
        var missingRun = Assert.Throws<ArgumentException>(
            () => new GovernedAgentCapabilityRequest(
                new AgentCapabilityRequestId("capability-request-1"),
                default,
                AgentCapability.ProcessControl,
                "Process control",
                ["List processes"],
                Target(),
                "Production terminal",
                7,
                Expiry));
        var undefinedCapability = Assert.Throws<ArgumentOutOfRangeException>(
            () => Create(capability: (AgentCapability)int.MaxValue));
        var negativeGeneration = Assert.Throws<ArgumentOutOfRangeException>(
            () => Create(policyGeneration: -1));

        Assert.Equal("id", missingId.ParamName);
        Assert.Equal("runId", missingRun.ParamName);
        Assert.Equal("capability", undefinedCapability.ParamName);
        Assert.Equal("policyGeneration", negativeGeneration.ParamName);
    }

    [Fact]
    public void Request_rejects_an_invalid_unicode_request_identity()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new GovernedAgentCapabilityRequest(
                new AgentCapabilityRequestId(
                    string.Concat("request-", '\uD800')),
                new AgentRunId("run-1"),
                AgentCapability.ProcessControl,
                "Process control",
                ["List processes"],
                Target(),
                "Production terminal",
                7,
                Expiry));

        Assert.Equal("id", error.ParamName);
    }

    [Fact]
    public void Request_requires_an_exact_target_and_utc_expiry()
    {
        var missingTarget = Assert.Throws<ArgumentNullException>(
            () => new GovernedAgentCapabilityRequest(
                new AgentCapabilityRequestId("capability-request-1"),
                new AgentRunId("run-1"),
                AgentCapability.ProcessControl,
                "Process control",
                ["List processes"],
                null!,
                "Production terminal",
                7,
                Expiry));
        var localExpiry = Assert.Throws<ArgumentException>(
            () => Create(expiresAtUtc:
                Expiry.ToOffset(TimeSpan.FromHours(3))));

        Assert.Equal("target", missingTarget.ParamName);
        Assert.Equal("expiresAtUtc", localExpiry.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("first\nsecond")]
    [InlineData("bell\u0007")]
    [InlineData("hidden\u200Bformat")]
    public void Request_rejects_unsafe_display_titles(string? title)
    {
        var displayError = Assert.ThrowsAny<ArgumentException>(
            () => Create(displayTitle: title!));
        var targetError = Assert.ThrowsAny<ArgumentException>(
            () => Create(targetTitle: title!));

        Assert.Equal("displayTitle", displayError.ParamName);
        Assert.Equal("targetTitle", targetError.ParamName);
    }

    [Fact]
    public void Request_enforces_utf8_title_limits()
    {
        var exactDisplay = new string('\u00E9', 128);
        var exactTool = new string('\u00E9', 128);
        var exactTarget = new string('\u00E9', 256);

        var request = Create(
            displayTitle: exactDisplay,
            affectedToolTitles: [exactTool],
            targetTitle: exactTarget);

        Assert.Equal(exactDisplay, request.DisplayTitle);
        Assert.Equal(exactTool, request.AffectedToolTitles[0]);
        Assert.Equal(exactTarget, request.TargetTitle);
        Assert.Equal(
            "displayTitle",
            Assert.Throws<ArgumentException>(
                () => Create(displayTitle:
                    string.Concat(exactDisplay, "x"))).ParamName);
        Assert.Equal(
            "affectedToolTitles",
            Assert.Throws<ArgumentException>(
                () => Create(affectedToolTitles:
                    [string.Concat(exactTool, "x")])).ParamName);
        Assert.Equal(
            "targetTitle",
            Assert.Throws<ArgumentException>(
                () => Create(targetTitle:
                    string.Concat(exactTarget, "x"))).ParamName);
    }

    [Fact]
    public void Request_requires_distinct_nonempty_bounded_tool_titles()
    {
        Assert.Equal(
            "affectedToolTitles",
            Assert.Throws<ArgumentNullException>(
                () => new GovernedAgentCapabilityRequest(
                    new AgentCapabilityRequestId("capability-request-1"),
                    new AgentRunId("run-1"),
                    AgentCapability.ProcessControl,
                    "Process control",
                    null!,
                    Target(),
                    "Production terminal",
                    7,
                    Expiry)).ParamName);
        Assert.Equal(
            "affectedToolTitles",
            Assert.Throws<ArgumentException>(
                () => Create(affectedToolTitles: [])).ParamName);
        Assert.Equal(
            "affectedToolTitles",
            Assert.Throws<ArgumentException>(
                () => Create(affectedToolTitles:
                    ["List processes", "List processes"])).ParamName);
        Assert.Equal(
            "affectedToolTitles",
            Assert.ThrowsAny<ArgumentException>(
                () => Create(affectedToolTitles: ["first\nsecond"])).ParamName);
        Assert.Equal(
            "affectedToolTitles",
            Assert.Throws<ArgumentException>(
                () => Create(affectedToolTitles:
                    Enumerable.Repeat(
                        "tool",
                        GovernedAgentCapabilityRequest
                            .MaximumAffectedToolCount + 1)
                        .Select((title, index) => $"{title}-{index}")))
                .ParamName);
    }

    [Fact]
    public void Decision_is_a_closed_non_yolo_choice()
    {
        GovernedAgentCapabilityDecision allow =
            new GovernedAgentCapabilityDecision.AllowAsk();
        GovernedAgentCapabilityDecision keepOff =
            new GovernedAgentCapabilityDecision.KeepOff();

        Assert.IsType<GovernedAgentCapabilityDecision.AllowAsk>(allow);
        Assert.IsType<GovernedAgentCapabilityDecision.KeepOff>(keepOff);
        Assert.DoesNotContain(
            typeof(GovernedAgentCapabilityDecision).GetNestedTypes(),
            type => type.Name.Contains(
                "Yolo",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Request_capability_is_intrinsic_and_not_capability_catalogued()
    {
        Assert.Equal(
            "agent.request_capability",
            IntrinsicAgentTools.RequestCapability);
        Assert.False(BuiltInAgentTools.Catalog.TryGet(
            IntrinsicAgentTools.RequestCapability,
            out _));
        Assert.DoesNotContain(
            BuiltInAgentTools.Catalog.Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_exposes_one_pending_request_and_disables_send()
    {
        var request = Create();
        var snapshot = new GovernedAgentSnapshot(
            GovernedAgentState.AwaitingCapabilityDecision,
            RunId: request.RunId,
            ProviderId: null,
            Target: request.Target,
            TargetTitle: request.TargetTitle,
            ContextItems: [],
            Messages: [],
            EffectivePolicy: AgentPolicy.Default,
            ProvisionalAssistantText: string.Empty,
            Status: "Waiting",
            PendingCapabilityRequest: request);

        Assert.Same(request, snapshot.PendingCapabilityRequest);
        Assert.True(snapshot.IsBusy);
        Assert.False(snapshot.CanSend);
    }

    [Fact]
    public void A_pending_request_defensively_blocks_send_even_in_ready_state()
    {
        var request = Create();
        var snapshot = new GovernedAgentSnapshot(
            GovernedAgentState.Ready,
            RunId: request.RunId,
            ProviderId: null,
            Target: request.Target,
            TargetTitle: request.TargetTitle,
            ContextItems: [],
            Messages: [],
            EffectivePolicy: AgentPolicy.Default,
            ProvisionalAssistantText: string.Empty,
            Status: "Ready",
            PendingCapabilityRequest: request);

        Assert.False(snapshot.IsBusy);
        Assert.False(snapshot.CanSend);
    }

    private static GovernedAgentCapabilityRequest Create(
        AgentCapabilityRequestId? id = null,
        AgentRunId? runId = null,
        AgentCapability capability = AgentCapability.ProcessControl,
        string displayTitle = "Process control",
        IEnumerable<string>? affectedToolTitles = null,
        AgentTarget? target = null,
        string targetTitle = "Production terminal",
        long policyGeneration = 7,
        DateTimeOffset? expiresAtUtc = null) =>
        new(
            id ?? new AgentCapabilityRequestId("capability-request-1"),
            runId ?? new AgentRunId("run-1"),
            capability,
            displayTitle,
            affectedToolTitles ??
            [
                "List processes",
                "Terminate process",
            ],
            target ?? Target(),
            targetTitle,
            policyGeneration,
            expiresAtUtc ?? Expiry);

    private static AgentTarget Target() =>
        new AgentTarget.ConnectionSession(new SessionId("session-1"));
}
