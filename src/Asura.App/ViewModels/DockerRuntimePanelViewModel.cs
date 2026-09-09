using System.Collections.ObjectModel;
using System.Windows.Input;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.App.ViewModels;

/// <summary>
/// Projects one Docker engine into the three-column resource browser. The engine
/// client owns command execution and parsing; this type owns only selection,
/// presentation state, and user-initiated refresh/action sequencing.
/// </summary>
public sealed class DockerRuntimePanelViewModel : RuntimePanelViewModel
{
    private const int LogPageSize = 500;
    private static readonly TimeSpan LogFollowInterval = TimeSpan.FromSeconds(2);
    private readonly IDockerEngineClient _client;
    private readonly ConnectionProfile _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private HostedPanelSessionLink? _hostedSession;
    private ISessionHostClient? _hostSessionClient;
    private Task _hostInitialization = Task.CompletedTask;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private readonly AsyncActionCommand _refreshCommand;
    private readonly AsyncActionCommand _startCommand;
    private readonly AsyncActionCommand _stopCommand;
    private readonly AsyncActionCommand _restartCommand;
    private readonly AsyncActionCommand _pauseCommand;
    private readonly AsyncActionCommand _resumeCommand;
    private readonly Dictionary<string, TerminalRuntimePanelViewModel> _inlineShells =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileRuntimePanelViewModel> _fileBrowsers =
        new(StringComparer.Ordinal);
    private IReadOnlyList<DockerResourceItemViewModel> _resources = [];
    private IReadOnlyList<DockerContainerStackViewModel> _containerStacks = [];
    private DockerResourceItemViewModel? _selectedResource;
    private TerminalRuntimePanelViewModel? _inlineShell;
    private FileRuntimePanelViewModel? _fileBrowser;
    private DockerEngineSnapshot? _snapshot;
    private DockerResourceInspection? _inspection;
    private DockerPanelSection _section;
    private DockerPanelDetail _detail;
    private readonly ObservableCollection<DockerLogRowViewModel> _logRows = [];
    private string _logSearchText = string.Empty;
    private string? _activeLogSearchText;
    private int _logSearchContext;
    private string? _oldestLogTimestamp;
    private string? _newestLogTimestamp;
    private string? _loadedLogContainerId;
    private string? _logIssueMessage;
    private bool _hasOlderLogs;
    private bool _isLoadingLogs;
    private bool _followLogs = true;
    private bool _isDownloadingLogs;
    private long _logScrollToEndRequest;
    private string _statusText = "Connecting to Docker…";
    private string _shellStateTitle = "Opening container shell…";
    private string _shellStateMessage =
        "This shell stays in the Docker panel. Use New tab when you want it beside other work.";
    private string? _issueTitle;
    private string? _issueMessage;
    private bool _showsDockerInstallHelp;
    private bool _isRefreshing;
    private bool _isLoadingDetail;
    private bool _isResolvingShell;
    private bool _isCalculatingVolumeSizes;
    private bool _isRunningStackAction;
    private bool _volumeUsageLoaded;
    private bool _disposed;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _logCancellation;
    private CancellationTokenSource? _logFollowCancellation;
    private CancellationTokenSource? _volumeUsageCancellation;
    private Task _volumeUsageLoading = Task.CompletedTask;
    private readonly string? _connectionDisplayName;

