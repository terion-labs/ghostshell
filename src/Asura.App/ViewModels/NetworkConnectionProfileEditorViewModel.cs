using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record NetworkConnectionKindOption(
    NetworkConnectionKind Kind,
    string DisplayName,
    string Description);

public sealed record NetworkProxyProtocolOption(
    NetworkProxyProtocol Protocol,
    string DisplayName);

public sealed record NetworkConnectionProfileSaveRequest(
    NetworkConnectionProfile Profile,
    long? ExpectedRevision);

public enum NetworkCredentialOptionState
{
    None,
    Prompt,
    Available,
    Pending,
    Unavailable,
    MetadataUnavailable,
    Loading,
}

public sealed record NetworkCredentialOption(
    SecretRef? Reference,
    string Label,
    SecretKind? Kind,
    SecretVaultPersistenceKind Persistence,
    DateTimeOffset? UpdatedAt,
    NetworkCredentialOptionState State,
    NetworkCredentialTarget? PendingTarget = null)
{
    public string DisplayName => State switch
    {
        NetworkCredentialOptionState.None => "No stored credential",
        NetworkCredentialOptionState.Prompt => "Prompt on each connection",
        NetworkCredentialOptionState.Available => $"{Label} · {KindLabel}",
        NetworkCredentialOptionState.Pending => $"{Label} · Ready to store",
        NetworkCredentialOptionState.Unavailable => "Credential unavailable",
        NetworkCredentialOptionState.MetadataUnavailable =>
            "Credential metadata unavailable",
        NetworkCredentialOptionState.Loading => "Checking stored credential…",
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, null),
    };

    public string Detail => State switch
    {
        NetworkCredentialOptionState.None => "Do not use a credential for this field.",
        NetworkCredentialOptionState.Prompt =>
            "The password is used for that connection only and is not stored.",
        NetworkCredentialOptionState.Available =>
            $"{KindLabel} · {PersistenceLabel} · Updated {UpdatedLabel}",
        NetworkCredentialOptionState.Pending =>
            $"{KindLabel} · Stored when the connection is saved",
        NetworkCredentialOptionState.Unavailable =>
            "The saved credential was not found in this connection's vault scope.",
        NetworkCredentialOptionState.MetadataUnavailable =>
            "The operating-system vault could not provide metadata for this binding.",
        NetworkCredentialOptionState.Loading =>
            "Reading this connection's credential metadata from the operating-system vault.",
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, null),
    };

    public bool IsAvailable => State is NetworkCredentialOptionState.None
        or NetworkCredentialOptionState.Prompt
        or NetworkCredentialOptionState.Available
        or NetworkCredentialOptionState.Pending;

    private string KindLabel => Kind switch
    {
        SecretKind.ApiKey => "API key",
        SecretKind.PrivateKey => "Private key",
        SecretKind.Other => "Configuration",
        null => "Credential",
        _ => Kind.Value.ToString(),
    };

    private string PersistenceLabel => Persistence switch
    {
        SecretVaultPersistenceKind.OsProtectedPersistent => "OS vault",
        SecretVaultPersistenceKind.MemoryOnly => "This session",
        _ => "Storage unavailable",
    };

    private string UpdatedLabel => UpdatedAt?.ToLocalTime().ToString(
        "g",
        System.Globalization.CultureInfo.CurrentCulture) ?? "unknown";
}

/// <summary>
/// Owns one network connection draft. Credential fields contain only vault references;
/// secret material never enters the durable profile.
/// </summary>
public sealed class NetworkConnectionProfileEditorViewModel : ObservableObject
{
    private readonly NetworkConnectionId _id;
    private readonly long? _expectedRevision;
    private string _name;
    private NetworkConnectionKindOption _selectedKind;
    private NetworkProxyProtocolOption _selectedProxyProtocol;
    private string _host = string.Empty;
    private string _port = string.Empty;
    private string _username = string.Empty;
    private string _passwordSecretReference = string.Empty;
    private string _configurationSecretReference = string.Empty;
    private string _gateway = string.Empty;
    private string _authenticationGroup = string.Empty;
    private string _clientCertificateSecretReference = string.Empty;
    private string _exitNode = string.Empty;
    private string _controlServer = string.Empty;
    private string _authKeySecretReference = string.Empty;
    private readonly Dictionary<SecretRef, NetworkCredentialOption> _storedCredentials = [];
    private readonly Dictionary<SecretRef, NetworkCredentialOption> _pendingCredentials = [];
    private NetworkCredentialOptionState _missingCredentialState =
        NetworkCredentialOptionState.MetadataUnavailable;
    private readonly ObservableCollection<NetworkCredentialOption> _passwordCredentialOptions = [];
    private readonly ObservableCollection<NetworkCredentialOption> _configurationCredentialOptions = [];
    private readonly ObservableCollection<NetworkCredentialOption> _clientCertificateCredentialOptions = [];
    private readonly ObservableCollection<NetworkCredentialOption> _authKeyCredentialOptions = [];
    private bool _isDirty;
    private bool _isCredentialDraftOpen;

