using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class AgentProcessSessionHostTests
{
    [Fact]
    public async Task HostileMonitorOutputIsProjectedAndAuditedWithCountOnly()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var session = fixture.ProcessSession;
        var longName = new string('a', 200);
        session.Snapshot = new ProcessMonitorSnapshot(
            DateTimeOffset.UnixEpoch,
            [
                Process(41, "/Users/alice/.ssh/id_rsa", cpu: 1),
                Process(7, longName, cpu: 80),
                Process(19, "asura", cpu: 20, isAsura: true),
            ],
            EnumeratedProcessCount: 3,
            ObservedProcessCount: 3,
            IsTruncated: true);
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(
                fixture.ProcessPanelId,
                limit: 16,
                ProcessMonitorSort.ProcessIdAscending));

        var result = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Value();

        Assert.Equal([7, 19, 41], result.Processes.Select(process => process.ProcessId));
        Assert.Equal(3, result.ReturnedCount);
        Assert.Equal(1, result.RedactedNameCount);
        Assert.Equal(1, result.TruncatedNameCount);
        Assert.True(result.Processes.Single(process => process.ProcessId == 41).Name.Redacted);
        Assert.True(result.Processes.Single(process => process.ProcessId == 7).Name.Truncated);
        Assert.DoesNotContain(
            result.Processes,
            process => process.Name.Text.Contains("id_rsa", StringComparison.Ordinal));
        Assert.Equal(1, session.ListCount);
        Assert.Equal(
            new ProcessMonitorQuery(16, ProcessMonitorSort.ProcessIdAscending),
            session.LastQuery);

        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("processes_listed", completion.StableCode);
        Assert.Equal(3, completion.ResultCount);
        Assert.DoesNotContain("id_rsa", completion.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(longName, completion.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphRevisionDriftAfterCaptureDiscardsTheCapturedProjection()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.Snapshot = Snapshot(
            Process(11, "captured-but-discarded", cpu: 50));
        fixture.ProcessSession.BlockList = true;
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var execution = fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.ProcessSession.ListStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var before = await fixture.GraphAsync();
        var graphMutation = fixture.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                fixture.WorkspaceId,
                fixture.TabId,
                fixture.StatisticsPanelId),
            fixture.HumanContext(before.Revision),
            default).AsTask();
        _ = (await graphMutation.WaitAsync(
            TimeSpan.FromSeconds(5))).Value();
        fixture.ProcessSession.ReleaseList.TrySetResult();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ResultCount);
    }

    [Fact]
    public async Task CapabilityDriftAfterCaptureDiscardsTheCapturedProjection()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.Snapshot = Snapshot(
            Process(21, "also-discarded", cpu: 10));
        fixture.ProcessSession.BlockList = true;
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var execution = fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.ProcessSession.ListStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        fixture.ProcessSession.RemoveCapability(
            SessionCapabilities.ProcessesList);
        fixture.ProcessSession.ReleaseList.TrySetResult();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.CapabilityNotSupported, failure.Code);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        Assert.Null(
            Assert.Single(fixture.Authorization.Completions).ResultCount);
    }

    [Fact]
    public async Task SessionRevisionDriftAfterCaptureDiscardsTheCapturedProjection()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.Snapshot = Snapshot(
            Process(22, "revision-drift-discarded", cpu: 10));
        fixture.ProcessSession.BlockList = true;
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var execution = fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.ProcessSession.ListStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        _ = (await fixture.Client.AttachAsync(
            new AttachSessionRequest(
                fixture.ProcessSessionId,
                fixture.ClientId,
                AttachmentKind.ReadOnly,
                new ViewportDescriptor(800, 600, 1),
                SessionHostTestHarness.AllCapabilities()),
            fixture.HumanContext(),
            default)).Value();
        fixture.ProcessSession.ReleaseList.TrySetResult();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        Assert.Null(
            Assert.Single(fixture.Authorization.Completions).ResultCount);
    }

    [Fact]
    public async Task ClosingTheSessionCancelsAnActiveAgentCapture()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.BlockList = true;
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var execution = fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.ProcessSession.ListStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        _ = (await fixture.Client.CloseAsync(
            CloseScopeRequest.Session(
                fixture.ProcessSessionId,
                CloseDecision.Request),
            fixture.HumanContext(),
            default)).Value();
        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.Cancelled, failure.Code);
        Assert.Equal("session_revoked", failure.StableCode);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("session_revoked", completion.StableCode);
        Assert.Null(completion.ResultCount);
    }

    [Fact]
    public async Task CompletionAuditUncertaintyDoesNotRecaptureProcesses()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.Snapshot = Snapshot(
            Process(31, "captured-once", cpu: 5));
        fixture.Authorization.CompletionError =
            new AgentAuthorizationError(
                AgentAuthorizationErrorCode.AuditUnavailable,
                "Audit unavailable.");
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.EngineFailed, failure.Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            failure.StableCode);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        Assert.Equal(2, fixture.Authorization.CompletionAttempts);
        Assert.All(
            fixture.Authorization.Completions,
            completion => Assert.Equal(1, completion.ResultCount));
    }

    [Fact]
    public async Task NativeMonitorFailureTextDoesNotCrossTheHostOrAuditBoundary()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        const string nativeSecret = "/private/token=super-secret";
        fixture.ProcessSession.ListError = new MonitorPanelError(
            MonitorPanelErrorCode.CaptureFailed,
            "native_secret_code",
            nativeSecret,
            Retryable: true);
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.EngineFailed, failure.Code);
        Assert.Equal("processes_capture_failed", failure.StableCode);
        Assert.DoesNotContain(nativeSecret, failure.Message, StringComparison.Ordinal);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal("processes_capture_failed", completion.StableCode);
        Assert.Null(completion.ResultCount);
        Assert.DoesNotContain(
            nativeSecret,
            completion.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PreparedActionMismatch.Tool)]
    [InlineData(PreparedActionMismatch.ArgumentDigest)]
    public async Task PreparedActionTamperingIsRejectedBeforeAuthorizationOrCapture(
        PreparedActionMismatch mismatch)
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        var proposal = mismatch switch
        {
            PreparedActionMismatch.Tool => CopyProposal(
                prepared.Proposal,
                toolName: BuiltInAgentTools.TerminalReadScreen),
            PreparedActionMismatch.ArgumentDigest => CopyProposal(
                prepared.Proposal,
                argumentDigest: AgentActionDigest.FromUtf8(
                    "tampered-process-arguments")),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var tampered = new AgentProcessListAction(
            prepared.Request,
            proposal);

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(tampered),
            tampered,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, fixture.Authorization.ConsumeAttempts);
        Assert.Equal(0, fixture.ProcessSession.ListCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Theory]
    [InlineData(PermitMismatch.Tool)]
    [InlineData(PermitMismatch.ArgumentDigest)]
    [InlineData(PermitMismatch.PolicyGeneration)]
    public async Task MismatchedConsumedPermitIsRejectedBeforeCapture(
        PermitMismatch mismatch)
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        fixture.Authorization.Mismatch = mismatch;

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(1, fixture.Authorization.ConsumeAttempts);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Equal(0, fixture.ProcessSession.ListCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Null(completion.ResultCount);
    }

    [Fact]
    public async Task AuthorizationDenialDoesNotCaptureOrComplete()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        fixture.Authorization.ConsumeError = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.PolicyDenied,
            "Denied.");

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(1, fixture.Authorization.ConsumeAttempts);
        Assert.Equal(0, fixture.ProcessSession.ListCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task WrongAuthorizationIdDoesNotCaptureOrComplete()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        _ = fixture.Authorization.Arm(action);

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            AgentAuthorizationId.New(),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(1, fixture.Authorization.ConsumeAttempts);
        Assert.Equal(0, fixture.ProcessSession.ListCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task StaleExactGraphTargetDoesNotCapture()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        var graph = await fixture.GraphAsync();
        _ = (await fixture.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                fixture.WorkspaceId,
                fixture.TabId,
                fixture.StatisticsPanelId),
            fixture.HumanContext(graph.Revision),
            default)).Value();

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, fixture.ProcessSession.ListCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task StaleExactSessionRevisionDoesNotCapture()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        _ = (await fixture.Client.AttachAsync(
            new AttachSessionRequest(
                fixture.ProcessSessionId,
                fixture.ClientId,
                AttachmentKind.ReadOnly,
                new ViewportDescriptor(800, 600, 1),
                SessionHostTestHarness.AllCapabilities()),
            fixture.HumanContext(),
            default)).Value();

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, fixture.ProcessSession.ListCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Theory]
    [InlineData(ActiveCancellation.Caller, "caller_cancelled")]
    [InlineData(ActiveCancellation.Permit, "authority_revoked")]
    public async Task ActiveCancellationInterruptsOneCaptureAndAuditsNoCount(
        ActiveCancellation cancellationKind,
        string expectedStableCode)
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.BlockList = true;
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        using var callerCancellation = new CancellationTokenSource();
        var execution = fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            callerCancellation.Token).AsTask();
        await fixture.ProcessSession.ListStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        if (cancellationKind == ActiveCancellation.Caller)
        {
            callerCancellation.Cancel();
        }
        else
        {
            fixture.Authorization.RevokePermit();
        }

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.Cancelled, failure.Code);
        Assert.Equal(expectedStableCode, failure.StableCode);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal(expectedStableCode, completion.StableCode);
        Assert.Null(completion.ResultCount);
    }

    [Fact]
    public async Task MalformedSnapshotFailsClosedWithoutLeakingProcessContent()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        const string hostileName = "process-content-canary";
        fixture.ProcessSession.Snapshot = Snapshot(
            Process(77, hostileName, cpu: 1),
            Process(77, hostileName, cpu: 2));
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.EngineFailed, failure.Code);
        Assert.Equal("processes_result_invalid", failure.StableCode);
        Assert.DoesNotContain(hostileName, failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal("processes_result_invalid", completion.StableCode);
        Assert.Null(completion.ResultCount);
        Assert.DoesNotContain(
            hostileName,
            completion.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MonitorPanelErrorCode.AccessDenied)]
    [InlineData(MonitorPanelErrorCode.Unavailable)]
    public async Task MonitorAvailabilityFailuresMapToClosedCodes(
        MonitorPanelErrorCode errorCode)
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.ListError = new MonitorPanelError(
            errorCode,
            "provider-secret-code",
            "provider-native-secret",
            Retryable: true);
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));

        var failure = (await fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.EngineFailed, failure.Code);
        Assert.Equal("processes_unavailable", failure.StableCode);
        Assert.DoesNotContain(
            "provider-native-secret",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        Assert.Null(
            Assert.Single(fixture.Authorization.Completions).ResultCount);
    }

    [Fact]
    public async Task HostDisposalCancelsActiveCaptureWithoutDeadlock()
    {
        await using var fixture = await AgentProcessHostFixture.CreateAsync();
        fixture.ProcessSession.BlockList = true;
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        var execution = fixture.Client.RunAgentProcessListAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.ProcessSession.ListStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        await fixture.Client.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(5));
        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.Cancelled, failure.Code);
        Assert.Equal("session_revoked", failure.StableCode);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        Assert.Equal(1, fixture.ProcessSession.DisposeCount);
        Assert.Null(
            Assert.Single(fixture.Authorization.Completions).ResultCount);
    }

    [Fact]
    public async Task RealBrokerConsumesOnePermitAndDurablyAuditsOnlyTheResultCount()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentProcessHostFixture.CreateAsync(
            clock,
            broker);
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.ProcessData,
                AgentPermission.Auto),
        };
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                policy,
                policyGeneration: 0),
            default));
        const int processId = 987_654;
        const string processName = "durable-process-name-canary";
        fixture.ProcessSession.Snapshot = Snapshot(
            Process(processId, processName, cpu: 33));
        var action = await fixture.PrepareAsync(
            new AgentProcessListRequest(fixture.ProcessPanelId, limit: 16));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(action.Proposal, default));

        var result = (await fixture.Client.RunAgentProcessListAsync(
            authorized.Authorization.Id,
            action,
            default)).Value();
        var replay = await fixture.Client.RunAgentProcessListAsync(
            authorized.Authorization.Id,
            action,
            default);

        Assert.Equal(1, result.ReturnedCount);
        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        Assert.Equal(1, fixture.ProcessSession.ListCount);
        var events = audit.Events
            .Where(item => string.Equals(item.CorrelationId, action.Proposal.Id.Value, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            [
                AuditOutcome.Requested,
                AuditOutcome.Approved,
                AuditOutcome.Started,
                AuditOutcome.Succeeded,
            ],
            events.Select(item => item.Outcome));
        Assert.Single(
            events,
            item => item.Outcome == AuditOutcome.Started);
        var completed = Assert.IsType<AuditDetails.AgentActionDetails>(
            events[^1].Details);
        Assert.Equal("processes_listed", completed.ResultCode);
        Assert.Equal(1, completed.Binding.ResultCount);
        Assert.All(events, auditEvent =>
        {
            Assert.DoesNotContain(
                processName,
                auditEvent.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                auditEvent.ToString(),
                StringComparison.Ordinal);
        });
    }

    private static AgentActionProposal CopyProposal(
        AgentActionProposal source,
        string? toolName = null,
        AgentTarget? target = null,
        AgentActionDigest? targetFingerprint = null,
        AgentActionDigest? argumentDigest = null,
        long? policyGeneration = null) =>
        new(
            source.Id,
            source.RunId,
            source.Actor,
            toolName ?? source.ToolName,
            target ?? source.Target,
            targetFingerprint ?? source.TargetFingerprint,
            argumentDigest ?? source.ArgumentDigest,
            source.Presentation,
            policyGeneration ?? source.PolicyGeneration,
            source.CreatedAtUtc,
            source.DeadlineUtc);

    private static ProcessMonitorEntry Process(
        int processId,
        string name,
        double? cpu,
        bool isAsura = false) =>
        new(
            processId,
            name,
            cpu,
            WorkingSetBytes: processId * 1_024,
            TotalProcessorTime: TimeSpan.FromSeconds(processId),
            StartedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(processId),
            isAsura);

    private static ProcessMonitorSnapshot Snapshot(
        params ProcessMonitorEntry[] processes) =>
        new(
            DateTimeOffset.UnixEpoch,
            Array.AsReadOnly(processes),
            processes.Length,
            processes.Length,
            false);

    private sealed class AgentProcessHostFixture : IAsyncDisposable
    {
        private AgentProcessHostFixture(
            ManualTimeProvider? clock = null,
            IAgentAuthorizationConsumer? authorizationConsumer = null)
        {
            Clock = clock ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            Factory = new FakeSystemMonitorPanelSessionFactory();
            Composer = new AgentProcessListActionComposer();
            Authorization = new FakeAuthorizationConsumer(Clock, ClientId);
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                Clock,
                systemMonitorFactory: Factory,
                agentAuthorizationConsumer:
                    authorizationConsumer ?? Authorization,
                agentProcessListActionComposer: Composer);
        }

        public ManualTimeProvider Clock { get; }

        public FakeSystemMonitorPanelSessionFactory Factory { get; }

        public AgentProcessListActionComposer Composer { get; }

        public FakeAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("process-test-client");

        public WindowInstanceId WindowId { get; } = new("process-window");

        public WorkspaceInstanceId WorkspaceId { get; } =
            new("process-workspace");

        public TabInstanceId TabId { get; } = new("process-tab");

        public PanelInstanceId ProcessPanelId { get; } =
            new("process-panel");

        public PanelInstanceId StatisticsPanelId { get; } =
            new("statistics-panel");

        public SessionId ProcessSessionId { get; } =
            new("process-session");

        public AgentRunId RunId { get; } = new("process-run");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("process-agent"),
            ActorKind.Agent,
            "Process agent");

        public FakeProcessMonitorPanelSession ProcessSession =>
            Factory.Processes(ProcessSessionId);

        public static async ValueTask<AgentProcessHostFixture> CreateAsync(
            ManualTimeProvider? clock = null,
            IAgentAuthorizationConsumer? authorizationConsumer = null)
        {
            var fixture = new AgentProcessHostFixture(
                clock,
                authorizationConsumer);
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    fixture.Workspace()),
                fixture.HumanContext(),
                default)).Value();
            _ = (await fixture.Client.EnsureProcessMonitorSessionAsync(
                new EnsureProcessMonitorSessionRequest(
                    fixture.ProcessSessionId,
                    fixture.Owner(),
                    "Process monitor"),
                fixture.HumanContext(),
                default)).Value();
            return fixture;
        }

        public async ValueTask<AgentProcessListAction> PrepareAsync(
            AgentProcessListRequest request)
        {
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Workspace(
                        WindowId,
                        WorkspaceId)),
                AgentContext(),
                default)).Value();
            var now = Clock.GetUtcNow();
            return Composer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    RunId,
                    Agent,
                    policyGeneration: 0,
                    now,
                    now.AddMinutes(1)),
                context,
                request);
        }

        public async ValueTask<WorkspaceGraphSnapshot> GraphAsync() =>
            (await Client.GetWorkspaceGraphAsync(
                WorkspaceId,
                HumanContext(),
                default)).Value();

        public OperationContext HumanContext(
            long? expectedRevision = null) =>
            new(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(ClientId.Value),
                    ActorKind.Human,
                    "Test user",
                    ClientId),
                expectedRevision,
                CancellationId: CancellationId.New());

        public ValueTask DisposeAsync() => Client.DisposeAsync();

        private OperationContext AgentContext() =>
            new(
                RequestId.New(),
                Agent,
                CancellationId: CancellationId.New());

        private SessionOwner Owner() =>
            new(
                HostMode.Desktop,
                WindowId,
                WorkspaceId,
                TabId,
                ProcessPanelId);

        private WorkspaceInstance Workspace()
        {
            var panels = new[]
            {
                new PanelInstance(
                    ProcessPanelId,
                    PanelKind.ProcessMonitor,
                    "Process monitor"),
                new PanelInstance(
                    StatisticsPanelId,
                    PanelKind.Statistics,
                    "Statistics"),
            };
            var tab = new TabInstance(
                TabId,
                "Monitoring",
                panels,
                ProcessPanelId);
            return new WorkspaceInstance(
                WorkspaceId,
                "Workspace",
                [tab],
                TabId);
        }
    }

    private sealed class FakeAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions =
            new();
        private AgentProcessListAction? _action;
        private AgentAuthorizationId _authorizationId;
        private CancellationTokenSource _permitRevocation = new();
        private int _completionAttempts;
        private int _consumeAttempts;
        private int _consumed;

        public AgentAuthorizationError? CompletionError { get; set; }

        public AgentAuthorizationError? ConsumeError { get; set; }

        public PermitMismatch Mismatch { get; set; }

        public int ConsumeCount => Volatile.Read(ref _consumed);

        public int ConsumeAttempts => Volatile.Read(ref _consumeAttempts);

        public int CompletionAttempts => Volatile.Read(
            ref _completionAttempts);

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentProcessListAction action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _authorizationId = AgentAuthorizationId.New();
            _permitRevocation.Dispose();
            _permitRevocation = new CancellationTokenSource();
            Volatile.Write(ref _consumeAttempts, 0);
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

        public void RevokePermit() => _permitRevocation.Cancel();

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _consumeAttempts);
            var action = _action
                ?? throw new InvalidOperationException(
                    "An action must be armed before authorization is consumed.");
            var expected = AgentActionExecutionBinding.FromProposal(
                action.Proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(expected, currentBinding))
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationMismatch,
                            "The process execution binding changed.")));
            }

            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationNotFound,
                            "The authorization was already consumed.")));
            }

            if (ConsumeError is { } consumeError)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(consumeError));
            }

            Assert.True(BuiltInAgentTools.Catalog.TryGet(
                Mismatch == PermitMismatch.Tool
                    ? BuiltInAgentTools.TerminalReadScreen
                    : action.Proposal.ToolName,
                out var tool));
            var now = timeProvider.GetUtcNow();
            var authorizationProposal = Mismatch switch
            {
                PermitMismatch.ArgumentDigest => CopyProposal(
                    action.Proposal,
                    argumentDigest: AgentActionDigest.FromUtf8(
                        "wrong-process-argument")),
                PermitMismatch.PolicyGeneration => CopyProposal(
                    action.Proposal,
                    policyGeneration: checked(
                        action.Proposal.PolicyGeneration + 1)),
                _ => action.Proposal,
            };
            return ValueTask.FromResult<AgentPermitResult>(
                new AgentPermitResult.Granted(
                    new AgentActionPermit(
                        new AgentActionAuthorization(
                            authorizationId,
                            authorizationProposal,
                            tool!,
                            AgentAuthorizationSource.AutoPolicy,
                            clientId,
                            now.AddMinutes(1)),
                        now,
                        _permitRevocation.Token)));
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            _ = permit;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _completionAttempts);
            _completions.Enqueue(completion);
            return ValueTask.FromResult(CompletionError);
        }

        private static bool BindingsMatch(
            AgentActionExecutionBinding left,
            AgentActionExecutionBinding right) =>
            left.ActionId == right.ActionId
            && left.RunId == right.RunId
            && left.ActorId == right.ActorId
            && string.Equals(
                left.ToolName,
                right.ToolName,
                StringComparison.Ordinal)
            && left.Target == right.Target
            && left.TargetIdentity == right.TargetIdentity
            && left.TargetFingerprint == right.TargetFingerprint
            && left.ArgumentDigest == right.ArgumentDigest
            && left.PolicyGeneration == right.PolicyGeneration;
    }

    public enum PermitMismatch
    {
        None,
        Tool,
        ArgumentDigest,
        PolicyGeneration,
    }

    public enum PreparedActionMismatch
    {
        Tool,
        ArgumentDigest,
    }

    public enum ActiveCancellation
    {
        Caller,
        Permit,
    }

    private sealed class InMemoryAuditStore : IAuditStore
    {
        private readonly object _gate = new();
        private readonly List<AuditEventRecord> _events = [];

        public IReadOnlyList<AuditEventRecord> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<
            AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<
                        IReadOnlyList<AuditEventRecord>>.Success(
                        [.. _events
                            .Where(item =>
                                string.Equals(
                                    item.CorrelationId,
                                    correlationId,
                                    StringComparison.Ordinal))]));
            }
        }
    }
}
