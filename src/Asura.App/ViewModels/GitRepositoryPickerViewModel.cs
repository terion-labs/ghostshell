using Asura.Core;
using Asura.Git;

namespace Asura.App.ViewModels;

/// <summary>One directory row in the repository picker.</summary>
public sealed class GitDirectoryEntryViewModel(GitDirectoryEntry entry, string parentPath)
{
    public string Name { get; } = entry.Name;

    public bool IsRepository { get; } = entry.IsRepository;

    public string FullPath { get; } =
        string.Equals(parentPath, "/", StringComparison.Ordinal)
            ? $"/{entry.Name}"
            : $"{parentPath}/{entry.Name}";

    public string AccessibleSummary =>
        IsRepository ? $"Repository {Name}" : $"Folder {Name}";
}

/// <summary>
/// Browses directories on the panel's connection target so a repository can
/// be chosen where it actually lives — locally or over SSH — instead of
/// through a picker that only sees this machine.
/// </summary>
public sealed class GitRepositoryPickerViewModel : ObservableObject
{
    private readonly IGitRepositoryClient _client;
    private readonly ConnectionProfile _connection;
    private readonly string? _connectionDisplayName;
    private readonly string? _initialPath;
    private string _currentPath = "";
    private string _pathInput = "";
    private IReadOnlyList<GitDirectoryEntryViewModel> _directories = [];
    private GitDirectoryEntryViewModel? _selectedDirectory;
    private bool _isLoading;
    private string? _issueMessage;

    public GitRepositoryPickerViewModel(
        IGitRepositoryClient client,
        ConnectionProfile connection,
        string? initialPath = null,
        string? connectionDisplayName = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connectionDisplayName = connectionDisplayName;
        _initialPath = initialPath;
    }

    public string ConnectionDisplayName =>
        _connectionDisplayName
        ?? (_connection.Endpoint is ConnectionEndpoint.Local ? "Local" : _connection.Name);

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(CanNavigateUp));
            }
        }
    }

    public string PathInput
    {
        get => _pathInput;
        set => SetProperty(ref _pathInput, value);
    }

    public IReadOnlyList<GitDirectoryEntryViewModel> Directories
    {
        get => _directories;
        private set
        {
            if (SetProperty(ref _directories, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public GitDirectoryEntryViewModel? SelectedDirectory
    {
        get => _selectedDirectory;
        set => SetProperty(ref _selectedDirectory, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !IsLoading && Directories.Count == 0;

    public string? IssueMessage
    {
        get => _issueMessage;
        private set
        {
            if (SetProperty(ref _issueMessage, value))
            {
                OnPropertyChanged(nameof(HasIssue));
            }
        }
    }

    public bool HasIssue => IssueMessage is not null;

    public bool CanNavigateUp =>
        CurrentPath.Length > 0 && !string.Equals(CurrentPath, "/", StringComparison.Ordinal);

    /// <summary>
    /// The path the dialog should hand back: the highlighted directory when
    /// one is chosen, otherwise the directory being viewed.
    /// </summary>
    public string? ResolveSelection() =>
        SelectedDirectory?.FullPath ?? (CurrentPath.Length > 0 ? CurrentPath : null);

    /// <summary>
    /// The first load tries the caller's path and falls back to the target's
    /// home directory when that path does not resolve to a directory.
    /// </summary>
    public async Task LoadInitialAsync()
    {
        if (!string.IsNullOrWhiteSpace(_initialPath))
        {
            await LoadAsync(_initialPath);
            if (!HasIssue)
            {
                return;
            }
        }

        await LoadAsync("");
    }

    public Task NavigateToInputAsync() => LoadAsync(PathInput);

    public Task NavigateUpAsync()
    {
        if (!CanNavigateUp)
        {
            return Task.CompletedTask;
        }

        var separator = CurrentPath.LastIndexOf('/');
        return LoadAsync(separator <= 0 ? "/" : CurrentPath[..separator]);
    }

    public Task EnterAsync(GitDirectoryEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return LoadAsync(entry.FullPath);
    }

    public async Task LoadAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IsLoading = true;
        IssueMessage = null;
        try
        {
            var result = await _client.ListDirectoriesAsync(
                _connection,
                path,
                CancellationToken.None);
            if (result is GitResult<GitDirectoryListing>.Failure failure)
            {
                IssueMessage = failure.Error.Message;
                return;
            }

            var listing = ((GitResult<GitDirectoryListing>.Success)result).Value;
            CurrentPath = listing.Path;
            PathInput = listing.Path;
            Directories = [.. listing.Directories
                .Select(entry => new GitDirectoryEntryViewModel(entry, listing.Path))];
            SelectedDirectory = null;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
