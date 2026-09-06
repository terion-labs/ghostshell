using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Protocol;
using GhostShell.SessionHost;

namespace GhostShell.SessionHost.Tests;

public sealed class DatabaseSessionHostTests
{
    private static readonly WindowInstanceId WindowId = new("database-window");
    private static readonly WorkspaceInstanceId WorkspaceId = new("database-workspace");
    private static readonly TabInstanceId TabId = new("database-tab");
    private static readonly PanelInstanceId PanelId = new("database-panel");
    private static readonly SessionId SessionId = new("database-session");

    [Fact]
    public async Task CancellationDuringDatabaseSessionCreationRetainsUncertainReplay()
    {
        var factory = new FakeDatabasePanelSessionFactory();
        var creationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.AfterCreateAsync = async (_, cancellationToken) =>
        {
            creationEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        };
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(
            new IdempotencyKey("database-create-cancelled"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureDatabaseSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await creationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var uncertain = await pending;
        var replay = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(WorkspaceId, factory.LastWorkspaceId);
    }

    [Fact]
    public async Task DatabaseSessionSnapshotFailureDisposesEngineAndRetainsUncertainReplay()
    {
        var factory = new FakeDatabasePanelSessionFactory
        {
            BeforeSnapshotForNewSessions = static _ =>
                ValueTask.FromException(new IOException("fake snapshot failure")),
        };
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(new IdempotencyKey("database-create-failed"));

        var uncertain = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, factory.Session!.DisposeCount);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ConcurrentDatabaseCreationCompletesKnownSuccessAfterCallerCancellation()
    {
        var factory = new FakeDatabasePanelSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotToken = CancellationToken.None;
        factory.BeforeSnapshotForNewSessions = async cancellationToken =>
        {
            snapshotToken = cancellationToken;
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(new IdempotencyKey("database-create-known"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureDatabaseSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var concurrentReplay = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        var completed = await pending;
        var completedReplay = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            concurrentReplay.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completed);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completedReplay);
        Assert.False(snapshotToken.CanBeCanceled);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task CancellationBeforeDatabaseSessionCreationLeavesKeyFresh()
    {
        var factory = new FakeDatabasePanelSessionFactory();
        await using var host = CreateHost(factory);
        var request = Request();
        var context = Context(
            new IdempotencyKey("database-create-pre-cancelled"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            cancellation.Token);
        var retry = await host.EnsureDatabaseSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(retry);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task DatabaseOpenReservationRejectsCrossFamilyTerminalOpen()
    {
        var factory = new FakeDatabasePanelSessionFactory();
        var terminals = new FakeTerminalSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.BeforeSnapshotForNewSessions = async _ =>
        {
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(factory, terminals);
        var context = Context(new IdempotencyKey("database-cross-family"));

        var database = host.EnsureDatabaseSessionAsync(
            Request(),
            context,
            CancellationToken.None).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await host.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("database-cross-family-terminal"),
                new SessionOwner(
                    HostMode.Desktop,
                    WindowId,
                    WorkspaceId,
                    TabId,
                    new PanelInstanceId("database-cross-family-terminal-panel")),
                "Terminal",
                new TerminalLaunchRequest("/tmp")),
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        Assert.Equal(HostErrorCode.IdempotencyKeyReused, rejected.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(await database);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, terminals.CreateCount);
    }

    [Fact]
    public async Task NegotiationReflectsTheConfiguredDatabaseFactory()
    {
        var factory = new FakeDatabasePanelSessionFactory();
        await using var configured = CreateHost(factory);
        await using var missing = CreateHost(null);
        var hello = (await configured.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], AllDatabaseCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        var missingHello = (await missing.NegotiateAsync(
            new ClientHello([ProtocolVersions.Current], AllDatabaseCapabilities()),
            Context(),
            CancellationToken.None)).Value();

        Assert.True(hello.Capabilities.Contains(SessionCapabilities.DatabaseReadState));
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.RedisRead));
        Assert.False(missingHello.Capabilities.Contains(SessionCapabilities.DatabaseReadState));
        Assert.False(missingHello.Capabilities.Contains(SessionCapabilities.RedisRead));

        var unsupported = await missing.EnsureDatabaseSessionAsync(
            Request(),
            Context(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.CapabilityNotSupported, unsupported.Error().Code);
    }

    [Fact]
    public async Task EnsureCreatesAndLinksOneExactDatabaseBinding()
    {
        var factory = new FakeDatabasePanelSessionFactory();
        await using var host = CreateHost(factory);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        var context = Context(idempotencyKey: new IdempotencyKey("open-database-once"));

        var first = (await host.EnsureDatabaseSessionAsync(
            Request(),
            context,
            CancellationToken.None)).Value();
        var replay = (await host.EnsureDatabaseSessionAsync(
            Request(),
            context,
            CancellationToken.None)).Value();
        var graph = (await host.GetWorkspaceGraphAsync(
            WorkspaceId,
            Context(),
            CancellationToken.None)).Value();

        Assert.Equal(first, replay);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(PanelKind.DatabaseViewer, first.Descriptor.Kind);
        Assert.Equal(SessionId, Assert.Single(Assert.Single(graph.Workspace.Tabs).Panels).SessionId);

        var changedBinding = await host.EnsureDatabaseSessionAsync(
            Request(bindingRevision: 8),
            Context(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.InvalidRequest, changedBinding.Error().Code);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ProviderFailureReturnsASecretFreeFixedError()
    {
        var factory = new FakeDatabasePanelSessionFactory { FailOpen = true };
        await using var host = CreateHost(factory);
        var result = await host.EnsureDatabaseSessionAsync(
            Request(connectionString: "Data Source=/tmp/private.db;Password=needle"),
            Context(),
            CancellationToken.None);
        var failure = Assert.IsType<HostResult<SessionSnapshot>.Failure>(result);

        Assert.Equal("database_open_failed", failure.Error.StableCode);
        Assert.DoesNotContain("needle", failure.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private.db", failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GovernedReadConsumesOneAuthorizationAndAuditsOneResult()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new FakeDatabasePanelSessionFactory();
        var composer = new AgentDatabaseReadActionComposer();
        var authorization = new DatabaseAuthorizationConsumer(
            clock,
            new ClientId("database-test-client"));
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            clock,
            databasePanelFactory: factory,
            agentAuthorizationConsumer: authorization,
            agentDatabaseReadActionComposer: composer);
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(WindowId, Workspace()),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.EnsureDatabaseSessionAsync(
            Request(),
            Context(),
            CancellationToken.None)).Value();
        var actor = new ActorDescriptor(
            new ActorId("database-agent"),
            ActorKind.Agent,
            "Database agent");
        var context = (await host.InspectAgentContextAsync(
            new AgentContextRequest(
                new AgentTarget.Workspace(WindowId, WorkspaceId)),
            new OperationContext(
                RequestId.New(),
                actor,
                CancellationId: CancellationId.New()),
            CancellationToken.None)).Value();
        var now = clock.GetUtcNow();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("database-agent-run"),
                actor,
                policyGeneration: 0,
                now,
                now.AddMinutes(1)),
            context,
            new AgentDatabaseReadRequest.ListObjects(PanelId, 10));
        var authorizationId = authorization.Arm(action);

        var first = (await host.RunAgentDatabaseReadAsync(
            authorizationId,
            action,
            CancellationToken.None)).Value();
        var replay = await host.RunAgentDatabaseReadAsync(
            authorizationId,
            action,
            CancellationToken.None);

        var objects = Assert.IsType<AgentDatabaseReadResult.Objects>(first);
        Assert.Equal("widgets", Assert.Single(objects.Value.Objects).Name);
        Assert.Equal(1, factory.Session!.ListCount);
        Assert.Equal(HostErrorCode.InvalidRequest, replay.Error().Code);
        var completion = Assert.Single(authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("database_read_completed", completion.StableCode);
        Assert.Equal(1, completion.ResultCount);
    }

    private static InMemorySessionHostClient CreateHost(
        IDatabasePanelSessionFactory? databaseFactory,
        ITerminalSessionFactory? terminalFactory = null) =>
        new(
            terminalFactory ?? new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            databasePanelFactory: databaseFactory);

    private static EnsureDatabaseSessionRequest Request(
        long bindingRevision = 7,
        string connectionString = "Data Source=/tmp/database.db") =>
        new(
            SessionId,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                WorkspaceId,
                TabId,
                PanelId),
            "Database",
            new DatabaseSessionTarget(
                "sqlite",
                connectionString,
                "saved-database",
                bindingRevision));

    private static WorkspaceInstance Workspace()
    {
        var panel = new PanelInstance(PanelId, PanelKind.DatabaseViewer, "Database");
        var tab = new TabInstance(TabId, "Database", [panel], PanelId);
        return new WorkspaceInstance(WorkspaceId, "Database", [tab], TabId);
    }

    private static CapabilitySet AllDatabaseCapabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.DatabaseReadState,
        SessionCapabilities.DatabaseListObjects,
        SessionCapabilities.DatabaseDescribeObject,
        SessionCapabilities.DatabaseReadTable,
        SessionCapabilities.DatabaseSchemaGraph,
        SessionCapabilities.RedisScan,
        SessionCapabilities.RedisRead,
        SessionCapabilities.RedisSearch,
    ]);

    private static OperationContext Context(IdempotencyKey? idempotencyKey = null) =>
        new(
            RequestId.New(),
            new ActorDescriptor(
                new ActorId("database-user"),
                ActorKind.Human,
                "Database user",
                new ClientId("database-user")),
            IdempotencyKey: idempotencyKey,
            CancellationId: CancellationId.New());

    private sealed class FakeDatabasePanelSessionFactory : IDatabasePanelSessionFactory
    {
        public CapabilitySet RelationalCapabilities { get; } = new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.DatabaseReadState,
            SessionCapabilities.DatabaseListObjects,
            SessionCapabilities.DatabaseDescribeObject,
            SessionCapabilities.DatabaseReadTable,
            SessionCapabilities.DatabaseSchemaGraph,
        ]);

        public CapabilitySet RedisCapabilities { get; } = new(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.DatabaseReadState,
            SessionCapabilities.RedisScan,
            SessionCapabilities.RedisRead,
            SessionCapabilities.RedisSearch,
        ]);

