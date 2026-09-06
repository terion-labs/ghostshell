using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IWorkspaceIsolationProvider? _workspaceIsolationProvider;
    private readonly IWorkspaceIsolationRuntimeInstaller?
        _workspaceIsolationRuntimeInstaller;
    private readonly IWorkspaceRuntimeServicesFactory?
        _workspaceRuntimeServicesFactory;
    private readonly WorkspaceRuntimeServices _hostWorkspaceRuntimeServices;
    private readonly WorkspaceRuntimeLeaseCoordinator _workspaceRuntimeLeases;
    private Guid? _workspaceIsolationStartupId;
    private WorkspaceId? _workspaceIsolationStartingWorkspaceId;
    private string? _workspaceIsolationStartingWorkspaceName;
    private string? _workspaceIsolationStartingStatus;

    public bool IsWorkspaceIsolationStarting =>
        _workspaceIsolationStartupId is not null;

    public string WorkspaceIsolationStartingHeading =>
        _workspaceIsolationStartingStatus
        ?? $"Starting {WorkspaceIsolationRuntimeDisplayName}…";

    public string WorkspaceIsolationStartingBody =>
        _workspaceIsolationStartingWorkspaceName is { } name
            ? $"Preparing the persistent isolate for “{name}”."
            : "Preparing the workspace's persistent isolate.";

    private string WorkspaceIsolationRuntimeDisplayName =>
        _workspaceIsolationRuntimeInstaller?.RuntimeDisplayName
        ?? _workspaceIsolationProvider?.Descriptor.DisplayName
        ?? "workspace isolation runtime";

    public bool CanInstallWorkspaceIsolationRuntime =>
        _workspaceIsolationProvider is null
        && _workspaceIsolationRuntimeInstaller is not null;

    private string? ActiveIsolationImageReference(WorkspaceId workspaceId)
    {
        foreach (var runtime in _openWorkspaces.Append(RuntimeWorkspace).OfType<RuntimeWorkspaceViewModel>())
        {
            if (_runtimeSources.TryGetValue(runtime.Id, out var source)
                && source.SourceDefinition.Kind == WorkspaceDefinition.Kind
                && string.Equals(
                    source.SourceDefinition.Value,
                    workspaceId.Value,
                    StringComparison.Ordinal))
            {
                return runtime.IsolationBinding?.RuntimeImageReference;
            }
        }

        return _workspaceRuntimeLeases.KnownIsolationImage(workspaceId);
    }

    private (bool Required, Uri? Proxy) AgentNetworkProxyFor(
        WorkspaceInstanceId workspaceId)
    {
        var runInIsolation = _runtimeSources.TryGetValue(workspaceId, out var source)
            && source.SourceDefinition.Kind == WorkspaceDefinition.Kind
            && _catalog.Snapshot.Workspaces.FirstOrDefault(
                item => item.Value.Key == source.SourceDefinition) is { } stored
            && stored.Value.RunAgentInIsolation;
        var runtimeServices = _workspaceRuntimeLeases.RuntimeServicesFor(workspaceId);
        if (runtimeServices?.IsNetworkBlocked is true)
        {
            return (true, null);
        }

        return (runInIsolation, runtimeServices?.EffectiveProxyUri);
    }

    public WorkspaceIsolationRuntimeInstallResult InstallWorkspaceIsolationRuntime()
    {
        ClearError();
        if (!CanInstallWorkspaceIsolationRuntime)
        {
            var unavailable = WorkspaceIsolationRuntimeInstallResult.Failure(
                "No workspace isolation runtime installer is available on this host.");
            SetError(unavailable.Error!);
            return unavailable;
        }

        var result = _workspaceIsolationRuntimeInstaller!.BeginInstallation();
        if (!result.Started)
        {
            SetError(result.Error!);
        }

        return result;
    }

    private void BeginWorkspaceIsolationStartup(
        WorkspaceDefinition workspace,
        Guid activationId)
    {
        _workspaceIsolationStartupId = activationId;
        _workspaceIsolationStartingWorkspaceId = workspace.Id;
        _workspaceIsolationStartingWorkspaceName = workspace.Name;
        _workspaceIsolationStartingStatus = null;
        OnPropertyChanged(nameof(IsWorkspaceIsolationStarting));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingHeading));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingBody));
        OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
        RefreshWorkspaceRuntimeFlags();

        // Navigation changes before runtime preparation is awaited. The
        // requested workspace is therefore the foreground surface while its
        // platform runtime starts, rather than an error appearing over the
        // workspace the user was leaving.
        Route = ShellRoute.Workspace;
    }

    private void CompleteWorkspaceIsolationStartup(Guid activationId)
    {
        if (_workspaceIsolationStartupId != activationId)
        {
            return;
        }

        _workspaceIsolationStartupId = null;
        _workspaceIsolationStartingWorkspaceId = null;
        _workspaceIsolationStartingWorkspaceName = null;
        _workspaceIsolationStartingStatus = null;
        OnPropertyChanged(nameof(IsWorkspaceIsolationStarting));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingHeading));
        OnPropertyChanged(nameof(WorkspaceIsolationStartingBody));
        OnPropertyChanged(nameof(IsWorkspaceCanvasVisible));
        RefreshWorkspaceRuntimeFlags();
    }

    private void ReportWorkspaceIsolationProgress(WorkspaceIsolationProgress progress)
    {
        if (_workspaceIsolationStartupId is null
            || string.Equals(
                _workspaceIsolationStartingStatus,
                progress.Status,
                StringComparison.Ordinal))
        {
            return;
        }

        _workspaceIsolationStartingStatus = progress.Status;
        OnPropertyChanged(nameof(WorkspaceIsolationStartingHeading));
    }

    private ValueTask<WorkspaceIsolationPreparation> PrepareWorkspaceIsolationAsync(
        WorkspaceDefinition workspace,
        CancellationToken cancellationToken)
    {
        if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
            SetError("This window is closing, so the workspace cannot be opened.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (!workspace.IsIsolated)
        {
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Host);
        }

        if (_workspaceIsolationProvider is null)
        {
            SetError("Workspace isolation is not available on this platform.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (!_workspaceRuntimeLeases.TryBeginPreparation(
                _shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested,
                out var preparationId,
                out var completion))
        {
            SetError("This window is closing, so the workspace cannot be opened.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        return PrepareTrackedWorkspaceIsolationAsync(
            workspace,
            preparationId,
            completion,
            cancellationToken);
    }

    private async ValueTask<WorkspaceIsolationPreparation>
        PrepareTrackedWorkspaceIsolationAsync(
            WorkspaceDefinition workspace,
            Guid preparationId,
            TaskCompletionSource completion,
            CancellationToken cancellationToken)
    {
        try
        {
            return await PrepareWorkspaceIsolationCoreAsync(workspace, cancellationToken);
        }
        catch (OperationCanceledException) when (_runtimeGraphLifetime.IsCancellationRequested)
        {
            return WorkspaceIsolationPreparation.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetError("Workspace isolation startup was cancelled.");
            return WorkspaceIsolationPreparation.Failed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.prepare.failed",
                exception);
            SetError("The workspace isolation runtime could not prepare the workspace.");
            return WorkspaceIsolationPreparation.Failed;
        }
        finally
        {
            _workspaceRuntimeLeases.CompletePreparation(preparationId, completion);
        }
    }

    private async ValueTask<WorkspaceIsolationPreparation>
        PrepareWorkspaceIsolationCoreAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken)
    {
        var provider = _workspaceIsolationProvider
            ?? throw new InvalidOperationException(
                "Workspace isolation preparation requires a platform provider.");
        var result = await provider.PrepareAsync(
            new WorkspaceIsolationPrepareRequest(
                workspace.Id,
                IsolationMountsOf(workspace),
                workspace.IsolationImageReference),
            new Progress<WorkspaceIsolationProgress>(ReportWorkspaceIsolationProgress),
            cancellationToken);
        if (result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure)
        {
            if (failure.CleanupValue is { } cleanupBinding)
            {
                _workspaceRuntimeLeases.TryOwnPreparedBinding(cleanupBinding);

                if (await ReleasePreparedWorkspaceIsolationAsync(
                        cleanupBinding,
                        CancellationToken.None) is not null)
                {
                    SecretSafeDiagnosticProjection.WriteStandardError(
                        "workspace-isolation.failed-prepare-stop.failed",
                        SecretSafeDiagnosticKind.Unexpected);
                }
            }

            SetError(failure.Error.Message);
            return WorkspaceIsolationPreparation.Failed;
        }

        var binding = ((WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)result).Value;
        if (binding.WorkspaceId != workspace.Id
            || binding.Provider != provider.Descriptor.Id)
        {
            _workspaceRuntimeLeases.TryOwnPreparedBinding(binding);

            var cleanupError = await ReleasePreparedWorkspaceIsolationAsync(
                binding,
                CancellationToken.None);
            SetError("The workspace isolation runtime returned an invalid workspace binding.");
            if (cleanupError is not null)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "workspace-isolation.invalid-binding-stop.failed",
                    SecretSafeDiagnosticKind.Unexpected);
            }

            return WorkspaceIsolationPreparation.Failed;
        }

        _workspaceRuntimeLeases.OwnPreparedBinding(binding);

        return new WorkspaceIsolationPreparation(true, binding);
    }

    private ValueTask<WorkspaceIsolationPreparation> PrepareRecoveredWorkspaceIsolationAsync(
        RuntimeWorkspaceRecoveryPayload recovered,
        CancellationToken cancellationToken)
    {
        if (recovered.HistorySource?.ToHistorySource() is not { } source
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            if (recovered.IsIsolated)
            {
                SetError(
                    "The recovered isolated workspace has no saved workspace identity. "
                    + "Recovery was stopped to prevent host execution.");
                return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
            }

            return ValueTask.FromResult(WorkspaceIsolationPreparation.Host);
        }

        var workspace = _catalog.Snapshot.Workspaces
            .Select(item => item.Value)
            .FirstOrDefault(candidate => candidate.Key == source.SourceDefinition);
        if (workspace is null)
        {
            if (recovered.IsIsolated)
            {
                SetError(
                    "The saved definition for this recovered isolated workspace is unavailable. "
                    + "Recovery was stopped to prevent host execution.");
                return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
            }

            return ValueTask.FromResult(WorkspaceIsolationPreparation.Host);
        }

        if (workspace.IsIsolated != recovered.IsIsolated)
        {
            SetError(
                "Workspace isolation changed since this recovery snapshot was written. "
                + "Discard the snapshot and reopen the workspace.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (workspace.IsIsolated
            && !IsolationMountsOf(workspace).SequenceEqual(
                recovered.IsolationMounts?.Select(mount => mount.ToMount()) ?? []))
        {
            SetError(
                "Workspace mounts changed since this recovery snapshot was written. "
                + "Discard the snapshot and reopen the workspace.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        if (workspace.IsIsolated
            && !string.Equals(
                workspace.IsolationImageReference,
                recovered.IsolationImageReference,
                StringComparison.Ordinal))
        {
            SetError(
                "The workspace runtime image changed since this recovery snapshot was written. "
                + "Discard the snapshot and reopen the workspace.");
            return ValueTask.FromResult(WorkspaceIsolationPreparation.Failed);
        }

        return PrepareWorkspaceIsolationAsync(workspace, cancellationToken);
    }

    private void RegisterWorkspaceConnectionRuntime(RuntimeWorkspaceViewModel workspace)
    {
        if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException(_runtimeGraphLifetime.Token);
        }

        _workspaceRuntimeLeases.Register(workspace.Id, workspace.IsolationBinding);
    }

    private IConnectionRuntime ConnectionRuntimeFor(WorkspaceInstanceId workspaceId)
    {
        var workspace = _openWorkspaces.FirstOrDefault(candidate => candidate.Id == workspaceId)
            ?? (RuntimeWorkspace?.Id == workspaceId ? RuntimeWorkspace : null);
        if (workspace is not null && !IsolationIntentMatches(workspace))
        {
            return WorkspaceIsolationUnavailableConnectionRuntime.ScopeChanged;
        }

        if (_workspaceRuntimeLeases.ConnectionRuntimeFor(workspaceId) is { } runtime)
        {
            return runtime;
        }

        return workspace is null || workspace.IsolationBinding is null
            ? _connectionRuntime
            : WorkspaceIsolationUnavailableConnectionRuntime.BindingMissing;
    }

    private bool IsolationIntentMatches(RuntimeWorkspaceViewModel runtime)
    {
        if (!_runtimeSources.TryGetValue(runtime.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind
            || _catalog.Snapshot.Workspaces.FirstOrDefault(
                item => item.Value.Key == source.SourceDefinition) is not { } stored)
        {
            return true;
        }

        if (stored.Value.IsIsolated != (runtime.IsolationBinding is not null))
        {
            return false;
        }

        return runtime.IsolationBinding is not { } binding
            || (IsolationMountsOf(stored.Value).SequenceEqual(binding.Mounts)
                && string.Equals(
                    stored.Value.IsolationImageReference,
                    binding.ImageReference,
                    StringComparison.Ordinal));
    }

    private bool WorkspaceIsolationConfigurationMatches(
        WorkspaceDefinition expected)
    {
        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == expected.Key);
        return current is not null
            && current.Value.IsIsolated == expected.IsIsolated
            && IsolationMountsOf(current.Value).SequenceEqual(
                IsolationMountsOf(expected))
            && string.Equals(
                current.Value.IsolationImageReference,
                expected.IsolationImageReference,
                StringComparison.Ordinal);
    }

    private bool RecoveredWorkspaceIsolationConfigurationMatches(
        RuntimeWorkspaceRecoveryPayload recovered)
    {
        if (recovered.HistorySource?.ToHistorySource() is not { } source
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return !recovered.IsIsolated;
        }

        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == source.SourceDefinition);
        return current is not null
            && current.Value.IsIsolated == recovered.IsIsolated
            && (!recovered.IsIsolated
                || (IsolationMountsOf(current.Value).SequenceEqual(
                        recovered.IsolationMounts?.Select(mount => mount.ToMount()) ?? [])
                    && string.Equals(
                        current.Value.IsolationImageReference,
                        recovered.IsolationImageReference,
                        StringComparison.Ordinal)));
    }

    private bool IsolationBadgeFor(WorkspaceDefinition definition) =>
        FindOpenWorkspace(definition.Key) is { } runtime
            ? runtime.IsolationBinding is not null
            : definition.IsIsolated;

    private static IReadOnlyList<WorkspaceIsolationMount> IsolationMountsOf(
        WorkspaceDefinition workspace) =>
        [.. workspace.IsolationMounts.Select(mount => new WorkspaceIsolationMount(
            mount.HostPath,
            mount.GuestPath,
            mount.IsReadOnly))];

    private async ValueTask<WorkspaceIsolationError?> ReleaseWorkspaceIsolationAsync(
        RuntimeWorkspaceViewModel workspace,
        CancellationToken cancellationToken) =>
        await ReleaseWorkspaceIsolationAsync(workspace.Id, cancellationToken);

    /// <summary>
    /// Starts the asynchronous part of closing an isolated workspace from sync-only
    /// graph and presentation callbacks. The task remains tracked until shutdown so
    /// every close path observes the same release attempt and shutdown can retry a
    /// failed stop without losing the exact lease.
    /// </summary>
    private Task<WorkspaceIsolationError?> ScheduleWorkspaceIsolationCleanup(
        RuntimeWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var schedule = _workspaceRuntimeLeases.ScheduleRelease(workspace.Id);
        return schedule.IsNew
            ? ReportWorkspaceRuntimeCleanupAsync(schedule.Completion)
            : schedule.Completion;
    }

    private async Task<WorkspaceIsolationError?> ReportWorkspaceRuntimeCleanupAsync(
        Task<WorkspaceIsolationError?> cleanup)
    {
        var error = await cleanup.ConfigureAwait(false);

        if (error is not null && !_shutdownStarted)
        {
            await _uiThreadDispatcher.InvokeAsync(
                    () => SetError(error.Message),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return error;
    }

    private async Task AwaitWorkspaceIsolationCleanupAsync()
        => await _workspaceRuntimeLeases.AwaitScheduledCleanupAsync()
            .ConfigureAwait(false);

    private async Task AwaitWorkspaceIsolationPreparationsAsync()
        => await _workspaceRuntimeLeases.AwaitPreparationsAsync().ConfigureAwait(false);

    private bool TryBeginWorkspaceActivation(
        out Guid activationId,
        out TaskCompletionSource completion)
    {
        return _workspaceRuntimeLeases.TryBeginActivation(
            _shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested,
            out activationId,
            out completion);
    }

    private void CompleteWorkspaceActivation(
        Guid activationId,
        TaskCompletionSource completion)
    {
        _workspaceRuntimeLeases.CompleteActivation(activationId, completion);
    }

    private async Task AwaitWorkspaceActivationsAsync()
        => await _workspaceRuntimeLeases.AwaitActivationsAsync().ConfigureAwait(false);

    private Task[] BeginWindowCloseActivationDrain()
        => _workspaceRuntimeLeases.BeginWindowCloseActivationDrain();

    internal void ResumeAfterWindowCloseAttempt()
        => _workspaceRuntimeLeases.ResumeAfterWindowCloseAttempt(_shutdownStarted);

    private void MarkWorkspaceGraphRegistrationAttempt(WorkspaceInstanceId workspaceId)
        => _workspaceRuntimeLeases.MarkGraphRegistrationAttempt(workspaceId);

    private void RetainWorkspaceGraph(WorkspaceInstanceId workspaceId)
        => _workspaceRuntimeLeases.RetainGraph(workspaceId);

    private bool RequiresWorkspaceGraphRollback(WorkspaceInstanceId workspaceId)
        => _workspaceRuntimeLeases.RequiresGraphRollback(workspaceId);

    private async Task<bool> TryRollbackWorkspaceGraphAsync(
        WorkspaceInstanceId workspaceId)
    {
        HostResult<CloseScopeResult> result;
        try
        {
            result = await RuntimeGraph.CloseAsync(
                    CloseScopeRequest.Workspace(workspaceId, CloseDecision.Confirm),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "runtime-graph.registration-rollback.failed",
                exception);
            return false;
        }

        if (result is not HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            }
            || completed.Scope != CloseScopeKind.Workspace
            || !string.Equals(
                completed.TargetId,
                workspaceId.Value,
                StringComparison.Ordinal)
            || completed.Sessions.Any(session => session.Outcome is not (
                SessionCloseOutcome.GracefullyClosed
                or SessionCloseOutcome.ForceTerminated
                or SessionCloseOutcome.AlreadyClosed)))
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "runtime-graph.registration-rollback.rejected",
                SecretSafeDiagnosticKind.Unexpected);
            return false;
        }

        _workspaceRuntimeLeases.RetainGraph(workspaceId);

        return true;
    }

    private async Task RetryWorkspaceGraphRollbacksAsync()
    {
        foreach (var workspaceId in _workspaceRuntimeLeases.PendingGraphRollbacks())
        {
            _ = await TryRollbackWorkspaceGraphAsync(workspaceId).ConfigureAwait(false);
        }
    }

    private async ValueTask<WorkspaceIsolationError?> ReleaseWorkspaceIsolationAsync(
        WorkspaceInstanceId workspaceId,
        CancellationToken cancellationToken)
        => await _workspaceRuntimeLeases.ReleaseAsync(workspaceId, cancellationToken);

    private async ValueTask<WorkspaceIsolationError?> ReleasePreparedWorkspaceIsolationAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
        => await _workspaceRuntimeLeases.ReleasePreparedAsync(binding, cancellationToken);

    private async Task<IReadOnlyList<WorkspaceIsolationError>> ReleaseAllWorkspaceIsolationAsync()
        => await _workspaceRuntimeLeases.ReleaseAllAsync();

    internal static bool SharesExecutionScope(
        RuntimeWorkspaceViewModel source,
        RuntimeWorkspaceViewModel destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return (source.IsolationBinding, destination.IsolationBinding) switch
        {
            (null, null) => true,
            ({ } sourceBinding, { } destinationBinding) =>
                sourceBinding.Provider == destinationBinding.Provider
                && string.Equals(
                    sourceBinding.ResourceName,
                    destinationBinding.ResourceName,
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private readonly record struct WorkspaceIsolationPreparation(
        bool Succeeded,
        WorkspaceIsolationBinding? Binding)
    {
        public static WorkspaceIsolationPreparation Host { get; } = new(true, null);

        public static WorkspaceIsolationPreparation Failed { get; } = new(false, null);
    }
}
