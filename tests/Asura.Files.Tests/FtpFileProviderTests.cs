namespace Asura.Files.Tests;

public sealed class FtpFileProviderTests
{
    [Fact]
    public void PlaintextIsExplicitAndAlwaysProducesSecurityDiagnostic()
    {
        var options = RemoteProviderTestProfiles.FtpOptions(FtpTransportSecurity.Plaintext);
        var provider = new FtpFileProvider(new FakeRemoteSessionFactory(), options);

        Assert.Equal(FtpTransportSecurity.Plaintext, provider.Options.TransportSecurity);
        Assert.Equal(FileNameComparison.ProviderDefined, provider.Capabilities.NameComparison);
        Assert.True(provider.Capabilities.Supports(FileProviderCapability.StreamingWrite));
        var warning = Assert.Single(provider.Diagnostics);
        Assert.Equal("ftp_plaintext_transport", warning.StableCode);
        Assert.Contains("without TLS", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Auto", Enum.GetNames<FtpTransportSecurity>(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task MetadataReconnectRetriesExactlyOnceOnAFreshSession()
    {
        var sessions = new FakeRemoteSessionFactory { FailOpenCount = 1 };
        var options = RemoteProviderTestProfiles.FtpOptions(
            reconnectPolicy: RemoteMetadataReconnectPolicy.RetryOnce);
        var provider = new FtpFileProvider(sessions, options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.StatAsync(new FileStatRequest(root), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, sessions.OpenCount);
    }

    [Theory]
    [InlineData(FtpTransportSecurity.ExplicitTls, 21)]
    [InlineData(FtpTransportSecurity.ImplicitTls, 990)]
    public void TlsModesKeepTheirExactPolicyWithoutPlaintextWarning(
        FtpTransportSecurity security,
        int expectedPort)
    {
        var options = RemoteProviderTestProfiles.FtpOptions(security);
        var provider = new FtpFileProvider(new FakeRemoteSessionFactory(), options);

        Assert.Equal(expectedPort, options.Port);
        Assert.Empty(provider.Diagnostics);
    }

    [Theory]
    [InlineData(FtpDataConnectionMode.Passive)]
    [InlineData(FtpDataConnectionMode.Active)]
    public void DataConnectionModeIsRetainedExactly(FtpDataConnectionMode mode)
    {
        var options = RemoteProviderTestProfiles.FtpOptions(dataMode: mode);
        var provider = new FtpFileProvider(new FakeRemoteSessionFactory(), options);

        Assert.Equal(mode, provider.Options.DataConnectionMode);
    }

    [Fact]
    public void NegotiatedRestFeatureDoesNotClaimResumableTransfer()
    {
        var options = RemoteProviderTestProfiles.FtpOptions();
        var sessions = new FakeRemoteSessionFactory
        {
            LastConnection = new FtpConnectionSnapshot(
                FtpTransportSecurity.ExplicitTls,
                IsEncrypted: true,
                FtpServerFeature.MachineListing | FtpServerFeature.RestartDownload,
                "utf-8"),
        };
        var provider = new FtpFileProvider(sessions, options, sessions);

        Assert.True(provider.LastConnection!.ServerFeatures.HasFlag(FtpServerFeature.RestartDownload));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.ResumableTransfer));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.AtomicReplace));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.ServerSideCopy));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Versioning));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Checksum));
    }

    [Fact]
    public async Task BackslashNamesAreRejectedBeforeReachingAnFtpSession()
    {
        var sessions = new FakeRemoteSessionFactory();
        var options = RemoteProviderTestProfiles.FtpOptions();
        var provider = new FtpFileProvider(sessions, options);
        var location = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root)
            .Child(new FilePathSegment("ambiguous\\name.txt"));

        var result = await provider.StatAsync(new FileStatRequest(location), CancellationToken.None);

        Assert.Equal(FileProviderErrorCode.InvalidName, result.Error!.Code);
        Assert.Equal(0, sessions.OpenCount);
    }

    [Fact]
    public void VendorSdkTypesDoNotEscapeThePublicProviderConstructors()
    {
        var publicProviderTypes = new[] { typeof(SftpFileProvider), typeof(FtpFileProvider) };

        var parameterAssemblies = publicProviderTypes
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Assembly.GetName().Name)
            .ToArray();

        Assert.DoesNotContain("Renci.SshNet", parameterAssemblies, StringComparer.Ordinal);
        Assert.DoesNotContain("FluentFTP", parameterAssemblies, StringComparer.Ordinal);
    }

    [Fact]
    public async Task CaseOnlyTransferAliasesAreRejectedBeforeOpeningASession()
    {
        var sessions = new FakeRemoteSessionFactory();
        var provider = new FtpFileProvider(sessions, RemoteProviderTestProfiles.FtpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        var source = root.Child(new FilePathSegment("same.txt"));
        var destination = root.Child(new FilePathSegment("SAME.txt"));

        var result = await provider.TransferAsync(
            new FileTransferRequest(
                source,
                destination,
                FileTransferKind.Move,
                bufferSize: 8,
                new FileMutationPrecondition.Any()),
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.InvalidLocation, result.Error!.Code);
        Assert.Equal(0, sessions.OpenCount);
    }

    [Theory]
    [InlineData(FtpTransportSecurity.Plaintext, FtpDataConnectionMode.Passive, FluentFTP.FtpEncryptionMode.None, FluentFTP.FtpDataConnectionType.AutoPassive, false)]
    [InlineData(FtpTransportSecurity.ExplicitTls, FtpDataConnectionMode.Active, FluentFTP.FtpEncryptionMode.Explicit, FluentFTP.FtpDataConnectionType.AutoActive, true)]
    [InlineData(FtpTransportSecurity.ImplicitTls, FtpDataConnectionMode.Passive, FluentFTP.FtpEncryptionMode.Implicit, FluentFTP.FtpDataConnectionType.AutoPassive, true)]
    public void VendorConfigurationPreservesSecurityDataModeAndLiteralPaths(
        FtpTransportSecurity security,
        FtpDataConnectionMode dataMode,
        FluentFTP.FtpEncryptionMode expectedEncryption,
        FluentFTP.FtpDataConnectionType expectedDataMode,
        bool encryptedData)
    {
        var options = RemoteProviderTestProfiles.FtpOptions(security, dataMode);

        var config = FluentFtpSessionFactory.CreateConfig(options);

        Assert.Equal(expectedEncryption, config.EncryptionMode);
        Assert.Equal(expectedDataMode, config.DataConnectionType);
        Assert.Equal(encryptedData, config.DataConnectionEncryption);
        Assert.False(config.ValidateAnyCertificate);
        Assert.Equal(
            System.Security.Authentication.SslProtocols.None,
            config.SslProtocols);
        Assert.Equal(1, config.RetryAttempts);
        Assert.False(config.SanitizeUrlEncoding);
        Assert.False(config.SanitizeTraversal);
        Assert.False(config.SanitizeControlChars);
        Assert.False(config.SanitizeMultiline);
        Assert.False(config.SanitizeUnicodeSpoofing);
    }

    [Fact]
    public async Task EdgeWhitespaceNamesAreRejectedBeforeVendorSanitizationCanAliasThem()
    {
        var sessions = new FakeRemoteSessionFactory();
        var provider = new FtpFileProvider(sessions, RemoteProviderTestProfiles.FtpOptions());
        var location = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root)
            .Child(new FilePathSegment("trailing-space "));

        var result = await provider.StatAsync(new FileStatRequest(location), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.InvalidName, result.Error!.Code);
        Assert.Equal(0, sessions.OpenCount);
    }

    [Fact]
    public async Task UnknownSourceSizeCannotCommitAnEmptyMoveAndDeleteTheSource()
    {
        var sessions = new FakeRemoteSessionFactory();
        var provider = new FtpFileProvider(sessions, RemoteProviderTestProfiles.FtpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        var sourceLocation = root.Child(new FilePathSegment("source.txt"));
        var destination = root.Child(new FilePathSegment("destination.txt"));
        await using (var source = new MemoryStream([1, 2, 3], writable: false))
        {
            var written = await provider.WriteAsync(
                new FileWriteRequest(
                    sourceLocation,
                    contentLength: 3,
                    bufferSize: 2,
                    new FileMutationPrecondition.MustNotExist()),
                source,
                progress: null,
                CancellationToken.None);
            Assert.True(written.IsSuccess, written.Error?.Message);
        }

        sessions.ReportUnknownFileSizes = true;
        var moved = await provider.TransferAsync(
            new FileTransferRequest(
                sourceLocation,
                destination,
                FileTransferKind.Move,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);

        Assert.False(moved.IsSuccess);
        Assert.Equal(FileProviderErrorCode.UnsupportedCapability, moved.Error!.Code);
        Assert.True((await provider.StatAsync(new FileStatRequest(sourceLocation), CancellationToken.None)).IsSuccess);
        var destinationStat = await provider.StatAsync(
            new FileStatRequest(destination),
            CancellationToken.None);
        Assert.False(destinationStat.IsSuccess);
        Assert.Equal(FileProviderErrorCode.NotFound, destinationStat.Error!.Code);
    }

    [Fact]
    public void EmptyFileRevisionIsStableWhenSizeIsEnrichedBySizeCommand()
    {
        var item = new FluentFTP.FtpListItem
        {
            Name = "empty.txt",
            Type = FluentFTP.FtpObjectType.File,
            Size = 0,
            Modified = DateTime.MinValue,
        };

        var listed = FluentFtpSessionFactory.MapListedEntry(item);
        var stated = FluentFtpSessionFactory.MapStatEntry(item, knownSize: 0);

        Assert.Null(listed.Size);
        Assert.Equal(0, stated.Size);
        Assert.Equal(listed.Revision, stated.Revision);
    }

    [Fact]
    public async Task ConfiguredEncodingRejectsAliasingNamesBeforeOpeningASession()
    {
        var options = new FtpFileProviderOptions(
            new FileProviderProfileId("ftp-ascii"),
            new FileAuthority("ftp-ascii"),
            "ftp.example.test",
            "fixture",
            passwordSecret: null,
            FtpTransportSecurity.ExplicitTls,
            encodingWebName: "us-ascii");
        var sessions = new FakeRemoteSessionFactory();
        var provider = new FtpFileProvider(sessions, options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        foreach (var unsafeName in new[] { "é", "\ud800" })
        {
            var location = root.Child(new FilePathSegment(unsafeName));
            var result = await provider.StatAsync(
                new FileStatRequest(location),
                CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(FileProviderErrorCode.InvalidName, result.Error!.Code);
        }

        Assert.Equal(0, sessions.OpenCount);
    }

    [Fact]
    public async Task CaseFoldedRenameAliasesAreRejectedBeforeOpeningASession()
    {
        var sessions = new FakeRemoteSessionFactory();
        var provider = new FtpFileProvider(sessions, RemoteProviderTestProfiles.FtpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        var source = root.Child(new FilePathSegment("folder"));
        var destinations = new[]
        {
            root.Child(new FilePathSegment("FOLDER")),
            root.Child(new FilePathSegment("FOLDER")).Child(new FilePathSegment("child")),
        };

        foreach (var destination in destinations)
        {
            var result = await provider.RenameAsync(
                new FileRenameRequest(
                    source,
                    destination,
                    new FileMutationPrecondition.Any()),
                CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(FileProviderErrorCode.InvalidLocation, result.Error!.Code);
        }

        Assert.Equal(0, sessions.OpenCount);
    }
}
