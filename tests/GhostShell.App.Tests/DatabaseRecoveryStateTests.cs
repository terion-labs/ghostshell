using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class DatabaseRecoveryStateTests
{
    [Fact]
    public async Task RestartRestoresWholeTargetWithoutPuttingCredentialsInToken()
    {
        using var vault = new TestVault();
        var state = new DatabaseRecoveryState(vault);
        var payload = new DatabaseRecoveryPayload("postgres", "Host=fixture.invalid;Password=private-fixture", "session-fixture", null, null);
        Assert.Null(state.Target);
        Assert.True(await state.SaveAsync(payload, CancellationToken.None));
        Assert.DoesNotContain("fixture", state.Target!, StringComparison.Ordinal);
        var restarted = new DatabaseRecoveryState(vault, DatabaseRecoveryToken.TryParse(state.Target));

        Assert.Equal(payload, await restarted.RestoreAsync(CancellationToken.None));
        Assert.Equal(1, vault.ResolveCount);
        Assert.Equal(state.Target, restarted.Target);
    }

    [Fact]
    public async Task ChangedOwnerAndWrongKindAreRejectedBeforeSecretResolution()
    {
        using var vault = new TestVault();
        var state = new DatabaseRecoveryState(vault);
        Assert.True(await state.SaveAsync(new("sqlite", "Data Source=fixture", null, null, null), CancellationToken.None));
        var token = DatabaseRecoveryToken.TryParse(state.Target)!;
        var wrongOwner = new DatabaseRecoveryState(vault, token with { OwnerId = Guid.NewGuid().ToString("N") });
        Assert.Null(await wrongOwner.RestoreAsync(CancellationToken.None));
        vault.WrongKind = true;
        Assert.Null(await new DatabaseRecoveryState(vault, token).RestoreAsync(CancellationToken.None));
        Assert.Equal(0, vault.ResolveCount);
        Assert.Throws<ArgumentException>(() => new DatabaseRecoveryState(vault, token with { OwnerId = "invalid" }));
    }

    [Fact]
    public async Task NoOpSavesReuseReferenceWhileLiveChangesLeaveManuallySavedTargetImmutable()
    {
        using var vault = new TestVault();
        var state = new DatabaseRecoveryState(vault);
        var first = new DatabaseRecoveryPayload("postgres", "Host=first.invalid", "first-password", null, null);
        var second = first with { ConnectionString = "Host=second.invalid", SessionPassword = "second-password" };
        Assert.True(await state.SaveAsync(first, CancellationToken.None));
        var target = state.Target;
        Assert.True(await state.SaveAsync(first, CancellationToken.None));
        Assert.Equal(target, state.Target);
        Assert.True(await state.SaveAsync(second, CancellationToken.None));
        Assert.NotEqual(target, state.Target, StringComparer.Ordinal);
        Assert.Equal(2, vault.Creates);
        Assert.Equal(0, vault.Replaces);
        Assert.Equal(first, await new DatabaseRecoveryState(vault, DatabaseRecoveryToken.TryParse(target)).RestoreAsync(CancellationToken.None));
        var latest = state.Target;
        vault.FailWrites = true;
        Assert.False(await state.SaveAsync(first, CancellationToken.None));
        Assert.Equal(latest, state.Target);
        Assert.Equal(second, await new DatabaseRecoveryState(vault, DatabaseRecoveryToken.TryParse(latest)).RestoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PendingWriteExposesNoRawTargetAndSerialWritesKeepNewestPayload()
    {
        using var vault = new TestVault();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vault.BeforeWrite = () => blocked.Task;
        var state = new DatabaseRecoveryState(vault);
        var first = new DatabaseRecoveryPayload("sqlite", "Data Source=first", null, null, null);
        var pending = state.SaveAsync(first, CancellationToken.None);
        Assert.Null(state.Target);
        var latest = state.SaveAsync(first with { ConnectionString = "Data Source=latest" }, CancellationToken.None);
        blocked.SetResult();
        Assert.True(await pending);
        Assert.True(await latest);
        Assert.Equal("Data Source=latest", (await state.RestoreAsync(CancellationToken.None))!.ConnectionString);
        Assert.Equal(2, vault.Creates);
    }

    [Fact]
    public async Task CancellationBeforeVaultCommitLeavesNoPublishedReference()
    {
        using var vault = new TestVault();
        using var cancellation = new CancellationTokenSource();
        vault.BeforeWrite = async () => await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        var state = new DatabaseRecoveryState(vault);
        var pending = state.SaveAsync(new("sqlite", "Data Source=private-fixture", null, null, null), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(state.Target);
        Assert.Equal(0, vault.Creates);
    }

    [Fact]
    public async Task TwoPanelsOpeningOneSavedTokenDoNotOverwriteEachOthersTarget()
    {
        using var vault = new TestVault();
        var first = new DatabaseRecoveryState(vault);
        var original = new DatabaseRecoveryPayload("postgres", "Host=original.invalid", "original-password", null, null);
        Assert.True(await first.SaveAsync(original, CancellationToken.None));
        var second = new DatabaseRecoveryState(vault, DatabaseRecoveryToken.TryParse(first.Target));
        Assert.Equal(original, await second.RestoreAsync(CancellationToken.None));
        var savedToken = first.Target;
        Assert.True(await second.SaveAsync(original with { ConnectionString = "Host=changed.invalid" }, CancellationToken.None));
        Assert.Equal(savedToken, first.Target);
        Assert.NotEqual(first.Target, second.Target, StringComparer.Ordinal);
        Assert.Equal(original, await first.RestoreAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledSuccessfulVaultWriteCleansOnlyUnpublishedNewVersion()
    {
        using var vault = new TestVault();
        var state = new DatabaseRecoveryState(vault);
        var original = new DatabaseRecoveryPayload("sqlite", "Data Source=original", null, null, null);
        Assert.True(await state.SaveAsync(original, CancellationToken.None));
        var saved = state.Target;
        using var cancellation = new CancellationTokenSource();
        vault.AfterCreate = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.SaveAsync(
            original with { ConnectionString = "Data Source=unpublished" }, cancellation.Token));
        Assert.Equal(saved, state.Target);
        Assert.Equal(1, vault.Deletes);
        Assert.Equal(original, await state.RestoreAsync(CancellationToken.None));
    }

    internal sealed class TestVault : ISecretVault
    {
        private readonly Dictionary<SecretRef, (SecretMetadata Metadata, SecretMaterial Material)> _entries = [];
        public int Creates { get; private set; }
        public int Replaces { get; private set; }
        public int ResolveCount { get; private set; }
        public int Deletes { get; private set; }
        public bool FailWrites { get; set; }
        public bool WrongKind { get; set; }
        public Func<Task>? BeforeWrite { get; set; }
        public Func<CancellationToken, Task>? BeforeResolve { get; set; }
        public Action? AfterCreate { get; set; }
        public SecretVaultAvailability Availability { get; } = new(SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.OsProtectedPersistent, SecretVaultCapabilities.All, "test", "test", "test");
        public async ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(CreateSecretRequest request, SecretMaterial material, CancellationToken cancellationToken)
        {
            if (BeforeWrite is not null) { await BeforeWrite(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites) { return Failed(); }
            Creates++;
            var metadata = new SecretMetadata(request.Reference, request.Label, request.Kind, request.Scope,
                SecretVaultPersistenceKind.OsProtectedPersistent, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            _entries.Add(request.Reference, (metadata, material.Clone()));
            AfterCreate?.Invoke();
            return SecretVaultResult<SecretMetadata>.Succeed(metadata);
        }
        public async ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(ReplaceSecretRequest request, SecretMaterial material, CancellationToken cancellationToken)
        {
            if (BeforeWrite is not null) { await BeforeWrite(); }
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrites) { return Failed(); }
            Replaces++;
            var entry = _entries[request.Reference];
            Assert.Equal(request.Scope, entry.Metadata.Scope);
            entry.Material.Dispose();
            _entries[request.Reference] = (entry.Metadata, material.Clone());
            return SecretVaultResult<SecretMetadata>.Succeed(entry.Metadata);
        }
        public async ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(ResolveSecretRequest request, CancellationToken cancellationToken)
        {
            ResolveCount++;
            if (BeforeResolve is not null) { await BeforeResolve(cancellationToken); }
            var entry = _entries[request.Reference];
            Assert.Equal(request.Scope, entry.Metadata.Scope);
            return SecretVaultResult<SecretMaterial>.Succeed(entry.Material.Clone());
        }
        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(GetSecretMetadataRequest request, CancellationToken cancellationToken)
        {
            var result = _entries.TryGetValue(request.Reference, out var entry) && entry.Metadata.Scope == request.Scope
                ? SecretVaultResult<SecretMetadata>.Succeed(WrongKind ? entry.Metadata with { Kind = SecretKind.Password } : entry.Metadata)
                : SecretVaultResult<SecretMetadata>.Fail(SecretVaultError.Create(SecretVaultErrorCode.NotFound));
            return ValueTask.FromResult(result);
        }
        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(RelabelSecretRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(DeleteSecretRequest request, CancellationToken cancellationToken)
        {
            if (!_entries.Remove(request.Reference, out var entry))
            {
                return ValueTask.FromResult(SecretVaultResult<Unit>.Fail(SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
            }
            Assert.Equal(request.Scope, entry.Metadata.Scope);
            entry.Material.Dispose();
            Deletes++;
            return ValueTask.FromResult(SecretVaultResult<Unit>.Succeed(Unit.Value));
        }
        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(ListSecretMetadataRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose()
        {
            foreach (var entry in _entries.Values) { entry.Material.Dispose(); }
            _entries.Clear();
        }
        private static SecretVaultResult<SecretMetadata> Failed() => SecretVaultResult<SecretMetadata>.Fail(SecretVaultError.Create(SecretVaultErrorCode.Unavailable));
    }
}
