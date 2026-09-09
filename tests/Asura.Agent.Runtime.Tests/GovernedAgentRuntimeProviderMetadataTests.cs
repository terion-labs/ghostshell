using Asura.Agent;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task CommittedReasoningSummaryAndUsageReachPresentationWithoutAuthority()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.AnswerWithMetadata());

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Check this workspace."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var assistant = Assert.Single(
            fixture.Runtime.Snapshot.Messages,
            message => message.Role == Application.AgentChatMessageRole.Assistant);
        Assert.Equal(
            "Checked only the host-provided workspace context.",
            assistant.ReasoningSummary);
        Assert.Equal("The workspace is healthy.", assistant.Content);
        var usage = Assert.IsType<Application.AgentChatUsage>(assistant.Usage);
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
        Assert.Equal(50, usage.CachedInputTokens);
        Assert.Equal(10, usage.ReasoningTokens);
        Assert.Equal(150, usage.TotalTokens);
        Assert.Equal(
            Core.AgentReasoningEffort.Automatic,
            assistant.RequestedReasoningEffort);
        Assert.Empty(fixture.Terminal.Actions);
        Assert.Empty(fixture.Audit.Events);
    }

    private sealed partial class ProviderRound
    {
        public static ProviderRound AnswerWithMetadata() =>
            new((_, _) =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ReasoningSummaryDelta(
                    "Checked only the host-provided workspace context."),
                new AgentProviderEvent.TextDelta("The workspace is healthy."),
                new AgentProviderEvent.Usage(
                    new AgentTokenUsage(
                        inputTokens: 120,
                        outputTokens: 30,
                        cachedInputTokens: 50,
                        reasoningTokens: 10)),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn),
            ]);
    }
}
