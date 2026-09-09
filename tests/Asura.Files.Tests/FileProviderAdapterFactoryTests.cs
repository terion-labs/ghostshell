using Asura.Application;
using Asura.Core;
using RuntimeProfileId = Asura.Core.FileProviderProfileId;

namespace Asura.Files.Tests;

public sealed class FileProviderAdapterFactoryTests
{
    [Fact]
    public void S3SdkConfigurationDisablesImplicitRequestReplay()
    {
        var configuration = FileProviderAdapterFactory
            .CreateS3ClientConfiguration(
                new FileProviderConfiguration.S3(
                    "asura-test-bucket",
                    serviceUri: new Uri("https://s3.example.test/")));

        Assert.Equal(0, configuration.MaxErrorRetry);
        Assert.Equal(0, configuration.MaxStaleConnectionRetries);
    }

    [Fact]
    public async Task EveryDurableProviderKindMaterializesBehindTheSameRegistrationSeam()
    {
        var localRoot = Directory.CreateTempSubdirectory("asura-provider-factory-");
        using var vault = new RejectingSecretVault();
        var factory = new FileProviderAdapterFactory(vault, new InMemorySftpKnownHostStore());
        var ssh = SshConnection();
        var connections = new Dictionary<ConnectionId, ConnectionProfile>
        {
            [ssh.Id] = ssh,
        };
        FileProviderProfile[] profiles =
        [
            Profile("local", new FileProviderConfiguration.Local(localRoot.FullName)),
            Profile("s3", new FileProviderConfiguration.S3("asura-test-bucket")),
            Profile("sftp", new FileProviderConfiguration.Sftp(ssh.Id)),
            Profile("ftp", new FileProviderConfiguration.Ftp(
                "ftp.example.test",
                21,
                null,
                null,
                FtpSecurityMode.ExplicitTls,
                FtpConnectionMode.AutoPassive)),
            Profile("smb", new FileProviderConfiguration.Smb(
                "smb.example.test",
                "files",
                SmbCredentialMode.Guest)),
            Profile("webdav", new FileProviderConfiguration.WebDav(
                new Uri("https://dav.example.test/files/"))),
        ];

        var registrations = new List<OwnedFileProviderRegistration>();
        try
        {
            foreach (var profile in profiles)
            {
                registrations.Add(await factory.CreateAsync(
                    profile,
                    connections,
                    CancellationToken.None));
            }

            Assert.Collection(
                registrations,
                item =>
                {
                    Assert.IsAssignableFrom<LocalFileProvider>(
                        item.Registration.Provider);
                    Assert.Equal(
                        OperatingSystem.IsWindows()
                            ? FilePanelCapability.None
                            : FilePanelCapability.GovernedCreateDirectory
                                | FilePanelCapability.GovernedDelete
                                | FilePanelCapability.GovernedRename,
                        item.Registration.GovernedMutationCapabilities);
                },
                item =>
                {
                    Assert.IsType<DeferredFileProvider>(
                        item.Registration.Provider);
                    Assert.Equal(
                        FilePanelCapability.None,
                        item.Registration.GovernedMutationCapabilities);
                },
                item =>
                {
                    Assert.IsType<SftpFileProvider>(
                        item.Registration.Provider);
                    Assert.Equal(
                        FilePanelCapability.None,
                        item.Registration.GovernedMutationCapabilities);
                },
                item =>
                {
                    Assert.IsType<FtpFileProvider>(
                        item.Registration.Provider);
                    Assert.Equal(
                        FilePanelCapability.None,
                        item.Registration.GovernedMutationCapabilities);
                },
                item =>
                {
                    Assert.IsType<SmbFileProvider>(
                        item.Registration.Provider);
                    Assert.Equal(
                        FilePanelCapability.None,
                        item.Registration.GovernedMutationCapabilities);
                },
                item =>
                {
                    Assert.IsType<DeferredFileProvider>(
                        item.Registration.Provider);
                    Assert.Equal(
                        FilePanelCapability.GovernedCreateDirectory
                        | FilePanelCapability.GovernedCreateFile
                        | FilePanelCapability.GovernedReplaceFile
                        | FilePanelCapability.GovernedCopySource
                        | FilePanelCapability.GovernedCopy,
                        item.Registration.GovernedMutationCapabilities);
                });
            Assert.All(registrations, item => Assert.Equal(
                item.Registration.Provider.ProfileId,
                item.Registration.Root.ProviderProfileId));
            using var panelClient = new FilePanelClient(
                registrations.Select(item => item.Registration));
            var profileCapabilities = panelClient.Profiles.ToDictionary(
                item => item.Id,
                item => item.Capabilities,
                StringComparer.Ordinal);
            Assert.False(profileCapabilities["files.s3"].HasFlag(
                FilePanelCapability.GovernedDelete));
            Assert.False(profileCapabilities["files.s3"].HasFlag(
                FilePanelCapability.GovernedCreateDirectory));
            Assert.True(profileCapabilities["files.s3"].HasFlag(
                FilePanelCapability.Delete));
            Assert.True(profileCapabilities["files.webdav"].HasFlag(
                FilePanelCapability.GovernedCreateDirectory));
            Assert.False(profileCapabilities["files.webdav"].HasFlag(
                FilePanelCapability.GovernedDelete));
            Assert.True(profileCapabilities["files.webdav"].HasFlag(
                FilePanelCapability.GovernedCreateFile));
            Assert.True(profileCapabilities["files.webdav"].HasFlag(
                FilePanelCapability.GovernedReplaceFile));
            Assert.True(profileCapabilities["files.webdav"].HasFlag(
                FilePanelCapability.GovernedCopy));
            Assert.All(
                [
                    "files.local",
                    "files.sftp",
                    "files.ftp",
                    "files.smb",
                    "files.webdav",
                ],
                id =>
                {
                    Assert.True(profileCapabilities[id].HasFlag(
                        FilePanelCapability.CreateDirectory));
                    Assert.True(profileCapabilities[id].HasFlag(
                        FilePanelCapability.Delete));
                });
            Assert.All(
                ["files.sftp", "files.ftp", "files.smb"],
                id => Assert.Equal(
                    FilePanelCapability.None,
                    profileCapabilities[id]
                    & (FilePanelCapability.GovernedCreateDirectory
                        | FilePanelCapability.GovernedDelete)));
            Assert.Equal(
                OperatingSystem.IsWindows()
                    ? FilePanelCapability.None
                    : FilePanelCapability.GovernedCreateDirectory
                        | FilePanelCapability.GovernedDelete,
                profileCapabilities["files.local"]
                & (FilePanelCapability.GovernedCreateDirectory
                    | FilePanelCapability.GovernedDelete));
            Assert.Empty(vault.ResolveRequests);
        }
        finally
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }

            localRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task S3CredentialResolutionUsesOnlyTheOwningFileProviderScope()
    {
        var reference = new SecretRef("s3-credential");
        using var vault = new StaticSecretVault(
            reference,
            """
            {"accessKeyId":"test-access","secretAccessKey":"test-secret"}
            """u8.ToArray());
        var factory = new FileProviderAdapterFactory(vault, new InMemorySftpKnownHostStore());
        var profile = Profile(
            "scoped-s3",
            new FileProviderConfiguration.S3(
                "asura-test-bucket",
                credentialsSecret: reference));

        using var registration = await factory.CreateAsync(
            profile,
            new Dictionary<ConnectionId, ConnectionProfile>(),
            CancellationToken.None);
        Assert.Empty(vault.ResolveRequests);
        var deferred = Assert.IsType<DeferredFileProvider>(registration.Registration.Provider);
        Assert.IsType<S3FileProvider>(await deferred.MaterializeAsync(CancellationToken.None));

        var request = Assert.Single(vault.ResolveRequests);
        Assert.Equal(SecretScopeKind.FileProvider, request.Scope.Kind);
        Assert.Equal(profile.Id.Value, request.Scope.OwnerId);
        Assert.Equal(SecretUseKind.FileProviderAuthentication, request.Purpose.Kind);
        Assert.Equal(profile.Id.Value, request.Purpose.TargetId);
    }

    [Fact]
    public async Task DeferredCredentialFailureReturnsSanitizedProviderError()
    {
        using var vault = new RejectingSecretVault();
        var factory = new FileProviderAdapterFactory(vault, new InMemorySftpKnownHostStore());
        var profile = Profile(
            "missing-s3-credential",
            new FileProviderConfiguration.S3(
                "asura-test-bucket",
                credentialsSecret: new SecretRef("missing")));

        using var registration = await factory.CreateAsync(
            profile,
            new Dictionary<ConnectionId, ConnectionProfile>(),
            CancellationToken.None);
        var result = await registration.Registration.Provider.ListAsync(
            new FileListRequest(registration.Registration.Root, 1),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal("io_failure", result.Error.StableCode);
        Assert.DoesNotContain("missing", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        var retry = await registration.Registration.Provider.ListAsync(
            new FileListRequest(registration.Registration.Root, 1),
            CancellationToken.None);
        Assert.False(retry.IsSuccess);
        Assert.Equal(2, vault.ResolveRequests.Count);
    }

    private static FileProviderProfile Profile(
        string id,
        FileProviderConfiguration configuration) =>
        new(
            new RuntimeProfileId($"files.{id}"),
            FileProviderProfile.CurrentSchemaVersion,
            id,
            configuration);

    private static ConnectionProfile SshConnection() => new(
        new ConnectionId("connection.sftp"),
        ConnectionProfile.CurrentSchemaVersion,
        "SFTP",
        new ConnectionEndpoint.Ssh("sftp.example.test", username: "operator"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);
}

internal class RejectingSecretVault : ISecretVault
{
    public List<ResolveSecretRequest> ResolveRequests { get; } = [];

    public SecretVaultAvailability Availability { get; } = new(
        SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.MemoryOnly,
        SecretVaultCapabilities.All,
        "test",
        "test",
        "Test vault");

    public virtual ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        ResolveRequests.Add(request);
        return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
            SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public virtual void Dispose()
    {
    }
}

internal sealed class StaticSecretVault(SecretRef reference, byte[] value) : RejectingSecretVault
{
    public override ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        ResolveRequests.Add(request);
        return ValueTask.FromResult(request.Reference == reference
            ? SecretVaultResult<SecretMaterial>.Succeed(SecretMaterial.CopyFrom(value))
            : SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
    }

    public override void Dispose() => System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
}
