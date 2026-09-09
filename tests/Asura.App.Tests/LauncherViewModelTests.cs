using System.Collections.Specialized;
using Asura.App.ViewModels;
using Asura.Core;
using FluentIcons.Common;

namespace Asura.App.Tests;

public sealed class LauncherViewModelTests
{
    [Fact]
    public void Catalog_projection_owns_sorted_bounded_previews_and_derived_state()
    {
        using var launcher = new LauncherViewModel(() => []);
        var terminalConnections = Enumerable.Range(0, 6)
            .Select(index => Connection($"Terminal {index}", SavedConnectionFamily.Terminal))
            .ToArray();
        var fileConnections = Enumerable.Range(0, 3)
            .Select(index => Connection($"File {index}", SavedConnectionFamily.Files))
            .ToArray();
        var databaseConnections =
            new[] { Connection("A database", SavedConnectionFamily.Database) };
        var screens = Enumerable.Range(0, 6)
            .Select(index => Screen($"Screen {index}"))
            .ToArray();

        launcher.ApplyCatalog(
            [Workspace("Operations")],
            terminalConnections,
            fileConnections,
            databaseConnections,
            screens);

        Assert.True(launcher.HasWorkspaces);
        Assert.True(launcher.HasConnections);
        Assert.Equal(10, launcher.TotalConnectionCount);
        Assert.Equal(8, launcher.ConnectionsPreview.Count);
        Assert.Equal("A database", launcher.ConnectionsPreview[0].Name);
        Assert.True(launcher.HasMoreConnectionsThanPreview);
        Assert.Equal(4, launcher.ScreensPreview.Count);
        Assert.True(launcher.HasMoreScreensThanPreview);
    }

    [Fact]
    public void Republishing_the_same_catalog_preserves_rows_and_collections()
    {
        using var launcher = new LauncherViewModel(() => []);
        var workspace = Workspace("Operations");
        var connection = Connection("Production", SavedConnectionFamily.Terminal);
        var screen = Screen("Deploy");
        launcher.ApplyCatalog([workspace], [connection], [], [], [screen]);
        var changes = 0;
        NotifyCollectionChangedEventHandler changed = (_, _) => changes++;
        launcher.Workspaces.CollectionChanged += changed;
        launcher.Connections.CollectionChanged += changed;
        launcher.Screens.CollectionChanged += changed;
        launcher.ConnectionsPreview.CollectionChanged += changed;
        launcher.ScreensPreview.CollectionChanged += changed;

        launcher.ApplyCatalog(
            [Workspace("Operations")],
            [Connection("Production", SavedConnectionFamily.Terminal)],
            [],
            [],
            [Screen("Deploy")]);

        Assert.Equal(0, changes);
        Assert.Same(workspace, Assert.Single(launcher.Workspaces));
        Assert.Same(connection, Assert.Single(launcher.Connections));
        Assert.Same(screen, Assert.Single(launcher.Screens));
    }

    [Fact]
    public void Search_query_publishes_the_current_snapshot_and_replaces_stale_selection()
    {
        IReadOnlyList<LauncherSearchResultViewModel> candidates =
        [
            Result("alpha", "Alpha server"),
            Result("beta", "Beta server"),
        ];
        using var launcher = new LauncherViewModel(() => candidates);
        launcher.RefreshSearchResults();
        launcher.SelectedSearchResult = launcher.SearchResults[0];

        launcher.SearchQuery = "beta";

        Assert.Equal("Beta server", Assert.Single(launcher.SearchResults).Title);
        Assert.Equal(
            new LauncherSearchTarget.Connection(new ConnectionId("beta")),
            launcher.SelectedSearchResult?.Target);
        Assert.Equal(
            "No commands or launch targets match ‘beta’.",
            launcher.SearchEmptyState);
    }

    [Fact]
    public void Refresh_preserves_selection_by_target_after_rows_change()
    {
        IReadOnlyList<LauncherSearchResultViewModel> candidates =
        [
            Result("alpha", "Alpha server"),
            Result("beta", "Beta server"),
        ];
        using var launcher = new LauncherViewModel(() => candidates);
        launcher.RefreshSearchResults();
        launcher.SelectedSearchResult = launcher.SearchResults.Single(
            item => item.Target is LauncherSearchTarget.Connection connection
                && connection.Id == new ConnectionId("beta"));
        candidates =
        [
            Result("alpha", "Alpha server"),
            Result("beta", "Renamed beta server"),
        ];

        launcher.RefreshSearchResults();

        Assert.Equal(
            new LauncherSearchTarget.Connection(new ConnectionId("beta")),
            launcher.SelectedSearchResult?.Target);
        Assert.Equal("Renamed beta server", launcher.SelectedSearchResult?.Title);
    }

