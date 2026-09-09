using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;
using Asura.Git;

namespace Asura.App.ViewModels;

public enum ConnectionAuthenticationChoice
{
    SystemConfiguration,
    SshAgent,
    Password,
    PrivateKey,
}

public sealed record ConnectionEditorSaveRequest(
    ConnectionProfile Profile,
    long? ExpectedRevision);

/// <summary>
/// One entry in the Git · SSH "From saved connection" selector. The first
/// entry is always "Enter manually" with no profile; the rest carry a saved
/// SSH profile the Git connection can reference for its endpoint and
/// credentials.
/// </summary>
public sealed record SavedSshSourceOption(string DisplayName, ConnectionProfile? Profile)
{
    public static SavedSshSourceOption Manual { get; } = new("Enter manually", null);
}

public sealed class ConnectionEditorViewModel : ObservableObject
{
    private readonly IConnectionRuntime _runtime;
    private readonly IConnectionSecurityRuntime? _securityRuntime;
    private readonly IGitRepositoryClient? _gitClient;
    private readonly ConnectionId _id;
    private readonly IReadOnlyList<ConnectionProfile> _savedConnections;
    private readonly int _schemaVersion;
    private readonly IReadOnlyList<ConnectionEnvironmentVariable> _environment;
    private readonly IReadOnlyList<string> _tags;
    private string _name = string.Empty;
    private ConnectionKind _kind = ConnectionKind.Local;
    private string _localShell = string.Empty;
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private string _container = string.Empty;
    private string _dockerContext = string.Empty;
    private string _distribution = string.Empty;
    private string _startupDirectory = string.Empty;
    private string _startupCommand = string.Empty;
    private ConnectionAuthenticationChoice _authentication;
    private string _secretReference = string.Empty;
    private string _passphraseSecretReference = string.Empty;
    private SshHostKeyPolicy _hostKeyPolicy = SshHostKeyPolicy.Strict;
    private bool _keepAliveEnabled;
    private int _keepAliveSeconds = 30;
    private int _keepAliveFailures = 3;
    private bool _opensGitRepository;
    private SavedSshSourceOption _selectedSavedSshSource = SavedSshSourceOption.Manual;
    private bool _isTesting;
    private string _testStatus = "Not tested";
    private string _testDetail = "Save is allowed without a test; opening validates the profile again.";
    private SshHostKeyReview? _hostKeyReview;

    public ConnectionEditorViewModel(
        IConnectionRuntime runtime,
        ConnectionProfile? existing = null,
        long? expectedRevision = null,
        IConnectionSecurityRuntime? securityRuntime = null,
        IGitRepositoryClient? gitClient = null,
        IReadOnlyList<ConnectionProfile>? savedConnections = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _securityRuntime = securityRuntime;
        _gitClient = gitClient;
        ExpectedRevision = expectedRevision;
        ConnectionKinds = Enum.GetValues<ConnectionKind>();
        AuthenticationChoices = Enum.GetValues<ConnectionAuthenticationChoice>();
        HostKeyPolicies =
        [
            SshHostKeyPolicy.Strict,
            SshHostKeyPolicy.AcceptNew,
            SshHostKeyPolicy.InsecureIgnore,
        ];

        _savedConnections = savedConnections ?? [];
        if (existing is null)
        {
            _id = ConnectionId.New();
            _schemaVersion = ConnectionProfile.CurrentSchemaVersion;
            _environment = [];
            _tags = [];
            SavedSshSources = BuildSavedSshSources(savedConnections);
            return;
        }

        _id = existing.Id;
        SavedSshSources = BuildSavedSshSources(savedConnections);
        _schemaVersion = existing.SchemaVersion;
        _environment = existing.Startup.Environment;
        _tags = existing.Tags;
        _name = existing.Name;
        _kind = existing.ConnectionKind;
        _opensGitRepository = existing.PreferredPanel == PanelKind.Git;
        _startupDirectory = existing.Startup.Directory ?? string.Empty;
        _startupCommand = existing.Startup.Command ?? string.Empty;
        _keepAliveEnabled = existing.KeepAlive.Enabled;
        if (_keepAliveEnabled)
        {
            _keepAliveSeconds = checked((int)existing.KeepAlive.Interval.TotalSeconds);
            _keepAliveFailures = existing.KeepAlive.MaximumFailures;
        }

        _hostKeyPolicy = existing.HostKeyPolicy == SshHostKeyPolicy.NotApplicable
            ? SshHostKeyPolicy.Strict
            : existing.HostKeyPolicy;
        if (existing.HostConnectionId is { } hostId)
        {
            // The profile delegates its endpoint: the stored endpoint and
            // authentication are stand-ins, so the fields stay empty and the
            // selector points at the referenced connection instead.
            var referenced = SavedSshSources
                .FirstOrDefault(option => option.Profile?.Id == hostId);
            if (referenced is not null)
            {
                _selectedSavedSshSource = referenced;
                return;
            }

            _testStatus = "Referenced connection missing";
            _testDetail =
                "The saved SSH connection this profile referenced no longer exists. "
                + "Enter the endpoint manually or choose another saved connection.";
            return;
        }

        LoadEndpoint(existing.Endpoint);
        LoadAuthentication(existing.Authentication);
    }

