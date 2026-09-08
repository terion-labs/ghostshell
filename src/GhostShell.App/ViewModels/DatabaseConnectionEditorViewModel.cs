using System.Globalization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// What saving the database form means: the shell persists the profile (and
/// optionally moves the password into the OS vault) through the catalog's
/// database save path rather than a plain definition write.
/// </summary>
public sealed record DatabaseConnectionSaveRequest(
    DatabaseConnectionProfileId? ExistingId,
    string Name,
    string DriverId,
    DatabaseConnectionDetails Details,
    bool StorePassword,
    ConnectionId? TunnelConnectionId,
    DatabaseInlineTunnelRequest? InlineTunnel = null,
    DatabaseConnectionProfileId? DraftId = null);

/// <summary>
/// An SSH tunnel that lives only inside the database profile. A null password
/// with <see cref="UseAgent"/> false means "keep the stored one".
/// </summary>
public sealed record DatabaseInlineTunnelRequest(
    string Host,
    int Port,
    string? Username,
    bool UseAgent,
    string? Password,
    SshHostKeyPolicy HostKeyPolicy = SshHostKeyPolicy.Strict);

public sealed record DatabaseTunnelOption(
    ConnectionId? Id,
    string Name,
    bool IsInline = false)
{
    public string DisplayName => Id is null && !IsInline ? "No tunnel" : Name;
}

/// <summary>
/// Structural editor for a saved database connection: driver, endpoint or file,
/// credentials, and an optional SSH tunnel — saved or living only in this
/// profile. Fields and a pasted connection string (URL or keyword form) are two
/// views of the same connection; the string never stores the password.
/// </summary>
public sealed class DatabaseConnectionEditorViewModel : ObservableObject
{
    public const string AgentAuthentication = "SSH agent";
    public const string PasswordAuthentication = "Password (OS keychain)";

    private readonly IDatabaseConnectionCatalog _client;
    private readonly IReadOnlyList<ConnectionProfile> _connections;
    private readonly DatabaseConnectionProfileId? _existingId;
    private readonly ConnectionProfile? _existingInlineTunnel;
    private readonly Func<CancellationToken, Task<string?>>? _storedPasswordResolver;
    private readonly IConnectionSecurityRuntime? _securityRuntime;
    private readonly DatabaseConnectionProfileId _draftProfileId;
    private SshHostKeyPolicy _tunnelHostKeyPolicy = SshHostKeyPolicy.Strict;
    private SshHostKeyReview? _hostKeyReview;
    private DatabaseDriverDescriptor _selectedDriver;
    private DatabaseTunnelOption _selectedTunnel;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private string _port = string.Empty;
    private string _databaseName = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _filePath = string.Empty;
    private string _options = string.Empty;
    private bool _storePassword;
    private bool _useConnectionString;
    private string _connectionStringInput = string.Empty;
    private string _tunnelHost = string.Empty;
    private string _tunnelPort = string.Empty;
    private string _tunnelUsername = string.Empty;
    private string _tunnelPassword = string.Empty;
    private string _tunnelAuthentication = AgentAuthentication;
    private string _testStatus = "Not tested";
    private string _testDetail =
        "Save is allowed without a test; connecting validates the server again.";
    private bool _isTesting;

