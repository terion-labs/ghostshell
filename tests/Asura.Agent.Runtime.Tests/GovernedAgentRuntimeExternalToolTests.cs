using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task ExternalMutationRequiresVisibleApprovalAndRejectsConcurrentCalls()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(WorkspaceGraphProviderRound.Answer("Unused"));
        await using var fixture = await WorkspaceGraphRuntimeFixture.CreateAsync(provider,
            WorkspaceGraphFixtureKind.GraphBackedWorkspaceLauncher, ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        using var arguments = JsonDocument.Parse("""{"kind":"placeholder"}""");
        var pending = fixture.Runtime.CallExternalToolAsync(BuiltInAgentTools.TabCreate,
            arguments.RootElement, CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(fixture.Runtime, previousApproval: null);
        using var empty = JsonDocument.Parse("{}");
        var concurrent = await fixture.Runtime.CallExternalToolAsync(BuiltInAgentTools.WorkspaceInspect,
            empty.RootElement, CancellationToken.None);
        Assert.Equal("agent_busy", concurrent.StableCode);
        Assert.True((await fixture.Runtime.DecideAsync(approval.Id, approved: false, CancellationToken.None)).IsAccepted);
        Assert.Equal("approval_denied", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).StableCode);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ExternalToolsInspectWorkspaceWithoutCallingProvider()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(WorkspaceGraphProviderRound.Answer("Unused"));
        await using var fixture = await WorkspaceGraphRuntimeFixture.CreateAsync(provider,
            WorkspaceGraphFixtureKind.GraphBackedWorkspaceLauncher, ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        var tools = await fixture.Runtime.ListExternalToolsAsync(CancellationToken.None);
        Assert.Contains(tools, tool => string.Equals(tool.Name, BuiltInAgentTools.WorkspaceInspect, StringComparison.Ordinal));
        Assert.Contains(tools, tool => string.Equals(tool.Name, IntrinsicAgentTools.RunSequence, StringComparison.Ordinal));
        Assert.DoesNotContain(tools, tool => string.Equals(tool.Name, IntrinsicAgentTools.AskUser, StringComparison.Ordinal));
        using var arguments = JsonDocument.Parse("{}");
        var result = await fixture.Runtime.CallExternalToolAsync(BuiltInAgentTools.WorkspaceInspect,
            arguments.RootElement, CancellationToken.None);
        Assert.Equal(AgentToolResultStatus.Succeeded, result.Status);
        Assert.Empty(provider.Requests);
        Assert.NotNull(fixture.Runtime.Snapshot.RunId);
        Assert.True((await fixture.Runtime.SendAsync(fixture.Prompt("Internal request"), CancellationToken.None)).Code
            == "agent_run_requires_clear");
        Assert.True(await fixture.Runtime.ClearAsync(CancellationToken.None));
        Assert.True((await fixture.Runtime.SendAsync(fixture.Prompt("Internal request"), CancellationToken.None)).IsSuccess);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ExternalToolsCannotWidenScopeOrCallIntrinsics()
    {
        var provider = ScriptedWorkspaceGraphProvider.Create(WorkspaceGraphProviderRound.Answer("Unused"));
        await using var fixture = await WorkspaceGraphRuntimeFixture.CreateAsync(provider,
            WorkspaceGraphFixtureKind.GraphBackedWorkspaceLauncher, ExactWorkspaceGraphPolicy(AgentPermission.Auto));
        using var empty = JsonDocument.Parse("{}");
        var result = await fixture.Runtime.CallExternalToolAsync(IntrinsicAgentTools.RequestCapability,
            empty.RootElement, CancellationToken.None);
        Assert.Equal("unknown_tool", result.StableCode);
        using var injected = JsonDocument.Parse("""{"workspace_id":"another-workspace"}""");
        result = await fixture.Runtime.CallExternalToolAsync(BuiltInAgentTools.WorkspaceInspect,
            injected.RootElement, CancellationToken.None);
        Assert.Equal(AgentToolResultStatus.Failed, result.Status);
        Assert.Empty(provider.Requests);
    }
}
