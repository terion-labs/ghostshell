using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class AgentStatisticsSessionHostTests
{
    [Fact]
    public async Task NumericSnapshotIsProjectedAndAuditedAsOneResult()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Statistics.Snapshot = new SystemStatisticsSnapshot(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromHours(3),
            LogicalProcessorCount: 8,
            EnumeratedProcessCount: 42,
            ObservedProcessCount: 40,
            ObservedCpuPercent: 20.5,
            ObservedWorkingSetBytes: 8_192,
            NetworkReceivedBytesPerSecond: 100,
            NetworkSentBytesPerSecond: 50);
        var action = await fixture.PrepareAsync();

        var result = (await fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Value();

        Assert.Equal(8, result.LogicalProcessorCount);
        Assert.Equal(42, result.EnumeratedProcessCount);
        Assert.Equal(40, result.ObservedProcessCount);
        Assert.Equal(20.5, result.ObservedCpuPercent);
        Assert.Equal(8_192, result.ObservedWorkingSetBytes);
        Assert.Equal(1, fixture.Statistics.ReadCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("statistics_read", completion.StableCode);
        Assert.Equal(1, completion.ResultCount);
    }

    [Fact]
    public async Task GraphDriftAfterCaptureDiscardsSnapshot()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Statistics.BlockRead = true;
        var action = await fixture.PrepareAsync();

        var execution = fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.Statistics.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var graph = await fixture.GraphAsync();
        _ = (await fixture.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                fixture.WorkspaceId,
                fixture.TabId,
                fixture.ProcessPanelId),
            fixture.HumanContext(graph.Revision),
            default)).Value();
        fixture.Statistics.ReleaseRead.TrySetResult();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(1, fixture.Statistics.ReadCount);
        Assert.Equal(
            AgentActionOutcome.Failed,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task CapabilityDriftAfterCaptureDiscardsSnapshot()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Statistics.BlockRead = true;
        var action = await fixture.PrepareAsync();

        var execution = fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.Statistics.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        fixture.Statistics.RemoveCapability(SessionCapabilities.StatisticsRead);
        fixture.Statistics.ReleaseRead.TrySetResult();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.CapabilityNotSupported, failure.Code);
        Assert.Equal("statistics_unavailable", failure.StableCode);
        Assert.Null(
            Assert.Single(fixture.Authorization.Completions).ResultCount);
    }

    [Fact]
    public async Task ClosingSessionCancelsActiveCapture()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Statistics.BlockRead = true;
        var action = await fixture.PrepareAsync();

        var execution = fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            default).AsTask();
        await fixture.Statistics.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        _ = (await fixture.Client.CloseAsync(
            CloseScopeRequest.Session(
                fixture.StatisticsSessionId,
                CloseDecision.Request),
            fixture.HumanContext(),
            default)).Value();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.Cancelled, failure.Code);
        Assert.Equal("session_revoked", failure.StableCode);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Cancelled, completion.Outcome);
        Assert.Equal("session_revoked", completion.StableCode);
    }

    [Fact]
    public async Task CallerCancellationCancelsActiveCaptureAndIsAudited()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Statistics.BlockRead = true;
        var action = await fixture.PrepareAsync();
        using var cancellation = new CancellationTokenSource();

        var execution = fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            cancellation.Token).AsTask();
        await fixture.Statistics.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var failure = (await execution.WaitAsync(
            TimeSpan.FromSeconds(5))).Error();

        Assert.Equal(HostErrorCode.Cancelled, failure.Code);
        Assert.Equal("caller_cancelled", failure.StableCode);
        Assert.Equal(
            AgentActionOutcome.Cancelled,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task InvalidNumericSnapshotFailsClosedAfterOneCapture()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Statistics.Snapshot = new SystemStatisticsSnapshot(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromHours(1),
            4,
            2,
            1,
            double.NaN,
            1024);
        var action = await fixture.PrepareAsync();

        var failure = (await fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.EngineFailed, failure.Code);
        Assert.Equal("statistics_result_invalid", failure.StableCode);
        Assert.Equal(1, fixture.Statistics.ReadCount);
        Assert.Equal(
            AgentActionOutcome.Failed,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task CompletionAuditUncertaintyDoesNotRecapture()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        fixture.Authorization.CompletionError = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "Audit unavailable.");
        var action = await fixture.PrepareAsync();

        var failure = (await fixture.Client.RunAgentStatisticsReadAsync(
            fixture.Authorization.Arm(action),
            action,
            default)).Error();

        Assert.Equal(HostErrorCode.EngineFailed, failure.Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            failure.StableCode);
        Assert.Equal(1, fixture.Statistics.ReadCount);
        Assert.Equal(2, fixture.Authorization.CompletionAttempts);
    }

    [Fact]
    public async Task OneActionAuthorizationCannotBeReplayed()
    {
        await using var fixture = await StatisticsHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync();
        var authorizationId = fixture.Authorization.Arm(action);

        _ = (await fixture.Client.RunAgentStatisticsReadAsync(
            authorizationId,
            action,
            default)).Value();
        var replay = await fixture.Client.RunAgentStatisticsReadAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        Assert.Equal(1, fixture.Statistics.ReadCount);
        Assert.Equal(2, fixture.Authorization.ConsumeAttempts);
    }

    private sealed class StatisticsHostFixture : IAsyncDisposable
    {
        private StatisticsHostFixture()
        {
            Clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            Factory = new FakeSystemMonitorPanelSessionFactory();
            Composer = new AgentStatisticsReadActionComposer();
            Authorization = new StatisticsAuthorizationConsumer(
                Clock,
                ClientId);
            Client = new InMemorySessionHostClient(
                new FakeTerminalSessionFactory(),
                new DesktopLifecyclePolicy(),
                Clock,
                systemMonitorFactory: Factory,
                agentAuthorizationConsumer: Authorization,
                agentStatisticsReadActionComposer: Composer);
        }

        public ManualTimeProvider Clock { get; }

        public FakeSystemMonitorPanelSessionFactory Factory { get; }

        public AgentStatisticsReadActionComposer Composer { get; }

        public StatisticsAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("statistics-test-client");

        public WindowInstanceId WindowId { get; } = new("statistics-window");

        public WorkspaceInstanceId WorkspaceId { get; } =
            new("statistics-workspace");

        public TabInstanceId TabId { get; } = new("statistics-tab");

        public PanelInstanceId StatisticsPanelId { get; } =
            new("statistics-panel");

        public PanelInstanceId ProcessPanelId { get; } =
            new("process-panel");

        public SessionId StatisticsSessionId { get; } =
            new("statistics-session");

        public AgentRunId RunId { get; } = new("statistics-run");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("statistics-agent"),
            ActorKind.Agent,
            "Statistics agent");

        public FakeStatisticsPanelSession Statistics =>
            Factory.Statistics(StatisticsSessionId);

        public static async ValueTask<StatisticsHostFixture> CreateAsync()
        {
            var fixture = new StatisticsHostFixture();
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    fixture.Workspace()),
                fixture.HumanContext(),
                default)).Value();
            _ = (await fixture.Client.EnsureStatisticsSessionAsync(
                new EnsureStatisticsSessionRequest(
                    fixture.StatisticsSessionId,
                    fixture.Owner(),
                    "Statistics"),
                fixture.HumanContext(),
                default)).Value();
            return fixture;
        }

        public async ValueTask<AgentStatisticsReadAction> PrepareAsync()
        {
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Workspace(WindowId, WorkspaceId)),
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
                new AgentStatisticsReadRequest(StatisticsPanelId));
        }

        public async ValueTask<WorkspaceGraphSnapshot> GraphAsync() =>
            (await Client.GetWorkspaceGraphAsync(
                WorkspaceId,
                HumanContext(),
                default)).Value();

        public OperationContext HumanContext(long? expectedRevision = null) =>
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
                StatisticsPanelId);

        private WorkspaceInstance Workspace()
        {
            var panels = new[]
            {
                new PanelInstance(
                    StatisticsPanelId,
                    PanelKind.Statistics,
                    "Statistics"),
                new PanelInstance(
                    ProcessPanelId,
                    PanelKind.ProcessMonitor,
                    "Process monitor"),
            };
            return new WorkspaceInstance(
                WorkspaceId,
                "Workspace",
                [new TabInstance(
                    TabId,
                    "Monitoring",
                    panels,
                    StatisticsPanelId)],
                TabId);
        }
    }

    private sealed class StatisticsAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions =
            new();
        private AgentStatisticsReadAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _completionAttempts;
        private int _consumeAttempts;
        private int _consumed;

        public AgentAuthorizationError? CompletionError { get; set; }

        public int CompletionAttempts => Volatile.Read(ref _completionAttempts);

        public int ConsumeAttempts => Volatile.Read(ref _consumeAttempts);

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentStatisticsReadAction action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _authorizationId = AgentAuthorizationId.New();
            Volatile.Write(ref _consumeAttempts, 0);
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

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
                || !BindingsMatch(expected, currentBinding)
                || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationMismatch,
                            "The Statistics execution binding changed.")));
            }

            Assert.True(BuiltInAgentTools.Catalog.TryGet(
                action.Proposal.ToolName,
                out var tool));
            var now = timeProvider.GetUtcNow();
            return ValueTask.FromResult<AgentPermitResult>(
                new AgentPermitResult.Granted(
                    new AgentActionPermit(
                        new AgentActionAuthorization(
                            authorizationId,
                            action.Proposal,
                            tool!,
                            AgentAuthorizationSource.AutoPolicy,
                            clientId,
                            now.AddMinutes(1)),
                        now,
                        CancellationToken.None)));
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
            && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
            && left.Target == right.Target
            && left.TargetIdentity == right.TargetIdentity
            && left.TargetFingerprint == right.TargetFingerprint
            && left.ArgumentDigest == right.ArgumentDigest
            && left.PolicyGeneration == right.PolicyGeneration;
    }
}
