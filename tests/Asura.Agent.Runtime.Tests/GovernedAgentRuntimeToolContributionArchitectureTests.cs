using System.Collections;
using System.Reflection;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task RuntimeRegistersOneContributionForEveryExistingToolFamily()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.AnswerEveryTurn());

        var field = typeof(GovernedAgentRuntime).GetField(
            "_toolContributions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var contributions = Assert.IsAssignableFrom<IEnumerable>(
                field.GetValue(fixture.Runtime))
            .Cast<object>()
            .Select(contribution => contribution.GetType().Name)
            .ToArray();

        Assert.Equal(
        [
            "WorkspaceGraphToolContribution",
            "WorkspaceLayoutToolContribution",
            "PanelToolContribution",
            "TerminalToolContribution",
            "BrowserToolContribution",
            "WebToolContribution",
            "ProcessToolContribution",
            "StatisticsToolContribution",
            "DatabaseToolContribution",
            "DockerToolContribution",
            "GitToolContribution",
            "FileToolContribution",
            "McpToolContribution",
        ],
            contributions);
    }

    [Fact]
    public async Task EveryStaticProviderToolHasExactlyOneContributionOwner()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.AnswerEveryTurn());
        var resolve = typeof(GovernedAgentRuntime).GetMethod(
            "ResolveToolContribution",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolve);

        foreach (var tool in BuiltInAgentTools.Catalog.Tools
                     .Where(tool => !string.Equals(tool.Name, BuiltInAgentTools.McpCall, StringComparison.Ordinal)))
        {
            var resolved = resolve.Invoke(
                fixture.Runtime,
                [tool.Name]);

            Assert.NotNull(resolved);
        }

        // mcp.call is an internal authorization identity. Provider-facing MCP
        // aliases are contributed only by a frozen run manifest.
        Assert.Null(resolve.Invoke(
            fixture.Runtime,
            [BuiltInAgentTools.McpCall]));
    }
}
