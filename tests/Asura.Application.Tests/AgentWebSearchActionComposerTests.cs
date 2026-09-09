using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentWebSearchActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreparationBindsExactQueryAndCountToRunScope()
    {
        var request = new AgentWebSearchRequest("CEF offscreen browser", 4);

        var action = new AgentWebToolActionComposer().Prepare(
            Envelope("action-1"),
            Context(revision: 3),
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(BuiltInAgentTools.WebSearch, action.Proposal.ToolName);
        Assert.Equal(Target(), action.Proposal.Target);
        Assert.Equal("Google search", action.Proposal.Presentation.TargetTitle);
        Assert.Equal("www.google.com", action.Proposal.Presentation.Host);
        Assert.Equal(
            ["query", "result_count"],
            action.Proposal.Presentation.Arguments.Select(argument => argument.Name),
            StringComparer.Ordinal);
    }

    [Fact]
    public void QueryOrCountChangesProduceDifferentActionDigests()
    {
        var composer = new AgentWebToolActionComposer();

        var first = composer.Prepare(
            Envelope("action-1"),
            Context(),
            new AgentWebSearchRequest("CEF", 3));
        var changedQuery = composer.Prepare(
            Envelope("action-1"),
            Context(),
            new AgentWebSearchRequest("Chromium", 3));
        var changedCount = composer.Prepare(
            Envelope("action-1"),
            Context(),
            new AgentWebSearchRequest("CEF", 4));

        Assert.NotEqual(first.Proposal.ArgumentDigest, changedQuery.Proposal.ArgumentDigest);
        Assert.NotEqual(first.Proposal.ArgumentDigest, changedCount.Proposal.ArgumentDigest);
    }

    [Fact]
    public void BindingUsesFreshFingerprintAndRejectsAnotherTarget()
    {
        var composer = new AgentWebToolActionComposer();
        var action = composer.Prepare(
            Envelope("action-1"),
            Context(revision: 3),
            new AgentWebSearchRequest("CEF", 3));

        var binding = composer.BindForExecution(action, Context(revision: 4));

        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        Assert.NotEqual(action.Proposal.TargetFingerprint, binding.TargetFingerprint);
        Assert.Throws<ArgumentException>(() => composer.BindForExecution(
            action,
            Context(
                revision: 4,
                target: new AgentTarget.OpenTab(
                    Window(),
                    Workspace(),
                    new TabInstanceId("other-tab")))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void RequestRejectsOutOfRangeResultCount(int resultCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentWebSearchRequest("CEF", resultCount));
    }

    [Fact]
    public void RequestRejectsControlsOversizeAndInvalidUnicode()
    {
        Assert.Throws<ArgumentException>(() => new AgentWebSearchRequest("line\nfeed"));
        Assert.Throws<ArgumentException>(() =>
            new AgentWebSearchRequest(new string('x', AgentWebSearchRequest.MaximumQueryBytes + 1)));
        Assert.Throws<ArgumentException>(() => new AgentWebSearchRequest("\ud800"));
    }

    [Fact]
    public void CatalogUsesExistingWebFetchPolicyAndObservationRisk()
    {
        foreach (var toolName in new[]
        {
            BuiltInAgentTools.HttpFetch,
            BuiltInAgentTools.WebRead,
            BuiltInAgentTools.WebSearch,
        })
        {
            Assert.True(BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor));
            Assert.Equal(AgentCapability.WebFetch, descriptor!.Capability);
            Assert.Equal(AgentActionRisk.Observation, descriptor.Risk);
        }

        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.WebFetch));
    }

    [Fact]
    public void FetchAndReadBindExactMethodFormatAndAddress()
    {
        var composer = new AgentWebToolActionComposer();
        var fetch = composer.Prepare(
            Envelope("action-1"),
            Context(),
            new AgentHttpFetchRequest(
                "https://api.example.test/v1/items",
                AgentHttpFetchMethod.Head));
        var read = composer.Prepare(
            Envelope("action-1"),
            Context(),
            new AgentWebReadRequest(
                "https://docs.example.test/guide",
                AgentWebReadFormat.RenderedHtml));

        Assert.Equal(BuiltInAgentTools.HttpFetch, fetch.Proposal.ToolName);
        Assert.Equal("api.example.test", fetch.Proposal.Presentation.Host);
        Assert.Equal(BuiltInAgentTools.WebRead, read.Proposal.ToolName);
        Assert.Equal("docs.example.test", read.Proposal.Presentation.Host);
        Assert.NotEqual(fetch.Proposal.ArgumentDigest, read.Proposal.ArgumentDigest);
    }

    [Theory]
    [InlineData("file:///tmp/private")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test/#fragment")]
    public void FetchAndReadRejectUnsafeAddresses(string url)
    {
        Assert.Throws<ArgumentException>(() => new AgentHttpFetchRequest(url));
        Assert.Throws<ArgumentException>(() => new AgentWebReadRequest(url));
    }

    private static AgentContextSnapshot Context(
        long revision = 3,
        AgentTarget? target = null)
    {
        var panel = new PanelInstance(
            Panel(),
            PanelKind.Browser,
            "Browser",
            Session());
        var tab = new TabInstance(Tab(), "Web", [panel], panel.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(Workspace(), "Workspace", [tab], tab.Id),
            revision,
            revision);
        var descriptor = new SessionDescriptor(
            Session(),
            PanelKind.Browser,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                Panel()),
            CapabilitySet.Empty,
            revision,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return new AgentContextSnapshot(
            target ?? Target(),
            [AgentContextPanel.ForGraphPanel(graph, Tab(), Panel(), descriptor)],
            Now);
    }

    private static AgentActionEnvelope Envelope(string actionId) =>
        new(
            new AgentActionId(actionId),
            new AgentRunId("web-search-run"),
            new ActorDescriptor(
                new ActorId("web-search-agent"),
                ActorKind.Agent,
                "Web search agent"),
            policyGeneration: 7,
            Now,
            Now.AddMinutes(1));

    private static AgentTarget.OpenTab Target() =>
        new(Window(), Workspace(), Tab());

    private static WindowInstanceId Window() => new("web-search-window");

    private static WorkspaceInstanceId Workspace() => new("web-search-workspace");

    private static TabInstanceId Tab() => new("web-search-tab");

    private static PanelInstanceId Panel() => new("web-search-panel");

    private static SessionId Session() => new("web-search-session");
}