    public DockerRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IDockerEngineClient client,
        ConnectionProfile connection,
        string? connectionDisplayName = null)
        : base(id, PanelKind.Docker, title, "Docker")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connectionDisplayName = connectionDisplayName;
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            throw new ArgumentException(
                "Docker panels require a local or SSH connection.",
                nameof(connection));
        }

        _refreshCommand = new AsyncActionCommand(
            RefreshAsync,
            () => !_disposed && !IsRefreshing);
        _startCommand = ActionCommand(
            DockerContainerAction.Start,
            () => SelectedResource is { IsContainer: true, IsRunning: false, IsPaused: false });
        _stopCommand = ActionCommand(
            DockerContainerAction.Stop,
            () => SelectedResource is
            { IsContainer: true, IsRunning: true }
                or { IsContainer: true, IsPaused: true });
        _restartCommand = ActionCommand(
            DockerContainerAction.Restart,
            () => SelectedResource is
            { IsContainer: true, IsPaused: true }
                or { IsContainer: true, IsRunning: true });
        _pauseCommand = ActionCommand(
            DockerContainerAction.Pause,
            () => SelectedResource is { IsContainer: true, IsRunning: true, IsPaused: false });
        _resumeCommand = ActionCommand(
            DockerContainerAction.Resume,
            () => SelectedResource is { IsContainer: true, IsPaused: true });
        Initialization = RefreshAsync();
    }

    public Task Initialization { get; }

    public SessionId? HostedSessionId => _hostedSession?.SessionId;

    public CapabilitySet HostedCapabilities =>
        _hostedSession?.Capabilities ?? CapabilitySet.Empty;

    public bool HasHostedSession => _hostedSession?.IsLinked == true;

    public Task StartHostingAsync(
        ISessionHostClient sessionClient,
        ClientId clientId,
        SessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(sessionClient);
        ArgumentNullException.ThrowIfNull(owner);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hostedSession is not null)
        {
            return _hostInitialization;
        }

        _hostSessionClient = sessionClient;
        _hostedSession = new HostedPanelSessionLink(
            sessionClient,
            clientId,
            owner,
            PanelKind.Docker);
        _hostInitialization = InitializeHostedSessionAsync();
        return _hostInitialization;
    }

    public ConnectionId ConnectionId => _connection.Id;

    public ConnectionProfile Connection => _connection;

    public string ConnectionDisplayName =>
        _connectionDisplayName
        ?? (_connection.Endpoint is ConnectionEndpoint.Local ? "Local" : _connection.Name);

    public DockerPanelSection Section
    {
        get => _section;
        private set
        {
            if (SetProperty(ref _section, value))
            {
                PublishSectionState();
            }
        }
    }

    public DockerPanelDetail Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                PublishDetailState();
                if (value == DockerPanelDetail.Logs)
                {
                    StartFollowingLogs();
                }
                else
                {
                    StopFollowingLogs();
                }
            }
        }
    }

    public IReadOnlyList<DockerResourceItemViewModel> Resources
    {
        get => _resources;
        private set
        {
            if (SetProperty(ref _resources, value))
            {
                OnPropertyChanged(nameof(ResourceCount));
                OnPropertyChanged(nameof(ResourceSummary));
            }
        }
    }

    public int ResourceCount => Resources.Count;

    public string ResourceSummary => IsVolumesSection && IsCalculatingVolumeSizes
        ? $"{ResourceCount} resources · Loading sizes…"
        : $"{ResourceCount} resources";

    public bool ShowVolumeSizeLoading => IsVolumesSection && IsCalculatingVolumeSizes;

    public bool ShowResourceProgress => IsRefreshing || ShowVolumeSizeLoading;

    public IReadOnlyList<DockerContainerStackViewModel> ContainerStacks
    {
        get => _containerStacks;
        private set => SetProperty(ref _containerStacks, value);
    }

    public DockerResourceItemViewModel? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (!SetProperty(ref _selectedResource, value))
            {
                value?.IsSelected = true;

                return;
            }

            foreach (var resource in Resources)
            {
                resource.IsSelected = ReferenceEquals(resource, value);
            }

            InlineShell = value?.Container is { } container
                ? _inlineShells.GetValueOrDefault(container.Id)
                : null;
            FileBrowser = value is { Resource.Kind: not DockerResourceKind.Network }
                ? GetOrCreateFileBrowser(value.Resource)
                : null;
            ResetShellState();
            Inspection = null;
            ResetLogs();
            PublishSelectionState();
            if (!IsDetailAvailable(Detail))
            {
                Detail = DockerPanelDetail.Info;
            }

            if (Detail == DockerPanelDetail.Files)
            {
                _ = FileBrowser?.StartInitialization();
            }

            _ = LoadSelectedResourceAsync();
        }
    }

    public TerminalRuntimePanelViewModel? InlineShell
    {
        get => _inlineShell;
        private set
        {
            if (SetProperty(ref _inlineShell, value))
            {
                OnPropertyChanged(nameof(HasInlineShell));
                OnPropertyChanged(nameof(CanRetryShell));
            }
        }
    }

    public FileRuntimePanelViewModel? FileBrowser
    {
        get => _fileBrowser;
        private set
        {
            if (SetProperty(ref _fileBrowser, value))
            {
                OnPropertyChanged(nameof(HasFileBrowser));
            }
        }
    }

    public DockerEngineSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                PublishSnapshotState();
            }
        }
    }

    public DockerResourceInspection? Inspection
    {
        get => _inspection;
        private set
        {
            if (SetProperty(ref _inspection, value))
            {
                OnPropertyChanged(nameof(HasInspection));
            }
        }
    }

    public ObservableCollection<DockerLogRowViewModel> LogRows => _logRows;

    public string LogSearchText
    {
        get => _logSearchText;
        set => SetProperty(ref _logSearchText, value);
    }

    public int LogSearchContext
    {
        get => _logSearchContext;
        set
        {
            if (SetProperty(ref _logSearchContext, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(LogSearchContextInput));
            }
        }
    }

    /// <summary>
    /// Matches Avalonia's nullable decimal editor value so an empty or partial
    /// numeric edit never requires a failing string-to-int binding conversion.
    /// </summary>
    public decimal? LogSearchContextInput
    {
        get => LogSearchContext;
        set => LogSearchContext = value is null
            ? 0
            : decimal.ToInt32(decimal.Truncate(Math.Clamp(value.Value, 0m, 100m)));
    }

    public string? LogIssueMessage
    {
        get => _logIssueMessage;
        private set
        {
            if (SetProperty(ref _logIssueMessage, value))
            {
                OnPropertyChanged(nameof(HasLogIssue));
            }
        }
    }

    public bool HasOlderLogs
    {
        get => _hasOlderLogs;
        private set => SetProperty(ref _hasOlderLogs, value);
    }

    public bool IsLoadingLogs
    {
        get => _isLoadingLogs;
        private set => SetProperty(ref _isLoadingLogs, value);
    }

    public bool FollowLogs
    {
        get => _followLogs;
        set
        {
            if (!SetProperty(ref _followLogs, value))
            {
                return;
            }

            if (value)
            {
                RequestLogScrollToEnd();
                StartFollowingLogs();
            }
            else
            {
                StopFollowingLogs();
            }
        }
    }

    public bool IsDownloadingLogs
    {
        get => _isDownloadingLogs;
        private set => SetProperty(ref _isDownloadingLogs, value);
    }

    public long LogScrollToEndRequest
    {
        get => _logScrollToEndRequest;
        private set => SetProperty(ref _logScrollToEndRequest, value);
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
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public string? IssueMessage
    {
        get => _issueMessage;
        private set => SetProperty(ref _issueMessage, value);
    }

    public bool ShowsDockerInstallHelp
    {
        get => _showsDockerInstallHelp;
        private set => SetProperty(ref _showsDockerInstallHelp, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
                PublishContainerActionState();
                RaiseContainerActionCanExecuteChanged();
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowResourceProgress));
            }
        }
    }

    public bool IsLoadingDetail
    {
        get => _isLoadingDetail;
        private set => SetProperty(ref _isLoadingDetail, value);
    }

    public bool IsResolvingShell
    {
        get => _isResolvingShell;
        private set
        {
            if (SetProperty(ref _isResolvingShell, value))
            {
                OnPropertyChanged(nameof(CanRetryShell));
            }
        }
    }

    public bool IsCalculatingVolumeSizes
    {
        get => _isCalculatingVolumeSizes;
        private set
        {
            if (SetProperty(ref _isCalculatingVolumeSizes, value))
            {
                OnPropertyChanged(nameof(ResourceSummary));
                OnPropertyChanged(nameof(ShowVolumeSizeLoading));
                OnPropertyChanged(nameof(ShowResourceProgress));
            }
        }
    }

    public Task VolumeUsageLoading => _volumeUsageLoading;

    public string ShellStateTitle
    {
        get => _shellStateTitle;
        private set => SetProperty(ref _shellStateTitle, value);
    }

    public string ShellStateMessage
    {
        get => _shellStateMessage;
        private set => SetProperty(ref _shellStateMessage, value);
    }

    public bool IsContainersSection => Section == DockerPanelSection.Containers;

    public bool IsImagesSection => Section == DockerPanelSection.Images;

    public bool IsVolumesSection => Section == DockerPanelSection.Volumes;

    public bool IsNetworksSection => Section == DockerPanelSection.Networks;

    public bool IsInfoDetail => Detail == DockerPanelDetail.Info;

    public bool IsLogsDetail => Detail == DockerPanelDetail.Logs;

    public bool IsStatsDetail => Detail == DockerPanelDetail.Stats;

    public bool IsShellDetail => Detail == DockerPanelDetail.Shell;

    public bool IsFilesDetail => Detail == DockerPanelDetail.Files;

    public bool IsJsonDetail => Detail == DockerPanelDetail.Json;

    public bool HasSnapshot => Snapshot is not null;

    public bool HasIssue => IssueTitle is not null;

    public bool HasSelection => SelectedResource is not null;

    public bool HasInspection => Inspection is not null;

    public bool HasLogs => LogRows.Count > 0;

    public bool HasLogIssue => LogIssueMessage is not null;

    public bool IsLogSearchActive => _activeLogSearchText is not null;

    public bool CanFollowLogs => !IsLogSearchActive;

    public string LogResultSummary => IsLogSearchActive
        ? $"{LogRows.Count} filtered rows"
        : $"{LogRows.Count} loaded rows";

    public string LogDownloadFileName => SelectedResource?.Container is { } container
        ? $"{SanitizeFileName(container.Name)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log"
        : "container.log";

    public bool HasInlineShell => InlineShell is not null;

    public bool HasFileBrowser => FileBrowser is not null;

    public bool CanRetryShell => !IsResolvingShell && !HasInlineShell;

    public bool ShowLoading => IsRefreshing && !HasSnapshot;

    public bool ShowEmptyState =>
        HasSnapshot && !IsRefreshing && !HasSelection && !HasIssue;

    public bool CanOpenShell => SelectedResource is { IsContainer: true, IsRunning: true };

    public bool CanStartSelectedContainer =>
        !IsRefreshing
        && SelectedResource is { IsContainer: true, IsRunning: false, IsPaused: false };

    public bool CanStopSelectedContainer =>
        !IsRefreshing
        && SelectedResource is
    { IsContainer: true, IsRunning: true }
            or { IsContainer: true, IsPaused: true };

    public bool CanRestartSelectedContainer =>
        !IsRefreshing
        && SelectedResource is
    { IsContainer: true, IsPaused: true }
            or { IsContainer: true, IsRunning: true };

    public bool CanPauseSelectedContainer =>
        !IsRefreshing
        && SelectedResource is { IsContainer: true, IsRunning: true, IsPaused: false };

    public bool CanResumeSelectedContainer =>
        !IsRefreshing
        && SelectedResource is { IsContainer: true, IsPaused: true };

    public bool SelectedContainerIsStopped =>
        SelectedResource is { IsContainer: true, IsRunning: false, IsPaused: false };

    public bool SelectedContainerIsActive =>
        SelectedResource is
    { IsContainer: true, IsRunning: true }
            or { IsContainer: true, IsPaused: true };

    public bool CanBrowseFiles =>
        SelectedResource?.Resource.Kind is DockerResourceKind.Container
            or DockerResourceKind.Image
            or DockerResourceKind.Volume;

    public bool ShowLogsTab => SelectedResource?.IsContainer == true;

    public bool ShowStatsTab => SelectedResource?.IsContainer == true;

    public bool ShowShellTab => CanOpenShell;

    public bool ShowFilesTab => CanBrowseFiles;

    public bool ShowJsonTab => HasSelection;

    public int ContainerCount => Snapshot?.Containers.Count ?? 0;

    public int RunningContainerCount => Snapshot?.Containers.Count(item => item.IsRunning) ?? 0;

    public int ImageCount => Snapshot?.Images.Count ?? 0;

    public int VolumeCount => Snapshot?.Volumes.Count ?? 0;

    public int NetworkCount => Snapshot?.Networks.Count ?? 0;

    public string EngineVersion => Snapshot?.Engine.Version ?? "—";

    public string EnginePlatform => Snapshot is { } snapshot
        ? $"{snapshot.Engine.OperatingSystem} · {snapshot.Engine.Architecture}"
        : "Docker engine";

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand StartCommand => _startCommand;

    public ICommand StopCommand => _stopCommand;

    public ICommand RestartCommand => _restartCommand;

    public ICommand PauseCommand => _pauseCommand;

    public ICommand ResumeCommand => _resumeCommand;

    public async Task RunStackActionAsync(
        DockerContainerStackViewModel stack,
        DockerContainerAction action)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (_disposed
            || IsRefreshing
            || _isRunningStackAction
            || !ContainerStacks.Contains(stack))
        {
            return;
        }

        var targets = stack.Containers
            .Where(container => CanApplyStackAction(container, action))
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        _isRunningStackAction = true;
        IssueTitle = null;
        IssueMessage = null;
        DockerError? firstFailure = null;
        try
        {
            foreach (var target in targets)
            {
                var result = await _client.RunContainerActionAsync(
                    _connection,
                    target.Resource.Id,
                    action,
                    _lifetime.Token);
                if (result is DockerResult<bool>.Failure failure)
                {
                    firstFailure ??= failure.Error;
                }
            }

            await RefreshAsync();
            if (firstFailure is not null)
            {
                PresentFailure(
                    firstFailure,
                    $"Could not {ActionVerb(action)} every container in {stack.Name}");
                return;
            }

            StatusText = $"{stack.Name} {ActionPastTense(action)} · {targets.Length} containers";
        }
        finally
        {
            _isRunningStackAction = false;
        }
    }

    public void SelectSection(DockerPanelSection section)
    {
        if (_disposed || Section == section)
        {
            return;
        }

        Section = section;
        ProjectResources(section);
        SelectedResource = Resources.FirstOrDefault();
        Detail = DockerPanelDetail.Info;
        if (section == DockerPanelSection.Volumes)
        {
            StartVolumeUsageLoad();
        }
    }

    public void SelectDetail(DockerPanelDetail detail)
    {
        if (_disposed || Detail == detail)
        {
            return;
        }

        if (!IsDetailAvailable(detail))
        {
            return;
        }

        Detail = detail;
        if (detail == DockerPanelDetail.Logs && _loadedLogContainerId is null)
        {
            _ = LoadInitialLogsAsync();
        }

        else if (detail == DockerPanelDetail.Files)
        {
            _ = FileBrowser?.StartInitialization();
        }
    }

    public void SelectResource(DockerResourceItemViewModel resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (!Resources.Contains(resource))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resource),
                "The selected Docker resource must belong to this panel.");
        }

        SelectedResource = resource;
    }

    public void AttachInlineShell(
        string containerId,
        TerminalRuntimePanelViewModel shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(shell);
        if (_disposed)
        {
            shell.Dispose();
            return;
        }

        if (_inlineShells.Remove(containerId, out var previous))
        {
            previous.Dispose();
        }

        _inlineShells.Add(containerId, shell);
        if (string.Equals(SelectedResource?.Container?.Id, containerId, StringComparison.Ordinal))
        {
            InlineShell = shell;
            ResetShellState();
        }
    }

    public void BeginShellResolution(string containerId)
    {
        if (!string.Equals(SelectedResource?.Container?.Id, containerId, StringComparison.Ordinal))
        {
            return;
        }

        ShellStateTitle = "Finding a container shell…";
        ShellStateMessage = "Checking common interactive shell paths inside this container.";
        IsResolvingShell = true;
    }

    public void PresentShellResolutionFailure(string containerId, DockerError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!string.Equals(SelectedResource?.Container?.Id, containerId, StringComparison.Ordinal))
        {
            return;
        }

        Detail = DockerPanelDetail.Shell;
        ShellStateTitle = error.Code == DockerErrorCode.ShellUnavailable
            ? "No interactive shell found"
            : "Container shell unavailable";
        ShellStateMessage = error.Message;
        IsResolvingShell = false;
    }

    public void CompleteShellResolution(string containerId)
    {
        if (string.Equals(SelectedResource?.Container?.Id, containerId, StringComparison.Ordinal))
        {
            ResetShellState();
        }
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        IsRefreshing = true;
        IssueTitle = null;
        IssueMessage = null;
        ShowsDockerInstallHelp = false;
        CancelVolumeUsageLoad();
        _volumeUsageLoaded = false;
        try
        {
            var selected = SelectedResource?.Resource;
            var result = await _client.ReadSnapshotAsync(_connection, _lifetime.Token);
            if (result is DockerResult<DockerEngineSnapshot>.Failure failure)
            {
                PresentFailure(failure.Error, "Docker unavailable");
                return;
            }

            Snapshot = ((DockerResult<DockerEngineSnapshot>.Success)result).Value;
            ProjectResources(Section);
            SelectedResource = selected is null
                ? Resources.FirstOrDefault()
                : Resources.FirstOrDefault(item =>
                    item.Resource.Kind == selected.Kind
                    && string.Equals(item.Resource.Id, selected.Id, StringComparison.Ordinal))
                    ?? Resources.FirstOrDefault();
            StatusText = $"{RunningContainerCount} running · Docker {EngineVersion}";
            QueueHostedSessionEnsure();
            if (Section == DockerPanelSection.Volumes)
            {
                StartVolumeUsageLoad();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hostedSession?.Dispose();
        _lifetime.Cancel();
        CancelVolumeUsageLoad();
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        CancelLogLoad();
        StopFollowingLogs();
        foreach (var shell in _inlineShells.Values)
        {
            shell.Dispose();
        }

        _inlineShells.Clear();
        foreach (var browser in _fileBrowsers.Values)
        {
            browser.Dispose();
        }

        _fileBrowsers.Clear();
        InlineShell = null;
        FileBrowser = null;
        _lifetime.Dispose();
        base.Dispose();
    }

    private async Task InitializeHostedSessionAsync()
    {
        try
        {
            await Initialization.ConfigureAwait(true);
            if (_disposed || Snapshot is null || _hostedSession?.IsLinked == true)
            {
                return;
            }

            await EnsureHostedSessionAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Docker's direct human client is independent of the governed
            // hosted projection, so the panel stays usable when hosting fails.
        }
    }

    private void QueueHostedSessionEnsure()
    {
        if (_hostedSession is not null && _hostSessionClient is not null)
        {
            _ = EnsureHostedSessionAsync(_lifetime.Token);
        }
    }

    private Task<bool> EnsureHostedSessionAsync(CancellationToken cancellationToken)
    {
        var hosted = _hostedSession;
        var sessionClient = _hostSessionClient;
        if (hosted is null || sessionClient is null || _disposed)
        {
            return Task.FromResult(false);
        }

        var target = new DockerSessionTarget(_connection, bindingRevision: 0);
        return hosted.EnsureAsync(
            (sessionId, context, token) =>
                sessionClient.EnsureDockerSessionAsync(
                    new EnsureDockerSessionRequest(
                        sessionId,
                        hosted.Owner,
                        Title,
                        target),
                    context,
                    token),
            cancellationToken);
    }

    private AsyncActionCommand ActionCommand(
        DockerContainerAction action,
        Func<bool> canExecute) => new(
        () => RunContainerActionAsync(action),
        () => !_disposed && !IsRefreshing && canExecute());

    private async Task RunContainerActionAsync(DockerContainerAction action)
    {
        if (SelectedResource?.Container is not { } container)
        {
            return;
        }

        IssueTitle = null;
        IssueMessage = null;
        var result = await _client.RunContainerActionAsync(
            _connection,
            container.Id,
            action,
            _lifetime.Token);
        if (result is DockerResult<bool>.Failure failure)
        {
            PresentFailure(failure.Error, $"Could not {ActionVerb(action)} container");
            return;
        }

        StatusText = $"Container {ActionPastTense(action)}";
        await RefreshAsync();
    }

    private async Task LoadSelectedResourceAsync()
    {
        var selected = SelectedResource;
        if (selected is null || _disposed)
        {
            return;
        }

        var cancellation = BeginDetailLoad();
        IsLoadingDetail = true;
        try
        {
            var result = await _client.InspectAsync(
                _connection,
                selected.Resource,
                cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (result is DockerResult<DockerResourceInspection>.Failure failure)
            {
                PresentFailure(failure.Error, "Could not inspect resource");
                return;
            }

            Inspection = ((DockerResult<DockerResourceInspection>.Success)result).Value;
            if (Detail == DockerPanelDetail.Logs && _loadedLogContainerId is null)
            {
                await LoadInitialLogsAsync();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                IsLoadingDetail = false;
            }
        }
    }

    public async Task SearchLogsAsync()
    {
        if (_disposed)
        {
            return;
        }

        var query = LogSearchText.Trim();
        if (query.Length == 0)
        {
            await ClearLogSearchAsync();
            return;
        }

        _activeLogSearchText = query;
        PublishLogModeState();
        StopFollowingLogs();
        await LoadInitialLogsAsync(force: true);
    }

    public async Task ClearLogSearchAsync()
    {
        if (_activeLogSearchText is null)
        {
            LogSearchText = string.Empty;
            return;
        }

        _activeLogSearchText = null;
        LogSearchText = string.Empty;
        PublishLogModeState();
        await LoadInitialLogsAsync(force: true);
    }

    public async Task<bool> LoadOlderLogsAsync()
    {
        if (_disposed
            || !HasOlderLogs
            || _oldestLogTimestamp is null
            || SelectedResource?.Container is not { } container)
        {
            return false;
        }

        try
        {
            if (!await _logGate.WaitAsync(0, _lifetime.Token))
            {
                return false;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }

        IsLoadingLogs = true;
        try
        {
            var expectedContainerId = container.Id;
            var result = await _client.ReadContainerLogsAsync(
                _connection,
                CreateLogRequest(container.Id, beforeTimestamp: _oldestLogTimestamp),
                _lifetime.Token);
            if (_disposed || !string.Equals(SelectedResource?.Container?.Id, expectedContainerId, StringComparison.Ordinal))
            {
                return false;
            }

            if (result is DockerResult<DockerContainerLogPage>.Failure failure)
            {
                LogIssueMessage = failure.Error.Message;
                return false;
            }

            var page = ((DockerResult<DockerContainerLogPage>.Success)result).Value;
            var prependCount = PrependWithoutOverlap(page.Lines);
            _oldestLogTimestamp = page.OldestTimestamp ?? _oldestLogTimestamp;
            HasOlderLogs = page.HasOlder;
            PublishLogRowsState();
            return prependCount > 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsLoadingLogs = false;
            _logGate.Release();
        }
    }

    public async Task<DockerResult<bool>> DownloadLogsAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (SelectedResource?.Container is not { } container || _disposed)
        {
            return new DockerResult<bool>.Failure(new DockerError(
                DockerErrorCode.CommandFailed,
                "Select a container before downloading logs.",
                false));
        }

        IsDownloadingLogs = true;
        LogIssueMessage = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        try
        {
            var result = await _client.DownloadContainerLogsAsync(
                _connection,
                container.Id,
                destination,
                linked.Token);
            if (result is DockerResult<bool>.Failure failure)
            {
                LogIssueMessage = failure.Error.Message;
            }

            return result;
        }
        finally
        {
            IsDownloadingLogs = false;
        }
    }

    private async Task LoadInitialLogsAsync(bool force = false)
    {
        if (SelectedResource?.Container is not { } container
            || _disposed
            || (!force && string.Equals(_loadedLogContainerId, container.Id, StringComparison.Ordinal)))
        {
            return;
        }

        try
        {
            await _logGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        if (!force && string.Equals(_loadedLogContainerId, container.Id, StringComparison.Ordinal))
        {
            _logGate.Release();
            return;
        }

        CancelLogLoad();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _logCancellation = cancellation;
        IsLoadingLogs = true;
        LogIssueMessage = null;
        try
        {
            var expectedContainerId = container.Id;
            var emptyLogCursor = DateTimeOffset.UtcNow.ToString("O");
            var result = await _client.ReadContainerLogsAsync(
                _connection,
                CreateLogRequest(container.Id),
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || _disposed
                || !string.Equals(SelectedResource?.Container?.Id, expectedContainerId, StringComparison.Ordinal))
            {
                return;
            }

            if (result is DockerResult<DockerContainerLogPage>.Failure failure)
            {
                LogIssueMessage = failure.Error.Message;
                return;
            }

            var page = ((DockerResult<DockerContainerLogPage>.Success)result).Value;
            _logRows.Clear();
            foreach (var line in page.Lines)
            {
                _logRows.Add(ToLogRow(line));
            }

            _loadedLogContainerId = expectedContainerId;
            _oldestLogTimestamp = page.OldestTimestamp;
            _newestLogTimestamp = page.NewestTimestamp ?? emptyLogCursor;
            HasOlderLogs = page.HasOlder;
            PublishLogRowsState();
            RequestLogScrollToEnd();
            StartFollowingLogs();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_logCancellation, cancellation))
            {
                _logCancellation = null;
            }

            cancellation.Dispose();
            IsLoadingLogs = false;
            _logGate.Release();
        }
    }

    private DockerContainerLogRequest CreateLogRequest(
        string containerId,
        string? beforeTimestamp = null,
        string? sinceTimestamp = null) => new(
        containerId,
        LogPageSize,
        beforeTimestamp,
        sinceTimestamp,
        _activeLogSearchText,
        LogSearchContext);

    private int PrependWithoutOverlap(IReadOnlyList<DockerContainerLogLine> lines)
    {
        var overlap = FindPrependOverlap(lines);
        var count = lines.Count - overlap;
        for (var index = count - 1; index >= 0; index--)
        {
            _logRows.Insert(0, ToLogRow(lines[index]));
        }

        return count;
    }

    private int FindPrependOverlap(IReadOnlyList<DockerContainerLogLine> lines)
    {
        var maximum = Math.Min(lines.Count, _logRows.Count);
        for (var length = maximum; length > 0; length--)
        {
            var matches = true;
            for (var index = 0; index < length; index++)
            {
                if (!SameLogLine(lines[lines.Count - length + index], _logRows[index]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return length;
            }
        }

        return 0;
    }

    private int AppendWithoutOverlap(IReadOnlyList<DockerContainerLogLine> lines)
    {
        var maximum = Math.Min(lines.Count, _logRows.Count);
        var overlap = 0;
        for (var length = maximum; length > 0; length--)
        {
            var matches = true;
            for (var index = 0; index < length; index++)
            {
                if (!SameLogLine(lines[index], _logRows[_logRows.Count - length + index]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                overlap = length;
                break;
            }
        }

        for (var index = overlap; index < lines.Count; index++)
        {
            _logRows.Add(ToLogRow(lines[index]));
        }

        return lines.Count - overlap;
    }

    private void StartFollowingLogs()
    {
        if (_disposed
            || !FollowLogs
            || IsLogSearchActive
            || !IsLogsDetail
            || _loadedLogContainerId is null
            || _logFollowCancellation is not null)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _logFollowCancellation = cancellation;
        _ = FollowLogsAsync(cancellation);
    }

    private async Task FollowLogsAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(LogFollowInterval, cancellation.Token);
                await LoadNewerLogsAsync(cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_logFollowCancellation, cancellation))
            {
                _logFollowCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task LoadNewerLogsAsync(CancellationToken cancellationToken)
    {
        if (_newestLogTimestamp is null
            || SelectedResource?.Container is not { } container
            || !await _logGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var expectedContainerId = container.Id;
            var result = await _client.ReadContainerLogsAsync(
                _connection,
                CreateLogRequest(container.Id, sinceTimestamp: _newestLogTimestamp),
                cancellationToken);
            if (!string.Equals(SelectedResource?.Container?.Id, expectedContainerId, StringComparison.Ordinal))
            {
                return;
            }

            if (result is DockerResult<DockerContainerLogPage>.Failure failure)
            {
                LogIssueMessage = failure.Error.Message;
                return;
            }

            var success = (DockerResult<DockerContainerLogPage>.Success)result;

            if (AppendWithoutOverlap(success.Value.Lines) == 0)
            {
                return;
            }

            LogIssueMessage = null;
            _newestLogTimestamp = success.Value.NewestTimestamp ?? _newestLogTimestamp;
            PublishLogRowsState();
            RequestLogScrollToEnd();
        }
        finally
        {
            _logGate.Release();
        }
    }

    private void ResetLogs()
    {
        CancelLogLoad();
        StopFollowingLogs();
        _logRows.Clear();
        _loadedLogContainerId = null;
        _oldestLogTimestamp = null;
        _newestLogTimestamp = null;
        _activeLogSearchText = null;
        LogSearchText = string.Empty;
        HasOlderLogs = false;
        LogIssueMessage = null;
        PublishLogRowsState();
        PublishLogModeState();
    }

    private void CancelLogLoad()
    {
        var cancellation = Interlocked.Exchange(ref _logCancellation, null);
        cancellation?.Cancel();
    }

    private void StopFollowingLogs()
    {
        var cancellation = Interlocked.Exchange(ref _logFollowCancellation, null);
        cancellation?.Cancel();
    }

    private void RequestLogScrollToEnd() =>
        LogScrollToEndRequest = unchecked(LogScrollToEndRequest + 1);

    private void PublishLogRowsState()
    {
        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(LogResultSummary));
    }

    private void PublishLogModeState()
    {
        OnPropertyChanged(nameof(IsLogSearchActive));
        OnPropertyChanged(nameof(CanFollowLogs));
        OnPropertyChanged(nameof(LogResultSummary));
    }

    private DockerLogRowViewModel ToLogRow(DockerContainerLogLine line) => new(
        line.Timestamp,
        line.Message,
        line.StartsContextBlock,
        BuildLogMessageSegments(line.Message, _activeLogSearchText));

    private static IReadOnlyList<DockerLogTextSegmentViewModel> BuildLogMessageSegments(
        string message,
        string? searchText)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return Array.AsReadOnly(
                [new DockerLogTextSegmentViewModel(message, false)]);
        }

        var segments = new List<DockerLogTextSegmentViewModel>();
        var position = 0;
        while (position < message.Length)
        {
            var match = message.IndexOf(
                searchText,
                position,
                StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                segments.Add(new DockerLogTextSegmentViewModel(message[position..], false));
                break;
            }

            if (match > position)
            {
                segments.Add(new DockerLogTextSegmentViewModel(
                    message[position..match],
                    false));
            }

            var matchEnd = match + searchText.Length;
            segments.Add(new DockerLogTextSegmentViewModel(message[match..matchEnd], true));
            position = matchEnd;
        }

        return segments.AsReadOnly();
    }

    private static bool SameLogLine(
        DockerContainerLogLine left,
        DockerLogRowViewModel right) =>
        string.Equals(left.Timestamp, right.Timestamp, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value.Select(character => invalid.Contains(character) ? '-' : character);
        return new string([.. characters]);
    }

    private CancellationTokenSource BeginDetailLoad()
    {
        var previous = Interlocked.Exchange(ref _detailCancellation, null);
        previous?.Cancel();
        previous?.Dispose();
        var current = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _detailCancellation = current;
        return current;
    }

    private void StartVolumeUsageLoad()
    {
        if (_disposed
            || _volumeUsageLoaded
            || _volumeUsageCancellation is not null
            || Snapshot is not { } snapshot)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _volumeUsageCancellation = cancellation;
        _volumeUsageLoading = LoadVolumeUsageAsync(snapshot, cancellation);
    }

    private async Task LoadVolumeUsageAsync(
        DockerEngineSnapshot expectedSnapshot,
        CancellationTokenSource cancellation)
    {
        IsCalculatingVolumeSizes = true;
        try
        {
            var result = await _client.ReadVolumeUsageAsync(
                _connection,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || _disposed
                || !ReferenceEquals(Snapshot, expectedSnapshot))
            {
                return;
            }

            _volumeUsageLoaded = true;
            if (result is not DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success success)
            {
                return;
            }

            var usageByName = success.Value
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var selected = SelectedResource?.Resource;
            Snapshot = expectedSnapshot with
            {
                Volumes = Array.AsReadOnly(expectedSnapshot.Volumes
                    .Select(volume => usageByName.TryGetValue(volume.Name, out var usage)
                        ? volume with { Size = usage.Size, SizeBytes = usage.SizeBytes }
                        : volume)
                    .ToArray()),
            };
            if (Section == DockerPanelSection.Volumes)
            {
                ProjectResources(Section);
                SelectedResource = selected is null
                    ? Resources.FirstOrDefault()
                    : Resources.FirstOrDefault(item =>
                        item.Resource.Kind == selected.Kind
                        && string.Equals(item.Resource.Id, selected.Id, StringComparison.Ordinal))
                        ?? Resources.FirstOrDefault();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_volumeUsageCancellation, cancellation))
            {
                _volumeUsageCancellation = null;
                IsCalculatingVolumeSizes = false;
            }

            cancellation.Dispose();
        }
    }

    private void CancelVolumeUsageLoad()
    {
        var cancellation = Interlocked.Exchange(ref _volumeUsageCancellation, null);
        cancellation?.Cancel();
        IsCalculatingVolumeSizes = false;
    }

    private void ProjectResources(DockerPanelSection section)
    {
        if (Snapshot is not { } snapshot)
        {
            ContainerStacks = [];
            Resources = [];
            return;
        }

        if (section == DockerPanelSection.Containers)
        {
            ContainerStacks = Array.AsReadOnly(snapshot.Containers
                .GroupBy(container => container.StackName, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var containers = Array.AsReadOnly(group
                        .OrderByDescending(container => container.IsRunning)
                        .ThenByDescending(container => container.IsPaused)
                        .ThenBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(DockerResourceItemViewModel.From)
                        .ToArray());
                    return new DockerContainerStackViewModel(
                        group.Key,
                        containers,
                        containers.Count(container => container.IsRunning),
                        group.All(container => container.IsStandalone));
                })
                .OrderBy(stack => stack.IsStandalone)
                .ThenByDescending(stack => stack.HasRunningContainers)
                .ThenBy(stack => stack.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
            Resources = Array.AsReadOnly(ContainerStacks
                .SelectMany(stack => stack.Containers)
                .ToArray());
            return;
        }

        ContainerStacks = [];
        Resources = ResourcesFor(section, snapshot);
    }

    private static IReadOnlyList<DockerResourceItemViewModel> ResourcesFor(
        DockerPanelSection section,
        DockerEngineSnapshot snapshot)
    {

        return section switch
        {
            DockerPanelSection.Containers => throw new ArgumentOutOfRangeException(
                nameof(section), section, "Containers are projected by stack."),
            DockerPanelSection.Images => Array.AsReadOnly(
                snapshot.Images.Select(DockerResourceItemViewModel.From).ToArray()),
            DockerPanelSection.Volumes => Array.AsReadOnly(
                snapshot.Volumes
                    .OrderByDescending(volume => volume.SizeBytes.HasValue)
                    .ThenByDescending(volume => volume.SizeBytes)
                    .ThenBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(DockerResourceItemViewModel.From)
                    .ToArray()),
            DockerPanelSection.Networks => Array.AsReadOnly(
                snapshot.Networks.Select(DockerResourceItemViewModel.From).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
        };
    }

    private void ResetShellState()
    {
        ShellStateTitle = "Opening container shell…";
        ShellStateMessage =
            "This shell stays in the Docker panel. Use New tab when you want it beside other work.";
        IsResolvingShell = false;
    }

    private FileRuntimePanelViewModel GetOrCreateFileBrowser(
        DockerResourceReference resource)
    {
        var key = $"{resource.Kind}:{resource.Id}";
        if (_fileBrowsers.TryGetValue(key, out var browser))
        {
            return browser;
        }

        browser = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            $"{resource.DisplayName} files",
            new DockerFilePanelClient(_client, _connection, resource),
            deferInitialization: true,
            connection: _connection);
        _fileBrowsers.Add(key, browser);
        return browser;
    }

    private bool IsDetailAvailable(DockerPanelDetail detail) => detail switch
    {
        DockerPanelDetail.Info => HasSelection,
        DockerPanelDetail.Logs or DockerPanelDetail.Stats => ShowLogsTab,
        DockerPanelDetail.Shell => ShowShellTab,
        DockerPanelDetail.Files => ShowFilesTab,
        DockerPanelDetail.Json => ShowJsonTab,
        _ => false,
    };

    private void PresentFailure(DockerError error, string title)
    {
        IssueTitle = title;
        IssueMessage = error.Message;
        ShowsDockerInstallHelp = error.Code == DockerErrorCode.RuntimeUnavailable;
        StatusText = error.Retryable ? "Retry available" : "Unavailable";
    }

    private void PublishSectionState()
    {
        OnPropertyChanged(nameof(IsContainersSection));
        OnPropertyChanged(nameof(IsImagesSection));
        OnPropertyChanged(nameof(IsVolumesSection));
        OnPropertyChanged(nameof(IsNetworksSection));
        OnPropertyChanged(nameof(ResourceSummary));
        OnPropertyChanged(nameof(ShowVolumeSizeLoading));
        OnPropertyChanged(nameof(ShowResourceProgress));
    }

    private void PublishDetailState()
    {
        OnPropertyChanged(nameof(IsInfoDetail));
        OnPropertyChanged(nameof(IsLogsDetail));
        OnPropertyChanged(nameof(IsStatsDetail));
        OnPropertyChanged(nameof(IsShellDetail));
        OnPropertyChanged(nameof(IsFilesDetail));
        OnPropertyChanged(nameof(IsJsonDetail));
    }

    private void PublishSelectionState()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanOpenShell));
        OnPropertyChanged(nameof(CanBrowseFiles));
        OnPropertyChanged(nameof(ShowLogsTab));
        OnPropertyChanged(nameof(ShowStatsTab));
        OnPropertyChanged(nameof(ShowShellTab));
        OnPropertyChanged(nameof(ShowFilesTab));
        OnPropertyChanged(nameof(ShowJsonTab));
        OnPropertyChanged(nameof(ShowEmptyState));
        PublishContainerActionState();
        RaiseContainerActionCanExecuteChanged();
    }

    private void PublishContainerActionState()
    {
        OnPropertyChanged(nameof(CanStartSelectedContainer));
        OnPropertyChanged(nameof(CanStopSelectedContainer));
        OnPropertyChanged(nameof(CanRestartSelectedContainer));
        OnPropertyChanged(nameof(CanPauseSelectedContainer));
        OnPropertyChanged(nameof(CanResumeSelectedContainer));
        OnPropertyChanged(nameof(SelectedContainerIsStopped));
        OnPropertyChanged(nameof(SelectedContainerIsActive));
    }

    private void RaiseContainerActionCanExecuteChanged()
    {
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _restartCommand.RaiseCanExecuteChanged();
        _pauseCommand.RaiseCanExecuteChanged();
        _resumeCommand.RaiseCanExecuteChanged();
    }

    private void PublishSnapshotState()
    {
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ContainerCount));
        OnPropertyChanged(nameof(RunningContainerCount));
        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(VolumeCount));
        OnPropertyChanged(nameof(NetworkCount));
        OnPropertyChanged(nameof(EngineVersion));
        OnPropertyChanged(nameof(EnginePlatform));
    }

    private static string ActionVerb(DockerContainerAction action) => action switch
    {
        DockerContainerAction.Start => "start",
        DockerContainerAction.Stop => "stop",
        DockerContainerAction.Restart => "restart",
        DockerContainerAction.Pause => "pause",
        DockerContainerAction.Resume => "resume",
        _ => "update",
    };

    private static bool CanApplyStackAction(
        DockerResourceItemViewModel container,
        DockerContainerAction action) => action switch
        {
            DockerContainerAction.Start => !container.IsRunning && !container.IsPaused,
            DockerContainerAction.Stop => container.IsRunning || container.IsPaused,
            DockerContainerAction.Restart => container.IsRunning || container.IsPaused,
            DockerContainerAction.Pause => container.IsRunning && !container.IsPaused,
            DockerContainerAction.Resume => container.IsPaused,
            _ => false,
        };

    private static string ActionPastTense(DockerContainerAction action) => action switch
    {
        DockerContainerAction.Start => "started",
        DockerContainerAction.Stop => "stopped",
        DockerContainerAction.Restart => "restarted",
        DockerContainerAction.Pause => "paused",
        DockerContainerAction.Resume => "resumed",
        _ => "updated",
    };
}
