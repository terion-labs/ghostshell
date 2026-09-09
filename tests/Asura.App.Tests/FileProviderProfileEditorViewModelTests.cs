using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class FileProviderProfileEditorViewModelTests
{
    [Fact]
    public void EveryProviderKindBuildsATypedDurableConfiguration()
    {
        var connection = SshConnection();
        var editor = new FileProviderProfileEditorViewModel(
            new StubProviderRuntime(),
            [connection],
            [])
        {
            Name = "Files",

            Kind = FileProviderKind.Local,
            LocalRoot = Path.GetTempPath()
        };
        Assert.IsType<FileProviderConfiguration.Local>(editor.CreateSaveRequest().Profile.Configuration);

        editor.Kind = FileProviderKind.S3;
        editor.BucketName = "example-bucket";
        Assert.IsType<FileProviderConfiguration.S3>(editor.CreateSaveRequest().Profile.Configuration);

        editor.Kind = FileProviderKind.Sftp;
        editor.SelectedSshConnection = Assert.Single(editor.SshConnections);
        Assert.IsType<FileProviderConfiguration.Sftp>(editor.CreateSaveRequest().Profile.Configuration);

        editor.Kind = FileProviderKind.Ftp;
        editor.Host = "ftp.example.test";
        editor.Username = string.Empty;
        Assert.IsType<FileProviderConfiguration.Ftp>(editor.CreateSaveRequest().Profile.Configuration);

        editor.Kind = FileProviderKind.Smb;
        editor.Server = "smb.example.test";
        editor.Share = "files";
        editor.SmbCredentialMode = SmbCredentialMode.Guest;
        Assert.IsType<FileProviderConfiguration.Smb>(editor.CreateSaveRequest().Profile.Configuration);

        editor.Kind = FileProviderKind.WebDav;
        editor.BaseUri = "https://dav.example.test/files/";
        editor.Username = string.Empty;
        Assert.IsType<FileProviderConfiguration.WebDav>(editor.CreateSaveRequest().Profile.Configuration);
    }

    [Fact]
    public void CredentialSelectorContainsOnlyOpaqueReferencesOwnedByTheProfile()
    {
        var existing = new FileProviderProfile(
            new FileProviderProfileId("files.scoped"),
            FileProviderProfile.CurrentSchemaVersion,
            "Scoped",
            new FileProviderConfiguration.S3("bucket"));
        var matching = Secret(
            "matching",
            new SecretScope(SecretScopeKind.FileProvider, existing.Id.Value));
        var other = Secret(
            "other",
            new SecretScope(SecretScopeKind.FileProvider, "files.other"));
        var connectionSecret = Secret(
            "connection",
            new SecretScope(SecretScopeKind.Connection, "connection.one"));

        var editor = new FileProviderProfileEditorViewModel(
            new StubProviderRuntime(),
            [],
            [matching, other, connectionSecret],
            existing,
            4);

        Assert.Collection(
            editor.SecretOptions,
            option => Assert.Null(option.Reference),
            option => Assert.Equal(matching.Reference, option.Reference));
        Assert.All(editor.SecretOptions, option =>
            Assert.DoesNotContain("secret value", option.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChangedSftpHostKeyRequiresExplicitReviewedReplacementAndRetest()
    {
        var connection = SshConnection();
        var profile = new FileProviderProfile(
            new FileProviderProfileId("files.sftp.repair"),
            FileProviderProfile.CurrentSchemaVersion,
            "Repair SFTP",
            new FileProviderConfiguration.Sftp(connection.Id, "/"));
        var runtime = new RepairingProviderRuntime(connection);
        var editor = new FileProviderProfileEditorViewModel(
            runtime,
            [connection],
            [],
            profile,
            expectedRevision: 2);

        await editor.TestAsync(CancellationToken.None);

        Assert.True(editor.HasHostKeyReview);
        Assert.True(editor.HasTrustedHostKey);
        Assert.Equal("SSH host key changed", editor.HostKeyReviewTitle);
        Assert.Equal("Replace trusted key", editor.HostKeyActionText);
        Assert.Contains("SHA256:", editor.PresentedHostKey, StringComparison.Ordinal);
        Assert.Contains("SHA256:", editor.TrustedHostKey, StringComparison.Ordinal);

        await editor.TrustHostKeyAsync(editor.HostKeyReview!.Id, CancellationToken.None);

        Assert.Equal(SshHostKeyTrustAction.ReplaceChanged, runtime.TrustAction);
        Assert.False(editor.HasHostKeyReview);
        Assert.Equal("Provider connected", editor.TestStatus);
        Assert.Equal(2, runtime.TestCount);
    }

    [Fact]
    public async Task HostKeyTrustRejectsAReviewOtherThanTheOneExplicitlyConfirmed()
    {
        var connection = SshConnection();
        var profile = new FileProviderProfile(
            new FileProviderProfileId("files.sftp.stale-review"),
            FileProviderProfile.CurrentSchemaVersion,
            "Stale review",
            new FileProviderConfiguration.Sftp(connection.Id, "/"));
        var runtime = new RepairingProviderRuntime(connection);
        var editor = new FileProviderProfileEditorViewModel(
            runtime,
            [connection],
            [],
            profile,
            expectedRevision: 1);
        await editor.TestAsync(CancellationToken.None);

        await editor.TrustHostKeyAsync(
            new SshHostKeyReviewId("a-different-confirmed-review"),
            CancellationToken.None);

        Assert.Null(runtime.TrustAction);
        Assert.True(editor.HasHostKeyReview);
        Assert.Equal(1, runtime.TestCount);
    }

    [Fact]
    public async Task UnknownSftpHostKeyUsesExplicitFirstTrustAction()
    {
        var connection = SshConnection();
        var profile = new FileProviderProfile(
            new FileProviderProfileId("files.sftp.first-trust"),
            FileProviderProfile.CurrentSchemaVersion,
            "First trust",
            new FileProviderConfiguration.Sftp(connection.Id, "/"));
        var runtime = new RepairingProviderRuntime(
            connection,
            SshHostKeyDisposition.Unknown);
        var editor = new FileProviderProfileEditorViewModel(
            runtime,
            [connection],
            [],
            profile,
            expectedRevision: 1);
        await editor.TestAsync(CancellationToken.None);

        Assert.Equal("Unknown SSH host key", editor.HostKeyReviewTitle);
        Assert.Equal("Trust this key", editor.HostKeyActionText);
        Assert.False(editor.HasTrustedHostKey);

        await editor.TrustHostKeyAsync(editor.HostKeyReview!.Id, CancellationToken.None);

        Assert.Equal(SshHostKeyTrustAction.TrustNew, runtime.TrustAction);
        Assert.Equal("Provider connected", editor.TestStatus);
    }

    private static SecretMetadataViewModel Secret(string id, SecretScope scope) => new(
        new SecretRef(id),
        id,
        SecretKind.Password.ToString(),
        scope.Kind.ToString(),
        "now",
        "never",
        scope,
        "none",
        0);

    private static ConnectionProfile SshConnection() => new(
        new ConnectionId("ssh.files"),
        ConnectionProfile.CurrentSchemaVersion,
        "SSH files",
        new ConnectionEndpoint.Ssh("example.test", username: "operator"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private sealed class StubProviderRuntime : IFileProviderProfileRuntime
    {
        public event EventHandler? ProfilesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileProviderTestResult(
                true,
                "ok",
                profile.Name));

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class RepairingProviderRuntime(
        ConnectionProfile connection,
        SshHostKeyDisposition disposition = SshHostKeyDisposition.Changed) :
        IFileProviderProfileRuntime,
        IFileProviderHostKeyRepair
    {
        private readonly SshHostKeyReview _review = new(
            new SshHostKeyReviewId("review-sftp-changed"),
            connection.Id,
            "example.test:22",
            disposition,
            new SshHostKeyIdentity("ssh-ed25519", $"SHA256:{new string('A', 43)}"),
            disposition == SshHostKeyDisposition.Changed
                ? new SshHostKeyIdentity("ssh-ed25519", $"SHA256:{new string('B', 43)}")
                : null,
            DateTimeOffset.UtcNow.AddMinutes(5));

        public event EventHandler? ProfilesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public int TestCount { get; private set; }

        public SshHostKeyTrustAction? TrustAction { get; private set; }

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestCount++;
            return ValueTask.FromResult(TestCount == 1
                ? new FileProviderTestResult(
                    false,
                    disposition == SshHostKeyDisposition.Changed
                        ? "file_host_key_changed"
                        : "file_host_key_unknown",
                    disposition == SshHostKeyDisposition.Changed
                        ? "The SFTP server host key changed."
                        : "The SFTP server host key is unknown.",
                    ErrorCode: disposition == SshHostKeyDisposition.Changed
                        ? FilePanelErrorCode.HostKeyChanged
                        : FilePanelErrorCode.HostKeyUnknown)
                : new FileProviderTestResult(
                    true,
                    "file_provider_test_succeeded",
                    "Connected."));
        }

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> InspectSshHostKeyAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ConnectionRuntimeResult<SshHostKeyReview>.Succeed(_review));
        }

        public ValueTask<ConnectionRuntimeResult<SshHostKeyReview>> TrustSshHostKeyAsync(
            FileProviderProfile profile,
            SshHostKeyReviewId reviewId,
            SshHostKeyTrustAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_review.Id, reviewId);
            TrustAction = action;
            var trusted = new SshHostKeyReview(
                SshHostKeyReviewId.New(),
                connection.Id,
                _review.Endpoint,
                SshHostKeyDisposition.Trusted,
                _review.Presented,
                _review.Presented,
                DateTimeOffset.UtcNow.AddMinutes(5));
            return ValueTask.FromResult(
                ConnectionRuntimeResult<SshHostKeyReview>.Succeed(trusted));
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }
}
