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

    [Fact]
    public async Task BlockedWorkspaceRouteRejectsSmbBeforeCredentialResolution()
    {
        var options = RemoteProviderTestProfiles.SmbOptions();
        using var vault = new ThrowingSecretVault();
        var connector = new FixedWorkspaceNetworkConnector(WorkspaceNetworkEgress.Blocked);
        var provider = new SmbFileProvider(vault, options, connector);
        var root = new FileLocation(
            provider.ProfileId,
            provider.Authority,
            FilePath.Root);

        var result = await provider.StatAsync(
            new FileStatRequest(root),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            FileProviderErrorCode.UnsupportedCapability,
            result.Error!.Code);
        Assert.Equal(
            "The workspace network kill switch is blocking SMB traffic.",
            result.Error.Message);
        Assert.Equal(0, connector.ConnectCount);
    }

    [Theory]
    [InlineData("proxy")]
    [InlineData("attached")]
    public async Task RoutedWorkspaceSmbUsesConnectorWithoutDirectFallback(string route)
    {
        var options = RemoteProviderTestProfiles.SmbOptions(
            new SmbAuthentication.Guest(),
            reconnectPolicy: RemoteMetadataReconnectPolicy.None);
        using var vault = new ThrowingSecretVault();
        var connector = new FixedWorkspaceNetworkConnector(route == "proxy"
            ? WorkspaceNetworkEgress.ViaProxy(
                new Uri("socks5://127.0.0.1:45678", UriKind.Absolute))
            : WorkspaceNetworkEgress.Attached);
        var provider = new SmbFileProvider(vault, options, connector);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.StatAsync(
            new FileStatRequest(root),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal(1, connector.ConnectCount);
        Assert.Equal(options.Server, connector.Host);
        Assert.Equal(445, connector.Port);
    }

    [Fact]
    public async Task RoutedTransportRelaysBytesAndUsesAnUnprivilegedEphemeralPort()
    {
        using var upstreamListener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        upstreamListener.Start();
        var upstreamPort = ((System.Net.IPEndPoint)upstreamListener.LocalEndpoint).Port;
        var connector = new TcpWorkspaceNetworkConnector(upstreamPort);
        var accepted = upstreamListener.AcceptTcpClientAsync();
        using var transport = await SmbLibraryRoutedTransport.OpenAsync(
            connector,
            "files.internal.test",
            CancellationToken.None);
        using var downstream = new System.Net.Sockets.TcpClient();
        await downstream.ConnectAsync(
            System.Net.IPAddress.Loopback,
            transport.LocalPort,
            CancellationToken.None);
        using var upstream = await accepted;
        var request = new byte[] { 1, 2, 3, 4 };

        await downstream.GetStream().WriteAsync(request);
        var received = new byte[request.Length];
        await upstream.GetStream().ReadExactlyAsync(received);
        await upstream.GetStream().WriteAsync(new byte[] { 5, 6 });
        var response = new byte[2];
        await downstream.GetStream().ReadExactlyAsync(response);

        Assert.Equal(request, received);
        Assert.Equal(new byte[] { 5, 6 }, response);
        Assert.NotEqual(445, transport.LocalPort);
        Assert.Equal("files.internal.test", connector.Host);
        Assert.Equal(445, connector.Port);
    }

    [Fact]
    public async Task DisposingRoutedTransportClosesTheConnectorStream()
    {
        using var upstreamListener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        upstreamListener.Start();
        var connector = new TcpWorkspaceNetworkConnector(
            ((System.Net.IPEndPoint)upstreamListener.LocalEndpoint).Port);
        var accepted = upstreamListener.AcceptTcpClientAsync();
        var transport = await SmbLibraryRoutedTransport.OpenAsync(
            connector,
            "files.internal.test",
            CancellationToken.None);
        using var downstream = new System.Net.Sockets.TcpClient();
        await downstream.ConnectAsync(
            System.Net.IPAddress.Loopback,
            transport.LocalPort,
            CancellationToken.None);
        using var upstream = await accepted;

        transport.Dispose();

        var closed = await upstream.GetStream().ReadAsync(new byte[1])
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task CancellingRoutedTransportOpenCancelsTheConnector()
    {
        var connector = new BlockingWorkspaceNetworkConnector();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SmbLibraryRoutedTransport.OpenAsync(
                connector,
                "files.internal.test",
                cancellation.Token));

        Assert.True(connector.CancellationObserved);
    }

    [Fact]
    public void RoutedClientPreservesLogicalServerNameAndRecognizesDfsBoundary()
    {
        Assert.True(RoutedSmb2Client.IsCompatible);
        var client = new RoutedSmb2Client(responseTimeoutInMilliseconds: 1_000);

        client.PrepareLogicalServerName("cluster-files.internal.test");

        Assert.Equal("cluster-files.internal.test", client.LogicalServerName);
        Assert.True(SmbLibrarySessionFactory.IsDfsFileStoreTypeName(
            "SMBLibrary.Client.DFS.SMB2DfsFileStore"));
        Assert.False(SmbLibrarySessionFactory.IsDfsFileStoreTypeName(
            "SMBLibrary.Client.SMB2FileStore"));
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

    private sealed class FixedWorkspaceNetworkConnector(
        WorkspaceNetworkEgress egress) : IWorkspaceNetworkConnector
    {
        public int ConnectCount { get; private set; }

        public string? Host { get; private set; }

        public int Port { get; private set; }

        public WorkspaceNetworkEgress Egress { get; } = egress;

        public Uri LocalProxyEndpoint { get; } =
            new("socks5://127.0.0.1:45678", UriKind.Absolute);

        public ValueTask<Stream> ConnectTcpAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            Host = host;
            Port = port;
            throw new IOException("Test workspace route is unavailable.");
        }
    }

    private sealed class TcpWorkspaceNetworkConnector(int upstreamPort) :
        IWorkspaceNetworkConnector
    {
        public string? Host { get; private set; }

        public int Port { get; private set; }

        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Attached;

        public Uri LocalProxyEndpoint { get; } =
            new("socks5://127.0.0.1:45678", UriKind.Absolute);

        public async ValueTask<Stream> ConnectTcpAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            Host = host;
            Port = port;
            var client = new System.Net.Sockets.TcpClient();
            try
            {
                await client.ConnectAsync(
                    System.Net.IPAddress.Loopback,
                    upstreamPort,
                    cancellationToken);
                return client.GetStream();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private sealed class BlockingWorkspaceNetworkConnector : IWorkspaceNetworkConnector
    {
        public bool CancellationObserved { get; private set; }

        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Attached;

        public Uri LocalProxyEndpoint { get; } =
            new("socks5://127.0.0.1:45678", UriKind.Absolute);

        public async ValueTask<Stream> ConnectTcpAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            _ = host;
            _ = port;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking connector unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
