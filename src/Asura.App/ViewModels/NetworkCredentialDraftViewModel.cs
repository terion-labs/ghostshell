using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public enum NetworkCredentialTarget
{
    ProxyPassword,
    WireGuardConfiguration,
    OpenVpnConfiguration,
    AnyConnectPassword,
    AnyConnectClientCertificate,
    TailscaleAuthKey,
    OpenVpnPassword,
}

public sealed record NetworkCredentialTargetOption(
    NetworkCredentialTarget Target,
    string DisplayName,
    SecretKind Kind);

/// <summary>Holds new secret material only until the vault accepts it.</summary>
public sealed class NetworkCredentialDraftViewModel : ObservableObject
{
    private NetworkCredentialTargetOption? _selectedTarget;
    private string _label = string.Empty;
    private string _value = string.Empty;

    public ObservableCollection<NetworkCredentialTargetOption> Targets { get; } = [];

    public NetworkCredentialTargetOption? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                OnPropertyChanged(nameof(CanStore));
                OnPropertyChanged(nameof(IsDocument));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(ValueLabel));
            }
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (SetProperty(ref _label, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanStore));
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanStore));
            }
        }
    }

    public bool CanStore =>
        SelectedTarget is not null
        && !string.IsNullOrWhiteSpace(Label)
        && !string.IsNullOrEmpty(Value);

    /// <summary>
    /// A configuration or certificate is a document and is pasted in the
    /// clear; a password or key is one line and is masked.
    /// </summary>
    public bool IsDocument =>
        SelectedTarget?.Kind is SecretKind.Other or SecretKind.Certificate;

    public string Title => SelectedTarget is { } target
        ? $"Add {target.DisplayName.ToLowerInvariant()}"
        : "Add credential";

    public string ValueLabel => SelectedTarget is { } target
        ? target.DisplayName
        : "Secret value";

    public void SetConnectionKind(NetworkConnectionKind kind)
    {
        var previous = SelectedTarget?.Target;
        Targets.Clear();
        foreach (var target in TargetsFor(kind))
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets.SingleOrDefault(item => item.Target == previous)
            ?? Targets.FirstOrDefault();
        OnPropertyChanged(nameof(Targets));
    }

    public void ClearValue()
    {
        Value = string.Empty;
        Label = string.Empty;
    }

    private static IReadOnlyList<NetworkCredentialTargetOption> TargetsFor(
        NetworkConnectionKind kind) => kind switch
        {
            NetworkConnectionKind.Proxy =>
            [
                new(NetworkCredentialTarget.ProxyPassword, "Proxy password", SecretKind.Password),
        ],
            NetworkConnectionKind.WireGuard =>
            [
                new(NetworkCredentialTarget.WireGuardConfiguration, "WireGuard configuration", SecretKind.Other),
        ],
            NetworkConnectionKind.OpenVpn =>
            [
                new(NetworkCredentialTarget.OpenVpnConfiguration, "OpenVPN profile", SecretKind.Other),
                new(NetworkCredentialTarget.OpenVpnPassword, "OpenVPN password", SecretKind.Password),
        ],
            NetworkConnectionKind.AnyConnect =>
            [
                new(NetworkCredentialTarget.AnyConnectPassword, "AnyConnect password", SecretKind.Password),
            new(NetworkCredentialTarget.AnyConnectClientCertificate, "Client certificate", SecretKind.Certificate),
        ],
            NetworkConnectionKind.Tailscale =>
            [
                new(NetworkCredentialTarget.TailscaleAuthKey, "Tailscale auth key", SecretKind.ApiKey),
        ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}
