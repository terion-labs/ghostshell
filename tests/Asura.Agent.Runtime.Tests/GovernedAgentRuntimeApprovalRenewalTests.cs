using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task ExpiredApprovalIsReissuedForTheSameProviderToolCall()
    {
        var time = new ManualQuestionTimeProvider(QuestionTestNow);
        await using var fixture = new RuntimeFixture(
            ProviderRound.SendTextThenAnswer("renew me"),
            timeProvider: time);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Completed());

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run the action."),
            CancellationToken.None).AsTask();
        var first = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);

        time.Advance(AgentCapabilityBroker.DefaultApprovalLifetime);

        var renewed = await WaitForNewApprovalAsync(
            fixture.Runtime,
            first.Id);
        Assert.NotEqual(first.Id, renewed.Id);
        Assert.Equal(first.ToolName, renewed.ToolName);
        Assert.Equal(first.ToolTitle, renewed.ToolTitle);
        Assert.Equal(first.Risk, renewed.Risk);
        Assert.Equal(first.Permission, renewed.Permission);
        Assert.Equal(first.Target, renewed.Target);
        Assert.Equal(
            first.Presentation.TargetTitle,
            renewed.Presentation.TargetTitle);
        Assert.Equal(first.Presentation.Host, renewed.Presentation.Host);
        Assert.Equal(
            first.Presentation.WorkingDirectory,
            renewed.Presentation.WorkingDirectory);
        Assert.Equal(
            first.Presentation.Arguments.ToArray(),
            renewed.Presentation.Arguments.ToArray());
        Assert.Equal(
            first.TemporarilyYieldsTerminalInput,
            renewed.TemporarilyYieldsTerminalInput);
        Assert.True(renewed.ExpiresAtUtc > first.ExpiresAtUtc);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Single(fixture.Provider.Requests);

        var staleDecision = await fixture.Runtime.DecideAsync(
            first.Id,
            approved: true,
            CancellationToken.None);
        Assert.False(staleDecision.IsAccepted);
        Assert.Equal("approval_not_found", staleDecision.Code);

        var decision = await fixture.Runtime.DecideAsync(
            renewed.Id,
            approved: true,
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(decision.IsAccepted);
        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Terminal.Actions);
        Assert.DoesNotContain(
            fixture.Provider.Requests.SelectMany(request => request.Messages),
            message => message.ToolResult?.StableCode
                is "approval_expired" or "authorization_expired");
    }
}
