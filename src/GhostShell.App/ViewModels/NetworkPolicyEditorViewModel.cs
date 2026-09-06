using System.Collections.ObjectModel;
using FluentIcons.Common;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed class NetworkPolicyConnectionOptionViewModel : ObservableObject
{
    private bool _isAvailable;

    public NetworkPolicyConnectionOptionViewModel(
        NetworkConnectionId id,
        string name,
        NetworkConnectionKind? kind,
        string summary,
        bool isAvailable)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Summary = summary;
        _isAvailable = isAvailable;
    }

    public event EventHandler? Changed;

    public NetworkConnectionId Id { get; }

    public string Name { get; }

    public NetworkConnectionKind? Kind { get; }

    public string Summary { get; }

    public bool IsMissing => Kind is null;

    public string KindLabel => Kind is { } kind
        ? NetworkConnectionPresentation.KindLabel(kind)
        : "Missing";

    public Symbol KindSymbol => Kind is { } kind
        ? NetworkConnectionPresentation.KindSymbol(kind)
        : Symbol.LinkDismiss;

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

/// <summary>
/// Edits one complete network policy. Availability and selection stay together so an
/// enabled policy cannot point outside its allowed connection set.
/// </summary>
public sealed class NetworkPolicyEditorViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<NetworkPolicyConnectionOptionViewModel> _connections;
    private readonly ObservableCollection<NetworkPolicyConnectionOptionViewModel>
        _availableConnections = [];
    private readonly ReadOnlyObservableCollection<NetworkPolicyConnectionOptionViewModel>
        _readOnlyAvailableConnections;
    private NetworkPolicyConnectionOptionViewModel? _selectedConnection;
    private bool _isEnabled;
    private bool _killSwitchEnabled;
    private bool _isDirty;
    private bool _disposed;

    public NetworkPolicyEditorViewModel(
        IReadOnlyList<NetworkConnectionProfile> profiles,
        NetworkPolicy policy,
        bool isDirty = false)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(policy);
        var profilesById = profiles.ToDictionary(profile => profile.Id);
        var options = profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new NetworkPolicyConnectionOptionViewModel(
                profile.Id,
                profile.Name,
                profile.ConnectionKind,
                NetworkConnectionPresentation.Summary(profile.Configuration),
                policy.Connections.Contains(profile.Id)))
            .ToList();
        foreach (var missingId in policy.Connections.Where(id => !profilesById.ContainsKey(id)))
        {
            options.Add(new(
                missingId,
                $"Missing connection ({missingId.Value})",
                null,
                "Remove this unavailable connection before saving.",
                isAvailable: true));
        }

        _connections = new(options);
        Connections = new(_connections);
        _readOnlyAvailableConnections = new(_availableConnections);
        foreach (var option in _connections)
        {
            option.Changed += OnConnectionAvailabilityChanged;
        }

        _isEnabled = policy.IsEnabled;
        _killSwitchEnabled = policy.KillSwitchEnabled;
        _isDirty = isDirty;
        RefreshAvailableConnections(policy.SelectedConnectionId);
    }

    public event EventHandler? Changed;

    public ReadOnlyObservableCollection<NetworkPolicyConnectionOptionViewModel> Connections { get; }

    public ReadOnlyObservableCollection<NetworkPolicyConnectionOptionViewModel>
        AvailableConnections => _readOnlyAvailableConnections;

    public bool HasConnections => Connections.Count > 0;

    public bool HasNoConnections => !HasConnections;

    public bool HasAvailableConnections => AvailableConnections.Count > 0;

    public bool HasMissingConnections => Connections.Any(option =>
        option is { IsAvailable: true, IsMissing: true });

    public bool CanEnable => HasAvailableConnections && !HasMissingConnections;

    public NetworkPolicyConnectionOptionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (value is not null && !_availableConnections.Contains(value))
            {
                return;
            }

            if (SetProperty(ref _selectedConnection, value))
            {
                if (value is null && _isEnabled)
                {
                    _isEnabled = false;
                    OnPropertyChanged(nameof(IsEnabled));
                }

                MarkChanged();
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value && SelectedConnection is null && _availableConnections.Count > 0)
            {
                _selectedConnection = _availableConnections[0];
                OnPropertyChanged(nameof(SelectedConnection));
            }

            var normalized = value && CanEnable;
            if (SetProperty(ref _isEnabled, normalized))
            {
                MarkChanged();
            }
        }
    }

    public bool KillSwitchEnabled
    {
        get => _killSwitchEnabled;
        set
        {
            if (SetProperty(ref _killSwitchEnabled, value))
            {
                MarkChanged();
            }
        }
    }

    public bool IsDirty => _isDirty;

    public bool IsValid => !HasMissingConnections && (!IsEnabled || SelectedConnection is not null);

    public bool CanSave => IsDirty && IsValid;

    public string Summary => IsEnabled && SelectedConnection is { } selected
        ? $"Traffic uses {selected.Name}."
        : "Traffic connects directly.";

    public NetworkPolicy CreatePolicy()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                "Remove missing connections and choose an available route before saving.");
        }

        return new(
            [.. _connections.Where(option => option.IsAvailable).Select(option => option.Id)],
            SelectedConnection?.Id,
            IsEnabled,
            KillSwitchEnabled);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var option in _connections)
        {
            option.Changed -= OnConnectionAvailabilityChanged;
        }
    }

    private void OnConnectionAvailabilityChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        var selectedId = SelectedConnection?.Id;
        RefreshAvailableConnections(selectedId);
        if (SelectedConnection is null && _availableConnections.Count > 0)
        {
            _selectedConnection = _availableConnections[0];
            OnPropertyChanged(nameof(SelectedConnection));
        }

        if (!CanEnable && _isEnabled)
        {
            _isEnabled = false;
            OnPropertyChanged(nameof(IsEnabled));
        }

        MarkChanged();
    }

    private void RefreshAvailableConnections(NetworkConnectionId? selectedId)
    {
        var previousSelection = _selectedConnection;
        _availableConnections.Clear();
        foreach (var option in _connections.Where(option => option.IsAvailable))
        {
            _availableConnections.Add(option);
        }

        _selectedConnection = selectedId is { } id
            ? _availableConnections.SingleOrDefault(option => option.Id == id)
            : null;
        if (!ReferenceEquals(previousSelection, _selectedConnection))
        {
            OnPropertyChanged(nameof(SelectedConnection));
        }

        OnPropertyChanged(nameof(AvailableConnections));
        PublishState();
    }

    private void MarkChanged()
    {
        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }

        PublishState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PublishState()
    {
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(HasAvailableConnections));
        OnPropertyChanged(nameof(HasMissingConnections));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(Summary));
    }
}

