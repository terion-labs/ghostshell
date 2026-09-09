using Asura.Core;
using Asura.Git;

namespace Asura.Application.Tests;

public sealed class AgentGitActionComposerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    public static IEnumerable<object[]> Requests()
    {
        yield return [new AgentGitRequest.ReadState(PanelId())];
        yield return [new AgentGitRequest.ReadDiff(
            PanelId(), State(), Change(), GitChangeArea.Unstaged)];
        yield return [new AgentGitRequest.ReadRemoteRef(
            PanelId(), State(), Remote(), Branch())];
        yield return [new AgentGitRequest.Stage(PanelId(), State(), Change())];
        yield return [new AgentGitRequest.Unstage(PanelId(), State(), Change())];
        yield return [new AgentGitRequest.BranchCreate(PanelId(), State(), "feature/safe")];
        yield return [new AgentGitRequest.BranchCheckout(PanelId(), State(), Branch())];
        yield return [new AgentGitRequest.Commit(PanelId(), State(), "Safe subject", "Safe body")];
        yield return [new AgentGitRequest.Push(
            PanelId(), State(), RemoteState(), Remote(), Branch())];
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void PreparationNarrowsBroadScopeAndRebindsEveryClosedVariant(
        AgentGitRequest request)
    {
        var composer = new AgentGitActionComposer();
        var action = composer.Prepare(
            Envelope(),
            Context(new AgentTarget.Workspace(WindowId(), WorkspaceId())),
            request);

        var binding = composer.BindForExecution(
            action,
            ExactContext(request.RequiredSessionCapability));

        Assert.Equal(ExactPanel(), action.Proposal.Target);
        Assert.Equal(request.ToolName, binding.ToolName);
        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        Assert.DoesNotContain(
            action.Proposal.Presentation.Arguments,
            argument => argument.DisplayValue.Contains("/Users/", StringComparison.Ordinal)
                || argument.DisplayValue.Contains("https://", StringComparison.Ordinal));
    }

    [Fact]
    public void QuarantineRejectsMutationButStillAllowsStateReconciliation()
    {
        var composer = new AgentGitActionComposer();
        var context = ExactContext(
            SessionCapabilities.GitStage,
            quarantined: true);
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            context,
            new AgentGitRequest.Stage(PanelId(), State(), Change())));

        var read = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.GitReadState, quarantined: true),
            new AgentGitRequest.ReadState(PanelId()));
        Assert.Equal(GitAgentToolNames.ReadState, read.Proposal.ToolName);
    }

    [Fact]
    public void RequestArgumentsRejectLiteralSecretsAndUnknownDiffAreas()
    {
        Assert.Throws<ArgumentException>(() => new AgentGitRequest.Commit(
            PanelId(),
            State(),
            "password=hunter2",
            body: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentGitRequest.ReadDiff(
            PanelId(),
            State(),
            Change(),
            (GitChangeArea)999));
    }

    [Theory]
    [InlineData(GitAgentToolNames.ReadState, AgentCapability.GitData, AgentActionRisk.Observation)]
    [InlineData(GitAgentToolNames.ReadDiff, AgentCapability.GitData, AgentActionRisk.Observation)]
    [InlineData(GitAgentToolNames.ReadRemoteRef, AgentCapability.GitData, AgentActionRisk.Routine)]
    [InlineData(GitAgentToolNames.Stage, AgentCapability.Git, AgentActionRisk.Mutation)]
    [InlineData(GitAgentToolNames.Unstage, AgentCapability.Git, AgentActionRisk.Mutation)]
    [InlineData(GitAgentToolNames.BranchCreate, AgentCapability.Git, AgentActionRisk.Mutation)]
    [InlineData(GitAgentToolNames.BranchCheckout, AgentCapability.Git, AgentActionRisk.Mutation)]
    [InlineData(GitAgentToolNames.Commit, AgentCapability.Git, AgentActionRisk.Mutation)]
    [InlineData(GitAgentToolNames.Push, AgentCapability.Git, AgentActionRisk.Privileged)]
    public void CatalogUsesTheSplitGitAuthoritiesAndExactRisk(
        string name,
        AgentCapability capability,
        AgentActionRisk risk)
    {
        Assert.True(BuiltInAgentTools.Catalog.TryGet(name, out var descriptor));
        Assert.Equal(capability, descriptor!.Capability);
        Assert.Equal(risk, descriptor.Risk);
    }

    [Fact]
    public void ContextFingerprintChangesWithRepositoryBindingAndQuarantine()
    {
        var baseline = ExactContext(SessionCapabilities.GitReadState);
        var quarantined = ExactContext(
            SessionCapabilities.GitReadState,
            quarantined: true);

        Assert.NotEqual(baseline.BindingFingerprint, quarantined.BindingFingerprint);
    }

    private static AgentContextSnapshot Context(AgentTarget target) =>
        new(
            target,
            [AgentContextPanel.ForGraphPanel(
                Graph(),
                TabId(),
                PanelId(),
                Descriptor(AllCapabilities()))],
            Now);

    private static AgentContextSnapshot ExactContext(
        string capability,
        bool quarantined = false) =>
        new(
            ExactPanel(),
            [AgentContextPanel.ForGraphPanel(
                Graph(),
                TabId(),
                PanelId(),
                Descriptor([capability], quarantined))],
            Now);

    private static WorkspaceGraphSnapshot Graph()
    {
        var panel = new PanelInstance(PanelId(), PanelKind.Git, "Repository", SessionId());
        var tab = new TabInstance(TabId(), "Git", [panel], panel.Id);
        return new WorkspaceGraphSnapshot(
            WindowId(),
            new WorkspaceInstance(WorkspaceId(), "Git", [tab], tab.Id),
            revision: 11,
            lastSequence: 11);
    }

    private static SessionDescriptor Descriptor(
        IReadOnlyList<string> capabilities,
        bool quarantined = false) =>
        new(
            SessionId(),
            PanelKind.Git,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                WindowId(),
                WorkspaceId(),
                TabId(),
                PanelId()),
            new CapabilitySet(capabilities),
            Revision: 17,
            HasActiveWork: false,
            StatusDetail: "Ready",
            GitMetadata: new GitSessionMetadata(
                new GitRepositoryIdentity(new string('a', 64)),
                BindingRevision: 3,
                "Local",
                ConnectionKind.Local,
                quarantined));

    private static string[] AllCapabilities() =>
    [
        SessionCapabilities.GitReadState,
        SessionCapabilities.GitReadDiff,
        SessionCapabilities.GitReadRemoteRef,
        SessionCapabilities.GitStage,
        SessionCapabilities.GitUnstage,
        SessionCapabilities.GitBranchCreate,
        SessionCapabilities.GitBranchCheckout,
        SessionCapabilities.GitCommit,
        SessionCapabilities.GitPush,
    ];

    private static AgentActionEnvelope Envelope() =>
        new(
            AgentActionId.New(),
            new AgentRunId("git-run"),
            new ActorDescriptor(new ActorId("git-agent"), ActorKind.Agent, "Git agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static GitStateReferenceId State() => new("state_ref");

    private static GitChangeReferenceId Change() => new("change_ref");

    private static GitBranchReferenceId Branch() => new("branch_ref");

    private static GitRemoteReferenceId Remote() => new("remote_ref");

    private static GitRemoteStateReferenceId RemoteState() => new("remote_state_ref");

    private static AgentTarget.Panel ExactPanel() =>
        new(WindowId(), WorkspaceId(), TabId(), PanelId());

    private static WindowInstanceId WindowId() => new("git-window");

    private static WorkspaceInstanceId WorkspaceId() => new("git-workspace");

    private static TabInstanceId TabId() => new("git-tab");

    private static PanelInstanceId PanelId() => new("git-panel");

    private static SessionId SessionId() => new("git-session");
}
