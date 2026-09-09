using System.Text;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BroadScopePublishesOrderedExactPresentationContext(
        bool workspaceScope)
    {
        await using var fixture = BroadScopeFixture.Create(
            workspaceScope ? ScopeKind.Workspace : ScopeKind.OpenTab,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.FirstPanelId.Value}}"}"""));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(Screen("ready")));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the operations terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var items = fixture.Runtime.Snapshot.ContextItems;
        Assert.Equal(
            [
                BroadScopeContextProxy.FirstPanelId,
                BroadScopeContextProxy.SecondPanelId,
            ],
            items.Select(item => item.PanelId));

        var logs = items[1];
        Assert.Equal(BroadScopeContextProxy.WindowId, logs.WindowId);
        Assert.Equal(BroadScopeContextProxy.WorkspaceId, logs.WorkspaceId);
        Assert.Equal(BroadScopeContextProxy.TabId, logs.TabId);
        Assert.Equal(BroadScopeContextProxy.SecondSessionId, logs.SessionId);
        Assert.Equal("Production workspace", logs.WorkspaceTitle);
        Assert.Equal("Operations", logs.TabTitle);
        Assert.Equal("Logs terminal", logs.PanelTitle);
        Assert.Equal("SSH · logs", logs.ConnectionBoundary);
        Assert.Equal("/srv/operations", logs.WorkingDirectory);
        Assert.Equal(SessionLifecycle.Active, logs.Lifecycle);
        Assert.Equal(SessionHealth.Healthy, logs.Health);
        Assert.True(logs.IsVisible);
        Assert.False(logs.IsFocused);
        Assert.False(logs.HasActiveWork);
        Assert.Equal(
            [
                BuiltInAgentTools.TerminalReadScreen,
                BuiltInAgentTools.TerminalReadScreenDiff,
                BuiltInAgentTools.TerminalFindOnScreen,
                BuiltInAgentTools.TerminalWait,
                BuiltInAgentTools.TerminalSendText,
                BuiltInAgentTools.TerminalSendKeys,
                BuiltInAgentTools.TerminalInterrupt,
            ],
            [.. logs.SupportedOperations]);

        var operations = fixture.Provider.Requests.First().Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            items.SelectMany(item => item.SupportedOperations),
            operation => Assert.Contains(operation, operations));
        Assert.True(items[0].IsFocused);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task PasteIsPublishedOnlyWithCapabilityAndInputBarrier(
        bool hasPasteCapability,
        bool hasInputBarrier,
        bool expected)
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.ReadThenAnswer());
        var capabilities = new List<string>
        {
            SessionCapabilities.ManagedRenderer,
            SessionCapabilities.TerminalReadScreen,
            SessionCapabilities.TerminalWait,
        };
        if (hasPasteCapability)
        {
            capabilities.Add(SessionCapabilities.TerminalPaste);
        }

        if (hasInputBarrier)
        {
            capabilities.Add(
                SessionCapabilities.TerminalAgentInputBarrier);
        }

        fixture.Context.Capabilities = new CapabilitySet(capabilities);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("ready", contentRevision: 7)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var firstRequest = fixture.Provider.Requests.First();
        Assert.Equal(
            expected,
            firstRequest.Tools.Any(
                tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalPaste, StringComparison.Ordinal)));
        var context = Assert.Single(fixture.Runtime.Snapshot.ContextItems);
        Assert.Equal(
            expected,
            context.SupportedOperations.Contains(
                BuiltInAgentTools.TerminalPaste,
                StringComparer.Ordinal));

        var systemPrompt = Assert.Single(
            firstRequest.Messages,
            message => message.Role == AgentMessageRole.System).Content;
        Assert.Contains(
            expected
                ? "operations=\"read_screen,read_screen_diff,find_on_screen,wait,paste\""
                : "operations=\"read_screen,read_screen_diff,find_on_screen,wait\"",
            systemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextDisplayMetadataIsRedactedBoundedAndClearedWithRun()
    {
        await using var fixture = new RuntimeFixture(
            ProviderRound.ReadThenAnswer());
        fixture.Context.WorkspaceTitle = "password=workspace-secret";
        fixture.Context.TabTitle = "token=tab-secret";
        fixture.Context.PanelTitle = "secret=panel-secret";
        fixture.Context.ConnectionBoundary = "api_key=connection-secret";
        fixture.Context.CurrentWorkingDirectory =
            "/" + new string('é', 200);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("ready", contentRevision: 7)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(fixture.Runtime.Snapshot.ContextItems);
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            item.WorkspaceTitle);
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            item.TabTitle);
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            item.PanelTitle);
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            item.ConnectionBoundary);
        Assert.NotNull(item.WorkingDirectory);
        Assert.True(
            Encoding.UTF8.GetByteCount(item.WorkingDirectory)
                <= GovernedAgentContextItem.MaximumDisplayTextBytes);
        Assert.EndsWith("…", item.WorkingDirectory, StringComparison.Ordinal);
        var displayMetadata = string.Join(
            '\n',
            item.WorkspaceTitle,
            item.TabTitle,
            item.PanelTitle,
            item.ConnectionBoundary);
        Assert.DoesNotContain(
            "workspace-secret",
            displayMetadata,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "tab-secret",
            displayMetadata,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "panel-secret",
            displayMetadata,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "connection-secret",
            displayMetadata,
            StringComparison.Ordinal);

        Assert.True(await fixture.Runtime.ClearAsync(CancellationToken.None));
        Assert.Empty(fixture.Runtime.Snapshot.ContextItems);
    }

    [Fact]
    public async Task BroadScopeOrderDriftRefreshesLiveContextAndExecutesExactAction()
    {
        await using var fixture = BroadScopeFixture.Create(
            ScopeKind.Workspace,
            ToolThenAnswer(
                BuiltInAgentTools.TerminalReadScreen,
                $$"""{"panel_id":"{{BroadScopeContextProxy.SecondPanelId.Value}}"}"""));
        fixture.Context.ReversePanelsAfterInspection = 1;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the logs terminal."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var action = Assert.Single(fixture.Terminal.Actions);
        var request = Assert.IsType<AgentTerminalRequest.ReadScreen>(
            action.Request);
        Assert.Equal(BroadScopeContextProxy.SecondSessionId, request.SessionId);
        Assert.Equal(fixture.Context.SecondTarget, action.Proposal.Target);
        Assert.Equal(
            [
                BroadScopeContextProxy.SecondPanelId,
                BroadScopeContextProxy.FirstPanelId,
            ],
            fixture.Runtime.Snapshot.ContextItems.Select(item => item.PanelId));
        var continuation = fixture.Provider.Requests.ToArray()[1];
        var refreshedPanelSchema = continuation.Tools
            .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalReadScreen, StringComparison.Ordinal))
            .InputSchema
            .GetProperty("properties")
            .GetProperty("panel_id");
        Assert.False(refreshedPanelSchema.TryGetProperty("enum", out _));
        Assert.Equal(
            fixture.Provider.Requests.ToArray()[0].Tools
                .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalReadScreen, StringComparison.Ordinal))
                .InputSchema.GetRawText(),
            continuation.Tools
                .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.TerminalReadScreen, StringComparison.Ordinal))
                .InputSchema.GetRawText());
        Assert.Equal(
            "tool_succeeded",
            Assert.Single(
                continuation.Messages,
                message => message.Role == AgentMessageRole.Tool)
                .ToolResult
                ?.StableCode);
    }
}