public static class NetworkConnectionPresentation
{
    /// <summary>
    /// The mark a connection carries in a list: a proxy relays, a VPN shields,
    /// and a tailnet is a globe of peers.
    /// </summary>
    public static Symbol KindSymbol(NetworkConnectionKind kind) => kind switch
    {
        NetworkConnectionKind.Proxy => Symbol.ArrowRouting,
        NetworkConnectionKind.WireGuard => Symbol.ShieldLock,
        NetworkConnectionKind.OpenVpn => Symbol.ShieldLock,
        NetworkConnectionKind.AnyConnect => Symbol.ShieldLock,
        NetworkConnectionKind.Tailscale => Symbol.Globe,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string KindLabel(NetworkConnectionKind kind) => kind switch
    {
        NetworkConnectionKind.Proxy => "Proxy",
        NetworkConnectionKind.WireGuard => "WireGuard",
        NetworkConnectionKind.OpenVpn => "OpenVPN",
        NetworkConnectionKind.AnyConnect => "AnyConnect",
        NetworkConnectionKind.Tailscale => "Tailscale",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string Summary(NetworkConnectionConfiguration configuration) => configuration switch
    {
        NetworkConnectionConfiguration.Proxy proxy => proxy.Endpoint.AbsoluteUri,
        NetworkConnectionConfiguration.WireGuard => "WireGuard configuration from the credential vault",
        NetworkConnectionConfiguration.OpenVpn => "OpenVPN profile from the credential vault",
        NetworkConnectionConfiguration.AnyConnect anyConnect => anyConnect.Gateway.AbsoluteUri,
        NetworkConnectionConfiguration.Tailscale tailscale => $"Exit node {tailscale.ExitNode}",
        _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
    };
}
