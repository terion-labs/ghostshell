namespace Asura.Core.Tests;

public sealed class AgentRunTests
{
    [Fact]
    public void Running_agent_action_can_be_linked_to_a_command_block_and_completed()
    {
        var createdAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var run = new AgentRun(
            new AgentRunId("deploy-fix"),
            Target(),
            "Repair the production deployment",
            AgentRunState.Pending,
            createdAt);

        var completed = run
            .Start(createdAt.AddSeconds(1))
            .LinkCommand(new CommandBlockId("deploy"))
            .Complete(createdAt.AddSeconds(5));

        Assert.Equal(AgentRunState.Succeeded, completed.State);
        Assert.Equal(new CommandBlockId("deploy"), completed.CommandBlockId);
        Assert.Equal(createdAt.AddSeconds(5), completed.FinishedAt);
    }

    [Fact]
    public void Completed_agent_action_cannot_be_cancelled()
    {
        var now = DateTimeOffset.UtcNow;
        var completed = new AgentRun(
                new AgentRunId("done"),
                Target(),
                "Done",
                AgentRunState.Pending,
                now)
            .Start(now)
            .Complete(now);

        Assert.Throws<InvalidOperationException>(() => completed.Cancel(now));
    }

    [Fact]
    public void Default_policy_exposes_the_effective_runtime_summary()
    {
        Assert.Equal(
            "Commands: Ask · Files: Ask · Git: Ask · Docker: Off",
            AgentPolicy.Default.EffectiveSummary);
    }

    private static AgentTarget Target() =>
        new AgentTarget.Panel(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId("panel-1"));
}