        public int CreateCount { get; private set; }

        public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

        public bool FailOpen { get; init; }

        public Func<FakeDatabasePanelSession, CancellationToken, ValueTask>? AfterCreateAsync
        {
            get;
            set;
        }

        public Func<CancellationToken, ValueTask>? BeforeSnapshotForNewSessions
        {
            get;
            set;
        }

        public FakeDatabasePanelSession? Session { get; private set; }

        public async ValueTask<IDatabasePanelSession> CreateAsync(
            WorkspaceInstanceId workspaceId,
            SessionId sessionId,
            DatabaseSessionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            LastWorkspaceId = workspaceId;
            if (FailOpen)
            {
                throw new InvalidOperationException(
                    "Provider included Password=needle in its exception.");
            }

            var session = new FakeDatabasePanelSession(
                sessionId,
                target.Binding,
                RelationalCapabilities)
            {
                BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
            };
            Session = session;
            if (AfterCreateAsync is { } afterCreate)
            {
                await afterCreate(session, cancellationToken).ConfigureAwait(false);
            }

            return session;
        }
    }

    private sealed class FakeDatabasePanelSession(
        SessionId id,
        DatabaseSessionBinding binding,
        CapabilitySet capabilities) : IRelationalDatabasePanelSession
    {
        private bool _closed;

        public SessionId Id { get; } = id;

        public PanelKind Kind => PanelKind.DatabaseViewer;

        public CapabilitySet Capabilities { get; } = capabilities;

        public DatabaseSessionBinding Binding { get; } = binding;

        public DatabasePanelSessionState State { get; } = new(
            DatabasePanelBackend.Relational,
            "sqlite",
            "SQLite",
            IsReady: true);

        public int ListCount { get; private set; }

        public Func<CancellationToken, ValueTask>? BeforeSnapshotAsync { get; set; }

        public int DisposeCount { get; private set; }

        public ValueTask<DatabaseObjectPage> ListObjectsAsync(
            int maximumObjects,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCount++;
            return ValueTask.FromResult(new DatabaseObjectPage(
                [new DatabaseObjectSummary(
                    new DatabaseObjectReference("object_ref_1"),
                    "widgets",
                    DatabaseTableKind.Table)],
                IsTruncated: false));
        }

        public ValueTask<DatabaseObjectSnapshot> DescribeObjectAsync(
            DatabaseObjectReference reference,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<DatabaseObjectSnapshot>(
                new NotSupportedException());

        public ValueTask<DatabaseTableSnapshot> ReadTableAsync(
            DatabaseTableReadRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<DatabaseTableSnapshot>(
                new NotSupportedException());

        public ValueTask<DatabaseSchemaGraphSnapshot> ReadSchemaGraphAsync(
            int maximumObjects,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<DatabaseSchemaGraphSnapshot>(
                new NotSupportedException());

        public async ValueTask<PanelSessionSnapshot> SnapshotAsync(
            CancellationToken cancellationToken)
        {
            if (BeforeSnapshotAsync is { } beforeSnapshot)
            {
                await beforeSnapshot(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _closed
                ? new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "Closed")
                : new PanelSessionSnapshot(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    false,
                    "Ready");
        }

        public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask<PanelCloseOutcome> CloseAsync(
            PanelCloseMode mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_closed)
            {
                return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
            }

            _closed = true;
            return ValueTask.FromResult(mode == PanelCloseMode.Force
                ? PanelCloseOutcome.ForceTerminated
                : PanelCloseOutcome.GracefullyClosed);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _closed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DatabaseAuthorizationConsumer(
        TimeProvider timeProvider,
        ClientId clientId) : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentDatabaseReadAction? _action;
        private AgentAuthorizationId _authorizationId;
        private int _consumed;

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public AgentAuthorizationId Arm(AgentDatabaseReadAction action)
        {
            _action = action;
            _authorizationId = AgentAuthorizationId.New();
            Volatile.Write(ref _consumed, 0);
            return _authorizationId;
        }

        public ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = _action
                ?? throw new InvalidOperationException("An action must be armed.");
            var expected = AgentActionExecutionBinding.FromProposal(action.Proposal);
            if (authorizationId != _authorizationId
                || !BindingsMatch(expected, currentBinding)
                || Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return ValueTask.FromResult<AgentPermitResult>(
                    new AgentPermitResult.Denied(
                        new AgentAuthorizationError(
                            AgentAuthorizationErrorCode.AuthorizationMismatch,
                            "The database execution binding changed.")));
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
            _completions.Enqueue(completion);
            return ValueTask.FromResult<AgentAuthorizationError?>(null);
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
