using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Avalonia.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Application.Previews;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class FileRuntimePanelViewModelTests
{
    [Fact]
    public void DetailsDefaultToNewestModifiedAndExposeHeaderSortState()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        Assert.Equal(FileEntrySortField.Modified, panel.SortField);
        Assert.Equal(FileEntrySortDirection.Descending, panel.SortDirection);
        Assert.True(panel.IsSortingByModified);
        Assert.True(panel.IsSortDescending);
        Assert.False(panel.IsSortingByName);
        Assert.False(panel.IsSortingBySize);

        List<string?> changed = [];
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        panel.ChangeSort(FileEntrySortField.Name);

        Assert.True(panel.IsSortingByName);
        Assert.False(panel.IsSortingByModified);
        Assert.False(panel.IsSortDescending);
        Assert.Contains(nameof(FileRuntimePanelViewModel.IsSortingByName), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(FileRuntimePanelViewModel.IsSortingByModified), changed, StringComparer.Ordinal);
        Assert.Contains(nameof(FileRuntimePanelViewModel.IsSortDescending), changed, StringComparer.Ordinal);

        panel.ChangeSort(FileEntrySortField.Name);

        Assert.True(panel.IsSortDescending);
    }

    [Fact]
    public void DetailsColumnsExposeAdjustableSharedWidths()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        Assert.True(panel.FileNameColumnWidth.IsStar);
        Assert.Equal(90, panel.FileSizeColumnWidth.Value);
        Assert.Equal(140, panel.FileModifiedColumnWidth.Value);

        panel.FileNameColumnWidth = new GridLength(320);
        panel.FileSizeColumnWidth = new GridLength(110);
        panel.FileModifiedColumnWidth = new GridLength(180);

        Assert.Equal(320, panel.FileNameColumnWidth.Value);
        Assert.Equal(110, panel.FileSizeColumnWidth.Value);
        Assert.Equal(180, panel.FileModifiedColumnWidth.Value);
    }

    [Fact]
    public void PreviewPaneCanBeHiddenWithoutDiscardingItsViewState()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        Assert.False(panel.HasListingSummary);
        Assert.True(panel.IsPreviewVisible);
        Assert.Equal("Preview visible", panel.PreviewVisibilityStatus);

        panel.IsPreviewVisible = false;

        Assert.False(panel.IsPreviewVisible);
        Assert.Equal("Preview hidden", panel.PreviewVisibilityStatus);
    }

    /// <summary>
    /// A hidden preview has to give its width back, and the panel has to be the
    /// one holding that width. The view used to keep it: hiding the preview
    /// zeroed the grid column in code-behind, so a view built again — floating
    /// the panel, adding another one, any relayout — came back with the width
    /// from the markup and the panel still saying hidden, and the preview's
    /// share of the panel stayed reserved for nothing.
    /// </summary>
    [Fact]
    public void AHiddenPreviewGivesItsWidthBackAndTheWidthLivesOnThePanel()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        Assert.True(panel.PreviewColumnWidth.Value > 0);
        Assert.True(panel.PreviewSplitterWidth.Value > 0);
        Assert.True(panel.PreviewColumnMinWidth > 0);

        panel.IsPreviewVisible = false;

        // Zero on all three: a minimum the grid still honours holds the column
        // open however narrow its width is asked to be.
        Assert.Equal(0, panel.PreviewColumnWidth.Value);
        Assert.Equal(0, panel.PreviewSplitterWidth.Value);
        Assert.Equal(0, panel.PreviewColumnMinWidth);
    }

    /// <summary>
    /// And showing it again returns the width the splitter was left at, not the
    /// one the markup started with.
    /// </summary>
    [Fact]
    public void ShowingThePreviewAgainRestoresTheWidthItWasDraggedTo()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        panel.FileListColumnWidth = new GridLength(4, GridUnitType.Star);
        panel.PreviewColumnWidth = new GridLength(1, GridUnitType.Star);

        panel.IsPreviewVisible = false;
        panel.IsPreviewVisible = true;

        Assert.Equal(1, panel.PreviewColumnWidth.Value);
        Assert.True(panel.PreviewColumnWidth.IsStar);
        Assert.Equal(4, panel.FileListColumnWidth.Value);
    }

    /// <summary>
    /// The toolbar and the overflow menu are the same list of actions, dressed
    /// twice. They cannot disagree about whether an action is possible, because
    /// neither of them decides.
    /// </summary>
    [Fact]
    public async Task TheToolbarShowsSomeOfTheActionsAndTheMenuShowsAllOfThem()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        await panel.StartInitialization();

        Assert.NotEmpty(panel.ToolbarActions);
        Assert.NotEmpty(panel.MenuActions);
        Assert.Subset(
            panel.MenuActions.Select(action => action.Action).ToHashSet(),
            panel.ToolbarActions.Select(action => action.Action).ToHashSet());

        foreach (var button in panel.ToolbarActions)
        {
            var entry = panel.MenuActions.Single(action => action.Action == button.Action);
            Assert.Equal(entry.IsEnabled, button.IsEnabled);
            Assert.Equal(panel.IsActionEnabled(button.Action), button.IsEnabled);
        }
    }

    /// <summary>
    /// A rule is drawn where the kind of action changes and never above the
    /// first one — which is only knowable after the actions this connection
    /// cannot perform have been dropped.
    /// </summary>
    [Fact]
    public async Task AMenuNeverOpensWithARuleAcrossTheTop()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        await panel.StartInitialization();

        Assert.False(panel.MenuActions[0].StartsGroup);
        for (var index = 1; index < panel.MenuActions.Count; index++)
        {
            Assert.Equal(
                panel.MenuActions[index - 1].Group != panel.MenuActions[index].Group,
                panel.MenuActions[index].StartsGroup);
        }
    }

    /// <summary>
    /// The two right-click menus divide the same list between them. Nothing is
    /// in both — a menu over a file that also offered to make a folder would be
    /// answering a question nobody asked — and nothing is lost between them.
    /// </summary>
    [Fact]
    public async Task TheTwoRightClickMenusDivideTheActionsBetweenThem()
    {
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            new StubFilePanelClient(),
            deferInitialization: true);

        await panel.StartInitialization();

        var entry = panel.EntryMenuActions.Select(action => action.Action).ToArray();
        var folder = panel.FolderMenuActions.Select(action => action.Action).ToArray();

        Assert.NotEmpty(entry);
        Assert.NotEmpty(folder);
        Assert.Empty(entry.Intersect(folder));
        Assert.Equal(
            panel.MenuActions.Select(action => action.Action).Order(),
            entry.Concat(folder).Order());
        Assert.All(
            panel.EntryMenuActions,
            action => Assert.Equal(FilePanelActionScope.Selection, action.Scope));
        Assert.All(
            panel.FolderMenuActions,
            action => Assert.Equal(FilePanelActionScope.Folder, action.Scope));

        // And each re-groups from its own survivors rather than inheriting the
        // whole list's grouping, or one of them opens with a rule at the top.
        Assert.False(panel.EntryMenuActions[0].StartsGroup);
        Assert.False(panel.FolderMenuActions[0].StartsGroup);
    }

    /// <summary>
    /// Pasting is possible because something was copied somewhere else in the
    /// window. The panel has to hear about that, or its menu says no while the
    /// keyboard says yes.
    /// </summary>
    [Fact]
    public async Task PuttingSomethingOnTheClipboardEnablesPastingInEveryPanel()
    {
        var clipboard = new FileTransferClipboard();
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client, "alpha"));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue(),
            clipboard: clipboard);

        await panel.Initialization;

        Assert.False(panel.IsActionEnabled(FilePanelAction.Paste));

        clipboard.Payload = new Views.RuntimePanels.FilePanelTransferPayload(
            panel.Id,
            [client.Entries[0]],
            FilePanelTransferOperation.Copy);

        Assert.True(panel.IsActionEnabled(FilePanelAction.Paste));
        Assert.True(panel.FolderMenuActions
            .Single(action => action.Action == FilePanelAction.Paste)
            .IsEnabled);
    }

    /// <summary>
    /// An action nobody named. The rules and the words are deliberately apart —
    /// one is about what a connection can do, the other about what to call it —
    /// and the seam between them is where an action added on one side and not
    /// the other would simply throw the first time a menu opened.
    /// </summary>
    [Fact]
    public void EveryActionIsNamedAndGivenASymbol()
    {
        foreach (var action in Enum.GetValues<FilePanelAction>())
        {
            Assert.False(string.IsNullOrWhiteSpace(
                FilePanelActionPresentation.Label(action)));
            Assert.False(string.IsNullOrWhiteSpace(
                FilePanelActionPresentation.Description(action)));
            Assert.NotEqual(
                default,
                FilePanelActionPresentation.Glyph(action));
        }
    }

    /// <summary>
    /// The pill's short reading. A count beside a folder path is unambiguous
    /// without the word "item" attached to it, and the word is what goes when
    /// the panel is too narrow to hold both a path and a count.
    /// </summary>
    [Fact]
    public async Task TheItemCountHasAShortFormForWhenThereIsNoRoomForWords()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client, "alpha"));
        client.Entries.Add(Entry(client, "beta"));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        await panel.StartInitialization();

        Assert.Equal("2 item(s)", panel.Status);
        Assert.Equal("2", panel.ShortStatus);

        panel.Filter = "alp";

        Assert.Equal("1 of 2 item(s)", panel.Status);
        Assert.Equal("1/2", panel.ShortStatus);
    }

    private static FilePanelEntry Entry(StubFilePanelClient client, string name) =>
        new(
            client.Root.Child(new FilePanelPathSegment(name)),
            name,
            FilePanelEntryKind.File,
            10,
            DateTimeOffset.UnixEpoch,
            IsHidden: false);

    [Fact]
    public async Task DeferredInitializationDoesNotListUntilExplicitlyStarted()
    {
        var client = new StubFilePanelClient();
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        client.AddProfile("files.remote", "Remote");

        Assert.Equal(0, client.ListCallCount);
        Assert.Equal(2, panel.Profiles.Count);

        await panel.StartInitialization();

        Assert.Equal(1, client.ListCallCount);
        Assert.Equal("/", panel.LocationText);
    }

    [Fact]
    public async Task StartingDeferredInitializationMoreThanOnceReusesTheSameOperation()
    {
        var listing = new TaskCompletionSource<FilePanelResult<FilePanelPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubFilePanelClient
        {
            ListCompletion = listing,
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        var first = panel.StartInitialization();
        var second = panel.StartInitialization();

        Assert.Same(first, second);
        Assert.Same(first, panel.Initialization);
        Assert.Equal(1, client.ListCallCount);

        listing.SetResult(FilePanelResult<FilePanelPage>.Success(
            new FilePanelPage([], null)));
        await first;
    }

    [Fact]
    public async Task DisposingDeferredPanelBeforeStartNeverLists()
    {
        var client = new StubFilePanelClient();
        var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            deferInitialization: true);

        panel.Dispose();

        Assert.Equal(0, client.ListCallCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => panel.StartInitialization());
        Assert.Equal(0, client.ListCallCount);
    }

    [Fact]
    public async Task LoadingAndEmptyLocationHaveDistinctAccessiblePresentation()
    {
        var listing = new TaskCompletionSource<FilePanelResult<FilePanelPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubFilePanelClient
        {
            ListCompletion = listing,
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        Assert.Equal(FileBrowserContentState.Loading, panel.ContentState);
        Assert.True(panel.ShowLoadingState);
        Assert.Equal("File Viewer loading", panel.ContentPresentation.AccessibleName);

        listing.SetResult(FilePanelResult<FilePanelPage>.Success(
            new FilePanelPage([], null)));
        await panel.Initialization;

        Assert.Equal(FileBrowserContentState.EmptyLocation, panel.ContentState);
        Assert.True(panel.ShowEmptyLocationState);
        Assert.False(panel.ShowSearchNoResultsState);
        Assert.Equal("File Viewer empty location", panel.ContentPresentation.AccessibleName);
        Assert.Contains(
            "Create a folder",
            panel.ContentPresentation.SuggestedAction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigationKeepsCurrentListingBehindInteractionBlockingProgress()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "before.txt", FilePanelEntryKind.File, 5));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        var listing = new TaskCompletionSource<FilePanelResult<FilePanelPage>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ListCompletion = listing;
        var refresh = panel.RefreshAsync();

        Assert.True(panel.IsLoading);
        Assert.True(panel.ShowNavigationProgress);
        Assert.False(panel.ShowLoadingState);
        Assert.Equal("before.txt", Assert.Single(panel.Entries).Name);
        Assert.False(panel.CanEditLocation);
        Assert.False(panel.CanSelectProfile);

        var replacement = Entry(client.Root, "after.txt", FilePanelEntryKind.File, 6);
        listing.SetResult(FilePanelResult<FilePanelPage>.Success(
            new FilePanelPage([replacement], null)));
        await refresh;

        Assert.False(panel.IsLoading);
        Assert.False(panel.ShowNavigationProgress);
        Assert.Equal("after.txt", Assert.Single(panel.Entries).Name);
        Assert.True(panel.CanEditLocation);
        Assert.True(panel.CanSelectProfile);
    }

    [Fact]
    public async Task InitializationListsProviderRootAndAppliesNameFilter()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "zeta.txt", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "alpha", FilePanelEntryKind.Directory, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.True(panel.HasListingSummary);
        Assert.Equal("Home", panel.SelectedProfile?.Name);
        Assert.Equal("/", panel.LocationText);
        Assert.Collection(
            panel.Entries,
            item => Assert.Equal("alpha", item.Name),
            item => Assert.Equal("zeta.txt", item.Name));
        Assert.Equal("2 item(s)", panel.Status);

        panel.Filter = "zeta";

        var visible = Assert.Single(panel.Entries);
        Assert.Equal("zeta.txt", visible.Name);
        Assert.Equal("1 of 2 item(s)", panel.Status);

        panel.Filter = "missing";

        Assert.Empty(panel.Entries);
        Assert.Equal("0 of 2 item(s)", panel.Status);
        Assert.Equal(FileBrowserContentState.SearchNoResults, panel.ContentState);
        Assert.True(panel.ShowSearchNoResultsState);
        Assert.Equal(
            "File Viewer search has no results",
            panel.ContentPresentation.AccessibleName);
        Assert.Contains("missing", panel.ContentPresentation.Message, StringComparison.Ordinal);

        panel.Filter = string.Empty;

        Assert.Equal(2, panel.Entries.Count);
        Assert.Equal("2 item(s)", panel.Status);
        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
    }

    [Fact]
    public async Task ListingPagesAreFetchedOnDemandAndReplacePriorRows()
    {
        var client = new StubFilePanelClient();
        for (var index = 0; index < 501; index++)
        {
            client.Entries.Add(Entry(
                client.Root,
                $"file-{index:D3}.txt",
                FilePanelEntryKind.File,
                index));
        }

        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal(500, panel.Entries.Count);
        Assert.Equal(1, client.ListCallCount);
        Assert.True(panel.HasNextPage);
        Assert.False(panel.HasPreviousPage);
        await panel.NextPageAsync();
        Assert.Single(panel.Entries);
        Assert.Equal(2, client.ListCallCount);
        Assert.False(panel.HasMore);
        Assert.Contains(panel.Entries, entry => string.Equals(entry.Name, "file-500.txt", StringComparison.Ordinal));
        Assert.True(panel.HasPreviousPage);
        await panel.RefreshAsync();
        Assert.Equal("file-500.txt", Assert.Single(panel.Entries).Name);
        await panel.PreviousPageAsync();
        Assert.Equal(500, panel.Entries.Count);
        await panel.NextPageAsync();
        Assert.Equal("file-500.txt", Assert.Single(panel.Entries).Name);
    }

    [Fact]
    public async Task SearchPausesAtOnePageUntilRequestedAndCancelsPendingDemandOnQueryChange()
    {
        var client = new StubFilePanelClient();
        client.EnableCapabilities(FilePanelCapability.Search);
        for (var index = 0; index < 1001; index++)
        {
            client.SearchEntries.Add(Entry(client.Root, $"match-{index:D4}", FilePanelEntryKind.File, index));
        }

        using var panel = new FileRuntimePanelViewModel(PanelInstanceId.New(), "Files", client);
        await panel.Initialization;
        panel.Filter = "match";
        await WaitUntilAsync(() => panel.HasNextPage);
        Assert.Equal(500, panel.Entries.Count);
        Assert.False(panel.SearchCompletion.IsCompleted);
        await panel.NextPageAsync();
        await WaitUntilAsync(() => panel.HasNextPage);
        Assert.Equal(500, panel.Entries.Count);
        Assert.All(panel.Entries, entry => Assert.DoesNotContain("match-00", entry.Name, StringComparison.Ordinal));
        panel.Filter = "match-1000";
        await panel.SearchCompletion;
        Assert.Equal("match-1000", Assert.Single(panel.Entries).Name);
        Assert.False(panel.HasNextPage);
    }

    [Fact]
    public async Task SearchUsesProviderResultsOutsideTheMaterializedListing()
    {
        var client = new StubFilePanelClient();
        client.EnableCapabilities(FilePanelCapability.Search);
        client.Entries.Add(Entry(client.Root, "visible.txt", FilePanelEntryKind.File, 12));
        var nested = client.Root
            .Child(new FilePanelPathSegment("nested"))
            .Child(new FilePanelPathSegment("test.md"));
        client.SearchEntries.Add(new FilePanelEntry(
            nested,
            "test.md",
            FilePanelEntryKind.File,
            7,
            DateTimeOffset.UnixEpoch,
            false));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.Filter = "test";
        await panel.SearchCompletion;

        var result = Assert.Single(panel.Entries);
        Assert.Equal(nested, result.Entry.Location);
        Assert.Equal("1 search result(s)", panel.Status);
    }

    [Fact]
    public async Task ProviderObservationRefreshesTheCurrentListing()
    {
        var client = new StubFilePanelClient();
        client.EnableCapabilities(FilePanelCapability.Watch);
        client.Entries.Add(Entry(client.Root, "before.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        client.Entries.Add(Entry(client.Root, "after.txt", FilePanelEntryKind.File, 13));

        await client.WatchChanges.Writer.WriteAsync(
            FilePanelResult<FilePanelChange>.Success(new FilePanelChange(
                client.Root,
                FilePanelChangeKind.Changed)));

        for (var attempt = 0; attempt < 100 && panel.Entries.Count != 2; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2, panel.Entries.Count);
        Assert.Contains(panel.Entries, entry => string.Equals(entry.Name, "after.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsavedViewerDefaultsToBuiltInHomeEvenWhenAnotherProfileSortsFirst()
    {
        var client = new StubFilePanelClient();
        client.AddProfile("files.alpha", "AAA remote", prepend: true);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal("builtin.files.home", panel.SelectedProfile?.Id);
    }

    [Fact]
    public async Task FileSelectionUsesBoundedPreviewAndFormatsText()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "readme.txt", FilePanelEntryKind.File, 5));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("readme.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("hello"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        var selected = Assert.Single(panel.Entries);

        panel.SelectedEntry = selected;
        await panel.PreviewSelectedAsync();

        Assert.Equal("hello", panel.PreviewText);
        Assert.Equal("readme.txt", panel.PreviewTitle);
        Assert.NotNull(client.LastPreviewRequest);
        Assert.InRange(client.LastPreviewRequest!.MaximumBytes, 1, 256 * 1024);
    }

    [Fact]
    public async Task VersionedDeleteUsesOptimisticPrecondition()
    {
        var client = new StubFilePanelClient();
        var location = client.Root
            .Child(new FilePanelPathSegment("stale.txt"))
            .WithVersion("etag-7");
        client.Entries.Add(new FilePanelEntry(
            location,
            "stale.txt",
            FilePanelEntryKind.File,
            8,
            null,
            false));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);

        var deleted = await panel.DeleteSelectedAsync();

        Assert.True(deleted);
        Assert.NotNull(client.LastDeleteRequest);
        Assert.Equal(
            FilePanelMutationPreconditionKind.VersionMatches,
            client.LastDeleteRequest!.Precondition.Kind);
        Assert.Equal("etag-7", client.LastDeleteRequest.Precondition.Version);
    }

    [Fact]
    public async Task TypedProviderFailureIsVisibleAndRetryableThroughRefresh()
    {
        var client = new StubFilePanelClient
        {
            ListError = new FilePanelError(
                FilePanelErrorCode.AccessDenied,
                "file_access_denied",
                "Permission denied by provider.",
                false),
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.True(panel.HasError);
        Assert.Equal("Permission denied by provider.", panel.ErrorMessage);
        Assert.Equal("Location unavailable", panel.Status);

        client.ListError = null;
        await panel.RefreshAsync();

        Assert.False(panel.HasError);
        Assert.Equal("This location is empty", panel.Status);
    }

    [Theory]
    [InlineData(
        FilePanelErrorCode.AccessDenied,
        FileBrowserContentState.AccessRequired,
        "File Viewer access required")]
    [InlineData(
        FilePanelErrorCode.UnsupportedCapability,
        FileBrowserContentState.Unsupported,
        "File Viewer location unsupported")]
    public async Task PermissionAndUnsupportedFailuresHaveDistinctPresentation(
        FilePanelErrorCode errorCode,
        FileBrowserContentState expectedState,
        string accessibleName)
    {
        var client = new StubFilePanelClient
        {
            ListError = new FilePanelError(
                errorCode,
                $"test_{errorCode}",
                "Provider-specific detail.",
                Retryable: false),
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal(expectedState, panel.ContentState);
        Assert.True(panel.ShowErrorState);
        Assert.Equal(accessibleName, panel.ContentPresentation.AccessibleName);
        Assert.Equal("Provider-specific detail.", panel.ContentPresentation.Message);
        Assert.False(panel.CanRetryContentState);
        Assert.False(panel.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecoverableRefreshClearsStaleSelectionAndPreviewBeforeRetry()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "stale.txt", FilePanelEntryKind.File, 5));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("stale.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("stale preview"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();
        Assert.True(panel.HasPreview);

        client.ListError = new FilePanelError(
            FilePanelErrorCode.Offline,
            "provider_offline",
            "The provider is temporarily offline.",
            Retryable: true);
        await panel.RefreshAsync();

        Assert.Equal(FileBrowserContentState.RecoverableError, panel.ContentState);
        Assert.Null(panel.SelectedEntry);
        Assert.Null(panel.SelectedMetadata);
        Assert.False(panel.HasPreview);
        Assert.True(panel.CanRetryContentState);
        Assert.True(panel.RetryCommand.CanExecute(null));

        client.ListError = null;
        await panel.RetryAsync();

        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.HasError);
        Assert.False(panel.RetryCommand.CanExecute(null));
        Assert.Single(panel.Entries);
    }

    [Fact]
    public async Task ValidationAndMutationFailuresStayVisibleWithoutReplacingTheListing()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "keep.txt", FilePanelEntryKind.File, 5));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.ReportValidationError("Choose a valid local file.");

        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.ShowErrorState);
        Assert.True(panel.HasOperationIssue);
        Assert.Equal(FileOperationIssueKind.Validation, panel.OperationIssue?.Kind);
        Assert.Single(panel.Entries);

        client.CreateDirectoryError = new FilePanelError(
            FilePanelErrorCode.Conflict,
            "folder_conflict",
            "That folder already exists.",
            Retryable: true);
        var created = await panel.CreateFolderAsync("existing");

        Assert.False(created);
        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.ShowErrorState);
        Assert.Equal(FileOperationIssueKind.Conflict, panel.OperationIssue?.Kind);
        Assert.Null(panel.ContentIssue);
        Assert.Single(panel.Entries);
    }

    [Fact]
    public async Task TransferFailureIsOperationFeedbackRatherThanLocationRetryState()
    {
        var client = new StubFilePanelClient();
        var source = Entry(client.Root, "source.txt", FilePanelEntryKind.File, 5);
        client.Entries.Add(source);
        var transferError = new FilePanelError(
            FilePanelErrorCode.Offline,
            "transfer_offline",
            "The transfer service is offline.",
            Retryable: true);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue(transferError));
        await panel.Initialization;
        var destination = client.Root.Child(new FilePanelPathSegment("copy.txt"));

        var queued = await panel.QueueTransferAsync(
            new FilePanelTransferRequest(
                source.Location,
                destination,
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail));

        Assert.False(queued);
        Assert.Equal(FileBrowserContentState.Ready, panel.ContentState);
        Assert.False(panel.CanRetryContentState);
        Assert.Equal(FileOperationIssueKind.Offline, panel.OperationIssue?.Kind);
        Assert.Null(panel.ContentIssue);
        Assert.Single(panel.Entries);
    }

    [Fact]
    public async Task CompletedAndFailedTransfersNotifyExactlyOnce()
    {
        var client = new StubFilePanelClient();
        var queue = new StubTransferQueue();
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            queue,
            deferInitialization: true);
        List<PanelNotificationEvent> notifications = [];
        panel.NotificationReceived += (_, notification) => notifications.Add(notification);
        var source = client.Root.Child(new FilePanelPathSegment("source.txt"));
        var completedDestination = client.Root.Child(new FilePanelPathSegment("copy.txt"));
        var failedDestination = client.Root.Child(new FilePanelPathSegment("backup.txt"));

        await panel.QueueTransferAsync(new FilePanelTransferRequest(
            source,
            completedDestination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.Fail));
        var completedId = Assert.Single(queue.Transfers).Id;
        queue.Transition(completedId, FilePanelTransferState.Running);

        Assert.Empty(notifications);

        var completedAt = DateTimeOffset.Parse("2026-08-18T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        queue.Transition(completedId, FilePanelTransferState.Completed, completedAt);
        queue.SignalChanged();

        await panel.QueueTransferAsync(new FilePanelTransferRequest(
            source,
            failedDestination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.Fail));
        var failedId = queue.Transfers.Single(transfer => transfer.Id != completedId).Id;
        var failedAt = completedAt.AddMinutes(1);
        queue.Transition(
            failedId,
            FilePanelTransferState.Failed,
            failedAt,
            new FilePanelError(
                FilePanelErrorCode.Offline,
                "transfer_offline",
                "The remote provider went offline.",
                Retryable: true));
        queue.SignalChanged();

        Assert.Collection(
            notifications,
            completed =>
            {
                Assert.Equal(1, completed.Sequence);
                Assert.Equal(PanelNotificationKind.FileTransferCompleted, completed.Kind);
                Assert.Equal(
                    PanelNotificationEffects.Visual | PanelNotificationEffects.System,
                    completed.Effects);
                Assert.Equal("File transfer completed", completed.Title);
                Assert.Equal("/source.txt → /copy.txt", completed.Body);
                Assert.Equal(completedAt, completed.TimestampUtc);
            },
            failed =>
            {
                Assert.Equal(2, failed.Sequence);
                Assert.Equal(PanelNotificationKind.FileTransferFailed, failed.Kind);
                Assert.Equal(
                    PanelNotificationEffects.Visual | PanelNotificationEffects.System,
                    failed.Effects);
                Assert.Equal("File transfer failed", failed.Title);
                Assert.Contains("/source.txt → /backup.txt", failed.Body);
                Assert.Contains("went offline", failed.Body);
                Assert.Equal(failedAt, failed.TimestampUtc);
            });
    }

    [Fact]
    public async Task DisposingAFilePanelStopsTransferNotifications()
    {
        var client = new StubFilePanelClient();
        var queue = new StubTransferQueue();
        var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            queue,
            deferInitialization: true);
        var notificationCount = 0;
        panel.NotificationReceived += (_, _) => notificationCount++;
        var source = client.Root.Child(new FilePanelPathSegment("source.txt"));
        var destination = client.Root.Child(new FilePanelPathSegment("copy.txt"));
        await panel.QueueTransferAsync(new FilePanelTransferRequest(
            source,
            destination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.Fail));
        var transferId = Assert.Single(queue.Transfers).Id;

        Assert.Equal(1, queue.SubscriberCount);

        panel.Dispose();
        queue.Transition(transferId, FilePanelTransferState.Completed);

        Assert.Equal(0, queue.SubscriberCount);
        Assert.Equal(0, notificationCount);
    }

    [Fact]
    public async Task FailedDestinationNavigationCannotLeaveEntriesFromThePreviousLocation()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "old.txt", FilePanelEntryKind.File, 5));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);
        var remote = client.AddProfile("files.remote", "Remote");
        client.ListError = new FilePanelError(
            FilePanelErrorCode.Offline,
            "remote_offline",
            "The remote provider is offline.",
            Retryable: true);

        await panel.SelectProfileAsync(remote);

        Assert.Equal(remote.Root, panel.CurrentLocation);
        Assert.Empty(panel.Entries);
        Assert.Null(panel.SelectedEntry);
        Assert.Equal(FileBrowserContentState.RecoverableError, panel.ContentState);
        Assert.NotNull(panel.ContentIssue);
        Assert.Null(panel.OperationIssue);
    }

    [Theory]
    [InlineData(FilePanelErrorCode.AccessDenied, FileOperationIssueKind.PermissionDenied)]
    [InlineData(FilePanelErrorCode.AuthenticationRequired, FileOperationIssueKind.AuthenticationRequired)]
    [InlineData(FilePanelErrorCode.Offline, FileOperationIssueKind.Offline)]
    [InlineData(FilePanelErrorCode.QuotaExceeded, FileOperationIssueKind.QuotaExceeded)]
    [InlineData(FilePanelErrorCode.Conflict, FileOperationIssueKind.Conflict)]
    [InlineData(FilePanelErrorCode.PreconditionFailed, FileOperationIssueKind.Stale)]
    [InlineData(FilePanelErrorCode.UnsupportedCapability, FileOperationIssueKind.Unsupported)]
    [InlineData(FilePanelErrorCode.PartialTransfer, FileOperationIssueKind.Partial)]
    [InlineData(FilePanelErrorCode.Cancelled, FileOperationIssueKind.Cancelled)]
    public async Task ProviderFailuresExposeStableUserFacingState(
        FilePanelErrorCode errorCode,
        FileOperationIssueKind expectedKind)
    {
        var client = new StubFilePanelClient
        {
            ListError = new FilePanelError(
                errorCode,
                $"test_{errorCode}",
                "Provider detail.",
                Retryable: false),
        };
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        Assert.Equal(expectedKind, panel.CurrentIssue?.Kind);
        Assert.Equal("Provider detail.", panel.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorTitle));
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorSuggestedAction));
    }

    [Fact]
    public void AuthenticationFailureHasAuthenticationSpecificPresentation()
    {
        var issue = FileOperationIssue.FromProvider(new FilePanelError(
            FilePanelErrorCode.AuthenticationRequired,
            "file_authentication_required",
            "SFTP authentication failed.",
            Retryable: false));

        Assert.Equal(FileOperationIssueKind.AuthenticationRequired, issue.Kind);
        Assert.Equal("Authentication failed", issue.Title);
        Assert.DoesNotContain(
            "permissions",
            issue.SuggestedAction,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        FilePanelErrorCode.AlreadyExists,
        "Destination already exists",
        "Keep Both")]
    [InlineData(FilePanelErrorCode.SharingViolation, "File is in use", "process")]
    [InlineData(FilePanelErrorCode.DirectoryNotEmpty, "Folder is not empty", "recursive")]
    public void ConflictFailuresKeepOperationSpecificRecovery(
        FilePanelErrorCode errorCode,
        string title,
        string expectedAction)
    {
        var issue = FileOperationIssue.FromProvider(new FilePanelError(
            errorCode,
            $"test_{errorCode}",
            "Provider detail.",
            Retryable: false));

        Assert.Equal(FileOperationIssueKind.Conflict, issue.Kind);
        Assert.Equal(title, issue.Title);
        Assert.Contains(expectedAction, issue.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        Assert.True(issue.CanRetry);
    }

    [Fact]
    public async Task EntriesCanBeSortedWithoutMovingFoldersBelowFiles()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "folder", FilePanelEntryKind.Directory, null));
        client.Entries.Add(Entry(client.Root, "small.txt", FilePanelEntryKind.File, 4));
        client.Entries.Add(Entry(client.Root, "large.txt", FilePanelEntryKind.File, 4096));
        client.Entries.Add(Entry(client.Root, "unknown.txt", FilePanelEntryKind.File, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SortField = FileEntrySortField.Size;
        panel.SortDirection = FileEntrySortDirection.Descending;
        panel.ViewMode = FileBrowserViewMode.Grid;

        Assert.Equal(
            ["folder", "large.txt", "small.txt", "unknown.txt"],
            panel.Entries.Select(item => item.Name), StringComparer.Ordinal);
        Assert.Equal(FileBrowserViewMode.Grid, panel.ViewMode);

        panel.ChangeSort(FileEntrySortField.Size);

        Assert.Equal(FileEntrySortDirection.Ascending, panel.SortDirection);
        Assert.Equal(
            ["folder", "small.txt", "large.txt", "unknown.txt"],
            panel.Entries.Select(item => item.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SelectionPresentsFreshStatMetadataAlongsidePreview()
    {
        var client = new StubFilePanelClient();
        var listed = Entry(client.Root, "report.bin", FilePanelEntryKind.File, 4);
        client.Entries.Add(listed);
        client.StatEntry = new FilePanelEntry(
            listed.Location.WithVersion("stat-version"),
            listed.Name,
            listed.Kind,
            4096,
            new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero),
            false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.Equal(listed.Location, client.LastStatLocation);
        Assert.True(panel.SelectedMetadata?.IsStatBacked);
        Assert.Equal("4 KB", panel.SelectedMetadata?.Size);
        Assert.Equal("stat-version", panel.SelectedMetadata?.Version);
        Assert.Equal("Live provider metadata", panel.SelectedMetadata?.Source);
        Assert.Null(panel.MetadataIssue);
    }

    [Fact]
    public async Task StatFailureKeepsListingMetadataAndExposesTypedIssue()
    {
        var client = new StubFilePanelClient
        {
            StatError = new FilePanelError(
                FilePanelErrorCode.AccessDenied,
                "file_access_denied",
                "Metadata is restricted.",
                Retryable: false),
        };
        client.Entries.Add(Entry(client.Root, "private.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.False(panel.SelectedMetadata?.IsStatBacked);
        Assert.Equal("12 B", panel.SelectedMetadata?.Size);
        Assert.Equal(FileOperationIssueKind.PermissionDenied, panel.MetadataIssue?.Kind);
        Assert.False(panel.HasError);
    }

    [Fact]
    public async Task FilteringOutSelectionClearsPreviewAndMetadata()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "selected.txt", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "visible.txt", FilePanelEntryKind.File, 24));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("selected.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("selected preview"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = panel.Entries.Single(item => string.Equals(item.Name, "selected.txt", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();

        panel.Filter = "visible";

        Assert.Null(panel.SelectedEntry);
        Assert.Null(panel.SelectedMetadata);
        Assert.False(panel.HasPreview);
        Assert.Equal("Preview", panel.PreviewTitle);
    }

    [Fact]
    public async Task ChangingSelectionCancelsAnInFlightPreviewWithoutLeavingLoadingState()
    {
        var client = new StubFilePanelClient
        {
            PreviewCompletion = new TaskCompletionSource<FilePanelResult<FilePanelPreview>>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        client.Entries.Add(Entry(client.Root, "slow.txt", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "folder", FilePanelEntryKind.Directory, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = panel.Entries.Single(item => string.Equals(item.Name, "slow.txt", StringComparison.Ordinal));
        var preview = panel.PreviewSelectedAsync();
        Assert.True(panel.IsPreviewLoading);

        panel.SelectedEntry = panel.Entries.Single(item => string.Equals(item.Name, "folder", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();
        await preview;

        Assert.False(panel.IsPreviewLoading);
        Assert.False(panel.HasPreview);
        Assert.Equal("Preview", panel.PreviewTitle);
    }

    [Fact]
    public async Task ProviderPreviewLimitLargerThanIntDoesNotOverflowBoundedRead()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "files.large-preview",
            "Large preview",
            maximumPreviewBytes: (long)int.MaxValue + 1);
        client.Entries.Add(Entry(remote.Root, "large.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        panel.SelectedEntry = Assert.Single(panel.Entries);

        await panel.PreviewSelectedAsync();

        Assert.Equal(256 * 1024, client.LastPreviewRequest?.MaximumBytes);
    }

    [Fact]
    public async Task TransferAndExternalOpenCapabilitiesFollowProviderAndSelection()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "local.txt", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;

        Assert.False(panel.CanDownload);
        Assert.False(panel.CanUpload);
        Assert.False(panel.CanOpenExternally);

        panel.SelectedEntry = Assert.Single(panel.Entries);

        Assert.True(panel.CanDownload);
        Assert.False(panel.CanUpload);
        Assert.True(panel.CanOpenExternally);

        var remote = client.AddProfile(
            "files.remote",
            "Remote",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite);
        client.Entries.Clear();
        client.Entries.Add(Entry(remote.Root, "remote.txt", FilePanelEntryKind.File, 24));
        await panel.SelectProfileAsync(remote);
        panel.SelectedEntry = Assert.Single(panel.Entries);

        Assert.True(panel.CanDownload);
        Assert.True(panel.CanUpload);
        Assert.False(panel.CanOpenExternally);
    }

    [Fact]
    public async Task IncomingDirectoryTransferTargetsTheCurrentFolderAndKeepsBoth()
    {
        var client = new StubFilePanelClient();
        var sourceProfile = client.AddProfile("files.source", "Source");
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        var source = Entry(
            sourceProfile.Root,
            "project",
            FilePanelEntryKind.Directory,
            null);

        var request = panel.CreateIncomingTransferRequest(
            source,
            FilePanelTransferOperation.Copy);

        Assert.Equal(source.Location, request.Source);
        Assert.Equal(
            client.Root.Child(new FilePanelPathSegment("project")),
            request.Destination);
        Assert.Equal(FilePanelConflictPolicy.KeepBoth, request.ConflictPolicy);
        Assert.Equal(FilePanelTransferOperation.Copy, request.Operation);
    }

    [Fact]
    public async Task IncomingTransferCanTargetAVisibleDestinationFolder()
    {
        var client = new StubFilePanelClient();
        var sourceProfile = client.AddProfile("files.source", "Source");
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        var source = Entry(
            sourceProfile.Root,
            "archive.zip",
            FilePanelEntryKind.File,
            2048);
        var destinationFolder = client.Root.Child(
            new FilePanelPathSegment("incoming"));

        var request = panel.CreateIncomingTransferRequest(
            source,
            FilePanelTransferOperation.Copy,
            destinationFolder);

        Assert.Equal(
            destinationFolder.Child(new FilePanelPathSegment("archive.zip")),
            request.Destination);
    }

    [Fact]
    public async Task IncomingTransferRejectsTheItemsExistingLocation()
    {
        var client = new StubFilePanelClient();
        var source = Entry(
            client.Root,
            "archive.zip",
            FilePanelEntryKind.File,
            2048);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;

        Assert.False(panel.CanReceiveTransfer(source));
        var error = Assert.Throws<InvalidOperationException>(() =>
            panel.CreateIncomingTransferRequest(
                source,
                FilePanelTransferOperation.Copy));

        Assert.Contains(
            "already in this destination",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncomingTransferRejectsADropFolderFromAnotherProvider()
    {
        var client = new StubFilePanelClient();
        var sourceProfile = client.AddProfile("files.source", "Source");
        var otherProfile = client.AddProfile("files.other", "Other");
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        var source = Entry(
            sourceProfile.Root,
            "archive.zip",
            FilePanelEntryKind.File,
            2048);

        var error = Assert.Throws<InvalidOperationException>(() =>
            panel.CreateIncomingTransferRequest(
                source,
                FilePanelTransferOperation.Copy,
                otherProfile.Root));

        Assert.Contains(
            "does not belong",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadEditorPrefersBuiltInHomeDestination()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile("files.remote", "Remote");
        var source = Entry(remote.Root, "archive.zip", FilePanelEntryKind.File, 2048);
        client.Entries.Add(source);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        panel.SelectedEntry = Assert.Single(panel.Entries);

        var editor = panel.CreateDownloadEditor();
        var request = editor.CreateRequest();

        Assert.Equal("builtin.files.home", editor.SelectedDestinationProfile.Id);
        Assert.Equal("/archive.zip", editor.Destination);
        Assert.Equal(source.Location, request.Source);
        Assert.Equal("builtin.files.home", request.Destination.ProviderProfileId);
    }

    [Fact]
    public async Task UploadEditorUsesHomeRelativeSourceAndCurrentDestination()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "files.remote",
            "Remote",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        panel.LocationText = "/incoming";
        await panel.NavigateFromTextAsync();

        var localPath = Path.Combine(
            HomePath(),
            $".ghostshell-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, "upload payload");
        try
        {
            var editor = panel.CreateUploadEditor(localPath);
            var request = editor.CreateRequest();

            Assert.Equal("builtin.files.home", editor.Source.Location.ProviderProfileId);
            Assert.Equal(Path.GetFileName(localPath), editor.Source.Name);
            Assert.Equal(remote.Id, editor.SelectedDestinationProfile.Id);
            Assert.Equal($"/incoming/{Path.GetFileName(localPath)}", editor.Destination);
            Assert.Equal(editor.Source.Location, request.Source);
            Assert.Equal(remote.Id, request.Destination.ProviderProfileId);
            Assert.Equal(editor.Destination, FileLocationPresentation.Display(request.Destination));
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public async Task UploadEditorRejectsAFileThatIsNotThereBeforeReadingIt()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "files.remote",
            "Remote",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);
        var missing = Path.Combine(HomePath(), $"ghostshell-missing-{Guid.NewGuid():N}.txt");

        // The local provider reaches the whole filesystem, so where a file sits
        // is no longer the question — whether it is there still is.
        var error = Assert.Throws<ArgumentException>(() => panel.CreateUploadEditor(missing));

        Assert.Contains("no longer exists", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedBuiltInHomeFileResolvesToLocalPath()
    {
        var client = new StubFilePanelClient();
        var folder = client.Root.Child(new FilePanelPathSegment("documents"));
        client.Entries.Add(Entry(folder, "notes.txt", FilePanelEntryKind.File, 32));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);

        var resolved = panel.GetSelectedLocalPath();

        Assert.True(panel.CanOpenExternally);
        // Provider paths are relative to the filesystem root the local
        // provider is rooted at, not to the user's home folder.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(LocalRootPath(), "documents", "notes.txt")),
            resolved);
    }

    [Fact]
    public async Task WindowsShapedProviderSegmentCannotResolveOutsideTheProviderRoot()
    {
        const string windowsTraversal = @"..\outside.txt";
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(
            client.Root,
            windowsTraversal,
            FilePanelEntryKind.File,
            1));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);

        if (OperatingSystem.IsWindows())
        {
            var error = Assert.Throws<InvalidOperationException>(panel.GetSelectedLocalPath);
            Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        // Backslash is a legal filename character on POSIX, not a path separator.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(LocalRootPath(), windowsTraversal)),
            panel.GetSelectedLocalPath());
    }

    [Fact]
    public async Task SavedProviderSelectionWaitsForTheLiveCatalogRefresh()
    {
        var client = new StubFilePanelClient();
        var savedId = new FileProviderProfileId("files.saved");
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            initialProfileId: savedId);

        await panel.Initialization;
        Assert.Null(panel.SelectedProfile);
        Assert.Contains("not currently available", panel.ErrorMessage, StringComparison.Ordinal);

        client.AddProfile("files.saved", "Saved provider");

        Assert.Equal("files.saved", panel.SelectedProfile?.Id);
        Assert.Equal("files.saved", panel.CurrentLocation?.ProviderProfileId);
    }

    [Fact]
    public async Task An_image_is_read_whole_rather_than_from_the_bounded_preview()
    {
        // A head of a JPEG is not a smaller JPEG: drawing the bounded preview
        // bytes produced noise for any image past the read limit.
        var path = Path.Combine(Path.GetTempPath(), $"ghostshell-image-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, TinyPng());
        try
        {
            var client = new StubFilePanelClient { MaterializedPath = path };
            client.Entries.Add(Entry(client.Root, "photo.png", FilePanelEntryKind.File, 4_700_000));
            client.Preview = new FilePanelPreview(
                client.Root.Child(new FilePanelPathSegment("photo.png")),
                FilePanelPreviewKind.Image,
                "image/png",
                // A truncated head, exactly as a bounded read of a large
                // photograph returns.
                TinyPng().AsSpan(0, 20),
                isTruncated: true);
            using var panel = new FileRuntimePanelViewModel(
                PanelInstanceId.New(),
                "Files",
                client);
            await panel.Initialization;

            panel.SelectedEntry = Assert.Single(panel.Entries);
            await panel.PreviewSelectedAsync();
            await WaitUntilAsync(() => client.MaterializeCallCount > 0);

            // The whole file was asked for. Decoding it needs a rendering
            // platform, which a view-model test has no business starting, so
            // the drawing itself is proven by the harness capture instead.
            Assert.Equal(1, client.MaterializeCallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>A 1x1 PNG, small enough to embed and real enough to decode.</summary>
    private static byte[] TinyPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
        0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x03, 0x01, 0x01, 0x00, 0x18, 0xDD, 0x8D,
        0xB0, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
        0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    [Fact]
    public async Task A_small_remote_file_previews_without_asking()
    {
        var (panel, client) = await RemotePanelAsync(sizeBytes: 64 * 1024);

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.False(panel.ShowPreviewDownloadPrompt);
        Assert.NotNull(client.LastPreviewRequest);
        panel.Dispose();
    }

    [Fact]
    public async Task A_large_remote_file_waits_to_be_asked_for()
    {
        var (panel, client) = await RemotePanelAsync(
            sizeBytes: FileRuntimePanelViewModel.AutoDownloadPreviewBytes + 1);

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.True(panel.ShowPreviewDownloadPrompt);
        Assert.False(panel.ShowPreviewPlaceholder);
        // Nothing was fetched: the point of the gate is that the bytes stay on
        // the far side of the connection until asked for.
        Assert.Null(client.LastPreviewRequest);
        Assert.Contains("download", panel.PreviewDownloadPromptDetail, StringComparison.OrdinalIgnoreCase);

        await panel.PreviewDeferredAsync();

        Assert.False(panel.ShowPreviewDownloadPrompt);
        Assert.NotNull(client.LastPreviewRequest);
        panel.Dispose();
    }

    [Fact]
    public async Task A_file_previewed_once_is_not_asked_about_again()
    {
        var (panel, client) = await RemotePanelAsync(
            sizeBytes: FileRuntimePanelViewModel.AutoDownloadPreviewBytes + 1);
        var file = Assert.Single(panel.Entries);

        panel.SelectedEntry = file;
        await panel.PreviewSelectedAsync();
        await panel.PreviewDeferredAsync();
        Assert.False(panel.ShowPreviewDownloadPrompt);

        // Away and back again: the answer given a moment ago still stands.
        panel.SelectedEntry = null;
        await panel.PreviewSelectedAsync();
        panel.SelectedEntry = file;
        await panel.PreviewSelectedAsync();

        Assert.False(panel.ShowPreviewDownloadPrompt);
        Assert.NotNull(client.LastPreviewRequest);
        panel.Dispose();
    }

    [Fact]
    public async Task Auto_download_off_defers_even_a_small_remote_file()
    {
        var (panel, client) = await RemotePanelAsync(sizeBytes: 1024);
        panel.AutoDownloadPreviews = false;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.True(panel.ShowPreviewDownloadPrompt);
        Assert.Null(client.LastPreviewRequest);
        panel.Dispose();
    }

    [Fact]
    public async Task A_local_file_never_waits_however_large_it_is()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(
            client.Root,
            "huge.bin",
            FilePanelEntryKind.File,
            FileRuntimePanelViewModel.AutoDownloadPreviewBytes * 4));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);
        await panel.Initialization;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.False(panel.RequiresHostTransferForPreview);
        Assert.False(panel.ShowPreviewDownloadPrompt);
        Assert.NotNull(client.LastPreviewRequest);
    }

    private static async Task<(FileRuntimePanelViewModel Panel, StubFilePanelClient Client)>
        RemotePanelAsync(long sizeBytes)
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "remote.sftp",
            "Remote",
            prepend: true,
            capabilities: FilePanelCapability.List
                | FilePanelCapability.Stat
                | FilePanelCapability.RangedRead,
            family: FileProviderFamily.Sftp);
        client.Entries.Clear();
        client.Entries.Add(Entry(remote.Root, "payload.bin", FilePanelEntryKind.File, sizeBytes));
        var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            initialProfileId: new FileProviderProfileId(remote.Id));
        await panel.Initialization;
        Assert.True(panel.RequiresHostTransferForPreview);
        return (panel, client);
    }

    private static FilePanelEntry Entry(
        FilePanelLocation parent,
        string name,
        FilePanelEntryKind kind,
        long? size) => new(
        parent.Child(new FilePanelPathSegment(name)).WithVersion($"version-{name}"),
        name,
        kind,
        size,
        new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
        false);

    /// <summary>
    /// The filesystem root the local provider is rooted at — the base its
    /// paths resolve against.
    /// </summary>
    private static string LocalRootPath() =>
        Path.GetPathRoot(HomePath()) is { Length: > 0 } root ? root : HomePath();

    private static string HomePath()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? Path.GetFullPath(AppContext.BaseDirectory)
            : Path.GetFullPath(path);
    }

    [Fact]
    public async Task A_delimited_file_previews_as_a_table()
    {
        var panel = await PreviewOf(
            "people.csv",
            "name,city\nada,london\ngrace,york\n");

        Assert.True(panel.HasTablePreview);
        Assert.False(panel.HasSourcePreview);
        var table = panel.PreviewTable!;
        Assert.Equal(["name", "city"], table.Columns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal(2, table.Rows.Count);
        // Every cell in a column is as wide as its header, or they do not line up.
        Assert.Equal(
            table.Columns[0].Width,
            table.Rows[0].Cells[0].Width);
        panel.Dispose();
    }

    [Fact]
    public async Task A_format_offers_its_own_switches()
    {
        var panel = await PreviewOf("notes.md", "# Title");

        var toggle = Assert.Single(panel.PreviewToggles);
        Assert.Equal("Show raw", toggle.Label);
        Assert.True(panel.HasMarkdownPreview);
        panel.Dispose();
    }

    [Fact]
    public async Task The_row_of_switches_announces_that_it_has_appeared()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "notes.md", FilePanelEntryKind.File, 7));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("notes.md")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("# Title"),
            isTruncated: false);
        using var panel = new FileRuntimePanelViewModel(PanelInstanceId.New(), "Files", client);
        await panel.Initialization;

        // The panel is bound before any file is selected, so a switch row that
        // never announces itself stays invisible however full it gets.
        var announced = new List<string>();
        panel.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);
        panel.SelectedEntry = panel.Entries.Single();
        await panel.PreviewSelectedAsync();

        Assert.True(panel.HasPreviewToggles);
        Assert.Contains(nameof(FileRuntimePanelViewModel.HasPreviewToggles), announced, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Flipping_a_switch_re_reads_the_bytes_already_in_hand()
    {
        var panel = await PreviewOf("notes.md", "# Title", out var client);
        var before = client.PreviewCallCount;

        panel.PreviewToggles.Single().IsOn = true;
        await panel.PreviewPresentation;

        // Changing how a file is read must not cost another provider call —
        // on a remote provider that would be another download.
        Assert.Equal(before, client.PreviewCallCount);
        Assert.False(panel.HasMarkdownPreview);
        Assert.True(panel.HasSourcePreview);
        Assert.Equal("# Title", panel.PreviewText);
        panel.Dispose();
    }

    [Fact]
    public async Task Flipping_a_switch_does_not_blank_the_text_on_the_way()
    {
        var panel = await PreviewOf("notes.md", "# Title\n\n```bash\nls -al\n```\n");
        var seen = new List<string?>();
        panel.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(FileRuntimePanelViewModel.PreviewText), StringComparison.Ordinal))
            {
                seen.Add(panel.PreviewText);
            }
        };

        panel.PreviewToggles.Single().IsOn = true;
        await panel.PreviewPresentation;

        // Nulling the text between readings makes every view bound to it tear
        // down and rebuild, and rebuilding a Markdown document reinstalls a
        // syntax grammar per fenced block — which is what made this slow.
        Assert.DoesNotContain(null, seen, StringComparer.Ordinal);
        panel.Dispose();
    }

    [Fact]
    public async Task Showing_markdown_as_source_announces_the_change()
    {
        // The two readings are the same string, so nothing about the text
        // changes — and a view told only about the text would keep drawing
        // the document it already drew.
        var panel = await PreviewOf("notes.md", "# Title");
        var announced = new List<string>();
        panel.PropertyChanged += (_, e) => announced.Add(e.PropertyName ?? string.Empty);

        panel.PreviewToggles.Single().IsOn = true;
        await panel.PreviewPresentation;

        Assert.True(panel.HasSourcePreview);
        Assert.False(panel.HasMarkdownPreview);
        Assert.Contains(nameof(FileRuntimePanelViewModel.HasSourcePreview), announced, StringComparer.Ordinal);
        Assert.Contains(nameof(FileRuntimePanelViewModel.HasMarkdownPreview), announced, StringComparer.Ordinal);

        announced.Clear();
        panel.PreviewToggles.Single().IsOn = false;
        await panel.PreviewPresentation;

        Assert.True(panel.HasMarkdownPreview);
        Assert.Contains(nameof(FileRuntimePanelViewModel.HasMarkdownPreview), announced, StringComparer.Ordinal);
        panel.Dispose();
    }

    [Fact]
    public async Task Turning_a_table_off_shows_the_file_as_written()
    {
        var panel = await PreviewOf("people.csv", "name,city\nada,london\n");

        panel.PreviewToggles.Single().IsOn = false;
        await panel.PreviewPresentation;

        Assert.False(panel.HasTablePreview);
        Assert.Equal("name,city\nada,london\n", panel.PreviewText);
        panel.Dispose();
    }

    [Fact]
    public async Task A_binary_file_is_named_rather_than_dumped_as_hex()
    {
        var panel = await PreviewOf(
            "libghost.dylib",
            "\u0000\u0001",
            kind: FilePanelPreviewKind.Hex);

        Assert.True(panel.HasBinaryPreview);
        Assert.False(panel.HasSourcePreview);
        Assert.Equal("DYLIB binary", panel.PreviewBinary!.FormatName);
        panel.Dispose();
    }

    [Fact]
    public async Task The_bytes_are_there_for_whoever_asks()
    {
        var panel = await PreviewOf(
            "payload.bin",
            "\u0000\u0001",
            kind: FilePanelPreviewKind.Hex);

        panel.PreviewToggles.Single().IsOn = true;
        await panel.PreviewPresentation;

        Assert.False(panel.HasBinaryPreview);
        Assert.True(panel.HasHexPreview);
        // Not text: a dump is drawn from rows, a screenful at a time.
        Assert.False(panel.HasSourcePreview);
        Assert.Equal("00000000", panel.PreviewHex!.Rows[0].Offset);
        panel.Dispose();
    }

    [Fact]
    public async Task Ordinary_text_wraps()
    {
        var text = await PreviewOf("readme.txt", "hello");

        Assert.True(text.WrapPreviewText);
        text.Dispose();
    }

    [Fact]
    public async Task An_archive_shows_what_is_inside_it()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "bundle.zip", FilePanelEntryKind.File, 512));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("bundle.zip")),
            FilePanelPreviewKind.Hex,
            "application/octet-stream",
            [0x50, 0x4B, 0x03, 0x04],
            isTruncated: true);
        client.MaterializedPath = Path.Combine(Path.GetTempPath(), "bundle.zip");
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            archiveReader: new StubArchiveReader(
            [
                new ArchiveEntryDescriptor("docs/guide.md", false, 120, 60),
                new ArchiveEntryDescriptor("src/main.c", false, 240, 100),
            ]));
        await panel.Initialization;

        panel.SelectedEntry = panel.Entries.Single();
        await panel.PreviewSelectedAsync();

        // Not merely present: a listing whose rows never arrive is an empty
        // panel with a summary under it.
        Assert.True(panel.HasTreePreview);
        Assert.Equal(2, panel.PreviewTree!.Nodes.Count);
        Assert.Equal(["docs", "src"], panel.PreviewTree.Nodes.Select(node => node.Name), StringComparer.Ordinal);
    }

    private sealed class StubArchiveReader(IReadOnlyList<ArchiveEntryDescriptor> entries)
        : IArchiveTableOfContents
    {
        public List<(int Offset, int MaximumEntries)> Requests { get; } = [];

        public bool Claims(string fileName) => true;

        public ValueTask<IReadOnlyList<ArchiveEntryDescriptor>?> ReadAsync(
            FilePreviewContent content,
            string fileName,
            int maximumEntries,
            CancellationToken cancellationToken,
            int offset = 0)
        {
            Requests.Add((offset, maximumEntries));
            return ValueTask.FromResult<IReadOnlyList<ArchiveEntryDescriptor>?>([.. entries.Skip(offset).Take(maximumEntries)]);
        }
    }

    [Fact]
    public async Task ArchivePagesKeepAllEntriesAccessibleWithoutAccumulatingTrees()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "bundle.zip", FilePanelEntryKind.File, 512));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("bundle.zip")), FilePanelPreviewKind.Hex,
            "application/octet-stream", [0x50, 0x4B, 0x03, 0x04], isTruncated: true);
        client.MaterializedPath = Path.Combine(Path.GetTempPath(), "bundle.zip");
        var reader = new StubArchiveReader([.. Enumerable.Range(0, 501)
            .Select(index => new ArchiveEntryDescriptor($"entry-{index:D3}.txt", false, 1, 1))]);
        using var panel = new FileRuntimePanelViewModel(PanelInstanceId.New(), "Files", client, archiveReader: reader);
        await panel.Initialization;
        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();
        Assert.Equal(250, panel.PreviewTree!.Nodes.Count);
        Assert.True(panel.HasNextArchivePage);
        await panel.ChangeArchivePageAsync(1);
        Assert.Equal(250, panel.PreviewTree!.Nodes.Count);
        Assert.True(panel.HasPreviousArchivePage);
        await panel.ChangeArchivePageAsync(1);
        Assert.Equal("entry-500.txt", Assert.Single(panel.PreviewTree!.Nodes).Name);
        Assert.False(panel.HasNextArchivePage);
        await panel.ChangeArchivePageAsync(-1);
        Assert.Equal(250, panel.PreviewTree!.Nodes.Count);
        Assert.Equal([0, 250, 500, 250], reader.Requests.Select(request => request.Offset));
        Assert.All(reader.Requests, request => Assert.Equal(251, request.MaximumEntries));
        await panel.RefreshAsync();
        Assert.False(panel.HasPreviousArchivePage);
        Assert.False(panel.HasNextArchivePage);
    }

    [Fact]
    public async Task A_switch_stays_where_the_reader_left_it_for_the_next_file()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "first.bin", FilePanelEntryKind.File, 3));
        client.Entries.Add(Entry(client.Root, "second.bin", FilePanelEntryKind.File, 3));
        using var panel = new FileRuntimePanelViewModel(PanelInstanceId.New(), "Files", client);
        await panel.Initialization;

        client.Preview = BinaryPreview(client.Root, "first.bin");
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "first.bin", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();
        panel.PreviewToggles.Single().IsOn = true;
        await panel.PreviewPresentation;
        Assert.True(panel.HasHexPreview);

        client.Preview = BinaryPreview(client.Root, "second.bin");
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "second.bin", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();

        // Someone reading bytes is reading bytes, not reading one file's bytes.
        Assert.True(panel.HasHexPreview);
        Assert.True(panel.PreviewToggles.Single().IsOn);
    }

    [Fact]
    public async Task A_file_that_does_not_offer_a_switch_is_unaffected_by_it()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "payload.bin", FilePanelEntryKind.File, 3));
        client.Entries.Add(Entry(client.Root, "people.csv", FilePanelEntryKind.File, 20));
        using var panel = new FileRuntimePanelViewModel(PanelInstanceId.New(), "Files", client);
        await panel.Initialization;

        client.Preview = BinaryPreview(client.Root, "payload.bin");
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "payload.bin", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();
        panel.PreviewToggles.Single().IsOn = true;
        await panel.PreviewPresentation;

        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("people.csv")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("name,city\nada,london\n"),
            isTruncated: false);
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "people.csv", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();

        Assert.False(panel.HasHexPreview);
        Assert.True(panel.HasTablePreview);
        Assert.Equal("As table", panel.PreviewToggles.Single().Label);
    }

    private static FilePanelPreview BinaryPreview(FilePanelLocation root, string name) =>
        new(
            root.Child(new FilePanelPathSegment(name)),
            FilePanelPreviewKind.Hex,
            "application/octet-stream",
            [1, 2, 3],
            isTruncated: false);

    [Fact]
    public async Task A_file_with_nothing_to_choose_shows_no_switches()
    {
        var panel = await PreviewOf("notes.md", "# Title", out var client);
        panel.PreviewToggles.Single().IsOn = true;

        client.Entries.Add(Entry(client.Root, "readme.txt", FilePanelEntryKind.File, 5));
        client.Preview = new FilePanelPreview(
            client.Root.Child(new FilePanelPathSegment("readme.txt")),
            FilePanelPreviewKind.Text,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("plain"),
            isTruncated: false);
        await panel.RefreshAsync();
        panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, "readme.txt", StringComparison.Ordinal));
        await panel.PreviewSelectedAsync();

        // The choices are the claiming previewer's; plain text offers none,
        // whatever was chosen for the file before it.
        Assert.Empty(panel.PreviewToggles);
        Assert.Equal("plain", panel.PreviewText);
        panel.Dispose();
    }

    [Fact]
    public async Task Everything_selected_is_downloaded_into_the_chosen_folder()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "report.pdf", FilePanelEntryKind.File, 12));
        client.Entries.Add(Entry(client.Root, "logs", FilePanelEntryKind.Directory, null));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;

        panel.SetSelectedEntries([.. panel.Entries.Select(entry => entry.Entry)]);
        var requests = panel.CreateDownloadRequests(
            Path.Combine(Path.GetTempPath(), "downloads"));

        // Folders come too: the transfer queue copies a tree, so the panel does
        // not have to walk one.
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("builtin.files.home", request.Destination.ProviderProfileId);
            Assert.Equal(FilePanelConflictPolicy.Replace, request.ConflictPolicy);
        });
        Assert.Contains(requests, request => request.Source.ToString().Contains("logs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_lands_at_the_path_that_was_chosen()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "report.pdf", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        panel.SetSelectedEntries([panel.Entries.Single().Entry]);

        var request = Assert.Single(panel.CreateDownloadRequests(
            Path.Combine(Path.GetTempPath(), "ghost", "downloads")));

        // The local provider is rooted at the filesystem root, so the chosen
        // folder addresses straight through as the path it is.
        var address = Assert.IsType<FilePanelAddress.Hierarchical>(request.Destination.Address);
        var segments = address.Path.Segments.Select(segment => segment.Value).ToArray();
        Assert.Equal("report.pdf", segments[^1]);
        Assert.Equal("downloads", segments[^2]);
        Assert.Equal("ghost", segments[^3]);
    }

    [Fact]
    public async Task Nothing_selected_is_nothing_to_download()
    {
        var client = new StubFilePanelClient();
        client.Entries.Add(Entry(client.Root, "report.pdf", FilePanelEntryKind.File, 12));
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;

        panel.SetSelectedEntries([]);

        Assert.False(panel.CanDownload);
        Assert.Throws<InvalidOperationException>(() => panel.CreateDownloadRequests("/tmp"));
    }

    [Fact]
    public async Task A_panel_opens_where_its_provider_says_it_opens()
    {
        var client = new StubFilePanelClient();
        var start = client.Root.Child(new FilePanelPathSegment("home"))
            .Child(new FilePanelPathSegment("terion"));
        client.SetStartLocation(start);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client);

        await panel.Initialization;

        // The provider reaches its whole root; it just does not open there.
        Assert.Equal(start.ToString(), panel.CurrentLocation?.ToString());
    }

    [Fact]
    public async Task A_file_anywhere_on_this_machine_can_be_uploaded()
    {
        var client = new StubFilePanelClient();
        var remote = client.AddProfile(
            "sftp.host",
            "dev.example",
            capabilities: FilePanelCapability.List | FilePanelCapability.StreamingWrite,
            family: FileProviderFamily.Sftp);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            new StubTransferQueue());
        await panel.Initialization;
        await panel.SelectProfileAsync(remote);

        var source = Path.Combine(Path.GetTempPath(), $"ghostshell-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(source, "payload");
        try
        {
            // The local provider reaches the whole filesystem now, so a file
            // outside the user's home folder is no longer out of bounds.
            var editor = panel.CreateUploadEditor(source);

            Assert.Equal(Path.GetFileName(source), editor.Source.Name);
            Assert.Equal("builtin.files.home", editor.Source.Location.ProviderProfileId);
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static Task<FileRuntimePanelViewModel> PreviewOf(
        string name,
        string content,
        FilePanelPreviewKind kind = FilePanelPreviewKind.Text) =>
        PreviewOf(name, content, out _, kind);

    private static Task<FileRuntimePanelViewModel> PreviewOf(
        string name,
        string content,
        out StubFilePanelClient client,
        FilePanelPreviewKind kind = FilePanelPreviewKind.Text)
    {
        var stub = new StubFilePanelClient();
        client = stub;
        stub.Entries.Add(Entry(stub.Root, name, FilePanelEntryKind.File, content.Length));
        stub.Preview = new FilePanelPreview(
            stub.Root.Child(new FilePanelPathSegment(name)),
            kind,
            "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(content),
            isTruncated: false);
        return Prepare(stub, name);

        static async Task<FileRuntimePanelViewModel> Prepare(
            StubFilePanelClient stub,
            string name)
        {
            var panel = new FileRuntimePanelViewModel(PanelInstanceId.New(), "Files", stub);
            await panel.Initialization;
            panel.SelectedEntry = panel.Entries.Single(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
            await panel.PreviewSelectedAsync();
            return panel;
        }
    }

    [Fact]
    public async Task The_auto_preview_caption_follows_the_threshold_setting()
    {
        var preferences = new InMemoryFilePreviewPreferences();
        var client = new StubFilePanelClient();
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Files",
            client,
            previewPreferences: preferences);
        await panel.Initialization;
        Assert.Equal("Automatically preview files < 2 MB", panel.AutoDownloadPreviewLabel);

        var announced = false;
        panel.PropertyChanged += (_, e) =>
            announced |= string.Equals(e.PropertyName, nameof(panel.AutoDownloadPreviewLabel), StringComparison.Ordinal);
        await preferences.ApplyAsync(
            preferences.Current with { AutoLoadThresholdBytes = 8 * 1024 * 1024 },
            CancellationToken.None);

        // A caption that promises a number must follow the number.
        Assert.True(announced);
        Assert.Equal("Automatically preview files < 8 MB", panel.AutoDownloadPreviewLabel);
    }

    internal sealed class StubFilePanelClient :
        IFilePanelClient,
        IFileProviderProfileRuntime,
        IFileContentSource
    {
        public StubFilePanelClient()
        {
            Root = new FilePanelLocation(
                "builtin.files.home",
                "local",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    "builtin.files.home",
                    "Home",
                    FileProviderFamily.Posix,
                    Root,
                    FilePanelCapability.List
                        | FilePanelCapability.Stat
                        | FilePanelCapability.RangedRead
                        | FilePanelCapability.StreamingWrite
                        | FilePanelCapability.CreateDirectory
                        | FilePanelCapability.Rename
                        | FilePanelCapability.Delete,
                    500,
                    1024 * 1024),
            ];
        }

        public FilePanelLocation Root { get; }

        /// <summary>
        /// Re-declares the only profile with a start location away from its
        /// root, the shape the local provider now has.
        /// </summary>
        public void SetStartLocation(FilePanelLocation start)
        {
            var existing = Profiles[0];
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    existing.Id,
                    existing.Name,
                    existing.Family,
                    existing.Root,
                    existing.Capabilities,
                    existing.MaximumPageSize,
                    existing.MaximumPreviewBytes,
                    start),
                .. Profiles.Skip(1),
            ];
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<FilePanelEntry> Entries { get; } = [];

        public List<FilePanelEntry> SearchEntries { get; } = [];

        public Channel<FilePanelResult<FilePanelChange>> WatchChanges { get; } =
            Channel.CreateUnbounded<FilePanelResult<FilePanelChange>>();

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; private set; }

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public event EventHandler? ProfilesChanged;

        public FilePanelError? ListError { get; set; }

        public TaskCompletionSource<FilePanelResult<FilePanelPage>>? ListCompletion { get; set; }

        public int ListCallCount { get; private set; }

        public FilePanelError? StatError { get; set; }

        public FilePanelError? CreateDirectoryError { get; set; }

        public FilePanelEntry? StatEntry { get; set; }

        public FilePanelPreview? Preview { get; set; }

        public TaskCompletionSource<FilePanelResult<FilePanelPreview>>? PreviewCompletion { get; set; }

        public FilePanelLocation? LastStatLocation { get; private set; }

        public FilePanelPreviewRequest? LastPreviewRequest { get; private set; }

        public int PreviewCallCount { get; private set; }

        public FilePanelDeleteRequest? LastDeleteRequest { get; private set; }

        public void EnableCapabilities(FilePanelCapability capabilities)
        {
            var existing = Profiles[0];
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    existing.Id,
                    existing.Name,
                    existing.Family,
                    existing.Root,
                    existing.Capabilities | capabilities,
                    existing.MaximumPageSize,
                    existing.MaximumPreviewBytes,
                    existing.StartLocation),
                .. Profiles.Skip(1),
            ];
        }

        public FileProviderProfileDescriptor AddProfile(
            string id,
            string name,
            bool prepend = false,
            FilePanelCapability capabilities = FilePanelCapability.List,
            long maximumPreviewBytes = 1024 * 1024,
            FileProviderFamily family = FileProviderFamily.Posix)
        {
            var root = new FilePanelLocation(
                id,
                id,
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            var profile = new FileProviderProfileDescriptor(
                id,
                name,
                family,
                root,
                capabilities,
                500,
                maximumPreviewBytes);
            Profiles = prepend ? [profile, .. Profiles] : [.. Profiles, profile];
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            return profile;
        }

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileProviderTestResult(true, "ok", profile.Name));

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken)
        {
            ListCallCount++;
            if (ListCompletion is not null)
            {
                return new ValueTask<FilePanelResult<FilePanelPage>>(
                    ListCompletion.Task.WaitAsync(cancellationToken));
            }

            if (ListError is not null)
            {
                return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Failure(ListError));
            }

            var offset = request.ContinuationToken is null
                ? 0
                : int.Parse(request.ContinuationToken, System.Globalization.CultureInfo.InvariantCulture);
            var eligible = Entries
                .Where(entry => request.ShowHidden || !entry.IsHidden)
                .ToArray();
            var listed = eligible
                .Skip(offset)
                .Take(request.PageSize)
                .ToArray();
            var nextOffset = offset + listed.Length;
            var continuation = nextOffset < eligible.Length
                ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(
                new FilePanelPage(listed, continuation)));
        }

        public async IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
            FilePanelSearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var entry in SearchEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((request.ShowHidden || !entry.IsHidden)
                    && entry.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase))
                {
                    yield return FilePanelResult<FilePanelEntry>.Success(entry);
                }
            }
        }

        public IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
            FilePanelWatchRequest request,
            CancellationToken cancellationToken) =>
            WatchChanges.Reader.ReadAllAsync(cancellationToken);

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken)
        {
            LastStatLocation = location;
            return ValueTask.FromResult(StatError is not null
                ? FilePanelResult<FilePanelEntry>.Failure(StatError)
                : FilePanelResult<FilePanelEntry>.Success(StatEntry ?? Entries.Single(
                    item => item.Location == location)));
        }

        /// <summary>The file whole-content calls hand back, when one is set.</summary>
        public string? MaterializedPath { get; set; }

        public int MaterializeCallCount { get; private set; }

        public ValueTask<FilePanelResult<FilePreviewContent>> OpenContentAsync(
            FilePanelLocation location,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            _ = location;
            _ = maximumBytes;
            _ = cancellationToken;
            MaterializeCallCount++;
            return ValueTask.FromResult(MaterializedPath is null
                ? FilePanelResult<FilePreviewContent>.Failure(new FilePanelError(
                    FilePanelErrorCode.NotFound,
                    "file_absent",
                    "No file.",
                    false))
                : FilePanelResult<FilePreviewContent>.Success(
                    FilePreviewContent.FromLocalFile(MaterializedPath)));
        }

        public async ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken)
        {
            LastPreviewRequest = request;
            PreviewCallCount++;
            if (PreviewCompletion is not null)
            {
                return await PreviewCompletion.Task.WaitAsync(cancellationToken);
            }

            return FilePanelResult<FilePanelPreview>.Success(
                Preview ?? new FilePanelPreview(
                    request.Location,
                    FilePanelPreviewKind.Text,
                    "text/plain; charset=utf-8",
                    [],
                    false));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CreateDirectoryError is null
                ? FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
                    request.Location,
                    "folder",
                    FilePanelEntryKind.Directory,
                    null,
                    null,
                    false))
                : FilePanelResult<FilePanelEntry>.Failure(CreateDirectoryError));

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
                request.Destination,
                "renamed",
                FilePanelEntryKind.File,
                0,
                null,
                false)));

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken)
        {
            LastDeleteRequest = request;
            Entries.RemoveAll(item => item.Location == request.Location);
            return ValueTask.FromResult(FilePanelResult<FilePanelDeleteReceipt>.Success(
                new FilePanelDeleteReceipt(request.Location, false)));
        }

        public void Dispose() => ProfilesChanged = null;
    }

    private sealed class StubTransferQueue(FilePanelError? enqueueError = null)
        : IFileTransferQueueClient
    {
        private readonly List<FilePanelTransferSnapshot> _transfers = [];

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers => [.. _transfers];

        public event EventHandler? TransfersChanged;

        public int SubscriberCount =>
            TransfersChanged?.GetInvocationList().Length ?? 0;

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken)
        {
            if (enqueueError is not null)
            {
                return ValueTask.FromResult(
                    FilePanelResult<FilePanelTransferSnapshot>.Failure(enqueueError));
            }

            var transfer = new FilePanelTransferSnapshot(
                FilePanelTransferId.New(),
                request,
                request.Destination,
                FilePanelTransferState.Queued,
                "Queued",
                0,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                null,
                null);
            _transfers.Insert(0, transfer);
            TransfersChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(
                FilePanelResult<FilePanelTransferSnapshot>.Success(transfer));
        }

        public void Transition(
            FilePanelTransferId id,
            FilePanelTransferState state,
            DateTimeOffset? completedAt = null,
            FilePanelError? error = null)
        {
            var index = _transfers.FindIndex(transfer => transfer.Id == id);
            Assert.True(index >= 0, "The test transfer must exist before it can transition.");
            _transfers[index] = _transfers[index] with
            {
                State = state,
                Stage = state.ToString(),
                Error = error,
                CompletedAt = completedAt,
            };
            TransfersChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SignalChanged() => TransfersChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
