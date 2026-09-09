using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns the recent-session presentation and its serialized persistence lifetime.
/// Runtime source resolution and reopening remain concerns of the window that hosts it.
/// </summary>
public sealed class RecentSessionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly RecentSessionHistory? _history;
    private readonly TimeProvider _timeProvider;
    private readonly Func<RecentSessionRecord, DateTimeOffset, RecentSessionHistoryItemViewModel>
        _projectItem;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationGate = new();
    private readonly HashSet<SessionId> _completedSessionIds = [];
    private Task _operations = Task.CompletedTask;
    private RecentSessionStoreError? _drainError;
    private StoredRecentSessionRetentionPolicy? _storedRetention;
    private string _searchQuery = string.Empty;
    private string _recentSessionStatus =
        "Sessions you open will appear here without storing terminal content or commands.";
    private string _exportStatus =
        "History exports contain definition metadata only; terminal commands and content are excluded.";
    private string _retentionStatus = "Loading local history privacy settings…";
    private bool _hasFailure;
    private bool _hasUnreadableHistory;
    private bool _isLoading;
    private bool _isMutating;
    private bool _isExporting;
    private bool _operationsSealed;
    private bool _isApplyingStoredRetention;
    private bool _hasPendingRetentionChange;
    private bool _disposed;
    private volatile bool _presentationStopped;
    private RecentSessionHistoryItemViewModel? _selectedSession;
    private HistoryExportScope _selectedExportScope;
    private HistoryRetentionOption? _selectedRetentionOption;

    public RecentSessionHistoryViewModel(
        RecentSessionHistory? history = null,
        TimeProvider? timeProvider = null,
        Func<RecentSessionRecord, DateTimeOffset, RecentSessionHistoryItemViewModel>?
            projectItem = null)
    {
        _history = history;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _projectItem = projectItem ?? (static (record, observedAt) =>
            new RecentSessionHistoryItemViewModel(record, CanOpen: false, observedAt));
    }

    /// <summary>
    /// Raised after the durable projection changes so the root can recompute its
    /// launcher and runtime-only joins without taking ownership of history state.
    /// </summary>
    public event EventHandler? SnapshotChanged;

    public ObservableCollection<RecentSessionHistoryItemViewModel> RecentSessions { get; } = [];

    public ObservableCollection<RecentSessionHistoryItemViewModel> Sessions { get; } = [];

    public ObservableCollection<RecentSessionHistoryItemViewModel> FilteredSessions { get; } = [];

    public IReadOnlyList<HistoryExportScope> ExportScopes { get; } =
        Enum.GetValues<HistoryExportScope>();

    public ObservableCollection<HistoryRetentionOption> RetentionOptions { get; } =
    [
        new(
            "Off",
            "Do not retain session metadata. Existing history is removed.",
            new RecentSessionRetentionPolicy(0, TimeSpan.FromDays(30))),
        new(
            "Private · 20 / 7 days",
            "Keep at most 20 records for up to 7 days.",
            new RecentSessionRetentionPolicy(20, TimeSpan.FromDays(7))),
        new(
            "Standard · 100 / 30 days",
            "Keep at most 100 records for up to 30 days.",
            RecentSessionRetentionPolicy.Default),
        new(
            "Extended · 500 / 90 days",
            "Keep at most 500 records for up to 90 days.",
            new RecentSessionRetentionPolicy(500, TimeSpan.FromDays(90))),
        new(
            "Maximum · 1,000 / 365 days",
            "Keep at most 1,000 records for up to 365 days.",
            new RecentSessionRetentionPolicy(1_000, TimeSpan.FromDays(365))),
    ];

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool HasNoRecentSessions => !HasRecentSessions && !IsLoading;

    public bool HasSessions => Sessions.Count > 0;

    public bool HasNoSessions => !HasSessions;

    public bool HasFilteredSessions => FilteredSessions.Count > 0;

    public bool HasNoFilteredSessions => !HasFilteredSessions && !IsLoading;

    public bool HasFailure
    {
        get => _hasFailure;
        private set
        {
            if (SetProperty(ref _hasFailure, value))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public bool HasUnreadableHistory
    {
        get => _hasUnreadableHistory;
        private set
        {
            if (SetProperty(ref _hasUnreadableHistory, value))
            {
                OnPropertyChanged(nameof(CanReset));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public bool IsMutating
    {
        get => _isMutating;
        private set
        {
            if (SetProperty(ref _isMutating, value))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                NotifyActionStateChanged();
            }
        }
    }

    public bool CanRetry => HasFailure && !IsLoading && !IsMutating;

    public bool CanClear => HasSessions && !IsLoading && !IsMutating;

    public bool CanReset =>
        _history is not null && HasUnreadableHistory && !IsLoading && !IsMutating;

    public bool CanExportAll =>
        HasSessions && !IsLoading && !IsMutating && !IsExporting;

    public bool CanExportFiltered =>
        HasFilteredSessions && !IsLoading && !IsMutating && !IsExporting;

    public string ResultCount => string.IsNullOrWhiteSpace(SearchQuery)
        ? $"{FilteredSessions.Count} retained"
        : $"{FilteredSessions.Count} matched";

    public string SearchEmptyState => HasSessions
        ? $"No retained sessions match ‘{SearchQuery.Trim()}’."
        : RecentSessionStatus;

    public string RecentSessionStatus
    {
        get => _recentSessionStatus;
        private set => SetProperty(ref _recentSessionStatus, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        private set => SetProperty(ref _exportStatus, value);
    }

    public string RetentionStatus
    {
        get => _retentionStatus;
        private set => SetProperty(ref _retentionStatus, value);
    }

    public bool CanManageRetention =>
        _history?.SupportsRetentionSettings == true
        && _storedRetention is not null
        && !IsLoading
        && !IsMutating;

    public HistoryRetentionOption? SelectedRetentionOption
    {
        get => _selectedRetentionOption;
        set
        {
            if (!SetProperty(ref _selectedRetentionOption, value))
            {
                return;
            }

            if (!_isApplyingStoredRetention)
            {
                HasPendingRetentionChange = _storedRetention is { } stored
                    && value?.Policy != stored.Policy;
            }

            OnPropertyChanged(nameof(RequiresRetentionConfirmation));
        }
    }

    public bool HasPendingRetentionChange
    {
        get => _hasPendingRetentionChange;
        private set
        {
            if (SetProperty(ref _hasPendingRetentionChange, value))
            {
                OnPropertyChanged(nameof(CanApplyRetention));
            }
        }
    }

    public bool CanApplyRetention => CanManageRetention && HasPendingRetentionChange;

    public bool RequiresRetentionConfirmation =>
        _storedRetention is { } stored
        && SelectedRetentionOption is { } selected
        && (selected.Policy.MaximumEntries < stored.Policy.MaximumEntries
            || selected.Policy.MaximumAge < stored.Policy.MaximumAge);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                RefreshSearchResults(preserveSelection: false);
            }
        }
    }

    public RecentSessionHistoryItemViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                OnPropertyChanged(nameof(HasSelectedSession));
                OnPropertyChanged(nameof(HasNoSelectedSession));
            }
        }
    }

    public bool HasSelectedSession => SelectedSession is not null;

    public bool HasNoSelectedSession => !HasSelectedSession;

    public HistoryExportScope SelectedExportScope
    {
        get => _selectedExportScope;
        set => SetProperty(ref _selectedExportScope, value);
    }

    public void StartLoading()
    {
        if (_history is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        _ = QueueOperation(async token =>
        {
            try
            {
                await RefreshCoreAsync(token);
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    public Task RecordStartedAsync(
        SessionId sessionId,
        DefinitionKey sourceDefinition,
        PanelKind kind,
        string durableDefinitionTitle)
    {
        if (_history is null)
        {
            return Task.CompletedTask;
        }

        // Capture before entering the queue: persistence latency must not move
        // the session's actual start time.
        var captured = _history.CaptureStarted(
            sessionId,
            sourceDefinition,
            kind,
            durableDefinitionTitle);
        return QueueOperation(async token =>
        {
            var result = await _history.RecordStartedAsync(captured, token);
            if (!result.IsSuccess)
            {
                ApplyPersistenceFailure(result.Error!);
                return;
            }

            await RefreshCoreAsync(token);
        });
    }

    public Task RecordCompletionsAsync(
        IReadOnlyList<(SessionId SessionId, RecentSessionOutcome Outcome)> completions)
    {
        ArgumentNullException.ThrowIfNull(completions);
        if (_history is null || completions.Count == 0)
        {
            return Task.CompletedTask;
        }

        List<RecentSessionCompletion> captured = [];
        lock (_operationGate)
        {
            foreach (var completion in completions)
            {
                if (_completedSessionIds.Add(completion.SessionId))
                {
                    // Capture before entering the queue for the same reason as starts.
                    captured.Add(_history.CaptureCompletion(
                        completion.SessionId,
                        completion.Outcome));
                }
            }
        }

        if (captured.Count == 0)
        {
            return Task.CompletedTask;
        }

        return QueueOperation(async token =>
        {
            foreach (var completion in captured)
            {
                var result = await _history.RecordCompletedAsync(completion, token);
                if (result.IsSuccess)
                {
                    continue;
                }

                if (result.Error!.Code == RecentSessionStoreErrorCode.Conflict)
                {
                    SecretSafeDiagnosticProjection.WriteStandardError(
                        "history.late-completion.ignored",
                        SecretSafeDiagnosticKind.Unexpected);
                    continue;
                }

                ApplyPersistenceFailure(result.Error);
                return;
            }

            await RefreshCoreAsync(token);
        });
    }

    public RecentSessionClearCutoff CaptureClearCutoff() =>
        _history?.CaptureClearCutoff()
        ?? new RecentSessionClearCutoff(_timeProvider.GetUtcNow());

    public async Task<bool> ClearAsync(
        RecentSessionClearCutoff cutoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cutoff);
        if (_history is null || IsLoading || IsMutating)
        {
            return false;
        }

        IsMutating = true;
        var cleared = false;
        try
        {
            var operation = QueueOperation(async token =>
            {
                var result = await _history.ClearThroughAsync(cutoff, token);
                if (!result.IsSuccess)
                {
                    ApplyPersistenceFailure(result.Error!);
                    return;
                }

                cleared = true;
                await RefreshCoreAsync(token);
            });
            await operation.WaitAsync(cancellationToken);
            return cleared;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsMutating = false;
        }
    }

    public async Task<bool> ResetUnreadableAsync(CancellationToken cancellationToken)
    {
        if (_history is null || !CanReset)
        {
            return false;
        }

        IsMutating = true;
        var reset = false;
        try
        {
            var operation = QueueOperation(async token =>
            {
                var result = await _history.ClearAllAsync(token);
                if (!result.IsSuccess)
                {
                    ApplyPersistenceFailure(result.Error!);
                    return;
                }

                reset = true;
                await RefreshCoreAsync(token);
            });
            await operation.WaitAsync(cancellationToken);
            return reset && !HasFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsMutating = false;
        }
    }

    public async Task<bool> RetryAsync(CancellationToken cancellationToken)
    {
        if (!CanRetry)
        {
            return false;
        }

        IsLoading = true;
        try
        {
            await QueueOperation(token => RefreshCoreAsync(token))
                .WaitAsync(cancellationToken);
            return !HasFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
        SaveRetentionAsync(CancellationToken cancellationToken)
    {
        if (_history is null
            || _storedRetention is not { } stored
            || SelectedRetentionOption is not { } selected)
        {
            return Failure(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "Recent-session retention settings are unavailable.");
        }

        if (selected.Policy == stored.Policy)
        {
            HasPendingRetentionChange = false;
            return RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Success(
                new RecentSessionRetentionUpdateResult(stored, 0));
        }

        if (IsLoading || IsMutating)
        {
            return Failure(
                RecentSessionStoreErrorCode.Conflict,
                "Another session-history change is already running.");
        }

        IsMutating = true;
        try
        {
            RecentSessionStoreResult<RecentSessionRetentionUpdateResult>? saved = null;
            var operation = QueueOperation(async token =>
            {
                saved = await _history.UpdateRetentionAsync(
                    selected.Policy,
                    stored.Revision,
                    token);
                if (!saved.IsSuccess)
                {
                    RetentionStatus =
                        $"History privacy settings could not be saved ({saved.Error!.Code}).";
                    if (saved.Error.Code == RecentSessionStoreErrorCode.Conflict)
                    {
                        await RefreshCoreAsync(token, replaceRetentionSelection: true);
                        if (_storedRetention is { Revision: var revision }
                            && revision != stored.Revision)
                        {
                            RetentionStatus =
                                "History privacy settings changed elsewhere; the current policy was reloaded.";
                        }
                    }
                    else
                    {
                        ApplyPersistenceFailure(saved.Error);
                    }

                    return;
                }

                HasPendingRetentionChange = false;
                ApplyStoredRetention(saved.Value!.StoredPolicy, replaceSelection: true);
                var completionStatus = saved.Value.PrunedSessionCount == 0
                    ? "History privacy settings saved."
                    : $"History privacy settings saved; {CountLabel(saved.Value.PrunedSessionCount, "retained record")} removed.";
                await RefreshCoreAsync(token);
                if (_storedRetention?.Revision == saved.Value.StoredPolicy.Revision)
                {
                    RetentionStatus = completionStatus;
                }
            });

            await operation.WaitAsync(cancellationToken);
            return saved ?? Failure(
                RecentSessionStoreErrorCode.Cancelled,
                "Saving recent-session retention was cancelled.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                RecentSessionStoreErrorCode.Cancelled,
                "Saving recent-session retention was cancelled.");
        }
        finally
        {
            IsMutating = false;
        }
    }

    public IReadOnlyList<RecentSessionRecord> CaptureExportSnapshot() =>
        [.. (SelectedExportScope == HistoryExportScope.CurrentResults
            ? FilteredSessions
            : Sessions).Select(item => item.Record)];

    public void SetExportStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ExportStatus = status.Trim();
    }

    public bool TryBeginExport(HistoryExportScope scope)
    {
        var canBegin = scope switch
        {
            HistoryExportScope.AllRetained => CanExportAll,
            HistoryExportScope.CurrentResults => CanExportFiltered,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
        if (!canBegin)
        {
            return false;
        }

        SelectedExportScope = scope;
        IsExporting = true;
        ExportStatus = "Preparing the metadata-only history export…";
        return true;
    }

    public void EndExport(string status)
    {
        SetExportStatus(status);
        IsExporting = false;
    }

    public void RefreshAvailability(
        Func<RecentSessionRecord, DateTimeOffset, RecentSessionHistoryItemViewModel> projectItem)
    {
        ArgumentNullException.ThrowIfNull(projectItem);
        ReplaceIfChanged(
            Sessions,
            [.. Sessions.Select(item => projectItem(item.Record, item.ObservedAt))]);
        ReplaceIfChanged(RecentSessions, [.. Sessions.Take(8)]);
        RefreshSearchResults();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops UI publication without cancelling queued durable writes. Shutdown
    /// still drains completions, including operations queued before this call.
    /// </summary>
    public void StopPresentationUpdates() => _presentationStopped = true;

    public void SealOperations()
    {
        lock (_operationGate)
        {
            _operationsSealed = true;
        }
    }

    public async Task<ApplicationRunResult<Unit>> DrainAsync(
        CancellationToken cancellationToken)
    {
        Task pending;
        lock (_operationGate)
        {
            pending = _operations;
        }

        try
        {
            await pending.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                ApplicationRunErrorCode.Cancelled,
                "Waiting for recent-session persistence was cancelled."));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                ApplicationRunErrorCode.StorageFailure,
                "Recent-session persistence could not be drained."));
        }

        RecentSessionStoreError? error;
        lock (_operationGate)
        {
            error = _drainError;
        }

        return error is null
            ? ApplicationRunResult<Unit>.Success(Unit.Value)
            : ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                error.Code == RecentSessionStoreErrorCode.StorageUnavailable
                    ? ApplicationRunErrorCode.StorageUnavailable
                    : ApplicationRunErrorCode.StorageFailure,
                $"Recent-session metadata could not be persisted safely: {error.Message}"));
    }

    private Task QueueOperation(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_operationGate)
        {
            if (_operationsSealed)
            {
                return _operations;
            }

            _operations = RunOperationAsync(_operations, operation, _lifetime.Token);
            return _operations;
        }
    }

    private async Task RunOperationAsync(
        Task previous,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = new RecentSessionStoreError(
                RecentSessionStoreErrorCode.StorageFailure,
                $"Recent-session metadata is temporarily unavailable ({exception.GetType().Name}).");
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "history.queued-operation.failed",
                exception);
            ApplyPersistenceFailure(error);
        }
    }

    private async Task RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool replaceRetentionSelection = false)
    {
        if (_history is null || _presentationStopped)
        {
            return;
        }

        if (_history.SupportsRetentionSettings)
        {
            var retention = await _history.GetRetentionAsync(cancellationToken);
            if (_presentationStopped)
            {
                return;
            }

            if (!retention.IsSuccess)
            {
                ClearRetentionSelection();
                ApplyFailure(retention.Error!);
                RetentionStatus =
                    $"History privacy settings are unavailable ({retention.Error!.Code}).";
                OnPropertyChanged(nameof(CanManageRetention));
                return;
            }

            ApplyStoredRetention(retention.Value!, replaceRetentionSelection);
        }

        var result = await _history.ListRecentAsync(
            RecentSessionQuery.MaximumLimit,
            cancellationToken);
        if (_presentationStopped)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            ApplyFailure(result.Error!);
            return;
        }

        var observedAt = _timeProvider.GetUtcNow();
        var items = result.Value!
            .Select(record => _projectItem(record, observedAt))
            .ToArray();
        HasFailure = false;
        HasUnreadableHistory = false;
        ReplaceIfChanged(Sessions, items);
        ReplaceIfChanged(RecentSessions, [.. items.Take(8)]);
        RecentSessionStatus = Sessions.Count > 0
            ? "Recent sessions store definition metadata only; commands and terminal content are excluded."
            : _storedRetention is { Policy.IsEnabled: false }
                ? "Session history is disabled in the local privacy settings."
                : "Sessions you open will appear here without storing terminal content or commands.";
        NotifyCollectionStateChanged();
        RefreshSearchResults();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRetentionSelection()
    {
        _storedRetention = null;
        _isApplyingStoredRetention = true;
        try
        {
            SelectedRetentionOption = null;
            HasPendingRetentionChange = false;
        }
        finally
        {
            _isApplyingStoredRetention = false;
        }
    }

    private void ApplyStoredRetention(
        StoredRecentSessionRetentionPolicy stored,
        bool replaceSelection = false)
    {
        _storedRetention = stored;
        var option = RetentionOptions.FirstOrDefault(item => item.Policy == stored.Policy);
        if (option is null)
        {
            option = new HistoryRetentionOption(
                $"Custom · {stored.Policy.MaximumEntries:N0} / {stored.Policy.MaximumAge.TotalDays:0} days",
                $"Keep at most {stored.Policy.MaximumEntries:N0} records for up to {stored.Policy.MaximumAge.TotalDays:0} days.",
                stored.Policy);
            RetentionOptions.Add(option);
        }

        if (replaceSelection
            || !HasPendingRetentionChange
            || SelectedRetentionOption is null)
        {
            _isApplyingStoredRetention = true;
            try
            {
                SelectedRetentionOption = option;
                HasPendingRetentionChange = false;
            }
            finally
            {
                _isApplyingStoredRetention = false;
            }
        }
        else if (SelectedRetentionOption.Policy == stored.Policy)
        {
            HasPendingRetentionChange = false;
        }

        RetentionStatus = stored.Policy.IsEnabled
            ? $"Local metadata retention: up to {stored.Policy.MaximumEntries:N0} records for {stored.Policy.MaximumAge.TotalDays:0} days."
            : "Session history is disabled; newly opened sessions will not be retained.";
        OnPropertyChanged(nameof(CanManageRetention));
        OnPropertyChanged(nameof(RequiresRetentionConfirmation));
    }

    private void RefreshSearchResults(bool preserveSelection = true)
    {
        var selectedSessionId = preserveSelection ? SelectedSession?.SessionId : null;
        var results = RecentSessionHistoryProjection.Search(SearchQuery, Sessions);
        ReplaceIfChanged(FilteredSessions, results);
        SelectedSession = RecentSessionHistoryProjection.ResolveSelection(
            results,
            selectedSessionId);
        OnPropertyChanged(nameof(HasFilteredSessions));
        OnPropertyChanged(nameof(HasNoFilteredSessions));
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(SearchEmptyState));
        OnPropertyChanged(nameof(CanExportFiltered));
    }

    private void ApplyFailure(RecentSessionStoreError error)
    {
        if (_presentationStopped)
        {
            return;
        }

        HasFailure = true;
        HasUnreadableHistory = error.Code == RecentSessionStoreErrorCode.InvalidHistoryData;
        Sessions.Clear();
        RecentSessions.Clear();
        RecentSessionStatus = $"Recent-session metadata is unavailable ({error.Code}).";
        NotifyCollectionStateChanged();
        RefreshSearchResults();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPersistenceFailure(RecentSessionStoreError error)
    {
        lock (_operationGate)
        {
            _drainError ??= error;
        }

        ApplyFailure(error);
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(HasNoRecentSessions));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));
        NotifyActionStateChanged();
    }

    private void NotifyActionStateChanged()
    {
        OnPropertyChanged(nameof(HasNoRecentSessions));
        OnPropertyChanged(nameof(HasNoFilteredSessions));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(CanExportAll));
        OnPropertyChanged(nameof(CanExportFiltered));
        OnPropertyChanged(nameof(CanManageRetention));
        OnPropertyChanged(nameof(CanApplyRetention));
    }

    private static RecentSessionStoreResult<RecentSessionRetentionUpdateResult> Failure(
        RecentSessionStoreErrorCode code,
        string message) =>
        RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Failure(
            new RecentSessionStoreError(code, message));

    private static string CountLabel(int count, string singular) =>
        count == 1 ? $"1 {singular}" : $"{count:N0} {singular}s";

    private static void ReplaceIfChanged(
        ObservableCollection<RecentSessionHistoryItemViewModel> target,
        IReadOnlyList<RecentSessionHistoryItemViewModel> source)
    {
        if (target.Count == source.Count
            && target.Zip(source).All(pair => pair.First.PresentsSameAs(pair.Second)))
        {
            return;
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPresentationUpdates();
        SealOperations();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