    public DatabaseConnectionEditorViewModel(
        IDatabaseConnectionCatalog client,
        IReadOnlyList<ConnectionProfile> connections,
        DatabaseConnectionProfile? existing = null,
        Func<CancellationToken, Task<string?>>? storedPasswordResolver = null,
        IConnectionSecurityRuntime? securityRuntime = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _storedPasswordResolver = storedPasswordResolver;
        _securityRuntime = securityRuntime;
        _draftProfileId = existing?.Id ?? DatabaseConnectionProfileId.New();
        Drivers = client.Drivers;
        _selectedDriver = Drivers[0];
        TunnelOptions = BuildTunnelOptions(connections, existing);
        _selectedTunnel = TunnelOptions[0];

        if (existing is null)
        {
            return;
        }

        _existingId = existing.Id;
        _existingInlineTunnel = existing.InlineTunnel;
        _tunnelHostKeyPolicy = existing.InlineTunnel?.HostKeyPolicy ?? SshHostKeyPolicy.Strict;
        _name = existing.Name;
        _selectedDriver = Drivers.FirstOrDefault(item => string.Equals(item.Id, existing.DriverId, StringComparison.Ordinal))
            ?? Drivers[0];
        _selectedTunnel = existing.InlineTunnel is not null
            ? TunnelOptions.First(item => item.IsInline)
            : TunnelOptions.FirstOrDefault(item =>
                    !item.IsInline && item.Id == existing.TunnelConnectionId)
                ?? TunnelOptions[0];
        HasStoredPassword = existing.PasswordSecret is not null;
        var details = client.ParseConnectionDetails(
            existing.DriverId,
            existing.ConnectionString);
        _host = details.Host ?? string.Empty;
        _port = details.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _databaseName = details.Database ?? string.Empty;
        _username = details.Username ?? string.Empty;
        _filePath = details.FilePath ?? string.Empty;
        _options = details.Options ?? string.Empty;

        if (existing.InlineTunnel is { Endpoint: ConnectionEndpoint.Ssh ssh } inline)
        {
            _tunnelHost = ssh.Host;
            _tunnelPort = ssh.Port.ToString(CultureInfo.InvariantCulture);
            _tunnelUsername = ssh.Username ?? string.Empty;
            HasStoredTunnelPassword =
                inline.Authentication is ConnectionAuthentication.Password;
            _tunnelAuthentication = HasStoredTunnelPassword
                ? PasswordAuthentication
                : AgentAuthentication;
        }
    }

    public bool IsEditing => _existingId is not null;

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    public IReadOnlyList<DatabaseTunnelOption> TunnelOptions { get; }

    public IReadOnlyList<string> TunnelAuthenticationChoices { get; } =
        [AgentAuthentication, PasswordAuthentication];

    /// <summary>A stored keychain password exists and is kept unless replaced.</summary>
    public bool HasStoredPassword { get; }

    /// <summary>The inline tunnel keeps a stored keychain password.</summary>
    public bool HasStoredTunnelPassword { get; }

