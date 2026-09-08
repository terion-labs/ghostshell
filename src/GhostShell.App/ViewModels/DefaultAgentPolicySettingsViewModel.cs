using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns default agent-policy editing, explicit and background persistence,
/// provider-option refresh, and the coordinator subscription.
/// </summary>
public sealed class DefaultAgentPolicySettingsViewModel : ObservableObject, IDisposable
{
    private readonly AgentPolicyCoordinator? _coordinator;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly Action<string> _setError;
    private readonly Action _clearError;
    private readonly object _saveGate = new();
    private AgentPolicy? _pendingPolicy;
    private Task _saveTask = Task.CompletedTask;
    private bool _saveRunning;
    private bool _sealed;
    private bool _disposed;

    public DefaultAgentPolicySettingsViewModel(
        AgentPolicyCoordinator? coordinator,
        IReadOnlyList<AiProviderProfileDescriptor>? providers,
        IUiThreadDispatcher dispatcher,
        Action<string> setError,
        Action clearError)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _clearError = clearError ?? throw new ArgumentNullException(nameof(clearError));
        Editor = CreateEditor(_coordinator?.Policy, providers);
        Editor.Changed += OnEditorChanged;
        if (_coordinator is { } activeCoordinator)
        {
            activeCoordinator.Changed += OnCoordinatorChanged;
            if (activeCoordinator.InitializationError is { } error)
            {
                _setError($"{error.Message} Agent capabilities are disabled until you review and save the default AI configuration.");
            }
        }
    }

    public event EventHandler? PolicyChanged;

    public SavedScreenAgentPolicyEditorViewModel Editor { get; private set; }

    public AgentPolicy? Policy => _coordinator?.Policy;

    public bool CanSave => _coordinator is not null && Editor.IsValid;

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_coordinator is null)
        {
            _setError("Default AI configuration storage is unavailable.");
            return;
        }

        AgentPolicy? policy;
        try
        {
            policy = Editor.Build();
        }
        catch (ArgumentException exception)
        {
            _setError(exception.Message);
            return;
        }

        if (policy is null)
        {
            _setError("The default AI configuration cannot be disabled.");
            return;
        }

        var result = await _coordinator.SaveAsync(policy, cancellationToken);
        if (!result.IsSuccess)
        {
            _setError(result.Error!.Message);
            return;
        }

        _clearError();
    }

    public void RefreshProviders(
        IReadOnlyList<AiProviderProfileDescriptor>? providers)
    {
        ThrowIfDisposed();
        AgentPolicy? draft = null;
        if (Editor.IsValid)
        {
            draft = Editor.Build();
        }

        Editor.Changed -= OnEditorChanged;
        Editor.Dispose();
        Editor = CreateEditor(draft ?? _coordinator?.Policy, providers);
        Editor.Changed += OnEditorChanged;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(CanSave));
        QueuePersistence(onlyWhenMissing: true);
    }

    public void QueuePersistence(bool onlyWhenMissing)
    {
        ThrowIfDisposed();
        if (_sealed
            || _coordinator is null
            || _coordinator.InitializationError is not null
            || onlyWhenMissing && _coordinator.Policy is not null
            || !Editor.IsValid)
        {
            return;
        }

        AgentPolicy? policy;
        try
        {
            policy = Editor.Build();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (policy is null)
        {
            return;
        }

        lock (_saveGate)
        {
            _pendingPolicy = policy;
            if (_saveRunning)
            {
                return;
            }

            _saveRunning = true;
            _saveTask = PersistQueuedPoliciesAsync();
        }
    }

    public async Task QuiesceAsync()
    {
        Seal();
        Task pending;
        lock (_saveGate)
        {
            pending = _saveTask;
        }

        await pending.ConfigureAwait(false);
    }

    public void Seal()
    {
        _sealed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sealed = true;
        if (_coordinator is { } activeCoordinator)
        {
            activeCoordinator.Changed -= OnCoordinatorChanged;
        }

        Editor.Changed -= OnEditorChanged;
        Editor.Dispose();
        PolicyChanged = null;
    }

    private async Task PersistQueuedPoliciesAsync()
    {
        while (true)
        {
            AgentPolicy? policy;
            lock (_saveGate)
            {
                policy = _pendingPolicy;
                _pendingPolicy = null;
                if (policy is null)
                {
                    _saveRunning = false;
                    return;
                }
            }

            var result = await _coordinator!
                .SaveAsync(policy, CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.IsSuccess && !_disposed)
            {
                await _dispatcher.InvokeAsync(
                        () => _setError(result.Error!.Message),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async void OnCoordinatorChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            await _dispatcher.InvokeAsync(
                    () =>
                    {
                        if (!_disposed)
                        {
                            PolicyChanged?.Invoke(this, EventArgs.Empty);
                        }
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    private void OnEditorChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        OnPropertyChanged(nameof(CanSave));
        QueuePersistence(onlyWhenMissing: false);
    }

    private static SavedScreenAgentPolicyEditorViewModel CreateEditor(
        AgentPolicy? policy,
        IReadOnlyList<AiProviderProfileDescriptor>? providers) =>
        new(policy, providers)
        {
            IsEnabled = true,
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
