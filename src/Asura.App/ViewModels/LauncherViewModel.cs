using System.Collections.ObjectModel;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns the launcher's catalog projection, bounded home previews, search, and
/// selection. The window supplies one synchronous candidate snapshot per
/// refresh; launch execution and other side effects remain with that host.
/// </summary>
public sealed class LauncherViewModel : ObservableObject, IDisposable
{
    private const int HomePreviewConnectionCount = 8;
    private const int HomePreviewScreenCount = 4;
    private Func<IReadOnlyList<LauncherSearchResultViewModel>>? _candidateSource;
    private string _searchQuery = string.Empty;
    private LauncherSearchResultViewModel? _selectedSearchResult;
    private bool _disposed;

    public LauncherViewModel(
        Func<IReadOnlyList<LauncherSearchResultViewModel>> candidateSource)
    {
        _candidateSource = candidateSource
            ?? throw new ArgumentNullException(nameof(candidateSource));
    }

    public ObservableCollection<LauncherWorkspaceViewModel> Workspaces { get; } = [];

    public ObservableCollection<LauncherConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<LauncherConnectionViewModel> FileConnections { get; } = [];

    public ObservableCollection<LauncherConnectionViewModel> DatabaseConnections { get; } = [];

    public ObservableCollection<LauncherScreenViewModel> Screens { get; } = [];

    public ObservableCollection<LauncherConnectionViewModel> ConnectionsPreview { get; } = [];

    public ObservableCollection<LauncherScreenViewModel> ScreensPreview { get; } = [];

    public ObservableCollection<LauncherSearchResultViewModel> SearchResults { get; } = [];

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool HasNoWorkspaces => !HasWorkspaces;

    public bool HasConnections => TotalConnectionCount > 0;

    public bool HasNoConnections => !HasConnections;

    public bool HasTerminalConnections => Connections.Count > 0;

    public bool HasFileConnections => FileConnections.Count > 0;

    public bool HasDatabaseConnections => DatabaseConnections.Count > 0;

    public int TotalConnectionCount =>
        Connections.Count + FileConnections.Count + DatabaseConnections.Count;

    public bool HasScreens => Screens.Count > 0;

    public bool HasNoScreens => !HasScreens;

    public bool HasMoreConnectionsThanPreview => TotalConnectionCount > ConnectionsPreview.Count;

    public bool HasMoreScreensThanPreview => Screens.Count > ScreensPreview.Count;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool HasNoSearchResults => !HasSearchResults;

    public string SearchEmptyState => string.IsNullOrWhiteSpace(SearchQuery)
        ? "No commands or saved launch targets are available."
        : $"No commands or launch targets match ‘{SearchQuery.Trim()}’.";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            ThrowIfDisposed();
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(SearchEmptyState));
                RefreshSearchResults(preserveSelection: false);
            }
        }
    }

    public LauncherSearchResultViewModel? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            ThrowIfDisposed();
            SetProperty(ref _selectedSearchResult, value);
        }
    }

    public void ApplyCatalog(
        IReadOnlyList<LauncherWorkspaceViewModel> workspaces,
        IReadOnlyList<LauncherConnectionViewModel> connections,
        IReadOnlyList<LauncherConnectionViewModel> fileConnections,
        IReadOnlyList<LauncherConnectionViewModel> databaseConnections,
        IReadOnlyList<LauncherScreenViewModel> screens)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(fileConnections);
        ArgumentNullException.ThrowIfNull(databaseConnections);
        ArgumentNullException.ThrowIfNull(screens);

        ReplaceIfChanged(Workspaces, workspaces, static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(Connections, connections, static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(FileConnections, fileConnections, static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            DatabaseConnections,
            databaseConnections,
            static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(Screens, screens, static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            ConnectionsPreview,
            [.. Connections
                .Concat(FileConnections)
                .Concat(DatabaseConnections)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(HomePreviewConnectionCount)],
            static (a, b) => a.PresentsSameAs(b));
        ReplaceIfChanged(
            ScreensPreview,
            [.. Screens.Take(HomePreviewScreenCount)],
            static (a, b) => a.PresentsSameAs(b));

        NotifyCatalogStateChanged();
    }

    public void RefreshSearchResults(bool preserveSelection = true)
    {
        ThrowIfDisposed();
        var selectedTarget = preserveSelection ? SelectedSearchResult?.Target : null;
        var candidates = _candidateSource!();
        var results = LauncherSearchProjection.Search(SearchQuery, candidates);

        // A refresh that presents the same rows must not rebuild the list. That
        // would destroy realized controls and move the row under the pointer.
        if (!PresentsSameResults(SearchResults, results))
        {
            Replace(SearchResults, results);
            SelectedSearchResult = LauncherSearchProjection.ResolveAvailableSelection(
                results,
                selectedTarget);
        }

        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(SearchEmptyState));
    }

    public void SelectFirstAvailableSearchResult()
    {
        ThrowIfDisposed();
        var index = LauncherSearchProjection.FindNextAvailableIndex(
            SearchResults,
            currentIndex: -1,
            direction: 1);
        SelectedSearchResult = index < 0 ? null : SearchResults[index];
    }

    public void MoveSearchSelection(int direction)
    {
        ThrowIfDisposed();
        var currentIndex = SelectedSearchResult is null
            ? -1
            : SearchResults.IndexOf(SelectedSearchResult);
        var nextIndex = LauncherSearchProjection.FindNextAvailableIndex(
            SearchResults,
            currentIndex,
            direction);
        SelectedSearchResult = nextIndex < 0 ? null : SearchResults[nextIndex];
    }

    public LauncherSearchTarget? ConfirmSearchSelection()
    {
        ThrowIfDisposed();
        return LauncherSearchProjection.ConfirmSelection(SelectedSearchResult);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _candidateSource = null;
    }

    private void NotifyCatalogStateChanged()
    {
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(HasNoConnections));
        OnPropertyChanged(nameof(HasTerminalConnections));
        OnPropertyChanged(nameof(HasFileConnections));
        OnPropertyChanged(nameof(HasDatabaseConnections));
        OnPropertyChanged(nameof(TotalConnectionCount));
        OnPropertyChanged(nameof(HasScreens));
        OnPropertyChanged(nameof(HasNoScreens));
        OnPropertyChanged(nameof(HasMoreConnectionsThanPreview));
        OnPropertyChanged(nameof(HasMoreScreensThanPreview));
    }

    private static bool PresentsSameResults(
        IReadOnlyList<LauncherSearchResultViewModel> current,
        IReadOnlyList<LauncherSearchResultViewModel> candidate)
    {
        if (current.Count != candidate.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!current[index].PresentsSameAs(candidate[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReplaceIfChanged<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> values,
        Func<T, T, bool> presentsSame)
    {
        if (target.Count == values.Count)
        {
            var unchanged = true;
            for (var index = 0; index < values.Count; index++)
            {
                if (!presentsSame(target[index], values[index]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        Replace(target, values);
    }

    private static void Replace<T>(
        ObservableCollection<T> target,
        IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
