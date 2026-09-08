using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files.Tests;

public sealed class SmbFileProviderTests
{
    [Fact]
    public void ProviderPreservesShareIdentityAndDoesNotClaimUnavailableCapabilities()
    {
        var options = RemoteProviderTestProfiles.SmbOptions();
        var provider = new SmbFileProvider(new FakeRemoteSessionFactory(), options);

        Assert.Same(options, provider.Options);
        Assert.Equal(options.Authority, provider.Authority);
        Assert.Equal(FileNameComparison.ProviderDefined, provider.Capabilities.NameComparison);
        Assert.True(provider.Capabilities.Supports(FileProviderCapability.StreamingWrite));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.AtomicReplace));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.ResumableTransfer));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.ServerSideCopy));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Symlinks));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Permissions));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.AccessControlLists));
        Assert.Contains(
            provider.Diagnostics,
            diagnostic => string.Equals(diagnostic.StableCode, "smb_transport_security_unverified", StringComparison.Ordinal));
    }

    [Fact]
    public void GuestAuthenticationProducesAVisibleDiagnostic()
    {
        var provider = new SmbFileProvider(
            new FakeRemoteSessionFactory(),
            RemoteProviderTestProfiles.SmbOptions(new SmbAuthentication.Guest()));

        Assert.Contains(
            provider.Diagnostics,
            diagnostic => string.Equals(diagnostic.StableCode, "smb_guest_authentication", StringComparison.Ordinal));
    }

    [Fact]
    public void DurableOptionsNeverFormatTheOpaqueCredentialReference()
    {
        var secret = new SecretRef("never-format-this-reference");
        var options = RemoteProviderTestProfiles.SmbOptions(
            new SmbAuthentication.Password("TEST", "fixture", secret));

        Assert.DoesNotContain(secret.Value, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Value, options.Authentication.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialResolutionIsScopedToTheFileProviderProfile()
    {
        var options = RemoteProviderTestProfiles.SmbOptions();
        var password = Assert.IsType<SmbAuthentication.Password>(options.Authentication);

        ResolveSecretRequest request = SmbLibrarySessionFactory.CreateCredentialRequest(
            options,
            password.PasswordSecret);

        Assert.Equal(password.PasswordSecret, request.Reference);
        Assert.Equal(SecretScopeKind.FileProvider, request.Scope.Kind);
        Assert.Equal(options.ProfileId.Value, request.Scope.OwnerId);
        Assert.Equal(SecretUseKind.FileProviderAuthentication, request.Purpose.Kind);
        Assert.Equal(options.ProfileId.Value, request.Purpose.TargetId);
    }

    [Fact]
    public async Task UnexpectedVaultFailuresAreSanitizedBeforeLeavingTheAdapter()
    {
        var options = RemoteProviderTestProfiles.SmbOptions();
        using var vault = new ThrowingSecretVault();
        var provider = new SmbFileProvider(vault, options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.StatAsync(new FileStatRequest(root), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.AuthenticationRequired, result.Error!.Code);
        Assert.DoesNotContain("vault-exposed-reference", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ((SmbAuthentication.Password)options.Authentication).PasswordSecret.Value,
            result.Error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreCancelledOpenDoesNotResolveOrConnect()
    {
        var options = RemoteProviderTestProfiles.SmbOptions();
        using var vault = new ThrowingSecretVault();
        var provider = new SmbFileProvider(vault, options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await provider.StatAsync(new FileStatRequest(root), cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.Cancelled, result.Error!.Code);
    }


    [Theory]
    [InlineData("/", "")]
    [InlineData("/folder/file.txt", "folder\\file.txt")]
    [InlineData("/folder/space name.txt", "folder\\space name.txt")]
    public void StructuredPathsTranslateWithoutInterpretingSegments(
        string remotePath,
        string expected) => Assert.Equal(expected, SmbLibrarySession.ToSmbPath(remotePath));

    [Theory]
    [InlineData("relative")]
    [InlineData("/folder\\escape")]
    [InlineData("/folder/../escape")]
    [InlineData("/folder/file.txt:alternate-stream")]
    [InlineData("/folder/wild*.txt")]
    [InlineData("/folder/trailing-dot.")]
    [InlineData("/folder/trailing-space ")]
    public void UnsafeSmbPathsAreRejected(string remotePath)
    {
        var error = Assert.Throws<RemoteFileSessionException>(
            () => SmbLibrarySession.ToSmbPath(remotePath));

        Assert.Equal(RemoteFileSessionErrorCode.InvalidName, error.Code);
    }

    [Theory]
    [InlineData(0xC0000034u, "NotFound", false)]
    [InlineData(0xC0000035u, "AlreadyExists", false)]
    [InlineData(0xC0000022u, "AccessDenied", false)]
    [InlineData(0xC0000103u, "NotDirectory", false)]
    [InlineData(0xC00000BAu, "IsDirectory", false)]
    [InlineData(0xC0000101u, "DirectoryNotEmpty", false)]
    [InlineData(0xC00000BBu, "Unsupported", false)]
    [InlineData(0xC00000B5u, "Transient", true)]
    [InlineData(0xC0000043u, "IoFailure", true)]
    public void NtStatusValuesMapToSanitizedTypedErrors(
        uint status,
        string expected,
        bool retryable)
    {
        var error = SmbLibrarySession.MapStatus(status, "perform the requested operation");

        Assert.Equal(expected, error.Code.ToString());
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain(
            status.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataReconnectRetriesOnceButMutationIsNeverReplayed()
    {
        var metadataSessions = new FakeRemoteSessionFactory { FailOpenCount = 1 };
        var options = RemoteProviderTestProfiles.SmbOptions(
            reconnectPolicy: RemoteMetadataReconnectPolicy.RetryOnce);
        var metadataProvider = new SmbFileProvider(metadataSessions, options);
        var root = new FileLocation(metadataProvider.ProfileId, metadataProvider.Authority, FilePath.Root);

        var stat = await metadataProvider.StatAsync(new FileStatRequest(root), CancellationToken.None);

        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.Equal(2, metadataSessions.OpenCount);

        var mutationSessions = new FakeRemoteSessionFactory { FailOpenCount = 1 };
        var mutationProvider = new SmbFileProvider(mutationSessions, options);
        var destination = new FileLocation(
                mutationProvider.ProfileId,
                mutationProvider.Authority,
                FilePath.Root)
            .Child(new FilePathSegment("write.txt"));
        await using var source = new MemoryStream([1, 2, 3], writable: false);

        var write = await mutationProvider.WriteAsync(
            new FileWriteRequest(
                destination,
                contentLength: 3,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            CancellationToken.None);

        Assert.False(write.IsSuccess);
        Assert.Equal(FileProviderErrorCode.IoFailure, write.Error!.Code);
        Assert.Equal(1, mutationSessions.OpenCount);
    }

    [Fact]
    public void OptionsRejectUnstructuredServerAndShareValues()
    {
        Assert.Throws<ArgumentException>(() => new SmbFileProviderOptions(
            new FileProviderProfileId("smb"),
            new FileAuthority("share"),
            "server/name",
            "share",
            new SmbAuthentication.Guest()));
        Assert.Throws<ArgumentException>(() => new SmbFileProviderOptions(
            new FileProviderProfileId("smb"),
            new FileAuthority("share"),
            "server",
            "share\\nested",
            new SmbAuthentication.Guest()));
    }

    [Fact]
    public void PasswordAuthenticationRequiresAnOpaqueCredentialReference()
    {
        Assert.Throws<ArgumentException>(() => new SmbAuthentication.Password(
            "TEST",
            "fixture",
            default));
    }

    [Fact]
    public void VendorSdkTypesDoNotEscapeThePublicProviderConstructor()
    {
        var parameterAssemblies = typeof(SmbFileProvider)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Assembly.GetName().Name)
            .ToArray();

        Assert.DoesNotContain("SMBLibrary", parameterAssemblies, StringComparer.Ordinal);
    }

    private sealed class ThrowingSecretVault : ISecretVault
    {
        public SecretVaultAvailability Availability => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                $"vault-exposed-reference:{request.Reference.Value}");

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

        public void Dispose()
        {
        }
    }

}