    public DatabaseDriverDescriptor SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (SetProperty(ref _selectedDriver, value))
            {
                OnPropertyChanged(nameof(IsFileBased));
                OnPropertyChanged(nameof(IsServerBased));
                OnPropertyChanged(nameof(PortPlaceholder));
                OnPropertyChanged(nameof(DatabaseLabel));
                OnPropertyChanged(nameof(ConnectionStringHint));
                OnPropertyChanged(nameof(ShowsTunnel));
            }
        }
    }

    public DatabaseTunnelOption SelectedTunnel
    {
        get => _selectedTunnel;
        set
        {
            if (SetProperty(ref _selectedTunnel, value) && value is not null)
            {
                OnPropertyChanged(nameof(IsInlineTunnel));
            }
        }
    }

    public bool IsInlineTunnel => SelectedTunnel.IsInline;

    public IReadOnlyList<SshHostKeyPolicy> TunnelHostKeyPolicies { get; } =
        [SshHostKeyPolicy.Strict, SshHostKeyPolicy.AcceptNew, SshHostKeyPolicy.InsecureIgnore];

    public SshHostKeyPolicy TunnelHostKeyPolicy
    {
        get => _tunnelHostKeyPolicy;
        set => SetProperty(ref _tunnelHostKeyPolicy, value);
    }

    public SshHostKeyReview? HostKeyReview => _hostKeyReview;

    public bool HasHostKeyReview => _hostKeyReview?.Disposition is SshHostKeyDisposition.Unknown or SshHostKeyDisposition.Changed;

    public bool IsFileBased => SelectedDriver.IsFileBased;

    public bool IsServerBased => !SelectedDriver.IsFileBased;

    /// <summary>File engines have no endpoint, so nothing to tunnel.</summary>
    public bool ShowsTunnel => IsServerBased;

    /// <summary>The engine's usual port, shown as the empty field's hint.</summary>
    public string PortPlaceholder =>
        SelectedDriver.DefaultPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>What this engine calls the thing it connects to.</summary>
    public string DatabaseLabel => SelectedDriver.DatabaseLabel;

    public string ConnectionStringHint => SelectedDriver.ConnectionStringHint;

    /// <summary>
    /// True when the connection is described by one pasted string (URL or
    /// keyword form) instead of the structured fields. Switching back to the
    /// fields reads the string into them, so nothing typed is lost.
    /// </summary>
    public bool UseConnectionString
    {
        get => _useConnectionString;
        set
        {
            if (!SetProperty(ref _useConnectionString, value))
            {
                return;
            }

            OnPropertyChanged(nameof(UseFields));
            if (!value)
            {
                AdoptConnectionStringIntoFields();
            }
            else if (string.IsNullOrWhiteSpace(_connectionStringInput))
            {
                PrefillConnectionStringFromFields();
            }
        }
    }

    public bool UseFields => !UseConnectionString;

    public string ConnectionStringInput
    {
        get => _connectionStringInput;
        set => SetProperty(ref _connectionStringInput, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string DatabaseName
    {
        get => _databaseName;
        set => SetProperty(ref _databaseName, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public string Options
    {
        get => _options;
        set => SetProperty(ref _options, value);
    }

    public bool StorePassword
    {
        get => _storePassword;
        set => SetProperty(ref _storePassword, value);
    }

    public string TunnelHost
    {
        get => _tunnelHost;
        set => SetProperty(ref _tunnelHost, value);
    }

    public string TunnelPort
    {
        get => _tunnelPort;
        set => SetProperty(ref _tunnelPort, value);
    }

    public string TunnelUsername
    {
        get => _tunnelUsername;
        set => SetProperty(ref _tunnelUsername, value);
    }

    public string TunnelPassword
    {
        get => _tunnelPassword;
        set => SetProperty(ref _tunnelPassword, value);
    }

    public string TunnelAuthentication
    {
        get => _tunnelAuthentication;
        set
        {
            if (SetProperty(ref _tunnelAuthentication, value))
            {
                OnPropertyChanged(nameof(TunnelUsesPassword));
            }
        }
    }

    public bool TunnelUsesPassword => string.Equals(TunnelAuthentication, PasswordAuthentication, StringComparison.Ordinal);

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

    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    public DatabaseConnectionSaveRequest CreateSaveRequest()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Connection name is required.");
        }

        var details = ResolveDetails();

        // The connection string must round-trip through the driver before the
        // request leaves the dialog, so malformed options fail here as a
        // validation error rather than during the save.
        _ = _client.BuildConnectionString(SelectedDriver.Id, details);
        var inlineTunnel = IsServerBased && IsInlineTunnel
            ? CreateInlineTunnelRequest()
            : null;
        return new DatabaseConnectionSaveRequest(
            _existingId,
            Name.Trim(),
            SelectedDriver.Id,
            details,
            StorePassword,
            IsInlineTunnel ? null : SelectedTunnel.Id,
            inlineTunnel,
            _draftProfileId);
    }

    /// <summary>
    /// The bounded reachability test: open one connection, read the session
    /// facts, close it. Runs through the same tunnel the connection would use.
    /// </summary>
    public async Task TestAsync(CancellationToken cancellationToken)
    {
        if (IsTesting)
        {
            return;
        }

        IsTesting = true;
        TestStatus = "Testing connection";
        TestDetail = "Validating connection parameters…";
        try
        {
            await TestConnectionAsync(cancellationToken);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        DatabaseConnectionSaveRequest request;
        try
        {
            request = CreateSaveRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            TestStatus = "Validation failed";
            TestDetail = exception.Message;
            return;
        }

        ConnectionProfile? tunnel;
        try
        {
            tunnel = ResolveTestTunnel(request);
        }
        catch (InvalidOperationException exception)
        {
            TestStatus = "Test unavailable";
            TestDetail = exception.Message;
            return;
        }

        var password = request.Details.Password;
        if (tunnel?.HostKeyPolicy == SshHostKeyPolicy.Strict && _securityRuntime is not null)
        {
            var prepared = await _securityRuntime.PrepareSshHostKeyAsync(tunnel, null, cancellationToken);
            if (prepared is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
            {
                TestStatus = "Host key unavailable";
                TestDetail = failure.Error.Message;
                return;
            }
            _hostKeyReview = ((ConnectionRuntimeResult<SshHostKeyReview>.Success)prepared).Value;
            OnPropertyChanged(nameof(HostKeyReview));
            OnPropertyChanged(nameof(HasHostKeyReview));
            if (HasHostKeyReview)
            {
                TestStatus = "Review SSH host key";
                TestDetail = "Verify the fingerprint through a trusted channel before connecting.";
                return;
            }
        }
        if (password is null && HasStoredPassword && _storedPasswordResolver is not null)
        {
            password = await _storedPasswordResolver(cancellationToken);
        }

        var connectionString = _client.BuildConnectionString(
            request.DriverId,
            request.Details with { Password = password });
        TestStatus = "Testing connection";
        TestDetail = "Opening a connection and reading the server's session facts…";
        try
        {
            var info = await _client.DescribeSessionAsync(
                request.DriverId,
                connectionString,
                tunnel,
                cancellationToken);
            TestStatus = "Connected";
            var facts = new List<string> { SelectedDriver.DisplayName };
            if (info.ServerVersion is { } version)
            {
                facts[0] = $"{SelectedDriver.DisplayName} {version}";
            }

            facts.Add(info.TlsProtocol ?? "no TLS");
            if (tunnel is not null)
            {
                facts.Add($"via SSH ({tunnel.Name})");
            }

            TestDetail = string.Join(" · ", facts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            TestStatus = "Test failed";
            TestDetail = exception.Message;
        }
    }

    /// <summary>
    /// Builds the profile an inline tunnel becomes, shared by the save path
    /// and the editor's own test.
    /// </summary>
    public static ConnectionProfile BuildInlineTunnelProfile(
        ConnectionId id,
        string name,
        DatabaseInlineTunnelRequest request,
        ConnectionAuthentication authentication) =>
        new(
            id,
            ConnectionProfile.CurrentSchemaVersion,
            name,
            new ConnectionEndpoint.Ssh(request.Host, request.Port, request.Username),
            authentication,
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            request.HostKeyPolicy);

    public async Task TrustHostKeyAsync(SshHostKeyReviewId confirmedReviewId, CancellationToken cancellationToken)
    {
        if (_securityRuntime is null || _hostKeyReview is not { } review
            || review.Id != confirmedReviewId || !HasHostKeyReview || IsTesting)
        {
            return;
        }
        var action = review.RequiresExplicitReplacement
            ? SshHostKeyTrustAction.ReplaceChanged
            : SshHostKeyTrustAction.TrustNew;
        var result = await _securityRuntime.TrustSshHostKeyAsync(
            new SshHostKeyTrustRequest(review.Id, review.ConnectionId, action), cancellationToken);
        if (result is ConnectionRuntimeResult<SshHostKeyReview>.Failure failure)
        {
            TestStatus = "Host key not trusted";
            TestDetail = failure.Error.Message;
            return;
        }
        _hostKeyReview = null;
        OnPropertyChanged(nameof(HostKeyReview));
        OnPropertyChanged(nameof(HasHostKeyReview));
        await TestAsync(cancellationToken);
    }

    private DatabaseConnectionDetails ResolveDetails()
    {
        if (UseConnectionString)
        {
            if (string.IsNullOrWhiteSpace(ConnectionStringInput))
            {
                throw new ArgumentException("Enter a connection string or URL first.");
            }

            return _client.ParseConnectionDetails(
                SelectedDriver.Id,
                ConnectionStringInput.Trim());
        }

        if (IsFileBased)
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                throw new ArgumentException("Database file path is required.");
            }

            return new DatabaseConnectionDetails(
                FilePath: FilePath.Trim(),
                Options: Optional(Options));
        }

        return new DatabaseConnectionDetails(
            Optional(Host),
            ParsePort(Port, "Port"),
            Optional(DatabaseName),
            Optional(Username),
            string.IsNullOrEmpty(Password) ? null : Password,
            Options: Optional(Options));
    }

    private DatabaseInlineTunnelRequest CreateInlineTunnelRequest()
    {
        if (string.IsNullOrWhiteSpace(TunnelHost))
        {
            throw new ArgumentException("The SSH tunnel needs a host.");
        }

        var useAgent = !TunnelUsesPassword;
        var password = string.IsNullOrEmpty(TunnelPassword) ? null : TunnelPassword;
        if (!useAgent && password is null && !HasStoredTunnelPassword)
        {
            throw new ArgumentException(
                "The SSH tunnel needs a password, or switch it to the SSH agent.");
        }

        return new DatabaseInlineTunnelRequest(
            TunnelHost.Trim(),
            ParsePort(TunnelPort, "Tunnel port") ?? 22,
            Optional(TunnelUsername),
            useAgent,
            useAgent ? null : password,
            TunnelHostKeyPolicy);
    }

    private ConnectionProfile? ResolveTestTunnel(DatabaseConnectionSaveRequest request)
    {
        if (request.TunnelConnectionId is { } savedId)
        {
            return _connections.FirstOrDefault(item => item.Id == savedId)
                ?? throw new InvalidOperationException(
                    "The selected SSH tunnel connection no longer exists.");
        }

        if (request.InlineTunnel is not { } inline)
        {
            return null;
        }

        ConnectionAuthentication authentication;
        if (inline.UseAgent)
        {
            authentication = new ConnectionAuthentication.SshAgent();
        }
        else if (inline.Password is not null)
        {
            // A freshly typed tunnel password reaches the OS keychain on save;
            // until then the tunnel opener has nothing to resolve.
            throw new InvalidOperationException(
                "Tunnel passwords are stored in the OS keychain on save. "
                + "Save the connection first, then test.");
        }
        else if (_existingInlineTunnel?.Authentication
            is ConnectionAuthentication.Password stored)
        {
            authentication = stored;
        }
        else
        {
            throw new InvalidOperationException(
                "The SSH tunnel has no stored password to test with.");
        }

        return BuildInlineTunnelProfile(
            DatabaseConnectionProfile.InlineTunnelId(
                _draftProfileId),
            $"{Name.Trim()} tunnel",
            inline,
            authentication);
    }

    /// <summary>
    /// Reads a pasted string into the structured fields when the user switches
    /// back — the two views describe one connection. A string the driver
    /// refuses leaves the fields as they were.
    /// </summary>
    private void AdoptConnectionStringIntoFields()
    {
        if (string.IsNullOrWhiteSpace(_connectionStringInput))
        {
            return;
        }

        DatabaseConnectionDetails details;
        try
        {
            details = _client.ParseConnectionDetails(
                SelectedDriver.Id,
                _connectionStringInput.Trim());
        }
        catch (ArgumentException)
        {
            return;
        }

        Host = details.Host ?? string.Empty;
        Port = details.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        DatabaseName = details.Database ?? string.Empty;
        Username = details.Username ?? string.Empty;
        if (details.Password is { } password)
        {
            Password = password;
        }

        FilePath = details.FilePath ?? string.Empty;
        Options = details.Options ?? string.Empty;
    }

    /// <summary>
    /// Seeds the string view from the fields — without the password, which
    /// never renders back into plain text.
    /// </summary>
    private void PrefillConnectionStringFromFields()
    {
        try
        {
            var details = IsFileBased
                ? new DatabaseConnectionDetails(
                    FilePath: Optional(FilePath),
                    Options: Optional(Options))
                : new DatabaseConnectionDetails(
                    Optional(Host),
                    ParsePort(Port, "Port"),
                    Optional(DatabaseName),
                    Optional(Username),
                    Options: Optional(Options));
            ConnectionStringInput = _client.BuildConnectionString(
                SelectedDriver.Id,
                details);
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException)
        {
            // Half-filled fields are not worth blocking the switch over.
        }
    }

    private static int? ParsePort(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(
                value.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed is < 1 or > 65_535)
        {
            throw new ArgumentException($"{label} must be a number between 1 and 65535.");
        }

        return parsed;
    }

    private static IReadOnlyList<DatabaseTunnelOption> BuildTunnelOptions(
        IReadOnlyList<ConnectionProfile> connections,
        DatabaseConnectionProfile? existing)
    {
        var options = new List<DatabaseTunnelOption>
        {
            new(null, "No tunnel"),
        };
        options.AddRange(connections
            .Where(item => item.Endpoint is ConnectionEndpoint.Ssh)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DatabaseTunnelOption(item.Id, item.Name)));
        if (existing?.TunnelConnectionId is { } tunnelId
            && options.All(item => item.Id != tunnelId))
        {
            options.Add(new DatabaseTunnelOption(tunnelId, $"Missing · {tunnelId.Value}"));
        }

        options.Add(new DatabaseTunnelOption(null, "Custom — this connection only", IsInline: true));
        return options.AsReadOnly();
    }

    private static string? Optional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