    public NetworkConnectionProfileEditorViewModel()
        : this(null, null)
    {
    }

    public NetworkConnectionProfileEditorViewModel(
        NetworkConnectionProfile? profile,
        long? expectedRevision)
    {
        KindOptions =
        [
            new(NetworkConnectionKind.Proxy, "Proxy", "SOCKS5, HTTP, or HTTPS proxy."),
            new(NetworkConnectionKind.WireGuard, "WireGuard", "WireGuard client configuration stored in the credential vault."),
            new(NetworkConnectionKind.OpenVpn, "OpenVPN", "OpenVPN client profile stored in the credential vault."),
            new(NetworkConnectionKind.AnyConnect, "AnyConnect", "Cisco AnyConnect-compatible gateway through OpenConnect."),
            new(NetworkConnectionKind.Tailscale, "Tailscale", "Tailscale exit node for workspace traffic."),
        ];
        ProxyProtocolOptions =
        [
            new(NetworkProxyProtocol.Socks5, "SOCKS5"),
            new(NetworkProxyProtocol.Http, "HTTP"),
            new(NetworkProxyProtocol.Https, "HTTPS"),
        ];

        _id = profile?.Id ?? NetworkConnectionId.New();
        _expectedRevision = expectedRevision;
        _name = profile?.Name ?? "New network connection";
        _selectedKind = KindOptions.Single(option =>
            option.Kind == (profile?.ConnectionKind ?? NetworkConnectionKind.Proxy));
        _selectedProxyProtocol = ProxyProtocolOptions[0];
        Credential = new();
        Credential.SetConnectionKind(_selectedKind.Kind);
        if (profile is not null)
        {
            Restore(profile.Configuration);
        }

        RebuildCredentialOptions();
    }

    public IReadOnlyList<NetworkConnectionKindOption> KindOptions { get; }

    public IReadOnlyList<NetworkProxyProtocolOption> ProxyProtocolOptions { get; }

    public NetworkCredentialDraftViewModel Credential { get; }

    public NetworkConnectionId Id => _id;

    public long? ExpectedRevision => _expectedRevision;

    public bool IsNew => ExpectedRevision is null;

    public string EditorTitle => IsNew ? "New network connection" : "Edit network connection";

    /// <summary>
    /// Whether the draft for one credential slot is open. It opens from the
    /// slot that wants it, already knowing which slot that is, and closes when
    /// the credential is added or the user thinks better of it.
    /// </summary>
    public bool IsCredentialDraftOpen
    {
        get => _isCredentialDraftOpen;
        private set => SetProperty(ref _isCredentialDraftOpen, value);
    }

    public void BeginCredentialDraft(NetworkCredentialTarget target)
    {
        Credential.SelectedTarget = Credential.Targets.SingleOrDefault(option =>
            option.Target == target)
            ?? throw new ArgumentOutOfRangeException(nameof(target), target, null);
        if (string.IsNullOrWhiteSpace(Credential.Label))
        {
            Credential.Label = $"{Name} · {Credential.SelectedTarget.DisplayName}";
        }

        IsCredentialDraftOpen = true;
    }

    public void CloseCredentialDraft()
    {
        Credential.ClearValue();
        IsCredentialDraftOpen = false;
    }

    /// <summary>The slot each picker adds to, so the view need not know the kind.</summary>
    public NetworkCredentialTarget PasswordCredentialTarget => SelectedKind.Kind switch
    {
        NetworkConnectionKind.AnyConnect => NetworkCredentialTarget.AnyConnectPassword,
        NetworkConnectionKind.OpenVpn => NetworkCredentialTarget.OpenVpnPassword,
        _ => NetworkCredentialTarget.ProxyPassword,
    };

