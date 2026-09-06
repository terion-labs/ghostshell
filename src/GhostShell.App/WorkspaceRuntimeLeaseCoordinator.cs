using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

/// <summary>
/// Owns the non-visual lifetime of workspace execution routes. A lease exists
/// for every running workspace; an isolation binding is only one possible
/// backing for that provider-neutral route.
/// </summary>
internal sealed class WorkspaceRuntimeLeaseCoordinator(
    IConnectionRuntime hostConnectionRuntime,
    WorkspaceRuntimeServices hostServices,
    IWorkspaceIsolationProvider? isolationProvider,
    IWorkspaceRuntimeServicesFactory? servicesFactory)
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkspaceInstanceId, WorkspaceRuntimeLease> _leases = [];
    private readonly Dictionary<WorkspaceId, string> _knownIsolationImages = [];
    private readonly Dictionary<Guid, WorkspaceIsolationBinding> _preparedBindings = [];
    private readonly Dictionary<WorkspaceInstanceId, Task<WorkspaceIsolationError?>>
        _cleanupTasks = [];
    private readonly Dictionary<Guid, Task> _preparationTasks = [];
    private readonly Dictionary<Guid, Task> _activationTasks = [];
    private readonly HashSet<WorkspaceInstanceId> _pendingGraphRollbacks = [];
    private bool _windowClosePending;

    public bool TryBeginPreparation(
        bool isClosing,
        out Guid operationId,
        out TaskCompletionSource completion) =>
        TryBeginOperation(
            _preparationTasks,
            isClosing,
            checkWindowClose: false,
            out operationId,
            out completion);

    public void CompletePreparation(Guid operationId, TaskCompletionSource completion) =>
        CompleteOperation(_preparationTasks, operationId, completion);

    public Task AwaitPreparationsAsync() => AwaitOperationsAsync(_preparationTasks);

    public bool TryBeginActivation(
        bool isClosing,
        out Guid operationId,
        out TaskCompletionSource completion) =>
        TryBeginOperation(
            _activationTasks,
            isClosing,
            checkWindowClose: true,
            out operationId,
            out completion);

    public void CompleteActivation(Guid operationId, TaskCompletionSource completion) =>
        CompleteOperation(_activationTasks, operationId, completion);

    public Task AwaitActivationsAsync() => AwaitOperationsAsync(_activationTasks);

    public Task[] BeginWindowCloseActivationDrain()
    {
        lock (_gate)
        {
            _windowClosePending = true;
            return [.. _activationTasks.Values];
        }
    }

    public void ResumeAfterWindowCloseAttempt(bool shutdownStarted)
    {
        lock (_gate)
        {
            if (!shutdownStarted)
            {
                _windowClosePending = false;
            }
        }
    }

    public string? KnownIsolationImage(WorkspaceId workspaceId)
    {
        lock (_gate)
        {
            return _knownIsolationImages.GetValueOrDefault(workspaceId);
        }
    }

    public void OwnPreparedBinding(WorkspaceIsolationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (!_preparedBindings.TryAdd(binding.LeaseId, binding))
            {
                throw new InvalidOperationException(
                    "The workspace isolation binding is already owned by this window.");
            }
        }
    }

    public void TryOwnPreparedBinding(WorkspaceIsolationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            _preparedBindings.TryAdd(binding.LeaseId, binding);
        }
    }

    public void Register(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding? binding)
    {
        lock (_gate)
        {
            if (_leases.ContainsKey(workspaceId))
            {
                throw new InvalidOperationException(
                    "The workspace already owns a runtime lease.");
            }

            if (binding is not null && !_preparedBindings.ContainsKey(binding.LeaseId))
            {
                throw new InvalidOperationException(
                    "The workspace isolation binding is not owned by this window.");
            }

            var networkEgressState = new WorkspaceNetworkEgressState();
            IConnectionRuntime connectionRuntime = new WorkspaceNetworkConnectionRuntime(
                hostConnectionRuntime,
                networkEgressState,
                injectProxyEnvironment: binding is null);
            if (binding is not null)
            {
                connectionRuntime = new WorkspaceIsolatedConnectionRuntime(
                    connectionRuntime,
                    isolationProvider
                        ?? throw new InvalidOperationException(
                            "An isolated runtime workspace requires an isolation provider."),
                    binding);
            }

            var runtimeServices = servicesFactory?.Create(
                    new WorkspaceRuntimeServicesRequest(
                        workspaceId,
                        connectionRuntime,
                        hostServices,
                        binding,
                        networkEgressState))
                ?? (binding is null
                    ? new WorkspaceRuntimeServices(
                        hostServices.Backends,
                        hostServices.NetworkRoute,
                        networkEgressSink: networkEgressState)
                    : null);
            if (ReferenceEquals(runtimeServices, hostServices))
            {
                runtimeServices = new WorkspaceRuntimeServices(
                    hostServices.Backends,
                    hostServices.NetworkRoute,
                    networkEgressSink: networkEgressState);
            }
            _leases.Add(
                workspaceId,
                new WorkspaceRuntimeLease(
                    binding,
                    connectionRuntime,
                    runtimeServices,
                    ProviderStopPending: binding is not null));
            if (binding?.RuntimeImageReference is { } runtimeImageReference)
            {
                _knownIsolationImages[binding.WorkspaceId] = runtimeImageReference;
            }

            if (binding is not null)
            {
                _preparedBindings.Remove(binding.LeaseId);
            }
        }
    }

    public IConnectionRuntime? ConnectionRuntimeFor(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _leases.GetValueOrDefault(workspaceId)?.ConnectionRuntime;
        }
    }

    public WorkspaceRuntimeServices? RuntimeServicesFor(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _leases.GetValueOrDefault(workspaceId)?.RuntimeServices;
        }
    }

    public WorkspaceRuntimeCleanupSchedule ScheduleRelease(
        WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            if (_cleanupTasks.TryGetValue(workspaceId, out var existing))
            {
                return new WorkspaceRuntimeCleanupSchedule(existing, IsNew: false);
            }

            var cleanup = ReleaseSafelyAsync(workspaceId);
            _cleanupTasks.Add(workspaceId, cleanup);
            return new WorkspaceRuntimeCleanupSchedule(cleanup, IsNew: true);
        }
    }

    public async Task AwaitScheduledCleanupAsync()
    {
        Task<WorkspaceIsolationError?>[] cleanupTasks;
        lock (_gate)
        {
            cleanupTasks = [.. _cleanupTasks.Values];
        }

        if (cleanupTasks.Length > 0)
        {
            await Task.WhenAll(cleanupTasks).ConfigureAwait(false);
        }
    }

    public async ValueTask<WorkspaceIsolationError?> ReleaseAsync(
        WorkspaceInstanceId workspaceId,
        CancellationToken cancellationToken)
    {
        WorkspaceRuntimeLease? lease;
        lock (_gate)
        {
            _leases.Remove(workspaceId, out lease);
        }

        if (lease is null)
        {
            return null;
        }

        var release = await TryReleaseLeaseAsync(lease, cancellationToken);
        if (!release.IsComplete)
        {
            lock (_gate)
            {
                _leases.TryAdd(workspaceId, release.Lease);
            }
        }

        return release.Error;
    }

    public async ValueTask<WorkspaceIsolationError?> ReleasePreparedAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var wasPrepared = false;
        WorkspaceInstanceId? workspaceId = null;
        WorkspaceRuntimeLease? lease = null;
        lock (_gate)
        {
            wasPrepared = _preparedBindings.Remove(binding.LeaseId);
            var owned = _leases.FirstOrDefault(
                item => item.Value.Binding?.LeaseId == binding.LeaseId);
            if (owned.Value is not null)
            {
                workspaceId = owned.Key;
                lease = owned.Value;
                _leases.Remove(owned.Key);
            }
        }

        if (!wasPrepared && lease is null)
        {
            return null;
        }

        if (lease is not null)
        {
            var release = await TryReleaseLeaseAsync(lease, cancellationToken);
            if (!release.IsComplete)
            {
                lock (_gate)
                {
                    _leases.TryAdd(workspaceId!.Value, release.Lease);
                }
            }

            return release.Error;
        }

        var error = await StopIsolationAsync(binding, cancellationToken);
        if (error is not null && wasPrepared)
        {
            lock (_gate)
            {
                _preparedBindings.TryAdd(binding.LeaseId, binding);
            }
        }

        return error;
    }

    public async Task<IReadOnlyList<WorkspaceIsolationError>> ReleaseAllAsync()
    {
        WorkspaceInstanceId[] workspaceIds;
        WorkspaceIsolationBinding[] pending;
        lock (_gate)
        {
            workspaceIds = [.. _leases.Keys.Where(
                workspaceId => !_pendingGraphRollbacks.Contains(workspaceId))];
            pending = [.. _preparedBindings.Values];
        }

        var errors = new List<WorkspaceIsolationError>();
        foreach (var workspaceId in workspaceIds)
        {
            if (await ReleaseAsync(workspaceId, CancellationToken.None) is { } error)
            {
                errors.Add(error);
            }
        }

        foreach (var binding in pending)
        {
            if (await ReleasePreparedAsync(binding, CancellationToken.None) is { } error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    public void MarkGraphRegistrationAttempt(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            _pendingGraphRollbacks.Add(workspaceId);
        }
    }

    public void RetainGraph(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            _pendingGraphRollbacks.Remove(workspaceId);
        }
    }

    public bool RequiresGraphRollback(WorkspaceInstanceId workspaceId)
    {
        lock (_gate)
        {
            return _pendingGraphRollbacks.Contains(workspaceId);
        }
    }

    public IReadOnlyList<WorkspaceInstanceId> PendingGraphRollbacks()
    {
        lock (_gate)
        {
            return [.. _pendingGraphRollbacks];
        }
    }

    private bool TryBeginOperation(
        IDictionary<Guid, Task> operations,
        bool isClosing,
        bool checkWindowClose,
        out Guid operationId,
        out TaskCompletionSource completion)
    {
        operationId = Guid.NewGuid();
        completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (isClosing || (checkWindowClose && _windowClosePending))
            {
                return false;
            }

            operations.Add(operationId, completion.Task);
            return true;
        }
    }

    private void CompleteOperation(
        IDictionary<Guid, Task> operations,
        Guid operationId,
        TaskCompletionSource completion)
    {
        lock (_gate)
        {
            operations.Remove(operationId);
            completion.TrySetResult();
        }
    }

    private async Task AwaitOperationsAsync(IDictionary<Guid, Task> operations)
    {
        Task[] tasks;
        lock (_gate)
        {
            tasks = [.. operations.Values];
        }

        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task<WorkspaceIsolationError?> ReleaseSafelyAsync(
        WorkspaceInstanceId workspaceId)
    {
        try
        {
            return await ReleaseAsync(workspaceId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-runtime.release.failed",
                exception);
            return WorkspaceIsolationError.Create(WorkspaceIsolationErrorCode.StopFailed);
        }
    }

    private async ValueTask<WorkspaceRuntimeRelease> TryReleaseLeaseAsync(
        WorkspaceRuntimeLease lease,
        CancellationToken cancellationToken)
    {
        WorkspaceIsolationError? error = null;
        if (lease.RuntimeServices is not null)
        {
            try
            {
                await lease.RuntimeServices.DisposeAsync().ConfigureAwait(false);
                lease = lease with { RuntimeServices = null };
            }
            catch (Exception exception)
            {
                SecretSafeDiagnostics.WriteTraceAndStandardError(
                    "workspace-runtime.services.dispose.failed",
                    exception);
                error = WorkspaceIsolationError.Create(
                    WorkspaceIsolationErrorCode.StopFailed);
            }
        }

        if (lease.ProviderStopPending)
        {
            var binding = lease.Binding
                ?? throw new InvalidOperationException(
                    "A pending isolation stop requires a provider binding.");
            var stopError = await StopIsolationAsync(binding, cancellationToken);
            if (stopError is null)
            {
                lease = lease with { ProviderStopPending = false };
            }
            else
            {
                error = stopError;
            }
        }

        return new WorkspaceRuntimeRelease(lease, error);
    }

    private async ValueTask<WorkspaceIsolationError?> StopIsolationAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var provider = isolationProvider
            ?? throw new InvalidOperationException(
                "An isolated runtime workspace requires an isolation provider.");
        try
        {
            var result = await provider.StopAsync(binding, cancellationToken);
            return result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure
                ? failure.Error
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WorkspaceIsolationError.Create(WorkspaceIsolationErrorCode.Cancelled);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.stop.failed",
                exception);
            return WorkspaceIsolationError.Create(WorkspaceIsolationErrorCode.StopFailed);
        }
    }

    private sealed record WorkspaceRuntimeLease(
        WorkspaceIsolationBinding? Binding,
        IConnectionRuntime ConnectionRuntime,
        WorkspaceRuntimeServices? RuntimeServices,
        bool ProviderStopPending);

    private readonly record struct WorkspaceRuntimeRelease(
        WorkspaceRuntimeLease Lease,
        WorkspaceIsolationError? Error)
    {
        public bool IsComplete =>
            Lease.RuntimeServices is null && !Lease.ProviderStopPending;
    }
}

internal readonly record struct WorkspaceRuntimeCleanupSchedule(
    Task<WorkspaceIsolationError?> Completion,
    bool IsNew);
