using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Git;

namespace Asura.Agent.Runtime.Tests;

public sealed class GitAgentToolContractTests
{
    [Fact]
    public void SchemasAreClosedCapabilityFilteredAndContainNoRawGitOperands()
    {
        var panel = ContextPanel(
            quarantined: false,
            SessionCapabilities.GitReadState,
            SessionCapabilities.GitReadDiff,
            SessionCapabilities.GitPush);

        var tools = GitAgentToolSet.For(panel);

        Assert.Equal(
            [
                GitAgentToolNames.ReadState,
                GitAgentToolNames.ReadDiff,
                GitAgentToolNames.Push,
            ],
            tools.Select(tool => tool.Name),
            StringComparer.Ordinal);
        Assert.All(tools, tool =>
        {
            var schema = tool.InputSchema.GetRawText();
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.DoesNotContain("repository_path", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("remote_url", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("object_id", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("refspec", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("argv", schema, StringComparison.Ordinal);
        });

        var quarantined = GitAgentToolSet.For(ContextPanel(
            quarantined: true,
            SessionCapabilities.GitReadState,
            SessionCapabilities.GitPush));
        Assert.Equal(
            [GitAgentToolNames.ReadState],
            quarantined.Select(tool => tool.Name),
            StringComparer.Ordinal);

        Assert.Empty(GitAgentToolSet.ForWorkspace([
            ContextPanel(
                quarantined: false,
                SessionCapabilities.DockerReadState),
        ]));
        var workspace = GitAgentToolSet.ForWorkspace([panel]);
        Assert.Equal(
            [
                GitAgentToolNames.ReadState,
                GitAgentToolNames.ReadDiff,
                GitAgentToolNames.Push,
            ],
            workspace.Select(tool => tool.Name),
            StringComparer.Ordinal);
        Assert.All(workspace, tool => Assert.True(
            tool.InputSchema
                .GetProperty("properties")
                .TryGetProperty("panel_id", out _)));
    }

    [Fact]
    public async Task ParserBuildsOnlyTypedOpaqueRequests()
    {
        var panel = ContextPanel(
            quarantined: false,
            SessionCapabilities.GitPush,
            SessionCapabilities.GitCommit);
        var parsed = Assert.IsType<GitAgentIntentResult.Parsed>(
            GitAgentToolParser.Parse(
                await ProposalAsync(
                    GitAgentToolNames.Push,
                    """
                    {"state_ref":"state","remote_state_ref":"observed","remote_ref":"remote","branch_ref":"branch"}
                    """),
                panel));
        Assert.IsType<AgentGitRequest.Push>(parsed.Request);

        Assert.IsType<GitAgentIntentResult.Rejected>(GitAgentToolParser.Parse(
            await ProposalAsync(
                GitAgentToolNames.Push,
                """
                {"state_ref":"state","remote_state_ref":"observed","remote_ref":"remote","branch_ref":"branch","refspec":"main:main"}
                """),
            panel));
        Assert.IsType<GitAgentIntentResult.Rejected>(GitAgentToolParser.Parse(
            await ProposalAsync(
                GitAgentToolNames.Commit,
                """
                {"state_ref":"state","subject":"password=hunter2"}
                """),
            panel));
    }

    [Fact]
    public void ResultProjectionScreensHostileGitContentAndNeverAddsEndpoints()
    {
        var result = new GitAgentOperationResult.State(new GitAgentStateSnapshot(
            new GitStateReferenceId("state"),
            "password=hunter2",
            "Local",
            "main",
            new string('a', 40),
            IsDetached: false,
            IsUnborn: false,
            HasConflicts: false,
            IsDirty: true,
            [new GitChangeItem(
                new GitChangeReferenceId("change"),
                "token=abcdefghijklmnop",
                GitChangeKind.Modified,
                GitChangeArea.Unstaged)],
            [new GitBranchItem(
                new GitBranchReferenceId("branch"),
                "main",
                new string('a', 40),
                IsCurrent: true)],
            [new GitRemoteItemProjection(
                new GitRemoteReferenceId("remote"),
                "origin")],
            IsTruncated: false,
            MutationsQuarantined: false,
            DateTimeOffset.UnixEpoch));

        var projection = GitAgentToolResultJson.Project(result);

        Assert.True(projection.IsSuccess);
        Assert.DoesNotContain("hunter2", projection.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", projection.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", projection.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", projection.Json, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(projection.Json)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);
        using var document = JsonDocument.Parse(projection.Json);
        Assert.Equal(
            GitAgentToolResultJson.ContentOrigin,
            document.RootElement.GetProperty("content_origin").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("redaction_count").GetInt32());
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("git-contract"));
        var result = await session.RunTurnAsync(
            "Use the Git tool.",
            [new AgentToolDefinition(
                name,
                "Test Git tool.",
                """{"type":"object","additionalProperties":true}"""u8.ToArray())],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentContextPanel ContextPanel(
        bool quarantined,
        params string[] capabilities)
    {
        var sessionId = new SessionId("git-session");
        var windowId = new WindowInstanceId("git-window");
        var workspaceId = new WorkspaceInstanceId("git-workspace");
        var tabId = new TabInstanceId("git-tab");
        var panelId = new PanelInstanceId("git-panel");
        var panel = new PanelInstance(panelId, PanelKind.Git, "Git", sessionId);
        var tab = new TabInstance(tabId, "Git", [panel], panelId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(workspaceId, "Git", [tab], tabId),
            revision: 2,
            lastSequence: 2);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.Git,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet(capabilities),
            Revision: 4,
            HasActiveWork: false,
            StatusDetail: "Ready",
            GitMetadata: new GitSessionMetadata(
                new GitRepositoryIdentity(new string('b', 64)),
                BindingRevision: 2,
                "Local",
                ConnectionKind.Local,
                quarantined));
        return AgentContextPanel.ForGraphPanel(graph, tabId, panelId, descriptor);
    }

    private sealed class ToolProvider(string name, string arguments) : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.ToolCallStarted(
                0,
                "git-call",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}
