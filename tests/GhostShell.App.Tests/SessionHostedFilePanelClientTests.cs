using System.Reflection;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class SessionHostedFilePanelClientTests
{
    [Fact]
    public async Task Provider_choices_are_snapshotted_after_the_hosted_session_binds()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateClient();
        var initial = Assert.Single(client.Profiles);
        fixture.ProfileSource.ReplaceProfiles([]);
        Assert.Empty(client.Profiles);
        fixture.ProfileSource.ReplaceProfiles([initial]);

        Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await client.InitializeAsync(CancellationToken.None));
        var captured = Assert.Single(client.Profiles);

        fixture.ProfileSource.ReplaceProfiles([]);

        Assert.Same(captured, Assert.Single(client.Profiles));
    }

    [Fact]
    public async Task First_operation_ensures_the_owned_session_and_preserves_provider_failures()
    {
        var fixture = new Fixture();
        var providerError = new FilePanelError(
            FilePanelErrorCode.AccessDenied,
            "provider_access_denied",
            "The provider denied access.",
            Retryable: false);
        fixture.Host.ListResult = FilePanelResult<FilePanelPage>.Failure(providerError);
        using var client = fixture.CreateClient();

        var result = await client.ListAsync(
            new FilePanelListRequest(fixture.Root, 25, null, ShowHidden: false),
            CancellationToken.None);
        _ = await client.StatAsync(fixture.Root, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Same(fixture.Host.ListResult, result);
        Assert.Same(providerError, result.Error);
        Assert.Single(fixture.Host.EnsureRequests);
        var ensure = Assert.Single(fixture.Host.EnsureRequests);
        Assert.Equal(fixture.SessionId, ensure.Request.SessionId);
        Assert.Equal(fixture.Owner, ensure.Request.Owner);
        Assert.Equal(fixture.ClientId, client.ClientId);
        Assert.Equal(fixture.Root, ensure.Request.InitialLocation);
        Assert.NotNull(ensure.Context.IdempotencyKey);
        Assert.Null(ensure.Context.ExpectedRevision);
        Assert.Equal(fixture.Now.AddSeconds(9), ensure.Context.DeadlineUtc);

        var list = Assert.Single(fixture.Host.ListRequests);
        Assert.Equal(fixture.SessionId, list.Request.SessionId);
        Assert.Equal(4, list.Context.ExpectedRevision);
        Assert.Null(list.Context.IdempotencyKey);
        Assert.Equal(fixture.Now.AddSeconds(9), list.Context.DeadlineUtc);
        Assert.Equal(fixture.ProfileSource.Profiles, client.Profiles);
        Assert.True(client.IsInitialized);
        Assert.Equal(4, client.Revision);
    }

    [Fact]
    public async Task Mutations_use_fresh_keys_advance_expected_revision_and_never_bypass_host()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateClient();
        var child = fixture.Root.Child(new FilePanelPathSegment("child"));
        var destination = fixture.Root.Child(new FilePanelPathSegment("renamed"));

        _ = await client.CreateDirectoryAsync(
            new FilePanelCreateDirectoryRequest(
                child,
                FilePanelMutationPrecondition.MustNotExist),
            CancellationToken.None);
        _ = await client.RenameAsync(
            new FilePanelRenameRequest(
                child,
                destination,
                FilePanelMutationPrecondition.MustNotExist),
            CancellationToken.None);
        _ = await client.DeleteAsync(
            new FilePanelDeleteRequest(
                destination,
                Recursive: false,
                FilePanelMutationPrecondition.MustExist),
            CancellationToken.None);

        var transferRequest = new FilePanelTransferRequest(
            child,
            destination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.Fail);
        var enqueued = await client.EnqueueAsync(transferRequest, CancellationToken.None);
        _ = await client.CancelAsync(enqueued.Value!.Id, CancellationToken.None);
        var retried = await client.RetryAsync(enqueued.Value.Id, CancellationToken.None);

        var mutationCalls = fixture.Host.Calls
            .Where(call => call.Name is not nameof(ISessionHostClient.EnsureFilePanelSessionAsync))
            .ToArray();
        Assert.Equal([4L, 5L, 6L, 7L, 8L, 9L], [.. mutationCalls.Select(call => call.Context.ExpectedRevision!.Value)]);
        var keys = mutationCalls.Select(call => call.Context.IdempotencyKey).ToArray();
        Assert.All(keys, key => Assert.NotNull(key));
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(10, client.Revision);
        Assert.Equal(fixture.SessionId, Assert.Single(fixture.Host.EnqueueRequests).SessionId);
        Assert.Equal(enqueued.Value.Id, Assert.Single(fixture.Host.CancelRequests).TransferId);
        Assert.Equal(enqueued.Value.Id, Assert.Single(fixture.Host.RetryRequests).TransferId);
        Assert.True(retried.IsSuccess);

        var sibling = fixture.Transfer(FilePanelTransferId.New(), "sibling", FilePanelTransferState.Running);
        fixture.TransferProjection.SetTransfers(enqueued.Value, sibling);
        var projected = client.Transfers;
        Assert.Contains(projected, transfer => transfer.Id == enqueued.Value.Id);
        Assert.Contains(projected, transfer => transfer.Id == retried.Value!.Id);
        Assert.DoesNotContain(projected, transfer => transfer.Id == sibling.Id);
        Assert.Equal(0, fixture.ProfileSource.OperationCalls);
        Assert.Equal(0, fixture.TransferProjection.MutationCalls);
    }

    [Fact]
    public async Task Host_failures_have_a_distinct_compatibility_mapping_and_resynchronize_revision()
    {
        var fixture = new Fixture();
        fixture.Host.NextListFailure = HostResult<FilePanelResult<FilePanelPage>>.Fail(
            HostError.Create(
                HostErrorCode.RevisionConflict,
                "The session changed before this request arrived."),
            currentRevision: 12);
        using var client = fixture.CreateClient();

        var result = await client.ListAsync(
            new FilePanelListRequest(fixture.Root, 25, null, ShowHidden: false),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.Conflict, result.Error!.Code);
        Assert.Equal("host_revision_conflict", result.Error.StableCode);
        Assert.Equal("The session changed before this request arrived.", result.Error.Message);
        Assert.Equal(12, client.Revision);
    }

    [Fact]
    public async Task Close_is_session_scoped_and_carries_the_latest_revision()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateClient();
        IHostedFilePanelClient lifecycle = client;
        _ = await lifecycle.InitializeAsync(CancellationToken.None);
        fixture.Host.CloseResultFactory = (request, revision) => request.Decision switch
        {
            CloseDecision.Request => new CloseScopeResult.ConfirmationRequired(
                request.Scope,
                request.TargetId,
                [
                    new ActiveSessionSummary(
                        fixture.SessionId,
                        fixture.Owner.PanelId,
                        "Files",
                        "One transfer is active.",
                        revision),
                ]),
            CloseDecision.Confirm => new CloseScopeResult.Completed(
                request.Scope,
                request.TargetId,
                [
                    new SessionCloseResult(
                        fixture.SessionId,
                        SessionCloseOutcome.ForceTerminated,
                        "Closed."),
                ]),
            _ => throw new InvalidOperationException(),
        };

        var preflight = await lifecycle.CloseAsync(
            CloseDecision.Request,
            CancellationToken.None);
        var confirmed = await lifecycle.CloseAsync(
            CloseDecision.Confirm,
            CancellationToken.None);

        Assert.IsType<CloseScopeResult.ConfirmationRequired>(
            Assert.IsType<HostResult<CloseScopeResult>.Success>(preflight).Value);
        Assert.IsType<CloseScopeResult.Completed>(
            Assert.IsType<HostResult<CloseScopeResult>.Success>(confirmed).Value);
        Assert.Collection(
            fixture.Host.CloseRequests,
            request => AssertCloseRequest(request, fixture.SessionId, 4, CloseDecision.Request),
            request => AssertCloseRequest(request, fixture.SessionId, 5, CloseDecision.Confirm));
        Assert.All(fixture.Host.CloseCalls, call =>
        {
            Assert.Null(call.Context.ExpectedRevision);
            Assert.NotNull(call.Context.IdempotencyKey);
        });
        Assert.NotEqual(
            fixture.Host.CloseCalls[0].Context.IdempotencyKey,
            fixture.Host.CloseCalls[1].Context.IdempotencyKey);
    }

    [Fact]
    public async Task Cancelled_initialization_does_not_create_or_poison_the_session()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await client.InitializeAsync(cancellation.Token);
        var retried = await client.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            HostErrorCode.Cancelled,
            Assert.IsType<HostResult<SessionSnapshot>.Failure>(cancelled).Error.Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(retried);
        Assert.Single(fixture.Host.EnsureRequests);
    }

    [Fact]
    public async Task Deferred_session_binds_to_the_exact_first_location_for_its_required_profile()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateDeferredClient(
            new FileProviderProfileId(fixture.Root.ProviderProfileId));
        var expected = fixture.Root.Child(new FilePanelPathSegment("saved"));

        var premature = await client.InitializeAsync(CancellationToken.None);
        var listed = await client.ListAsync(
            new FilePanelListRequest(expected, 25, null, ShowHidden: false),
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.InvalidRequest,
            Assert.IsType<HostResult<SessionSnapshot>.Failure>(premature).Error.Code);
        Assert.True(listed.IsSuccess);
        Assert.Equal(expected, Assert.Single(fixture.Host.EnsureRequests).Request.InitialLocation);
        Assert.Equal(expected, Assert.Single(fixture.Host.ListRequests).Request.Request.Location);
        Assert.True(client.IsInitialized);
    }

    [Fact]
    public async Task Deferred_session_never_substitutes_a_different_provider()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateDeferredClient(
            new FileProviderProfileId(fixture.Root.ProviderProfileId));
        var other = new FilePanelLocation(
            "files.other",
            "other",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));

        var result = await client.ListAsync(
            new FilePanelListRequest(other, 25, null, ShowHidden: false),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FilePanelErrorCode.InvalidLocation, result.Error!.Code);
        Assert.Empty(fixture.Host.EnsureRequests);
        Assert.False(client.IsInitialized);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Deferred_view_binds_the_resolved_saved_folder_and_keeps_human_navigation(
        bool textLocation,
        bool explicitFilesystemRoot)
    {
        var fixture = new Fixture();
        using var client = fixture.CreateDeferredClient(
            new FileProviderProfileId(fixture.Root.ProviderProfileId));
        var expected = explicitFilesystemRoot
            ? fixture.Root
            : fixture.Root.Child(new FilePanelPathSegment("saved"))
                .Child(new FilePanelPathSegment("project"));
        var savedText = explicitFilesystemRoot ? "/" : "/saved/project";
        using var panel = new FileRuntimePanelViewModel(
            fixture.Owner.PanelId,
            "Files",
            client,
            client,
            new FileProviderProfileId(fixture.Root.ProviderProfileId),
            initialLocation: textLocation ? null : expected,
            initialLocationText: textLocation ? savedText : null,
            deferInitialization: true);

        Assert.IsType<HostResult<SessionSnapshot>.Failure>(
            await client.InitializeAsync(CancellationToken.None));
        Assert.Empty(fixture.Host.EnsureRequests);
        await panel.StartInitialization();

        var binding = Assert.Single(fixture.Host.EnsureRequests).Request.InitialLocation;
        Assert.Equal(expected, binding);
        Assert.Equal(expected, panel.CurrentLocation);
        var metadata = new FileSessionMetadata(binding, FilePanelCapability.List, 250, 1024);
        Assert.Equal(expected.Child(new FilePanelPathSegment("note.txt")),
            AgentFileActionComposer.ResolveLocation(metadata, [new FilePanelPathSegment("note.txt")]));
        Assert.Throws<ArgumentException>(() => new FilePanelPathSegment(".."));

        await panel.NavigateUpAsync();
        Assert.Equal(expected.Parent, panel.CurrentLocation);
        Assert.Equal(expected.Parent, fixture.Host.ListRequests.Last().Request.Request.Location);
        Assert.Single(fixture.Host.EnsureRequests);
        Assert.Equal(fixture.Root, Assert.Single(panel.Profiles).Root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unresolved_saved_view_does_not_create_host_authority(bool missingProvider)
    {
        var fixture = new Fixture();
        var profileId = new FileProviderProfileId(
            missingProvider ? "unavailable.saved.provider" : fixture.Root.ProviderProfileId);
        using var client = fixture.CreateDeferredClient(profileId);
        using var panel = new FileRuntimePanelViewModel(
            fixture.Owner.PanelId,
            "Files",
            client,
            client,
            profileId,
            initialLocationText: missingProvider ? "/saved/project" : "/saved/../outside",
            deferInitialization: true);

        await panel.StartInitialization();

        Assert.False(client.IsInitialized);
        Assert.Empty(fixture.Host.EnsureRequests);
        Assert.Empty(fixture.Host.ListRequests);
        Assert.Null(panel.CurrentLocation);
        Assert.NotNull(panel.ContentIssue);
        Assert.False(panel.CanEditLocation);
        Assert.False(panel.CanSelectProfile);
    }

    [Fact]
    public async Task Closing_an_unbound_deferred_session_does_not_create_host_authority()
    {
        var fixture = new Fixture();
        using var client = fixture.CreateDeferredClient(
            new FileProviderProfileId(fixture.Root.ProviderProfileId));

        var result = await client.CloseAsync(
            CloseDecision.Request,
            CancellationToken.None);

        var completed = Assert.IsType<CloseScopeResult.Completed>(
            Assert.IsType<HostResult<CloseScopeResult>.Success>(result).Value);
        var closed = Assert.Single(completed.Sessions);
        Assert.Equal(fixture.SessionId, closed.SessionId);
        Assert.Equal(SessionCloseOutcome.AlreadyClosed, closed.Outcome);
        Assert.Empty(fixture.Host.EnsureRequests);
        Assert.Empty(fixture.Host.CloseRequests);
        Assert.False(client.IsInitialized);
    }

    private static void AssertCloseRequest(
        CloseScopeRequest request,
        SessionId sessionId,
        long expectedRevision,
        CloseDecision decision)
    {
        Assert.Equal(CloseScopeKind.Session, request.Scope);
        Assert.Equal(sessionId.Value, request.TargetId);
        Assert.Equal(decision, request.Decision);
        Assert.Equal(expectedRevision, request.ExpectedSessionRevisions![sessionId]);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            SessionId = new SessionId("file-session");
            ClientId = new ClientId("desktop-client");
            Owner = new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("panel"));
            Root = new FilePanelLocation(
                "builtin.files.home",
                "local",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            ProfileSource = new ThrowingFilePanelClient(Root);
            TransferProjection = new ProjectedTransferQueue();
            SessionHost = DispatchProxy.Create<ISessionHostClient, RecordingSessionHost>();
            Host = (RecordingSessionHost)(object)SessionHost;
            Host.Owner = Owner;
            Host.Root = Root;
            Host.SessionId = SessionId;
        }

        public DateTimeOffset Now { get; } = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

        public SessionId SessionId { get; }

        public ClientId ClientId { get; }

        public SessionOwner Owner { get; }

        public FilePanelLocation Root { get; }

        public ThrowingFilePanelClient ProfileSource { get; }

        public ProjectedTransferQueue TransferProjection { get; }

        public RecordingSessionHost Host { get; }

        public ISessionHostClient SessionHost { get; }

        public SessionHostedFilePanelClient CreateClient() => new(
            SessionHost,
            ProfileSource,
            new HostedFilePanelClientOptions(
                SessionId,
                Owner,
                ClientId,
                "Files",
                Root,
                TimeSpan.FromSeconds(9)),
            TransferProjection,
            new FixedTimeProvider(Now));

        public SessionHostedFilePanelClient CreateDeferredClient(
            FileProviderProfileId? requiredProfileId) => new(
            SessionHost,
            ProfileSource,
            HostedFilePanelClientOptions.Deferred(
                SessionId,
                Owner,
                ClientId,
                "Files",
                requiredProfileId,
                TimeSpan.FromSeconds(9)),
            TransferProjection,
            new FixedTimeProvider(Now));

        public FilePanelTransferSnapshot Transfer(
            FilePanelTransferId id,
            string stage,
            FilePanelTransferState state) => new(
            id,
            new FilePanelTransferRequest(
                Root.Child(new FilePanelPathSegment("source")),
                Root.Child(new FilePanelPathSegment("destination")),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            Root.Child(new FilePanelPathSegment("destination")),
            state,
            stage,
            BytesTransferred: 0,
            TotalBytes: 1024,
            Error: null,
            QueuedAt: Now,
            StartedAt: null,
            CompletedAt: null);
    }

    private class RecordingSessionHost : DispatchProxy
    {
        private long _revision = 4;

        public SessionOwner Owner { get; set; } = null!;

        public SessionId SessionId { get; set; }

        public FilePanelLocation Root { get; set; } = null!;

        public FilePanelResult<FilePanelPage> ListResult { get; set; } =
            FilePanelResult<FilePanelPage>.Success(new FilePanelPage([], null));

        public HostResult<FilePanelResult<FilePanelPage>>? NextListFailure { get; set; }

        public Func<CloseScopeRequest, long, CloseScopeResult>? CloseResultFactory { get; set; }

        public List<(EnsureFilePanelSessionRequest Request, OperationContext Context)> EnsureRequests { get; } = [];

        public List<(FilePanelListHostRequest Request, OperationContext Context)> ListRequests { get; } = [];

        public List<(string Name, OperationContext Context)> Calls { get; } = [];

        public List<FilePanelTransferEnqueueHostRequest> EnqueueRequests { get; } = [];

        public List<FilePanelTransferCancelHostRequest> CancelRequests { get; } = [];

        public List<FilePanelTransferRetryHostRequest> RetryRequests { get; } = [];

        public List<CloseScopeRequest> CloseRequests { get; } = [];

        public List<(string Name, OperationContext Context)> CloseCalls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            var context = (OperationContext)args[1]!;
            return targetMethod.Name switch
            {
                nameof(ISessionHostClient.EnsureFilePanelSessionAsync) => Ensure(
                    (EnsureFilePanelSessionRequest)args[0]!,
                    context),
                nameof(ISessionHostClient.ListFilesAsync) => List(
                    (FilePanelListHostRequest)args[0]!,
                    context),
                nameof(ISessionHostClient.StatFileAsync) => Read(
                    targetMethod.Name,
                    context,
                    FilePanelResult<FilePanelEntry>.Success(Entry(Root, "root"))),
                nameof(ISessionHostClient.PreviewFileAsync) => Read(
                    targetMethod.Name,
                    context,
                    FilePanelResult<FilePanelPreview>.Success(new FilePanelPreview(
                        Root,
                        FilePanelPreviewKind.Text,
                        "text/plain",
                        [],
                        isTruncated: false))),
                nameof(ISessionHostClient.CreateFileDirectoryAsync) => Mutate(
                    targetMethod.Name,
                    context,
                    FilePanelResult<FilePanelEntry>.Success(Entry(
                        ((FilePanelCreateDirectoryHostRequest)args[0]!).Request.Location,
                        "child"))),
                nameof(ISessionHostClient.RenameFileAsync) => Mutate(
                    targetMethod.Name,
                    context,
                    FilePanelResult<FilePanelEntry>.Success(Entry(
                        ((FilePanelRenameHostRequest)args[0]!).Request.Destination,
                        "renamed"))),
                nameof(ISessionHostClient.DeleteFileAsync) => Mutate(
                    targetMethod.Name,
                    context,
                    FilePanelResult<FilePanelDeleteReceipt>.Success(new FilePanelDeleteReceipt(
                        ((FilePanelDeleteHostRequest)args[0]!).Request.Location,
                        WasDirectory: false))),
                nameof(ISessionHostClient.EnqueueFileTransferAsync) => Enqueue(
                    (FilePanelTransferEnqueueHostRequest)args[0]!,
                    context),
                nameof(ISessionHostClient.CancelFileTransferAsync) => Cancel(
                    (FilePanelTransferCancelHostRequest)args[0]!,
                    context),
                nameof(ISessionHostClient.RetryFileTransferAsync) => Retry(
                    (FilePanelTransferRetryHostRequest)args[0]!,
                    context),
                nameof(ISessionHostClient.CloseAsync) => Close(
                    (CloseScopeRequest)args[0]!,
                    context),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }

        private ValueTask<HostResult<SessionSnapshot>> Ensure(
            EnsureFilePanelSessionRequest request,
            OperationContext context)
        {
            EnsureRequests.Add((request, context));
            var descriptor = new SessionDescriptor(
                request.SessionId,
                PanelKind.FileViewer,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                request.Owner,
                CapabilitySet.Empty,
                _revision,
                HasActiveWork: false,
                "Ready");
            return Result(new SessionSnapshot(descriptor, 1, [], null), _revision);
        }

        private ValueTask<HostResult<FilePanelResult<FilePanelPage>>> List(
            FilePanelListHostRequest request,
            OperationContext context)
        {
            ListRequests.Add((request, context));
            if (NextListFailure is { } failure)
            {
                NextListFailure = null;
                if (failure is HostResult<FilePanelResult<FilePanelPage>>.Failure hostFailure
                    && hostFailure.Error.Code == HostErrorCode.RevisionConflict)
                {
                    _revision = hostFailure.CurrentRevision;
                }

                return ValueTask.FromResult(failure);
            }

            return Result(ListResult, _revision);
        }

        private ValueTask<HostResult<FilePanelResult<T>>> Read<T>(
            string name,
            OperationContext context,
            FilePanelResult<T> result)
        {
            Calls.Add((name, context));
            return Result(result, _revision);
        }

        private ValueTask<HostResult<FilePanelResult<T>>> Mutate<T>(
            string name,
            OperationContext context,
            FilePanelResult<T> result)
        {
            Calls.Add((name, context));
            return Result(result, ++_revision);
        }

        private ValueTask<HostResult<FilePanelResult<FilePanelTransferSnapshot>>> Enqueue(
            FilePanelTransferEnqueueHostRequest request,
            OperationContext context)
        {
            EnqueueRequests.Add(request);
            var transfer = Transfer(request.Request, FilePanelTransferId.New(), "queued");
            return Mutate(
                nameof(ISessionHostClient.EnqueueFileTransferAsync),
                context,
                FilePanelResult<FilePanelTransferSnapshot>.Success(transfer));
        }

        private ValueTask<HostResult<FilePanelResult<Unit>>> Cancel(
            FilePanelTransferCancelHostRequest request,
            OperationContext context)
        {
            CancelRequests.Add(request);
            return Mutate(
                nameof(ISessionHostClient.CancelFileTransferAsync),
                context,
                FilePanelResult<Unit>.Success(Unit.Value));
        }

        private ValueTask<HostResult<FilePanelResult<FilePanelTransferSnapshot>>> Retry(
            FilePanelTransferRetryHostRequest request,
            OperationContext context)
        {
            RetryRequests.Add(request);
            var transferRequest = new FilePanelTransferRequest(
                Root.Child(new FilePanelPathSegment("source")),
                Root.Child(new FilePanelPathSegment("destination")),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail);
            return Mutate(
                nameof(ISessionHostClient.RetryFileTransferAsync),
                context,
                FilePanelResult<FilePanelTransferSnapshot>.Success(
                    Transfer(transferRequest, FilePanelTransferId.New(), "retry queued")));
        }

        private ValueTask<HostResult<CloseScopeResult>> Close(
            CloseScopeRequest request,
            OperationContext context)
        {
            CloseRequests.Add(request);
            CloseCalls.Add((nameof(ISessionHostClient.CloseAsync), context));
            _revision++;
            var result = CloseResultFactory?.Invoke(request, _revision)
                ?? new CloseScopeResult.Completed(request.Scope, request.TargetId, []);
            return Result<CloseScopeResult>(result, _revision);
        }

        private static FilePanelEntry Entry(FilePanelLocation location, string name) => new(
            location,
            name,
            FilePanelEntryKind.Directory,
            Size: null,
            LastModifiedAt: null,
            IsHidden: false);

        private static FilePanelTransferSnapshot Transfer(
            FilePanelTransferRequest request,
            FilePanelTransferId id,
            string stage) => new(
            id,
            request,
            request.Destination,
            FilePanelTransferState.Queued,
            stage,
            BytesTransferred: 0,
            TotalBytes: null,
            Error: null,
            QueuedAt: DateTimeOffset.UnixEpoch,
            StartedAt: null,
            CompletedAt: null);

        private static ValueTask<HostResult<T>> Result<T>(T value, long revision) =>
            ValueTask.FromResult(HostResult<T>.Succeed(value, revision));
    }

    private sealed class ThrowingFilePanelClient(FilePanelLocation root) : IFilePanelClient
    {
        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; private set; } =
        [
            new FileProviderProfileDescriptor(
                root.ProviderProfileId,
                "Home",
                FileProviderFamily.Posix,
                root,
                FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead
                    | FilePanelCapability.CreateDirectory
                    | FilePanelCapability.Rename
                    | FilePanelCapability.Delete,
                250,
                1024 * 1024),
        ];

        public int OperationCalls { get; private set; }

        public void ReplaceProfiles(
            IReadOnlyList<FileProviderProfileDescriptor> profiles) =>
            Profiles = profiles;

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken) => Unexpected<FilePanelPage>();

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => Unexpected<FilePanelEntry>();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => Unexpected<FilePanelPreview>();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => Unexpected<FilePanelEntry>();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => Unexpected<FilePanelEntry>();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => Unexpected<FilePanelDeleteReceipt>();

        private ValueTask<FilePanelResult<T>> Unexpected<T>()
        {
            OperationCalls++;
            throw new InvalidOperationException("File operations must pass through the session host.");
        }
    }

    private sealed class ProjectedTransferQueue : IFileTransferQueueClient
    {
        public IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; private set; } = [];

        public int MutationCalls { get; private set; }

        public event EventHandler? TransfersChanged;

        public void SetTransfers(params FilePanelTransferSnapshot[] transfers)
        {
            Transfers = transfers;
            TransfersChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) => Unexpected<FilePanelTransferSnapshot>();

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => Unexpected<Unit>();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => Unexpected<FilePanelTransferSnapshot>();

        private ValueTask<FilePanelResult<T>> Unexpected<T>()
        {
            MutationCalls++;
            throw new InvalidOperationException("Transfer mutations must pass through the session host.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
