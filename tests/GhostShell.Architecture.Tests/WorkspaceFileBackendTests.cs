using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Renci.SshNet;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceFileBackendTests
{
    [Fact]
    public async Task RealChildPreservesAllOperationsStreamingVersionsAndPagination()
    {
        await using var fixture = new FileBackendFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var provider = await fixture.OpenAsync(token);
        var root = fixture.Location();
        var folder = fixture.Location("folder");
        var file = fixture.Location("folder", "binary");
        _ = Success(await provider.CreateDirectoryAsync(new(folder, new FileMutationPrecondition.MustNotExist()), token));
        var bytes = new byte[192 * 1024 + 7];
        Random.Shared.NextBytes(bytes);
        using var source = new MemoryStream(bytes, writable: false);
        var write = Success(await provider.WriteAsync(new(file, bytes.Length, 128 * 1024,
            new FileMutationPrecondition.MustNotExist()), source, null, token));
        Assert.Equal(bytes.Length, write.BytesWritten);
        var stat = Success(await provider.StatAsync(new(file), token));
        Assert.Equal(bytes.Length, stat.Size);
        Assert.False(string.IsNullOrEmpty(stat.Version.Value));
        using var read = new MemoryStream();
        var receipt = Success(await provider.ReadAsync(new(file, 17, bytes.Length - 29, 128 * 1024), read, null, token));
        Assert.Equal(bytes.Length - 29, receipt.BytesRead);
        Assert.Equal(bytes.AsSpan(17, bytes.Length - 29).ToArray(), read.ToArray());
        var access = Success(await provider.GetAccessControlAsync(new(file), token));
        Assert.NotNull(access.Mode);
        var changed = Success(await provider.SetAccessControlAsync(new(file, mode: new(0x180)), token));
        Assert.Equal(0x180, changed.Mode?.Permissions);
        var copy = fixture.Location("copy");
        _ = Success(await provider.TransferAsync(new(file, copy, FileTransferKind.Copy, 128 * 1024,
            new FileMutationPrecondition.MustNotExist()), null, token));
        var renamed = fixture.Location("renamed");
        _ = Success(await provider.RenameAsync(new(copy, renamed, new FileMutationPrecondition.MustNotExist()), token));
        var first = Success(await provider.ListAsync(new(root, 1), token));
        Assert.Single(first.Items);
        Assert.NotNull(first.ContinuationToken);
        var second = Success(await provider.ListAsync(new(root, 1, first.ContinuationToken), token));
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Location, second.Items[0].Location);
        Assert.Equal(1, fixture.Launches);
        _ = Success(await provider.DeleteAsync(new(renamed, false, new FileMutationPrecondition.MustExist()), token));
        _ = Success(await provider.DeleteAsync(new(folder, true, new FileMutationPrecondition.MustExist()), token));
        Assert.Empty(Success(await provider.ListAsync(new(root, 10), token)).Items);
    }

    [Fact]
    public async Task RevocationClosesIdleChildAndNextOperationUsesNewRouteWithoutReusingCursor()
    {
        await using var fixture = new FileBackendFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var provider = await fixture.OpenAsync(token);
        _ = Success(await provider.CreateDirectoryAsync(new(fixture.Location("one"), new FileMutationPrecondition.Any()), token));
        _ = Success(await provider.CreateDirectoryAsync(new(fixture.Location("two"), new FileMutationPrecondition.Any()), token));
        var oldPage = Success(await provider.ListAsync(new(fixture.Location(), 1), token));
        Assert.NotNull(oldPage.ContinuationToken);
        fixture.Route.Cancel();
        await fixture.LastCleanup.WaitAsync(token);
        fixture.ReplaceRoute();
        var stale = await provider.ListAsync(new(fixture.Location(), 1, oldPage.ContinuationToken), token);
        Assert.Equal(FileProviderErrorCode.InvalidLocation, stale.Error?.Code);
        Assert.Equal(1, fixture.Launches);
        _ = Success(await provider.ListAsync(new(fixture.Location(), 1), token));
        Assert.Equal(2, fixture.Launches);
        stale = await provider.ListAsync(new(fixture.Location(), 1, oldPage.ContinuationToken), token);
        Assert.Equal(FileProviderErrorCode.InvalidLocation, stale.Error?.Code);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("object")]
    [InlineData("root")]
    public async Task WirePreservesOpaqueLocationsAndConditionalMutations(string kind)
    {
        var location = kind switch
        {
            "object" => FileLocation.ForObjectKey(new("provider"), new("bucket"), new("a//../%20/space x"), new("version")),
            "root" => FileLocation.ForContainerRoot(new("provider"), new("bucket"), new("version")),
            _ => new FileLocation(new("provider"), null, FilePath.FromSegments([new("space x"), new("%2f")]), new("version")),
        };
        var profile = new FileProviderProfile(new("provider"), 1, "fixture", new FileProviderConfiguration.Local("/fixture"));
        FileMutationPrecondition[] conditions = [new FileMutationPrecondition.Any(), new FileMutationPrecondition.MustExist(),
            new FileMutationPrecondition.MustNotExist(), new FileMutationPrecondition.VersionMatches(new("opaque-version"))];
        foreach (var condition in conditions)
        {
            var request = new FileWorkspaceRequest(profile, null, FileWorkspaceOperation.Write,
                Write: new(location, 42, 65536, condition));
            using var stream = new MemoryStream();
            await BackendJsonFrames.WriteAsync(stream, request, FileWorkspaceJsonContext.Default.FileWorkspaceRequest, CancellationToken.None);
            stream.Position = 0;
            var restored = await BackendJsonFrames.ReadAsync(stream, FileWorkspaceJsonContext.Default.FileWorkspaceRequest, CancellationToken.None);
            Assert.Equal(profile, restored.Profile);
            Assert.Equal(location, restored.Write?.Location);
            Assert.Equal(condition, restored.Write?.Precondition);
        }
    }

    [Fact]
    public async Task FixedEnvelopeRejectsUnknownFieldsAndContradictoryOperations()
    {
        using var stream = new MemoryStream();
        await DatabaseOperationProtocol.WriteFrameAsync(stream, "{\"Kind\":0,\"ExtraAuthority\":\"secret\"}"u8.ToArray(), CancellationToken.None);
        stream.Position = 0;
        await Assert.ThrowsAsync<JsonException>(() => BackendJsonFrames.ReadAsync(stream,
            FileWorkspaceJsonContext.Default.FileWorkspaceMessage, CancellationToken.None));
        var profile = new FileProviderProfile(new("provider"), 1, "fixture", new FileProviderConfiguration.Local("/fixture"));
        var location = new FileLocation(new("provider"), null, FilePath.Root);
        Assert.Throws<InvalidDataException>(() => FileWorkspaceChild.Validate(new(profile, null,
            FileWorkspaceOperation.Stat, Stat: new(location), Delete: new(location, true, new FileMutationPrecondition.Any()))));
    }

    [Fact]
    public async Task CredentialCallbackIsBoundToExactProfileReferenceScopeAndPurpose()
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("selected-secret");
        var profile = new FileProviderProfile(new("provider"), 1, "fixture",
            new FileProviderConfiguration.S3("bucket", credentialsSecret: reference));
        using var credentials = new FileWorkspaceHostCredentials(profile, null, vault, new RejectTrust(), null);
        var allowed = new ResolveSecretRequest(reference, new(SecretScopeKind.FileProvider, "provider"),
            new(SecretUseKind.FileProviderAuthentication, "provider"));
        Assert.True(credentials.AllowsSecret(allowed));
        Assert.False(credentials.AllowsSecret(allowed with { Reference = new("other-secret") }));
        Assert.False(credentials.AllowsSecret(allowed with { Scope = new(SecretScopeKind.FileProvider, "other") }));
        Assert.False(credentials.AllowsSecret(allowed with { Purpose = new(SecretUseKind.FileProviderAuthentication, "other") }));
        var rejected = await credentials.HandleAsync(new(FileWorkspaceMessageKind.ResolveSecret,
            Secret: allowed, Count: 1), CancellationToken.None);
        Assert.Equal(SecretVaultErrorCode.AccessDenied, rejected.SecretFailure);
        await Assert.ThrowsAsync<InvalidDataException>(() => credentials.HandleAsync(new(FileWorkspaceMessageKind.Identities), CancellationToken.None));
    }

    [Theory]
    [InlineData("other-user", "ssh-connection", "ssh-ed25519", false)]
    [InlineData("user", "other-service", "ssh-ed25519", false)]
    [InlineData("user", "ssh-connection", "other-algorithm", false)]
    [InlineData("user", "ssh-connection", "ssh-ed25519", true)]
    public void SigningOnlyAcceptsSelectedSshAuthenticationTuple(string username, string service, string algorithm, bool accepted)
    {
        using var buffer = new MemoryStream();
        WriteSshString(buffer, new byte[32]);
        buffer.WriteByte(50);
        WriteSshString(buffer, Encoding.UTF8.GetBytes(username));
        WriteSshString(buffer, Encoding.UTF8.GetBytes(service));
        WriteSshString(buffer, "publickey"u8.ToArray());
        buffer.WriteByte(1);
        WriteSshString(buffer, Encoding.UTF8.GetBytes(algorithm));
        WriteSshString(buffer, [1, 2, 3]);
        var bytes = buffer.ToArray();
        if (accepted) { FileWorkspaceHostCredentials.ValidateAuthenticationPayload(bytes, "user", "ssh-ed25519", [1, 2, 3]); }
        else { Assert.Throws<InvalidDataException>(() => FileWorkspaceHostCredentials.ValidateAuthenticationPayload(bytes, "user", "ssh-ed25519", [1, 2, 3])); }
        Assert.Throws<InvalidDataException>(() => FileWorkspaceHostCredentials.ValidateAuthenticationPayload([.. bytes, 0], "user", "ssh-ed25519", [1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => FileWorkspaceHostCredentials.ValidateAuthenticationPayload(bytes, "user", "ssh-ed25519", [9]));
    }

    [Fact]
    public async Task PrivateKeysAreReplacedByDelegatedPublicKeyAuthenticationInGuestRequest()
    {
        using var vault = new InMemorySecretVault();
        var connection = new ConnectionProfile(new("ssh-fixture"), 1, "fixture", new ConnectionEndpoint.Ssh("example.invalid", username: "fixture"),
            new ConnectionAuthentication.PrivateKey(new("key-secret"), new("passphrase-secret")), ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        var profile = new FileProviderProfile(new("sftp-fixture"), 1, "fixture", new FileProviderConfiguration.Sftp(connection.Id));
        using var credentials = new FileWorkspaceHostCredentials(profile, connection, vault, new RejectTrust(), null);
        Assert.IsType<ConnectionAuthentication.SshAgent>(credentials.GuestConnection?.Authentication);
        var request = new ResolveSecretRequest(new("key-secret"), new(SecretScopeKind.Connection, connection.Id.Value),
            new(SecretUseKind.ConnectionAuthentication, connection.Id.Value));
        Assert.False(credentials.AllowsSecret(request));
        Assert.False(credentials.AllowsSecret(request with { Reference = new("passphrase-secret") }));
        var rejected = await credentials.HandleAsync(new(FileWorkspaceMessageKind.ResolveSecret, Secret: request), CancellationToken.None);
        Assert.Equal(SecretVaultErrorCode.AccessDenied, rejected.SecretFailure);
        using var stream = new MemoryStream();
        var location = new FileLocation(new("sftp-fixture"), null, FilePath.Root);
        await BackendJsonFrames.WriteAsync(stream, new FileWorkspaceRequest(profile, credentials.GuestConnection,
            FileWorkspaceOperation.Stat, Stat: new(location)), FileWorkspaceJsonContext.Default.FileWorkspaceRequest, CancellationToken.None);
        Assert.DoesNotContain("key-secret", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
        stream.Position = 0;
        var restored = await BackendJsonFrames.ReadAsync(stream, FileWorkspaceJsonContext.Default.FileWorkspaceRequest, CancellationToken.None);
        Assert.Equal(connection.Id, restored.Connection?.Id);
        Assert.IsType<ConnectionAuthentication.SshAgent>(restored.Connection?.Authentication);
    }

    [Fact]
    public async Task SerializedRedisBuffersAreClearedAfterBothSuccessfulAndFailedWrites()
    {
        var bytes = await RedisWorkspaceProtocol.SerializeAsync(new RedisWorkspaceRequest(1, RedisWorkspaceOperation.Open, Text: "fixture-secret"),
            RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, CancellationToken.None);
        using var stream = new MemoryStream();
        await RedisWorkspaceProtocol.WriteAsync(stream, bytes, CancellationToken.None);
        Assert.All(bytes, value => Assert.Equal(0, value));
        stream.Position = 0;
        var restored = await RedisWorkspaceProtocol.ReadAsync(stream, RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, CancellationToken.None);
        Assert.Equal("fixture-secret", restored.Text);
        stream.Dispose();
        var failed = new byte[] { 1, 2, 3 };
        await Assert.ThrowsAsync<ObjectDisposedException>(() => RedisWorkspaceProtocol.WriteAsync(stream, failed, CancellationToken.None));
        Assert.All(failed, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task VaultPrivateKeySignsOnlyValidatedAuthenticationAndProducesAValidSignature()
    {
        using var key = RSA.Create(2048);
        var bytes = Encoding.UTF8.GetBytes(key.ExportRSAPrivateKeyPem());
        using var material = SecretMaterial.CopyFrom(bytes);
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("fixture-private-key");
        var connection = new ConnectionProfile(new("ssh-fixture"), 1, "fixture", new ConnectionEndpoint.Ssh("example.invalid", username: "fixture"),
            new ConnectionAuthentication.PrivateKey(reference), ConnectionStartup.Default, ConnectionKeepAlive.Disabled, SshHostKeyPolicy.Strict);
        _ = await vault.CreateAsync(new(reference, "fixture", SecretKind.PrivateKey,
            new(SecretScopeKind.Connection, connection.Id.Value), new(SecretUseKind.ConnectionAuthentication, connection.Id.Value)), material, CancellationToken.None);
        var profile = new FileProviderProfile(new("sftp-fixture"), 1, "fixture", new FileProviderConfiguration.Sftp(connection.Id));
        using var credentials = new FileWorkspaceHostCredentials(profile, connection, vault, new TrustFixtureKey(), null);
        var identities = await credentials.HandleAsync(new(FileWorkspaceMessageKind.Identities), CancellationToken.None);
        Assert.NotEmpty(identities.Identities!);
        var identity = identities.Identities![0];
        using var payload = new MemoryStream();
        WriteSshString(payload, new byte[32]); payload.WriteByte(50);
        WriteSshString(payload, "fixture"u8.ToArray()); WriteSshString(payload, "ssh-connection"u8.ToArray());
        WriteSshString(payload, "publickey"u8.ToArray()); payload.WriteByte(1);
        WriteSshString(payload, Encoding.UTF8.GetBytes(identity.Algorithm)); WriteSshString(payload, identity.PublicKey);
        var request = new FileWorkspaceMessage(FileWorkspaceMessageKind.Sign, Identity: identity.Id, Bytes: payload.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => credentials.HandleAsync(request, CancellationToken.None));
        _ = await credentials.HandleAsync(new(FileWorkspaceMessageKind.VerifyHostKey,
            HostKey: new(identity.Algorithm, Convert.ToBase64String(identity.PublicKey))), CancellationToken.None);
        var signed = await credentials.HandleAsync(request, CancellationToken.None);
        using var verifyingKey = new PrivateKeyFile(new MemoryStream(bytes, writable: false));
        Assert.True(verifyingKey.HostKeyAlgorithms.Single(algorithm => string.Equals(algorithm.Name, identity.Algorithm, StringComparison.Ordinal))
            .VerifySignature(request.Bytes!, signed.Bytes!));
        CryptographicOperations.ZeroMemory(bytes);
    }

    [Fact]
    public async Task CredentialReplyCannotCombineFailureWithSecretOrUnrelatedAuthority()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await BackendJsonFrames.WriteAsync(input, new FileWorkspaceReply(FileWorkspaceMessageKind.ResolveSecret,
            Bytes: [1, 2, 3], SecretFailure: SecretVaultErrorCode.AccessDenied), FileWorkspaceJsonContext.Default.FileWorkspaceReply, CancellationToken.None);
        input.Position = 0;
        using var callbacks = new FileWorkspaceCallbacks(input, output);
        Assert.Throws<InvalidDataException>(() => callbacks.Exchange(new(FileWorkspaceMessageKind.ResolveSecret)));
    }

    private static void WriteSshString(Stream stream, byte[] bytes)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        stream.Write(header); stream.Write(bytes);
    }

    private static T Success<T>(FileProviderResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return Assert.IsType<T>(result.Value);
    }

    private sealed class RejectTrust : ISshHostKeyTrustStore
    {
        public SshHostKeyVerification Verify(ConnectionId connectionId, SshHostKeyPolicy policy, SshHostKeyCandidate presented) =>
            throw new InvalidOperationException("Local file operations must not request SSH trust.");
    }

    private sealed class TrustFixtureKey : ISshHostKeyTrustStore
    {
        public SshHostKeyVerification Verify(ConnectionId connectionId, SshHostKeyPolicy policy, SshHostKeyCandidate presented) => SshHostKeyVerification.Trusted;
    }

    private sealed class FileBackendFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"ghostshell-file-backend-test-{Guid.NewGuid():N}");
        private readonly InMemorySecretVault _vault = new();
        private OwnedFileProviderRegistration? _owned;
        internal CancellationTokenSource Route { get; private set; } = new();
        internal Task LastCleanup { get; private set; } = Task.CompletedTask;
        internal int Launches { get; private set; }

        internal async Task<IFileProvider> OpenAsync(CancellationToken token)
        {
            Directory.CreateDirectory(_root);
            var profile = new FileProviderProfile(new("fixture"), 1, "fixture", new FileProviderConfiguration.Local(_root));
            var factory = new WorkspaceFileProviderFactory(_vault, new RejectTrust(), null, LaunchAsync, localInWorkspace: true);
            _owned = await factory.CreateAsync(profile, new Dictionary<ConnectionId, ConnectionProfile>(), token);
            Assert.False(_owned.Registration.Provider is ILocalFilePathSource);
            return _owned.Registration.Provider;
        }

        internal FileLocation Location(params string[] segments) => new(new("fixture"), new FileAuthority("local"),
            FilePath.FromSegments(segments.Select(value => new FilePathSegment(value))));

        internal void ReplaceRoute() { Route.Dispose(); Route = new(); }

        private Task<DatabaseWorkspaceOperationLaunch> LaunchAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var operation = Guid.NewGuid().ToString("N");
            DatabaseWorkspaceScratch.Prepare(operation);
            var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LastCleanup = cleaned.Task;
            Launches++;
            return Task.FromResult(new DatabaseWorkspaceOperationLaunch(BackendStart(operation), async () =>
            {
                try { await DatabaseWorkspaceScratch.CleanupAsync(operation, CancellationToken.None); }
                finally { cleaned.TrySetResult(); }
            }, Route.Token));
        }

        public async ValueTask DisposeAsync()
        {
            _owned?.Dispose();
            Route.Cancel();
            await LastCleanup.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            Route.Dispose(); _vault.Dispose();
            if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
        }

        private static ProcessStartInfo BackendStart(string operation)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
            var backend = typeof(WorkspaceFileBackendTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => string.Equals(attribute.Key, "DatabaseBackendPath", StringComparison.Ordinal)).Value;
            var start = new ProcessStartInfo(Path.Combine(root!.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            start.ArgumentList.Add(backend!); start.ArgumentList.Add("files"); start.ArgumentList.Add(operation);
            return start;
        }
    }
}
