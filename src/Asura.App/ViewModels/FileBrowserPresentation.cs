using System.Windows.Input;
using Asura.Application;

namespace Asura.App.ViewModels;

public enum FileBrowserViewMode
{
    Details,
    List,
    Grid,
}

public enum FileEntrySortField
{
    Name,
    Kind,
    Size,
    Modified,
}

public enum FileEntrySortDirection
{
    Ascending,
    Descending,
}

public enum FileOperationIssueKind
{
    Validation,
    Configuration,
    PermissionDenied,
    AuthenticationRequired,
    Offline,
    QuotaExceeded,
    Conflict,
    Stale,
    Unsupported,
    Partial,
    Cancelled,
    Unexpected,
}

public enum FileBrowserContentState
{
    Loading,
    Ready,
    EmptyLocation,
    SearchNoResults,
    RecoverableError,
    AccessRequired,
    Unsupported,
    Unavailable,
}

/// <summary>
/// Provider-independent directory presentation. Keeping this state explicit prevents an empty
/// folder, an empty search, and an inaccessible location from collapsing into the same message.
/// </summary>
public sealed record FileBrowserContentPresentation(
    FileBrowserContentState State,
    string Title,
    string Message,
    string SuggestedAction,
    string AccessibleName,
    bool CanRetry)
{
    public bool IsError => State is FileBrowserContentState.RecoverableError
        or FileBrowserContentState.AccessRequired
        or FileBrowserContentState.Unsupported
        or FileBrowserContentState.Unavailable;

    public static FileBrowserContentPresentation Resolve(
        bool isLoading,
        bool hasLoadedListing,
        int loadedCount,
        int visibleCount,
        string filter,
        FileOperationIssue? issue,
        string? errorMessage,
        bool canCreateFolder,
        bool hasCurrentLocation)
    {
        if (isLoading || (!hasLoadedListing && issue is null))
        {
            return new FileBrowserContentPresentation(
                FileBrowserContentState.Loading,
                "Loading files",
                "Reading this location from the selected provider.",
                string.Empty,
                "File Viewer loading",
                CanRetry: false);
        }

        if (issue is not null)
        {
            var state = issue.Kind switch
            {
                FileOperationIssueKind.PermissionDenied
                    or FileOperationIssueKind.AuthenticationRequired =>
                    FileBrowserContentState.AccessRequired,
                FileOperationIssueKind.Unsupported => FileBrowserContentState.Unsupported,
                _ when issue.CanRetry => FileBrowserContentState.RecoverableError,
                _ => FileBrowserContentState.Unavailable,
            };
            var accessibleName = state switch
            {
                FileBrowserContentState.AccessRequired => "File Viewer access required",
                FileBrowserContentState.Unsupported => "File Viewer location unsupported",
                FileBrowserContentState.RecoverableError => "File Viewer recoverable error",
                _ => "File Viewer unavailable",
            };
            return new FileBrowserContentPresentation(
                state,
                issue.Title,
                string.IsNullOrWhiteSpace(errorMessage) ? issue.Message : errorMessage,
                issue.SuggestedAction,
                accessibleName,
                issue.CanRetry && hasCurrentLocation);
        }

        if (loadedCount == 0)
        {
            return new FileBrowserContentPresentation(
                FileBrowserContentState.EmptyLocation,
                "This location is empty",
                "No files or folders are stored here.",
                canCreateFolder
                    ? "Create a folder here, or choose another location."
                    : "Choose another location.",
                "File Viewer empty location",
                CanRetry: false);
        }

        if (visibleCount == 0 && !string.IsNullOrWhiteSpace(filter))
        {
            return new FileBrowserContentPresentation(
                FileBrowserContentState.SearchNoResults,
                "No matching items",
                $"No files or folders match “{filter.Trim()}”.",
                "Clear or change the filter to see this location's items.",
                "File Viewer search has no results",
                CanRetry: false);
        }

        return new FileBrowserContentPresentation(
            FileBrowserContentState.Ready,
            string.Empty,
            string.Empty,
            string.Empty,
            "File Viewer contents",
            CanRetry: false);
    }
}

