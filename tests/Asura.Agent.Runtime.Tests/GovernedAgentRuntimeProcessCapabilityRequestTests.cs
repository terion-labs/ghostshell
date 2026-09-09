using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeProcessTests
{
    [Fact]
    public async Task OffProcessCapabilityRequiresGrantThenSeparateActionApproval()
    {
        var provider = new ScriptedProvider((call, request) => call switch
        {
            1 => CapabilityToolCall(
                "process-capability",
                IntrinsicAgentTools.RequestCapability,
                """{"capability":"process_data"}"""),
            2 when request.Messages.Any(message =>
                message.ToolResult is { } toolResult
                && string.Equals(
                    toolResult.ProviderCallId,
                    "process-capability",
                    StringComparison.Ordinal)
                && toolResult.Status == AgentToolResultStatus.Succeeded) =>
                CapabilityToolCall(
                    "process-list-after-grant",
                    BuiltInAgentTools.ProcessesList,
                    """{"sort":"pid_asc","limit":16}"""),
            3 when request.Messages.Any(message => string.Equals(
                message.ToolResult?.ProviderCallId,
                "process-list-after-grant",
                StringComparison.Ordinal)) =>
                CapabilityAnswer("The local process list was returned."),
            _ => throw new InvalidOperationException(
                "The process capability provider received an unexpected round."),
        });
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactPanel,
            provider,
            ProcessPolicy(AgentPermission.Off));

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("List local processes."),
            CancellationToken.None).AsTask();
        await WaitUntilAsync(() =>
            fixture.Runtime.Snapshot.State
                == GovernedAgentState.AwaitingCapabilityDecision);
        var capabilityRequest =
            Assert.IsType<GovernedAgentCapabilityRequest>(
                fixture.Runtime.Snapshot.PendingCapabilityRequest);

        Assert.Equal(
            AgentCapability.ProcessData,
            capabilityRequest.Capability);
        Assert.Equal(
            AgentCapabilityProtocol.ProcessData,
            capabilityRequest.CapabilityToken);
        Assert.Equal("Process inspection", capabilityRequest.DisplayTitle);
        Assert.Equal("Process Monitor", capabilityRequest.TargetTitle);
        Assert.Equal(
            "List local processes",
            Assert.Single(capabilityRequest.AffectedToolTitles));
        Assert.Empty(fixture.Processes.Actions);
        var requestedAudit = Assert.Single(fixture.Audit.Events);
        Assert.Equal("agent.capability.request", requestedAudit.Action);
        Assert.Equal(AuditOutcome.Requested, requestedAudit.Outcome);
        Assert.Equal(capabilityRequest.Id.Value, requestedAudit.CorrelationId);
        var initialRequest = provider.Requests.ToArray()[0];
        var intrinsic = Assert.Single(
            initialRequest.Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
        Assert.Equal(
            [AgentCapabilityProtocol.ProcessData],
            intrinsic.InputSchema
                .GetProperty("properties")
                .GetProperty("capability")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);

        var capabilityDecision =
            await fixture.Runtime.DecideCapabilityRequestAsync(
                capabilityRequest.Id,
                new GovernedAgentCapabilityDecision.AllowAsk(),
                CancellationToken.None);
        Assert.True(capabilityDecision.IsAccepted);
        Assert.Equal(
            "capability_request_allowed",
            capabilityDecision.Code);

        var approval = await WaitForApprovalAsync(fixture.Runtime);
        Assert.Equal(BuiltInAgentTools.ProcessesList, approval.ToolName);
        Assert.Equal(AgentPermission.Ask, approval.Permission);
        Assert.Equal(AgentActionRisk.Observation, approval.Risk);
        Assert.False(approval.TemporarilyYieldsTerminalInput);
        Assert.Empty(fixture.Processes.Actions);
        var receipt = Assert.Single(
            provider.Requests.ToArray()[1].Messages,
            message => string.Equals(
                message.ToolResult?.ProviderCallId,
                "process-capability",
                StringComparison.Ordinal)).ToolResult!;
        Assert.Equal(
            """{"ok":true,"capability":"process_data","permission":"ask","scope":"run","action_approval_required":true}""",
            receipt.Value.Content);
        Assert.DoesNotContain(
            provider.Requests.ToArray()[1].Tools,
            tool => string.Equals(tool.Name, IntrinsicAgentTools.RequestCapability, StringComparison.Ordinal));
        Assert.Single(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action, "agent.run.policy", StringComparison.Ordinal));

        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Single(fixture.Processes.Actions);
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent => string.Equals(
                auditEvent.Action,
                BuiltInAgentTools.ProcessesList,
                StringComparison.Ordinal)
                && auditEvent.Outcome == AuditOutcome.Approved);
        var processResult = Assert.Single(
            provider.Requests.ToArray()[^1].Messages,
            message => string.Equals(
                message.ToolResult?.ProviderCallId,
                "process-list-after-grant",
                StringComparison.Ordinal)).ToolResult!;
        Assert.Equal(
            "processes_listed",
            processResult.StableCode);
    }

    private static AgentProviderEvent[] CapabilityToolCall(
        string callId,
        string toolName,
        string arguments) =>
    [
        new AgentProviderEvent.ResponseStarted(),
        new AgentProviderEvent.ToolCallStarted(
            0,
            callId,
            ProviderToolName.FromInternal(toolName)),
        new AgentProviderEvent.ToolCallArgumentsDelta(
            0,
            arguments),
        new AgentProviderEvent.ToolCallCompleted(0),
        new AgentProviderEvent.ResponseCompleted(
            AgentProviderStopReason.ToolUse),
    ];

    private static AgentProviderEvent[] CapabilityAnswer(string text) =>
    [
        new AgentProviderEvent.ResponseStarted(),
        new AgentProviderEvent.TextDelta(text),
        new AgentProviderEvent.ResponseCompleted(
            AgentProviderStopReason.EndTurn),
    ];
}
