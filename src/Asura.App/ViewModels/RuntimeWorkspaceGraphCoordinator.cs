using System.Runtime.CompilerServices;

using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public enum RuntimeGraphStaleProposalHandling
{
    RefreshAndRetry,
    Reject,
}

/// <summary>
/// Owns validation and application of the session host's authoritative runtime
/// workspace revisions. Feature surfaces may propose a graph, but only this
/// coordinator advances host cursors or accepts a receipt/projection.
/// </summary>
public sealed class RuntimeWorkspaceGraphCoordinator : IDisposable
{
    private const int MutationAttemptCount = 2;
    private static readonly TimeSpan ReceiptReconciliationTimeout =
        TimeSpan.FromSeconds(1);
    private readonly ISessionHostClient? _sessionClient;
    private readonly ClientId _clientId;
    private readonly WindowInstanceId _windowId;
    private readonly IUiThreadDispatcher? _uiThreadDispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly Func<RuntimeWorkspaceViewModel?> _currentWorkspace;
    private readonly Action<RuntimeWorkspaceViewModel>? _workspaceRemoved;
    private readonly Action<string> _setError;
    private readonly Action _projectionApplied;
    private readonly Action<RuntimeWorkspaceViewModel> _workspaceCommitted;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watchGate = new();
    private readonly List<Task> _watchTasks = [];
    private CancellationTokenSource? _watchCancellation;
    private bool _disposed;

    public RuntimeWorkspaceGraphCoordinator(
        ISessionHostClient sessionClient,
        ClientId clientId,
        WindowInstanceId windowId,
        IUiThreadDispatcher uiThreadDispatcher,
        TimeProvider timeProvider,
        Func<RuntimeWorkspaceViewModel?> currentWorkspace,
        Action<RuntimeWorkspaceViewModel> workspaceRemoved,
        Action<string> setError,
        Action projectionApplied,
        Action<RuntimeWorkspaceViewModel> workspaceCommitted)
    {
        _sessionClient = sessionClient
            ?? throw new ArgumentNullException(nameof(sessionClient));
        _clientId = clientId;
        _windowId = windowId;
        _uiThreadDispatcher = uiThreadDispatcher
            ?? throw new ArgumentNullException(nameof(uiThreadDispatcher));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        _currentWorkspace = currentWorkspace
            ?? throw new ArgumentNullException(nameof(currentWorkspace));
        _workspaceRemoved = workspaceRemoved
            ?? throw new ArgumentNullException(nameof(workspaceRemoved));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _projectionApplied = projectionApplied
            ?? throw new ArgumentNullException(nameof(projectionApplied));
        _workspaceCommitted = workspaceCommitted
            ?? throw new ArgumentNullException(nameof(workspaceCommitted));
    }

    internal RuntimeWorkspaceGraphCoordinator(
        WindowInstanceId windowId,
        Func<RuntimeWorkspaceViewModel?> currentWorkspace,
        Action<string> setError,
        Action projectionApplied)
    {
        _clientId = default;
        _windowId = windowId;
        _timeProvider = TimeProvider.System;
        _currentWorkspace = currentWorkspace
            ?? throw new ArgumentNullException(nameof(currentWorkspace));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _projectionApplied = projectionApplied
            ?? throw new ArgumentNullException(nameof(projectionApplied));
        _workspaceCommitted = _ => { };
    }

    public CancellationToken LifetimeToken => _lifetime.Token;

    public bool IsStopping => _lifetime.IsCancellationRequested;

    internal SemaphoreSlim SerializationGate => _gate;

