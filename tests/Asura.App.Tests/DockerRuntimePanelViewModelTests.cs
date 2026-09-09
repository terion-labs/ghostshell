using Asura.App.ViewModels;
using Asura.Core;
using Asura.Docker;
using Asura.Testing;
using FluentIcons.Common;

namespace Asura.App.Tests;

public sealed class DockerRuntimePanelViewModelTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public async Task InitializationSelectsTheFirstContainerAndLoadsInspection()
    {
        var client = new FakeDockerEngineClient(Snapshot());
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.Equal(PanelKind.Docker, panel.Kind);
        Assert.Equal("api", panel.SelectedResource?.Title);
        Assert.Equal(3, panel.ContainerCount);
        Assert.Equal(1, panel.RunningContainerCount);
        Assert.True(panel.CanOpenShell);
        Assert.NotNull(panel.Inspection);
        Assert.Equal("1 running · Docker 28.3.0", panel.StatusText);
    }

    [Fact]
    public async Task RunningContainerActionsAreReevaluatedAfterInitialRefreshCompletes()
    {
        var client = new FakeDockerEngineClient(Snapshot());
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.False(panel.StartCommand.CanExecute(null));
        Assert.True(panel.StopCommand.CanExecute(null));
        Assert.True(panel.RestartCommand.CanExecute(null));
        Assert.True(panel.PauseCommand.CanExecute(null));
        Assert.False(panel.ResumeCommand.CanExecute(null));
        Assert.False(panel.CanStartSelectedContainer);
        Assert.True(panel.CanStopSelectedContainer);
        Assert.True(panel.CanRestartSelectedContainer);
        Assert.True(panel.CanPauseSelectedContainer);
        Assert.False(panel.CanResumeSelectedContainer);

        panel.SelectResource(Assert.Single(panel.Resources, item => string.Equals(item.Title, "worker", StringComparison.Ordinal)));

        Assert.True(panel.StartCommand.CanExecute(null));
        Assert.False(panel.StopCommand.CanExecute(null));
        Assert.True(panel.SelectedContainerIsStopped);
        Assert.False(panel.SelectedContainerIsActive);
        Assert.True(panel.CanStartSelectedContainer);
        Assert.False(panel.CanStopSelectedContainer);
    }

    [Fact]
    public async Task PausedContainerUsesStopAndResumeLifecycleStates()
    {
        var snapshot = Snapshot() with
        {
            Containers = [Container("paused", "paused-api", "paused")],
        };
        var client = new FakeDockerEngineClient(snapshot);
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.False(panel.StartCommand.CanExecute(null));
        Assert.True(panel.StopCommand.CanExecute(null));
        Assert.True(panel.RestartCommand.CanExecute(null));
        Assert.False(panel.PauseCommand.CanExecute(null));
        Assert.True(panel.ResumeCommand.CanExecute(null));
        Assert.False(panel.SelectedContainerIsStopped);
        Assert.True(panel.SelectedContainerIsActive);
        Assert.False(panel.CanStartSelectedContainer);
        Assert.True(panel.CanStopSelectedContainer);
    }

    [Fact]
    public async Task LoadingAndEmptyStatesAreMutuallyExclusive()
    {
        var snapshotCompletion = new TaskCompletionSource<DockerEngineSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDockerEngineClient(Snapshot())
        {
            SnapshotCompletion = snapshotCompletion,
        };
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        Assert.True(panel.ShowLoading);
        Assert.False(panel.ShowEmptyState);
        Assert.True(panel.ShowResourceProgress);

        snapshotCompletion.SetResult(Snapshot() with { Containers = [] });
        await panel.Initialization;

        Assert.False(panel.ShowLoading);
        Assert.True(panel.ShowEmptyState);
        Assert.False(panel.ShowResourceProgress);
    }

    [Fact]
    public async Task ContainersAreGroupedByComposeStackWithRunningStacksAndContainersFirst()
    {
        var client = new FakeDockerEngineClient(Snapshot());
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.Equal(["zeta", "alpha"], panel.ContainerStacks.Select(stack => stack.Name), StringComparer.Ordinal);
        Assert.Equal(["api", "web"], panel.ContainerStacks[0].Containers.Select(item => item.Title), StringComparer.Ordinal);
        Assert.Equal("1/2 running", panel.ContainerStacks[0].Summary);
        Assert.Equal("1 stopped", panel.ContainerStacks[1].Summary);
        Assert.Equal("api", panel.Resources[0].Title);
    }

    [Fact]
    public async Task StandaloneContainersAreVisibleAfterComposeStacks()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Containers =
            [
                .. snapshot.Containers,
                Container("standalone-running", "toolbox", "running"),
                Container("standalone-stopped", "old-toolbox", "exited"),
            ],
        };
        var client = new FakeDockerEngineClient(snapshot);
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.Equal(
            ["zeta", "alpha", "Standalone containers"],
            panel.ContainerStacks.Select(stack => stack.Name), StringComparer.Ordinal);
        var standalone = panel.ContainerStacks[^1];
        Assert.True(standalone.IsStandalone);
        Assert.Equal("Standalone containers", standalone.Name);
        Assert.Equal(["toolbox", "old-toolbox"], standalone.Containers.Select(item => item.Title), StringComparer.Ordinal);
        Assert.Equal("1/2 running", standalone.Summary);
        Assert.Equal(5, panel.Resources.Count);
    }

    [Theory]
    [InlineData(DockerContainerAction.Start, "web")]
    [InlineData(DockerContainerAction.Stop, "api,cache")]
    [InlineData(DockerContainerAction.Restart, "api,cache")]
    [InlineData(DockerContainerAction.Pause, "api")]
    [InlineData(DockerContainerAction.Resume, "cache")]
    public async Task StackActionsApplyToEveryEligibleContainerAndRefreshOnce(
        DockerContainerAction action,
        string expectedContainerNames)
    {
        var snapshot = Snapshot() with
        {
            Containers =
            [
                .. Snapshot().Containers,
                new DockerContainerSummary(
                    "paused",
                    "cache",
                    "redis:8-alpine",
                    "paused",
                    "Up 2 hours (Paused)",
                    string.Empty,
                    "2 hours ago",
                    "—",
                    "—",
                    "—",
                    "—",
                    "zeta",
                    "cache"),
            ],
        };
        var client = new FakeDockerEngineClient(snapshot);
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;

        var stack = Assert.Single(panel.ContainerStacks, item => string.Equals(item.Name, "zeta", StringComparison.Ordinal));
        await panel.RunStackActionAsync(stack, action);

        var namesById = snapshot.Containers.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        Assert.Equal(
            expectedContainerNames.Split(','),
            client.ContainerActions.Select(call => namesById[call.ContainerId]), StringComparer.Ordinal);
        Assert.All(client.ContainerActions, call => Assert.Equal(action, call.Action));
        Assert.Equal(2, client.SnapshotReadCount);
    }

    [Fact]
    public async Task ShellDetailIsAvailableOnlyForARunningContainer()
    {
        var client = new FakeDockerEngineClient(Snapshot());
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;

        panel.SelectDetail(DockerPanelDetail.Shell);
        Assert.True(panel.IsShellDetail);

        panel.SelectResource(Assert.Single(panel.Resources, item => string.Equals(item.Title, "worker", StringComparison.Ordinal)));

        Assert.True(panel.IsInfoDetail);
        Assert.False(panel.CanOpenShell);
    }

    [Fact]
    public async Task SwitchingSectionReprojectsResourcesAndDisablesContainerDetails()
    {
        var client = new FakeDockerEngineClient(Snapshot());
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;

        panel.SelectSection(DockerPanelSection.Images);
        panel.SelectDetail(DockerPanelDetail.Logs);

        Assert.True(panel.IsImagesSection);
        Assert.Single(panel.Resources);
        Assert.Equal("demo/api:latest", panel.SelectedResource?.Title);
        Assert.Equal(Symbol.Archive, panel.SelectedResource?.Icon);
        Assert.False(panel.CanOpenShell);
        Assert.True(panel.IsInfoDetail);
    }

    [Fact]
    public async Task DetailTabsFollowTheSelectedResourceCapabilities()
    {
        var client = new FakeDockerEngineClient(Snapshot());
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;

        Assert.True(panel.ShowLogsTab);
        Assert.True(panel.ShowStatsTab);
        Assert.True(panel.ShowShellTab);
        Assert.True(panel.ShowFilesTab);
        Assert.True(panel.ShowJsonTab);

        panel.SelectSection(DockerPanelSection.Images);

        Assert.False(panel.ShowLogsTab);
        Assert.False(panel.ShowStatsTab);
        Assert.False(panel.ShowShellTab);
        Assert.True(panel.ShowFilesTab);
        Assert.True(panel.ShowJsonTab);
        panel.SelectDetail(DockerPanelDetail.Files);
        Assert.True(panel.IsFilesDetail);

        panel.SelectSection(DockerPanelSection.Volumes);
        Assert.True(panel.ShowFilesTab);
        panel.SelectDetail(DockerPanelDetail.Files);
        Assert.True(panel.IsFilesDetail);

        panel.SelectSection(DockerPanelSection.Networks);
        Assert.False(panel.ShowFilesTab);
        panel.SelectDetail(DockerPanelDetail.Files);
        Assert.True(panel.IsInfoDetail);
        Assert.True(panel.ShowJsonTab);
    }

    [Fact]
    public async Task LogSearchSendsQueryAndContextToEngineInsteadOfFilteringVisibleRows()
    {
        var client = new FakeDockerEngineClient(Snapshot())
        {
            LogPageFactory = request => new DockerContainerLogPage(
                [new DockerContainerLogLine("2026-08-10T12:00:00Z", request.SearchText ?? "latest")],
                false,
                "2026-08-10T12:00:00Z",
                "2026-08-10T12:00:00Z"),
        };
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;
        panel.LogSearchText = "database unavailable";
        panel.LogSearchContext = 4;

        await panel.SearchLogsAsync();

        var request = Assert.Single(client.LogRequests);
        Assert.Equal("database unavailable", request.SearchText);
        Assert.Equal(4, request.ContextLines);
        Assert.True(panel.IsLogSearchActive);
        Assert.False(panel.CanFollowLogs);
        Assert.Equal("database unavailable", Assert.Single(panel.LogRows).Message);
    }

    [Fact]
    public async Task LogSearchHighlightsEveryCaseInsensitiveMatchWithoutChangingTheMessage()
    {
        var client = new FakeDockerEngineClient(Snapshot())
        {
            LogPageFactory = _ => new DockerContainerLogPage(
                [new DockerContainerLogLine(
                    "2026-08-10T12:00:00Z",
                    "Completed cleanup; completed archive")],
                false,
                "2026-08-10T12:00:00Z",
                "2026-08-10T12:00:00Z"),
        };
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;
        panel.LogSearchText = "completed";

        await panel.SearchLogsAsync();

        var row = Assert.Single(panel.LogRows);
        Assert.Equal("Completed cleanup; completed archive", row.Message);
        Assert.Equal(
            ["Completed", " cleanup; ", "completed", " archive"],
            row.MessageSegments.Select(segment => segment.Text), StringComparer.Ordinal);
        Assert.Equal(
            [true, false, true, false],
            row.MessageSegments.Select(segment => segment.IsMatch));
    }

    [Fact]
    public void LogSearchContextUsesABoundedNumericEditor()
    {
        var input = ApplicationViews
            .FindUniqueNamedElement("LogSearchContextInput")
            .Element;

        Assert.Equal("NumericUpDown", input.Name.LocalName);
        Assert.Equal("0", input.Attribute("Minimum")?.Value);
        Assert.Equal("100", input.Attribute("Maximum")?.Value);
        Assert.Equal("1", input.Attribute("Increment")?.Value);
        Assert.Equal("False", input.Attribute("ShowButtonSpinner")?.Value);
        Assert.Equal(
            "{Binding LogSearchContextInput, Mode=TwoWay}",
            input.Attribute("Value")?.Value);
    }

    [Fact]
    public void LogSearchClearActionIsComposedInsideTheSearchField()
    {
        var search = ApplicationViews
            .FindUniqueNamedElement("LogSearchBox")
            .Element;
        var clear = ApplicationViews
            .FindUniqueNamedElement("LogSearchClearButton")
            .Element;

        Assert.Equal("Grid", search.Parent?.Name.LocalName);
        Assert.Same(search.Parent, clear.Parent);
        Assert.Equal("Right", clear.Attribute("HorizontalAlignment")?.Value);
        Assert.Contains("Right=Xxl", search.Attribute("Padding")?.Value);
    }

    [Fact]
    public async Task LogSearchContextEditorNormalizesEmptyFractionalAndOutOfRangeValues()
    {
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            new FakeDockerEngineClient(Snapshot()),
            BuiltInConnections.Local);
        await panel.Initialization;

        panel.LogSearchContextInput = 12.9m;
        Assert.Equal(12, panel.LogSearchContext);

        panel.LogSearchContextInput = 101m;
        Assert.Equal(100, panel.LogSearchContext);

        panel.LogSearchContextInput = null;
        Assert.Equal(0, panel.LogSearchContext);
    }

    [Fact]
    public async Task OlderLogPageIsPrependedWithoutRepeatingCursorOverlap()
    {
        var client = new FakeDockerEngineClient(Snapshot())
        {
            LogPageFactory = request => request.BeforeTimestamp is null
                ? new DockerContainerLogPage(
                    [
                        new DockerContainerLogLine("2026-08-10T12:00:01Z", "middle"),
                        new DockerContainerLogLine("2026-08-10T12:00:02Z", "newest"),
                    ],
                    true,
                    "2026-08-10T12:00:01Z",
                    "2026-08-10T12:00:02Z")
                : new DockerContainerLogPage(
                    [
                        new DockerContainerLogLine("2026-08-10T12:00:00Z", "oldest"),
                        new DockerContainerLogLine("2026-08-10T12:00:01Z", "middle"),
                    ],
                    false,
                    "2026-08-10T12:00:00Z",
                    "2026-08-10T12:00:01Z"),
        };
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;
        panel.LogSearchText = "anything";
        await panel.SearchLogsAsync();

        Assert.True(await panel.LoadOlderLogsAsync());

        Assert.Equal(["oldest", "middle", "newest"], panel.LogRows.Select(row => row.Message), StringComparer.Ordinal);
        Assert.False(panel.HasOlderLogs);
        Assert.NotNull(client.LogRequests[1].BeforeTimestamp);
    }

    [Fact]
    public async Task VolumesAreSortedByLoadedSizeDescendingWithUnknownSizesLast()
    {
        var snapshot = Snapshot() with
        {
            Volumes =
            [
                new DockerVolumeSummary("unknown", "local", "local", "/unknown"),
                new DockerVolumeSummary("small", "local", "local", "/small"),
                new DockerVolumeSummary("large", "local", "local", "/large"),
                new DockerVolumeSummary("also-large", "local", "local", "/also-large"),
            ],
        };
        var client = new FakeDockerEngineClient(snapshot)
        {
            VolumeUsage =
            [
                new DockerVolumeUsage("small", "10 MB", 10_000_000),
                new DockerVolumeUsage("large", "2 GB", 2_000_000_000),
                new DockerVolumeUsage("also-large", "2 GB", 2_000_000_000),
                new DockerVolumeUsage("unknown", "—", null),
            ],
        };
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;

        panel.SelectSection(DockerPanelSection.Volumes);
        await panel.VolumeUsageLoading;

        Assert.Equal(
            ["also-large", "large", "small", "unknown"],
            panel.Resources.Select(item => item.Title), StringComparer.Ordinal);
        Assert.Equal(["2 GB", "2 GB", "10 MB", "—"], panel.Resources.Select(item => item.Subtitle), StringComparer.Ordinal);
        Assert.Equal(1, client.VolumeUsageReadCount);
        Assert.Equal("4 resources", panel.ResourceSummary);
    }

    [Fact]
    public async Task VolumeSectionExposesLoadingStateUntilSizeUsageFinishes()
    {
        var volumeUsage = new TaskCompletionSource<IReadOnlyList<DockerVolumeUsage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeDockerEngineClient(Snapshot())
        {
            VolumeUsageCompletion = volumeUsage,
        };
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);
        await panel.Initialization;

        panel.SelectSection(DockerPanelSection.Volumes);

        Assert.True(panel.ShowVolumeSizeLoading);
        Assert.True(panel.ShowResourceProgress);
        Assert.Equal("1 resources · Loading sizes…", panel.ResourceSummary);

        panel.SelectSection(DockerPanelSection.Images);
        Assert.False(panel.ShowResourceProgress);
        panel.SelectSection(DockerPanelSection.Volumes);
        Assert.True(panel.ShowResourceProgress);

        volumeUsage.SetResult([new DockerVolumeUsage("app-data", "64 MB", 64_000_000)]);
        await panel.VolumeUsageLoading;

        Assert.False(panel.ShowVolumeSizeLoading);
        Assert.False(panel.ShowResourceProgress);
        Assert.Equal("1 resources", panel.ResourceSummary);
        Assert.Equal("64 MB", Assert.Single(panel.Resources).Subtitle);
    }

    [Fact]
    public async Task EngineFailurePresentsRetryableIssue()
    {
        var client = new FakeDockerEngineClient(new DockerError(
            DockerErrorCode.ConnectionFailed,
            "SSH target is offline.",
            true));
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.True(panel.HasIssue);
        Assert.Equal("Docker unavailable", panel.IssueTitle);
        Assert.Equal("SSH target is offline.", panel.IssueMessage);
        Assert.Equal("Retry available", panel.StatusText);
        Assert.False(panel.ShowsDockerInstallHelp);
    }

    [Fact]
    public async Task RuntimeUnavailableFailureOffersDockerInstallationHelp()
    {
        var client = new FakeDockerEngineClient(new DockerError(
            DockerErrorCode.RuntimeUnavailable,
            "Docker is not installed in this environment, or its daemon is not running.",
            true));
        using var panel = new DockerRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker",
            client,
            BuiltInConnections.Local);

        await panel.Initialization;

        Assert.True(panel.ShowsDockerInstallHelp);
    }

    private static DockerEngineSnapshot Snapshot() => new(
        new DockerEngineSummary("28.3.0", "linux", "arm64", "1.51"),
        [
            new DockerContainerSummary(
                "abc",
                "api",
                "demo/api:latest",
                "running",
                "Up 2 hours",
                "8080/tcp",
                "2 hours ago",
                "2%",
                "128 MiB",
                "1 MB / 2 MB",
                "3 MB / 4 MB",
                "zeta",
                "api"),
            new DockerContainerSummary(
                "ghi",
                "web",
                "demo/web:latest",
                "exited",
                "Exited (0)",
                string.Empty,
                "3 hours ago",
                "—",
                "—",
                "—",
                "—",
                "zeta",
                "web"),
            new DockerContainerSummary(
                "def",
                "worker",
                "demo/worker:latest",
                "exited",
                "Exited (0)",
                string.Empty,
                "1 day ago",
                "—",
                "—",
                "—",
                "—",
                "alpha",
                "worker"),
        ],
        [new DockerImageSummary("image", "demo/api", "latest", "184 MB", "2 days ago")],
        [new DockerVolumeSummary("app-data", "local", "local", "/data")],
        [new DockerNetworkSummary("network", "app", "bridge", "local", "today")],
        DateTimeOffset.UtcNow);

    private static DockerContainerSummary Container(
        string id,
        string name,
        string state) => new(
        id,
        name,
        "demo/toolbox:latest",
        state,
string.Equals(state, "running", StringComparison.Ordinal) ? "Up 2 hours" : "Exited (0)",
        string.Empty,
        "2 hours ago",
        "—",
        "—",
        "—",
        "—");

    private sealed class FakeDockerEngineClient : IDockerEngineClient
    {
        private readonly DockerEngineSnapshot? _snapshot;
        private readonly DockerError? _error;

        public IReadOnlyList<DockerVolumeUsage> VolumeUsage { get; init; } = [];

        public TaskCompletionSource<IReadOnlyList<DockerVolumeUsage>>? VolumeUsageCompletion
        {
            get;
            init;
        }

        public TaskCompletionSource<DockerEngineSnapshot>? SnapshotCompletion
        {
            get;
            init;
        }

        public int VolumeUsageReadCount { get; private set; }

        public int SnapshotReadCount { get; private set; }

        public List<(string ContainerId, DockerContainerAction Action)> ContainerActions { get; } = [];

        public List<DockerContainerLogRequest> LogRequests { get; } = [];

        public Func<DockerContainerLogRequest, DockerContainerLogPage> LogPageFactory { get; init; } =
            _ => new DockerContainerLogPage(
                [new DockerContainerLogLine("2026-08-10T12:00:00Z", "ready")],
                false,
                "2026-08-10T12:00:00Z",
                "2026-08-10T12:00:00Z");

        public FakeDockerEngineClient(DockerEngineSnapshot snapshot) => _snapshot = snapshot;

        public FakeDockerEngineClient(DockerError error) => _error = error;

        public async ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            SnapshotReadCount++;
            if (SnapshotCompletion is not null)
            {
                var completedSnapshot = await SnapshotCompletion.Task.WaitAsync(cancellationToken);
                return new DockerResult<DockerEngineSnapshot>.Success(completedSnapshot);
            }

            return _snapshot is { } snapshot
                ? new DockerResult<DockerEngineSnapshot>.Success(snapshot)
                : new DockerResult<DockerEngineSnapshot>.Failure(_error!);
        }

        public async ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            VolumeUsageReadCount++;
            var usage = VolumeUsageCompletion is null
                ? VolumeUsage
                : await VolumeUsageCompletion.Task.WaitAsync(cancellationToken);
            return new DockerResult<IReadOnlyList<DockerVolumeUsage>>.Success(usage);
        }

        public ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<DockerResourceInspection>>(
                new DockerResult<DockerResourceInspection>.Success(
                    new DockerResourceInspection(
                        resource,
                        [new DockerInspectionProperty("Id", resource.Id)],
                        "{}")));

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
            ConnectionProfile connection,
            DockerContainerLogRequest request,
            CancellationToken cancellationToken)
        {
            LogRequests.Add(request);
            return ValueTask.FromResult<DockerResult<DockerContainerLogPage>>(
                new DockerResult<DockerContainerLogPage>.Success(LogPageFactory(request)));
        }

        public ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
            ConnectionProfile connection,
            string containerId,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<bool>>(new DockerResult<bool>.Success(true));

        public ValueTask<DockerResult<string>> ResolveContainerShellAsync(
            ConnectionProfile connection,
            string containerId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<string>>(
                new DockerResult<string>.Success("/bin/sh"));

        public ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DockerResult<DockerFileListing>>(
                new DockerResult<DockerFileListing>.Success(
                    new DockerFileListing(resource, path, [])));

        public ValueTask<DockerResult<bool>> RunContainerActionAsync(
            ConnectionProfile connection,
            string containerId,
            DockerContainerAction action,
            CancellationToken cancellationToken)
        {
            ContainerActions.Add((containerId, action));
            return ValueTask.FromResult<DockerResult<bool>>(
                new DockerResult<bool>.Success(true));
        }
    }
}
