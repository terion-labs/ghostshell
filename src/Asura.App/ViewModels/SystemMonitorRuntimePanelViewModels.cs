using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public enum SystemMonitorPanelState
{
    Waiting,
    Live,
    Stale,
    Failed,
    Disposed,
}

internal static class SystemMonitorPolling
{
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRemoteBackoff = TimeSpan.FromSeconds(30);

    public static TimeSpan Delay(
        ConnectionKind connectionKind,
        int consecutiveFailures)
    {
        if (connectionKind == ConnectionKind.Local || consecutiveFailures <= 0)
        {
            return NormalInterval;
        }

        var exponent = Math.Min(consecutiveFailures, 4);
        var delay = TimeSpan.FromTicks(NormalInterval.Ticks * (1L << exponent));
        return delay <= MaximumRemoteBackoff ? delay : MaximumRemoteBackoff;
    }
}

public sealed class StatisticsRuntimePanelViewModel : RuntimePanelViewModel
{
    internal const int HistoryCapacity = 60;
    private readonly ISessionHostClient _sessionClient;
    private readonly ConnectionProfile _connection;
    private readonly ClientId _clientId;
    private readonly SessionOwner _owner;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly AsyncActionCommand _refreshCommand;
    private readonly List<double?> _cpuSamples = [];
    private readonly List<double?> _memorySamples = [];
    private readonly List<double?> _networkReceivedSamples = [];
    private readonly List<double?> _networkSentSamples = [];
    private IReadOnlyList<double?> _cpuHistory = [];
    private IReadOnlyList<double?> _memoryHistory = [];
    private IReadOnlyList<double?> _networkReceivedHistory = [];
    private IReadOnlyList<double?> _networkSentHistory = [];
    private SystemStatisticsSnapshot? _snapshot;
    private SystemMonitorPanelState _state = SystemMonitorPanelState.Waiting;
    private string _statusText = "Waiting for first sample…";
    private string? _issueTitle;
    private string? _issueMessage;
    private bool _isRefreshing;
    private bool _hasHostedSession;
    private bool _started;
    private bool _disposed;
    private long _hostRevision;
    private Task? _polling;
    private readonly string? _connectionDisplayName;