    public async ValueTask<RuntimeWorkspaceGraphLease> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _gate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            return new RuntimeWorkspaceGraphLease(_gate, linkedCancellation);
        }
        catch
        {
            linkedCancellation.Dispose();
            throw;
        }
    }

    public void StartWatching(RuntimeWorkspaceViewModel runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionClient is null
            || _uiThreadDispatcher is null
            || _workspaceRemoved is null)
        {
            throw new InvalidOperationException(
                "Workspace graph watching was not configured for this coordinator.");
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        lock (_watchGate)
        {
            if (IsStopping)
            {
                cancellation.Dispose();
                return;
            }

            _watchCancellation = cancellation;
            _watchTasks.Add(WatchAsync(
                runtime,
                runtime.HostSequence,
                cancellation.Token));
        }
    }

    public void StopWatching()
    {
        CancellationTokenSource? cancellation;
        lock (_watchGate)
        {
            cancellation = _watchCancellation;
            _watchCancellation = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    public async Task QuiesceAsync()
    {
        _lifetime.Cancel();
        StopWatching();
        Task[] watches;
        lock (_watchGate)
        {
            watches = [.. _watchTasks];
        }

        await Task.WhenAll(watches).ConfigureAwait(false);
    }

    public async Task<bool> RegisterAsync(
        RuntimeWorkspaceViewModel runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        using var lease = await EnterAsync(cancellationToken);
        WorkspaceInstance proposal;
        try
        {
            proposal = RuntimeWorkspaceGraphProjection.Capture(runtime);
        }
        catch (ArgumentException)
        {
            _setError(
                $"A workspace can contain at most {WorkspaceInstance.MaximumPanelCount} panels.");
            return false;
        }

        HostResult<WorkspaceGraphSnapshot> result;
        try
        {
            result = await RequireSessionClient().RegisterWorkspaceGraphAsync(
                new RegisterWorkspaceGraphRequest(_windowId, proposal),
                OperationContext.ForHuman(
                    _clientId,
                    idempotencyKey: IdempotencyKey.New()),
                lease.Token);
        }
        catch (Exception exception) when (
            IsAmbiguousReceiptFailure(exception)
            && !IsStopping)
        {
            var authoritative = await QueryForReconciliationAsync(runtime.Id);
            if (authoritative
                    is not HostResult<WorkspaceGraphSnapshot>.Success reconciledSuccess
                || !IsExpectedReceipt(
                    reconciledSuccess,
                    proposal,
                    currentRevision: 0,
                    currentSequence: 0))
            {
                throw;
            }

            result = reconciledSuccess;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            _setError("The runtime workspace graph could not be registered.");
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            _setError(
                "The session host rejected workspace registration "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        if (!IsExpectedReceipt(
                success,
                RuntimeWorkspaceGraphProjection.Capture(runtime),
                runtime.HostRevision,
                runtime.HostSequence))
        {
            _setError("The session host returned an invalid workspace registration receipt.");
            return false;
        }

        return TryApplyValidatedReceipt(runtime, success, "workspace registration");
    }

    public async Task<bool> ActivateTabAsync(
        RuntimeWorkspaceViewModel runtime,
        TabInstanceId tabId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Tabs.All(tab => tab.Id != tabId))
        {
            return false;
        }

        using var lease = await EnterAsync(cancellationToken);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        var request = new ActivateWorkspaceTabRequest(runtime.Id, tabId);
        var idempotencyKey = IdempotencyKey.New();
        for (var attempt = 0; attempt < MutationAttemptCount; attempt++)
        {
            var result = await RequireSessionClient().ActivateWorkspaceTabAsync(
                request,
                OperationContext.ForHuman(
                    _clientId,
                    runtime.HostRevision,
                    idempotencyKey),
                lease.Token);
            if (await TryRefreshRevisionConflictAsync(
                runtime,
                result,
                attempt,
                lease.Token))
            {
                continue;
            }

            return TryApplyResult(
                runtime,
                result,
                "tab activation",
                projection => projection.ActiveTabId == tabId);
        }

        return false;
    }

    public async Task<bool> ActivatePanelAsync(
        RuntimeWorkspaceViewModel runtime,
        PanelInstanceId panelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var tab = runtime.Tabs.SingleOrDefault(item =>
            item.Panels.Any(panel => panel.Id == panelId));
        if (tab is null)
        {
            return false;
        }

        using var lease = await EnterAsync(cancellationToken);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        var request = new ActivateWorkspacePanelRequest(
            runtime.Id,
            tab.Id,
            panelId);
        var idempotencyKey = IdempotencyKey.New();
        for (var attempt = 0; attempt < MutationAttemptCount; attempt++)
        {
            var result = await RequireSessionClient().ActivateWorkspacePanelAsync(
                request,
                OperationContext.ForHuman(
                    _clientId,
                    runtime.HostRevision,
                    idempotencyKey),
                lease.Token);
            if (await TryRefreshRevisionConflictAsync(
                runtime,
                result,
                attempt,
                lease.Token))
            {
                continue;
            }

            return TryApplyResult(
                runtime,
                result,
                "panel activation",
                projection =>
                    projection.ActiveTabId == tab.Id
                    && projection.Tabs.SingleOrDefault(
                            candidate => candidate.Id == tab.Id)
                        ?.ActivePanelId == panelId);
        }

        return false;
    }

    public async Task<bool> TransferTabUnderGateAsync(
        RuntimeWorkspaceViewModel source,
        RuntimeWorkspaceViewModel destination,
        TransferWorkspaceTabRequest request,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commit);
        return await TransferUnderGateAsync(
            source,
            destination,
            request.Source,
            request.Destination,
            request.TabId,
            null,
            () => RequireSessionClient().TransferWorkspaceTabAsync(
                request,
                OperationContext.ForHuman(
                    _clientId,
                    idempotencyKey: IdempotencyKey.New()),
                cancellationToken),
            commit,
            "tab transfer");
    }

    public async Task<bool> TransferPanelUnderGateAsync(
        RuntimeWorkspaceViewModel source,
        RuntimeWorkspaceViewModel destination,
        TransferWorkspacePanelRequest request,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commit);
        return await TransferUnderGateAsync(
            source,
            destination,
            request.Source,
            request.Destination,
            request.SourceTabId,
            request.PanelId,
            () => RequireSessionClient().TransferWorkspacePanelAsync(
                request,
                OperationContext.ForHuman(
                    _clientId,
                    idempotencyKey: IdempotencyKey.New()),
                cancellationToken),
            commit,
            "panel transfer");
    }

    private async Task<bool> TransferUnderGateAsync(
        RuntimeWorkspaceViewModel source,
        RuntimeWorkspaceViewModel destination,
        WorkspaceInstance sourceProposal,
        WorkspaceInstance destinationProposal,
        TabInstanceId tabId,
        PanelInstanceId? panelId,
        Func<ValueTask<HostResult<WorkspaceGraphTransferReceipt>>> submit,
        Action commit,
        string operation)
    {
        if (!ReferenceEquals(_currentWorkspace(), source)
            || source == destination
            || source.Id != sourceProposal.Id
            || destination.Id != destinationProposal.Id)
        {
            return false;
        }

        HostResult<WorkspaceGraphTransferReceipt> result;
        try
        {
            result = await submit();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            _setError($"The runtime workspace could not apply {operation}.");
            return false;
        }

        if (result is HostResult<WorkspaceGraphTransferReceipt>.Failure failure)
        {
            _setError(
                $"The session host rejected {operation} "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphTransferReceipt>.Success)result;
        var receipt = success.Value;
        if (receipt.TransferId == Guid.Empty
            || receipt.TabId != tabId
            || receipt.PanelId != panelId
            || receipt.Source.WindowId != _windowId
            || receipt.Destination.WindowId != _windowId
            || receipt.Source.Workspace.Id != source.Id
            || receipt.Destination.Workspace.Id != destination.Id
            || receipt.Source.Revision <= source.HostRevision
            || receipt.Destination.Revision <= destination.HostRevision
            || receipt.Source.LastSequence <= source.HostSequence
            || receipt.Destination.LastSequence <= destination.HostSequence
            || success.ResultingRevision != Math.Max(
                receipt.Source.Revision,
                receipt.Destination.Revision)
            || !RuntimeWorkspaceGraphProjection.IntentMatches(
                sourceProposal,
                receipt.Source.Workspace)
            || !RuntimeWorkspaceGraphProjection.IntentMatches(
                destinationProposal,
                receipt.Destination.Workspace)
            || receipt.Sessions.Select(item => item.SessionId).Distinct().Count()
                != receipt.Sessions.Count
            || !OwnershipReceiptsMatch(
                receipt.Sessions,
                sourceProposal,
                destinationProposal,
                tabId,
                panelId))
        {
            _setError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        commit();
        _workspaceCommitted(source);
        _workspaceCommitted(destination);
        try
        {
            source.ApplyHostProjection(
                receipt.Source.Workspace,
                receipt.Source.Revision,
                receipt.Source.LastSequence);
            destination.ApplyHostProjection(
                receipt.Destination.Workspace,
                receipt.Destination.Revision,
                receipt.Destination.LastSequence);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"The host-approved {operation} could not be applied to the runtime views.",
                exception);
        }

        _projectionApplied();
        return true;
    }

    private bool OwnershipReceiptsMatch(
        IReadOnlyList<SessionOwnershipTransferReceipt> sessions,
        WorkspaceInstance source,
        WorkspaceInstance destination,
        TabInstanceId sourceTabId,
        PanelInstanceId? panelId)
    {
        var destinationTabId = panelId is null
            ? sourceTabId
            : destination.Tabs.SingleOrDefault(tab =>
                tab.Panels.Any(panel => panel.Id == panelId))?.Id;
        if (destinationTabId is null)
        {
            return false;
        }

        return sessions.All(item =>
            item.Source.WindowId == _windowId
            && item.Source.WorkspaceId == source.Id
            && item.Source.TabId == sourceTabId
            && item.Destination.WindowId == _windowId
            && item.Destination.WorkspaceId == destination.Id
            && item.Destination.TabId == destinationTabId
            && item.Source.PanelId == item.Destination.PanelId
            && (panelId is null || item.Source.PanelId == panelId));
    }

    public ValueTask<HostResult<CloseScopeResult>> CloseAsync(
        CloseScopeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RequireSessionClient().CloseAsync(
            request,
            OperationContext.ForHuman(_clientId),
            cancellationToken);
    }

    public async ValueTask<WorkspaceGraphSnapshot?> ObserveWorkspaceAsync(
        WorkspaceInstanceId workspaceId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await RequireSessionClient().GetWorkspaceGraphAsync(
            workspaceId,
            OperationContext.ForHuman(_clientId),
            cancellationToken);
        return result is HostResult<WorkspaceGraphSnapshot>.Success success
            && success.ResultingRevision == success.Value.Revision
            && success.Value.WindowId == _windowId
            && success.Value.Workspace.Id == workspaceId
                ? success.Value
                : null;
    }

    public async ValueTask<SessionSnapshot?> EnsureBrowserSessionAsync(
        EnsureBrowserSessionRequest request,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await RequireSessionClient().EnsureBrowserSessionAsync(
            request,
            OperationContext.ForHuman(
                _clientId,
                idempotencyKey: IdempotencyKey.New()),
            cancellationToken);
        return result is HostResult<SessionSnapshot>.Success success
            && success.ResultingRevision == success.Value.Descriptor.Revision
            && success.Value.Descriptor.Id == request.SessionId
            && success.Value.Descriptor.Owner == owner
            && success.Value.Descriptor.Kind == PanelKind.Browser
            && success.Value.Descriptor.Lifecycle == SessionLifecycle.Active
                ? success.Value
                : null;
    }

    public async ValueTask<SessionSnapshot?> EnsureTerminalSessionAsync(
        EnsureTerminalSessionRequest request,
        SessionOwner owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await RequireSessionClient().EnsureTerminalSessionAsync(
            request,
            OperationContext.ForHuman(
                _clientId,
                idempotencyKey: IdempotencyKey.New()),
            cancellationToken);
        return result is HostResult<SessionSnapshot>.Success success
            && success.ResultingRevision == success.Value.Descriptor.Revision
            && success.Value.Descriptor.Id == request.SessionId
            && success.Value.Descriptor.Owner == owner
            && success.Value.Descriptor.Kind == PanelKind.Terminal
            && success.Value.Descriptor.Lifecycle is
                SessionLifecycle.Starting or SessionLifecycle.Active
                ? success.Value
                : null;
    }

    public async IAsyncEnumerable<SessionSnapshot> WatchSessionAsync(
        SessionId sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await foreach (var item in RequireSessionClient().WatchAsync(
            new WatchSessionRequest(sessionId, AfterSequence: 0),
            OperationContext.ForHuman(_clientId),
            cancellationToken))
        {
            var snapshot = item switch
            {
                SessionStreamItem.Event sessionEvent => new SessionSnapshot(
                    sessionEvent.Value.Descriptor,
                    sessionEvent.Value.Sequence,
                    [],
                    null),
                SessionStreamItem.ResynchronizationRequired resynchronization =>
                    resynchronization.Snapshot,
                _ => throw new ArgumentOutOfRangeException(nameof(item)),
            };
            if (snapshot.Descriptor.Id != sessionId)
            {
                yield break;
            }

            yield return snapshot;
        }
    }

    public async Task<bool> ReplaceAsync(
        RuntimeWorkspaceViewModel runtime,
        string operation,
        Func<RuntimeWorkspaceViewModel, WorkspaceInstance?> buildProposal,
        Action commit,
        CancellationToken cancellationToken,
        RuntimeGraphStaleProposalHandling staleProposalHandling =
            RuntimeGraphStaleProposalHandling.RefreshAndRetry)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(buildProposal);
        ArgumentNullException.ThrowIfNull(commit);
        using var lease = await EnterAsync(cancellationToken);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        WorkspaceInstance? proposal;
        try
        {
            proposal = buildProposal(runtime);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            _setError($"The runtime workspace changed before {operation} could start.");
            return false;
        }

        if (proposal is null)
        {
            _setError($"The runtime workspace changed before {operation} could start.");
            return false;
        }

        return await ReplaceUnderGateAsync(
            runtime,
            proposal,
            operation,
            commit,
            staleProposalHandling,
            lease.Token,
            buildProposal);
    }

    public async Task<bool> ReplaceUnderGateAsync(
        RuntimeWorkspaceViewModel runtime,
        WorkspaceInstance proposal,
        string operation,
        Action commit,
        RuntimeGraphStaleProposalHandling staleProposalHandling,
        CancellationToken cancellationToken,
        Func<RuntimeWorkspaceViewModel, WorkspaceInstance?>? rebuildProposal = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(commit);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        HostResult<WorkspaceGraphSnapshot>? result = null;
        var reconciledAfterAmbiguousReceipt = false;
        var idempotencyKey = IdempotencyKey.New();
        var attemptCount = staleProposalHandling
            == RuntimeGraphStaleProposalHandling.RefreshAndRetry
            ? MutationAttemptCount
            : 1;
        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            try
            {
                var request = new RegisterWorkspaceGraphRequest(_windowId, proposal);
                var attemptResult = await RequireSessionClient().RegisterWorkspaceGraphAsync(
                    request,
                    OperationContext.ForHuman(
                        _clientId,
                        runtime.HostRevision,
                        idempotencyKey),
                    cancellationToken);
                result = attemptResult;
                if (staleProposalHandling
                        == RuntimeGraphStaleProposalHandling.RefreshAndRetry
                    && await TryRefreshRevisionConflictAsync(
                        runtime,
                        attemptResult,
                        attempt,
                        cancellationToken))
                {
                    if (rebuildProposal?.Invoke(runtime) is not { } rebuiltProposal)
                    {
                        _setError(
                            $"The runtime workspace changed before {operation} could retry.");
                        return false;
                    }

                    proposal = rebuiltProposal;
                    idempotencyKey = IdempotencyKey.New();
                    continue;
                }
            }
            catch (Exception exception) when (
                IsAmbiguousReceiptFailure(exception)
                && !IsStopping)
            {
                var reconciled = await ReconcileMutationAsync(runtime, proposal);
                if (reconciled is null)
                {
                    throw;
                }

                result = reconciled;
                reconciledAfterAmbiguousReceipt = true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                _setError($"The runtime workspace could not apply {operation}.");
                return false;
            }

            break;
        }

        if (result is null)
        {
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            _setError(
                $"The session host rejected {operation} "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        var receiptIsExpected = reconciledAfterAmbiguousReceipt
            ? IsExpectedReconciledReceipt(
                success,
                proposal,
                runtime.HostRevision,
                runtime.HostSequence)
            : IsExpectedReceipt(
                success,
                proposal,
                runtime.HostRevision,
                runtime.HostSequence);
        if (!receiptIsExpected)
        {
            _setError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        commit();
        _workspaceCommitted(runtime);
        var applied = reconciledAfterAmbiguousReceipt
            ? TryApplyProjection(
                runtime,
                success.Value.WindowId,
                success.Value.Workspace,
                success.Value.Revision,
                success.Value.LastSequence,
                $"{operation} reconciliation")
            : TryApplyValidatedReceipt(runtime, success, operation);
        if (!applied)
        {
            throw new InvalidOperationException(
                $"The host-approved {operation} could not be applied to the runtime view.");
        }

        if (!reconciledAfterAmbiguousReceipt)
        {
            _projectionApplied();
        }

        return true;
    }

    public async Task<bool> UnregisterUnderGateAsync(
        RuntimeWorkspaceViewModel runtime,
        string operation,
        Action commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(commit);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        HostResult<Unit>? result = null;
        var reconciledRemoval = false;
        var request = new UnregisterWorkspaceGraphRequest(_windowId, runtime.Id);
        var idempotencyKey = IdempotencyKey.New();
        for (var attempt = 0; attempt < MutationAttemptCount; attempt++)
        {
            try
            {
                var attemptResult = await RequireSessionClient().UnregisterWorkspaceGraphAsync(
                    request,
                    OperationContext.ForHuman(
                        _clientId,
                        runtime.HostRevision,
                        idempotencyKey),
                    cancellationToken);
                result = attemptResult;
                if (await TryRefreshRevisionConflictAsync(
                    runtime,
                    attemptResult,
                    attempt,
                    cancellationToken))
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                IsAmbiguousReceiptFailure(exception)
                && !IsStopping)
            {
                var authoritative = await QueryForReconciliationAsync(runtime.Id);
                if (authoritative is not HostResult<WorkspaceGraphSnapshot>.Failure
                    {
                        Error.Code: HostErrorCode.NotFound,
                    })
                {
                    throw;
                }

                reconciledRemoval = true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                _setError($"The runtime workspace could not apply {operation}.");
                return false;
            }

            break;
        }

        if (reconciledRemoval)
        {
            commit();
            return true;
        }

        if (result is null)
        {
            return false;
        }

        if (result is HostResult<Unit>.Failure failure)
        {
            _setError(
                $"The session host rejected {operation} "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<Unit>.Success)result;
        if (success.ResultingRevision <= runtime.HostRevision)
        {
            _setError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        commit();
        return true;
    }

    public async ValueTask<bool> TryRefreshRevisionConflictAsync<T>(
        RuntimeWorkspaceViewModel runtime,
        HostResult<T> result,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt != 0
            || result is not HostResult<T>.Failure
            {
                Error.Code: HostErrorCode.RevisionConflict,
            } failure
            || failure.CurrentRevision <= runtime.HostRevision)
        {
            return false;
        }

        return await RefreshProjectionAsync(runtime, cancellationToken);
    }

    public bool IsExpectedReceipt(
        HostResult<WorkspaceGraphSnapshot>.Success success,
        WorkspaceInstance proposal,
        long currentRevision,
        long currentSequence) =>
        success.Value.WindowId == _windowId
        && success.ResultingRevision == success.Value.Revision
        && success.ResultingRevision > currentRevision
        && success.Value.LastSequence > currentSequence
        && RuntimeWorkspaceGraphProjection.IntentMatches(
            proposal,
            success.Value.Workspace);

    public bool IsExpectedReconciledReceipt(
        HostResult<WorkspaceGraphSnapshot>.Success success,
        WorkspaceInstance proposal,
        long currentRevision,
        long currentSequence) =>
        success.Value.WindowId == _windowId
        && success.Value.Workspace.Id == proposal.Id
        && success.ResultingRevision == success.Value.Revision
        && success.ResultingRevision > currentRevision
        && success.Value.LastSequence > currentSequence
        && RuntimeWorkspaceGraphProjection.TopologyMatches(
            proposal,
            success.Value.Workspace);

    /// <summary>
    /// Applies a receipt whose window, cursor advance, and requested topology
    /// were validated against its submitted proposal.
    /// </summary>
    public bool TryApplyValidatedReceipt(
        RuntimeWorkspaceViewModel runtime,
        HostResult<WorkspaceGraphSnapshot>.Success success,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var receipt = success.Value;
        var currentIsAtLeastAsNew =
            runtime.HostRevision >= receipt.Revision
            && runtime.HostSequence >= receipt.LastSequence;
        if (currentIsAtLeastAsNew)
        {
            if (RuntimeWorkspaceGraphProjection.TopologyMatches(
                    RuntimeWorkspaceGraphProjection.Capture(runtime),
                    receipt.Workspace))
            {
                return true;
            }

            _setError($"The runtime workspace changed while applying {operation}.");
            return false;
        }

        if (receipt.Revision <= runtime.HostRevision
            || receipt.LastSequence <= runtime.HostSequence)
        {
            _setError($"The session host returned an invalid {operation} cursor.");
            return false;
        }

        try
        {
            runtime.ApplyHostProjection(
                receipt.Workspace,
                receipt.Revision,
                receipt.LastSequence);
            return true;
        }
        catch (InvalidOperationException)
        {
            _setError($"The session host returned a different {operation} graph.");
            return false;
        }
    }

    public bool TryApplyResult(
        RuntimeWorkspaceViewModel expectedWorkspace,
        HostResult<WorkspaceGraphSnapshot> result,
        string operation,
        Func<WorkspaceInstance, bool> requestedFocusMatches)
    {
        ArgumentNullException.ThrowIfNull(expectedWorkspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(requestedFocusMatches);
        if (!ReferenceEquals(_currentWorkspace(), expectedWorkspace))
        {
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            _setError(
                $"The session host rejected {operation} "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        var currentProjection = RuntimeWorkspaceGraphProjection.Capture(expectedWorkspace);
        var sameCursor =
            success.Value.Revision == expectedWorkspace.HostRevision
            && success.Value.LastSequence == expectedWorkspace.HostSequence;
        var advancedCursor =
            success.Value.Revision > expectedWorkspace.HostRevision
            && success.Value.LastSequence > expectedWorkspace.HostSequence;
        if (success.Value.WindowId != _windowId
            || success.Value.Workspace.Id != expectedWorkspace.Id
            || success.ResultingRevision != success.Value.Revision
            || !requestedFocusMatches(success.Value.Workspace)
            || !(advancedCursor
                || sameCursor && requestedFocusMatches(currentProjection))
            || !RuntimeWorkspaceGraphProjection.TopologyMatches(
                currentProjection,
                success.Value.Workspace))
        {
            _setError($"The session host returned an invalid {operation} receipt.");
            return false;
        }

        try
        {
            expectedWorkspace.ApplyHostProjection(
                success.Value.Workspace,
                success.Value.Revision,
                success.Value.LastSequence);
        }
        catch (InvalidOperationException)
        {
            _setError("The session host returned a different runtime workspace graph.");
            return false;
        }

        _projectionApplied();
        return true;
    }

    public bool TryApplyProjection(
        RuntimeWorkspaceViewModel runtime,
        WindowInstanceId windowId,
        WorkspaceInstance projection,
        long revision,
        long sequence,
        string source)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        if (windowId != _windowId
            || projection.Id != runtime.Id
            || revision < runtime.HostRevision
            || sequence < runtime.HostSequence
            || !RuntimeWorkspaceGraphProjection.TopologyMatches(
                RuntimeWorkspaceGraphProjection.Capture(runtime),
                projection))
        {
            _setError($"The session host returned an invalid {source}.");
            return false;
        }

        try
        {
            runtime.ApplyHostProjection(projection, revision, sequence);
        }
        catch (InvalidOperationException)
        {
            _setError($"The session host returned a different {source} graph.");
            return false;
        }

        _projectionApplied();
        return true;
    }

    private async ValueTask<HostResult<WorkspaceGraphSnapshot>.Success?>
        ReconcileMutationAsync(
            RuntimeWorkspaceViewModel runtime,
            WorkspaceInstance proposal)
    {
        var result = await QueryForReconciliationAsync(runtime.Id);
        return result is HostResult<WorkspaceGraphSnapshot>.Success success
            && IsExpectedReconciledReceipt(
                success,
                proposal,
                runtime.HostRevision,
                runtime.HostSequence)
                ? success
                : null;
    }

    private async ValueTask<HostResult<WorkspaceGraphSnapshot>?>
        QueryForReconciliationAsync(WorkspaceInstanceId workspaceId)
    {
        using var timeoutCancellation = new CancellationTokenSource(
            ReceiptReconciliationTimeout,
            _timeProvider);
        using var reconciliationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                timeoutCancellation.Token);
        try
        {
            return await RequireSessionClient().GetWorkspaceGraphAsync(
                workspaceId,
                OperationContext.ForHuman(_clientId),
                reconciliationCancellation.Token);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or NotSupportedException
                or OperationCanceledException
                or TimeoutException)
        {
            return null;
        }
    }

    private async ValueTask<bool> RefreshProjectionAsync(
        RuntimeWorkspaceViewModel runtime,
        CancellationToken cancellationToken)
    {
        HostResult<WorkspaceGraphSnapshot> result;
        try
        {
            result = await RequireSessionClient().GetWorkspaceGraphAsync(
                runtime.Id,
                OperationContext.ForHuman(_clientId),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or NotSupportedException)
        {
            _setError("The runtime workspace could not be refreshed.");
            return false;
        }

        if (result is HostResult<WorkspaceGraphSnapshot>.Failure failure)
        {
            _setError(
                "The session host could not refresh the workspace "
                + $"({failure.Error.StableCode}): {failure.Error.Message}");
            return false;
        }

        var success = (HostResult<WorkspaceGraphSnapshot>.Success)result;
        if (success.ResultingRevision != success.Value.Revision)
        {
            _setError("The session host returned an invalid workspace refresh receipt.");
            return false;
        }

        return TryApplyProjection(
            runtime,
            success.Value.WindowId,
            success.Value.Workspace,
            success.Value.Revision,
            success.Value.LastSequence,
            "workspace refresh");
    }

    private ISessionHostClient RequireSessionClient() =>
        _sessionClient
        ?? throw new InvalidOperationException(
            "Session-host graph operations were not configured for this coordinator.");

    private static bool IsAmbiguousReceiptFailure(Exception exception) =>
        exception is OperationCanceledException or IOException or TimeoutException;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        StopWatching();
        _lifetime.Dispose();
    }

    private async Task WatchAsync(
        RuntimeWorkspaceViewModel runtime,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        var sessionClient = _sessionClient
            ?? throw new InvalidOperationException("Workspace graph watching is unavailable.");
        var dispatcher = _uiThreadDispatcher
            ?? throw new InvalidOperationException("Workspace graph watching is unavailable.");
        try
        {
            var cursor = afterSequence;
            while (!cancellationToken.IsCancellationRequested)
            {
                var restartAfterResynchronization = false;
                await foreach (var item in sessionClient.WatchWorkspaceGraphAsync(
                    new WatchWorkspaceGraphRequest(runtime.Id, cursor),
                    OperationContext.ForHuman(_clientId),
                    cancellationToken).ConfigureAwait(false))
                {
                    using var lease = await EnterAsync(cancellationToken);
                    if (!ReferenceEquals(_currentWorkspace(), runtime))
                    {
                        return;
                    }

                    var accepted = false;
                    await dispatcher.InvokeAsync(
                        () => accepted = ApplyStreamItem(runtime, item),
                        cancellationToken);
                    if (!accepted)
                    {
                        return;
                    }

                    cursor = runtime.HostSequence;
                    if (item is WorkspaceGraphStreamItem.ResynchronizationRequired)
                    {
                        restartAfterResynchronization = true;
                        break;
                    }
                }

                if (!restartAfterResynchronization)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (NotSupportedException)
        {
            // Compatibility clients can omit workspace watches. Mutation
            // receipts still keep those clients coherent.
        }
        catch (Exception)
        {
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_currentWorkspace(), runtime))
                    {
                        _setError("Live workspace updates are temporarily unavailable.");
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Presentation teardown won the race with the best-effort error.
            }
        }
    }

    private bool ApplyStreamItem(
        RuntimeWorkspaceViewModel runtime,
        WorkspaceGraphStreamItem item)
    {
        if (!ReferenceEquals(_currentWorkspace(), runtime))
        {
            return false;
        }

        switch (item)
        {
            case WorkspaceGraphStreamItem.Event { Value: var workspaceEvent }
                when workspaceEvent.Sequence <= runtime.HostSequence:
                return true;
            case WorkspaceGraphStreamItem.Event
            {
                Value.Kind: WorkspaceGraphEventKind.Removed,
                Value: var workspaceEvent,
            }:
                if (workspaceEvent.WindowId != _windowId
                    || workspaceEvent.WorkspaceId != runtime.Id
                    || workspaceEvent.Revision < runtime.HostRevision)
                {
                    _setError("The session host returned an invalid workspace removal event.");
                    return false;
                }

                (_workspaceRemoved
                    ?? throw new InvalidOperationException(
                        "Workspace removal handling is unavailable."))(runtime);
                return true;
            case WorkspaceGraphStreamItem.Event { Value: var workspaceEvent }:
                return TryApplyProjection(
                    runtime,
                    workspaceEvent.WindowId,
                    workspaceEvent.Workspace,
                    workspaceEvent.Revision,
                    workspaceEvent.Sequence,
                    "workspace event");
            case WorkspaceGraphStreamItem.ResynchronizationRequired
            {
                Snapshot: var snapshot,
                ResumeAfterSequence: var resumeAfterSequence,
            }:
                if (resumeAfterSequence != snapshot.LastSequence)
                {
                    _setError(
                        "The session host returned an invalid workspace resynchronization cursor.");
                    return false;
                }

                if (resumeAfterSequence <= runtime.HostSequence)
                {
                    return true;
                }

                return TryApplyProjection(
                    runtime,
                    snapshot.WindowId,
                    snapshot.Workspace,
                    snapshot.Revision,
                    resumeAfterSequence,
                    "workspace resynchronization");
            default:
                throw new ArgumentOutOfRangeException(nameof(item));
        }
    }
}

public sealed class RuntimeWorkspaceGraphLease : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private CancellationTokenSource? _linkedCancellation;

    internal RuntimeWorkspaceGraphLease(
        SemaphoreSlim gate,
        CancellationTokenSource linkedCancellation)
    {
        _gate = gate;
        _linkedCancellation = linkedCancellation;
    }

    public CancellationToken Token =>
        _linkedCancellation?.Token
        ?? throw new ObjectDisposedException(nameof(RuntimeWorkspaceGraphLease));

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref _linkedCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        _gate.Release();
        cancellation.Dispose();
    }
}
