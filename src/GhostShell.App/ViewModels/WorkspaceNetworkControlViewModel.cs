using System.Collections.ObjectModel;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Presents and changes the live network route for one running workspace. The
/// durable policy stays unchanged; this control owns only the runtime choice.
/// </summary>
public sealed class WorkspaceNetworkControlViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IWorkspaceNetworkSession? _session;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly Action<WorkspaceNetworkSnapshot>? _applyEgress;
    private readonly IWorkspaceNetworkConnector? _networkConnector;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<WorkspaceNetworkConnectionOptionViewModel> _connections = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private WorkspaceNetworkPolicyUpdate _policy;
    private WorkspaceNetworkPolicyUpdate _catalogPolicy;
    private WorkspaceNetworkSnapshot _snapshot;
    private string? _progressStatus;
    private bool _isApplying;
    private bool _disposed;

    public WorkspaceNetworkControlViewModel(
        WorkspaceNetworkPolicyUpdate policy,
        IWorkspaceNetworkSession? session,
        IUiThreadDispatcher dispatcher,
        Action<WorkspaceNetworkSnapshot>? applyEgress = null,
        IWorkspaceNetworkConnector? networkConnector = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _catalogPolicy = policy;
        _session = session;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _applyEgress = applyEgress;
        _networkConnector = networkConnector;
        networkConnector?.BrowserAuthenticationRouteFailed += OnBrowserAuthenticationRouteFailed;
        _snapshot = session?.Snapshot ?? UnavailableSnapshot(policy.Policy);
        Connections = new ReadOnlyObservableCollection<WorkspaceNetworkConnectionOptionViewModel>(
            _connections);
        RebuildConnections();
        RefreshSelection();
        _applyEgress?.Invoke(_snapshot);
        if (_session is { } activeSession)
        {
            activeSession.Changed += OnSessionChanged;
        }
    }

    public ReadOnlyObservableCollection<WorkspaceNetworkConnectionOptionViewModel> Connections { get; }

    public WorkspaceNetworkSnapshot Snapshot => _snapshot;

    public bool HasConnections => Connections.Count > 0;

    public bool IsNetworkingEnabled => _policy.Policy.IsEnabled;

    public bool IsKillSwitchEnabled => _policy.Policy.KillSwitchEnabled;

    public bool IsApplying => _isApplying;

    public bool IsConnecting =>
        _snapshot.State == WorkspaceNetworkState.Connecting || _isApplying;

    public bool IsConnected => _snapshot.State == WorkspaceNetworkState.Connected;

    public bool IsFailed => _snapshot.State == WorkspaceNetworkState.Failed;

    public bool IsBlocked => _snapshot.State == WorkspaceNetworkState.Blocked;

    public bool CanToggle => _session is not null && HasConnections && !_isApplying;

    public string ToggleAction => IsNetworkingEnabled ? "Turn off" : "Connect";

    /// <summary>What the title bar names: the route, or the absence of one.</summary>
    public string RouteLabel => _snapshot.State == WorkspaceNetworkState.Direct
        ? "Direct"
        : SelectedName;

    /// <summary>The state on its own, for the chip that colours it.</summary>
    public string StateLabel => _snapshot.State switch
    {
        WorkspaceNetworkState.Direct => "Off",
        WorkspaceNetworkState.Connecting => "Connecting",
        WorkspaceNetworkState.Connected => "Connected",
        WorkspaceNetworkState.Failed => "Failed",
        WorkspaceNetworkState.Blocked => "Blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(_snapshot), _snapshot.State, null),
    };

    public bool IsDirect => _snapshot.State == WorkspaceNetworkState.Direct;

    /// <summary>A route that is not carrying traffic the way it was asked to.</summary>
    public bool IsDegraded => IsFailed || IsBlocked;

    public string CompactStatus => _snapshot.State switch
    {
        WorkspaceNetworkState.Direct => "Direct",
        WorkspaceNetworkState.Connecting => $"{SelectedName} · Connecting",
        WorkspaceNetworkState.Connected => $"{SelectedName} · Connected",
        WorkspaceNetworkState.Failed => $"{SelectedName} · Failed",
        WorkspaceNetworkState.Blocked => "Traffic blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(_snapshot), _snapshot.State, null),
    };

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_progressStatus) && IsConnecting)
            {
                return _progressStatus;
            }

            return _snapshot.State switch
            {
                WorkspaceNetworkState.Direct when _session is null && HasConnections =>
                    "Workspace networking is unavailable in this build.",
                WorkspaceNetworkState.Direct =>
                    "Networking is off. Workspace traffic uses its direct connection.",
                WorkspaceNetworkState.Connecting => $"Connecting to {SelectedName}.",
                WorkspaceNetworkState.Connected when _snapshot.RouteCapabilities is { } capabilities
                    && !capabilities.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Udp)
                    => $"Workspace TCP traffic and DNS use {SelectedName}. This route does not support other UDP traffic or ping.",
                WorkspaceNetworkState.Connected when _snapshot.RouteCapabilities is { } capabilities
                    && !capabilities.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.ControlMessages)
                    => $"Workspace TCP and UDP traffic use {SelectedName}. This route does not support ping or other IP protocols.",
                WorkspaceNetworkState.Connected => $"Workspace traffic uses {SelectedName}.",
                WorkspaceNetworkState.Failed =>
                    _snapshot.Error?.Message ?? "The network connection failed.",
                WorkspaceNetworkState.Blocked when _snapshot.Error is { } error =>
                    $"Workspace traffic is blocked. {error.Message}",
                WorkspaceNetworkState.Blocked =>
                    "Workspace traffic is blocked.",
                _ => throw new ArgumentOutOfRangeException(nameof(_snapshot), _snapshot.State, null),
            };
        }
    }

    public string AutomationLabel => $"Workspace network. {CompactStatus}. {StatusText}";

    public async Task SelectAsync(
        NetworkConnectionId connectionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SelectCoreAsync(connectionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task UpdatePolicyAsync(
        WorkspaceNetworkPolicyUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (MatchesCatalogPolicy(update))
            {
                return;
            }

            _catalogPolicy = update;
            _policy = update;
            RebuildConnections();
            RefreshSelection();
            if (_session is null)
            {
                ApplySnapshot(UnavailableSnapshot(update.Policy));
                return;
            }

            await ApplyUpdateAsync(
                    update,
                    update.Policy.IsEnabled
                        ? $"Applying {NameOf(update.Policy.SelectedConnectionId!.Value)}."
                        : "Turning networking off.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task SelectCoreAsync(
        NetworkConnectionId connectionId,
        CancellationToken cancellationToken)
    {
        if (_policy.Policy.Connections.All(id => id != connectionId))
        {
            throw new ArgumentException(
                "The network connection is not available to this workspace.",
                nameof(connectionId));
        }

        if (_policy.Policy is { IsEnabled: true, SelectedConnectionId: { } selected }
            && selected == connectionId
            && _snapshot.State is WorkspaceNetworkState.Connected or WorkspaceNetworkState.Connecting)
        {
            return;
        }

        await ApplyAsync(connectionId, enabled: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task ToggleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selected = _policy.Policy.SelectedConnectionId
                ?? Connections.FirstOrDefault()?.Id;
            if (selected is null || _session is null || _isApplying)
            {
                return;
            }

            await ApplyAsync(
                    selected.Value,
                    enabled: !_policy.Policy.IsEnabled,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _networkConnector?.BrowserAuthenticationRouteFailed -= OnBrowserAuthenticationRouteFailed;
        _lifetime.Cancel();
        if (_session is { } activeSession)
        {
            activeSession.Changed -= OnSessionChanged;
        }
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        _operationGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ApplyAsync(
        NetworkConnectionId selected,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (_session is null || _isApplying)
        {
            return;
        }

        var nextPolicy = new NetworkPolicy(
            _policy.Policy.Connections,
            selected,
            enabled,
            _policy.Policy.KillSwitchEnabled);
        var update = new WorkspaceNetworkPolicyUpdate(nextPolicy, _policy.Connections);
        _policy = update;
        RefreshSelection();
        await ApplyUpdateAsync(
                update,
                enabled ? $"Connecting to {NameOf(selected)}." : "Turning networking off.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyUpdateAsync(
        WorkspaceNetworkPolicyUpdate update,
        string progressStatus,
        CancellationToken cancellationToken)
    {
        var session = _session
            ?? throw new InvalidOperationException("The workspace network session is unavailable.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        _progressStatus = progressStatus;
        SetApplying(true);

        try
        {
            var progress = new Progress<NetworkConnectionProgress>(value =>
            {
                _progressStatus = value.Status;
                NotifyStatusChanged();
            });
            var result = await session.ApplyAsync(update, progress, operation.Token)
                .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(
                    () => ApplyResult(result),
                    operation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!_disposed)
            {
                await _dispatcher.InvokeAsync(
                        () =>
                        {
                            _progressStatus = null;
                            SetApplying(false);
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private void ApplyResult(NetworkConnectionResult<WorkspaceNetworkSnapshot> result)
    {
        var snapshot = result switch
        {
            NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success success => success.Value,
            NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure =>
                _session?.Snapshot ?? _snapshot,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        ApplySnapshot(snapshot);
    }

    private async void OnSessionChanged(
        object? sender,
        WorkspaceNetworkSnapshot snapshot)
    {
        _ = sender;
        try
        {
            await _dispatcher.InvokeAsync(
                () => ApplySnapshot(snapshot),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnBrowserAuthenticationRouteFailed(object? sender, EventArgs args)
    {
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _snapshot = new WorkspaceNetworkSnapshot(WorkspaceNetworkState.Blocked,
                    WorkspaceNetworkEgress.Blocked, _snapshot.SelectedConnectionId,
                    new NetworkConnectionError(NetworkConnectionErrorCode.RouteUnavailable,
                        "browser_auth_route_reset_failed",
                        "Traffic stays blocked because saved browser authentication could not be cleared for the new route. Select the connection again to retry.",
                        retryable: true));
                NotifyStatusChanged();
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void ApplySnapshot(WorkspaceNetworkSnapshot snapshot)
    {
        _snapshot = snapshot;
        _applyEgress?.Invoke(snapshot);
        NotifyStatusChanged();
    }

    private void SetApplying(bool value)
    {
        if (!SetProperty(ref _isApplying, value, nameof(IsApplying)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleAction));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AutomationLabel));
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(IsNetworkingEnabled));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsDirect));
        OnPropertyChanged(nameof(IsDegraded));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(ToggleAction));
        OnPropertyChanged(nameof(RouteLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(CompactStatus));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AutomationLabel));
    }

    private void RefreshSelection()
    {
        foreach (var connection in Connections)
        {
            connection.IsSelected = connection.Id == _policy.Policy.SelectedConnectionId;
        }

        NotifyStatusChanged();
    }

    private void RebuildConnections()
    {
        var profiles = _policy.Connections.ToDictionary(connection => connection.Id);
        _connections.Clear();
        foreach (var id in _policy.Policy.Connections)
        {
            var profile = profiles[id];
            _connections.Add(new WorkspaceNetworkConnectionOptionViewModel(
                profile.Id,
                profile.Name,
                KindLabel(profile.ConnectionKind)));
        }

        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(CanToggle));
    }

    // The window picker changes the live policy, not saved settings. Compare
    // catalog refreshes with their previous input so browser/tab autosave cannot
    // undo a runtime selection or turn a manually disabled VPN back on.
    private bool MatchesCatalogPolicy(WorkspaceNetworkPolicyUpdate update) =>
        _catalogPolicy.Policy.IsEnabled == update.Policy.IsEnabled
        && _catalogPolicy.Policy.KillSwitchEnabled == update.Policy.KillSwitchEnabled
        && _catalogPolicy.Policy.SelectedConnectionId == update.Policy.SelectedConnectionId
        && _catalogPolicy.Policy.Connections.SequenceEqual(update.Policy.Connections)
        && _catalogPolicy.Connections.SequenceEqual(update.Connections);

    private string SelectedName => _snapshot.SelectedConnectionId is { } selected
        ? NameOf(selected)
        : "Network";

    private string NameOf(NetworkConnectionId id) =>
        Connections.FirstOrDefault(connection => connection.Id == id)?.Name
        ?? "network connection";

    private static WorkspaceNetworkSnapshot UnavailableSnapshot(NetworkPolicy policy)
    {
        if (!policy.IsEnabled)
        {
            return WorkspaceNetworkSnapshot.Direct;
        }

        var error = new NetworkConnectionError(
            NetworkConnectionErrorCode.RuntimeMissing,
            "workspace_network_runtime_missing",
            "Workspace networking is unavailable in this build.",
            retryable: false);
        return policy.KillSwitchEnabled
            ? new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Blocked,
                WorkspaceNetworkEgress.Blocked,
                policy.SelectedConnectionId,
                error)
            : new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Failed,
                WorkspaceNetworkEgress.Direct,
                policy.SelectedConnectionId,
                error);
    }

    private static string KindLabel(NetworkConnectionKind kind) => kind switch
    {
        NetworkConnectionKind.Proxy => "Proxy",
        NetworkConnectionKind.WireGuard => "WireGuard",
        NetworkConnectionKind.OpenVpn => "OpenVPN",
        NetworkConnectionKind.AnyConnect => "AnyConnect",
        NetworkConnectionKind.Tailscale => "Tailscale",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

public sealed class WorkspaceNetworkConnectionOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public WorkspaceNetworkConnectionOptionViewModel(
        NetworkConnectionId id,
        string name,
        string kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    public NetworkConnectionId Id { get; }

    public string Name { get; }

    public string Kind { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