    [Fact]
    public void Consecutive_refreshes_publish_only_the_latest_candidate_snapshot()
    {
        IReadOnlyList<LauncherSearchResultViewModel> candidates = [Result("old", "Old")];
        using var launcher = new LauncherViewModel(() => candidates);
        launcher.RefreshSearchResults();
        candidates = [Result("new", "New")];

        launcher.RefreshSearchResults();

        Assert.Equal("New", Assert.Single(launcher.SearchResults).Title);
        Assert.DoesNotContain(launcher.SearchResults, result => result.Title == "Old");
    }

    [Fact]
    public void Keyboard_selection_skips_unavailable_rows_and_confirms_the_available_target()
    {
        using var launcher = new LauncherViewModel(() =>
        [
            Result("unavailable", "Unavailable") with
            {
                IsAvailable = false,
                UnavailableReason = "Missing runtime",
            },
            Result("first", "First"),
            Result("second", "Second"),
        ]);
        launcher.RefreshSearchResults();

        launcher.SelectFirstAvailableSearchResult();
        Assert.Equal(
            new LauncherSearchTarget.Connection(new ConnectionId("first")),
            launcher.SelectedSearchResult?.Target);

        launcher.MoveSearchSelection(direction: 1);

        Assert.Equal(
            new LauncherSearchTarget.Connection(new ConnectionId("second")),
            launcher.ConfirmSearchSelection());
    }

    [Fact]
    public void Search_refresh_and_notifications_stay_on_the_callers_context()
    {
        var previousContext = SynchronizationContext.Current;
        var context = new SynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var launcher = new LauncherViewModel(() =>
            {
                Assert.Same(context, SynchronizationContext.Current);
                return [Result("alpha", "Alpha")];
            });
            launcher.PropertyChanged += (_, _) =>
                Assert.Same(context, SynchronizationContext.Current);

            launcher.RefreshSearchResults();

            Assert.Single(launcher.SearchResults);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void Workspace_isolation_toggle_requires_only_a_compatible_runtime()
    {
        var unavailable = Workspace(
            "Unavailable",
            isIsolated: false,
            isIsolationAvailable: false);
        var portable = Workspace(
            "Portable",
            isIsolated: true,
            isIsolationAvailable: false);

        Assert.False(unavailable.CanToggleIsolation);
        Assert.True(portable.CanToggleIsolation);

        portable.IsOpen = true;

        Assert.True(portable.CanToggleIsolation);
    }

    [Fact]
    public void Dispose_is_idempotent_and_rejects_further_work()
    {
        var sourceCalls = 0;
        var launcher = new LauncherViewModel(() =>
        {
            sourceCalls++;
            return [];
        });

        launcher.Dispose();
        launcher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => launcher.RefreshSearchResults());
        Assert.Throws<ObjectDisposedException>(() => launcher.SearchQuery = "query");
        Assert.Equal(0, sourceCalls);
    }

    private static LauncherWorkspaceViewModel Workspace(
        string name,
        bool isIsolated = false,
        bool isIsolationAvailable = true) =>
        new(
            new WorkspaceId(name.ToLowerInvariant()),
            revision: 1,
            name,
            "Description",
            "#336699",
            "OP",
            Symbol.Window,
            itemCount: 1,
            isIsolated,
            isIsolationAvailable);

    private static LauncherConnectionViewModel Connection(
        string name,
        SavedConnectionFamily family) =>
        new(
            new ConnectionId(name.ToLowerInvariant().Replace(' ', '-')),
            Revision: 1,
            name,
            family.ToString(),
            "Detail",
            "Ready",
            CanOpen: true,
            Tags: [],
            family);

    private static LauncherScreenViewModel Screen(string name) =>
        new(
            new ScreenId(name.ToLowerInvariant().Replace(' ', '-')),
            Revision: 1,
            name,
            "Description",
            "Single",
            PanelCount: 1,
            PreviewPanels: [],
            Summary: "One panel");

    private static LauncherSearchResultViewModel Result(string id, string title) =>
        new(
            new LauncherSearchTarget.Connection(new ConnectionId(id)),
            Symbol.Server,
            "CONNECTION · LOCAL",
            title,
            "Detail",
            "Open",
            IsAvailable: true,
            UnavailableReason: null,
            ["connection", id, title]);
}