    public NetworkCredentialTarget ConfigurationCredentialTarget => IsOpenVpn
        ? NetworkCredentialTarget.OpenVpnConfiguration
        : NetworkCredentialTarget.WireGuardConfiguration;

    public string KindDescription => SelectedKind.Description;

    public string Name
    {
        get => _name;
        set => Change(ref _name, value ?? string.Empty);
    }

    public NetworkConnectionKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedKind, value))
            {
                Credential.SetConnectionKind(value.Kind);
                IsCredentialDraftOpen = false;
                PublishKind();
                Changed();
            }
        }
    }

    public NetworkProxyProtocolOption SelectedProxyProtocol
    {
        get => _selectedProxyProtocol;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedProxyProtocol, value))
            {
                Changed();
            }
        }
    }

    public string Host
    {
        get => _host;
        set => Change(ref _host, value ?? string.Empty);
    }

    public string Port
    {
        get => _port;
        set => Change(ref _port, value ?? string.Empty);
    }

    public string Username
    {
        get => _username;
        set => Change(ref _username, value ?? string.Empty);
    }

    public string PasswordSecretReference
    {
        get => _passwordSecretReference;
        set => ChangeCredentialReference(ref _passwordSecretReference, value);
    }

    public string ConfigurationSecretReference
    {
        get => _configurationSecretReference;
        set => ChangeCredentialReference(ref _configurationSecretReference, value);
    }

    public string Gateway
    {
        get => _gateway;
        set => Change(ref _gateway, value ?? string.Empty);
    }

    public string AuthenticationGroup
    {
        get => _authenticationGroup;
        set => Change(ref _authenticationGroup, value ?? string.Empty);
    }

    public string ClientCertificateSecretReference
    {
        get => _clientCertificateSecretReference;
        set => ChangeCredentialReference(ref _clientCertificateSecretReference, value);
    }

    public string ExitNode
    {
        get => _exitNode;
        set => Change(ref _exitNode, value ?? string.Empty);
    }

    public string ControlServer
    {
        get => _controlServer;
        set => Change(ref _controlServer, value ?? string.Empty);
    }

    public string AuthKeySecretReference
    {
        get => _authKeySecretReference;
        set => ChangeCredentialReference(ref _authKeySecretReference, value);
    }

    public bool IsProxy => SelectedKind.Kind == NetworkConnectionKind.Proxy;

    public bool IsWireGuard => SelectedKind.Kind == NetworkConnectionKind.WireGuard;

    public bool IsOpenVpn => SelectedKind.Kind == NetworkConnectionKind.OpenVpn;

    public bool IsConfigurationFileVpn => IsWireGuard || IsOpenVpn;

    public bool IsAnyConnect => SelectedKind.Kind == NetworkConnectionKind.AnyConnect;

    public bool IsTailscale => SelectedKind.Kind == NetworkConnectionKind.Tailscale;

    public string ConfigurationSecretLabel => IsWireGuard
        ? "WireGuard configuration credential"
        : "OpenVPN profile credential";

    public IReadOnlyList<NetworkCredentialOption> PasswordCredentialOptions =>
        _passwordCredentialOptions;

    public IReadOnlyList<NetworkCredentialOption> ConfigurationCredentialOptions =>
        _configurationCredentialOptions;

    public IReadOnlyList<NetworkCredentialOption> ClientCertificateCredentialOptions =>
        _clientCertificateCredentialOptions;

    public IReadOnlyList<NetworkCredentialOption> AuthKeyCredentialOptions =>
        _authKeyCredentialOptions;

    // ItemsSource refreshes briefly clear ComboBox.SelectedItem. Only an
    // explicit option may change a reference; Prompt/None options clear it.
    public NetworkCredentialOption? SelectedPasswordCredential
    {
        get => SelectedCredential(PasswordCredentialOptions, PasswordSecretReference);
        set
        {
            if (value is not null)
            {
                PasswordSecretReference = value.Reference?.Value ?? string.Empty;
            }
        }
    }

    public NetworkCredentialOption? SelectedConfigurationCredential
    {
        get => SelectedCredential(
            ConfigurationCredentialOptions,
            ConfigurationSecretReference);
        set
        {
            if (value is not null)
            {
                ConfigurationSecretReference = value.Reference?.Value ?? string.Empty;
            }
        }
    }

    public NetworkCredentialOption? SelectedClientCertificateCredential
    {
        get => SelectedCredential(
            ClientCertificateCredentialOptions,
            ClientCertificateSecretReference);
        set
        {
            if (value is not null)
            {
                ClientCertificateSecretReference = value.Reference?.Value ?? string.Empty;
            }
        }
    }

    public NetworkCredentialOption? SelectedAuthKeyCredential
    {
        get => SelectedCredential(AuthKeyCredentialOptions, AuthKeySecretReference);
        set
        {
            if (value is not null)
            {
                AuthKeySecretReference = value.Reference?.Value ?? string.Empty;
            }
        }
    }

    public bool IsDirty => _isDirty;

    public bool IsValid => TryBuild(out _, out _);

    public bool CanSave => IsDirty && IsValid;

    public string StatusLabel => IsValid ? "Valid" : "Needs attention";

    public bool HasValidationError => !IsValid;

    public string ValidationMessage => TryBuild(out _, out var error)
        ? "Connection configuration is valid."
        : error;

    /// <summary>The footer's one sentence: what is still missing, or what saving does.</summary>
    public string FooterHint => TryBuild(out _, out var error)
        ? "Saving stores any new credential in the operating-system vault."
        : error;

    public NetworkConnectionProfileSaveRequest CreateSaveRequest()
    {
        if (!TryBuild(out var profile, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return new(profile!, ExpectedRevision);
    }

    public void ApplyCredential(
        NetworkCredentialTarget target,
        SecretRef reference,
        string label,
        SecretKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        foreach (var pendingReference in _pendingCredentials
                     .Where(item => item.Value.PendingTarget == target)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _pendingCredentials.Remove(pendingReference);
        }

        _pendingCredentials[reference] = new(
            reference,
            label.Trim(),
            kind,
            SecretVaultPersistenceKind.None,
            null,
            NetworkCredentialOptionState.Pending,
            target);
        switch (target)
        {
            case NetworkCredentialTarget.ProxyPassword:
            case NetworkCredentialTarget.AnyConnectPassword:
            case NetworkCredentialTarget.OpenVpnPassword:
                PasswordSecretReference = reference.Value;
                break;
            case NetworkCredentialTarget.WireGuardConfiguration:
            case NetworkCredentialTarget.OpenVpnConfiguration:
                ConfigurationSecretReference = reference.Value;
                break;
            case NetworkCredentialTarget.AnyConnectClientCertificate:
                ClientCertificateSecretReference = reference.Value;
                break;
            case NetworkCredentialTarget.TailscaleAuthKey:
                AuthKeySecretReference = reference.Value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    public void BeginCredentialMetadataLoad()
    {
        _missingCredentialState = NetworkCredentialOptionState.Loading;
        RebuildCredentialOptions();
    }

    public void ApplyCredentialMetadata(IReadOnlyList<SecretMetadata> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _storedCredentials.Clear();
        foreach (var credential in credentials.Where(item =>
                     item.Scope.Kind == SecretScopeKind.NetworkConnection
                     && string.Equals(
                         item.Scope.OwnerId,
                         Id.Value,
                         StringComparison.Ordinal)))
        {
            _storedCredentials[credential.Reference] = new(
                credential.Reference,
                credential.Label,
                credential.Kind,
                credential.Persistence,
                credential.UpdatedAt,
                NetworkCredentialOptionState.Available);
        }

        _missingCredentialState = NetworkCredentialOptionState.Unavailable;
        RebuildCredentialOptions();
    }

    public void MarkCredentialMetadataUnavailable()
    {
        _storedCredentials.Clear();
        _missingCredentialState = NetworkCredentialOptionState.MetadataUnavailable;
        RebuildCredentialOptions();
    }

    private bool TryBuild(
        out NetworkConnectionProfile? profile,
        out string error)
    {
        try
        {
            profile = new(
                Id,
                NetworkConnectionProfile.CurrentSchemaVersion,
                Name,
                BuildConfiguration());
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            profile = null;
            error = FriendlyValidationMessage(exception);
            return false;
        }
    }

    private NetworkConnectionConfiguration BuildConfiguration() => SelectedKind.Kind switch
    {
        NetworkConnectionKind.Proxy => new NetworkConnectionConfiguration.Proxy(
            SelectedProxyProtocol.Protocol,
            Host,
            RequirePort(),
            Optional(Username),
            OptionalSecret(PasswordSecretReference)),
        NetworkConnectionKind.WireGuard => new NetworkConnectionConfiguration.WireGuard(
            RequiredSecret(ConfigurationSecretReference, "WireGuard configuration credential")),
        NetworkConnectionKind.OpenVpn => new NetworkConnectionConfiguration.OpenVpn(
            RequiredSecret(ConfigurationSecretReference, "OpenVPN profile credential"),
            Optional(Username),
            OptionalSecret(PasswordSecretReference)),
        NetworkConnectionKind.AnyConnect => new NetworkConnectionConfiguration.AnyConnect(
            new Uri(Gateway, UriKind.RelativeOrAbsolute),
            Optional(Username),
            OptionalSecret(PasswordSecretReference),
            Optional(AuthenticationGroup),
            OptionalSecret(ClientCertificateSecretReference)),
        NetworkConnectionKind.Tailscale => new NetworkConnectionConfiguration.Tailscale(
            ExitNode,
            OptionalUri(ControlServer),
            OptionalSecret(AuthKeySecretReference)),
        _ => throw new InvalidOperationException("Choose a supported network connection type."),
    };

    private int RequirePort()
    {
        if (!int.TryParse(
            Port,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port))
        {
            throw new ArgumentException("Enter a proxy port between 1 and 65535.");
        }

        return port;
    }

    private void Restore(NetworkConnectionConfiguration configuration)
    {
        switch (configuration)
        {
            case NetworkConnectionConfiguration.Proxy proxy:
                _selectedProxyProtocol = ProxyProtocolOptions.Single(option =>
                    option.Protocol == proxy.Protocol);
                _host = proxy.Host;
                _port = proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _username = proxy.Username ?? string.Empty;
                _passwordSecretReference = proxy.PasswordSecret?.Value ?? string.Empty;
                break;
            case NetworkConnectionConfiguration.WireGuard wireGuard:
                _configurationSecretReference = wireGuard.ConfigurationSecret.Value;
                break;
            case NetworkConnectionConfiguration.OpenVpn openVpn:
                _configurationSecretReference = openVpn.ConfigurationSecret.Value;
                _username = openVpn.Username ?? string.Empty;
                _passwordSecretReference = openVpn.PasswordSecret?.Value ?? string.Empty;
                break;
            case NetworkConnectionConfiguration.AnyConnect anyConnect:
                _gateway = anyConnect.Gateway.AbsoluteUri;
                _username = anyConnect.Username ?? string.Empty;
                _passwordSecretReference = anyConnect.PasswordSecret?.Value ?? string.Empty;
                _authenticationGroup = anyConnect.AuthenticationGroup ?? string.Empty;
                _clientCertificateSecretReference =
                    anyConnect.ClientCertificateSecret?.Value ?? string.Empty;
                break;
            case NetworkConnectionConfiguration.Tailscale tailscale:
                _exitNode = tailscale.ExitNode;
                _controlServer = tailscale.ControlServer?.AbsoluteUri ?? string.Empty;
                _authKeySecretReference = tailscale.AuthKeySecret?.Value ?? string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private void Change(ref string field, string value)
    {
        if (SetProperty(ref field, value))
        {
            Changed();
        }
    }

    private void ChangeCredentialReference(ref string field, string? value)
    {
        if (SetProperty(ref field, value ?? string.Empty))
        {
            Changed();
            RebuildCredentialOptions();
        }
    }

    private void Changed()
    {
        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }

        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(FooterHint));
    }

    private void PublishKind()
    {
        OnPropertyChanged(nameof(IsProxy));
        OnPropertyChanged(nameof(IsWireGuard));
        OnPropertyChanged(nameof(IsOpenVpn));
        OnPropertyChanged(nameof(IsConfigurationFileVpn));
        OnPropertyChanged(nameof(IsAnyConnect));
        OnPropertyChanged(nameof(IsTailscale));
        OnPropertyChanged(nameof(ConfigurationSecretLabel));
        OnPropertyChanged(nameof(PasswordCredentialTarget));
        OnPropertyChanged(nameof(ConfigurationCredentialTarget));
        OnPropertyChanged(nameof(KindDescription));
        RebuildCredentialOptions();
    }

    private static string? Optional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SecretRef? OptionalSecret(string value) =>
        Optional(value) is { } reference ? new SecretRef(reference) : null;

    private static SecretRef RequiredSecret(string value, string label) =>
        new(string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Enter the {label} reference.")
            : value.Trim());

    private static Uri? OptionalUri(string value) =>
        Optional(value) is { } uri ? new Uri(uri, UriKind.RelativeOrAbsolute) : null;

    private static string FriendlyValidationMessage(Exception exception)
    {
        var message = exception.Message;
        var parameterSuffix = message.IndexOf("\nParameter", StringComparison.Ordinal);
        if (parameterSuffix > 0)
        {
            message = message[..parameterSuffix];
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return message.Trim();
        }

        return "Complete the required fields with a valid network configuration.";
    }

    private void RebuildCredentialOptions()
    {
        var passwordOptions = BuildCredentialOptions(
            SecretKind.Password,
            PasswordSecretReference,
            isRequired: false,
            SelectedKind.Kind switch
            {
                NetworkConnectionKind.AnyConnect => NetworkCredentialTarget.AnyConnectPassword,
                NetworkConnectionKind.OpenVpn => NetworkCredentialTarget.OpenVpnPassword,
                _ => NetworkCredentialTarget.ProxyPassword,
            });
        var configurationOptions = BuildCredentialOptions(
            SecretKind.Other,
            ConfigurationSecretReference,
            isRequired: true,
            IsOpenVpn
                ? NetworkCredentialTarget.OpenVpnConfiguration
                : NetworkCredentialTarget.WireGuardConfiguration);
        var certificateOptions = BuildCredentialOptions(
            SecretKind.Certificate,
            ClientCertificateSecretReference,
            isRequired: false,
            NetworkCredentialTarget.AnyConnectClientCertificate);
        var authKeyOptions = BuildCredentialOptions(
            SecretKind.ApiKey,
            AuthKeySecretReference,
            isRequired: false,
            NetworkCredentialTarget.TailscaleAuthKey);

        // Keep ItemsSource identity stable. Replacing it through a binding can
        // restore the control's stale selection after SelectedItem is published.
        ReplaceCredentialOptions(_passwordCredentialOptions, passwordOptions);
        ReplaceCredentialOptions(_configurationCredentialOptions, configurationOptions);
        ReplaceCredentialOptions(_clientCertificateCredentialOptions, certificateOptions);
        ReplaceCredentialOptions(_authKeyCredentialOptions, authKeyOptions);
        OnPropertyChanged(nameof(SelectedPasswordCredential));
        OnPropertyChanged(nameof(SelectedConfigurationCredential));
        OnPropertyChanged(nameof(SelectedClientCertificateCredential));
        OnPropertyChanged(nameof(SelectedAuthKeyCredential));
    }

    private static void ReplaceCredentialOptions(
        ObservableCollection<NetworkCredentialOption> destination,
        IReadOnlyList<NetworkCredentialOption> options)
    {
        destination.Clear();
        foreach (var option in options)
        {
            destination.Add(option);
        }
    }

    private IReadOnlyList<NetworkCredentialOption> BuildCredentialOptions(
        SecretKind expectedKind,
        string boundReference,
        bool isRequired,
        NetworkCredentialTarget target)
    {
        var reference = OptionalSecret(boundReference);
        var options = _storedCredentials.Values
            .Where(item => item.Kind == expectedKind || item.Reference == reference)
            .Concat(_pendingCredentials.Values.Where(item =>
                item.PendingTarget == target || item.Reference == reference))
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (reference is { } bound
            && options.All(item => item.Reference != bound))
        {
            options.Add(MissingCredential(bound));
        }

        if (!isRequired)
        {
            options.Insert(0, target is NetworkCredentialTarget.ProxyPassword
                or NetworkCredentialTarget.AnyConnectPassword
                or NetworkCredentialTarget.OpenVpnPassword
                    ? PromptForPassword()
                    : NoCredential());
        }

        return options.AsReadOnly();
    }

    private NetworkCredentialOption MissingCredential(SecretRef reference) => new(
        reference,
        string.Empty,
        null,
        SecretVaultPersistenceKind.None,
        null,
        _missingCredentialState);

    private static NetworkCredentialOption NoCredential() => new(
        null,
        "No stored credential",
        null,
        SecretVaultPersistenceKind.None,
        null,
        NetworkCredentialOptionState.None);

    private static NetworkCredentialOption PromptForPassword() => new(
        null,
        "Prompt on each connection",
        SecretKind.Password,
        SecretVaultPersistenceKind.None,
        null,
        NetworkCredentialOptionState.Prompt);

    private static NetworkCredentialOption? SelectedCredential(
        IReadOnlyList<NetworkCredentialOption> options,
        string reference)
    {
        var selectedReference = OptionalSecret(reference);
        return options.FirstOrDefault(item => item.Reference == selectedReference);
    }

}
