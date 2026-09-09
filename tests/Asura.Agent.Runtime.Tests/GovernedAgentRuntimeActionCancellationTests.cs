using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task CancelActiveActionWithoutDispatchIsStableNoOp()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.ReadThenAnswer());

        var result = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);

        Assert.False(result.WasRequested);
        Assert.Equal("agent_action_not_running", result.Code);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Null(fixture.Runtime.Snapshot.ActiveTool);
    }

    [Fact]
    public async Task CancelActiveActionContinuesProviderAndKeepsRunUsable()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.ReadThenTwoTextTurns(
                "echo first",
                "echo second"));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("ready", contentRevision: 1)));
        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None)).IsSuccess);
        var runId = fixture.Runtime.Snapshot.RunId;
        Assert.True(runId.HasValue);

        fixture.Terminal.HoldBlockedActionAfterCancellation = true;
        fixture.Terminal.BlockNextAction();
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run the first command."),
            CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        await fixture.Terminal.BlockedActionStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var cancellation = await fixture.Runtime.CancelActiveActionAsync(
                CancellationToken.None);
            await fixture.Terminal.BlockedActionCancellationObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(cancellation.WasRequested);
            Assert.Equal("agent_action_cancel_requested", cancellation.Code);
            Assert.Equal(
                GovernedAgentState.RunningTool,
                fixture.Runtime.Snapshot.State);
            var activity = Assert.IsType<GovernedAgentToolActivity>(
                fixture.Runtime.Snapshot.ActiveTool);
            Assert.Equal(fixture.Target.PanelId, activity.PanelId);
            Assert.True(activity.CancellationRequested);
            Assert.Equal(
                "Cancelling this action…",
                fixture.Runtime.Snapshot.Status);

            var duplicate = await fixture.Runtime.CancelActiveActionAsync(
                CancellationToken.None);

            Assert.False(duplicate.WasRequested);
            Assert.Equal(
                "agent_action_cancel_already_requested",
                duplicate.Code);
        }
        finally
        {
            fixture.Terminal.ReleaseBlockedActionCancellation.TrySetResult();
        }

        var cancelledTurn = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancelledTurn.IsSuccess);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Equal(runId, fixture.Runtime.Snapshot.RunId);
        Assert.Null(fixture.Runtime.Snapshot.ActiveTool);
        var cancelledToolResult = Assert.Single(
            fixture.Provider.Requests.SelectMany(request => request.Messages),
            message => message.Role == AgentMessageRole.Tool
                && string.Equals(message.ToolResult?.StableCode, "caller_cancelled", StringComparison.Ordinal));
        Assert.Contains(
            "\"code\":\"caller_cancelled\"",
            cancelledToolResult.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            fixture.Audit.Events,
            item => string.Equals(item.Action, BuiltInAgentTools.TerminalSendText
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Cancelled
                && item.Details is AuditDetails.AgentActionDetails
                {
                    ResultCode: "caller_cancelled",
                });

        var laterTurn = fixture.Runtime.SendAsync(
            fixture.Prompt("Run the second command."),
            CancellationToken.None).AsTask();
        var laterApproval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: approval.Id);
        Assert.True((await fixture.Runtime.DecideAsync(
            laterApproval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);

        Assert.True((await laterTurn.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(runId, fixture.Runtime.Snapshot.RunId);
        Assert.Equal(
            2,
            fixture.Terminal.Actions.Count(action =>
                action.Request is AgentTerminalRequest.SendText));
    }

    [Fact]
    public async Task CancelAfterActionCompletionDoesNotAffectRun()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.ReadThenAnswer());
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("ready", contentRevision: 1)));

        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None)).IsSuccess);
        var runId = fixture.Runtime.Snapshot.RunId;

        var cancellation = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);

        Assert.False(cancellation.WasRequested);
        Assert.Equal("agent_action_not_running", cancellation.Code);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Equal(runId, fixture.Runtime.Snapshot.RunId);
        Assert.False(
            Assert.Single(fixture.Terminal.Permits)
                .CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task FastActionCancellationExceptionIsNotReportedAsHostFailure()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.SendTextThenAnswer("echo ready"));
        fixture.Terminal.ThrowOnCallerCancellation = true;
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run the command."),
            CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        await fixture.Terminal.BlockedActionStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.WasRequested);
        Assert.True(result.IsSuccess);
        var toolResult = Assert.Single(
            fixture.Provider.Requests.SelectMany(request => request.Messages),
            message => message.Role == AgentMessageRole.Tool);
        Assert.Equal("caller_cancelled", toolResult.ToolResult?.StableCode);
        Assert.DoesNotContain(
            "terminal_host_failed",
            toolResult.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAfterActionCancellationStillRevokesRunAuthority()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.ReadThenTwoTextTurns(
                "echo first",
                "echo second"));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("ready", contentRevision: 1)));
        Assert.True((await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect."),
            CancellationToken.None)).IsSuccess);

        fixture.Terminal.HoldBlockedActionAfterCancellation = true;
        fixture.Terminal.BlockNextAction();
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run the first command."),
            CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        var permit = await fixture.Terminal.BlockedActionStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        GovernedAgentStopResult stopped;
        try
        {
            var cancellation = await fixture.Runtime.CancelActiveActionAsync(
                CancellationToken.None);
            await fixture.Terminal.BlockedActionCancellationObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cancellation.WasRequested);

            stopped = await fixture.Runtime.StopAsync(CancellationToken.None);

            Assert.True(stopped.WasRunning);
            Assert.True(permit.CancellationToken.IsCancellationRequested);
            Assert.Equal(
                GovernedAgentState.Cancelled,
                fixture.Runtime.Snapshot.State);
            Assert.Null(fixture.Runtime.Snapshot.ActiveTool);
        }
        finally
        {
            fixture.Terminal.ReleaseBlockedActionCancellation.TrySetResult();
        }

        var sendResult = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(sendResult.IsSuccess);
        Assert.Equal("agent_cancelled", sendResult.Code);
        Assert.Contains(
            fixture.Audit.Events,
            item => string.Equals(item.Action, BuiltInAgentTools.TerminalSendText
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Cancelled
                && item.Details is AuditDetails.AgentActionDetails
                {
                    ResultCode: "authority_revoked",
                });
        Assert.True(await fixture.Runtime.ClearAsync(CancellationToken.None));
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
    }

    [Fact]
    public async Task RealHostActionCancellationReturnsCallerCancelledToProvider()
    {
        var provider = InteractiveTuiProvider.OneKeyThenAnswer();
        await using var fixture = await InteractiveTuiFixture.CreateAsync(provider);
        fixture.Terminal.BlockNextKey = true;
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Move the selection down."),
            CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        await fixture.Terminal.KeyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.WasRequested);
        Assert.True(result.IsSuccess);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Empty(fixture.Terminal.ReceivedKeys);
        var toolResult = Assert.Single(
            provider.Requests.SelectMany(request => request.Messages),
            message => message.Role == AgentMessageRole.Tool);
        Assert.Equal("caller_cancelled", toolResult.ToolResult?.StableCode);
        Assert.Contains(
            "\"code\":\"caller_cancelled\"",
            toolResult.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            fixture.Audit.Events,
            item => string.Equals(item.Action, BuiltInAgentTools.TerminalSendKeys
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Cancelled
                && item.Details is AuditDetails.AgentActionDetails
                {
                    ResultCode: "caller_cancelled",
                });
    }

    [Fact]
    public async Task RealHostWaitCancellationPreservesFinalScreenForProvider()
    {
        var provider = InteractiveTuiProvider.OneWaitThenAnswer();
        await using var fixture = await InteractiveTuiFixture.CreateAsync(provider);
        fixture.Terminal.BlockNextWait = true;
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Wait for a marker."),
            CancellationToken.None).AsTask();
        await fixture.Terminal.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var runId = fixture.Runtime.Snapshot.RunId;

        var cancellation = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.WasRequested);
        Assert.True(result.IsSuccess);
        Assert.Equal(GovernedAgentState.Ready, fixture.Runtime.Snapshot.State);
        Assert.Equal(runId, fixture.Runtime.Snapshot.RunId);
        var toolResult = Assert.Single(
            provider.Requests.SelectMany(request => request.Messages),
            message => message.Role == AgentMessageRole.Tool);
        Assert.Equal(AgentToolResultStatus.Succeeded, toolResult.ToolResult?.Status);
        Assert.Equal("tool_succeeded", toolResult.ToolResult?.StableCode);
        Assert.Contains(
            "\"ok\":true",
            toolResult.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"wait_outcome\":\"cancelled\"",
            toolResult.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"content_revision\":1",
            toolResult.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            fixture.Audit.Events,
            item => string.Equals(item.Action, BuiltInAgentTools.TerminalWait
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Cancelled
                && item.Details is AuditDetails.AgentActionDetails
                {
                    ResultCode: "caller_cancelled",
                });
    }
}