    public long? ExpectedRevision { get; }

    public bool IsEditing => ExpectedRevision is not null;

    public string EditorTitle => IsEditing ? "Edit connection" : "New connection";

    public IReadOnlyList<ConnectionKind> ConnectionKinds { get; }

    public IReadOnlyList<ConnectionAuthenticationChoice> AuthenticationChoices { get; }

    public IReadOnlyList<SshHostKeyPolicy> HostKeyPolicies { get; }

    /// <summary>
    /// "Enter manually" plus every saved SSH connection this Git · SSH profile
    /// can reference for its endpoint. The profile being edited never offers
    /// itself, and delegating profiles are excluded so references never chain.
    /// </summary>
    public IReadOnlyList<SavedSshSourceOption> SavedSshSources { get; }

    /// <summary>
    /// Selecting a saved connection makes the profile a reference: the saved
    /// profile stays the live source of the endpoint and credentials, so later
    /// edits to it apply to this connection. Nothing is copied into the fields;
    /// "Enter manually" returns to a standalone profile built from them.
    /// </summary>
    public SavedSshSourceOption SelectedSavedSshSource
    {
        get => _selectedSavedSshSource;
        set
        {
            if (SetProperty(ref _selectedSavedSshSource, value))
            {
                OnPropertyChanged(nameof(ShowsSshEndpointFields));
                OnPropertyChanged(nameof(SavedSshSourceSummary));
            }
        }
    }

    /// <summary>
    /// The selector belongs to the Git · SSH type only, and only when there is
    /// at least one saved SSH connection to reference.
    /// </summary>
    public bool ShowsSavedSshSources => OpensGitRepository && IsSsh && SavedSshSources.Count > 1;

    /// <summary>
    /// With a saved connection chosen the referenced endpoint speaks for
    /// itself; the fields fold away and return on "Enter manually", untouched.
    /// </summary>
    public bool ShowsSshEndpointFields =>
        !ShowsSavedSshSources || SelectedSavedSshSource.Profile is null;

    /// <summary>
    /// The referenced connection's current endpoint — the options carry the
    /// saved profiles from the catalog at editor-open time, so the summary
    /// always shows what the reference resolves to, not a stored copy.
    /// </summary>
    public string SavedSshSourceSummary =>
        SelectedSavedSshSource.Profile is { Endpoint: ConnectionEndpoint.Ssh ssh }
            ? EndpointSummary(ssh.Username ?? string.Empty, ssh.Host, ssh.Port)
            : EndpointSummary(Username.Trim(), Host.Trim(), Port);

    private static string EndpointSummary(string username, string host, int port) =>
        username.Length > 0
            ? $"{username}@{host} · port {port}"
            : $"{host} · port {port}";