    public StatisticsRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        ISessionHostClient sessionClient,
        ClientId clientId,
        SessionOwner owner,
        IUiThreadDispatcher dispatcher,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? connectionDisplayName = null)
        : this(
            id,
            title,
            sessionClient,
            clientId,
            owner,
            BuiltInConnections.Local,
            dispatcher,
            delay,
            connectionDisplayName)
    {
    }

    public StatisticsRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        ISessionHostClient sessionClient,
        ClientId clientId,
        SessionOwner owner,
        ConnectionProfile connection,
        IUiThreadDispatcher dispatcher,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? connectionDisplayName = null)
        : base(id, PanelKind.Statistics, title, "Statistics")
    {
        _sessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _clientId = clientId;
        _owner = owner;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _delay = delay ?? Task.Delay;
        _connectionDisplayName = connectionDisplayName;
        SessionId = SessionId.New();
        _refreshCommand = new AsyncActionCommand(
            RetryAsync,
            () => !_disposed
                && !IsRefreshing
                && (HasHostedSession || State == SystemMonitorPanelState.Failed));
    }

    public SessionId SessionId { get; }

    public ConnectionId ConnectionId => _connection.Id;

    public string ConnectionDisplayName =>
        _connectionDisplayName
        ?? (_connection.Endpoint is ConnectionEndpoint.Local ? "Local" : _connection.Name);

    public Task Initialization { get; private set; } = Task.CompletedTask;

    public ICommand RefreshCommand => _refreshCommand;

    public SystemStatisticsSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                if (value is not null)
                {
                    AppendHistory(value);
                }

                OnPropertyChanged(nameof(HasSnapshot));
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(ShowContent));
                OnPropertyChanged(nameof(ShowTerminalError));
                _refreshCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CpuText));
                OnPropertyChanged(nameof(MemoryText));
                OnPropertyChanged(nameof(NetworkReceivedText));
                OnPropertyChanged(nameof(NetworkSentText));
                OnPropertyChanged(nameof(ProcessCountText));
                OnPropertyChanged(nameof(ProcessDetailText));
                OnPropertyChanged(nameof(UptimeText));
                OnPropertyChanged(nameof(ProcessorCountText));
                OnPropertyChanged(nameof(CapturedAtText));
            }
        }
    }

    public SystemMonitorPanelState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(ShowContent));
                OnPropertyChanged(nameof(ShowTerminalError));
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? IssueTitle
    {
        get => _issueTitle;
        private set
        {
            if (SetProperty(ref _issueTitle, value))
            {
                OnPropertyChanged(nameof(HasIssue));
            }
        }
    }

    public string? IssueMessage
    {
        get => _issueMessage;
        private set => SetProperty(ref _issueMessage, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasHostedSession
    {
        get => _hasHostedSession;
        private set
        {
            if (SetProperty(ref _hasHostedSession, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSnapshot => Snapshot is not null;

    public bool HasIssue => IssueTitle is not null;

    public bool ShowLoading => !HasSnapshot && State == SystemMonitorPanelState.Waiting;

    public bool ShowContent => HasSnapshot;

    public bool ShowTerminalError => !HasSnapshot && State == SystemMonitorPanelState.Failed;

    public string StatusColor => State switch
    {
        SystemMonitorPanelState.Live => "#72B57B",
        SystemMonitorPanelState.Stale => "#D79B57",
        SystemMonitorPanelState.Failed => "#D96B6B",
        SystemMonitorPanelState.Disposed => "#77777F",
        _ => "#9A9AA2",
    };

    public string CpuText => Snapshot?.ObservedCpuPercent is { } value
        ? $"{value.ToString("0.0", CultureInfo.InvariantCulture)}%"
        : "Unavailable";

    public string MemoryText => MonitorPanelPresentation.FormatBytes(
        Snapshot?.ObservedWorkingSetBytes);

    public string NetworkReceivedText => MonitorPanelPresentation.FormatBytesPerSecond(
        Snapshot?.NetworkReceivedBytesPerSecond);

    public string NetworkSentText => MonitorPanelPresentation.FormatBytesPerSecond(
        Snapshot?.NetworkSentBytesPerSecond);

    public string ProcessCountText => Snapshot is { } snapshot
        ? snapshot.EnumeratedProcessCount.ToString("N0", CultureInfo.CurrentCulture)
        : "Unavailable";

    public string ProcessDetailText => Snapshot is { } snapshot
        ? snapshot.ObservedProcessCount == snapshot.EnumeratedProcessCount
            ? "Resource details available for all processes"
            : $"Resource details available for {snapshot.ObservedProcessCount:N0} of {snapshot.EnumeratedProcessCount:N0}"
        : "Process details unavailable";

    public string UptimeText => Snapshot is { } snapshot
        ? MonitorPanelPresentation.FormatDuration(snapshot.HostUptime)
        : "Unavailable";

    public string ProcessorCountText => Snapshot is { } snapshot
        ? snapshot.LogicalProcessorCount.ToString(CultureInfo.InvariantCulture)
        : "Unavailable";

    public string CapturedAtText => Snapshot is { } snapshot
        ? $"Captured {snapshot.CapturedAtUtc.ToLocalTime():T}"
        : "No sample captured";

    public IReadOnlyList<double?> CpuHistory => _cpuHistory;

    public IReadOnlyList<double?> MemoryHistory => _memoryHistory;

    public IReadOnlyList<double?> NetworkReceivedHistory => _networkReceivedHistory;

    public IReadOnlyList<double?> NetworkSentHistory => _networkSentHistory;

    public Task Start()
    {
        if (_disposed || _started)
        {
            return Initialization;
        }

        _started = true;
        Initialization = InitializeAsync();
        return Initialization;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(cancellationToken);

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        State = SystemMonitorPanelState.Disposed;
        StatusText = "Monitoring stopped";
        _refreshCommand.RaiseCanExecuteChanged();
        base.Dispose();
    }

    private async Task InitializeAsync()
    {
        HostResult<SessionSnapshot> result;
        try
        {
            result = await _sessionClient.EnsureStatisticsSessionAsync(
                new EnsureStatisticsSessionRequest(SessionId, _owner, Title, _connection),
                OperationContext.ForHuman(
                    _clientId,
                    idempotencyKey: IdempotencyKey.New()),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            await ApplyHostFailureAsync(
                "Statistics unavailable",
                $"The statistics session for {ConnectionDisplayName} could not be started.");
            return;
        }

        if (result is HostResult<SessionSnapshot>.Failure failure)
        {
            await ApplyHostFailureAsync(
                "Statistics unavailable",
                failure.Error.Message);
            return;
        }

        var success = (HostResult<SessionSnapshot>.Success)result;
        _hostRevision = success.ResultingRevision;
        await _dispatcher.InvokeAsync(
            () => HasHostedSession = true,
            _lifetime.Token);
        await RefreshCoreAsync(_lifetime.Token);
        if (!_lifetime.IsCancellationRequested)
        {
            _polling = PollAsync(_lifetime.Token);
        }
    }

    private async Task RetryAsync()
    {
        if (HasHostedSession)
        {
            await RefreshCoreAsync(CancellationToken.None);
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                IssueTitle = null;
                IssueMessage = null;
                State = SystemMonitorPanelState.Waiting;
                StatusText = $"Starting statistics · {ConnectionDisplayName}…";
            },
            CancellationToken.None);
        Initialization = InitializeAsync();
        await Initialization;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = State == SystemMonitorPanelState.Live ? 0 : 1;
        try
        {
            while (true)
            {
                await _delay(
                    SystemMonitorPolling.Delay(
                        _connection.ConnectionKind,
                        consecutiveFailures),
                    cancellationToken);
                await RefreshCoreAsync(cancellationToken);
                consecutiveFailures = State == SystemMonitorPanelState.Live
                    ? 0
                    : Math.Min(consecutiveFailures + 1, 5);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!HasHostedSession || _disposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _refreshGate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() => IsRefreshing = true, linked.Token);
            HostResult<MonitorPanelResult<SystemStatisticsSnapshot>> result;
            try
            {
                result = await _sessionClient.ReadStatisticsAsync(
                    SessionId,
                    OperationContext.ForHuman(_clientId, _hostRevision),
                    linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                await ApplyCaptureFailureAsync(
                    "Statistics refresh failed",
                    $"The statistics snapshot for {ConnectionDisplayName} could not be captured.",
                    linked.Token);
                return;
            }

            if (result is HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>.Failure failure)
            {
                await ApplyCaptureFailureAsync(
                    "Statistics refresh failed",
                    failure.Error.Message,
                    linked.Token);
                return;
            }

            var monitorResult =
                ((HostResult<MonitorPanelResult<SystemStatisticsSnapshot>>.Success)result).Value;
            if (!monitorResult.IsSuccess)
            {
                if (monitorResult.Error!.Code != MonitorPanelErrorCode.Cancelled)
                {
                    await ApplyCaptureFailureAsync(
                        "Statistics refresh failed",
                        monitorResult.Error.Message,
                        linked.Token);
                }

                return;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    Snapshot = monitorResult.Value;
                    IssueTitle = null;
                    IssueMessage = null;
                    State = SystemMonitorPanelState.Live;
                    StatusText = $"Live · {ConnectionDisplayName}";
                },
                linked.Token);
        }
        finally
        {
            _refreshGate.Release();
            if (!_disposed)
            {
                try
                {
                    await _dispatcher.InvokeAsync(
                        () => IsRefreshing = false,
                        _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
            }
        }
    }

    private Task ApplyHostFailureAsync(string title, string message) =>
        ApplyFailureAsync(title, message, CancellationToken.None);

    private Task ApplyCaptureFailureAsync(
        string title,
        string message,
        CancellationToken cancellationToken) =>
        ApplyFailureAsync(title, message, cancellationToken);

    private Task ApplyFailureAsync(
        string title,
        string message,
        CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(
            () =>
            {
                if (_disposed)
                {
                    return;
                }

                IssueTitle = title;
                IssueMessage = message;
                State = HasSnapshot
                    ? SystemMonitorPanelState.Stale
                    : SystemMonitorPanelState.Failed;
                StatusText = HasSnapshot ? "Stale · retry available" : "Unavailable";
            },
            cancellationToken);

    private void AppendHistory(SystemStatisticsSnapshot snapshot)
    {
        AppendBounded(_cpuSamples, snapshot.ObservedCpuPercent);
        AppendBounded(_memorySamples, snapshot.ObservedWorkingSetBytes);
        AppendBounded(_networkReceivedSamples, snapshot.NetworkReceivedBytesPerSecond);
        AppendBounded(_networkSentSamples, snapshot.NetworkSentBytesPerSecond);
        _cpuHistory = [.. _cpuSamples];
        _memoryHistory = [.. _memorySamples];
        _networkReceivedHistory = [.. _networkReceivedSamples];
        _networkSentHistory = [.. _networkSentSamples];
        OnPropertyChanged(nameof(CpuHistory));
        OnPropertyChanged(nameof(MemoryHistory));
        OnPropertyChanged(nameof(NetworkReceivedHistory));
        OnPropertyChanged(nameof(NetworkSentHistory));
    }

    private static void AppendBounded(List<double?> samples, double? value)
    {
        if (samples.Count == HistoryCapacity)
        {
            samples.RemoveAt(0);
        }

        samples.Add(value);
    }
}

public sealed class ProcessMonitorEntryViewModel : ObservableObject
{
    private ProcessMonitorEntry _entry;

    public ProcessMonitorEntryViewModel(ProcessMonitorEntry entry)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public ProcessMonitorEntry Entry => _entry;

    public int ProcessId => Entry.ProcessId;

    public string Name => Entry.Name;

    public string Cpu => Entry.CpuPercent is { } value
        ? $"{value.ToString("0.0", CultureInfo.InvariantCulture)}%"
        : "—";

    public string Memory => MonitorPanelPresentation.FormatBytes(Entry.WorkingSetBytes);

    public string Started => Entry.StartedAtUtc?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";

    public string AccessibleSummary =>
        $"PID {ProcessId}, {Name}, CPU {MonitorPanelPresentation.AccessiblePercent(Entry.CpuPercent)}, "
        + $"memory {Memory}, started {Started}.";

    internal void Update(ProcessMonitorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entry == entry)
        {
            return;
        }

        _entry = entry;
        // Avalonia treats an empty property name as "all properties changed".
        // One row notification lets its compiled bindings refresh together;
        // emitting one event per displayed field multiplies the UI-thread work.
        OnPropertyChanged(string.Empty);
    }
}

public sealed class ProcessMonitorRuntimePanelViewModel : RuntimePanelViewModel
{
    private readonly ISessionHostClient _sessionClient;
    private readonly ConnectionProfile _connection;
    private readonly ClientId _clientId;
    private readonly SessionOwner _owner;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly AsyncActionCommand _refreshCommand;
    private IReadOnlyList<ProcessMonitorEntry> _latestEntries = [];
    private ProcessMonitorSnapshot? _snapshot;
    private ProcessMonitorEntryViewModel? _selectedProcess;
    private ProcessMonitorSort _sort = ProcessMonitorSort.CpuDescending;
    private SystemMonitorPanelState _state = SystemMonitorPanelState.Waiting;
    private string _filter = string.Empty;
    private string _statusText = "Waiting for first sample…";
    private string? _issueTitle;
    private string? _issueMessage;
    private bool _isRefreshing;
    private bool _hasHostedSession;
    private bool _started;
    private bool _disposed;
    private long _hostRevision;
    private Task? _polling;
    private readonly string? _connectionDisplayName;

    public ProcessMonitorRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        ISessionHostClient sessionClient,
        ClientId clientId,
        SessionOwner owner,
        IUiThreadDispatcher dispatcher,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? connectionDisplayName = null)
        : this(
            id,
            title,
            sessionClient,
            clientId,
            owner,
            BuiltInConnections.Local,
            dispatcher,
            delay,
            connectionDisplayName)
    {
    }

    public ProcessMonitorRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        ISessionHostClient sessionClient,
        ClientId clientId,
        SessionOwner owner,
        ConnectionProfile connection,
        IUiThreadDispatcher dispatcher,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? connectionDisplayName = null)
        : base(id, PanelKind.ProcessMonitor, title, "Process monitor")
    {
        _sessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _clientId = clientId;
        _owner = owner;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _delay = delay ?? Task.Delay;
        _connectionDisplayName = connectionDisplayName;
        SessionId = SessionId.New();
        _refreshCommand = new AsyncActionCommand(
            RetryAsync,
            () => !_disposed
                && !IsRefreshing
                && (HasHostedSession || State == SystemMonitorPanelState.Failed));
    }

    public ObservableCollection<ProcessMonitorEntryViewModel> Processes { get; } = [];

    public SessionId SessionId { get; }

    public ConnectionId ConnectionId => _connection.Id;

    public string ConnectionDisplayName =>
        _connectionDisplayName
        ?? (_connection.Endpoint is ConnectionEndpoint.Local ? "Local" : _connection.Name);

    public Task Initialization { get; private set; } = Task.CompletedTask;

    public ICommand RefreshCommand => _refreshCommand;

    public ProcessMonitorSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                OnPropertyChanged(nameof(HasSnapshot));
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(ShowContent));
                OnPropertyChanged(nameof(ShowTerminalError));
                OnPropertyChanged(nameof(ShowInlineIssue));
                OnPropertyChanged(nameof(CapturedAtText));
            }
        }
    }

    public ProcessMonitorEntryViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public ProcessMonitorSort Sort
    {
        get => _sort;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (!SetProperty(ref _sort, value))
            {
                return;
            }

            NotifySortChanged();
            if (_started)
            {
                _ = RefreshAsync();
            }
        }
    }

    public bool IsSortingByCpu =>
        Sort is ProcessMonitorSort.CpuDescending or ProcessMonitorSort.CpuAscending;

    public bool IsSortingByMemory =>
        Sort is ProcessMonitorSort.MemoryDescending or ProcessMonitorSort.MemoryAscending;

    public bool IsSortingByName =>
        Sort is ProcessMonitorSort.NameAscending or ProcessMonitorSort.NameDescending;

    public bool IsSortingByProcessId =>
        Sort is ProcessMonitorSort.ProcessIdAscending or ProcessMonitorSort.ProcessIdDescending;

    public bool IsSortDescending =>
        Sort is ProcessMonitorSort.CpuDescending
            or ProcessMonitorSort.MemoryDescending
            or ProcessMonitorSort.NameDescending
            or ProcessMonitorSort.ProcessIdDescending;

    public void ChangeSort(ProcessMonitorSort sort)
    {
        if (!Enum.IsDefined(sort))
        {
            throw new ArgumentOutOfRangeException(nameof(sort), sort, null);
        }

        Sort = Sort == sort ? ReverseSort(sort) : sort;
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public SystemMonitorPanelState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(ShowContent));
                OnPropertyChanged(nameof(ShowTerminalError));
                OnPropertyChanged(nameof(ShowInlineIssue));
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? IssueTitle
    {
        get => _issueTitle;
        private set
        {
            if (SetProperty(ref _issueTitle, value))
            {
                OnPropertyChanged(nameof(HasIssue));
                OnPropertyChanged(nameof(ShowInlineIssue));
            }
        }
    }

    public string? IssueMessage
    {
        get => _issueMessage;
        private set => SetProperty(ref _issueMessage, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasHostedSession
    {
        get => _hasHostedSession;
        private set
        {
            if (SetProperty(ref _hasHostedSession, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSnapshot => Snapshot is not null;

    public bool HasIssue => IssueTitle is not null;

    public bool ShowLoading => !HasSnapshot && State == SystemMonitorPanelState.Waiting;

    public bool ShowContent => HasSnapshot;

    public bool ShowTerminalError => !HasSnapshot && State == SystemMonitorPanelState.Failed;

    public bool ShowInlineIssue => HasSnapshot && HasIssue;

    public string StatusColor => State switch
    {
        SystemMonitorPanelState.Live => "#72B57B",
        SystemMonitorPanelState.Stale => "#D79B57",
        SystemMonitorPanelState.Failed => "#D96B6B",
        SystemMonitorPanelState.Disposed => "#77777F",
        _ => "#9A9AA2",
    };

    public string CapturedAtText => Snapshot is { } snapshot
        ? $"Captured {snapshot.CapturedAtUtc.ToLocalTime():T}"
        : "No sample captured";

    /// <summary>
    /// How many are in the list. It used to also say how the sample was taken
    /// and how many were walked to get it — three facts where one was wanted,
    /// and two of them about the shell rather than about the machine. Whether a
    /// sample is bounded belongs in the settings that bound it.
    /// </summary>
    public string ShowingText => Snapshot is null
        ? string.Empty
        : $"{Processes.Count:N0} process{(Processes.Count == 1 ? string.Empty : "es")}";

    public Task Start()
    {
        if (_disposed || _started)
        {
            return Initialization;
        }

        _started = true;
        Initialization = InitializeAsync();
        return Initialization;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(cancellationToken);

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        State = SystemMonitorPanelState.Disposed;
        StatusText = "Monitoring stopped";
        _refreshCommand.RaiseCanExecuteChanged();
        base.Dispose();
    }

    private async Task InitializeAsync()
    {
        HostResult<SessionSnapshot> result;
        try
        {
            result = await _sessionClient.EnsureProcessMonitorSessionAsync(
                new EnsureProcessMonitorSessionRequest(SessionId, _owner, Title, _connection),
                OperationContext.ForHuman(
                    _clientId,
                    idempotencyKey: IdempotencyKey.New()),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            await ApplyFailureAsync(
                "Process monitor unavailable",
                $"The process-monitor session for {ConnectionDisplayName} could not be started.",
                CancellationToken.None);
            return;
        }

        if (result is HostResult<SessionSnapshot>.Failure failure)
        {
            await ApplyFailureAsync(
                "Process monitor unavailable",
                failure.Error.Message,
                CancellationToken.None);
            return;
        }

        var success = (HostResult<SessionSnapshot>.Success)result;
        _hostRevision = success.ResultingRevision;
        await _dispatcher.InvokeAsync(
            () => HasHostedSession = true,
            _lifetime.Token);
        await RefreshCoreAsync(_lifetime.Token);
        if (!_lifetime.IsCancellationRequested)
        {
            _polling = PollAsync(_lifetime.Token);
        }
    }

    private async Task RetryAsync()
    {
        if (HasHostedSession)
        {
            await RefreshCoreAsync(CancellationToken.None);
            return;
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                IssueTitle = null;
                IssueMessage = null;
                State = SystemMonitorPanelState.Waiting;
                StatusText = $"Starting process monitor · {ConnectionDisplayName}…";
            },
            CancellationToken.None);
        Initialization = InitializeAsync();
        await Initialization;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = State == SystemMonitorPanelState.Live ? 0 : 1;
        try
        {
            while (true)
            {
                await _delay(
                    SystemMonitorPolling.Delay(
                        _connection.ConnectionKind,
                        consecutiveFailures),
                    cancellationToken);
                await RefreshCoreAsync(cancellationToken);
                consecutiveFailures = State == SystemMonitorPanelState.Live
                    ? 0
                    : Math.Min(consecutiveFailures + 1, 5);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!HasHostedSession || _disposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _refreshGate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() => IsRefreshing = true, linked.Token);
            HostResult<MonitorPanelResult<ProcessMonitorSnapshot>> result;
            try
            {
                result = await _sessionClient.ListProcessesAsync(
                    new ProcessMonitorHostRequest(
                        SessionId,
                        new ProcessMonitorQuery(
                            ProcessMonitorQuery.DefaultMaximumResults,
                            Sort)),
                    OperationContext.ForHuman(_clientId, _hostRevision),
                    linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                await ApplyFailureAsync(
                    "Process refresh failed",
                    $"The process list for {ConnectionDisplayName} could not be loaded.",
                    linked.Token);
                return;
            }

            if (result is HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>.Failure failure)
            {
                await ApplyFailureAsync(
                    "Process refresh failed",
                    failure.Error.Message,
                    linked.Token);
                return;
            }

            var monitorResult =
                ((HostResult<MonitorPanelResult<ProcessMonitorSnapshot>>.Success)result).Value;
            if (!monitorResult.IsSuccess)
            {
                if (monitorResult.Error!.Code != MonitorPanelErrorCode.Cancelled)
                {
                    await ApplyFailureAsync(
                        "Process refresh failed",
                        monitorResult.Error.Message,
                        linked.Token);
                }

                return;
            }

            await _dispatcher.InvokeAsync(
                () =>
                {
                    Snapshot = monitorResult.Value;
                    _latestEntries = Snapshot!.Processes;
                    ApplyFilter();
                    IssueTitle = null;
                    IssueMessage = null;
                    State = SystemMonitorPanelState.Live;
                    StatusText = $"Live · {ConnectionDisplayName}";
                },
                linked.Token);
        }
        finally
        {
            _refreshGate.Release();
            if (!_disposed)
            {
                try
                {
                    await _dispatcher.InvokeAsync(
                        () => IsRefreshing = false,
                        _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                }
            }
        }
    }

    private Task ApplyFailureAsync(
        string title,
        string message,
        CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(
            () =>
            {
                if (_disposed)
                {
                    return;
                }

                IssueTitle = title;
                IssueMessage = message;
                State = HasSnapshot
                    ? SystemMonitorPanelState.Stale
                    : SystemMonitorPanelState.Failed;
                StatusText = HasSnapshot ? "Stale · retry available" : "Unavailable";
            },
            cancellationToken);

    private void ApplyFilter()
    {
        var hadSelection = SelectedProcess is { };
        var selectedIdentity = SelectedProcess is { } selected
            ? (selected.Entry.ProcessId, selected.Entry.StartedAtUtc)
            : default;
        var terms = Filter.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var visibleEntries = _latestEntries
            .Where(entry =>
                terms.Length == 0
                || terms.All(term =>
                    entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || entry.ProcessId.ToString(CultureInfo.InvariantCulture)
                        .Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // Rows are stable presentation slots. Rebuilding the collection every two
        // seconds makes Avalonia discard and recreate as many as 250 containers,
        // briefly starving other composited surfaces in the same window.
        var retainedCount = Math.Min(Processes.Count, visibleEntries.Length);
        for (var index = 0; index < retainedCount; index++)
        {
            Processes[index].Update(visibleEntries[index]);
        }

        while (Processes.Count > visibleEntries.Length)
        {
            Processes.RemoveAt(Processes.Count - 1);
        }

        for (var index = retainedCount; index < visibleEntries.Length; index++)
        {
            Processes.Add(new ProcessMonitorEntryViewModel(visibleEntries[index]));
        }

        SelectedProcess = hadSelection
            ? Processes.FirstOrDefault(process =>
                process.Entry.ProcessId == selectedIdentity.ProcessId
                && process.Entry.StartedAtUtc == selectedIdentity.StartedAtUtc)
            : null;
        OnPropertyChanged(nameof(ShowingText));
    }

    private void NotifySortChanged()
    {
        OnPropertyChanged(nameof(IsSortingByCpu));
        OnPropertyChanged(nameof(IsSortingByMemory));
        OnPropertyChanged(nameof(IsSortingByName));
        OnPropertyChanged(nameof(IsSortingByProcessId));
        OnPropertyChanged(nameof(IsSortDescending));
    }

    private static ProcessMonitorSort ReverseSort(ProcessMonitorSort sort) =>
        sort switch
        {
            ProcessMonitorSort.CpuDescending => ProcessMonitorSort.CpuAscending,
            ProcessMonitorSort.CpuAscending => ProcessMonitorSort.CpuDescending,
            ProcessMonitorSort.MemoryDescending => ProcessMonitorSort.MemoryAscending,
            ProcessMonitorSort.MemoryAscending => ProcessMonitorSort.MemoryDescending,
            ProcessMonitorSort.NameAscending => ProcessMonitorSort.NameDescending,
            ProcessMonitorSort.NameDescending => ProcessMonitorSort.NameAscending,
            ProcessMonitorSort.ProcessIdAscending => ProcessMonitorSort.ProcessIdDescending,
            ProcessMonitorSort.ProcessIdDescending => ProcessMonitorSort.ProcessIdAscending,
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null),
        };
}

internal static class MonitorPanelPresentation
{
    public static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "Unavailable";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, (double)bytes.Value);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value.ToString("0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        var bounded = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return bounded.TotalDays >= 1
            ? $"{(int)bounded.TotalDays}d {bounded.Hours}h {bounded.Minutes}m"
            : $"{bounded.Hours}h {bounded.Minutes}m";
    }

    public static string FormatBytesPerSecond(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } rate || !double.IsFinite(rate))
        {
            return "Unavailable";
        }

        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s"];
        var value = Math.Max(0, rate);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value.ToString("0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string AccessiblePercent(double? value) => value is { } percent
        ? $"{percent.ToString("0.0", CultureInfo.InvariantCulture)} percent"
        : "unavailable";
}