public sealed class AsyncActionCommand(
    Func<Task> execute,
    Func<bool> canExecute) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        _ = parameter;
        return !_isExecuting && canExecute();
    }

    public async void Execute(object? parameter)
    {
        _ = parameter;
        if (!CanExecute(null))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A provider-independent problem that the File Viewer can render without interpreting
/// protocol-specific error strings.
/// </summary>
public sealed record FileOperationIssue(
    FileOperationIssueKind Kind,
    string Title,
    string Message,
    string SuggestedAction,
    bool CanRetry)
{
    public static FileOperationIssue FromProvider(FilePanelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            FilePanelErrorCode.AccessDenied => Create(
                FileOperationIssueKind.PermissionDenied,
                "Permission denied",
                error,
                "Check this account's permissions or choose another location."),
            FilePanelErrorCode.AuthenticationRequired => Create(
                    FileOperationIssueKind.AuthenticationRequired,
                    "Authentication failed",
                    error,
                    "Check this connection's authentication settings, then retry.",
                    canRetry: true),
            FilePanelErrorCode.CertificateRejected
                or FilePanelErrorCode.HostKeyRejected
                or FilePanelErrorCode.HostKeyUnknown
                or FilePanelErrorCode.HostKeyChanged
                or FilePanelErrorCode.HostKeyStoreInvalid => Create(
                    FileOperationIssueKind.AuthenticationRequired,
                    "Trust required",
                    error,
                    "Review this connection's certificate or host-key trust, then retry.",
                    canRetry: true),
            FilePanelErrorCode.Offline => Create(
                FileOperationIssueKind.Offline,
                "Provider offline",
                error,
                "Check the connection, then retry.",
                canRetry: true),
            FilePanelErrorCode.QuotaExceeded => Create(
                FileOperationIssueKind.QuotaExceeded,
                "Storage limit reached",
                error,
                "Free space or choose another destination."),
            FilePanelErrorCode.AlreadyExists => Create(
                FileOperationIssueKind.Conflict,
                "Destination already exists",
                error,
                "Choose Skip, Replace, or Keep Both before retrying.",
                canRetry: true),
            FilePanelErrorCode.SharingViolation => Create(
                FileOperationIssueKind.Conflict,
                "File is in use",
                error,
                "Close the application or process using the item, then retry.",
                canRetry: true),
            FilePanelErrorCode.DirectoryNotEmpty => Create(
                FileOperationIssueKind.Conflict,
                "Folder is not empty",
                error,
                "Remove the folder contents first, or choose an explicitly recursive delete.",
                canRetry: true),
            FilePanelErrorCode.Conflict => Create(
                FileOperationIssueKind.Conflict,
                "File conflict",
                error,
                "Refresh and choose whether to replace, rename, or skip.",
                canRetry: true),
            FilePanelErrorCode.PreconditionFailed => Create(
                FileOperationIssueKind.Stale,
                "Item changed",
                error,
                "Refresh before applying the operation again.",
                canRetry: true),
            FilePanelErrorCode.UnsupportedCapability
                or FilePanelErrorCode.LinkNotAllowed
                or FilePanelErrorCode.RangeNotSatisfiable => Create(
                    FileOperationIssueKind.Unsupported,
                    "Operation unsupported",
                    error,
                    "Choose an operation supported by this provider."),
            FilePanelErrorCode.PartialTransfer
                or FilePanelErrorCode.UnexpectedEndOfStream => Create(
                    FileOperationIssueKind.Partial,
                    "Operation incomplete",
                    error,
                    "Retry and verify the destination before continuing.",
                    canRetry: true),
            FilePanelErrorCode.Cancelled => Create(
                FileOperationIssueKind.Cancelled,
                "Operation cancelled",
                error,
                "Run the operation again when ready.",
                canRetry: true),
            _ => Create(
                FileOperationIssueKind.Unexpected,
                "File operation failed",
                error,
                error.Retryable
                    ? "Retry the operation. If it keeps failing, inspect the provider settings."
                    : "Inspect the provider settings and try again."),
        };
    }

    public static FileOperationIssue Validation(string message) => new(
        FileOperationIssueKind.Validation,
        "Check the file details",
        message,
        "Correct the highlighted value and try again.",
        CanRetry: true);

    public static FileOperationIssue Configuration(string message) => new(
        FileOperationIssueKind.Configuration,
        "File provider unavailable",
        message,
        "Choose or configure a file-provider profile.",
        CanRetry: false);

    public static FileOperationIssue Unexpected(string message) => new(
        FileOperationIssueKind.Unexpected,
        "File operation failed",
        message,
        "Retry the operation. If it keeps failing, inspect the provider settings.",
        CanRetry: true);

    private static FileOperationIssue Create(
        FileOperationIssueKind kind,
        string title,
        FilePanelError error,
        string suggestedAction,
        bool? canRetry = null) => new(
            kind,
            title,
            error.Message,
            suggestedAction,
            canRetry ?? error.Retryable);
}

/// <summary>
/// Selected-entry metadata. <see cref="IsStatBacked"/> distinguishes a fresh provider stat
/// from the bounded metadata returned as part of a directory listing.
/// </summary>
public sealed class FileEntryMetadataViewModel
{
    public FileEntryMetadataViewModel(FilePanelEntry entry, bool isStatBacked)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        IsStatBacked = isStatBacked;
    }

    public FilePanelEntry Entry { get; }

    public bool IsStatBacked { get; }

    public string Name => Entry.Name;

    public string Kind => Entry.Kind.ToString();

    public string Size => Entry.Kind == FilePanelEntryKind.Directory
        ? "Folder"
        : FileEntryViewModel.FormatSize(Entry.Size);

    public string Modified => Entry.LastModifiedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";

    public string Location => FileLocationPresentation.Display(Entry.Location);

    public string Version => Entry.Location.Version ?? "Unavailable";

    public string Source => IsStatBacked ? "Live provider metadata" : "Listing metadata";
}
