using Asura.Core;
using Asura.Docker;

namespace Asura.Application.Tests;

public sealed class AgentDockerReadActionComposerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    public static IEnumerable<object[]> ToolNames()
    {
        yield return [BuiltInAgentTools.DockerReadState];
        yield return [BuiltInAgentTools.DockerInspect];
        yield return [BuiltInAgentTools.DockerLogs];
        yield return [BuiltInAgentTools.DockerFilesList];
        yield return [BuiltInAgentTools.DockerFilesStat];
        yield return [BuiltInAgentTools.DockerFileRead];
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void CatalogUsesDockerObservationCapability(string toolName)
    {
        Assert.True(BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor));
        Assert.Equal(AgentCapability.DockerData, descriptor!.Capability);
        Assert.Equal(AgentActionRisk.Observation, descriptor.Risk);
        Assert.Equal(
            AgentPermission.Off,
            AgentPolicy.Default.GetPermission(AgentCapability.DockerData));
    }

    [Fact]
    public void PreparationNarrowsBroadScopeAndBindsTheTypedRequest()
    {
        var composer = new AgentDockerReadActionComposer();
        var request = new AgentDockerReadRequest.ReadState(PanelId(), 25);

        var action = composer.Prepare(
            Envelope(),
            Context(new AgentTarget.Workspace(WindowId(), WorkspaceId())),
            request);
        var binding = composer.BindForExecution(
            action,
            ExactContext(SessionCapabilities.DockerReadState));

        Assert.Equal(ExactPanel(), action.Proposal.Target);
        Assert.Equal(BuiltInAgentTools.DockerReadState, binding.ToolName);
        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
    }

    [Fact]
    public void FileProjectionAcceptsOnlyStrictUtf8Text()
    {
        var composer = new AgentDockerReadActionComposer();
        var request = new AgentDockerReadRequest.FileRead(
            PanelId(),
            new DockerResourceReferenceId("opaque_ref"),
            "/srv/config.txt",
            64);
        var action = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.DockerFilesRead),
            request);
        var resource = Resource(request.Resource);

        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            new DockerFileSnapshot(
                resource,
                request.Path,
                new byte[] { 0xff, 0xfe },
                IsTruncated: false)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            new DockerFileSnapshot(
                resource,
                request.Path,
                "a\0b"u8.ToArray(),
                IsTruncated: false)));

        var projected = Assert.IsType<AgentDockerReadResult.FileText>(
            composer.Project(
                action,
                new DockerFileSnapshot(
                    resource,
                    request.Path,
                    "hello\n"u8.ToArray(),
                    IsTruncated: false)));
        Assert.Equal("hello\n", projected.Value.Text);
    }

    [Fact]
    public void ProjectionRejectsUnsafeInspectAndOversizedAggregate()
    {
        var composer = new AgentDockerReadActionComposer();
        var reference = new DockerResourceReferenceId("opaque_ref");
        var inspectAction = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.DockerInspect),
            new AgentDockerReadRequest.Inspect(PanelId(), reference));
        Assert.Throws<ArgumentException>(() => composer.Project(
            inspectAction,
            new DockerInspectionSnapshot(
                Resource(reference),
                [new DockerInspectionProperty(
                    "Config.Cmd",
                    "--password=hunter2")],
                IsTruncated: false)));

        var stateAction = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.DockerReadState),
            new AgentDockerReadRequest.ReadState(PanelId(), 100));
        var containers = Enumerable.Range(0, 100)
            .Select(index => new DockerContainerItem(
                Resource(new DockerResourceReferenceId($"ref_{index}")),
                new string('i', 1_024),
                "running",
                new string('s', 1_024),
                new string('p', 2_048),
                "now",
                "1%",
                "1MiB",
                "0/0",
                "0/0"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => composer.Project(
            stateAction,
            DockerEngineGeneration.New(),
            new DockerPanelSnapshot(
                new DockerEngineSummary("1", "Linux", "amd64", "1"),
                containers,
                [],
                [],
                [],
                DateTimeOffset.UnixEpoch,
                IsTruncated: false)));
    }

    [Theory]
    [InlineData("/srv/password=hunter2")]
    [InlineData("/srv/../secret")]
    public void ModelVisiblePathsRejectSecretsAndTraversal(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentDockerReadRequest.FileRead(
                PanelId(),
                new DockerResourceReferenceId("opaque_ref"),
                path,
                64));
    }

    private static DockerResourceItem Resource(DockerResourceReferenceId reference) =>
        new(reference, DockerResourceKind.Container, "api");

    private static AgentContextSnapshot Context(AgentTarget target) =>
        new(
            target,
            [AgentContextPanel.ForGraphPanel(
                Graph(),
                TabId(),
                PanelId(),
                Descriptor(AllCapabilities()))],
            Now);

    private static AgentContextSnapshot ExactContext(string capability) =>
        new(
            ExactPanel(),
            [AgentContextPanel.ForGraphPanel(
                Graph(),
                TabId(),
                PanelId(),
                Descriptor([capability]))],
            Now);

    private static WorkspaceGraphSnapshot Graph()
    {
        var panel = new PanelInstance(
            PanelId(),
            PanelKind.Docker,
            "Docker",
            SessionId());
        var tab = new TabInstance(TabId(), "Docker", [panel], panel.Id);
        return new WorkspaceGraphSnapshot(
            WindowId(),
            new WorkspaceInstance(WorkspaceId(), "Docker", [tab], tab.Id),
            revision: 11,
            lastSequence: 11);
    }

    private static SessionDescriptor Descriptor(IReadOnlyList<string> capabilities) =>
        new(
            SessionId(),
            PanelKind.Docker,
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
            StatusDetail: "Ready");

    private static string[] AllCapabilities() =>
    [
        SessionCapabilities.DockerReadState,
        SessionCapabilities.DockerInspect,
        SessionCapabilities.DockerReadLogs,
        SessionCapabilities.DockerFilesList,
        SessionCapabilities.DockerFilesStat,
        SessionCapabilities.DockerFilesRead,
    ];

    private static AgentActionEnvelope Envelope() =>
        new(
            AgentActionId.New(),
            new AgentRunId("docker-run"),
            new ActorDescriptor(
                new ActorId("docker-agent"),
                ActorKind.Agent,
                "Docker agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentTarget.Panel ExactPanel() =>
        new(WindowId(), WorkspaceId(), TabId(), PanelId());

    private static WindowInstanceId WindowId() => new("docker-window");

    private static WorkspaceInstanceId WorkspaceId() => new("docker-workspace");

    private static TabInstanceId TabId() => new("docker-tab");

    private static PanelInstanceId PanelId() => new("docker-panel");

    private static SessionId SessionId() => new("docker-session");
}