    public ObservableCollection<ConnectionDiagnosticItemViewModel> Diagnostics { get; } = [];

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ConnectionKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsLocal));
                OnPropertyChanged(nameof(IsSsh));
                OnPropertyChanged(nameof(IsDocker));
                OnPropertyChanged(nameof(IsWsl));
                OnPropertyChanged(nameof(ShowsLocalShell));
                OnPropertyChanged(nameof(CanBrowseRepository));
                OnPropertyChanged(nameof(ShowsSavedSshSources));
                OnPropertyChanged(nameof(ShowsSshEndpointFields));
            }
        }
    }

    /// <summary>
    /// Git connection types reuse the Local and SSH endpoints but save a
    /// profile whose preferred panel is Git and whose startup directory is
    /// the repository path.
    /// </summary>
    public bool OpensGitRepository
    {
        get => _opensGitRepository;
        set
        {
            if (SetProperty(ref _opensGitRepository, value))
            {
                OnPropertyChanged(nameof(ShowsLocalShell));
                OnPropertyChanged(nameof(ShowsStartupOptions));
                OnPropertyChanged(nameof(CanBrowseRepository));
                OnPropertyChanged(nameof(ShowsSavedSshSources));
                OnPropertyChanged(nameof(ShowsSshEndpointFields));
            }
        }
    }

    public string LocalShell
    {
        get => _localShell;
        set => SetProperty(ref _localShell, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Container
    {
        get => _container;
        set => SetProperty(ref _container, value);
    }

    public string DockerContext
    {
        get => _dockerContext;
        set => SetProperty(ref _dockerContext, value);
    }

    public string Distribution
    {
        get => _distribution;
        set => SetProperty(ref _distribution, value);
    }

    public string StartupDirectory
    {
        get => _startupDirectory;
        set
        {
            if (SetProperty(ref _startupDirectory, value))
            {
                OnPropertyChanged(nameof(RepositoryPath));
            }
        }
    }

    /// <summary>
    /// The Git types label the startup directory as the repository path: one
    /// stored field, so switching between a Git and a plain terminal type
    /// keeps the directory the user typed.
    /// </summary>
    public string RepositoryPath
    {
        get => StartupDirectory;
        set => StartupDirectory = value;
    }

    public string StartupCommand
    {
        get => _startupCommand;
        set => SetProperty(ref _startupCommand, value);
    }

    public ConnectionAuthenticationChoice Authentication
    {
        get => _authentication;
        set
        {
            if (SetProperty(ref _authentication, value))
            {
                OnPropertyChanged(nameof(UsesSecretReference));
                OnPropertyChanged(nameof(UsesPassphraseReference));
            }
        }
    }

    public string SecretReference
    {
        get => _secretReference;
        set => SetProperty(ref _secretReference, value);
    }

    public string PassphraseSecretReference
    {
        get => _passphraseSecretReference;
        set => SetProperty(ref _passphraseSecretReference, value);
    }

    public SshHostKeyPolicy HostKeyPolicy
    {
        get => _hostKeyPolicy;
        set => SetProperty(ref _hostKeyPolicy, value);
    }

    public bool KeepAliveEnabled
    {
        get => _keepAliveEnabled;
        set => SetProperty(ref _keepAliveEnabled, value);
    }

    public int KeepAliveSeconds
    {
        get => _keepAliveSeconds;
        set => SetProperty(ref _keepAliveSeconds, value);
    }

    public int KeepAliveFailures
    {
        get => _keepAliveFailures;
        set => SetProperty(ref _keepAliveFailures, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    public string TestStatus
    {
        get => _testStatus;
        private set => SetProperty(ref _testStatus, value);
    }

    public string TestDetail
    {
        get => _testDetail;
        private set => SetProperty(ref _testDetail, value);
    }

    public SshHostKeyReview? HostKeyReview
    {
        get => _hostKeyReview;
        private set
        {
            if (SetProperty(ref _hostKeyReview, value))
            {
                OnPropertyChanged(nameof(HasHostKeyReview));
                OnPropertyChanged(nameof(HostKeyReviewTitle));
                OnPropertyChanged(nameof(TrustedHostKeyFingerprint));
                OnPropertyChanged(nameof(TrustHostKeyLabel));
                OnPropertyChanged(nameof(CanTrustHostKey));
            }
        }
    }

    public bool IsLocal => Kind == ConnectionKind.Local;

    public bool IsSsh => Kind == ConnectionKind.Ssh;

    public bool IsDocker => Kind == ConnectionKind.Docker;

    public bool IsWsl => Kind == ConnectionKind.Wsl;

    /// <summary>A Git · Local repository needs only the repository path.</summary>
    public bool ShowsLocalShell => IsLocal && !OpensGitRepository;

    /// <summary>
    /// Git types replace the startup card with the repository-path card. The
    /// hidden startup command and keep-alive values still round-trip.
    /// </summary>
    public bool ShowsStartupOptions => !OpensGitRepository;

    /// <summary>
    /// Browsing lists directories over the connection target itself, so it
    /// needs the Git client and an endpoint the Git panel supports.
    /// </summary>
    public bool CanBrowseRepository => _gitClient is not null && (IsLocal || IsSsh);

    public bool UsesSecretReference => Authentication is
        ConnectionAuthenticationChoice.Password or ConnectionAuthenticationChoice.PrivateKey;

    public bool UsesPassphraseReference => Authentication == ConnectionAuthenticationChoice.PrivateKey;

    public bool HasHostKeyReview => HostKeyReview is not null;

    public string HostKeyReviewTitle => HostKeyReview?.Disposition switch
    {
        SshHostKeyDisposition.Unknown => "Unknown SSH host key",
        SshHostKeyDisposition.Changed => "SSH host key changed",
        SshHostKeyDisposition.Trusted => "SSH host key trusted",
        SshHostKeyDisposition.VerificationDisabled => "SSH host-key verification disabled",
        _ => string.Empty,
    };

    public string TrustedHostKeyFingerprint => HostKeyReview?.Trusted?.Sha256Fingerprint
        ?? "No previously trusted key";

    public string TrustHostKeyLabel => HostKeyReview?.Disposition == SshHostKeyDisposition.Changed
        ? "Replace trusted key…"
        : "Trust host key…";

    public bool CanTrustHostKey => !IsTesting
        && HostKeyReview?.Disposition is SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed;

    public ConnectionEditorSaveRequest CreateSaveRequest() =>
        new(BuildProfile(), ExpectedRevision);

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting)
        {
            return;
        }

        ConnectionProfile profile;
        try
        {
            profile = BuildProfile();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            TestStatus = "Validation failed";
            TestDetail = exception.Message;
            return;
        }

        // Diagnostics run against what opening will use: a reference profile
        // resolves to the referenced connection's endpoint and credentials.
        if (profile.HostConnectionId is not null)
        {
            var resolved = profile.ResolveHostConnection(
                id => _savedConnections.FirstOrDefault(saved => saved.Id == id));
            if (resolved is null)
            {
                TestStatus = "Validation failed";
                TestDetail = "The referenced connection no longer exists.";
                return;
            }

            profile = resolved;
        }

        IsTesting = true;
        OnPropertyChanged(nameof(CanTrustHostKey));
        TestStatus = "Testing connection";
        TestDetail = "Validating profile…";
        Diagnostics.Clear();
        HostKeyReview = null;
        var progress = new Progress<ConnectionProgress>(item =>
        {
            if (IsTesting)
            {
                TestStatus = ProgressTitle(item.Stage);
                TestDetail = item.Message;
            }
        });
        try
        {
            if (_securityRuntime is not null)
            {
                await RunDiagnosticsAsync(profile, progress, cancellationToken);
                return;
            }

            var result = await _runtime.TestAsync(profile, progress, cancellationToken);
            switch (result)
            {
                case ConnectionRuntimeResult<ConnectionTestReport>.Success success:
                    TestStatus = success.Value.EndpointReached
                        ? "Connection succeeded"
                        : "Configuration validated";
                    TestDetail = success.Value.Verification switch
                    {
                        ConnectionTestVerification.RuntimeAvailable =>
                            "The required local runtime is available.",
                        ConnectionTestVerification.ConfigurationValidated =>
                            "Runtime and credential references are valid. The stored credential is claimed only when a terminal starts.",
                        ConnectionTestVerification.EndpointAuthenticated =>
                            "The SSH endpoint accepted authentication.",
                        ConnectionTestVerification.ContainerReachable =>
                            "The Docker container is reachable.",
                        ConnectionTestVerification.DistributionReachable =>
                            "The WSL distribution is reachable.",
                        _ => throw new ArgumentOutOfRangeException(nameof(result)),
                    };
                    await ProbeGitRepositoryAsync(profile, cancellationToken);
                    break;
                case ConnectionRuntimeResult<ConnectionTestReport>.Failure failure:
                    TestStatus = "Test failed";
                    TestDetail = $"{failure.Error.Message} {RecoveryText(failure.Error.RecoveryAction)}".Trim();
                    break;
                default:
                    throw new InvalidOperationException("The connection test result is invalid.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TestStatus = "Test cancelled";
            TestDetail = "The connection test was cancelled.";
        }
        catch (Exception)
        {
            TestStatus = "Test failed";
            TestDetail = "The connection runtime could not complete the test.";
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(CanTrustHostKey));
        }
    }

    public async Task TrustHostKeyAsync(CancellationToken cancellationToken)
    {
        if (_securityRuntime is null || HostKeyReview is not { } review || IsTesting)
        {
            return;
        }

        var action = review.RequiresExplicitReplacement
            ? SshHostKeyTrustAction.ReplaceChanged
            : SshHostKeyTrustAction.TrustNew;
        IsTesting = true;
        OnPropertyChanged(nameof(CanTrustHostKey));
        try
        {
            var result = await _securityRuntime.TrustSshHostKeyAsync(
                new SshHostKeyTrustRequest(review.Id, review.ConnectionId, action),
                cancellationToken);
            if (result is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
            {
                TestStatus = "Host key not trusted";
                TestDetail = $"{failure.Error.Message} {RecoveryText(failure.Error.RecoveryAction)}".Trim();
                return;
            }

            HostKeyReview = ((ConnectionRuntimeResult<SshHostKeyReview>.Success)result).Value;
            TestStatus = "Host key trusted";
            TestDetail = "The trust decision was stored. Run diagnostics again to verify the endpoint.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TestStatus = "Host-key review cancelled";
            TestDetail = "The trusted host key was not changed.";
        }
        catch (Exception)
        {
            TestStatus = "Host key not trusted";
            TestDetail = "The trust decision could not be stored.";
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(CanTrustHostKey));
        }
    }

    private async Task RunDiagnosticsAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress> progress,
        CancellationToken cancellationToken)
    {
        var result = await _securityRuntime!.DiagnoseAsync(profile, progress, cancellationToken);
        if (result is ConnectionRuntimeResult<ConnectionDiagnosticsReport>.Failure failure)
        {
            TestStatus = "Diagnostics failed";
            TestDetail = $"{failure.Error.Message} {RecoveryText(failure.Error.RecoveryAction)}".Trim();
            return;
        }

        var report = ((ConnectionRuntimeResult<ConnectionDiagnosticsReport>.Success)result).Value;
        foreach (var item in report.Items)
        {
            Diagnostics.Add(ConnectionDiagnosticItemViewModel.From(item));
        }

        HostKeyReview = report.HostKeyReview;
        if (report.Failure is { } diagnosticFailure)
        {
            TestStatus = diagnosticFailure.Code switch
            {
                ConnectionRuntimeErrorCode.UnknownHostKey => "Unknown host key",
                ConnectionRuntimeErrorCode.HostKeyChanged => "Host key changed",
                ConnectionRuntimeErrorCode.AuthenticationRequired => "Authentication required",
                ConnectionRuntimeErrorCode.AuthenticationFailed => "Authentication failed",
                ConnectionRuntimeErrorCode.PermissionDenied => "Permission denied",
                ConnectionRuntimeErrorCode.Timeout => "Connection timed out",
                ConnectionRuntimeErrorCode.Offline => "Connection unavailable",
                _ => "Diagnostics found a problem",
            };
            TestDetail = $"{diagnosticFailure.Message} {RecoveryText(diagnosticFailure.RecoveryAction)}".Trim();
            return;
        }

        TestStatus = report.Verification == ConnectionTestVerification.EndpointAuthenticated
            ? "Connection succeeded"
            : "Diagnostics passed";
        TestDetail = report.Verification switch
        {
            ConnectionTestVerification.EndpointAuthenticated => "The SSH endpoint accepted authentication.",
            ConnectionTestVerification.ContainerReachable => "The Docker container is reachable.",
            ConnectionTestVerification.DistributionReachable => "The WSL distribution is reachable.",
            ConnectionTestVerification.RuntimeAvailable => "The required local runtime is available.",
            ConnectionTestVerification.ConfigurationValidated => "The connection configuration is valid.",
            null => "All completed diagnostics passed.",
            _ => throw new ArgumentOutOfRangeException(nameof(report.Verification)),
        };
        await ProbeGitRepositoryAsync(profile, cancellationToken);
    }

    /// <summary>
    /// The saved connection a Git · SSH profile references, when one is
    /// chosen. The selection is honored even while the selector is hidden by a
    /// type switch only if the current type is still Git · SSH.
    /// </summary>
    private ConnectionProfile? ReferencedSshSource =>
        OpensGitRepository && IsSsh ? SelectedSavedSshSource.Profile : null;

    private ConnectionProfile BuildProfile()
    {
        // A reference profile stores no endpoint or credentials of its own:
        // the stand-in endpoint satisfies the schema, and resolution swaps in
        // the referenced connection's endpoint at every use.
        var reference = ReferencedSshSource;
        var endpoint = reference is null
            ? BuildEndpoint()
            : ConnectionProfile.DelegatedSshEndpoint;
        var authentication = reference is null && Kind == ConnectionKind.Ssh
            ? BuildAuthentication()
            : new ConnectionAuthentication.None();
        var keepAlive = KeepAliveEnabled
            ? ConnectionKeepAlive.EnabledEvery(
                TimeSpan.FromSeconds(KeepAliveSeconds),
                KeepAliveFailures)
            : ConnectionKeepAlive.Disabled;
        var directory = OpensGitRepository
            ? Required(RepositoryPath, "Repository path")
            : Optional(StartupDirectory);
        return new ConnectionProfile(
            _id,
            _schemaVersion,
            Required(Name, "Connection name"),
            endpoint,
            authentication,
            new ConnectionStartup(directory, _environment, Optional(StartupCommand)),
            keepAlive,
            reference is not null
                ? SshHostKeyPolicy.Strict
                : Kind == ConnectionKind.Ssh
                    ? HostKeyPolicy
                    : SshHostKeyPolicy.NotApplicable,
            _tags,
            OpensGitRepository ? PanelKind.Git : null,
            reference?.Id);
    }

    private ConnectionEndpoint BuildEndpoint() => Kind switch
    {
        ConnectionKind.Local => new ConnectionEndpoint.Local(Optional(LocalShell)),
        ConnectionKind.Ssh => new ConnectionEndpoint.Ssh(
            Required(Host, "SSH host"),
            Port,
            Optional(Username)),
        ConnectionKind.Docker => new ConnectionEndpoint.Docker(
            Required(Container, "Docker container"),
            Optional(DockerContext)),
        ConnectionKind.Wsl => new ConnectionEndpoint.Wsl(
            Required(Distribution, "WSL distribution"),
            Optional(Username)),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
    };

    /// <summary>
    /// A picker over the connection described by the current fields, so the
    /// repository is browsed where it lives — the target host, not this
    /// machine. The probe profile needs only the endpoint and credentials;
    /// the connection name may still be blank while browsing.
    /// </summary>
    public GitRepositoryPickerViewModel CreateRepositoryPicker()
    {
        if (_gitClient is null)
        {
            throw new InvalidOperationException("Git support is unavailable in this build.");
        }

        // A chosen saved connection is the endpoint: the browse probe uses it
        // directly, the same resolution every other use of the reference gets.
        var reference = ReferencedSshSource;
        var probe = new ConnectionProfile(
            _id,
            _schemaVersion,
            "Repository browse",
            reference?.Endpoint ?? BuildEndpoint(),
            reference is not null
                ? reference.Authentication
                : Kind == ConnectionKind.Ssh
                    ? BuildAuthentication()
                    : new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            reference?.HostKeyPolicy
                ?? (Kind == ConnectionKind.Ssh ? HostKeyPolicy : SshHostKeyPolicy.NotApplicable));
        return new GitRepositoryPickerViewModel(_gitClient, probe, Optional(RepositoryPath));
    }

    /// <summary>
    /// After the endpoint diagnostics pass, a Git type also proves the
    /// repository itself opens on the target.
    /// </summary>
    private async Task ProbeGitRepositoryAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (!OpensGitRepository
            || _gitClient is null
            || profile.Startup.Directory is not { } path)
        {
            return;
        }

        TestStatus = "Opening repository";
        TestDetail = $"Resolving {path} on the target…";
        var result = await _gitClient.OpenRepositoryAsync(profile, path, cancellationToken);
        if (result is GitResult<GitRepositoryHandle>.Failure failure)
        {
            TestStatus = "Repository not opened";
            TestDetail = failure.Error.Message;
            return;
        }

        var handle = ((GitResult<GitRepositoryHandle>.Success)result).Value;
        TestStatus = "Repository opened";
        TestDetail = $"Git repository at {handle.WorkingTreeRoot}.";
    }

    private ConnectionAuthentication BuildAuthentication() => Authentication switch
    {
        ConnectionAuthenticationChoice.SystemConfiguration =>
            new ConnectionAuthentication.None(),
        ConnectionAuthenticationChoice.SshAgent => new ConnectionAuthentication.SshAgent(),
        ConnectionAuthenticationChoice.Password => new ConnectionAuthentication.Password(
            new SecretRef(Required(SecretReference, "Password secret reference"))),
        ConnectionAuthenticationChoice.PrivateKey => new ConnectionAuthentication.PrivateKey(
            new SecretRef(Required(SecretReference, "Private-key secret reference")),
            Optional(PassphraseSecretReference) is { } passphrase
                ? new SecretRef(passphrase)
                : null),
        _ => throw new ArgumentOutOfRangeException(nameof(Authentication), Authentication, null),
    };

    private void LoadEndpoint(ConnectionEndpoint endpoint)
    {
        switch (endpoint)
        {
            case ConnectionEndpoint.Local local:
                _localShell = local.ShellPath ?? string.Empty;
                break;
            case ConnectionEndpoint.Ssh ssh:
                _host = ssh.Host;
                _port = ssh.Port;
                _username = ssh.Username ?? string.Empty;
                break;
            case ConnectionEndpoint.Docker docker:
                _container = docker.Container;
                _dockerContext = docker.Context ?? string.Empty;
                break;
            case ConnectionEndpoint.Wsl wsl:
                _distribution = wsl.Distribution;
                _username = wsl.Username ?? string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint));
        }
    }

    private IReadOnlyList<SavedSshSourceOption> BuildSavedSshSources(
        IReadOnlyList<ConnectionProfile>? savedConnections)
    {
        var options = new List<SavedSshSourceOption> { SavedSshSourceOption.Manual };
        if (savedConnections is not null)
        {
            options.AddRange(savedConnections
                .Where(profile => profile.ConnectionKind == ConnectionKind.Ssh
                    && profile.Id != _id
                    && profile.HostConnectionId is null)
                .Select(profile => new SavedSshSourceOption(profile.Name, profile)));
        }

        return options.AsReadOnly();
    }

    private void LoadAuthentication(ConnectionAuthentication authentication)
    {
        switch (authentication)
        {
            case ConnectionAuthentication.None:
                _authentication = ConnectionAuthenticationChoice.SystemConfiguration;
                break;
            case ConnectionAuthentication.SshAgent:
                _authentication = ConnectionAuthenticationChoice.SshAgent;
                break;
            case ConnectionAuthentication.Password password:
                _authentication = ConnectionAuthenticationChoice.Password;
                _secretReference = password.PasswordSecret.Value;
                break;
            case ConnectionAuthentication.PrivateKey privateKey:
                _authentication = ConnectionAuthenticationChoice.PrivateKey;
                _secretReference = privateKey.PrivateKeySecret.Value;
                _passphraseSecretReference = privateKey.PassphraseSecret?.Value ?? string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(authentication));
        }
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

    private static string ProgressTitle(ConnectionProgressStage stage) => stage switch
    {
        ConnectionProgressStage.ValidatingProfile => "Validating profile",
        ConnectionProgressStage.DetectingRuntime => "Detecting runtime",
        ConnectionProgressStage.ResolvingCredentials => "Checking credentials",
        ConnectionProgressStage.BuildingLaunchPlan => "Building launch plan",
        ConnectionProgressStage.InspectingHostKey => "Inspecting host key",
        ConnectionProgressStage.Authenticating => "Authenticating",
        ConnectionProgressStage.ProbingEndpoint => "Probing endpoint",
        ConnectionProgressStage.Reconnecting => "Reconnecting",
        ConnectionProgressStage.Completed => "Test complete",
        _ => "Testing connection",
    };

    private static string RecoveryText(ConnectionRecoveryAction action) => action switch
    {
        ConnectionRecoveryAction.EditProfile => "Edit the profile and retry.",
        ConnectionRecoveryAction.InstallRuntime => "Install the required runtime and retry.",
        ConnectionRecoveryAction.UnlockSecretVault => "Unlock the credential vault and retry.",
        ConnectionRecoveryAction.ProvideAuthentication => "Update the authentication settings.",
        ConnectionRecoveryAction.ReviewHostKey => "Review the remote host key before reconnecting.",
        ConnectionRecoveryAction.GrantPermission => "Grant the required operating-system permission.",
        ConnectionRecoveryAction.Retry or ConnectionRecoveryAction.Reconnect => "Retry the test.",
        ConnectionRecoveryAction.SelectContainer => "Choose a running container.",
        ConnectionRecoveryAction.SelectDistribution => "Choose an installed WSL distribution.",
        _ => string.Empty,
    };
}
