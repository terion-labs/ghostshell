using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record FileProviderProfileSaveRequest(
    FileProviderProfile Profile,
    long? ExpectedRevision);

public sealed record FileProviderSecretOption(
    SecretRef? Reference,
    string Label,
    string Kind,
    bool IsAvailable)
{
    public string DisplayName => Reference is null
        ? Label
        : IsAvailable
            ? $"{Label} · {Kind} · {Reference.Value.Value}"
            : $"Missing · {Reference.Value.Value}";
}

public sealed record FileProviderConnectionOption(
    ConnectionId Id,
    string Name,
    bool IsAvailable)
{
    public string DisplayName => IsAvailable ? Name : $"Missing · {Name}";
}

public sealed class FileProviderProfileEditorViewModel : ObservableObject
{
    private readonly IFileProviderProfileRuntime _runtime;
    private readonly IFileProviderHostKeyRepair? _hostKeyRepair;
    private readonly FileProviderProfileId _id;
    private readonly int _schemaVersion;
    private string _name = string.Empty;
    private FileProviderKind _kind;
    private string _localRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _bucketName = string.Empty;
    private string _region = string.Empty;
    private string _serviceUri = string.Empty;
    private bool _forcePathStyle;
    private bool _allowInsecureTransport;
    private string _rootPrefix = string.Empty;
    private FileProviderConnectionOption? _selectedSshConnection;
    private string _remoteRoot = "/";
    private string _host = string.Empty;
    private int _port = 21;
    private string _username = string.Empty;
    private FileProviderSecretOption? _selectedCredential;
    private FtpSecurityMode _ftpSecurity = FtpSecurityMode.ExplicitTls;
    private FtpConnectionMode _ftpConnectionMode = FtpConnectionMode.AutoPassive;
    private bool _allowPlaintext;
    private string _server = string.Empty;
    private string _share = string.Empty;
    private SmbCredentialMode _smbCredentialMode = SmbCredentialMode.Guest;
    private string _domain = string.Empty;
    private string _baseUri = "https://";
    private bool _isTesting;
    private string _testStatus = "Not tested";
    private string _testDetail = "Save is allowed without a test; opening validates the provider again.";
    private SshHostKeyReview? _hostKeyReview;
    private string _hostKeyReviewStatus = string.Empty;
    private bool _isApplyingHostKeyTrust;

    public FileProviderProfileEditorViewModel(
        IFileProviderProfileRuntime runtime,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<SecretMetadataViewModel> secrets,
        FileProviderProfile? existing = null,
        long? expectedRevision = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _hostKeyRepair = runtime as IFileProviderHostKeyRepair;
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(secrets);
        ExpectedRevision = expectedRevision;
        _id = existing?.Id ?? FileProviderProfileId.New();
        _schemaVersion = existing?.SchemaVersion ?? FileProviderProfile.CurrentSchemaVersion;
        ProviderKinds = Enum.GetValues<FileProviderKind>();
        FtpSecurityModes = Enum.GetValues<FtpSecurityMode>();
        FtpConnectionModes = Enum.GetValues<FtpConnectionMode>();
        SmbCredentialModes = Enum.GetValues<SmbCredentialMode>();
        SshConnections = BuildSshConnections(connections, existing);
        SecretOptions = BuildSecretOptions(secrets, existing);
        _selectedCredential = SecretOptions[0];

        if (existing is not null)
        {
            _name = existing.Name;
            _kind = existing.ProviderKind;
            Load(existing.Configuration);
        }
    }

    public long? ExpectedRevision { get; }

    public bool IsEditing => ExpectedRevision is not null;

    public string EditorTitle => IsEditing ? "Edit file connection" : "New file connection";

    public string ProfileId => _id.Value;

    public IReadOnlyList<FileProviderKind> ProviderKinds { get; }

    public IReadOnlyList<FtpSecurityMode> FtpSecurityModes { get; }

    public IReadOnlyList<FtpConnectionMode> FtpConnectionModes { get; }

    public IReadOnlyList<SmbCredentialMode> SmbCredentialModes { get; }

    public IReadOnlyList<FileProviderConnectionOption> SshConnections { get; }

