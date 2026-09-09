using System.Text;
using Asura.Application;
using Asura.Core;
using RuntimeProfileId = Asura.Core.FileProviderProfileId;

namespace Asura.Files.Tests;

public sealed class CatalogFileProviderRuntimeTests
{
    [Fact]
    public void RetiredGenerationDisposesOwnedClientOnlyAfterLastLease()
    {
        var authority = new FileAuthority("memory");
        var provider = new InMemoryFileProvider(
            new FileProviderProfileId("leased-provider"),
            authority);
        var owner = new TrackingDisposable();
        var registration = new OwnedFileProviderRegistration(
            new RuntimeProfileId("files.leased-provider"),
            new FileProviderRegistration(
                "Leased provider",
                FileProviderFamily.Posix,
                provider,
                new FileLocation(provider.ProfileId, authority, FilePath.Root)),
            [owner]);
        var generation = new ProviderGeneration([registration]);
        using var lease = generation.Acquire();

        generation.Retire();

        Assert.False(owner.IsDisposed);
        lease.Dispose();
        Assert.True(owner.IsDisposed);
    }

    [Fact]
    public async Task CatalogChangesAtomicallyRefreshProfilesAndPreserveBuiltInHome()
    {
        var root = Directory.CreateTempSubdirectory("asura-runtime-profile-");
        var catalog = CreateCatalog();
        var initialized = await catalog.InitializeAsync(CancellationToken.None);
        Assert.True(initialized.IsSuccess, initialized.Error?.Message);
        using var vault = new RejectingSecretVault();
        using var runtime = new CatalogFileProviderRuntime(
            catalog,
            vault,
            new InMemorySftpKnownHostStore());
        await WaitForProfilesAsync(runtime, () => runtime.Profiles.Count == 1);
        Assert.Equal("builtin.files.home", Assert.Single(runtime.Profiles).Id);

        var profile = new FileProviderProfile(
            new RuntimeProfileId("files.test-local"),
            FileProviderProfile.CurrentSchemaVersion,
            "Test root",
            new FileProviderConfiguration.Local(root.FullName));
        var added = WaitForProfileChangeAsync(runtime);
        var saved = await catalog.SaveFileProviderProfileAsync(
            profile,
            null,
            CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        await added;

        Assert.Contains(runtime.Profiles, item => string.Equals(item.Id, profile.Id.Value, StringComparison.Ordinal));
        Assert.Contains(runtime.Profiles, item => string.Equals(item.Id, "builtin.files.home", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Diagnostics, item => item.ProfileId == profile.Id
            && item.Severity == FileProviderRuntimeDiagnosticSeverity.Error);

        var removed = WaitForProfileChangeAsync(runtime);
        var deleted = await catalog.DeleteAsync(
            profile.Key,
            saved.Value!.Revision,
            CancellationToken.None);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        await removed;

        Assert.Equal("builtin.files.home", Assert.Single(runtime.Profiles).Id);
        root.Delete(recursive: true);
    }

    [Fact]
    public async Task PosixCatalogSessionAdvertisesAndExecutesGovernedExactMutations()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("asura-governed-local-");
        try
        {
            var catalog = CreateCatalog();
            Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
            using var vault = new RejectingSecretVault();
            using var runtime = new CatalogFileProviderRuntime(
                catalog,
                vault,
                new InMemorySftpKnownHostStore());
            var profile = new FileProviderProfile(
                new RuntimeProfileId("files.governed-local"),
                FileProviderProfile.CurrentSchemaVersion,
                "Governed local",
                new FileProviderConfiguration.Local(root.FullName));
            var refreshed = WaitForProfileChangeAsync(runtime);
            var saved = await catalog.SaveFileProviderProfileAsync(
                profile,
                expectedRevision: null,
                CancellationToken.None);
            Assert.True(saved.IsSuccess, saved.Error?.Message);
            await refreshed;
            var descriptor = Assert.Single(runtime.Profiles, item => string.Equals(item.Id, profile.Id.Value, StringComparison.Ordinal));
            Assert.True(descriptor.Capabilities.HasFlag(
                FilePanelCapability.GovernedCreateDirectory));
            Assert.True(descriptor.Capabilities.HasFlag(
                FilePanelCapability.GovernedDelete));
            var factory = new FilePanelSessionFactory(runtime, runtime);
            await using var session = await factory.CreateAsync(
                new SessionId("governed-local-session"),
                descriptor.Root,
                CancellationToken.None);
            var directory = descriptor.Root.Child(
                new FilePanelPathSegment("agent-created"));

            var created = await session.CreateDirectoryAsync(
                new FilePanelCreateDirectoryRequest(
                    directory,
                    FilePanelMutationPrecondition.MustNotExist),
                CancellationToken.None);
            var deleted = await session.DeleteAsync(
                new FilePanelDeleteRequest(
                    directory,
                    Recursive: false,
                    FilePanelMutationPrecondition.MustExist),
                CancellationToken.None);

            Assert.True(created.IsSuccess, created.Error?.Message);
            Assert.True(deleted.IsSuccess, deleted.Error?.Message);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "agent-created")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HostedFileSessionsRemainBoundToTheirProviderGenerationAcrossRefresh()
    {
        var oldRoot = Directory.CreateTempSubdirectory(
            "asura-runtime-old-root-");
        var newRoot = Directory.CreateTempSubdirectory(
            "asura-runtime-new-root-");
        await File.WriteAllTextAsync(
            Path.Combine(oldRoot.FullName, "marker.txt"),
            "old generation");
        await File.WriteAllTextAsync(
            Path.Combine(newRoot.FullName, "marker.txt"),
            "new generation");
        var catalog = CreateCatalog();
        Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        using var vault = new RejectingSecretVault();
        using var runtime = new CatalogFileProviderRuntime(
            catalog,
            vault,
            new InMemorySftpKnownHostStore());
        var profile = new FileProviderProfile(
            new RuntimeProfileId("files.pinned-local"),
            FileProviderProfile.CurrentSchemaVersion,
            "Pinned root",
            new FileProviderConfiguration.Local(oldRoot.FullName));
        var initialRefresh = WaitForProfileChangeAsync(runtime);
        var saved = await catalog.SaveFileProviderProfileAsync(
            profile,
            null,
            CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        await initialRefresh;
        await WaitForProfilesAsync(
            runtime,
            () => runtime.Profiles.Any(item => string.Equals(item.Id, profile.Id.Value, StringComparison.Ordinal)));
        var factory = new FilePanelSessionFactory(runtime, runtime);
        var oldLocation = runtime.Profiles
            .Single(item => string.Equals(item.Id, profile.Id.Value, StringComparison.Ordinal))
            .Root;
        await using var oldSession = await factory.CreateAsync(
            new SessionId("files-old-generation"),
            oldLocation,
            CancellationToken.None);

        var replacement = new FileProviderProfile(
            profile.Id,
            profile.SchemaVersion,
            profile.Name,
            new FileProviderConfiguration.Local(newRoot.FullName));
        var replacementRefresh = WaitForProfileChangeAsync(runtime);
        var replaced = await catalog.SaveFileProviderProfileAsync(
            replacement,
            saved.Value!.Revision,
            CancellationToken.None);
        Assert.True(replaced.IsSuccess, replaced.Error?.Message);
        await replacementRefresh;
        var newLocation = runtime.Profiles
            .Single(item => string.Equals(item.Id, profile.Id.Value, StringComparison.Ordinal))
            .Root;
        await using var newSession = await factory.CreateAsync(
            new SessionId("files-new-generation"),
            newLocation,
            CancellationToken.None);

        var oldContent = await ReadTextAsync(oldSession, oldLocation);
        var newContent = await ReadTextAsync(newSession, newLocation);

        Assert.Equal("old generation", oldContent);
        Assert.Equal("new generation", newContent);
        await oldSession.DisposeAsync();
        await newSession.DisposeAsync();
        oldRoot.Delete(recursive: true);
        newRoot.Delete(recursive: true);
    }

    [Fact]
    public async Task HostedFileSessionTransfersRemainBoundToTheirProviderGenerationAcrossRefresh()
    {
        var oldRoot = Directory.CreateTempSubdirectory(
            "asura-runtime-old-transfer-root-");
        var newRoot = Directory.CreateTempSubdirectory(
            "asura-runtime-new-transfer-root-");
        await File.WriteAllTextAsync(
            Path.Combine(oldRoot.FullName, "source.txt"),
            "old generation");
        await File.WriteAllTextAsync(
            Path.Combine(newRoot.FullName, "source.txt"),
            "new generation");
        var catalog = CreateCatalog();
        Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        using var vault = new RejectingSecretVault();
        using var runtime = new CatalogFileProviderRuntime(
            catalog,
            vault,
            new InMemorySftpKnownHostStore());
        var profile = new FileProviderProfile(
            new RuntimeProfileId("files.pinned-transfer"),
            FileProviderProfile.CurrentSchemaVersion,
            "Pinned transfer root",
            new FileProviderConfiguration.Local(oldRoot.FullName));
        var initialRefresh = WaitForProfileChangeAsync(runtime);
        var saved = await catalog.SaveFileProviderProfileAsync(
            profile,
            null,
            CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        await initialRefresh;
        var factory = new FilePanelSessionFactory(runtime, runtime);
        var oldLocation = runtime.Profiles
            .Single(item => string.Equals(item.Id, profile.Id.Value, StringComparison.Ordinal))
            .Root;
        await using var oldSession = await factory.CreateAsync(
            new SessionId("files-old-transfer-generation"),
            oldLocation,
            CancellationToken.None);
        var failed = await oldSession.EnqueueTransferAsync(
            new FilePanelTransferRequest(
                oldLocation.Child(new FilePanelPathSegment("retry-source.txt")),
                oldLocation.Child(new FilePanelPathSegment("retried.txt")),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        Assert.True(failed.IsSuccess, failed.Error?.Message);
        Assert.Equal(
            FilePanelTransferState.Failed,
            (await WaitForTransferAsync(oldSession, failed.Value!.Id)).State);

        var replacement = new FileProviderProfile(
            profile.Id,
            profile.SchemaVersion,
            profile.Name,
            new FileProviderConfiguration.Local(newRoot.FullName));
        var replacementRefresh = WaitForProfileChangeAsync(runtime);
        var replaced = await catalog.SaveFileProviderProfileAsync(
            replacement,
            saved.Value!.Revision,
            CancellationToken.None);
        Assert.True(replaced.IsSuccess, replaced.Error?.Message);
        await replacementRefresh;
        await File.WriteAllTextAsync(
            Path.Combine(oldRoot.FullName, "retry-source.txt"),
            "old retry generation");
        await File.WriteAllTextAsync(
            Path.Combine(newRoot.FullName, "retry-source.txt"),
            "new retry generation");

        var enqueued = await oldSession.EnqueueTransferAsync(
            new FilePanelTransferRequest(
                oldLocation.Child(new FilePanelPathSegment("source.txt")),
                oldLocation.Child(new FilePanelPathSegment("copied.txt")),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        Assert.True(enqueued.IsSuccess, enqueued.Error?.Message);
        var completed = await WaitForTransferAsync(oldSession, enqueued.Value!.Id);
        var retried = await oldSession.RetryTransferAsync(
            failed.Value.Id,
            CancellationToken.None);
        Assert.True(retried.IsSuccess, retried.Error?.Message);
        var retryCompleted = await WaitForTransferAsync(
            oldSession,
            retried.Value!.Id);

        Assert.Equal(FilePanelTransferState.Completed, completed.State);
        Assert.Equal(FilePanelTransferState.Completed, retryCompleted.State);
        Assert.Equal(
            "old generation",
            await File.ReadAllTextAsync(Path.Combine(oldRoot.FullName, "copied.txt")));
        Assert.Equal(
            "old retry generation",
            await File.ReadAllTextAsync(Path.Combine(oldRoot.FullName, "retried.txt")));
        Assert.False(File.Exists(Path.Combine(newRoot.FullName, "copied.txt")));
        Assert.False(File.Exists(Path.Combine(newRoot.FullName, "retried.txt")));
        await oldSession.DisposeAsync();
        oldRoot.Delete(recursive: true);
        newRoot.Delete(recursive: true);
    }

    [Fact]
    public async Task SftpHostKeyRepairUsesTheReferencedConnectionSecurityWorkflow()
    {
        var catalog = CreateCatalog();
        Assert.True((await catalog.InitializeAsync(CancellationToken.None)).IsSuccess);
        var connection = new ConnectionProfile(
            new ConnectionId("ssh.shared-trust"),
            ConnectionProfile.CurrentSchemaVersion,
            "Shared trust",
            new ConnectionEndpoint.Ssh("sftp.example.test", username: "operator"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        Assert.True((await catalog.SaveConnectionAsync(
            connection,
            null,
            CancellationToken.None)).IsSuccess);
        var security = new RecordingConnectionSecurityRuntime(connection);
        using var vault = new RejectingSecretVault();
        using var runtime = new CatalogFileProviderRuntime(
            catalog,
            vault,
            new InMemorySftpKnownHostStore(),
            security);
        var provider = new FileProviderProfile(
            new RuntimeProfileId("files.shared-trust"),
            FileProviderProfile.CurrentSchemaVersion,
            "Shared SFTP",
            new FileProviderConfiguration.Sftp(connection.Id, "/"));

        var inspected = await runtime.InspectSshHostKeyAsync(provider, CancellationToken.None);
        var review = Assert.IsType<ConnectionRuntimeResult<SshHostKeyReview>.Success>(inspected).Value;
        var trusted = await runtime.TrustSshHostKeyAsync(
            provider,
            review.Id,
            SshHostKeyTrustAction.TrustNew,
            CancellationToken.None);

        Assert.IsType<ConnectionRuntimeResult<SshHostKeyReview>.Success>(trusted);
        Assert.Same(connection, security.InspectedProfile);
        Assert.Equal(connection.Id, security.TrustRequest?.ConnectionId);
        Assert.Equal(SshHostKeyTrustAction.TrustNew, security.TrustRequest?.Action);
    }

    private static async Task WaitForProfilesAsync(
        CatalogFileProviderRuntime runtime,
        Func<bool> condition)
    {
        if (condition())
        {
            return;
        }

        for (var attempt = 0; attempt < 20 && !condition(); attempt++)
        {
            await WaitForProfileChangeAsync(runtime);
        }

        Assert.True(condition());
    }

    private static Task WaitForProfileChangeAsync(CatalogFileProviderRuntime runtime)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            runtime.ProfilesChanged -= handler;
            completion.TrySetResult();
        };
        runtime.ProfilesChanged += handler;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<string> ReadTextAsync(
        IFilePanelSession session,
        FilePanelLocation root)
    {
        var result = await session.PreviewAsync(
            new FilePanelPreviewRequest(
                root.Child(new FilePanelPathSegment("marker.txt")),
                64),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return Encoding.UTF8.GetString(result.Value!.Content.Span);
    }

    private static async Task<FilePanelTransferSnapshot> WaitForTransferAsync(
        IFilePanelSession session,
        FilePanelTransferId id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = session.Transfers.Single(item => item.Id == id);
            if (!snapshot.CanCancel)
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The file transfer did not reach a terminal state.");
    }

    private static DefinitionCatalog CreateCatalog() => new(
        new MemoryDefinitionRepository<ConnectionProfile>(),
        new MemoryDefinitionRepository<LayoutDefinition>(),
        new MemoryDefinitionRepository<ScreenDefinition>(),
        new MemoryDefinitionRepository<WorkspaceDefinition>(),
        new MemoryDefinitionRepository<ThemePreference>(),
        new MemoryDefinitionRepository<TerminalProfile>(),
        new MemoryDefinitionRepository<KeymapProfile>(),
        new MemoryDefinitionRepository<FileProviderProfile>(),
        new MemoryDefinitionRepository<AiProviderProfile>(),
        new MemoryDefinitionRepository<McpServerProfile>(),
        new MemoryDefinitionRepository<QuickTerminalSettings>());
}

internal sealed class RecordingConnectionSecurityRuntime(ConnectionProfile expectedProfile) :
    IConnectionSecurityRuntime
{
    private readonly SshHostKeyReview _review = new(
        new SshHostKeyReviewId("files-review"),
        expectedProfile.Id,
        "sftp.example.test:22",
        SshHostKeyDisposition.Unknown,
        new SshHostKeyIdentity("ssh-ed25519", $"SHA256:{new string('C', 43)}"),
        null,
        DateTimeOffset.UtcNow.AddMinutes(5));

    public ConnectionProfile? InspectedProfile { get; private set; }

    public SshHostKeyTrustRequest? TrustRequest { get; private set; }

    public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> PrepareSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        InspectSshHostKeyAsync(profile, progress, cancellationToken);

    public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InspectedProfile = profile;
        return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(_review));
    }

    public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
        SshHostKeyTrustRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TrustRequest = request;
        return ValueTask.FromResult(ConnectionRuntimeResult<SshHostKeyReview>.Succeed(
            new SshHostKeyReview(
                SshHostKeyReviewId.New(),
                expectedProfile.Id,
                _review.Endpoint,
                SshHostKeyDisposition.Trusted,
                _review.Presented,
                _review.Presented,
                DateTimeOffset.UtcNow.AddMinutes(5))));
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionDiagnosticsReport>> DiagnoseAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class TrackingDisposable : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal sealed class MemoryDefinitionRepository<TDefinition> : IDefinitionRepository<TDefinition>
    where TDefinition : IDurableDefinition
{
    private readonly Dictionary<DefinitionKey, StoredDefinition<TDefinition>> _items = [];

    public ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> GetAsync(
        DefinitionKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryGetValue(key, out var item)
            ? DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(item)
            : DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(
                new DefinitionStoreError(DefinitionStoreErrorCode.NotFound, "Not found.")));
    }

    public ValueTask<DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StoredDefinition<TDefinition>> items = [.. _items.Values];
        return ValueTask.FromResult(
            DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>.Success(items));
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveAsync(
        TDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _items.TryGetValue(definition.Key, out var existing);
        if ((existing is null && expectedRevision is not null)
            || (existing is not null && expectedRevision != existing.Revision))
        {
            return ValueTask.FromResult(
                DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.RevisionConflict,
                        "Revision conflict.",
                        existing?.Revision)));
        }

        var now = DateTimeOffset.UtcNow;
        var stored = new StoredDefinition<TDefinition>(
            definition,
            (existing?.Revision ?? 0) + 1,
            existing?.CreatedAt ?? now,
            now);
        _items[definition.Key] = stored;
        return ValueTask.FromResult(
            DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(stored));
    }

    public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(key, out var existing))
        {
            return ValueTask.FromResult(DefinitionStoreResult<Unit>.Failure(
                new DefinitionStoreError(DefinitionStoreErrorCode.NotFound, "Not found.")));
        }

        if (existing.Revision != expectedRevision)
        {
            return ValueTask.FromResult(DefinitionStoreResult<Unit>.Failure(
                new DefinitionStoreError(
                    DefinitionStoreErrorCode.RevisionConflict,
                    "Revision conflict.",
                    existing.Revision)));
        }

        _items.Remove(key);
        return ValueTask.FromResult(DefinitionStoreResult<Unit>.Success(Unit.Value));
    }
}
