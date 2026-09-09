using System.Collections.Concurrent;
using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;

namespace Asura.SessionHost.Tests;

public sealed class AgentFileSessionHostTests
{
    [Fact]
    public async Task Existing_file_session_cannot_be_reopened_with_a_different_trusted_root()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();

        var result = await fixture.Client.EnsureFilePanelSessionAsync(
            new EnsureFilePanelSessionRequest(
                fixture.SessionId,
                new SessionOwner(
                    HostMode.Desktop,
                    fixture.WindowId,
                    fixture.WorkspaceId,
                    fixture.TabId,
                    fixture.PanelId),
                "Files",
                Location("different-root")),
            fixture.HumanContext(),
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(fixture.Root, fixture.Files.Metadata.TrustedRoot);
        var snapshot = (await fixture.Client.GetSnapshotAsync(
            fixture.SessionId,
            fixture.HumanContext(),
            default)).Value();
        Assert.Equal(
            fixture.Files.Metadata,
            snapshot.Descriptor.FileMetadata);
    }

    [Fact]
    public async Task List_resolves_beneath_trusted_root_and_returns_bounded_first_page()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.ListOperation = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var child = request.Location
                .Child(new FilePanelPathSegment("listed.txt"))
                .WithVersion("provider-version");
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPage>.Success(
                    new FilePanelPage(
                    [
                        new FilePanelEntry(
                            child,
                            "listed.txt",
                            FilePanelEntryKind.File,
                            7,
                            null,
                            false),
                    ],
                    "provider-continuation")));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.List(
                fixture.SessionId,
                Path("logs")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var page = Assert.IsType<AgentFileActionResult.Page>(
            result.Value()).Value;
        Assert.Null(page.ContinuationToken);
        var listed = Assert.Single(page.Entries);
        Assert.Equal("listed.txt", listed.Name);
        Assert.Null(listed.Location.Version);
        var request = Assert.IsType<FilePanelListRequest>(
            fixture.Files.LastListRequest);
        Assert.Equal(100, request.PageSize);
        Assert.Null(request.ContinuationToken);
        Assert.False(request.ShowHidden);
        Assert.Equal(
            ["srv", "workspace", "logs"],
            Segments(request.Location));
        Assert.Equal(fixture.Root.ProviderProfileId, request.Location.ProviderProfileId);
        Assert.Equal(fixture.Root.Authority, request.Location.Authority);
        Assert.Null(request.Location.Version);
        Assert.Equal(1, fixture.Files.ListCount);
        Assert.Equal("files_listed", Assert.Single(
            fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Stat_uses_exact_relative_target_and_strips_provider_version()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.StatOperation = (location, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelEntry>.Success(
                    new FilePanelEntry(
                        location.WithVersion("provider-version-secret"),
                        "app.log",
                        FilePanelEntryKind.File,
                        42,
                        DateTimeOffset.UnixEpoch,
                        false)));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Stat(
                fixture.SessionId,
                Path("logs", "app.log")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var entry = Assert.IsType<AgentFileActionResult.Entry>(
            result.Value()).Value;
        Assert.Equal(["srv", "workspace", "logs", "app.log"], Segments(entry.Location));
        Assert.Null(entry.Location.Version);
        Assert.Equal(entry.Location, fixture.Files.LastStatLocation);
        Assert.Equal(1, fixture.Files.StatCount);
    }

    [Fact]
    public async Task Read_uses_bounded_preview_and_returns_only_valid_text()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.PreviewOperation = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPreview>.Success(
                    new FilePanelPreview(
                        request.Location.WithVersion("opaque-provider-version"),
                        FilePanelPreviewKind.StructuredText,
                        "Application/Json; charset=utf-8",
                        """{"status":"ok"}"""u8,
                        false)));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Read(
                fixture.SessionId,
                Path("status.json")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var preview = Assert.IsType<AgentFileActionResult.Preview>(
            result.Value()).Value;
        Assert.Equal(FilePanelPreviewKind.StructuredText, preview.Kind);
        Assert.Equal("application/json", preview.MediaType);
        Assert.Equal("""{"status":"ok"}"""u8.ToArray(), preview.Content.ToArray());
        Assert.Null(preview.Location.Version);
        Assert.Equal(
            AgentFileActionComposer.MaximumAgentReadBytes,
            fixture.Files.LastPreviewRequest?.MaximumBytes);
        Assert.Equal(1, fixture.Files.PreviewCount);
    }

    [Fact]
    public async Task Search_is_provider_gated_bounded_and_root_relative()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync(
            configureFileFactory: factory =>
                factory.MetadataFactory = root => new FileSessionMetadata(
                    root,
                    FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead
                    | FilePanelCapability.Search,
                    100,
                    64 * 1024));
        fixture.Files.SearchOperation = SearchResults;
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Search(
                fixture.SessionId,
                Path("logs"),
                "error",
                FilePanelDiscoveryScope.Subtree,
                MaximumResults: 1));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var search = Assert.IsType<AgentFileActionResult.SearchResults>(
            result.Value());
        Assert.True(search.IsTruncated);
        Assert.Equal("error.log", Assert.Single(search.Entries).Name);
        Assert.Equal(1, fixture.Files.SearchCount);
        Assert.Equal("error", fixture.Files.LastSearchRequest?.Query);
        Assert.Equal(
            FilePanelDiscoveryScope.Subtree,
            fixture.Files.LastSearchRequest?.Scope);
        Assert.False(fixture.Files.LastSearchRequest?.ShowHidden);
        Assert.Equal(
            "files_searched",
            Assert.Single(fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Access_read_strips_version_and_returns_bounded_grants()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync(
            configureFileFactory: factory =>
                factory.MetadataFactory = root => new FileSessionMetadata(
                    root,
                    FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead
                    | FilePanelCapability.Permissions,
                    100,
                    64 * 1024));
        fixture.Files.AccessControlOperation = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelAccessControl>.Success(
                    new FilePanelAccessControl(
                        request.Location.WithVersion("provider-version"),
                        mode: new FilePanelPosixMode(0x1A4),
                        owner: "alice",
                        group: "staff",
                        grants:
                        [
                            new FilePanelAccessGrant(
                                new FilePanelGrantee(
                                    FilePanelGranteeKind.User,
                                    "user-1",
                                    "Alice"),
                                FilePanelAccessRight.Read),
                        ],
                        version: "acl-version")));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.AccessRead(
                fixture.SessionId,
                Path("report.txt")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var access = Assert.IsType<AgentFileActionResult.AccessControl>(
            result.Value());
        Assert.False(access.IsTruncated);
        Assert.Null(access.Value.Version);
        Assert.Null(access.Value.Location.Version);
        Assert.Equal("alice", access.Value.Owner);
        Assert.Single(access.Value.Grants);
        Assert.Equal(
            "file_access_read",
            Assert.Single(fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Transfers_returns_only_the_session_snapshot_without_mutation()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var transfer = new FilePanelTransferSnapshot(
            FilePanelTransferId.New(),
            new FilePanelTransferRequest(
                Location("srv", "workspace", "source.txt"),
                Location("srv", "workspace", "copies"),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            Location("srv", "workspace", "copies", "source.txt"),
            FilePanelTransferState.Running,
            "Copying",
            12,
            24,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null);
        fixture.Files.AddTransfer(transfer);
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Transfers(fixture.SessionId));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var transfers = Assert.IsType<AgentFileActionResult.Transfers>(
            result.Value());
        Assert.False(transfers.IsTruncated);
        var observed = Assert.Single(transfers.Values);
        Assert.Equal(transfer.Id, observed.Id);
        Assert.Equal(FilePanelTransferState.Running, observed.State);
        Assert.Equal(12, observed.BytesTransferred);
        Assert.Equal(
            "file_transfers_read",
            Assert.Single(fixture.Authorization.Completions).StableCode);
    }

    [Fact]
    public async Task Provider_limits_reduce_agent_list_and_read_bounds()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync(
            configureFileFactory: factory =>
                factory.MetadataFactory = root => new FileSessionMetadata(
                    root,
                    FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead,
                    maximumListPageSize: 7,
                    maximumPreviewBytes: 31));
        var list = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path()));
        var listAuthorization = fixture.Authorization.Arm(list);
        _ = (await fixture.Client.RunAgentFileActionAsync(
            listAuthorization,
            list,
            default)).Value();
        var read = await fixture.PrepareAsync(
            new AgentFileRequest.Read(
                fixture.SessionId,
                Path("small.txt")));
        var readAuthorization = fixture.Authorization.Arm(read);

        _ = (await fixture.Client.RunAgentFileActionAsync(
            readAuthorization,
            read,
            default)).Value();

        Assert.Equal(7, fixture.Files.LastListRequest?.PageSize);
        Assert.Equal(31, fixture.Files.LastPreviewRequest?.MaximumBytes);
    }

    [Fact]
    public async Task Trusted_scope_change_during_authorization_denies_provider_dispatch()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path("logs")));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.BlockConsumes = true;

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        fixture.Files.ReplaceMetadata(new FileSessionMetadata(
            Location("other-root"),
            fixture.Files.Metadata.Capabilities,
            fixture.Files.Metadata.MaximumListPageSize,
            fixture.Files.Metadata.MaximumPreviewBytes));
        fixture.Authorization.ReleaseConsume.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Files.ListCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Equal(
            AgentActionOutcome.Failed,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task Capability_removed_during_authorization_denies_provider_dispatch()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path()));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.BlockConsumes = true;

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        fixture.Files.RemoveCapability(SessionCapabilities.FilesList);
        fixture.Authorization.ReleaseConsume.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            HostErrorCode.CapabilityNotSupported,
            result.Error().Code);
        Assert.Equal(0, fixture.Files.ListCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Session_revision_change_during_authorization_denies_provider_dispatch()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Stat(fixture.SessionId, Path("app.log")));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.BlockConsumes = true;

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        _ = (await fixture.Client.AttachAsync(
            new AttachSessionRequest(
                fixture.SessionId,
                new ClientId("reader-2"),
                AttachmentKind.ReadOnly,
                new ViewportDescriptor(800, 600, 1),
                new CapabilitySet([SessionCapabilities.AttachRead])),
            fixture.HumanContext(new ClientId("reader-2")),
            default)).Value();
        fixture.Authorization.ReleaseConsume.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Files.StatCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Authorization_without_read_files_capability_is_consumed_but_not_dispatched()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Stat(fixture.SessionId, Path("app.log")));
        fixture.Authorization.AuthorizationToolOverride =
            BuiltInAgentTools.TerminalReadScreen;
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Files.StatCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Single(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Provider_list_outside_trusted_root_is_rejected_without_leaking_location()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.ListOperation = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            var outside = Location("private", "password=hunter2");
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPage>.Success(
                    new FilePanelPage(
                    [
                        new FilePanelEntry(
                            outside,
                            "password=hunter2",
                            FilePanelEntryKind.File,
                            12,
                            null,
                            false),
                    ],
                    "secret-continuation")));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path()));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal("file_result_invalid", result.Error().StableCode);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Files.ListCount);
    }

    [Fact]
    public async Task Provider_page_larger_than_effective_bound_is_rejected()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync(
            configureFileFactory: factory =>
                factory.MetadataFactory = root => new FileSessionMetadata(
                    root,
                    FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead,
                    maximumListPageSize: 1,
                    maximumPreviewBytes: 128));
        fixture.Files.ListOperation = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPage>.Success(
                    new FilePanelPage(
                    [
                        ListedEntry(request.Location, "one.txt"),
                        ListedEntry(request.Location, "two.txt"),
                    ],
                    null)));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path()));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal("file_result_invalid", result.Error().StableCode);
        Assert.Equal(1, fixture.Files.ListCount);
    }

    [Fact]
    public async Task Provider_error_is_normalized_without_message_or_stable_code_leakage()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.StatOperation = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelEntry>.Failure(
                    new FilePanelError(
                        FilePanelErrorCode.AccessDenied,
                        "password_hunter2",
                        "password=hunter2",
                        Retryable: false)));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Stat(fixture.SessionId, Path("denied.txt")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("file_access_denied", result.Error().StableCode);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().StableCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_text_preview_has_stable_failure_and_returns_no_provider_data()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.PreviewOperation = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPreview>.Success(
                    new FilePanelPreview(
                        request.Location,
                        FilePanelPreviewKind.Image,
                        "image/png",
                        "password=hunter2"u8,
                        false)));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Read(fixture.SessionId, Path("image.png")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.CapabilityNotSupported, result.Error().Code);
        Assert.Equal("file_preview_not_text", result.Error().StableCode);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_shaped_text_preview_is_withheld()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        fixture.Files.PreviewOperation = (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FilePanelResult<FilePanelPreview>.Success(
                    new FilePanelPreview(
                        request.Location,
                        FilePanelPreviewKind.Text,
                        "text/plain",
                        "password=hunter2"u8,
                        false)));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Read(fixture.SessionId, Path("config.txt")));
        var authorizationId = fixture.Authorization.Arm(action);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("file_content_sensitive", result.Error().StableCode);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_after_non_cooperative_provider_return_wins()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Files.PreviewOperation = async (request, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return FilePanelResult<FilePanelPreview>.Success(
                new FilePanelPreview(
                    request.Location,
                    FilePanelPreviewKind.Text,
                    "text/plain",
                    "safe"u8,
                    false));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Read(fixture.SessionId, Path("slow.txt")));
        var authorizationId = fixture.Authorization.Arm(action);
        using var cancellation = new CancellationTokenSource();

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HostErrorCode.Cancelled, result.Error().Code);
        Assert.Equal("caller_cancelled", result.Error().StableCode);
        Assert.Equal(
            AgentActionOutcome.Cancelled,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task Expired_authorization_denies_before_provider_dispatch()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path()));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.ConsumeFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuthorizationExpired,
            "The authorization expired.");

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.DeadlineExceeded, result.Error().Code);
        Assert.Equal(0, fixture.Files.ListCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Empty(fixture.Authorization.Completions);
    }

    [Fact]
    public async Task Completion_audit_failure_does_not_redispatch()
    {
        await using var fixture = await AgentFileHostFixture.CreateAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Stat(fixture.SessionId, Path("once.txt")));
        var authorizationId = fixture.Authorization.Arm(action);
        fixture.Authorization.CompletionFailure = new AgentAuthorizationError(
            AgentAuthorizationErrorCode.AuditUnavailable,
            "The completion audit failed.");

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Files.StatCount);
        Assert.Equal(2, fixture.Authorization.Completions.Count);
        Assert.Equal(
            fixture.Authorization.Completions[0],
            fixture.Authorization.Completions[1]);
    }

    [Fact]
    public async Task Real_broker_audits_exact_file_action_and_quarantines_on_completion_failure()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var audit = new InMemoryAuditStore();
        await using var broker = new AgentCapabilityBroker(
            BuiltInAgentTools.Catalog,
            audit,
            clock);
        await using var fixture = await AgentFileHostFixture.CreateAsync(
            clock,
            broker);
        Assert.Null(await broker.RegisterRunAsync(
            new AgentRunRegistration(
                fixture.RunId,
                fixture.Agent,
                fixture.ClientId,
                new AgentTarget.Workspace(
                    fixture.WindowId,
                    fixture.WorkspaceId),
                AgentPolicy.Default,
                policyGeneration: 0),
            default));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Stat(fixture.SessionId, Path("audit.txt")));
        var authorized = Assert.IsType<AgentAuthorizationResult.Authorized>(
            await broker.RequestAsync(action.Proposal, default));
        audit.FailurePredicate = item => string.Equals(item.CorrelationId, action.Proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded;

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorized.Authorization.Id,
            action,
            default);
        var next = await fixture.PrepareAsync(
            new AgentFileRequest.List(fixture.SessionId, Path()));
        var nextAuthorization = await broker.RequestAsync(
            next.Proposal,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            result.Error().StableCode);
        Assert.Equal(1, fixture.Files.StatCount);
        Assert.Equal(
            AgentAuthorizationErrorCode.RunSuspended,
            Assert.IsType<AgentAuthorizationResult.Denied>(
                nextAuthorization).Error.Code);
        Assert.DoesNotContain(
            audit.Events,
            item => string.Equals(item.CorrelationId, action.Proposal.Id.Value
, StringComparison.Ordinal) && item.Outcome == AuditOutcome.Succeeded);
    }

    [Fact]
    public async Task Create_directory_derives_must_not_exist_and_returns_a_trusted_receipt()
    {
        await using var fixture = await MutationFixtureAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var created = Assert.IsType<AgentFileActionResult.CreatedDirectory>(
            result.Value()).Value;
        Assert.Equal(["srv", "workspace", "generated"], Segments(created.Location));
        Assert.Null(created.Location.Version);
        Assert.Equal(FilePanelEntryKind.Directory, created.Kind);
        var request = Assert.IsType<FilePanelCreateDirectoryRequest>(
            fixture.Files.LastCreateDirectoryRequest);
        Assert.Equal(
            FilePanelMutationPreconditionKind.MustNotExist,
            request.Precondition.Kind);
        Assert.Equal(1, fixture.Files.CreateDirectoryCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("directory_created", completion.StableCode);
    }

    [Fact]
    public async Task Delete_derives_non_recursive_must_exist_and_returns_a_fixed_receipt()
    {
        await using var fixture = await MutationFixtureAsync();
        var reference = await fixture.ReferenceAsync(Path("obsolete.txt"));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Delete(
                fixture.SessionId,
                Path("obsolete.txt"),
                EntryReference: reference));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var deleted = Assert.IsType<AgentFileActionResult.Deleted>(
            result.Value()).Value;
        Assert.Equal(
            ["srv", "workspace", "obsolete.txt"],
            Segments(deleted.DeletedLocation));
        Assert.Null(deleted.DeletedLocation.Version);
        var request = Assert.IsType<FilePanelDeleteRequest>(
            fixture.Files.LastDeleteRequest);
        Assert.False(request.Recursive);
        Assert.Equal(
            FilePanelMutationPreconditionKind.VersionMatches,
            request.Precondition.Kind);
        Assert.Equal(1, fixture.Files.DeleteCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("file_deleted", completion.StableCode);
    }

    [Fact]
    public async Task Move_derives_must_not_exist_and_returns_the_verified_destination()
    {
        await using var fixture = await MutationFixtureAsync();
        var reference = await fixture.ReferenceAsync(Path("draft.txt"));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Move(
                fixture.SessionId,
                Path("draft.txt"),
                Path("published", "report.txt"),
                reference));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        var moved = Assert.IsType<AgentFileActionResult.Moved>(
            result.Value()).Value;
        Assert.Equal(
            ["srv", "workspace", "published", "report.txt"],
            Segments(moved.Location));
        Assert.Null(moved.Location.Version);
        var request = Assert.IsType<FilePanelRenameRequest>(
            fixture.Files.LastRenameRequest);
        Assert.Equal(
            ["srv", "workspace", "draft.txt"],
            Segments(request.Source));
        Assert.Equal(
            ["srv", "workspace", "published", "report.txt"],
            Segments(request.Destination));
        Assert.Equal(
            FilePanelMutationPreconditionKind.MustNotExist,
            request.DestinationPrecondition.Kind);
        Assert.Equal(1, fixture.Files.RenameCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Succeeded, completion.Outcome);
        Assert.Equal("file_moved", completion.StableCode);
    }

    [Fact]
    public async Task Text_create_and_replace_use_exact_preconditions_and_consume_stat_reference()
    {
        await using var fixture = await MutationFixtureAsync();
        var create = await fixture.PrepareAsync(
            new AgentFileRequest.CreateText(
                fixture.SessionId,
                Path("note.txt"),
                "hello"));
        var createResult = await fixture.Client.RunAgentFileActionAsync(
            fixture.Authorization.Arm(create, AgentAuthorizationSource.HumanApproval),
            create,
            default);

        Assert.IsType<AgentFileActionResult.CreatedText>(createResult.Value());
        Assert.Equal(
            FilePanelMutationPreconditionKind.MustNotExist,
            fixture.Files.LastWriteTextRequest!.Precondition.Kind);
        fixture.Authorization.ClearCompletions();

        var reference = await fixture.ReferenceAsync(Path("note.txt"));
        var replace = await fixture.PrepareAsync(
            new AgentFileRequest.ReplaceText(
                fixture.SessionId,
                Path("note.txt"),
                reference,
                "updated"));
        var replaceResult = await fixture.Client.RunAgentFileActionAsync(
            fixture.Authorization.Arm(replace, AgentAuthorizationSource.HumanApproval),
            replace,
            default);

        Assert.IsType<AgentFileActionResult.ReplacedText>(replaceResult.Value());
        Assert.Equal(
            FilePanelMutationPreconditionKind.VersionMatches,
            fixture.Files.LastWriteTextRequest!.Precondition.Kind);
        Assert.Equal("test-version", fixture.Files.LastWriteTextRequest.Location.Version);

        var replay = await fixture.PrepareAsync(
            new AgentFileRequest.ReplaceText(
                fixture.SessionId,
                Path("note.txt"),
                reference,
                "again"));
        var replayResult = await fixture.Client.RunAgentFileActionAsync(
            fixture.Authorization.Arm(replay, AgentAuthorizationSource.HumanApproval),
            replay,
            default);
        Assert.Equal("file_reference_invalid", replayResult.Error().StableCode);
        Assert.Equal(2, fixture.Files.WriteTextCount);
    }

    [Fact]
    public async Task Copy_requires_a_versioned_regular_source_and_a_new_destination()
    {
        await using var fixture = await MutationFixtureAsync();
        var reference = await fixture.ReferenceAsync(Path("source.txt"));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Copy(
                fixture.SessionId,
                Path("source.txt"),
                reference,
                Path("archive", "source.txt")));

        var result = await fixture.Client.RunAgentFileActionAsync(
            fixture.Authorization.Arm(action, AgentAuthorizationSource.HumanApproval),
            action,
            default);

        Assert.IsType<AgentFileActionResult.Copied>(result.Value());
        Assert.Equal("test-version", fixture.Files.LastCopyRequest!.Source.Version);
        Assert.Equal(
            AgentFileActionComposer.MaximumAgentCopyBytes,
            fixture.Files.LastCopyRequest.MaximumBytes);
        Assert.Equal(1, fixture.Files.CopyCount);
    }

    [Fact]
    public async Task Mutation_rejects_auto_policy_before_provider_dispatch()
    {
        await using var fixture = await MutationFixtureAsync();
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.AutoPolicy);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, fixture.Files.CreateDirectoryCount);
        Assert.Equal(
            AgentActionOutcome.Failed,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task GovernedMutationCapabilityRemovedDuringAuthorizationDeniesDispatch()
    {
        await using var fixture = await MutationFixtureAsync();
        var reference = await fixture.ReferenceAsync(Path("obsolete.txt"));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Delete(
                fixture.SessionId,
                Path("obsolete.txt"),
                EntryReference: reference));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);
        fixture.Authorization.BlockConsumes = true;

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await fixture.Authorization.ConsumeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        fixture.Files.ReplaceMetadata(new FileSessionMetadata(
            fixture.Files.Metadata.TrustedRoot,
            fixture.Files.Metadata.Capabilities
            & ~FilePanelCapability.GovernedDelete,
            fixture.Files.Metadata.MaximumListPageSize,
            fixture.Files.Metadata.MaximumPreviewBytes));
        fixture.Authorization.ReleaseConsume.TrySetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            HostErrorCode.CapabilityNotSupported,
            result.Error().Code);
        Assert.Equal(0, fixture.Files.DeleteCount);
        Assert.Equal(1, fixture.Authorization.ConsumeCount);
        Assert.Equal(
            AgentActionOutcome.Failed,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task Mutation_success_wins_over_late_caller_cancellation()
    {
        await using var fixture = await MutationFixtureAsync();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Files.CreateDirectoryOperation = async (request, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return FilePanelResult<FilePanelEntry>.Success(
                new FilePanelEntry(
                    request.Location.WithVersion("opaque-provider-version"),
                    "generated",
                    FilePanelEntryKind.Directory,
                    null,
                    null,
                    false));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);
        using var cancellation = new CancellationTokenSource();

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        _ = Assert.IsType<AgentFileActionResult.CreatedDirectory>(
            result.Value());
        Assert.Equal(1, fixture.Files.CreateDirectoryCount);
        Assert.Equal(
            AgentActionOutcome.Succeeded,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task Mutation_success_wins_over_late_authority_and_capability_drift()
    {
        await using var fixture = await MutationFixtureAsync();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Files.CreateDirectoryOperation = async (request, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return FilePanelResult<FilePanelEntry>.Success(
                new FilePanelEntry(
                    request.Location,
                    "generated",
                    FilePanelEntryKind.Directory,
                    null,
                    null,
                    false));
        };
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        using var authority = new CancellationTokenSource();
        fixture.Authorization.PermitCancellationToken = authority.Token;
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var execution = fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await authority.CancelAsync();
        fixture.Files.RemoveCapability(
            SessionCapabilities.FilesCreateDirectory);
        release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        _ = Assert.IsType<AgentFileActionResult.CreatedDirectory>(
            result.Value());
        Assert.Equal(1, fixture.Files.CreateDirectoryCount);
        Assert.Equal(
            AgentActionOutcome.Succeeded,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    [Fact]
    public async Task Deterministic_mutation_rejection_preserves_safe_typed_failure()
    {
        await using var fixture = await MutationFixtureAsync();
        var reference = await fixture.ReferenceAsync(Path("obsolete.txt"));
        fixture.Files.DeleteOperation = (_, _) =>
            ValueTask.FromResult(
                FilePanelResult<FilePanelDeleteReceipt>.Failure(
                    new FilePanelError(
                        FilePanelErrorCode.AccessDenied,
                        "provider-secret-code",
                        "password=hunter2",
                        Retryable: true)));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.Delete(
                fixture.SessionId,
                Path("obsolete.txt"),
                EntryReference: reference));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("file_access_denied", result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Files.DeleteCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("file_access_denied", completion.StableCode);
    }

    [Fact]
    public async Task Create_directory_precondition_failure_is_returned_to_agent()
    {
        await using var fixture = await MutationFixtureAsync();
        fixture.Files.CreateDirectoryOperation = (_, _) =>
            ValueTask.FromResult(
                FilePanelResult<FilePanelEntry>.Failure(
                    new FilePanelError(
                        FilePanelErrorCode.PreconditionFailed,
                        "provider-secret-code",
                        "password=hunter2",
                        Retryable: true)));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal("file_precondition_failed", result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Files.CreateDirectoryCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal("file_precondition_failed", completion.StableCode);
    }

    [Fact]
    public async Task Ambiguous_mutation_transport_failure_remains_outcome_unknown()
    {
        await using var fixture = await MutationFixtureAsync();
        fixture.Files.CreateDirectoryOperation = (_, _) =>
            ValueTask.FromResult(
                FilePanelResult<FilePanelEntry>.Failure(
                    new FilePanelError(
                        FilePanelErrorCode.IoFailure,
                        "provider-secret-code",
                        "password=hunter2",
                        Retryable: true)));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(HostErrorCode.EngineFailed, result.Error().Code);
        Assert.Equal(
            "file_mutation_outcome_unknown",
            result.Error().StableCode);
        Assert.False(result.Error().Retryable);
        Assert.DoesNotContain(
            "hunter2",
            result.Error().Message,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Files.CreateDirectoryCount);
        var completion = Assert.Single(fixture.Authorization.Completions);
        Assert.Equal(AgentActionOutcome.Failed, completion.Outcome);
        Assert.Equal(
            "file_mutation_outcome_unknown",
            completion.StableCode);
    }

    [Fact]
    public async Task Invalid_mutation_receipt_is_outcome_unknown_and_not_redispatched()
    {
        await using var fixture = await MutationFixtureAsync();
        fixture.Files.CreateDirectoryOperation = (_, _) =>
            ValueTask.FromResult(
                FilePanelResult<FilePanelEntry>.Success(
                    new FilePanelEntry(
                        Location("outside", "generated"),
                        "generated",
                        FilePanelEntryKind.Directory,
                        null,
                        null,
                        false)));
        var action = await fixture.PrepareAsync(
            new AgentFileRequest.CreateDirectory(
                fixture.SessionId,
                Path("generated")));
        var authorizationId = fixture.Authorization.Arm(
            action,
            AgentAuthorizationSource.HumanApproval);

        var result = await fixture.Client.RunAgentFileActionAsync(
            authorizationId,
            action,
            default);

        Assert.Equal(
            "file_mutation_outcome_unknown",
            result.Error().StableCode);
        Assert.Equal(1, fixture.Files.CreateDirectoryCount);
        Assert.Equal(
            AgentActionOutcome.Failed,
            Assert.Single(fixture.Authorization.Completions).Outcome);
    }

    private static ValueTask<AgentFileHostFixture> MutationFixtureAsync() =>
        AgentFileHostFixture.CreateAsync(
            configureFileFactory: factory =>
                factory.MetadataFactory = root => new FileSessionMetadata(
                    root,
                    FilePanelCapability.List
                    | FilePanelCapability.Stat
                    | FilePanelCapability.RangedRead
                    | FilePanelCapability.CreateDirectory
                    | FilePanelCapability.Rename
                    | FilePanelCapability.Delete
                    | FilePanelCapability.StreamingWrite
                    | FilePanelCapability.Copy
                    | FilePanelCapability.GovernedCreateDirectory
                    | FilePanelCapability.GovernedRename
                    | FilePanelCapability.GovernedDelete
                    | FilePanelCapability.GovernedCreateFile
                    | FilePanelCapability.GovernedReplaceFile
                    | FilePanelCapability.GovernedCopySource
                    | FilePanelCapability.GovernedCopy,
                    maximumListPageSize: 100,
                    maximumPreviewBytes: 64 * 1024));

    private static ImmutableArray<FilePanelPathSegment> Path(
        params string[] segments) =>
        [.. segments.Select(item => new FilePanelPathSegment(item))];

    private static FilePanelLocation Location(params string[] segments) =>
        new(
            "files.remote",
            "server.example",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                    segments.Select(item =>
                        new FilePanelPathSegment(item)))));

    private static string[] Segments(FilePanelLocation location) =>
        [.. Assert.IsType<FilePanelAddress.Hierarchical>(location.Address)
            .Path.Segments
            .Select(item => item.Value)];

    private static FilePanelEntry ListedEntry(
        FilePanelLocation parent,
        string name) =>
        new(
            parent.Child(new FilePanelPathSegment(name)),
            name,
            FilePanelEntryKind.File,
            1,
            null,
            false);

    private static async IAsyncEnumerable<FilePanelResult<FilePanelEntry>>
        SearchResults(
            FilePanelSearchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return FilePanelResult<FilePanelEntry>.Success(
            ListedEntry(request.Location, "error.log"));
        yield return FilePanelResult<FilePanelEntry>.Success(
            ListedEntry(request.Location, "other-error.log"));
        await Task.CompletedTask;
    }

    private sealed class AgentFileHostFixture : IAsyncDisposable
    {
        private AgentFileHostFixture(
            ManualTimeProvider? clock,
            IAgentAuthorizationConsumer? authorizationConsumer)
        {
            Clock = clock ?? new ManualTimeProvider(DateTimeOffset.UnixEpoch);
            TerminalFactory = new FakeTerminalSessionFactory();
            FileFactory = new FakeFilePanelSessionFactory();
            Composer = new AgentFileActionComposer();
            Authorization = new FakeAuthorizationConsumer(Clock);
            Client = new InMemorySessionHostClient(
                TerminalFactory,
                new DesktopLifecyclePolicy(),
                Clock,
                filePanelFactory: FileFactory,
                agentAuthorizationConsumer:
                    authorizationConsumer ?? Authorization,
                agentFileActionComposer: Composer);
        }

        public ManualTimeProvider Clock { get; }

        public FakeTerminalSessionFactory TerminalFactory { get; }

        public FakeFilePanelSessionFactory FileFactory { get; }

        public AgentFileActionComposer Composer { get; }

        public FakeAuthorizationConsumer Authorization { get; }

        public InMemorySessionHostClient Client { get; }

        public ClientId ClientId { get; } = new("test-client");

        public WindowInstanceId WindowId { get; } = new("window-1");

        public WorkspaceInstanceId WorkspaceId { get; } = new("workspace-1");

        public TabInstanceId TabId { get; } = new("tab-1");

        public PanelInstanceId PanelId { get; } = new("panel-1");

        public SessionId SessionId { get; } = new("file-session-1");

        public AgentRunId RunId { get; } = new("run-1");

        public ActorDescriptor Agent { get; } = new(
            new ActorId("agent-1"),
            ActorKind.Agent,
            "Test agent");

        public FilePanelLocation Root { get; } =
            Location("srv", "workspace");

        public FakeFilePanelSession Files => FileFactory[SessionId];

        public static async ValueTask<AgentFileHostFixture> CreateAsync(
            ManualTimeProvider? clock = null,
            IAgentAuthorizationConsumer? authorizationConsumer = null,
            Action<FakeFilePanelSessionFactory>? configureFileFactory = null)
        {
            var fixture = new AgentFileHostFixture(
                clock,
                authorizationConsumer);
            configureFileFactory?.Invoke(fixture.FileFactory);
            var panel = new PanelInstance(
                fixture.PanelId,
                PanelKind.FileViewer,
                "Files");
            var tab = new TabInstance(
                fixture.TabId,
                "Primary",
                [panel],
                panel.Id);
            var workspace = new WorkspaceInstance(
                fixture.WorkspaceId,
                "Workspace",
                [tab],
                tab.Id);
            _ = (await fixture.Client.RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(
                    fixture.WindowId,
                    workspace),
                fixture.HumanContext(),
                default)).Value();
            _ = (await fixture.Client.EnsureFilePanelSessionAsync(
                new EnsureFilePanelSessionRequest(
                    fixture.SessionId,
                    new SessionOwner(
                        HostMode.Desktop,
                        fixture.WindowId,
                        fixture.WorkspaceId,
                        fixture.TabId,
                        fixture.PanelId),
                    "Files",
                    fixture.Root),
                fixture.HumanContext(),
                default)).Value();
            return fixture;
        }

        public async ValueTask<AgentFileAction> PrepareAsync(
            AgentFileRequest request)
        {
            var context = (await Client.InspectAgentContextAsync(
                new AgentContextRequest(
                    new AgentTarget.Panel(
                        WindowId,
                        WorkspaceId,
                        TabId,
                        PanelId)),
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

        public async ValueTask<AgentFileEntryReference> ReferenceAsync(
            ImmutableArray<FilePanelPathSegment> path)
        {
            var stat = await PrepareAsync(new AgentFileRequest.Stat(SessionId, path));
            var authorization = Authorization.Arm(stat);
            var result = await Client.RunAgentFileActionAsync(
                authorization,
                stat,
                default);
            var reference = Assert.IsType<AgentFileActionResult.Entry>(result.Value())
                .Reference;
            Authorization.ClearCompletions();
            return Assert.NotNull(reference);
        }

        public OperationContext HumanContext(ClientId? clientId = null)
        {
            var authenticatedClient = clientId ?? ClientId;
            return new(
                RequestId.New(),
                new ActorDescriptor(
                    new ActorId(authenticatedClient.Value),
                    ActorKind.Human,
                    "Test user",
                    authenticatedClient),
                CancellationId: CancellationId.New());
        }

        private OperationContext AgentContext() =>
            new(
                RequestId.New(),
                Agent,
                CancellationId: CancellationId.New());

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private sealed class FakeAuthorizationConsumer(TimeProvider timeProvider)
        : IAgentAuthorizationConsumer
    {
        private readonly ConcurrentQueue<AgentActionCompletion> _completions = new();
        private AgentFileAction? _authorizedAction;
        private AgentAuthorizationId _authorizationId;
        private AgentAuthorizationSource _source;
        private ClientId _approvingClientId = new("test-client");
        private int _consumed;
        private int _consumeCount;

        public int ConsumeCount => Volatile.Read(ref _consumeCount);

        public AgentAuthorizationError? ConsumeFailure { get; set; }

        public AgentAuthorizationError? CompletionFailure { get; set; }

        public string? AuthorizationToolOverride { get; set; }

        public CancellationToken PermitCancellationToken { get; set; }

        public bool BlockConsumes { get; set; }

        public TaskCompletionSource ConsumeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseConsume { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AgentActionCompletion> Completions =>
            [.. _completions];

        public void ClearCompletions()
        {
            while (_completions.TryDequeue(out _))
            {
            }

            Volatile.Write(ref _consumeCount, 0);
        }

        public AgentAuthorizationId Arm(
            AgentFileAction action,
            AgentAuthorizationSource source = AgentAuthorizationSource.AutoPolicy,
            ClientId? approvingClientId = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            _authorizedAction = action;
            _authorizationId = AgentAuthorizationId.New();
            _source = source;
            _approvingClientId = approvingClientId ?? new ClientId("test-client");
            Volatile.Write(ref _consumed, 0);
            ConsumeFailure = null;
            CompletionFailure = null;
            return _authorizationId;
        }

        public async ValueTask<AgentPermitResult> ConsumeAsync(
            AgentAuthorizationId authorizationId,
            AgentActionExecutionBinding currentBinding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _consumeCount);
            ConsumeStarted.TrySetResult();
            if (BlockConsumes)
            {
                await ReleaseConsume.Task.WaitAsync(cancellationToken);
            }

            if (ConsumeFailure is { } failure)
            {
                return new AgentPermitResult.Denied(failure);
            }

            var action = _authorizedAction
                ?? throw new InvalidOperationException(
                    "An action must be armed before consuming authorization.");
            var expected = AgentActionExecutionBinding.FromProposal(
                action.Proposal);
            if (authorizationId != _authorizationId
                || !Matches(expected, currentBinding))
            {
                return Denied(
                    AgentAuthorizationErrorCode.AuthorizationMismatch,
                    "The execution binding differs from the approved action.");
            }

            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                return Denied(
                    AgentAuthorizationErrorCode.AuthorizationNotFound,
                    "The one-action authorization has already been consumed.");
            }

            var toolName = AuthorizationToolOverride
                ?? action.Proposal.ToolName;
            if (!BuiltInAgentTools.Catalog.TryGet(toolName, out var tool))
            {
                throw new InvalidOperationException(
                    "The test authorization tool is missing from the built-in catalog.");
            }

            var now = timeProvider.GetUtcNow();
            var authorization = new AgentActionAuthorization(
                authorizationId,
                action.Proposal,
                tool!,
                _source,
                _approvingClientId,
                now.AddMinutes(1));
            return new AgentPermitResult.Granted(
                new AgentActionPermit(
                    authorization,
                    now,
                    PermitCancellationToken));
        }

        public ValueTask<AgentAuthorizationError?> CompleteAsync(
            AgentActionPermit permit,
            AgentActionCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _completions.Enqueue(completion);
            return ValueTask.FromResult(CompletionFailure);
        }

        private static AgentPermitResult.Denied Denied(
            AgentAuthorizationErrorCode code,
            string message) =>
            new(new AgentAuthorizationError(code, message));

        private static bool Matches(
            AgentActionExecutionBinding expected,
            AgentActionExecutionBinding actual) =>
            expected.ActionId == actual.ActionId
            && expected.RunId == actual.RunId
            && expected.ActorId == actual.ActorId
            && string.Equals(
                expected.ToolName,
                actual.ToolName,
                StringComparison.Ordinal)
            && expected.Target == actual.Target
            && expected.TargetIdentity == actual.TargetIdentity
            && expected.TargetFingerprint == actual.TargetFingerprint
            && expected.ArgumentDigest == actual.ArgumentDigest
            && expected.PolicyGeneration == actual.PolicyGeneration;
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

        public Func<AuditEventRecord, bool>? FailurePredicate { get; set; }

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            cancellationToken.ThrowIfCancellationRequested();
            if (FailurePredicate?.Invoke(auditEvent) == true)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<Unit>.Failure(
                        new AuditStoreError(
                            AuditStoreErrorCode.StorageUnavailable,
                            "The audit store is unavailable.")));
            }

            lock (_gate)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.FromResult(
                AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                        [.. _events
                            .Where(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal))]));
            }
        }
    }
}
