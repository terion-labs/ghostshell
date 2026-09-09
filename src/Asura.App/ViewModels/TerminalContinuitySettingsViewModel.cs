using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record ManagedRemoteSessionViewModel(
    TerminalMultiplexerLease Lease,
    string ConnectionName)
{
    public string SessionName => Lease.Session.SessionName;

    public string Status => Lease.State == TerminalMultiplexerLeaseState.Active
        ? "Detached or active"
        : "Cleanup pending";

    public bool IsCleanupPending =>
        Lease.State == TerminalMultiplexerLeaseState.TerminationPending;
}

/// <summary>
/// Owns terminal-continuity preference persistence, managed-session mutation,
/// live lease subscription, and presentation projection.
/// </summary>
public sealed class TerminalContinuitySettingsViewModel : ObservableObject, IDisposable
{
    private readonly TerminalMultiplexerCoordinator? _coordinator;
    private readonly Func<ConnectionId, ConnectionProfile?> _resolveConnection;
    private readonly Action<string> _setError;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private TerminalMultiplexingMode _mode;
    private bool _preferenceLoaded;
    private bool _preferenceSaving;
    private bool _disposed;

    public TerminalContinuitySettingsViewModel(
        TerminalMultiplexerCoordinator? coordinator,
        Func<ConnectionId, ConnectionProfile?> resolveConnection,
        Action<string> setError,
        IUiThreadDispatcher dispatcher)
    {
        _coordinator = coordinator;
        _resolveConnection = resolveConnection
            ?? throw new ArgumentNullException(nameof(resolveConnection));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (_coordinator is { } activeCoordinator)
        {
            activeCoordinator.LeasesChanged += OnLeasesChanged;
        }
    }

    public ObservableCollection<ManagedRemoteSessionViewModel> ManagedSessions { get; } = [];

    public TerminalMultiplexingMode Mode => _mode;

    public bool UseForSshTerminals => _mode == TerminalMultiplexingMode.Automatic;

    public bool CanChange =>
        _coordinator is null || (_preferenceLoaded && !_preferenceSaving);

    public bool HasManagedSessions => ManagedSessions.Count > 0;

    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_coordinator is null)
        {
            _preferenceLoaded = true;
            OnPropertyChanged(nameof(CanChange));
            return true;
        }

        var preference = await _coordinator.ReadPreferenceAsync(cancellationToken);
        var leases = await _coordinator.ListAsync(cancellationToken);
        if (!preference.IsSuccess || !leases.IsSuccess)
        {
            _setError("Terminal continuity settings could not be loaded.");
            return false;
        }

        _mode = preference.Value;
        _preferenceLoaded = true;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(UseForSshTerminals));
        OnPropertyChanged(nameof(CanChange));
        Project(leases.Value!);
        return true;
    }

    public async Task<bool> SetUseForSshTerminalsAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var mode = enabled
            ? TerminalMultiplexingMode.Automatic
            : TerminalMultiplexingMode.Disabled;
        if (_coordinator is null)
        {
            SetMode(mode);
            return true;
        }

        if (!_preferenceLoaded || _preferenceSaving)
        {
            return false;
        }

        _preferenceSaving = true;
        OnPropertyChanged(nameof(CanChange));
        try
        {
            var result = await _coordinator.WritePreferenceAsync(
                mode,
                cancellationToken);
            if (!result.IsSuccess)
            {
                _setError("Terminal continuity settings could not be saved.");
                OnPropertyChanged(nameof(UseForSshTerminals));
                return false;
            }

            SetMode(mode);
            return true;
        }
        finally
        {
            _preferenceSaving = false;
            OnPropertyChanged(nameof(CanChange));
        }
    }

    public async Task TerminateAsync(
        ManagedRemoteSessionViewModel item,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (_coordinator is null
            || _resolveConnection(item.Lease.ConnectionId) is not { } connection)
        {
            _setError("The connection for this managed remote session is unavailable.");
            return;
        }

        var result = await _coordinator.TerminateAsync(
            connection,
            item.Lease.Session,
            cancellationToken);
        if (!result.Terminated)
        {
            _setError(result.Detail);
        }

        await RefreshSessionsAsync(cancellationToken);
    }

    public async Task ForgetAsync(
        ManagedRemoteSessionViewModel item,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(item);
        if (_coordinator is null)
        {
            return;
        }

        _ = await _coordinator.ForgetAsync(item.Lease, cancellationToken);
        await RefreshSessionsAsync(cancellationToken);
    }

    public async Task RefreshSessionsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_coordinator is null)
        {
            return;
        }

        var result = await _coordinator.ListAsync(cancellationToken);
        if (result.IsSuccess)
        {
            Project(result.Value!);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is { } activeCoordinator)
        {
            activeCoordinator.LeasesChanged -= OnLeasesChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async void OnLeasesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_coordinator is null || _disposed)
        {
            return;
        }

        try
        {
            var result = await _coordinator.ListAsync(_lifetime.Token);
            if (!result.IsSuccess || _disposed)
            {
                return;
            }

            await _dispatcher.InvokeAsync(
                () => Project(result.Value!),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void Project(IReadOnlyList<TerminalMultiplexerLease> leases)
    {
        ManagedSessions.Clear();
        foreach (var lease in leases)
        {
            ManagedSessions.Add(new ManagedRemoteSessionViewModel(
                lease,
                _resolveConnection(lease.ConnectionId)?.Name
                    ?? lease.ConnectionId.Value));
        }

        OnPropertyChanged(nameof(ManagedSessions));
        OnPropertyChanged(nameof(HasManagedSessions));
    }

    private void SetMode(TerminalMultiplexingMode mode)
    {
        _mode = mode;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(UseForSshTerminals));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