    public IReadOnlyList<FileProviderSecretOption> SecretOptions { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public FileProviderKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsLocal));
                OnPropertyChanged(nameof(IsS3));
                OnPropertyChanged(nameof(IsSftp));
                OnPropertyChanged(nameof(IsFtp));
                OnPropertyChanged(nameof(IsSmb));
                OnPropertyChanged(nameof(IsWebDav));
                OnPropertyChanged(nameof(UsesProviderCredential));
                ClearHostKeyReview();
            }
        }
    }

    public string LocalRoot { get => _localRoot; set => SetProperty(ref _localRoot, value); }

    public string BucketName { get => _bucketName; set => SetProperty(ref _bucketName, value); }

    public string Region { get => _region; set => SetProperty(ref _region, value); }

    public string ServiceUri { get => _serviceUri; set => SetProperty(ref _serviceUri, value); }

    public bool ForcePathStyle { get => _forcePathStyle; set => SetProperty(ref _forcePathStyle, value); }

    public bool AllowInsecureTransport
    {
        get => _allowInsecureTransport;
        set => SetProperty(ref _allowInsecureTransport, value);
    }

    public string RootPrefix { get => _rootPrefix; set => SetProperty(ref _rootPrefix, value); }

    public FileProviderConnectionOption? SelectedSshConnection
    {
        get => _selectedSshConnection;
        set
        {
            if (SetProperty(ref _selectedSshConnection, value))
            {
                ClearHostKeyReview();
            }
        }
    }

    public string RemoteRoot { get => _remoteRoot; set => SetProperty(ref _remoteRoot, value); }

    public string Host { get => _host; set => SetProperty(ref _host, value); }

    public int Port { get => _port; set => SetProperty(ref _port, value); }

    public string Username { get => _username; set => SetProperty(ref _username, value); }

    public FileProviderSecretOption? SelectedCredential
    {
        get => _selectedCredential;
        set => SetProperty(ref _selectedCredential, value);
    }

    public FtpSecurityMode FtpSecurity
    {
        get => _ftpSecurity;
        set
        {
            if (SetProperty(ref _ftpSecurity, value))
            {
                Port = value == FtpSecurityMode.ImplicitTls ? 990 : 21;
                OnPropertyChanged(nameof(IsPlaintextFtp));
            }
        }
    }

    public FtpConnectionMode FtpConnectionMode
    {
        get => _ftpConnectionMode;
        set => SetProperty(ref _ftpConnectionMode, value);
    }

    public bool AllowPlaintext { get => _allowPlaintext; set => SetProperty(ref _allowPlaintext, value); }

    public string Server { get => _server; set => SetProperty(ref _server, value); }

    public string Share { get => _share; set => SetProperty(ref _share, value); }

    public SmbCredentialMode SmbCredentialMode
    {
        get => _smbCredentialMode;
        set
        {
            if (SetProperty(ref _smbCredentialMode, value))
            {
                OnPropertyChanged(nameof(UsesSmbCredential));
            }
        }
    }

    public string Domain { get => _domain; set => SetProperty(ref _domain, value); }

    public string BaseUri { get => _baseUri; set => SetProperty(ref _baseUri, value); }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                OnPropertyChanged(nameof(CanApplyHostKeyTrust));
            }
        }
    }

    public string TestStatus { get => _testStatus; private set => SetProperty(ref _testStatus, value); }

    public string TestDetail { get => _testDetail; private set => SetProperty(ref _testDetail, value); }

    public bool IsLocal => Kind == FileProviderKind.Local;

    public bool IsS3 => Kind == FileProviderKind.S3;

    public bool IsSftp => Kind == FileProviderKind.Sftp;

    public bool IsFtp => Kind == FileProviderKind.Ftp;

    public bool IsSmb => Kind == FileProviderKind.Smb;

    public bool IsWebDav => Kind == FileProviderKind.WebDav;

    public bool UsesProviderCredential => IsS3 || IsFtp || IsSmb || IsWebDav;

    public bool UsesSmbCredential => IsSmb && SmbCredentialMode == SmbCredentialMode.UsernamePassword;

    public bool IsPlaintextFtp => IsFtp && FtpSecurity == FtpSecurityMode.Plaintext;

    public bool HasHostKeyReview => _hostKeyReview is
    {
        Disposition: SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed,
    };

    public SshHostKeyReview? HostKeyReview => _hostKeyReview;

    public bool HasTrustedHostKey => _hostKeyReview?.Trusted is not null;

    public string HostKeyReviewTitle => _hostKeyReview?.Disposition switch
    {
        SshHostKeyDisposition.Unknown => "Unknown SSH host key",
        SshHostKeyDisposition.Changed => "SSH host key changed",
        _ => "SSH host-key review",
    };

    public string PresentedHostKey => FormatHostKey(_hostKeyReview?.Presented);

    public string TrustedHostKey => FormatHostKey(_hostKeyReview?.Trusted);

    public string HostKeyReviewStatus => _hostKeyReviewStatus;

    public string HostKeyActionText => _hostKeyReview?.Disposition == SshHostKeyDisposition.Changed
        ? "Replace trusted key"
        : "Trust this key";

    public bool CanApplyHostKeyTrust => HasHostKeyReview && !IsTesting && !_isApplyingHostKeyTrust;

    public FileProviderProfileSaveRequest CreateSaveRequest() =>
        new(BuildProfile(), ExpectedRevision);

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting)
        {
            return;
        }

        FileProviderProfile profile;
        try
        {
            profile = BuildProfile();
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            TestStatus = "Validation failed";
            TestDetail = exception.Message;
            return;
        }

        IsTesting = true;
        ClearHostKeyReview();
        TestStatus = "Testing provider";
        TestDetail = "Resolving the adapter and listing its configured root…";
        try
        {
            var result = await _runtime.TestAsync(profile, cancellationToken);
            TestStatus = result.IsSuccess ? "Provider connected" : "Test failed";
            TestDetail = result.Message;
            if (!result.IsSuccess
                && result.ErrorCode is FilePanelErrorCode.HostKeyUnknown
                    or FilePanelErrorCode.HostKeyChanged)
            {
                await InspectHostKeyAsync(profile, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TestStatus = "Test cancelled";
            TestDetail = "The provider test was cancelled.";
        }
        catch (Exception)
        {
            TestStatus = "Test failed";
            TestDetail = "The provider runtime could not complete the test.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    public async Task TrustHostKeyAsync(
        SshHostKeyReviewId confirmedReviewId,
        CancellationToken cancellationToken)
    {
        if (_hostKeyRepair is null
            || _hostKeyReview is not { } review
            || review.Id != confirmedReviewId
            || !CanApplyHostKeyTrust)
        {
            return;
        }

        FileProviderProfile profile;
        try
        {
            profile = BuildProfile();
        }
        catch (ArgumentException exception)
        {
            _hostKeyReviewStatus = exception.Message;
            OnPropertyChanged(nameof(HostKeyReviewStatus));
            return;
        }

        _isApplyingHostKeyTrust = true;
        OnPropertyChanged(nameof(CanApplyHostKeyTrust));
        _hostKeyReviewStatus = review.Disposition == SshHostKeyDisposition.Changed
            ? "Replacing the exact reviewed host key…"
            : "Trusting the exact reviewed host key…";
        OnPropertyChanged(nameof(HostKeyReviewStatus));
        try
        {
            var action = review.Disposition == SshHostKeyDisposition.Changed
                ? SshHostKeyTrustAction.ReplaceChanged
                : SshHostKeyTrustAction.TrustNew;
            var result = await _hostKeyRepair.TrustSshHostKeyAsync(
                profile,
                review.Id,
                action,
                cancellationToken);
            if (result is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
            {
                _hostKeyReviewStatus = failure.Error.Message;
                OnPropertyChanged(nameof(HostKeyReviewStatus));
                return;
            }

            ClearHostKeyReview();
            TestStatus = "Host key trusted";
            TestDetail = "The shared SSH host-key store was updated. Testing the provider again…";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _hostKeyReviewStatus = "Host-key trust was cancelled.";
            OnPropertyChanged(nameof(HostKeyReviewStatus));
            return;
        }
        finally
        {
            _isApplyingHostKeyTrust = false;
            OnPropertyChanged(nameof(CanApplyHostKeyTrust));
        }

        await TestAsync(cancellationToken);
    }

    private async Task InspectHostKeyAsync(
        FileProviderProfile profile,
        CancellationToken cancellationToken)
    {
        if (_hostKeyRepair is null)
        {
            TestDetail = $"{TestDetail} Open the referenced SSH connection to review its host key.";
            return;
        }

        var result = await _hostKeyRepair.InspectSshHostKeyAsync(profile, cancellationToken);
        if (result is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
        {
            TestDetail = $"{TestDetail} Host-key inspection failed: {failure.Error.Message}";
            return;
        }

        var review = ((ConnectionRuntimeResult<SshHostKeyReview>.Success)result).Value;
        if (review.Disposition is not (SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed))
        {
            TestDetail = $"{TestDetail} The connection trust changed; run the test again.";
            return;
        }

        _hostKeyReview = review;
        _hostKeyReviewStatus = review.Disposition == SshHostKeyDisposition.Changed
            ? "Compare both fingerprints before explicitly replacing the trusted key."
            : "Confirm this fingerprint through a separate trusted channel before accepting it.";
        NotifyHostKeyReviewChanged();
    }

    private void ClearHostKeyReview()
    {
        _hostKeyReview = null;
        _hostKeyReviewStatus = string.Empty;
        NotifyHostKeyReviewChanged();
    }

    private void NotifyHostKeyReviewChanged()
    {
        OnPropertyChanged(nameof(HasHostKeyReview));
        OnPropertyChanged(nameof(HostKeyReview));
        OnPropertyChanged(nameof(HasTrustedHostKey));
        OnPropertyChanged(nameof(HostKeyReviewTitle));
        OnPropertyChanged(nameof(PresentedHostKey));
        OnPropertyChanged(nameof(TrustedHostKey));
        OnPropertyChanged(nameof(HostKeyReviewStatus));
        OnPropertyChanged(nameof(HostKeyActionText));
        OnPropertyChanged(nameof(CanApplyHostKeyTrust));
    }

    private static string FormatHostKey(SshHostKeyIdentity? identity) => identity is null
        ? string.Empty
        : $"{identity.Algorithm}  {identity.Sha256Fingerprint}";

    private FileProviderProfile BuildProfile() => new(
        _id,
        _schemaVersion,
        Required(Name, "Provider name"),
        BuildConfiguration());

    private FileProviderConfiguration BuildConfiguration() => Kind switch
    {
        FileProviderKind.Local => new FileProviderConfiguration.Local(
            Required(LocalRoot, "Local root")),
        FileProviderKind.S3 => new FileProviderConfiguration.S3(
            Required(BucketName, "Bucket name"),
            Optional(Region),
            OptionalUri(ServiceUri),
            ForcePathStyle,
            AllowInsecureTransport,
            SelectedCredential?.Reference,
            OptionalExact(RootPrefix)),
        FileProviderKind.Sftp => new FileProviderConfiguration.Sftp(
            SelectedSshConnection?.Id
                ?? throw new ArgumentException("An SSH connection is required for SFTP."),
            Required(RemoteRoot, "Remote root")),
        FileProviderKind.Ftp => new FileProviderConfiguration.Ftp(
            Required(Host, "FTP host"),
            Port,
            Optional(Username),
            SelectedCredential?.Reference,
            FtpSecurity,
            FtpConnectionMode,
            Required(RemoteRoot, "Remote root"),
            AllowPlaintext),
        FileProviderKind.Smb => new FileProviderConfiguration.Smb(
            Required(Server, "SMB server"),
            Required(Share, "SMB share"),
            SmbCredentialMode,
            Optional(Domain),
            UsesSmbCredential ? Required(Username, "SMB username") : null,
            UsesSmbCredential
                ? SelectedCredential?.Reference
                    ?? throw new ArgumentException("An SMB password secret is required.")
                : null,
            Required(RemoteRoot, "Remote root")),
        FileProviderKind.WebDav => new FileProviderConfiguration.WebDav(
            RequiredUri(BaseUri, "WebDAV base URI"),
            Optional(Username),
            SelectedCredential?.Reference,
            AllowInsecureTransport),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };

    private void Load(FileProviderConfiguration configuration)
    {
        switch (configuration)
        {
            case FileProviderConfiguration.Local local:
                _localRoot = local.RootPath;
                break;
            case FileProviderConfiguration.S3 s3:
                _bucketName = s3.BucketName;
                _region = s3.Region ?? string.Empty;
                _serviceUri = s3.ServiceUri?.AbsoluteUri ?? string.Empty;
                _forcePathStyle = s3.ForcePathStyle;
                _allowInsecureTransport = s3.AllowInsecureTransport;
                _rootPrefix = s3.RootPrefix ?? string.Empty;
                SelectSecret(s3.CredentialsSecret);
                break;
            case FileProviderConfiguration.Sftp sftp:
                _selectedSshConnection = SshConnections.FirstOrDefault(item =>
                    item.Id == sftp.ConnectionId);
                _remoteRoot = sftp.RemoteRoot;
                break;
            case FileProviderConfiguration.Ftp ftp:
                _host = ftp.Host;
                _port = ftp.Port;
                _username = ftp.Username ?? string.Empty;
                _ftpSecurity = ftp.Security;
                _ftpConnectionMode = ftp.ConnectionMode;
                _remoteRoot = ftp.RemoteRoot;
                _allowPlaintext = ftp.AllowPlaintext;
                SelectSecret(ftp.PasswordSecret);
                break;
            case FileProviderConfiguration.Smb smb:
                _server = smb.Server;
                _share = smb.Share;
                _smbCredentialMode = smb.CredentialMode;
                _domain = smb.Domain ?? string.Empty;
                _username = smb.Username ?? string.Empty;
                _remoteRoot = smb.RemoteRoot;
                SelectSecret(smb.PasswordSecret);
                break;
            case FileProviderConfiguration.WebDav webDav:
                _baseUri = webDav.BaseUri.AbsoluteUri;
                _username = webDav.Username ?? string.Empty;
                _allowInsecureTransport = webDav.AllowInsecureTransport;
                SelectSecret(webDav.PasswordSecret);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private void SelectSecret(SecretRef? reference)
    {
        _selectedCredential = SecretOptions.FirstOrDefault(item => item.Reference == reference)
            ?? SecretOptions[0];
    }

    private IReadOnlyList<FileProviderConnectionOption> BuildSshConnections(
        IReadOnlyList<ConnectionProfile> connections,
        FileProviderProfile? existing)
    {
        var options = connections
            .Where(item => item.Endpoint is ConnectionEndpoint.Ssh)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FileProviderConnectionOption(item.Id, item.Name, true))
            .ToList();
        if (existing?.Configuration is FileProviderConfiguration.Sftp sftp
            && options.All(item => item.Id != sftp.ConnectionId))
        {
            options.Add(new FileProviderConnectionOption(
                sftp.ConnectionId,
                sftp.ConnectionId.Value,
                false));
        }

        return options.AsReadOnly();
    }

    private IReadOnlyList<FileProviderSecretOption> BuildSecretOptions(
        IReadOnlyList<SecretMetadataViewModel> secrets,
        FileProviderProfile? existing)
    {
        var options = new List<FileProviderSecretOption>
        {
            new(null, "No stored credential", string.Empty, true),
        };
        options.AddRange(secrets
            .Where(item => item.SecretScope.Kind == SecretScopeKind.FileProvider
                && string.Equals(item.SecretScope.OwnerId, _id.Value, StringComparison.Ordinal))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(item => new FileProviderSecretOption(
                item.Reference,
                item.Label,
                item.Kind,
                true)));
        var referenced = existing?.Configuration switch
        {
            FileProviderConfiguration.S3 value => value.CredentialsSecret,
            FileProviderConfiguration.Ftp value => value.PasswordSecret,
            FileProviderConfiguration.Smb value => value.PasswordSecret,
            FileProviderConfiguration.WebDav value => value.PasswordSecret,
            _ => null,
        };
        if (referenced is { } reference
            && options.All(item => item.Reference != reference))
        {
            options.Add(new FileProviderSecretOption(reference, "Missing credential", "UNAVAILABLE", false));
        }

        return options.AsReadOnly();
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} is required.");
        }

        return value.Trim();
    }

    private static string? Optional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? OptionalExact(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static Uri RequiredUri(string value, string label)
    {
        if (!Uri.TryCreate(Required(value, label), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"{label} must be an absolute URI.");
        }

        return uri;
    }

    private static Uri? OptionalUri(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredUri(value, "Service URI");
}
