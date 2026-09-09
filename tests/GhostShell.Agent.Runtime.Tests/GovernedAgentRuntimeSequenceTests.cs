using System.Text.Json;
using GhostShell.Agent;
using GhostShell.Application;

namespace GhostShell.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task SequenceCancellationDuringDelayPreventsDispatch()
    {
        var provider = ProviderRound.BatchThenAnswer(
        [new("sequence", IntrinsicAgentTools.RunSequence,
            """{"steps":[{"tool":"terminal.read_screen","arguments":{},"delay_ms":10000}]}""")]);
        await using var fixture = new RuntimeFixture(provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await fixture.Runtime.SendAsync(fixture.Prompt("Read after a delay."), cancellation.Token);
        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
    }

    [Fact]
    public async Task SequenceRunsInOrderWithoutProviderRoundTrips()
    {
        var provider = ProviderRound.BatchThenAnswer(
        [new("sequence", IntrinsicAgentTools.RunSequence,
            """{"steps":[{"tool":"terminal.read_screen","arguments":{}},{"tool":"terminal.read_screen","arguments":{},"delay_ms":1}]}""")]);
        await using var fixture = new RuntimeFixture(provider);
        fixture.Terminal.Results.Enqueue(new AgentTerminalActionResult.Screen(fixture.Context.Screen("first", contentRevision: 1)));
        fixture.Terminal.Results.Enqueue(new AgentTerminalActionResult.Screen(fixture.Context.Screen("second", contentRevision: 2)));

        Assert.True((await fixture.Runtime.SendAsync(fixture.Prompt("Read twice."), CancellationToken.None)).IsSuccess);
        Assert.Equal(2, fixture.Terminal.Actions.Count);
        Assert.Equal(2, provider.Requests.Count);
        var result = Assert.Single(provider.Requests.Last().Messages.Where(message => message.Role == AgentMessageRole.Tool));
        using var json = JsonDocument.Parse(result.ToolResult!.Value.Content);
        Assert.Equal(2, json.RootElement.GetProperty("executed_steps").GetInt32());
        Assert.Contains("first", json.RootElement.GetProperty("results")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("second", json.RootElement.GetProperty("results")[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequenceStopsAfterDeniedAction()
    {
        var provider = ProviderRound.BatchThenAnswer(
        [new("sequence", IntrinsicAgentTools.RunSequence,
            """{"steps":[{"tool":"terminal.send_text","arguments":{"text":"first"}},{"tool":"terminal.read_screen","arguments":{}}]}""")]);
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(fixture.Prompt("Run a sequence."), CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(fixture.Runtime, previousApproval: null);
        Assert.True((await fixture.Runtime.DecideAsync(approval.Id, approved: false, CancellationToken.None)).IsAccepted);
        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
        var result = Assert.Single(provider.Requests.Last().Messages.Where(message => message.Role == AgentMessageRole.Tool));
        Assert.Equal("approval_denied", result.ToolResult!.StableCode);
        using var json = JsonDocument.Parse(result.ToolResult.Value.Content);
        Assert.Equal(1, json.RootElement.GetProperty("remaining_steps").GetInt32());
    }

    [Theory]
    [InlineData("{\"steps\":[]}")]
    [InlineData("{\"steps\":[{\"tool\":\"agent.run_sequence\",\"arguments\":{}}]}")]
    [InlineData("{\"steps\":[{\"tool\":\"terminal.read_screen\",\"arguments\":{},\"delay_ms\":10001}]}")]
    [InlineData("{\"steps\":[{\"tool\":\"terminal.read_screen\",\"arguments\":{}},{\"tool\":\"unknown\",\"arguments\":{}}]}")]
    public async Task InvalidSequenceIsRejectedBeforeDispatch(string arguments)
    {
        var provider = ProviderRound.BatchThenAnswer([new("sequence", IntrinsicAgentTools.RunSequence, arguments)]);
        await using var fixture = new RuntimeFixture(provider);
        Assert.True((await fixture.Runtime.SendAsync(fixture.Prompt("Run a sequence."), CancellationToken.None)).IsSuccess);
        Assert.Empty(fixture.Terminal.Actions);
        var result = Assert.Single(provider.Requests.Last().Messages.Where(message => message.Role == AgentMessageRole.Tool));
        Assert.Equal("invalid_sequence", result.ToolResult!.StableCode);
    }
}
