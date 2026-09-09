using Asura.App.ViewModels;
using Asura.App.Views.RuntimePanels;
using Asura.Core;
using Asura.Docker;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using DockerListItem = Asura.App.Controls.ListItem;
using IdentityTile = Asura.App.Controls.IdentityTile;

namespace Asura.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class DockerRuntimePanelViewHeadlessTests
{
    [Fact]
    public Task DockerResourceListsFillTheirAvailableWidth() =>
        RunHeadlessAsync(async () =>
        {
            var client = new PagedLogDockerClient();
            using var panel = new DockerRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Docker",
                client,
                BuiltInConnections.Local);
            await panel.Initialization;

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return view.GetVisualDescendants()
                        .OfType<Button>()
                        .Any(button => button.Classes.Contains("DockerResourceRow"));
                });

                var familyNavigation = view.FindControl<StackPanel>("DockerFamilyNavigation")!;
                var familyButtons = familyNavigation.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => button.Classes.Contains("DockerNav"))
                    .ToArray();
                Assert.Equal(4, familyButtons.Length);
                Assert.All(
                    familyButtons,
                    button => Assert.InRange(
                        familyNavigation.Bounds.Width - button.Bounds.Width,
                        0,
                        2));

                var stackList = view.FindControl<ItemsControl>("ContainerStackList")!;
                var containerResourceHost = view.FindControl<ScrollViewer>(
                    "ContainerResourceHost")!;
                var flatResourceHost = view.FindControl<ListBox>("FlatResourceHost")!;
                Assert.Equal(
                    Colors.Transparent,
                    Assert.IsAssignableFrom<ISolidColorBrush>(containerResourceHost.Background).Color);
                Assert.Equal(
                    Colors.Transparent,
                    Assert.IsAssignableFrom<ISolidColorBrush>(flatResourceHost.Background).Color);
                Assert.Equal(default, containerResourceHost.BorderThickness);
                Assert.Equal(default, flatResourceHost.BorderThickness);
                Assert.Equal(default, containerResourceHost.Padding);
                Assert.Equal(default, flatResourceHost.Padding);
                var stack = Assert.Single(
                    stackList.GetVisualDescendants().OfType<Grid>(),
                    grid => grid.Classes.Contains("DockerStack"));
                var row = Assert.Single(
                    stack.GetVisualDescendants().OfType<Button>(),
                    button => button.Classes.Contains("DockerResourceRow"));
                var containerList = Assert.IsType<ItemsControl>(
                    row.GetVisualAncestors().OfType<ItemsControl>().First());
                var stackContainer = Assert.Single(
                    stack.GetVisualAncestors().OfType<ContentPresenter>(),
                    presenter => ReferenceEquals(presenter.DataContext, stack.DataContext));
                var rowContainer = Assert.Single(
                    row.GetVisualAncestors().OfType<ContentPresenter>(),
                    presenter => ReferenceEquals(presenter.DataContext, row.DataContext));

                Assert.InRange(
                    stackContainer.Bounds.Width,
                    stackList.Bounds.Width - 1,
                    stackList.Bounds.Width + 1);
                Assert.InRange(
                    rowContainer.Bounds.Width,
                    containerList.Bounds.Width - 1,
                    containerList.Bounds.Width + 1);
                Assert.InRange(
                    rowContainer.Bounds.Width - row.Bounds.Width,
                    1,
                    16);
                var rowRightInStack = row.TranslatePoint(
                    new Point(row.Bounds.Width, 0),
                    stack);
                Assert.NotNull(rowRightInStack);
                Assert.InRange(
                    stack.Bounds.Width - rowRightInStack.Value.X,
                    1,
                    16);
                var rowContent = Assert.Single(
                    row.GetVisualDescendants().OfType<DockerListItem>(),
                    item => item.Classes.Contains("DockerResourceListItem"));
                // Read while attached: the row inset comes from a dynamic
                // resource now, and a detached element resolves it to zero.
                var rowContentPadding = rowContent.ContentPadding;
                var iconTile = Assert.Single(
                    row.GetVisualDescendants().OfType<IdentityTile>());
                var title = Assert.Single(
                    rowContent.GetVisualDescendants().OfType<TextBlock>(),
                    text => string.Equals(text.Name, "PART_Title", StringComparison.Ordinal));
                var titleOrigin = title.TranslatePoint(default, rowContent);
                var iconOrigin = iconTile.TranslatePoint(default, rowContent);
                Assert.NotNull(titleOrigin);
                Assert.NotNull(iconOrigin);
                Assert.InRange(
                    iconOrigin.Value.Y - titleOrigin.Value.Y,
                    2,
                    8);

                var stackToggle = Assert.Single(
                    stack.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Expand or collapse stack", StringComparison.Ordinal));
                var stackViewModel = Assert.IsType<DockerContainerStackViewModel>(
                    stackToggle.DataContext);
                stackToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.False(stackViewModel.IsExpanded);
                Assert.False(containerList.IsVisible);
                stackToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.True(stackViewModel.IsExpanded);
                Assert.True(containerList.IsVisible);

                var stopStack = Assert.Single(
                    stack.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button), "Stop stack", StringComparison.Ordinal));
                var stopStackCenter = stopStack.TranslatePoint(
                    new Point(stopStack.Bounds.Width / 2, stopStack.Bounds.Height / 2),
                    window);
                Assert.NotNull(stopStackCenter);
                window.MouseDown(stopStackCenter.Value, MouseButton.Left);
                window.MouseUp(stopStackCenter.Value, MouseButton.Left);
                await WaitUntilAsync(() =>
                    client.ContainerActions.Count == 1 && !panel.IsRefreshing);
                Assert.Equal(
                    ("container", DockerContainerAction.Stop),
                    client.ContainerActions[0]);

                var containerActions = Assert.Single(
                    view.GetVisualDescendants().OfType<StackPanel>(),
                    candidate => candidate.Classes.Contains("DockerContainerActions"));
                var actionButtons = containerActions.Children.OfType<Button>().ToArray();
                Assert.Equal(2, actionButtons.Length);
                Assert.All(
                    actionButtons,
                    button => Assert.InRange(
                        Math.Abs(button.Bounds.Height - actionButtons[^1].Bounds.Height),
                        0,
                        1));
                panel.SelectSection(DockerPanelSection.Volumes);
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return view.GetVisualDescendants()
                        .OfType<ListBoxItem>()
                        .Any(item => item.IsVisible);
                });
                var resourceList = Assert.Single(
                    view.GetVisualDescendants().OfType<ListBox>(),
                    list => list.Classes.Contains("DockerResources"));
                var resourceItem = Assert.Single(
                    resourceList.GetVisualDescendants().OfType<ListBoxItem>());
                Assert.InRange(
                    resourceList.Bounds.Width - resourceItem.Bounds.Width,
                    1,
                    16);
                var flatRowContent = Assert.Single(
                    resourceItem.GetVisualDescendants().OfType<DockerListItem>(),
                    item => item.Classes.Contains("DockerResourceListItem"));
                var flatIconTile = Assert.Single(
                    resourceItem.GetVisualDescendants().OfType<IdentityTile>());
                Assert.Equal(rowContentPadding, flatRowContent.ContentPadding);
                Assert.InRange(
                    Math.Abs(rowContent.Bounds.Height - flatRowContent.Bounds.Height),
                    0,
                    1);
                Assert.Equal(iconTile.TileSize, flatIconTile.TileSize);
                Assert.InRange(
                    Math.Abs(iconTile.Bounds.Width - flatIconTile.Bounds.Width),
                    0,
                    1);

                foreach (var section in new[]
                         {
                             DockerPanelSection.Images,
                             DockerPanelSection.Networks,
                         })
                {
                    panel.SelectSection(section);
                    await WaitUntilAsync(() =>
                    {
                        window.UpdateLayout();
                        return resourceList.GetVisualDescendants()
                            .OfType<ListBoxItem>()
                            .Any(item => item.IsEffectivelyVisible);
                    });
                    var sectionItem = Assert.Single(
                        resourceList.GetVisualDescendants().OfType<ListBoxItem>());
                    var sectionRow = Assert.Single(
                        sectionItem.GetVisualDescendants().OfType<DockerListItem>(),
                        item => item.Classes.Contains("DockerResourceListItem"));
                    var sectionIcon = Assert.Single(
                        sectionItem.GetVisualDescendants().OfType<IdentityTile>());

                    Assert.Equal(rowContentPadding, sectionRow.ContentPadding);
                    Assert.InRange(
                        Math.Abs(rowContent.Bounds.Height - sectionRow.Bounds.Height),
                        0,
                        1);
                    Assert.Equal(iconTile.TileSize, sectionIcon.TileSize);
                }
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task ContainerLifecycleButtonsDispatchFromPointerClicks() =>
        RunHeadlessAsync(async () =>
        {
            var client = new PagedLogDockerClient();
            using var panel = new DockerRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Docker",
                client,
                BuiltInConnections.Local);
            await panel.Initialization;

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return view.GetVisualDescendants()
                        .OfType<Button>()
                        .Any(button => AutomationProperties.GetName(button)
                            == "Restart container"
                            && button.IsEffectivelyVisible
                            && button.Bounds.Width > 0
                            && button.Bounds.Height > 0);
                });

                var restart = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => AutomationProperties.GetName(button) == "Restart container");
                var start = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => AutomationProperties.GetName(button) == "Start container");
                var stop = Assert.Single(
                    view.GetVisualDescendants().OfType<Button>(),
                    button => AutomationProperties.GetName(button) == "Stop container");
                Assert.False(start.IsEnabled);
                Assert.False(start.IsEffectivelyVisible);
                Assert.True(stop.IsEffectivelyVisible);
                Assert.True(restart.IsEnabled);
                Assert.NotNull(restart.Command);

                var stopCenter = stop.TranslatePoint(
                    new Point(stop.Bounds.Width / 2, stop.Bounds.Height / 2),
                    window);
                Assert.NotNull(stopCenter);
                window.MouseDown(stopCenter.Value, MouseButton.Left);
                window.MouseUp(stopCenter.Value, MouseButton.Left);
                await WaitUntilAsync(() =>
                    client.ContainerActions.Count == 1 && !panel.IsRefreshing);
                Assert.Equal(
                    ("container", DockerContainerAction.Stop),
                    client.ContainerActions[0]);

                var restartCenter = restart.TranslatePoint(
                    new Point(restart.Bounds.Width / 2, restart.Bounds.Height / 2),
                    window);
                Assert.NotNull(restartCenter);
                window.MouseDown(restartCenter.Value, MouseButton.Left);
                window.MouseUp(restartCenter.Value, MouseButton.Left);

                await WaitUntilAsync(() =>
                    client.ContainerActions.Count == 2 && !panel.IsRefreshing);
                Assert.Equal(
                    ("container", DockerContainerAction.Restart),
                    client.ContainerActions[1]);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task VolumeSizeIndicatorTracksTheUsageRequestLifetime() =>
        RunHeadlessAsync(async () =>
        {
            var volumeUsage = new TaskCompletionSource<IReadOnlyList<DockerVolumeUsage>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new PagedLogDockerClient
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

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var indicator = Assert.Single(
                    view.GetVisualDescendants()
                        .OfType<ProgressBar>(),
                    progress => AutomationProperties.GetName(progress)
                        == "Loading Docker resources");
                Assert.True(indicator.IsVisible);
                Assert.Contains("Loading sizes", panel.ResourceSummary, StringComparison.Ordinal);

                volumeUsage.SetResult(
                    [new DockerVolumeUsage("app-data", "64 MB", 64_000_000)]);
                await panel.VolumeUsageLoading;
                window.UpdateLayout();

                Assert.False(indicator.IsVisible);
                Assert.Equal("64 MB", Assert.Single(panel.Resources).Subtitle);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task RuntimeUnavailableStateOffersDockerInstallationGuide() =>
        RunHeadlessAsync(async () =>
        {
            var client = new PagedLogDockerClient
            {
                SnapshotError = new DockerError(
                    DockerErrorCode.RuntimeUnavailable,
                    "Docker is not installed in this environment, or its daemon is not running.",
                    true),
            };
            using var panel = new DockerRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Docker",
                client,
                BuiltInConnections.Local);
            await panel.Initialization;

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                var requested = false;
                view.DockerInstallGuideRequested += (_, _) => requested = true;
                window.Show();
                window.UpdateLayout();

                var button = view.FindControl<Button>("DockerInstallGuideButton");
                Assert.NotNull(button);
                Assert.True(button.IsEffectivelyVisible);
                Assert.Equal("Install Docker", button.Content);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(requested);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task DetailTabsCollapseFromTheDetailHeaderWidth() =>
        RunHeadlessAsync(async () =>
        {
            var client = new PagedLogDockerClient();
            using var panel = new DockerRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Docker",
                client,
                BuiltInConnections.Local);
            await panel.Initialization;

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 800,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                var detailHeader = view.FindControl<Grid>("DockerDetailHeader")!;
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return detailHeader.Classes.Contains("compactDetails");
                });
                var tabHost = Assert.Single(
                    detailHeader.GetVisualDescendants().OfType<StackPanel>(),
                    panel => panel.Classes.Contains("DockerDetailTabs"));
                var menuButton = Assert.Single(
                    detailHeader.GetVisualDescendants().OfType<Button>(),
                    button => string.Equals(AutomationProperties.GetName(button)
, "Open container view menu", StringComparison.Ordinal));
                Assert.False(tabHost.IsEffectivelyVisible);
                Assert.True(menuButton.IsEffectivelyVisible);
                var menuButtonCenter = menuButton.TranslatePoint(
                    new Point(menuButton.Bounds.Width / 2, menuButton.Bounds.Height / 2),
                    window);
                Assert.NotNull(menuButtonCenter);
                window.MouseDown(menuButtonCenter.Value, MouseButton.Left);
                window.MouseUp(menuButtonCenter.Value, MouseButton.Left);
                await WaitUntilAsync(() => menuButton.Flyout?.IsOpen == true);
                Assert.True(menuButton.Flyout?.IsOpen);
                menuButton.Flyout!.IsOpen = false;

                window.Width = 1_200;
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return !detailHeader.Classes.Contains("compactDetails");
                });
                Assert.True(tabHost.IsEffectivelyVisible);
                Assert.False(menuButton.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task InfoValuesRestoreTheirAvailableWidthAfterCompactLayout() =>
        RunHeadlessAsync(async () =>
        {
            var client = new PagedLogDockerClient();
            using var panel = new DockerRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Docker",
                client,
                BuiltInConnections.Local);
            await panel.Initialization;

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                var value = await WaitForInfoValueAsync(view, window);
                var initialWideWidth = value.Bounds.Width;
                Assert.True(initialWideWidth > 400);

                window.Width = 560;
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return view.Classes.Contains("narrowPanel")
                        && value.Bounds.Width < initialWideWidth;
                });

                window.Width = 1_200;
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return !view.Classes.Contains("narrowPanel")
                        && value.Bounds.Width > 400;
                });

                Assert.InRange(
                    Math.Abs(value.Bounds.Width - initialWideWidth),
                    0,
                    2);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public Task InitialLogsSettleAtTheEndAndRepeatedTopScrollsLoadPreviousPages() =>
        RunHeadlessAsync(async () =>
        {
            var client = new PagedLogDockerClient();
            using var panel = new DockerRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Docker",
                client,
                BuiltInConnections.Local);
            await panel.Initialization;
            panel.SelectDetail(DockerPanelDetail.Logs);
            await WaitUntilAsync(() => panel.LogRows.Count == 500);

            var view = new DockerRuntimePanelView
            {
                DataContext = panel,
            };
            var window = new Window
            {
                Width = 1_200,
                Height = 700,
                Content = view,
            };

            try
            {
                window.Show();
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    var candidate = view
                        .FindControl<ListBox>("LogList")?
                        .GetVisualDescendants()
                        .OfType<ScrollViewer>()
                        .FirstOrDefault();
                    return candidate is not null
                        && candidate.Extent.Height > candidate.Viewport.Height
                        && IsAtEnd(candidate);
                });

                var logList = view.FindControl<ListBox>("LogList")!;
                var scrollViewer = Assert.Single(
                    logList
                        .GetVisualDescendants()
                        .OfType<ScrollViewer>());
                await Task.Delay(100);
                window.UpdateLayout();
                Assert.True(IsAtEnd(scrollViewer));

                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
                var translatedLogCenter = logList.TranslatePoint(
                    new Point(logList.Bounds.Width / 2, logList.Bounds.Height / 2),
                    window);
                Assert.NotNull(translatedLogCenter);
                window.MouseWheel(
                    translatedLogCenter.Value,
                    new Vector(0, 1),
                    RawInputModifiers.None);
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return client.OlderPageReadCount == 1;
                });
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return panel.LogRows.Count == 1_000 && scrollViewer.Offset.Y > 80;
                });

                Assert.Equal(1_000, panel.LogRows.Count);
                Assert.True(panel.HasOlderLogs);
                Assert.True(scrollViewer.Offset.Y > 80);

                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
                window.MouseWheel(
                    translatedLogCenter.Value,
                    new Vector(0, 1),
                    RawInputModifiers.None);
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return client.OlderPageReadCount == 2;
                });
                await WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return panel.LogRows.Count == 1_500 && scrollViewer.Offset.Y > 80;
                });

                Assert.Equal(1_500, panel.LogRows.Count);
                Assert.False(panel.HasOlderLogs);
                Assert.True(scrollViewer.Offset.Y > 80);
            }
            finally
            {
                window.Close();
            }
        });

    private static bool IsAtEnd(ScrollViewer scrollViewer)
    {
        var maximumOffset = Math.Max(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        return scrollViewer.Offset.Y >= maximumOffset - 1;
    }

    private static async Task<TextBlock> WaitForInfoValueAsync(
        DockerRuntimePanelView view,
        Window window)
    {
        TextBlock? value = null;
        await WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            value = view.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(textBlock =>
                    textBlock.Classes.Contains("DockerPropertyValue")
                    && textBlock.IsEffectivelyVisible
                    && textBlock.Bounds.Width > 0);
            return value is not null;
        });
        return value!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The Docker log view did not reach the expected state in time.");
    }

    private static async Task RunHeadlessAsync(Func<Task> assertion)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            var completed = await session.Dispatch(
                async () =>
                {
                    await assertion();
                    return true;
                },
                timeout.Token);
            Assert.True(completed);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private sealed class PagedLogDockerClient : IDockerEngineClient
    {
        public int OlderPageReadCount { get; private set; }

        public List<(string ContainerId, DockerContainerAction Action)> ContainerActions { get; } = [];

        public DockerError? SnapshotError { get; init; }

        public TaskCompletionSource<IReadOnlyList<DockerVolumeUsage>>? VolumeUsageCompletion
        {
            get;
            init;
        }

        public ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            if (SnapshotError is not null)
            {
                return ValueTask.FromResult<DockerResult<DockerEngineSnapshot>>(
                    new DockerResult<DockerEngineSnapshot>.Failure(SnapshotError));
            }

            return ValueTask.FromResult<DockerResult<DockerEngineSnapshot>>(
                new DockerResult<DockerEngineSnapshot>.Success(new DockerEngineSnapshot(
                    new DockerEngineSummary("29.4.0", "linux", "arm64", "1.52"),
                    [new DockerContainerSummary(
                        "container",
                        "api",
                        "demo/api:latest",
                        "running",
                        "Up 1 hour",
                        string.Empty,
                        "1 hour ago",
                        "—",
                        "—",
                        "—",
                        "—",
                        "demo",
                        "api")],
                    [new DockerImageSummary(
                        "image",
                        "demo/api",
                        "latest",
                        "184 MB",
                        "2 days ago")],
                    [new DockerVolumeSummary(
                        "app-data",
                        "local",
                        "local",
                        "/var/lib/docker/volumes/app-data/_data")],
                    [new DockerNetworkSummary(
                        "network",
                        "app",
                        "bridge",
                        "local",
                        "today")],
                    DateTimeOffset.UtcNow)));
        }

        public async ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            var usage = VolumeUsageCompletion is null
                ? []
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
                        [new DockerInspectionProperty(
                            "Id",
                            "7d012249fbede52f16b253f3aad9b0afbdcacf669a4e29ebf0415dc72eebc334")],
                        "{}")));

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
            ConnectionProfile connection,
            DockerContainerLogRequest request,
            CancellationToken cancellationToken)
        {
            if (request.BeforeTimestamp is not null)
            {
                OlderPageReadCount++;
                var isFirstOlderPage = OlderPageReadCount == 1;
                var start = isFirstOlderPage ? 500 : 0;
                return ValueTask.FromResult<DockerResult<DockerContainerLogPage>>(
                    new DockerResult<DockerContainerLogPage>.Success(new DockerContainerLogPage(
                        CreateLines(start, 501),
                        isFirstOlderPage,
                        Timestamp(start),
                        Timestamp(start + 500))));
            }

            return ValueTask.FromResult<DockerResult<DockerContainerLogPage>>(
                new DockerResult<DockerContainerLogPage>.Success(new DockerContainerLogPage(
                    CreateLines(1_000, 500),
                    true,
                    Timestamp(1_000),
                    Timestamp(1_499))));
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
            return ValueTask.FromResult<DockerResult<bool>>(new DockerResult<bool>.Success(true));
        }

        private static IReadOnlyList<DockerContainerLogLine> CreateLines(int start, int count) =>
            [.. Enumerable.Range(start, count).Select(index => new DockerContainerLogLine(Timestamp(index), $"row {index}"))];

        private static string Timestamp(int index) =>
            $"2026-08-10T12:{index / 60:00}:{index % 60:00}.000000000Z";
    }
}
