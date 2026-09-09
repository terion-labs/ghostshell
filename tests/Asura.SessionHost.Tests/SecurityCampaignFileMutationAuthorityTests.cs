using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class SecurityCampaignFileMutationAuthorityTests
{
    [Fact(DisplayName = "authority.files.create_text broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.files.create_text")]
    public Task CreateTextAsync() => ExerciseAsync(GovernedFileToolNames.CreateText);

    [Fact(DisplayName = "authority.files.replace_text broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.files.replace_text")]
    public Task ReplaceTextAsync() => ExerciseAsync(GovernedFileToolNames.ReplaceText);

    [Fact(DisplayName = "authority.files.copy broker host and sink")]
    [Trait("SecurityCampaignCase", "authority.files.copy")]
    public Task CopyAsync() => ExerciseAsync(GovernedFileToolNames.Copy);

    private static async Task ExerciseAsync(string toolName)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        var fileFactory = new FakeFilePanelSessionFactory
        {
            MetadataFactory = root => new FileSessionMetadata(
                root,
                FilePanelCapability.List
                | FilePanelCapability.Stat
                | FilePanelCapability.RangedRead
                | FilePanelCapability.StreamingWrite
                | FilePanelCapability.Copy
                | FilePanelCapability.GovernedCreateFile
                | FilePanelCapability.GovernedReplaceFile
                | FilePanelCapability.GovernedCopySource
                | FilePanelCapability.GovernedCopy,
                maximumListPageSize: 100,
                maximumPreviewBytes: 64 * 1024),
        };
        var composer = new AgentFileActionComposer();
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            filePanelFactory: fileFactory,
            agentAuthorizationConsumer: broker,
            agentFileActionComposer: composer);
        var ids = new TestIds(toolName.Replace('.', '-'));
        var human = new ActorDescriptor(
            new ActorId(ids.ClientId.Value),
            ActorKind.Human,
            "File security campaign user",
            ids.ClientId);
        var humanContext = new OperationContext(
            RequestId.New(),
            human,
            CancellationId: CancellationId.New());
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(ids.WindowId, ids.Workspace()),
            humanContext,
            CancellationToken.None)).Value();
        _ = (await host.EnsureFilePanelSessionAsync(
            new EnsureFilePanelSessionRequest(
                ids.SessionId,
                ids.Owner,
                "Files",
                ids.Root),
            humanContext,
            CancellationToken.None)).Value();

        var agent = new ActorDescriptor(
            new ActorId($"agent-{ids.Suffix}"),
            ActorKind.Agent,
            "File security campaign agent");
        var runId = new AgentRunId($"run-{ids.Suffix}");
        var workspaceTarget = new AgentTarget.Workspace(
            ids.WindowId,
            ids.WorkspaceId);
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions
                .SetItem(AgentCapability.ReadFiles, AgentPermission.Auto)
                .SetItem(AgentCapability.EditFiles, AgentPermission.Ask),
        };
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                runId,
                agent,
                ids.ClientId,
                workspaceTarget,
                policy,
                policyGeneration: 1),
            CancellationToken.None));
        var agentContext = new OperationContext(
            RequestId.New(),
            agent,
            CancellationId: CancellationId.New());
        var exactTarget = new AgentTarget.Panel(
            ids.WindowId,
            ids.WorkspaceId,
            ids.TabId,
            ids.PanelId);
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(exactTarget),
            agentContext,
            CancellationToken.None)).Value();
        var sourcePath = Path("source.txt");
        AgentFileEntryReference? reference = null;
        if (toolName is GovernedFileToolNames.ReplaceText or GovernedFileToolNames.Copy)
        {
            var stat = Prepare(
                composer,
                clock,
                runId,
                agent,
                context,
                new AgentFileRequest.Stat(ids.SessionId, sourcePath));
            var statAuthorization = Assert.IsType<AgentAuthorizationResult.Authorized>(
                await broker.RequestAsync(stat.Proposal, CancellationToken.None));
            var statResult = await host.RunAgentFileActionAsync(
                statAuthorization.Authorization.Id,
                stat,
                CancellationToken.None);
            reference = Assert.IsType<AgentFileActionResult.Entry>(statResult.Value())
                .Reference;
            Assert.NotNull(reference);
        }

        AgentFileRequest request = toolName switch
        {
            GovernedFileToolNames.CreateText => new AgentFileRequest.CreateText(
                ids.SessionId,
                Path("created.txt"),
                "created content"),
            GovernedFileToolNames.ReplaceText => new AgentFileRequest.ReplaceText(
                ids.SessionId,
                sourcePath,
                reference!.Value,
                "replacement content"),
            GovernedFileToolNames.Copy => new AgentFileRequest.Copy(
                ids.SessionId,
                sourcePath,
                reference!.Value,
                Path("archive", "source.txt")),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName)),
        };
        var action = Prepare(composer, clock, runId, agent, context, request);
        Assert.Equal(toolName, action.Proposal.ToolName);
        var required = Assert.IsType<AgentAuthorizationResult.ApprovalRequired>(
            await broker.RequestAsync(action.Proposal, CancellationToken.None));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.DecideAsync(
                new AgentApprovalDecision(
                    required.Approval.Id,
                    human,
                    approved: true,
                    AgentApprovalDuration.Once,
                    clock.GetUtcNow()),
                CancellationToken.None));
        Assert.Equal(
            AgentAuthorizationSource.HumanApproval,
            authorized.Authorization.Source);

        var result = await host.RunAgentFileActionAsync(
            authorized.Authorization.Id,
            action,
            CancellationToken.None);
        var session = fileFactory[ids.SessionId];
        VerifyExactSinkAndReceipt(toolName, result.Value(), session);
        Assert.Contains(audit.Events, auditEvent =>
            string.Equals(
                auditEvent.CorrelationId,
                action.Proposal.Id.Value,
                StringComparison.Ordinal)
            && auditEvent.Outcome == AuditOutcome.Succeeded);
    }

    private static AgentFileAction Prepare(
        AgentFileActionComposer composer,
        ManualTimeProvider clock,
        AgentRunId runId,
        ActorDescriptor agent,
        AgentContextSnapshot context,
        AgentFileRequest request)
    {
        var now = clock.GetUtcNow();
        return composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                runId,
                agent,
                policyGeneration: 1,
                now,
                now.AddMinutes(1)),
            context,
            request);
    }

    private static void VerifyExactSinkAndReceipt(
        string toolName,
        AgentFileActionResult result,
        FakeFilePanelSession session)
    {
        switch (toolName)
        {
            case GovernedFileToolNames.CreateText:
                {
                    var receipt = Assert.IsType<AgentFileActionResult.CreatedText>(result).Value;
                    var request = Assert.IsType<FilePanelTextWriteRequest>(
                        session.LastWriteTextRequest);
                    Assert.Equal(["srv", "campaign", "created.txt"], Segments(request.Location));
                    Assert.Equal("created content", request.Content);
                    Assert.Equal(
                        FilePanelMutationPreconditionKind.MustNotExist,
                        request.Precondition.Kind);
                    Assert.False(receipt.ReplacedExisting);
                    Assert.Equal(15, receipt.BytesWritten);
                    Assert.Equal(1, session.WriteTextCount);
                    break;
                }
            case GovernedFileToolNames.ReplaceText:
                {
                    var receipt = Assert.IsType<AgentFileActionResult.ReplacedText>(result).Value;
                    var request = Assert.IsType<FilePanelTextWriteRequest>(
                        session.LastWriteTextRequest);
                    Assert.Equal(["srv", "campaign", "source.txt"], Segments(request.Location));
                    Assert.Equal("test-version", request.Location.Version);
                    Assert.Equal("replacement content", request.Content);
                    Assert.Equal(
                        FilePanelMutationPreconditionKind.VersionMatches,
                        request.Precondition.Kind);
                    Assert.True(receipt.ReplacedExisting);
                    Assert.Equal(19, receipt.BytesWritten);
                    Assert.Equal(1, session.WriteTextCount);
                    break;
                }
            case GovernedFileToolNames.Copy:
                {
                    var receipt = Assert.IsType<AgentFileActionResult.Copied>(result).Value;
                    var request = Assert.IsType<FilePanelCopyRequest>(session.LastCopyRequest);
                    Assert.Equal(["srv", "campaign", "source.txt"], Segments(request.Source));
                    Assert.Equal("test-version", request.Source.Version);
                    Assert.Equal(
                        ["srv", "campaign", "archive", "source.txt"],
                        Segments(request.Destination));
                    Assert.Equal(AgentFileActionComposer.MaximumAgentCopyBytes, request.MaximumBytes);
                    Assert.Equal(7, receipt.BytesCopied);
                    Assert.Equal(1, session.CopyCount);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(toolName));
        }
    }

    private static ImmutableArray<FilePanelPathSegment> Path(params string[] segments) =>
        [.. segments.Select(segment => new FilePanelPathSegment(segment))];

    private static string[] Segments(FilePanelLocation location) =>
        [.. Assert.IsType<FilePanelAddress.Hierarchical>(location.Address)
            .Path.Segments
            .Select(segment => segment.Value)];

    private sealed class TestIds(string suffix)
    {
        public string Suffix { get; } = suffix;

        public WindowInstanceId WindowId { get; } = new($"window-{suffix}");

        public WorkspaceInstanceId WorkspaceId { get; } = new($"workspace-{suffix}");

        public TabInstanceId TabId { get; } = new($"tab-{suffix}");

        public PanelInstanceId PanelId { get; } = new($"panel-{suffix}");

        public SessionId SessionId { get; } = new($"session-{suffix}");

        public ClientId ClientId { get; } = new($"client-{suffix}");

        public FilePanelLocation Root { get; } = new(
            "security.files",
            "security.example",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                [
                    new FilePanelPathSegment("srv"),
                    new FilePanelPathSegment("campaign"),
                ])));

        public SessionOwner Owner => new(
            HostMode.Desktop,
            WindowId,
            WorkspaceId,
            TabId,
            PanelId);

        public WorkspaceInstance Workspace()
        {
            var panel = new PanelInstance(PanelId, PanelKind.FileViewer, "Files");
            var tab = new TabInstance(TabId, "Files", [panel], PanelId);
            return new WorkspaceInstance(WorkspaceId, "Files", [tab], TabId);
        }
    }

    private sealed class InMemoryAuditStore : IAuditStore
    {
        private readonly List<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events => _events;

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add(auditEvent);
            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AuditEventRecord> matches =
            [
                .. _events.Where(item => string.Equals(
                    item.CorrelationId,
                    correlationId,
                    StringComparison.Ordinal)),
            ];
            return ValueTask.FromResult(
                AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(matches));
        }
    }
}
