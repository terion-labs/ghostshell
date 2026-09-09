using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Asura.Application;
using Asura.Application.Previews;
using Asura.Core;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Asura.App.ViewModels;

public sealed class FileEntryViewModel
{
    public FileEntryViewModel(FilePanelEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public FilePanelEntry Entry { get; }

    public string Name => Entry.Name;

    public string Kind => Entry.Kind.ToString();

    public string Size => Entry.Kind == FilePanelEntryKind.Directory
        ? "Folder"
        : FormatSize(Entry.Size);

    public string Modified => Entry.LastModifiedAt?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";

    public bool IsDirectory => Entry.Kind == FilePanelEntryKind.Directory;

    public bool IsLink => Entry.Kind == FilePanelEntryKind.Link;

    internal static string FormatSize(long? size)
    {
        if (size is null)
        {
            return "Unknown";
        }

        return ByteSize.Format(size.Value);
    }
}

public sealed class FileRuntimePanelViewModel : RuntimePanelViewModel, IPanelNotificationSource
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(2);
    private const double PreviewMinimumWidth = 220;
    private const double PreviewSplitterThickness = 5;
    private const int DefaultPreviewBytes = 256 * 1024;
    private const int MaximumFormattedBinaryBytes = 16 * 1024;
    private readonly IFilePanelClient _client;
    private readonly ConnectionProfile _connection;
    private readonly IHostedFilePanelClient? _hostedClient;
    private readonly IFileTransferQueueClient? _transferQueue;
    private readonly object _transferNotificationGate = new();
    private readonly HashSet<FilePanelTransferId> _notifiedTransfers = [];
    private long _notificationSequence;
    private readonly IFileProviderProfileRuntime? _profileRuntime;
    private readonly string? _initialProfileId;
    private readonly FilePanelLocation? _initialLocation;
    private readonly string? _initialLocationText;
    private readonly FileProviderProfileId? _recoveryProfileId;
    private readonly object _initializationGate = new();
    private readonly object _initialSelectionGate = new();
    private readonly AsyncActionCommand _retryCommand;
    private readonly AsyncActionCommand _nextPageCommand;
    private readonly AsyncActionCommand _previousPageCommand;
    private readonly List<string?> _listingPages = [null];
    private int _listingPageIndex;
    private TaskCompletionSource? _searchPageRequest;
    private readonly AsyncActionCommand _nextArchivePageCommand;
    private readonly AsyncActionCommand _previousArchivePageCommand;
    private FilePanelLocation? _archiveLocation;
    private int _archiveOffset;
    private bool _archiveHasMore;
    private readonly List<FilePanelEntry> _allEntries = [];
    private readonly List<FilePanelEntry> _searchEntries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _initialization = Task.CompletedTask;
    private Task _initialSelection = Task.CompletedTask;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _watch;
    private CancellationTokenSource? _preview;
    private CancellationTokenSource? _metadata;
    private FileProviderProfileDescriptor? _selectedProfile;
    private FileEntryViewModel? _selectedEntry;
    private FileEntryMetadataViewModel? _selectedMetadata;
    private FilePanelLocation? _currentLocation;
    private FilePanelLocation? _pendingInitialBindingLocation;
    private string _locationText = string.Empty;
    private string _filter = string.Empty;
    private string? _continuationToken;
    private string _status = "Preparing file provider";
    private string _shortStatus = "…";
    private FileOperationIssue? _contentIssue;
    private FileOperationIssue? _operationIssue;
    private FileOperationIssue? _metadataIssue;
    private FileOperationIssue? _previewIssue;
    private string? _previewText;
    private string _previewTitle = "Preview";
    private Bitmap? _previewImage;
    private bool _showHidden;
    private bool _isLoading;
    private bool _isSearchLoading;
    private bool _isPreviewLoading;
    private bool _isMetadataLoading;
    private bool _isPreviewVisible = true;
    private FileEntrySortField _sortField = FileEntrySortField.Modified;
    private FileEntrySortDirection _sortDirection = FileEntrySortDirection.Descending;
    private FileBrowserViewMode _viewMode = FileBrowserViewMode.Details;
    private GridLength _fileNameColumnWidth = new(1, GridUnitType.Star);
    private GridLength _fileSizeColumnWidth = new(90);
    private GridLength _fileModifiedColumnWidth = new(140);
    private GridLength _fileListColumnWidth = new(3, GridUnitType.Star);
    private GridLength _previewColumnWidth = new(2, GridUnitType.Star);
    private GridLength _hiddenPreviewColumnWidth = new(2, GridUnitType.Star);
    private bool _initialSelectionPending;
    private bool _initialSelectionRetryRequested;
    private bool _hasLoadedListing;
    private volatile bool _initializationStarted;
    private bool _disposed;
    private Task _searchCompletion = Task.CompletedTask;

    private bool _autoDownloadPreviews = true;
    private FileEntryViewModel? _deferredPreviewEntry;
    private FileEntryViewModel? _requestedPreviewEntry;

    /// <summary>
    /// Files whose preview the user has already asked for, keyed by the
    /// location's full identity — profile, address, and version. Asking again
    /// for a file you just previewed is the shortcut wearing out its welcome,
    /// and a version in the key means an edited file is asked about afresh.
    /// </summary>
    private readonly HashSet<string> _grantedPreviews = new(StringComparer.Ordinal);
    private readonly IDatabasePanelClient? _databaseClient;
    private readonly IInMemoryDatabaseRegistry? _databaseRegistry;
    private readonly IFilePreviewPreferences? _previewPreferences;
    private readonly IImagePreviewDecoder? _imageDecoder;
    private readonly IPdfPreviewRenderer? _pdfRenderer;
    private FilePreviewContent? _pdfContent;
    private BrowserAddress? _htmlAddress;
    private int _pdfPageIndex;
    private int _pdfPageCount;
    private readonly IFileContentSource? _contentSource;
    private readonly IArchiveTableOfContents? _archiveReader;
    private readonly FilePreviewCatalog _previewers;

    /// <summary>
    /// The preview last read, kept so a switch can be flipped without asking
    /// the provider — or the network — for the same bytes again.
    /// </summary>
    private FilePanelPreview? _lastPreview;

    /// <summary>
    /// How the reader chose to be shown things, kept across files. Someone who
    /// turns on the bytes is reading bytes, not reading one file's bytes, and
    /// having to ask again for every file is the sort of thing that makes a
    /// switch not worth using.
    /// </summary>
    private readonly Dictionary<string, bool> _previewToggleState =
        new(StringComparer.Ordinal);

    private bool _markdownRendering;
    private bool _wrapPreviewText = true;
    private PreviewTableViewModel? _previewTable;
    private PreviewTreeViewModel? _previewTree;
    private BinaryPreviewRendering? _previewBinary;
    private PreviewHexViewModel? _previewHex;
    private DatabaseRuntimePanelViewModel? _databasePreview;

    /// <summary>
    /// The registration serving a remote database preview from memory, so
    /// closing the preview can release the image. Null while the previewed
    /// database is a local file opened in place.
    /// </summary>
    private string? _databaseMemoryRegistration;

    public FileRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IFilePanelClient client,
        IFileTransferQueueClient? transferQueue = null,
        FileProviderProfileId? initialProfileId = null,
        FilePanelLocation? initialLocation = null,
        string? initialLocationText = null,
        bool deferInitialization = false,
        ConnectionProfile? connection = null,
        IDatabasePanelClient? databaseClient = null,
        IImagePreviewDecoder? imageDecoder = null,
        IPdfPreviewRenderer? pdfRenderer = null,
        IArchiveTableOfContents? archiveReader = null,
        FilePreviewCatalog? previewers = null,
        IInMemoryDatabaseRegistry? databaseRegistry = null,
        IFilePreviewPreferences? previewPreferences = null,
        FileTransferClipboard? clipboard = null,
        FileProviderProfileId? recoveryProfileId = null)
        : base(id, PanelKind.FileViewer, title, "Files")
    {
        _clipboard = clipboard;
        _recoveryProfileId = recoveryProfileId;
        _clipboard?.Changed += OnTransferClipboardChanged;

        _client = client ?? throw new ArgumentNullException(nameof(client));
        // Both are optional: a build without database drivers, or a client that
        // cannot hand out a real path, simply previews databases as bytes.
        _databaseClient = databaseClient;
        _imageDecoder = imageDecoder;
        _archiveReader = archiveReader;
        _previewers = previewers ?? new FilePreviewCatalog();
        // The row of switches appears and disappears with the collection, and
        // a bound bool over a collection count is only read once unless the
        // change is announced.
        PreviewToggles.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(HasPreviewToggles));
        _pdfRenderer = pdfRenderer;
        _databaseRegistry = databaseRegistry;
        _previewPreferences = previewPreferences;
        _previewPreferences?.Changed += OnPreviewPreferencesChanged;

        _contentSource = client as IFileContentSource;
        _connection = connection ?? BuiltInConnections.Local;
        _retryCommand = new AsyncActionCommand(
            () => RetryAsync(),
            () => CanRetryContentState);
        _nextPageCommand = new AsyncActionCommand(() => NextPageAsync(), () => HasNextPage);
        _previousPageCommand = new AsyncActionCommand(() => PreviousPageAsync(), () => HasPreviousPage && !IsLoading);
        _nextArchivePageCommand = new AsyncActionCommand(() => ChangeArchivePageAsync(1), () => HasNextArchivePage && !IsPreviewLoading);
        _previousArchivePageCommand = new AsyncActionCommand(() => ChangeArchivePageAsync(-1), () => HasPreviousArchivePage && !IsPreviewLoading);
        _hostedClient = client as IHostedFilePanelClient;
        _transferQueue = transferQueue;
        _profileRuntime = client as IFileProviderProfileRuntime;
        if (initialProfileId is { } profileId
            && initialLocation is not null
            && !string.Equals(profileId.Value, initialLocation.ProviderProfileId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The restored file location must belong to the initial provider profile.",
                nameof(initialLocation));
        }

        _initialLocation = initialLocation;
        _initialLocationText = initialLocationText;
        _initialProfileId = initialLocation?.ProviderProfileId ?? initialProfileId?.Value;
        _initialSelectionPending = _initialProfileId is not null;
        Replace(Profiles, client.Profiles);
        if (_hostedClient is not null)
        {
            _hostedClient.ProfilesChanged += OnProfilesChanged;
        }
        else
        {
            _profileRuntime?.ProfilesChanged += OnProfilesChanged;
        }

        _transferQueue?.TransfersChanged += OnTransfersChanged;

        RebuildActions();
        if (!deferInitialization)
        {
            _ = StartInitialization();
        }
    }

    public ObservableCollection<FileProviderProfileDescriptor> Profiles { get; } = [];

    public ObservableCollection<FileEntryViewModel> Entries { get; } = [];

    public Task Initialization
    {
        get
        {
            lock (_initializationGate)
            {
                return _initialization;
            }
        }
    }

    public Task StartInitialization()
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializationStarted)
            {
                return _initialization;
            }

            _initializationStarted = true;
            _initialization = InitializeAsync();
            return _initialization;
        }
    }

    public IHostedFilePanelClient? HostedClient => _hostedClient;

    /// <summary>
    /// Raised when one of this panel's transfers completes or fails. Like the
    /// terminal notification source, this event is raised on the queue's
    /// delivery thread; the shell owns UI-thread marshalling.
    /// </summary>
    public event EventHandler<PanelNotificationEvent>? NotificationReceived;

    public ConnectionId ConnectionId => _connection.Id;

    public FileProviderProfileId? RecoveryProfileId
    {
        get
        {
            if (_recoveryProfileId is { } recoveryProfileId)
            {
                return recoveryProfileId;
            }

            var profileId = SelectedProfile?.Id ?? CurrentLocation?.ProviderProfileId;
            return profileId is null ? null : new FileProviderProfileId(profileId);
        }
    }

    public string ConnectionDisplayName =>
        SelectedProfile is null
            ? (_connection.Endpoint is ConnectionEndpoint.Local ? "Local" : _connection.Name)
            : string.Equals(SelectedProfile.Id, BuiltInFileProviders.HomeId.Value
, StringComparison.Ordinal) ? "Local"
                : SelectedProfile.Name;

    public bool UsesConnection(ConnectionId connectionId)
    {
        if (string.Equals(SelectedProfile?.Id, BuiltInFileProviders.HomeId.Value, StringComparison.Ordinal))
        {
            return connectionId == _connection.Id
                && _connection.Endpoint is ConnectionEndpoint.Local;
        }

        return string.Equals(SelectedProfile?.Id, ConnectionFileProviderProfiles.Id(connectionId).Value, StringComparison.Ordinal);
    }

    public bool UsesProfile(FileProviderProfileId profileId) => string.Equals(SelectedProfile?.Id, profileId.Value
, StringComparison.Ordinal) || (SelectedProfile is null && string.Equals(_initialProfileId, profileId.Value, StringComparison.Ordinal));

    public FileProviderProfileDescriptor? SelectedProfile
    {
        get => _selectedProfile;
        private set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(ConnectionDisplayName));
                NotifyFileInteractionStateChanged();
                // The choice follows the transport: remote filesystems and
                // Docker resources transfer preview bytes onto the host;
                // ordinary local files do not.
                OnPropertyChanged(nameof(RequiresHostTransferForPreview));
                OnContentPresentationChanged();
            }
        }
    }

    public FileEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                ClearMetadata();
                _preview?.Cancel();
                IsPreviewLoading = false;
                ClearPreview();
                OnPropertyChanged(nameof(DownloadLabel));
                NotifyFileInteractionStateChanged();
            }
        }
    }

    public FileEntryMetadataViewModel? SelectedMetadata
    {
        get => _selectedMetadata;
        private set
        {
            if (SetProperty(ref _selectedMetadata, value))
            {
                OnPropertyChanged(nameof(HasSelectedMetadata));
            }
        }
    }

    public bool HasSelectedMetadata => SelectedMetadata is not null;

    public FileOperationIssue? MetadataIssue
    {
        get => _metadataIssue;
        private set
        {
            if (SetProperty(ref _metadataIssue, value))
            {
                OnPropertyChanged(nameof(HasMetadataIssue));
            }
        }
    }

    public bool HasMetadataIssue => MetadataIssue is not null;

    public FilePanelLocation? CurrentLocation
    {
        get => _currentLocation;
        private set
        {
            if (SetProperty(ref _currentLocation, value))
            {
                LocationText = value is null ? string.Empty : FileLocationPresentation.Display(value);
                NotifyFileInteractionStateChanged();
                OnContentPresentationChanged();
            }
        }
    }

    public string LocationText
    {
        get => _locationText;
        set => SetProperty(ref _locationText, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ScheduleSearch();
            }
        }
    }

    /// <summary>The active provider-backed search, exposed so hosts and tests can await it.</summary>
    public Task SearchCompletion => _searchCompletion;

    public FileEntrySortField SortField
    {
        get => _sortField;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (SetProperty(ref _sortField, value))
            {
                NotifySortFieldChanged();
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<FileEntrySortField> SortFields { get; } =
        Enum.GetValues<FileEntrySortField>();

    public FileEntrySortDirection SortDirection
    {
        get => _sortDirection;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (SetProperty(ref _sortDirection, value))
            {
                OnPropertyChanged(nameof(IsSortDescending));
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<FileEntrySortDirection> SortDirections { get; } =
        Enum.GetValues<FileEntrySortDirection>();

    public bool IsSortingByName => SortField == FileEntrySortField.Name;

    public bool IsSortingBySize => SortField == FileEntrySortField.Size;

    public bool IsSortingByModified => SortField == FileEntrySortField.Modified;

    public bool IsSortDescending => SortDirection == FileEntrySortDirection.Descending;

    public FileBrowserViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
            }

            if (SetProperty(ref _viewMode, value))
            {
                OnPropertyChanged(nameof(IsDetailsView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsGridView));
            }
        }
    }

    public IReadOnlyList<FileBrowserViewMode> ViewModes { get; } =
        Enum.GetValues<FileBrowserViewMode>();

    public bool IsDetailsView => ViewMode == FileBrowserViewMode.Details;

    public bool IsListView => ViewMode == FileBrowserViewMode.List;

    public bool IsGridView => ViewMode == FileBrowserViewMode.Grid;

    public GridLength FileNameColumnWidth
    {
        get => _fileNameColumnWidth;
        set => SetProperty(ref _fileNameColumnWidth, value);
    }

    public GridLength FileSizeColumnWidth
    {
        get => _fileSizeColumnWidth;
        set => SetProperty(ref _fileSizeColumnWidth, value);
    }

    public GridLength FileModifiedColumnWidth
    {
        get => _fileModifiedColumnWidth;
        set => SetProperty(ref _fileModifiedColumnWidth, value);
    }

    /// <summary>
    /// How the listing and its preview divide the panel, and how wide the grip
    /// between them is. These live here rather than in the markup because a
    /// view is rebuilt whenever the layout changes and the panel is not: a
    /// division kept in the view comes back as whatever the markup said, while
    /// the panel goes on describing the arrangement the person chose.
    /// </summary>
    public GridLength FileListColumnWidth
    {
        get => _fileListColumnWidth;
        set => SetProperty(ref _fileListColumnWidth, value);
    }

    public GridLength PreviewColumnWidth
    {
        get => _previewColumnWidth;
        set => SetProperty(ref _previewColumnWidth, value);
    }

    public GridLength PreviewSplitterWidth =>
        IsPreviewVisible ? new GridLength(PreviewSplitterThickness) : new GridLength(0);

    /// <summary>
    /// A minimum is a promise the grid keeps whatever the width asks for, so a
    /// hidden preview has to withdraw it as well as its width.
    /// </summary>
    public double PreviewColumnMinWidth =>
        IsPreviewVisible ? PreviewMinimumWidth : 0;

    public void ChangeSort(FileEntrySortField field)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        if (SortField == field)
        {
            SortDirection = SortDirection == FileEntrySortDirection.Ascending
                ? FileEntrySortDirection.Descending
                : FileEntrySortDirection.Ascending;
            return;
        }

        var directionChanged = SetProperty(
            ref _sortDirection,
            FileEntrySortDirection.Ascending,
            nameof(SortDirection));
        var fieldChanged = SetProperty(ref _sortField, field, nameof(SortField));
        if (directionChanged)
        {
            OnPropertyChanged(nameof(IsSortDescending));
        }

        if (fieldChanged)
        {
            NotifySortFieldChanged();
        }

        ApplyFilter();
    }

    private void NotifySortFieldChanged()
    {
        OnPropertyChanged(nameof(IsSortingByName));
        OnPropertyChanged(nameof(IsSortingBySize));
        OnPropertyChanged(nameof(IsSortingByModified));
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (SetProperty(ref _showHidden, value) && CurrentLocation is not null)
            {
                _ = RefreshDiscoveryAsync();
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
                NotifyPagingChanged();
                NotifyFileInteractionStateChanged();
                OnPropertyChanged(nameof(ShowEmptyState));
                OnContentPresentationChanged();
            }
        }
    }

    public bool IsSearchLoading
    {
        get => _isSearchLoading;
        private set
        {
            if (SetProperty(ref _isSearchLoading, value))
            {
                NotifyPagingChanged();
                OnPropertyChanged(nameof(ShowNavigationProgress));
                OnPropertyChanged(nameof(ShowSearchNoResultsState));
                OnContentPresentationChanged();
            }
        }
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set
        {
            if (SetProperty(ref _isPreviewLoading, value))
            {
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public bool IsMetadataLoading
    {
        get => _isMetadataLoading;
        private set => SetProperty(ref _isMetadataLoading, value);
    }

    /// <summary>
    /// Whether the optional inspector occupies layout space. Preview content is
    /// retained while hidden, so reopening the panel does not repeat a remote read.
    /// </summary>
    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set
        {
            if (!SetProperty(ref _isPreviewVisible, value))
            {
                return;
            }

            if (!value && PreviewColumnWidth.Value > 0)
            {
                _hiddenPreviewColumnWidth = PreviewColumnWidth;
            }

            OnPropertyChanged(nameof(PreviewVisibilityStatus));
            // The minimum first: it is a promise the grid keeps whatever width
            // it is given, so a column still carrying one cannot be closed.
            OnPropertyChanged(nameof(PreviewColumnMinWidth));
            OnPropertyChanged(nameof(PreviewSplitterWidth));
            PreviewColumnWidth = value
                ? _hiddenPreviewColumnWidth
                : new GridLength(0);
        }
    }

    public string PreviewVisibilityStatus =>
        IsPreviewVisible ? "Preview visible" : "Preview hidden";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// The same count with the words taken out, for when the toolbar has no
    /// room for them. A number in a pill beside a folder path is already
    /// unambiguous; "item(s)" is what goes when something has to.
    /// </summary>
    public string ShortStatus
    {
        get => _shortStatus;
        private set => SetProperty(ref _shortStatus, value);
    }

    public FileOperationIssue? ContentIssue
    {
        get => _contentIssue;
        private set
        {
            if (SetProperty(ref _contentIssue, value))
            {
                PublishIssueState();
                OnContentPresentationChanged();
            }
        }
    }

    public FileOperationIssue? OperationIssue
    {
        get => _operationIssue;
        private set
        {
            if (SetProperty(ref _operationIssue, value))
            {
                OnPropertyChanged(nameof(HasOperationIssue));
                PublishIssueState();
            }
        }
    }

    public FileOperationIssue? CurrentIssue => OperationIssue ?? ContentIssue;

    public string? ErrorMessage => CurrentIssue?.Message;

    public string ErrorTitle => CurrentIssue?.Title ?? "File operation failed";

    public string? ErrorSuggestedAction => CurrentIssue?.SuggestedAction;

    public bool CanRetryError => CurrentIssue?.CanRetry == true;

    public bool HasOperationIssue => OperationIssue is not null;

    public FileOperationIssue? PreviewIssue
    {
        get => _previewIssue;
        private set
        {
            if (SetProperty(ref _previewIssue, value))
            {
                OnPropertyChanged(nameof(HasPreviewIssue));
            }
        }
    }

    public bool HasPreviewIssue => PreviewIssue is not null;

    public string PreviewTitle
    {
        get => _previewTitle;
        private set
        {
            if (SetProperty(ref _previewTitle, value))
            {
                // Which view the text gets is decided by the file's name, so
                // the choice has to follow the name changing.
                OnPropertyChanged(nameof(HasMarkdownPreview));
                OnPropertyChanged(nameof(HasSourcePreview));
            }
        }
    }

    public string? PreviewText
    {
        get => _previewText;
        private set
        {
            if (SetProperty(ref _previewText, value))
            {
                OnPropertyChanged(nameof(HasTextPreview));
                OnPropertyChanged(nameof(HasMarkdownPreview));
                OnPropertyChanged(nameof(HasSourcePreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var previous = _previewImage;
            if (SetProperty(ref _previewImage, value))
            {
                previous?.Dispose();
                OnPropertyChanged(nameof(HasImagePreview));
                OnPropertyChanged(nameof(HasSourcePreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public bool HasError => CurrentIssue is not null;

    /// <summary>
    /// The default size at or below which a remote file is fetched for preview
    /// without asking, for hosts composed without live settings. The setting
    /// of the same meaning overrides this.
    /// </summary>
    public const long AutoDownloadPreviewBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The size at or below which a remote file is fetched for preview without
    /// asking. Previewing a remote file costs the user's bandwidth, so above
    /// this the preview waits to be asked for. Read live from settings, so a
    /// change on the settings page is in effect for the very next selection.
    /// </summary>
    private long AutoLoadThresholdBytes =>
        _previewPreferences?.Current.AutoLoadThresholdBytes ?? AutoDownloadPreviewBytes;

    /// <summary>
    /// The toggle's caption, carrying the live threshold: the number it
    /// promises is the number the settings page set, not a hardcoded one.
    /// </summary>
    public string AutoDownloadPreviewLabel =>
        $"Automatically preview files < {ByteSize.Format(AutoLoadThresholdBytes)}";

    private void OnPreviewPreferencesChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(AutoDownloadPreviewLabel));

    /// <summary>
    /// Whether previewing this provider must transfer bytes onto the host.
    /// This includes remote filesystems and Docker resources, regardless of
    /// whether the Docker engine itself is local or reached over SSH.
    /// </summary>
    public bool RequiresHostTransferForPreview =>
        SelectedProfile?.RequiresHostTransferForPreview == true;

    public bool AutoDownloadPreviews
    {
        get => _autoDownloadPreviews;
        set
        {
            if (SetProperty(ref _autoDownloadPreviews, value) && value)
            {
                // Turning it on is itself the answer to the waiting question.
                _ = PreviewDeferredAsync();
            }
        }
    }

    /// <summary>
    /// A remote file is selected and its preview is waiting to be asked for,
    /// because auto-download is off or the file is over the threshold.
    /// </summary>
    public bool ShowPreviewDownloadPrompt => _deferredPreviewEntry is not null;

    public string PreviewDownloadPromptDetail => _deferredPreviewEntry?.Entry.Size is { } size
        ? $"Preview will download {FileEntryViewModel.FormatSize(size)} to a temporary location."
        : "Preview will download this file to a temporary location.";

    /// <summary>
    /// The database viewer bound to the selected file, when that file is a
    /// database. It is the same view model the docked database panel uses, so
    /// the preview is the product's database viewer rather than a second one.
    /// </summary>
    public DatabaseRuntimePanelViewModel? DatabasePreview => _databasePreview;

    public bool HasDatabasePreview => _databasePreview is not null;

    public bool HasTextPreview => !string.IsNullOrEmpty(PreviewText);

    /// <summary>
    /// Whether the text in hand should be laid out as Markdown rather than
    /// shown as source. Decided by the previewer that claimed the file, so
    /// "Show raw" turns it off without changing what the file is.
    /// </summary>
    public bool HasMarkdownPreview => HasTextPreview && _markdownRendering;

    public bool HasSourcePreview =>
        HasTextPreview && !HasMarkdownPreview && !HasImagePreview && !HasPdfPreview;

    /// <summary>
    /// Whether source text wraps. A hex dump is a fixed-width grid and must
    /// not: wrapping folds every row and the columns stop lining up.
    /// </summary>
    public bool WrapPreviewText
    {
        get => _wrapPreviewText;
        private set => SetProperty(ref _wrapPreviewText, value);
    }

    /// <summary>
    /// The switches the current format offers — "Show raw", "Prettify" —
    /// shown beside the file's details.
    /// </summary>
    public ObservableCollection<PreviewToggleViewModel> PreviewToggles { get; } = [];

    public bool HasPreviewToggles => PreviewToggles.Count > 0;

    public PreviewTableViewModel? PreviewTable
    {
        get => _previewTable;
        private set
        {
            if (SetProperty(ref _previewTable, value))
            {
                OnPropertyChanged(nameof(HasTablePreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public bool HasTablePreview => _previewTable is not null;

    public PreviewTreeViewModel? PreviewTree
    {
        get => _previewTree;
        private set
        {
            if (SetProperty(ref _previewTree, value))
            {
                OnPropertyChanged(nameof(HasTreePreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public bool HasTreePreview => _previewTree is not null;

    /// <summary>
    /// What is known about a file the shell cannot show: its format and a
    /// symbol for it, rather than a wall of hex nobody asked for.
    /// </summary>
    public BinaryPreviewRendering? PreviewBinary
    {
        get => _previewBinary;
        private set
        {
            if (SetProperty(ref _previewBinary, value))
            {
                OnPropertyChanged(nameof(HasBinaryPreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public bool HasBinaryPreview => _previewBinary is not null;

    /// <summary>The file's bytes, as rows a list can draw one screen at a time.</summary>
    public PreviewHexViewModel? PreviewHex
    {
        get => _previewHex;
        private set
        {
            if (SetProperty(ref _previewHex, value))
            {
                OnPropertyChanged(nameof(HasHexPreview));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(ShowPreviewPlaceholder));
                OnPropertyChanged(nameof(ShowPreviewProgressDetail));
            }
        }
    }

    public bool HasHexPreview => _previewHex is not null;

    public bool HasImagePreview => PreviewImage is not null;

    public bool HasPreview =>
        HasTextPreview || HasImagePreview || HasDatabasePreview || HasHtmlPreview
        || HasTablePreview || HasTreePreview || HasBinaryPreview || HasHexPreview;

    /// <summary>
    /// Whether the panel is waiting with nothing to show. Once something is on
    /// screen the wait is said in the strip along the top instead, so a reading
    /// is never replaced by a spinner.
    /// </summary>
    public bool ShowPreviewProgressDetail => IsPreviewLoading && !HasPreview;

    public bool ShowPreviewPlaceholder =>
        !HasPreview && !IsPreviewLoading && !ShowPreviewDownloadPrompt;

    public FileBrowserContentPresentation ContentPresentation =>
        FileBrowserContentPresentation.Resolve(
            IsLoading,
            _hasLoadedListing,
            _allEntries.Count,
            Entries.Count,
            Filter,
            ContentIssue,
            ContentIssue?.Message,
            CanCreateFolder,
            CurrentLocation is not null);

    public FileBrowserContentState ContentState => ContentPresentation.State;

    public bool ShowLoadingState =>
        ContentState == FileBrowserContentState.Loading && !_hasLoadedListing;

    public bool ShowNavigationProgress =>
        (IsLoading && _hasLoadedListing) || IsSearchLoading;

    public bool ShowEmptyLocationState =>
        ContentState == FileBrowserContentState.EmptyLocation;

    public bool ShowSearchNoResultsState =>
        ContentState == FileBrowserContentState.SearchNoResults && !IsSearchLoading;

    public bool ShowErrorState => ContentPresentation.IsError;

    public bool CanRetryContentState => !_disposed && ContentPresentation.CanRetry;

    public bool ShowEmptyState => ShowEmptyLocationState || ShowSearchNoResultsState;

    public ICommand RetryCommand => _retryCommand;
    public ICommand NextPageCommand => _nextPageCommand;
    public ICommand PreviousPageCommand => _previousPageCommand;
    public ICommand NextArchivePageCommand => _nextArchivePageCommand;
    public ICommand PreviousArchivePageCommand => _previousArchivePageCommand;
    public bool HasNextArchivePage => _archiveHasMore;
    public bool HasPreviousArchivePage => _archiveOffset > 0;
    private bool UsesProviderSearch => Filter.Trim().Length > 0
        && SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Search) == true;
    public bool HasPreviousPage => !UsesProviderSearch && _listingPageIndex > 0;
    public bool HasNextPage => !IsLoading && !IsSearchLoading
        && (UsesProviderSearch ? _searchPageRequest is not null : HasMore);

    public bool CanSelectProfile => !IsLoading && !IsInitialHostedBindingPending;

    public bool CanEditLocation => !IsLoading && !IsInitialHostedBindingPending;

    public bool CanNavigateUp => !IsLoading
        && !IsInitialHostedBindingPending
        && CurrentLocation?.Address is FilePanelAddress.Hierarchical hierarchical
        && !hierarchical.Path.IsRoot;

    public bool HasMore => !string.IsNullOrWhiteSpace(_continuationToken);

    public bool HasListingSummary => _hasLoadedListing;

    /// <summary>
    /// What this connection is, said in the terms the action rules are written
    /// in. Every question about whether something can be done goes through here,
    /// so the toolbar, the menus and the guards that run on a click all read the
    /// same answer.
    /// </summary>
    private FilePanelActionContext ActionContext
    {
        get
        {
            var transferable = DownloadableEntries();
            var selected = _selectedEntries.Count > 0
                ? _selectedEntries.Count
                : SelectedEntry is null ? 0 : 1;
            return new FilePanelActionContext
            {
                Capabilities = SelectedProfile?.Capabilities ?? FilePanelCapability.None,
                IsBusy = IsLoading,
                IsBindingSavedSession = IsInitialHostedBindingPending,
                HasLocation = CurrentLocation is not null,
                IsHierarchicalLocation =
                    CurrentLocation?.Address is FilePanelAddress.Hierarchical,
                SelectionCount = selected,
                SelectionIsSingleFile =
                    SelectedEntry?.Entry.Kind == FilePanelEntryKind.File,
                SelectionIsTransferable = transferable.Count > 0,
                HasTransferQueue = _transferQueue is not null,
                HasLocalProvider = Profiles.Any(profile => string.Equals(profile.Id, BuiltInFileProviders.HomeId.Value, StringComparison.Ordinal)),
                IsLocalProvider =
string.Equals(SelectedProfile?.Id, BuiltInFileProviders.HomeId.Value, StringComparison.Ordinal),
                HasClipboard = _clipboard?.HasContent == true,
            };
        }
    }

    /// <summary>
    /// Every action this connection can perform, in menu order, each greyed out
    /// or not according to what is selected. The overflow menu and the
    /// right-click menu are both this list.
    /// </summary>
    public IReadOnlyList<FileActionViewModel> MenuActions => _menuActions;

    /// <summary>
    /// The few that earn a button of their own while there is room for them.
    /// Drawn from the same list, so a button and its menu entry can never
    /// disagree about whether the action is possible.
    /// </summary>
    public IReadOnlyList<FileActionViewModel> ToolbarActions => _toolbarActions;

    /// <summary>
    /// A right-click on a row: what can be done to the things picked out.
    /// </summary>
    public IReadOnlyList<FileActionViewModel> EntryMenuActions => _entryMenuActions;

    /// <summary>
    /// A right-click on the space below them, which is the folder itself: what
    /// can be put into it or done to it.
    /// </summary>
    public IReadOnlyList<FileActionViewModel> FolderMenuActions => _folderMenuActions;

    private static readonly FilePanelAction[] ToolbarPrimaryActions =
    [
        FilePanelAction.Upload,
        FilePanelAction.Download,
        FilePanelAction.NewFolder,
        FilePanelAction.Delete,
    ];

    private IReadOnlyList<FileActionViewModel> _menuActions = [];
    private IReadOnlyList<FileActionViewModel> _toolbarActions = [];
    private IReadOnlyList<FileActionViewModel> _entryMenuActions = [];
    private IReadOnlyList<FileActionViewModel> _folderMenuActions = [];

    private void RebuildActions()
    {
        var states = FilePanelActionCatalog.Resolve(ActionContext)
            .Where(state => state.IsAvailable)
            .ToArray();
        _menuActions = Dress(states);
        _toolbarActions = Dress([.. states.Where(state => ToolbarPrimaryActions.Contains(state.Action))]);
        // Each menu re-groups from its own survivors, so neither opens with a
        // rule across the top or draws one where nothing changed.
        _entryMenuActions = Dress([.. states.Where(state => state.Scope == FilePanelActionScope.Selection)]);
        _folderMenuActions = Dress([.. states.Where(state => state.Scope == FilePanelActionScope.Folder)]);
        OnPropertyChanged(nameof(MenuActions));
        OnPropertyChanged(nameof(ToolbarActions));
        OnPropertyChanged(nameof(EntryMenuActions));
        OnPropertyChanged(nameof(FolderMenuActions));
    }

    private FileActionViewModel[] Dress(IReadOnlyList<FilePanelActionState> states)
    {
        var dressed = new FileActionViewModel[states.Count];
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            dressed[index] = new FileActionViewModel(
                state,
                FilePanelActionPresentation.Label(state.Action),
                // What downloading will actually pull is worth saying: a person
                // about to take a folder tree over a network should be told so.
                state.Action == FilePanelAction.Download
                    ? DownloadLabel
                    : FilePanelActionPresentation.Description(state.Action),
                FilePanelActionPresentation.Glyph(state.Action),
                FilePanelActionPresentation.Gesture(state.Action),
                startsGroup: index > 0 && states[index - 1].Group != state.Group,
                RequestAction);
        }

        return dressed;
    }

    public bool CanCreateFolder => IsActionEnabled(FilePanelAction.NewFolder);

    public bool CanRename => IsActionEnabled(FilePanelAction.Rename);

    public bool CanDelete => IsActionEnabled(FilePanelAction.Delete);

    public bool CanTransfer => IsActionEnabled(FilePanelAction.Transfer);

    public bool CanDownload => IsActionEnabled(FilePanelAction.Download);

    public bool CanUpload => IsActionEnabled(FilePanelAction.Upload);

    public bool CanOpenExternally => IsActionEnabled(FilePanelAction.OpenExternally);

    public bool IsActionEnabled(FilePanelAction action) =>
        FilePanelActionCatalog.IsEnabled(ActionContext, action);

    /// <summary>
    /// Asked for by a button, an overflow entry or a right-click menu. What it
    /// takes to carry out — a folder picker, a confirmation, a name to type —
    /// belongs to the window, so the panel says what was asked and stops there.
    /// </summary>
    public event EventHandler<FilePanelAction>? ActionRequested;

    private void RequestAction(FilePanelAction action) =>
        ActionRequested?.Invoke(this, action);

    /// <summary>
    /// Who can read or change the selected item, as this connection describes
    /// it. A failure is reported on the panel rather than thrown: being told
    /// the server refused is more use than a dialog that will not open.
    /// </summary>
    public async Task<FilePanelAccessControl?> ReadAccessControlAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SelectedEntry is not { } entry)
        {
            return null;
        }

        ClearOperationIssue();
        var result = await _client
            .GetAccessControlAsync(
                new FilePanelAccessControlRequest(entry.Entry.Location),
                cancellationToken)
            .ConfigureAwait(true);
        if (result.IsSuccess)
        {
            return result.Value;
        }

        SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
        return null;
    }

    /// <summary>
    /// And the change back. What comes back is what the connection has after
    /// the write, not what was asked for: a server is allowed to normalise.
    /// </summary>
    public async Task<FilePanelAccessControl?> WriteAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearOperationIssue();
        var result = await _client
            .SetAccessControlAsync(request, cancellationToken)
            .ConfigureAwait(true);
        if (result.IsSuccess)
        {
            return result.Value;
        }

        SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
        return null;
    }

    /// <summary>
    /// Whatever was copied or cut anywhere in this window, which is what says
    /// whether pasting into this folder is possible.
    /// </summary>
    public FileTransferClipboard? TransferClipboard => _clipboard;

    /// <summary>
    /// What a menu acts on: everything picked out, or — when a right-click
    /// landed on a row without the listing reporting a multiple selection —
    /// the one thing the listing calls selected.
    /// </summary>
    public IReadOnlyList<FilePanelEntry> SelectedEntriesOrCurrent =>
        _selectedEntries.Count > 0
            ? _selectedEntries
            : SelectedEntry is { } single ? [single.Entry] : [];

    /// <summary>
    /// Where the selected item lives, written the way it would be typed into
    /// the location bar — which is what makes it worth putting on a clipboard.
    /// </summary>
    public string? SelectedEntryPath => SelectedEntry is { } entry
        ? FileLocationPresentation.Display(entry.Entry.Location)
        : null;

    private readonly FileTransferClipboard? _clipboard;

    private void OnTransferClipboardChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RebuildActions();
    }

    private bool IsInitialHostedBindingPending =>
        _initialSelectionPending
        && _hostedClient is { IsInitialized: false };

    public async Task SelectProfileAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Profiles.Any(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal)))
        {
            SetOperationIssue(FileOperationIssue.Configuration(
                "The selected file-provider profile no longer exists."));
            return;
        }

        if (_initialSelectionPending)
        {
            if (_initialProfileId is not null
                && !string.Equals(profile.Id, _initialProfileId, StringComparison.Ordinal))
            {
                SetOperationIssue(FileOperationIssue.Configuration(
                    "This File Viewer is still waiting for its saved provider. "
                    + "It will not substitute another provider before the saved session binds."));
                return;
            }

            await SelectInitialProfileAsync(profile, cancellationToken);
            return;
        }

        SelectedProfile = profile;
        // Where the provider says it opens, which need not be its root: the
        // local provider reaches the whole filesystem and opens at home.
        await NavigateAsync(profile.StartLocation, cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentLocation is null)
        {
            return;
        }

        CancelSearch(clearResults: true);
        await LoadListingAsync(CurrentLocation, cancellationToken, _listingPages[_listingPageIndex]);
        ScheduleSearch();
    }

    public async Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!HasNextPage || CurrentLocation is null)
        {
            return;
        }

        if (_searchPageRequest is { } request)
        {
            _searchPageRequest = null;
            IsSearchLoading = true;
            NotifyPagingChanged();
            request.TrySetResult();
            return;
        }

        var next = _continuationToken!;
        if (_listingPages.Take(_listingPageIndex + 1).Contains(next, StringComparer.Ordinal))
        {
            SetContentIssue(FileOperationIssue.Unexpected("The file provider repeated a continuation token."));
            return;
        }

        _listingPages.RemoveRange(_listingPageIndex + 1, _listingPages.Count - _listingPageIndex - 1);
        _listingPages.Add(next);
        _listingPageIndex++;
        await LoadListingAsync(CurrentLocation, cancellationToken, next);
    }

    public async Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPreviousPage || IsLoading || CurrentLocation is null)
        {
            return;
        }

        _listingPageIndex--;
        await LoadListingAsync(CurrentLocation, cancellationToken, _listingPages[_listingPageIndex]);
    }

    private void NotifyPagingChanged()
    {
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        _nextPageCommand.RaiseCanExecuteChanged();
        _previousPageCommand.RaiseCanExecuteChanged();
    }

    public Task RetryAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    public Task NavigateUpAsync(CancellationToken cancellationToken = default) =>
        CanNavigateUp && CurrentLocation is not null
            ? NavigateAsync(CurrentLocation.Parent, cancellationToken)
            : Task.CompletedTask;

    public async Task NavigateFromTextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearOperationIssue();
        if (SelectedProfile is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "Choose a file-provider profile first."));
            return;
        }

        FilePanelLocation location;
        try
        {
            location = FileLocationPresentation.Parse(SelectedProfile, LocationText);
        }
        catch (ArgumentException exception)
        {
            SetOperationIssue(FileOperationIssue.Validation(exception.Message));
            return;
        }

        await NavigateAsync(location, cancellationToken);
    }

    public async Task OpenEntryAsync(
        FileEntryViewModel entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.Entry.Location.WithVersion(null), cancellationToken);
        }
        else
        {
            SelectedEntry = entry;
            await PreviewSelectedAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Fetches the preview the user just asked for, from the button or the
    /// space bar. The request is remembered for exactly this entry, so the gate
    /// lets it through once without turning auto-download on for everything.
    /// </summary>
    public Task PreviewDeferredAsync(CancellationToken cancellationToken = default)
    {
        if (_deferredPreviewEntry is not { } entry)
        {
            return Task.CompletedTask;
        }

        _requestedPreviewEntry = entry;
        _grantedPreviews.Add(PreviewGrantKey(entry));
        return LoadPreviewAsync(entry, cancellationToken);
    }

    /// <summary>
    /// The identity a grant is remembered against. The location already carries
    /// profile, address, and version, so two files cannot share a key and one
    /// file cannot keep a grant across an edit.
    /// </summary>
    private static string PreviewGrantKey(FileEntryViewModel entry) =>
        entry.Entry.Location.ToString();

    private void SetDeferredPreview(FileEntryViewModel? entry)
    {
        if (ReferenceEquals(_deferredPreviewEntry, entry))
        {
            return;
        }

        _deferredPreviewEntry = entry;
        OnPropertyChanged(nameof(ShowPreviewDownloadPrompt));
        OnPropertyChanged(nameof(PreviewDownloadPromptDetail));
        OnPropertyChanged(nameof(ShowPreviewPlaceholder));
        OnPropertyChanged(nameof(ShowPreviewProgressDetail));
    }

    public Task PreviewSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedEntry;
        return Task.WhenAll(
            LoadMetadataAsync(selected, cancellationToken),
            LoadPreviewAsync(selected, cancellationToken));
    }

    public async Task<bool> CreateFolderAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ClearOperationIssue();
        if (!CanCreateFolder || CurrentLocation is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "This provider cannot create a folder at the current location."));
            return false;
        }

        FilePanelLocation location;
        try
        {
            location = CurrentLocation.Child(new FilePanelPathSegment(name.Trim()));
        }
        catch (ArgumentException exception)
        {
            SetOperationIssue(FileOperationIssue.Validation(exception.Message));
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _client.CreateDirectoryAsync(
            new FilePanelCreateDirectoryRequest(
                location,
                FilePanelMutationPrecondition.MustNotExist),
            linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RenameSelectedAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ClearOperationIssue();
        if (!CanRename || SelectedEntry is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "Choose an item that this provider can rename."));
            return false;
        }

        FilePanelLocation destination;
        try
        {
            destination = SelectedEntry.Entry.Location.Parent.Child(
                new FilePanelPathSegment(name.Trim()));
        }
        catch (ArgumentException exception)
        {
            SetOperationIssue(FileOperationIssue.Validation(exception.Message));
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _client.RenameAsync(
            new FilePanelRenameRequest(
                SelectedEntry.Entry.Location,
                destination,
                FilePanelMutationPrecondition.MustNotExist),
            linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        await RefreshAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        ClearOperationIssue();
        if (!CanDelete || SelectedEntry is null)
        {
            SetOperationIssue(FileOperationIssue.Validation(
                "Choose an item that this provider can delete."));
            return false;
        }

        var selected = SelectedEntry;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _client.DeleteAsync(
            new FilePanelDeleteRequest(
                selected.Entry.Location,
                Recursive: false,
                selected.Entry.Location.Version is { } version
                    ? FilePanelMutationPrecondition.VersionMatches(version)
                    : FilePanelMutationPrecondition.MustExist),
            linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        SelectedEntry = null;
        await RefreshAsync(cancellationToken);
        return true;
    }

    public FileTransferEditorViewModel CreateTransferEditor()
    {
        if (!CanTransfer || SelectedEntry is null)
        {
            throw new InvalidOperationException(
                "Choose a file or folder before creating a transfer.");
        }

        return new FileTransferEditorViewModel(
            SelectedEntry.Entry,
            Profiles,
            SelectedProfile?.Id);
    }

    public FilePanelTransferRequest CreateIncomingTransferRequest(
        FilePanelEntry source,
        FilePanelTransferOperation operation,
        FilePanelLocation? destinationFolder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }

        if (IsLoading || CurrentLocation is null || SelectedProfile is null)
        {
            throw new InvalidOperationException(
                "Wait for the destination folder to finish loading.");
        }

        if (source.Kind is not (
            FilePanelEntryKind.File or FilePanelEntryKind.Directory))
        {
            throw new InvalidOperationException(
                "Only regular files and folders can be transferred.");
        }

        var requiredCapabilities = source.Kind == FilePanelEntryKind.Directory
            ? FilePanelCapability.CreateDirectory | FilePanelCapability.StreamingWrite
            : FilePanelCapability.StreamingWrite;
        if (!SelectedProfile.Capabilities.HasFlag(requiredCapabilities))
        {
            throw new InvalidOperationException(
                "The selected destination cannot receive this item type.");
        }

        var destinationParent = destinationFolder?.WithVersion(null)
            ?? CurrentLocation;
        if (!string.Equals(
                destinationParent.ProviderProfileId,
                SelectedProfile.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                destinationParent.Authority,
                CurrentLocation.Authority,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The drop folder does not belong to the selected destination.");
        }

        var destination = FileLocationPresentation.Child(
            destinationParent,
            source.Name);
        if (source.Location.WithVersion(null) == destination.WithVersion(null))
        {
            throw new InvalidOperationException(
                "The item is already in this destination.");
        }

        if (source.Kind == FilePanelEntryKind.Directory
            && source.Location.WithVersion(null) == destinationParent)
        {
            throw new InvalidOperationException(
                "A folder cannot be transferred into itself.");
        }

        return new FilePanelTransferRequest(
            source.Location,
            destination,
            operation,
            FilePanelConflictPolicy.KeepBoth);
    }

    public bool CanReceiveTransfer(
        FilePanelEntry source,
        FilePanelLocation? destinationFolder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsLoading || CurrentLocation is null || SelectedProfile is null)
        {
            return false;
        }

        var requiredCapabilities = source.Kind switch
        {
            FilePanelEntryKind.File =>
                FilePanelCapability.StreamingWrite,
            FilePanelEntryKind.Directory =>
                FilePanelCapability.CreateDirectory
                | FilePanelCapability.StreamingWrite,
            _ => FilePanelCapability.None,
        };
        if (requiredCapabilities == FilePanelCapability.None
            || !SelectedProfile.Capabilities.HasFlag(requiredCapabilities))
        {
            return false;
        }

        var destinationParent = destinationFolder?.WithVersion(null)
            ?? CurrentLocation;
        if (!string.Equals(
                destinationParent.ProviderProfileId,
                SelectedProfile.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                destinationParent.Authority,
                CurrentLocation.Authority,
                StringComparison.Ordinal))
        {
            return false;
        }

        var sourceLocation = source.Location.WithVersion(null);
        var destination = FileLocationPresentation.Child(
            destinationParent,
            source.Name);
        return sourceLocation != destination.WithVersion(null)
            && (source.Kind != FilePanelEntryKind.Directory
                || sourceLocation != destinationParent);
    }

    public FileTransferEditorViewModel CreateDownloadEditor()
    {
        if (!CanDownload)
        {
            throw new InvalidOperationException(
                "Choose a regular file before creating a download.");
        }

        return new FileTransferEditorViewModel(
            SelectedEntry!.Entry,
            Profiles,
            "builtin.files.home");
    }

    /// <summary>
    /// Everything selected, which may be several files and folders at once.
    /// Kept beside <see cref="SelectedEntry"/>, which stays the one whose
    /// details and preview are shown.
    /// </summary>
    public IReadOnlyList<FilePanelEntry> SelectedEntries => _selectedEntries;

    private IReadOnlyList<FilePanelEntry> _selectedEntries = [];

    public void SetSelectedEntries(IReadOnlyList<FilePanelEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _selectedEntries = entries;
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(DownloadLabel));
        NotifyFileInteractionStateChanged();
    }

    /// <summary>
    /// What the download action will do, said plainly: a person about to pull
    /// a folder tree over a network should be told that is what this is.
    /// </summary>
    public string DownloadLabel
    {
        get
        {
            var entries = DownloadableEntries();
            if (entries.Count == 0)
            {
                return "Download selected items";
            }

            var name = entries.Count == 1 ? $"\u201c{entries[0].Name}\u201d" : $"{entries.Count} items";
            return $"Download {name} to a folder";
        }
    }

    /// <summary>
    /// Copies everything selected — files and folders alike — into a folder on
    /// this machine. Folders are copied whole by the transfer queue; nothing
    /// here walks a remote tree by hand.
    /// </summary>
    public IReadOnlyList<FilePanelTransferRequest> CreateDownloadRequests(
        string destinationFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolderPath);
        var entries = DownloadableEntries();
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Select a file or folder to download.");
        }

        var localProfile = Profiles.SingleOrDefault(profile => string.Equals(profile.Id, BuiltInFileProviders.HomeId.Value, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "This session has no local provider to download into.");
        var folder = LocalLocation(localProfile, destinationFolderPath);
        return [.. entries
            .Select(entry => new FilePanelTransferRequest(
                entry.Location,
                FileLocationPresentation.Child(folder, entry.Name),
                FilePanelTransferOperation.Copy,
                // A second download of the same file replaces the first rather
                // than failing: the reader asked for it again on purpose.
                FilePanelConflictPolicy.Replace))];
    }

    private IReadOnlyList<FilePanelEntry> DownloadableEntries()
    {
        if (_selectedEntries.Count > 0)
        {
            return [.. _selectedEntries
                .Where(entry => entry.Kind is
                    FilePanelEntryKind.File or FilePanelEntryKind.Directory)];
        }

        return SelectedEntry?.Entry is { Kind: FilePanelEntryKind.File or FilePanelEntryKind.Directory } single
            ? [single]
            : [];
    }

    /// <summary>
    /// A path on this machine as a location in the local provider, which is
    /// rooted at the filesystem root and so can address any of it.
    /// </summary>
    private static FilePanelLocation LocalLocation(
        FileProviderProfileDescriptor localProfile,
        string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        var relative = string.IsNullOrEmpty(root) ? full : Path.GetRelativePath(root, full);
        var segments = relative is "." or ".."
            ? []
            : relative
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => new FilePanelPathSegment(segment));
        return new FilePanelLocation(
            localProfile.Id,
            localProfile.Root.Authority,
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(segments)));
    }

    public FileTransferEditorViewModel CreateUploadEditor(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!CanUpload || CurrentLocation is null || SelectedProfile is null)
        {
            throw new InvalidOperationException(
                "This provider cannot receive an uploaded file at the current location.");
        }

        var homeProfile = Profiles.Single(profile => string.Equals(profile.Id, "builtin.files.home", StringComparison.Ordinal));
        var sourcePath = Path.GetFullPath(localPath.Trim());
        var file = new FileInfo(sourcePath);
        if (!file.Exists)
        {
            throw new ArgumentException("The selected local file no longer exists.", nameof(localPath));
        }

        // The local provider is rooted at the filesystem root, so any file the
        // operating system let the user pick is addressable.
        var source = new FilePanelEntry(
            LocalLocation(homeProfile, sourcePath),
            file.Name,
            FilePanelEntryKind.File,
            file.Length,
            file.LastWriteTimeUtc,
            file.Name.StartsWith('.'));
        var editor = new FileTransferEditorViewModel(source, Profiles, SelectedProfile.Id)
        {
            Destination = ChildLocationDisplay(CurrentLocation, file.Name),
        };
        return editor;
    }

    public string GetSelectedLocalPath()
    {
        if (!CanOpenExternally
            || SelectedEntry?.Entry.Location.Address is not FilePanelAddress.Hierarchical path)
        {
            throw new InvalidOperationException(
                "Only regular files from the built-in Home provider can be opened externally.");
        }

        var segments = path.Path.Segments.Select(segment => segment.Value).ToArray();
        var rootPath = LocalRootPath();
        var resolvedPath = Path.GetFullPath(Path.Combine([rootPath, .. segments]));
        if (!IsWithinDirectory(rootPath, resolvedPath))
        {
            throw new InvalidOperationException(
                "The selected file resolves outside the local provider.");
        }

        return resolvedPath;
    }

    public async Task<bool> QueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearOperationIssue();
        if (_transferQueue is null)
        {
            SetOperationIssue(FileOperationIssue.Configuration(
                "The transfer queue is unavailable."));
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var result = await _transferQueue.EnqueueAsync(request, linked.Token);
        if (!result.IsSuccess)
        {
            SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
            return false;
        }

        Status = result.Value!.State == FilePanelTransferState.Skipped
            ? "Transfer skipped because the destination exists"
            : "Transfer queued";
        return true;
    }

    public void ClearError()
    {
        ContentIssue = null;
        OperationIssue = null;
    }

    public void ClearOperationIssue() => OperationIssue = null;

    public void ReportValidationError(string message) =>
        SetOperationIssue(FileOperationIssue.Validation(message));

    private void OnTransfersChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_disposed || _transferQueue is null)
        {
            return;
        }

        List<PanelNotificationEvent> notifications = [];
        lock (_transferNotificationGate)
        {
            foreach (var transfer in _transferQueue.Transfers)
            {
                if (transfer.State is not (FilePanelTransferState.Completed
                    or FilePanelTransferState.Failed)
                    || !_notifiedTransfers.Add(transfer.Id))
                {
                    continue;
                }

                var route = $"{FileLocationPresentation.Display(transfer.Request.Source)} → "
                    + FileLocationPresentation.Display(transfer.EffectiveDestination);
                var body = transfer.State == FilePanelTransferState.Failed
                    && !string.IsNullOrWhiteSpace(transfer.Error?.Message)
                        ? $"{route}\n{transfer.Error.Message}"
                        : route;
                notifications.Add(new PanelNotificationEvent(
                    notifications.Count + _notificationSequence + 1,
                    transfer.State == FilePanelTransferState.Completed
                        ? PanelNotificationKind.FileTransferCompleted
                        : PanelNotificationKind.FileTransferFailed,
                    transfer.State == FilePanelTransferState.Completed
                        ? "File transfer completed"
                        : "File transfer failed",
                    body,
                    transfer.CompletedAt ?? transfer.QueuedAt)
                {
                    Effects = PanelNotificationEffects.Visual
                        | PanelNotificationEffects.System,
                });
            }

            _notificationSequence += notifications.Count;
        }

        foreach (var notification in notifications)
        {
            NotificationReceived?.Invoke(this, notification);
        }
    }

    public override void Dispose()
    {
        lock (_initializationGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_hostedClient is not null)
        {
            _hostedClient.ProfilesChanged -= OnProfilesChanged;
        }
        else
        {
            _profileRuntime?.ProfilesChanged -= OnProfilesChanged;
        }

        _previewPreferences?.Changed -= OnPreviewPreferencesChanged;

        _clipboard?.Changed -= OnTransferClipboardChanged;

        _transferQueue?.TransfersChanged -= OnTransfersChanged;

        _lifetime.Cancel();
        _navigation?.Cancel();
        _search?.Cancel();
        _watch?.Cancel();
        _preview?.Cancel();
        _metadata?.Cancel();
        _navigation?.Dispose();
        _search?.Dispose();
        _watch?.Dispose();
        _preview?.Dispose();
        _metadata?.Dispose();
        _lifetime.Dispose();
        PreviewImage = null;
        ClearDatabasePreview();
        if (_hostedClient is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task InitializeAsync()
    {
        if (Profiles.Count == 0)
        {
            Status = "No file providers configured";
            SetContentIssue(FileOperationIssue.Configuration(
                "Add a file-provider profile before opening the File Viewer."));
            return;
        }

        var initial = _initialProfileId is null
            ? Profiles.FirstOrDefault(item => string.Equals(item.Id, "builtin.files.home", StringComparison.Ordinal))
            : Profiles.FirstOrDefault(item => string.Equals(item.Id, _initialProfileId, StringComparison.Ordinal));
        if (_initialSelectionPending && initial is null)
        {
            Status = "Waiting for saved file provider";
            SetContentIssue(FileOperationIssue.Configuration(
                "The saved File Viewer provider is not currently available."));
            return;
        }

        await SelectInitialProfileAsync(initial ?? Profiles[0]);
    }

    private void OnProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
            || Avalonia.Application.Current is null)
        {
            ApplyProfiles();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyProfiles);
        }
    }

    private void ApplyProfiles()
    {
        if (_disposed)
        {
            return;
        }

        var selectedId = _initialSelectionPending
            ? _initialProfileId
            : SelectedProfile?.Id;
        var previousRoot = SelectedProfile?.Root;
        Replace(Profiles, _client.Profiles);
        if (!_initializationStarted)
        {
            return;
        }

        var selected = selectedId is null
            ? null
            : Profiles.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        if (selected is not null)
        {
            if (_initialSelectionPending || previousRoot != selected.Root)
            {
                _ = _initialSelectionPending
                    ? SelectInitialProfileAsync(selected)
                    : SelectProfileAsync(selected);
            }
            else
            {
                SelectedProfile = selected;
            }
            return;
        }

        if (_initialSelectionPending)
        {
            ResetListing();
            SelectedProfile = null;
            CurrentLocation = null;
            Status = "Saved file provider unavailable";
            SetContentIssue(FileOperationIssue.Configuration(
                "The saved File Viewer provider is not currently available. Repair the saved screen or provider profile."));
            return;
        }

        if (Profiles.Count == 0)
        {
            ResetListing();
            SelectedProfile = null;
            CurrentLocation = null;
            Status = "No file providers configured";
            SetContentIssue(FileOperationIssue.Configuration(
                "Add a file-provider profile before opening the File Viewer."));
            return;
        }

        _ = SelectProfileAsync(Profiles[0]);
    }

    private Task SelectInitialProfileAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        lock (_initialSelectionGate)
        {
            if (!_initialSelection.IsCompleted)
            {
                _initialSelectionRetryRequested = true;
                return _initialSelection;
            }

            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _initialSelection = completion.Task;
        }

        _ = CompleteInitialProfileSelectionAsync(
            profile,
            cancellationToken,
            completion);
        return completion.Task;
    }

    private async Task CompleteInitialProfileSelectionAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await SelectInitialProfileCoreAsync(profile, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        FileProviderProfileDescriptor? retryProfile = null;
        lock (_initialSelectionGate)
        {
            if (_initialSelectionRetryRequested
                && _initialSelectionPending
                && !_disposed
                && _initialProfileId is { } initialProfileId)
            {
                retryProfile = Profiles.FirstOrDefault(
                    item => string.Equals(item.Id, initialProfileId, StringComparison.Ordinal));
            }

            _initialSelectionRetryRequested = false;
        }

        if (failure is null)
        {
            completion.TrySetResult();
        }
        else if (failure is OperationCanceledException
                 && cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        else
        {
            completion.TrySetException(failure);
        }

        if (retryProfile is not null)
        {
            _ = SelectInitialProfileAsync(retryProfile);
        }
    }

    private async Task SelectInitialProfileCoreAsync(
        FileProviderProfileDescriptor profile,
        CancellationToken cancellationToken)
    {
        FilePanelLocation initialLocation;
        if (_initialLocation is not null)
        {
            initialLocation = _initialLocation;
        }
        else if (_initialLocationText is null)
        {
            initialLocation = profile.StartLocation;
        }
        else
        {
            try
            {
                initialLocation = FileLocationPresentation.Parse(
                    profile,
                    _initialLocationText);
            }
            catch (ArgumentException)
            {
                SelectedProfile = profile;
                CurrentLocation = null;
                Status = "Saved startup location invalid";
                SetContentIssue(FileOperationIssue.Configuration(
                    "The saved File Viewer startup location is not valid for this provider. Repair the saved screen before reopening it."));
                return;
            }
        }

        _pendingInitialBindingLocation = initialLocation.WithVersion(null);
        await NavigateAsync(initialLocation, cancellationToken);
    }

    private async Task NavigateAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedLocation = location.WithVersion(null);
        if (IsInitialHostedBindingPending
            && normalizedLocation != _pendingInitialBindingLocation)
        {
            LocationText = CurrentLocation is null
                ? string.Empty
                : FileLocationPresentation.Display(CurrentLocation);
            SetOperationIssue(FileOperationIssue.Configuration(
                "This File Viewer must bind its exact saved startup location before navigating elsewhere."));
            return;
        }

        var profile = Profiles.SingleOrDefault(item => string.Equals(item.Id, location.ProviderProfileId, StringComparison.Ordinal));
        if (profile is null)
        {
            SetOperationIssue(FileOperationIssue.Configuration(
                "The selected file-provider profile no longer exists."));
            return;
        }

        SelectedProfile = profile;
        CurrentLocation = normalizedLocation;
        _listingPages.Clear();
        _listingPages.Add(null);
        _listingPageIndex = 0;
        CancelSearch(clearResults: true);
        StopObservation();
        SelectedEntry = null;
        ClearPreview();
        await LoadListingAsync(CurrentLocation, cancellationToken);
        ScheduleSearch();
        StartObservation();
        if (_initialSelectionPending
            && string.Equals(location.ProviderProfileId, _initialProfileId, StringComparison.Ordinal)
            && (_hostedClient is null || _hostedClient.IsInitialized))
        {
            _initialSelectionPending = false;
            _pendingInitialBindingLocation = null;
            NotifyInitialBindingStateChanged();
        }
    }

    private async Task LoadListingAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken,
        string? continuation = null)
    {
        var operation = ReplaceNavigation(cancellationToken);
        IsLoading = true;
        ClearError();
        Status = "Loading folder…";
        var pageSize = Math.Min(500, SelectedProfile?.MaximumPageSize ?? 250);
        var firstPage = true;
        try
        {
            {
                var result = await _client.ListAsync(
                    new FilePanelListRequest(
                        location,
                        pageSize,
                        continuation,
                        ShowHidden),
                    operation.Token);
                if (!ReferenceEquals(_navigation, operation)
                    || operation.IsCancellationRequested)
                {
                    return;
                }

                if (!result.IsSuccess)
                {
                    if (firstPage)
                    {
                        ResetListing();
                    }

                    Status = "Location unavailable";
                    SetContentIssue(FileOperationIssue.FromProvider(result.Error!));
                    return;
                }

                if (firstPage)
                {
                    // Preserve the old folder until its replacement arrives, then replace it in
                    // one UI turn. Later pages extend that same virtualized collection.
                    ResetListing();
                    firstPage = false;
                }

                _allEntries.AddRange(result.Value!.Entries);
                continuation = result.Value.ContinuationToken;
                _continuationToken = continuation;
                _hasLoadedListing = true;
                OnPropertyChanged(nameof(HasListingSummary));
                ApplyFilter();
                Status = $"{_allEntries.Count.ToString(CultureInfo.InvariantCulture)} items loaded…";
                ShortStatus = $"{_allEntries.Count.ToString(CultureInfo.InvariantCulture)}+";
                OnPropertyChanged(nameof(HasMore));

            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (!operation.IsCancellationRequested)
            {
                if (firstPage)
                {
                    ResetListing();
                }

                SetContentIssue(FileOperationIssue.Unexpected(
                    "The file provider failed unexpectedly while listing this location."));
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_navigation, operation))
            {
                IsLoading = false;
                UpdateListingStatus();
                NotifyPagingChanged();
            }
        }
    }

    private void NotifyInitialBindingStateChanged()
    {
        NotifyFileInteractionStateChanged();
    }

    private void NotifyFileInteractionStateChanged()
    {
        OnPropertyChanged(nameof(CanSelectProfile));
        OnPropertyChanged(nameof(CanEditLocation));
        OnPropertyChanged(nameof(CanNavigateUp));
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanTransfer));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(CanOpenExternally));
        RebuildActions();
    }

    private async Task LoadMetadataAsync(
        FileEntryViewModel? entry,
        CancellationToken cancellationToken)
    {
        _metadata?.Cancel();
        _metadata?.Dispose();
        _metadata = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var operation = _metadata;
        SelectedMetadata = null;
        MetadataIssue = null;
        if (entry is null)
        {
            return;
        }

        if (SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Stat) != true)
        {
            SelectedMetadata = new FileEntryMetadataViewModel(
                entry.Entry,
                isStatBacked: false);
            return;
        }

        IsMetadataLoading = true;
        FilePanelResult<FilePanelEntry> result;
        try
        {
            result = await _client.StatAsync(entry.Entry.Location, operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_metadata, operation))
            {
                SelectedMetadata = new FileEntryMetadataViewModel(
                    entry.Entry,
                    isStatBacked: false);
                MetadataIssue = FileOperationIssue.Unexpected(
                    "The provider failed unexpectedly while reading item metadata.");
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_metadata, operation))
            {
                IsMetadataLoading = false;
            }
        }

        if (!ReferenceEquals(_metadata, operation) || operation.IsCancellationRequested)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            SelectedMetadata = new FileEntryMetadataViewModel(
                entry.Entry,
                isStatBacked: false);
            MetadataIssue = FileOperationIssue.FromProvider(result.Error!);
            return;
        }

        SelectedMetadata = new FileEntryMetadataViewModel(
            result.Value!,
            isStatBacked: true);
    }

    private async Task LoadPreviewAsync(
        FileEntryViewModel? entry,
        CancellationToken cancellationToken = default)
    {
        _preview?.Cancel();
        _preview?.Dispose();
        _preview = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        var operation = _preview;
        // Captured before ClearPreview resets it: an explicit request is about
        // this exact entry, and clearing the previous preview must not throw it
        // away before the gate below has read it.
        var requested = _requestedPreviewEntry;
        ClearPreview();
        IsPreviewLoading = false;
        if (entry is null || entry.IsDirectory || entry.IsLink)
        {
            return;
        }

        // A transferred preview spends bandwidth or copies bytes into host
        // storage. Above the threshold, or with auto-load off, it waits to be
        // asked for instead of doing that work while the user scrolls.
        if (RequiresHostTransferForPreview
            && !ReferenceEquals(entry, requested)
            && !_grantedPreviews.Contains(PreviewGrantKey(entry))
            && (!AutoDownloadPreviews
                || entry.Entry.Size is null
                || entry.Entry.Size > AutoLoadThresholdBytes))
        {
            PreviewTitle = entry.Name;
            SetDeferredPreview(entry);
            return;
        }

        SetDeferredPreview(null);
        IsPreviewLoading = true;
        PreviewTitle = entry.Name;
        var maximum = Math.Min(
            DefaultPreviewBytes,
            SelectedProfile?.MaximumPreviewBytes ?? DefaultPreviewBytes);
        FilePanelResult<FilePanelPreview> result;
        try
        {
            result = await _client.PreviewAsync(
                new FilePanelPreviewRequest(entry.Entry.Location, maximum),
                operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "Preview failed unexpectedly.";
                PreviewIssue = FileOperationIssue.Unexpected(PreviewText);
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }

        if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
        {
            return;
        }

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            PreviewText = error.Message;
            PreviewIssue = FileOperationIssue.FromProvider(error);
            return;
        }

        await PresentPreviewAsync(result.Value!);
    }

    /// <summary>
    /// Hands the preview to whichever previewer claims the format and draws
    /// what comes back. The panel knows the renderings, not the formats: a new
    /// format is a new previewer, not another arm of this method.
    /// </summary>
    private Task PresentPreviewAsync(FilePanelPreview preview)
    {
        _lastPreview = preview;
        return ApplyPreviewersAsync(preview);
    }

    /// <summary>
    /// The presentation currently being prepared. Reading a file — laying out
    /// a hex dump, indenting a document, parsing rows — is work, and it is not
    /// done on the thread that has to stay answering the reader.
    /// </summary>
    internal Task PreviewPresentation { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Rises with every presentation, so a reading that finishes after a newer
    /// one was asked for is dropped rather than drawn over it.
    /// </summary>
    private int _presentationGeneration;

    private async Task ApplyPreviewersAsync(FilePanelPreview preview)
    {
        var generation = ++_presentationGeneration;
        var operation = _preview;
        var source = new FilePreviewSource(
            PreviewTitle ?? string.Empty,
            preview.Kind,
            preview.MediaType,
            preview.Content,
            preview.IsTruncated);
        var chosen = new Dictionary<string, bool>(_previewToggleState, StringComparer.Ordinal);

        FilePreviewOutcome outcome;
        IsPreviewLoading = true;
        try
        {
            outcome = await Task.Run(
                () => _previewers.Create(source, chosen),
                operation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (generation == _presentationGeneration)
            {
                IsPreviewLoading = false;
            }
        }

        if (generation != _presentationGeneration
            || (operation is not null && !ReferenceEquals(_preview, operation)))
        {
            return;
        }

        Replace(
            PreviewToggles,
            outcome.Toggles.Select(toggle => new PreviewToggleViewModel(
                toggle,
                OnPreviewToggled)));

        // Each reading clears the ones it replaces — but a reading made of text
        // assigns straight over the old text rather than blanking it first.
        // Blanking makes every view bound to it tear down, and rebuilding a
        // Markdown document reinstalls a syntax grammar per fenced block.
        await ShowAsync(outcome.Rendering, preview, generation);
    }

    /// <summary>
    /// Puts a reading on screen without ever holding the thread that draws for
    /// longer than a step. Producing the reading happens away from this thread;
    /// attaching it happens here, because it must, but a handful at a time.
    /// </summary>
    private async Task ShowAsync(
        FilePreviewRendering rendering,
        FilePanelPreview preview,
        int generation)
    {
        using var attachment = CancellationTokenSource.CreateLinkedTokenSource(
            _preview?.Token ?? CancellationToken.None);
        var token = attachment.Token;
        try
        {
            await AttachAsync(rendering, preview, token);
        }
        catch (OperationCanceledException)
        {
        }

        _ = generation;
    }

    private async Task AttachAsync(
        FilePreviewRendering rendering,
        FilePanelPreview preview,
        CancellationToken token)
    {
        switch (rendering)
        {
            case SourcePreviewRendering rendered:
                ClearRenderingsOtherThanText();
                WrapPreviewText = rendered.Wrap;
                SetMarkdownRendering(false);
                PreviewText = rendered.Text;
                break;
            case MarkdownPreviewRendering markdown:
                ClearRenderingsOtherThanText();
                SetMarkdownRendering(true);
                PreviewText = markdown.Text;
                break;
            case HexPreviewRendering hex:
                ClearRenderedPreview();
                var bytes = new PreviewHexViewModel(hex);
                PreviewHex = bytes;
                await bytes.FillAsync(token);
                break;
            case BinaryPreviewRendering binary:
                ClearRenderedPreview();
                PreviewBinary = binary;
                break;
            case TablePreviewRendering table:
                ClearRenderedPreview();
                var rows = new PreviewTableViewModel(table);
                PreviewTable = rows;
                await rows.FillAsync(token);
                break;
            case ArchivePreviewRendering:
                ClearRenderedPreview();
                await OpenArchivePreviewAsync(preview.Location);
                break;
            case ImagePreviewRendering:
                ClearRenderedPreview();
                PresentImagePreview(preview);
                break;
            case PdfPreviewRendering:
                ClearRenderedPreview();
                _ = OpenPdfPreviewAsync(preview.Location);
                break;
            case WebPagePreviewRendering:
                ClearRenderedPreview();
                _ = OpenHtmlPreviewAsync(preview.Location);
                break;
            case DatabasePreviewRendering:
                ClearRenderedPreview();
                _ = OpenDatabasePreviewAsync(preview.Location);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(rendering),
                    rendering,
                    "The panel has no way to draw this rendering.");
        }
    }

    /// <summary>
    /// A switch was flipped. The bytes are already in hand, so the format is
    /// simply read again the other way — no provider call, no download.
    /// </summary>
    private void OnPreviewToggled(string id, bool isOn)
    {
        _previewToggleState[id] = isOn;
        if (_lastPreview is { } preview)
        {
            // Not awaited: the switch answers at once and the reading arrives
            // when it is ready, with the strip saying so meanwhile.
            // Deliberately not clearing the text first: nulling it makes every
            // view bound to it tear down and rebuild, and rebuilding a Markdown
            // document reinstalls a syntax grammar for each fenced block.
            PreviewPresentation = ApplyPreviewersAsync(preview);
        }
    }

    /// <summary>
    /// Clears what was drawn, keeping the file, its details and the chosen
    /// switches: this is a change of reading, not a change of file.
    /// </summary>
    /// <summary>
    /// Whether the text in hand is laid out as Markdown or shown as source.
    /// Announced here rather than left to the text itself: showing a Markdown
    /// file as its source is the same string read a different way, so nothing
    /// about the text changes and nothing about it would be announced.
    /// </summary>
    private void SetMarkdownRendering(bool isMarkdown)
    {
        if (_markdownRendering == isMarkdown)
        {
            return;
        }

        _markdownRendering = isMarkdown;
        OnPropertyChanged(nameof(HasMarkdownPreview));
        OnPropertyChanged(nameof(HasSourcePreview));
    }

    private void ClearRenderingsOtherThanText()
    {
        PreviewImage = null;
        PreviewTable = null;
        PreviewTree = null;
        PreviewBinary = null;
        PreviewHex = null;
        ClearHtmlPreview();
    }

    private void ClearRenderedPreview()
    {
        PreviewText = null;
        PreviewImage = null;
        PreviewTable = null;
        PreviewTree = null;
        PreviewBinary = null;
        PreviewHex = null;
        SetMarkdownRendering(false);
        WrapPreviewText = true;
        ClearHtmlPreview();
    }

    private async Task RefreshDiscoveryAsync()
    {
        try
        {
            _listingPages.Clear();
            _listingPages.Add(null);
            _listingPageIndex = 0;
            await RefreshAsync(_lifetime.Token);
            RestartObservation();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void ScheduleSearch()
    {
        var query = Filter.Trim();
        CancelSearch(clearResults: true);
        if (query.Length == 0
            || CurrentLocation is null
            || SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Search) != true)
        {
            ApplyFilter();
            UpdateListingStatus();
            return;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _search = operation;
        IsSearchLoading = true;
        ApplyFilter();
        UpdateListingStatus();
        _searchCompletion = CompleteSearchAsync(
            CurrentLocation,
            query,
            operation);
    }

    private async Task CompleteSearchAsync(
        FilePanelLocation location,
        string query,
        CancellationTokenSource operation)
    {
        try
        {
            await Task.Delay(SearchDebounce, operation.Token);
            var request = new FilePanelSearchRequest(
                location,
                query,
                FilePanelDiscoveryScope.Subtree,
                ShowHidden);
            var pendingPresentation = 0;
            var pageSize = Math.Min(500, SelectedProfile?.MaximumPageSize ?? 250);
            await foreach (var result in _client.SearchAsync(request, operation.Token))
            {
                if (!ReferenceEquals(_search, operation) || operation.IsCancellationRequested)
                {
                    return;
                }

                if (!result.IsSuccess)
                {
                    SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
                    return;
                }

                if (_searchEntries.Count >= pageSize)
                {
                    var demand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ApplyFilter();
                    _searchPageRequest = demand;
                    IsSearchLoading = false;
                    UpdateListingStatus();
                    NotifyPagingChanged();
                    await demand.Task.WaitAsync(operation.Token);
                    _searchEntries.Clear();
                    SelectedEntry = null;
                    IsSearchLoading = true;
                }

                _searchEntries.Add(result.Value!);
                pendingPresentation++;
                if (pendingPresentation >= 25)
                {
                    pendingPresentation = 0;
                    ApplyFilter();
                    UpdateListingStatus();
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_search, operation))
            {
                SetOperationIssue(FileOperationIssue.Unexpected(
                    "The file provider failed unexpectedly while searching this location."));
            }
        }
        finally
        {
            if (ReferenceEquals(_search, operation))
            {
                _search = null;
                operation.Dispose();
                _searchPageRequest = null;
                ApplyFilter();
                IsSearchLoading = false;
                UpdateListingStatus();
                NotifyPagingChanged();
            }
        }
    }

    private void CancelSearch(bool clearResults)
    {
        var operation = _search;
        _search = null;
        operation?.Cancel();
        operation?.Dispose();
        _searchCompletion = Task.CompletedTask;
        _searchPageRequest = null;
        IsSearchLoading = false;
        NotifyPagingChanged();
        if (clearResults)
        {
            _searchEntries.Clear();
        }
    }

    private void RestartObservation()
    {
        StopObservation();
        StartObservation();
    }

    private void StartObservation()
    {
        if (CurrentLocation is null
            || SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Watch) != true
            || _disposed)
        {
            return;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _watch = operation;
        _ = ObserveLocationAsync(
            new FilePanelWatchRequest(
                CurrentLocation,
                FilePanelDiscoveryScope.CurrentDirectory,
                ShowHidden,
                ObservationInterval),
            operation);
    }

    private async Task ObserveLocationAsync(
        FilePanelWatchRequest request,
        CancellationTokenSource operation)
    {
        try
        {
            await foreach (var result in _client.WatchAsync(request, operation.Token))
            {
                if (!ReferenceEquals(_watch, operation) || operation.IsCancellationRequested)
                {
                    return;
                }

                if (!result.IsSuccess)
                {
                    if (result.Error?.Retryable != true)
                    {
                        SetOperationIssue(FileOperationIssue.FromProvider(result.Error!));
                    }

                    continue;
                }

                await RefreshAsync(operation.Token);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (ReferenceEquals(_watch, operation))
            {
                // Observation is an enhancement over a valid listing, so its failure is operation
                // feedback rather than a replacement for files already on screen.
                SetOperationIssue(FileOperationIssue.Unexpected(
                    "Automatic file updates stopped unexpectedly. Refresh the location to retry."));
            }
        }
    }

    private void StopObservation()
    {
        var operation = _watch;
        _watch = null;
        operation?.Cancel();
        operation?.Dispose();
    }

    private void ApplyFilter()
    {
        var query = Filter.Trim();
        var selected = SelectedEntry;
        var usesProviderSearch = query.Length > 0
            && SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Search) == true;
        var source = usesProviderSearch
            ? _searchEntries
            : _allEntries;
        var visible = source
            .Where(item => usesProviderSearch
                || query.Length == 0
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item, Comparer<FilePanelEntry>.Create(CompareEntries))
            .Select(item => selected is not null && ReferenceEquals(selected.Entry, item)
                ? selected
                : new FileEntryViewModel(item))
            .ToArray();
        Replace(Entries, visible);
        if (selected is not null && !visible.Contains(selected))
        {
            SelectedEntry = null;
        }

        OnPropertyChanged(nameof(ShowEmptyState));
        OnContentPresentationChanged();
    }

    private void UpdateListingStatus()
    {
        if (!_hasLoadedListing || IsLoading || ContentIssue is not null)
        {
            return;
        }

        if (_allEntries.Count == 0)
        {
            Status = "This location is empty";
            ShortStatus = "0";
            return;
        }

        var usesProviderSearch = !string.IsNullOrWhiteSpace(Filter)
            && SelectedProfile?.Capabilities.HasFlag(FilePanelCapability.Search) == true;
        if (usesProviderSearch)
        {
            var resultCount = Entries.Count.ToString(CultureInfo.InvariantCulture);
            Status = IsSearchLoading
                ? $"{resultCount} search result(s) found…"
                : $"{resultCount} search result(s)";
            ShortStatus = resultCount + (IsSearchLoading ? "+" : string.Empty);
            return;
        }

        var loadedCount = _allEntries.Count.ToString(CultureInfo.InvariantCulture);
        var loadedLabel = HasMore
            ? $"{loadedCount} loaded item(s)"
            : $"{loadedCount} item(s)";
        var shownCount = Entries.Count.ToString(CultureInfo.InvariantCulture);
        var unfiltered = string.IsNullOrWhiteSpace(Filter);
        Status = unfiltered
            ? loadedLabel
            : $"{shownCount} of {loadedLabel}";
        // A trailing mark for a listing that has not been read to the end, so
        // the short form does not claim the folder holds exactly this many.
        ShortStatus = (unfiltered ? loadedCount : $"{shownCount}/{loadedCount}")
            + (HasMore ? "+" : string.Empty);
    }

    private int CompareEntries(FilePanelEntry? left, FilePanelEntry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var directoryOrder = CompareDirectoryGroup(left, right);
        if (directoryOrder != 0)
        {
            return directoryOrder;
        }

        var missingValueOrder = SortField switch
        {
            FileEntrySortField.Size => CompareMissing(left.Size is null, right.Size is null),
            FileEntrySortField.Modified => CompareMissing(
                left.LastModifiedAt is null,
                right.LastModifiedAt is null),
            _ => 0,
        };
        if (missingValueOrder != 0)
        {
            return missingValueOrder;
        }

        var primaryOrder = SortField switch
        {
            FileEntrySortField.Name => StringComparer.OrdinalIgnoreCase.Compare(
                left.Name,
                right.Name),
            FileEntrySortField.Kind => left.Kind.CompareTo(right.Kind),
            FileEntrySortField.Size => CompareNullable(left.Size, right.Size),
            FileEntrySortField.Modified => CompareNullable(
                left.LastModifiedAt,
                right.LastModifiedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(SortField), SortField, null),
        };
        if (primaryOrder != 0)
        {
            return SortDirection == FileEntrySortDirection.Ascending
                ? primaryOrder
                : -primaryOrder;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }

    private static int CompareDirectoryGroup(FilePanelEntry left, FilePanelEntry right) =>
        (left.Kind == FilePanelEntryKind.Directory ? 0 : 1)
        .CompareTo(right.Kind == FilePanelEntryKind.Directory ? 0 : 1);

    private static int CompareMissing(bool leftMissing, bool rightMissing) =>
        (leftMissing ? 1 : 0).CompareTo(rightMissing ? 1 : 0);

    private static int CompareNullable<T>(T? left, T? right)
        where T : struct, IComparable<T>
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        return right is null ? -1 : left.Value.CompareTo(right.Value);
    }

    private void ClearPreview()
    {
        _archiveLocation = null;
        _archiveOffset = 0;
        _archiveHasMore = false;
        NotifyArchivePagingChanged();
        PreviewImage = null;
        PreviewText = null;
        PreviewTable = null;
        PreviewTree = null;
        PreviewBinary = null;
        PreviewHex = null;
        PreviewIssue = null;
        PreviewTitle = "Preview";
        _requestedPreviewEntry = null;
        _pdfContent?.Dispose();
        _pdfContent = null;
        _lastPreview = null;
        SetMarkdownRendering(false);
        WrapPreviewText = true;
        PreviewToggles.Clear();
        ClearHtmlPreview();

        _pdfPageIndex = 0;
        _pdfPageCount = 0;
        NotifyPdfChanged();
        SetDeferredPreview(null);
        ClearDatabasePreview();
    }

    private void ClearHtmlPreview()
    {
        if (_htmlAddress is null)
        {
            return;
        }

        _htmlAddress = null;
        OnPropertyChanged(nameof(HtmlAddress));
        OnPropertyChanged(nameof(HasHtmlPreview));
    }

    /// <summary>
    /// A demand page replaces the preceding tree; all remaining entries are
    /// accessible without accumulating every archive node in the presentation.
    /// </summary>
    private const int ArchivePageSize = 250;

    public Task ChangeArchivePageAsync(int direction)
    {
        if (_archiveLocation is null || IsPreviewLoading
            || (direction > 0 ? !HasNextArchivePage : !HasPreviousArchivePage))
        {
            return Task.CompletedTask;
        }

        return OpenArchivePreviewAsync(_archiveLocation,
            Math.Max(0, checked(_archiveOffset + (direction > 0 ? ArchivePageSize : -ArchivePageSize))));
    }

    private void NotifyArchivePagingChanged()
    {
        OnPropertyChanged(nameof(HasNextArchivePage));
        OnPropertyChanged(nameof(HasPreviousArchivePage));
        _nextArchivePageCommand.RaiseCanExecuteChanged();
        _previousArchivePageCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The ceiling on an archive read for its listing. Nothing is unpacked —
    /// a zip is answered from the index at its end — but that index lives at
    /// the end of the file, so a remote archive is fetched whole into the
    /// preview cache and listed from there.
    /// </summary>
    private const long MaximumArchivePreviewBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Lists an archive's contents without extracting any of it.
    /// </summary>
    private async Task OpenArchivePreviewAsync(FilePanelLocation location, int offset = 0)
    {
        if (_archiveReader is null || _contentSource is null)
        {
            PreviewText = "Archives cannot be listed on this system.";
            return;
        }

        var operation = _preview;
        if (operation is null)
        {
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var opened = await _contentSource.OpenContentAsync(
                location,
                MaximumArchivePreviewBytes,
                operation.Token);
            if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
            {
                return;
            }

            if (!opened.IsSuccess)
            {
                PreviewText = opened.Error!.Message;
                PreviewIssue = FileOperationIssue.FromProvider(opened.Error);
                return;
            }

            using var content = opened.Value!;
            var entries = await _archiveReader.ReadAsync(
                content,
                PreviewTitle,
                ArchivePageSize + 1,
                operation.Token,
                offset);
            if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
            {
                return;
            }

            if (entries is null)
            {
                PreviewText = "This file could not be read as an archive.";
                return;
            }

            _archiveLocation = location;
            _archiveOffset = offset;
            _archiveHasMore = entries.Count > ArchivePageSize;
            var page = entries.Take(ArchivePageSize).ToArray();
            var listing = new PreviewTreeViewModel(
                PreviewTreeBuilder.FromPaths(page),
                $"Page {(offset / ArchivePageSize) + 1}: {SummarizeArchive(page)}");
            PreviewTree = listing;
            // Attached in steps like every other reading; without this the
            // listing exists and nothing of it is on screen.
            await listing.FillAsync(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
                NotifyArchivePagingChanged();
            }
        }
    }

    private static string SummarizeArchive(IReadOnlyList<ArchiveEntryDescriptor> entries)
    {
        var files = entries.Count(entry => !entry.IsDirectory);
        var bytes = entries.Sum(entry => entry.Size ?? 0);
        var counted = files == 1 ? "1 file" : $"{files} files";
        return bytes > 0
            ? $"{counted}, {PreviewTreeBuilder.FormatSize(bytes)} unpacked"
            : counted;
    }

    /// <summary>
    /// Disposes the previewed database. Its file, when it was a downloaded
    /// copy, stays in the materializer's cache so selecting the same database
    /// again does not download it a second time.
    /// </summary>
    /// <summary>
    /// The ceiling on a database opened through the file preview. A remote
    /// database is copied in full before it can be opened — a partial copy is a
    /// corrupt database — so the limit is what we are willing to pull, not what
    /// we are willing to render.
    /// </summary>
    private const long MaximumDatabasePreviewBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Opens the selected file with the database viewer. A local file opens
    /// where it is; a remote one is downloaded as bytes and served to the
    /// engine from memory — its content never becomes a file on this machine.
    /// </summary>
    internal static string ReadOnlySqliteFileConnectionString(string path) =>
        new System.Data.Common.DbConnectionStringBuilder
        {
            ["Data Source"] = path,
            ["Mode"] = "ReadOnly",
        }.ConnectionString;

    private async Task OpenDatabasePreviewAsync(FilePanelLocation location)
    {
        if (_databaseClient is null || _contentSource is null)
        {
            PreviewText = "This build cannot open database files in the preview.";
            return;
        }

        var operation = _preview;
        if (operation is null)
        {
            return;
        }

        IsPreviewLoading = true;
        string? connectionString = null;
        string? registration = null;
        FilePanelResult<FilePreviewContent> result;
        try
        {
            result = await _contentSource.OpenContentAsync(
                location,
                MaximumDatabasePreviewBytes,
                operation.Token);
            if (result.IsSuccess)
            {
                using var content = result.Value!;
                if (content.LocalPath is { } path)
                {
                    // Read-only: previewing a file must not write a journal
                    // beside the user's own database.
                    connectionString = ReadOnlySqliteFileConnectionString(path);
                }
                else if (_databaseRegistry is not null)
                {
                    registration = _databaseRegistry.Register(
                        await content.ReadAllBytesAsync(operation.Token));
                    connectionString = registration;
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "The database could not be opened.";
            }

            return;
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }

        // The selection moved while the download was in flight. The bytes stay
        // in the cache: the work is done, and selecting this file again should
        // find them there rather than fetch them twice.
        if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
        {
            if (registration is not null)
            {
                _databaseRegistry!.Unregister(registration);
            }

            return;
        }

        if (!result.IsSuccess)
        {
            var error = result.Error!;
            PreviewText = error.Message;
            PreviewIssue = FileOperationIssue.FromProvider(error);
            return;
        }

        if (connectionString is null)
        {
            PreviewText = "This build cannot open remote database files in the preview.";
            return;
        }

        var viewer = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            PreviewTitle,
            _databaseClient,
            SqliteDriverId,
            connectionString,
            forcedReadOnlyReason: "Database file previews are read-only.");
        _databaseMemoryRegistration = registration;
        _databasePreview = viewer;
        OnPropertyChanged(nameof(DatabasePreview));
        OnPropertyChanged(nameof(HasDatabasePreview));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(ShowPreviewPlaceholder));
        OnPropertyChanged(nameof(ShowPreviewProgressDetail));
        await viewer.ConnectAsync();
    }

    /// <summary>
    /// Shows an image, decoding it first when the drawing stack cannot read the
    /// format. Formats it can read are drawn from the preview bytes already in
    /// hand; the rest need the whole file, which is fetched the same way a
    /// database is — cached, and gated on remote providers.
    /// </summary>
    /// <summary>
    /// Shows an image from the whole file, always.
    ///
    /// The bounded preview read is a head of the file, and a head of a JPEG is
    /// not a smaller JPEG — it decodes to noise or not at all. Any image large
    /// enough to be cut off was therefore being drawn as garbage, so every
    /// image is materialized and read from disk.
    /// </summary>
    private void PresentImagePreview(FilePanelPreview preview)
    {
        _ = DecodeImagePreviewAsync(preview.Location);
    }

    private async Task DecodeImagePreviewAsync(FilePanelLocation location)
    {
        if (_contentSource is null)
        {
            PreviewText = "This client cannot open whole images.";
            return;
        }

        var operation = _preview;
        if (operation is null)
        {
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var opened = await _contentSource.OpenContentAsync(
                location,
                MaximumImagePreviewBytes,
                operation.Token);
            if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
            {
                return;
            }

            if (!opened.IsSuccess)
            {
                PreviewText = opened.Error!.Message;
                PreviewIssue = FileOperationIssue.FromProvider(opened.Error);
                return;
            }

            using var content = opened.Value!;
            if (_imageDecoder?.Claims(PreviewTitle) == true)
            {
                var decoded = await _imageDecoder.DecodeAsync(
                    content,
                    PreviewRasterBudget.MaximumPixels,
                    operation.Token);
                if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
                {
                    return;
                }

                if (decoded is null)
                {
                    PreviewText = "The image data could not be decoded safely.";
                    return;
                }

                using var decodedStream = new MemoryStream(
                    decoded.PngBytes.ToArray(),
                    writable: false);
                PreviewImage = new Bitmap(decodedStream);
                PreviewText = null;
                return;
            }

            var bitmap = await Task.Run(
                () => OrdinaryImagePreviewDecoder.Decode(content),
                operation.Token);
            if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }

            if (bitmap is null)
            {
                PreviewText = "The image data could not be decoded safely.";
                return;
            }

            PreviewImage = bitmap;
            PreviewText = null;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "The image could not be opened.";
            }
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>
    /// The ceiling on an image opened through the preview. Generous next to a
    /// bounded read because a partial image is not a smaller image, but still
    /// far short of what a scanner or camera can produce.
    /// </summary>
    private const long MaximumImagePreviewBytes = 128L * 1024 * 1024;

    /// <summary>
    /// The page the embedded browser should show. A local file is shown from
    /// where it lives; a remote one is carried to the browser as markup, so its
    /// bytes never land on this machine. Null until a web page is being previewed.
    /// </summary>
    public BrowserAddress? HtmlAddress => _htmlAddress;

    public bool HasHtmlPreview => _htmlAddress is not null;

    /// <summary>
    /// Opens a previewed web page in the same browser engine the browser panel uses,
    /// so it renders as the page its author wrote. A local file is opened by
    /// its own URL — relative subresources beside it keep working. A remote
    /// file has no URL this machine can open, so its markup is handed to the
    /// browser as a string and no copy of it is ever written to disk.
    /// </summary>
    private async Task OpenHtmlPreviewAsync(FilePanelLocation location)
    {
        if (_contentSource is null)
        {
            PreviewText = "This client cannot open web pages.";
            return;
        }

        var operation = _preview;
        if (operation is null)
        {
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var opened = await _contentSource.OpenContentAsync(
                location,
                MaximumHtmlPreviewBytes,
                operation.Token);
            if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
            {
                return;
            }

            if (!opened.IsSuccess)
            {
                PreviewText = opened.Error!.Message;
                PreviewIssue = FileOperationIssue.FromProvider(opened.Error);
                return;
            }

            using var content = opened.Value!;
            if (content.LocalPath is { } path)
            {
                _htmlAddress = BrowserAddress.ForLocalFile(path);
            }
            else
            {
                using var reader = new StreamReader(
                    content.OpenRead(),
                    // The BOM wins when present; without one HTML is UTF-8 in
                    // practice, and a wrong guess mangles glyphs, not safety.
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                _htmlAddress = BrowserAddress.ForDocument(
                    await reader.ReadToEndAsync(operation.Token));
            }

            OnPropertyChanged(nameof(HtmlAddress));
            OnPropertyChanged(nameof(HasHtmlPreview));
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(ShowPreviewPlaceholder));
            OnPropertyChanged(nameof(ShowPreviewProgressDetail));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is UriFormatException or IOException)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "The web page could not be opened.";
            }
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>
    /// The ceiling on a previewed web page. Far below the image ceiling it
    /// used to share: the page becomes one string in memory and then a
    /// browser document, and hundreds of megabytes of markup serve nobody.
    /// </summary>
    private const long MaximumHtmlPreviewBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The width a PDF page is rasterized at. Generous so the fitted page stays
    /// sharp on a large panel; the view scales it down to fit.
    /// </summary>
    private const int PdfPageWidth = 1600;

    public bool HasPdfPreview => _pdfPageCount > 0;

    public string PdfPageStatus => _pdfPageCount == 0
        ? string.Empty
        : $"Page {_pdfPageIndex + 1} of {_pdfPageCount}";

    public bool CanTurnPdfPageBack => _pdfPageCount > 0 && _pdfPageIndex > 0;

    public bool CanTurnPdfPageForward => _pdfPageIndex + 1 < _pdfPageCount;

    public Task TurnPdfPageAsync(int delta)
    {
        var target = _pdfPageIndex + delta;
        if (_pdfContent is null || target < 0 || target >= _pdfPageCount)
        {
            return Task.CompletedTask;
        }

        _pdfPageIndex = target;
        return RenderPdfPageAsync(_preview);
    }

    /// <summary>
    /// Opens a PDF and shows its first page. The content is opened once and
    /// paged through from there, so turning a page costs a render rather than
    /// another download.
    /// </summary>
    private async Task OpenPdfPreviewAsync(FilePanelLocation location)
    {
        if (_pdfRenderer is null || _contentSource is null)
        {
            PreviewText = "This build cannot open PDF files in the preview.";
            return;
        }

        var operation = _preview;
        if (operation is null)
        {
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var opened = await _contentSource.OpenContentAsync(
                location,
                MaximumImagePreviewBytes,
                operation.Token);
            if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
            {
                return;
            }

            if (!opened.IsSuccess)
            {
                PreviewText = opened.Error!.Message;
                PreviewIssue = FileOperationIssue.FromProvider(opened.Error);
                return;
            }

            _pdfContent = opened.Value!;
            _pdfPageIndex = 0;
            _pdfPageCount = await _pdfRenderer.CountPagesAsync(_pdfContent, operation.Token);
            if (_pdfPageCount == 0)
            {
                PreviewText = "This PDF could not be opened; it may be damaged or encrypted.";
                return;
            }

            await RenderPdfPageAsync(operation);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (ReferenceEquals(_preview, operation))
            {
                PreviewText = "The PDF could not be opened.";
            }
        }
        finally
        {
            if (ReferenceEquals(_preview, operation))
            {
                IsPreviewLoading = false;
            }
        }
    }

    private async Task RenderPdfPageAsync(CancellationTokenSource? operation)
    {
        if (_pdfRenderer is null || _pdfContent is null || operation is null)
        {
            return;
        }

        var page = await _pdfRenderer.RenderPageAsync(
            _pdfContent,
            _pdfPageIndex,
            PdfPageWidth,
            operation.Token);
        if (!ReferenceEquals(_preview, operation) || operation.IsCancellationRequested)
        {
            return;
        }

        if (page is null)
        {
            PreviewText = "That page could not be rendered.";
            return;
        }

        using var stream = new MemoryStream(page.PngBytes.ToArray(), writable: false);
        PreviewImage = new Bitmap(stream);
        NotifyPdfChanged();
    }

    private void NotifyPdfChanged()
    {
        OnPropertyChanged(nameof(HasPdfPreview));
        OnPropertyChanged(nameof(PdfPageStatus));
        OnPropertyChanged(nameof(CanTurnPdfPageBack));
        OnPropertyChanged(nameof(CanTurnPdfPageForward));
    }

    private const string SqliteDriverId = "sqlite";

    private void ClearDatabasePreview()
    {
        var preview = _databasePreview;
        _databasePreview = null;
        if (_databaseMemoryRegistration is { } registration)
        {
            _databaseMemoryRegistration = null;
            // Safe against a query still in flight: the registry keeps the
            // image alive until the last borrowed connection closes.
            _databaseRegistry?.Unregister(registration);
        }

        if (preview is not null)
        {
            OnPropertyChanged(nameof(DatabasePreview));
            OnPropertyChanged(nameof(HasDatabasePreview));
        }

        preview?.Dispose();
    }

    private void ClearMetadata()
    {
        _metadata?.Cancel();
        SelectedMetadata = null;
        MetadataIssue = null;
        IsMetadataLoading = false;
    }

    private void SetContentIssue(FileOperationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        OperationIssue = null;
        ContentIssue = issue;
    }

    private void SetOperationIssue(FileOperationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        OperationIssue = issue;
    }

    private void ResetListing()
    {
        _allEntries.Clear();
        _continuationToken = null;
        _hasLoadedListing = false;
        OnPropertyChanged(nameof(HasListingSummary));
        SelectedEntry = null;
        ClearMetadata();
        _preview?.Cancel();
        IsPreviewLoading = false;
        ClearPreview();
        Replace(Entries, []);
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnContentPresentationChanged();
    }

    private void PublishIssueState()
    {
        OnPropertyChanged(nameof(CurrentIssue));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorTitle));
        OnPropertyChanged(nameof(ErrorSuggestedAction));
        OnPropertyChanged(nameof(CanRetryError));
    }

    private void OnContentPresentationChanged()
    {
        OnPropertyChanged(nameof(ContentPresentation));
        OnPropertyChanged(nameof(ContentState));
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowNavigationProgress));
        OnPropertyChanged(nameof(ShowEmptyLocationState));
        OnPropertyChanged(nameof(ShowSearchNoResultsState));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(CanRetryContentState));
        OnPropertyChanged(nameof(ShowEmptyState));
        _retryCommand.RaiseCanExecuteChanged();
    }

    private static string ChildLocationDisplay(FilePanelLocation parent, string name)
    {
        var displayed = FileLocationPresentation.Display(parent);
        return parent.Address switch
        {
            FilePanelAddress.Hierarchical => string.Equals(displayed, "/"
, StringComparison.Ordinal) ? $"/{name}"
                : $"{displayed.TrimEnd('/')}/{name}",
            FilePanelAddress.ObjectKey or FilePanelAddress.ContainerRoot =>
                displayed.Length == 0 ? name : $"{displayed.TrimEnd('/')}/{name}",
            _ => name,
        };
    }

    /// <summary>
    /// The filesystem root the local provider is rooted at, which is what its
    /// paths are relative to.
    /// </summary>
    private static string LocalRootPath() =>
        Path.GetPathRoot(BuiltInHomePath()) is { Length: > 0 } root
            ? root
            : BuiltInHomePath();

    private static string BuiltInHomePath()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? Path.GetFullPath(AppContext.BaseDirectory)
            : Path.GetFullPath(path);
    }

    private static bool IsWithinDirectory(string directory, string candidate)
    {
        var relativePath = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, ".."
, StringComparison.Ordinal) && !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private CancellationTokenSource ReplaceNavigation(CancellationToken cancellationToken)
    {
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        return _navigation;
    }

    private static string FormatJson(ReadOnlySpan<byte> content, bool truncated)
    {
        if (truncated)
        {
            return Encoding.UTF8.GetString(content) + "\n\n[preview truncated]";
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       stream,
                       new JsonWriterOptions { Indented = true }))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(
                stream.GetBuffer(),
                0,
                checked((int)stream.Length));
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(content);
        }
    }

    private static string FormatHex(ReadOnlySpan<byte> content, bool providerTruncated)
    {
        var shown = content[..Math.Min(content.Length, MaximumFormattedBinaryBytes)];
        var builder = new StringBuilder((shown.Length / 16 + 1) * 72);
        for (var offset = 0; offset < shown.Length; offset += 16)
        {
            var row = shown.Slice(offset, Math.Min(16, shown.Length - offset));
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
            builder.Append("  ");
            for (var index = 0; index < 16; index++)
            {
                if (index < row.Length)
                {
                    builder.Append(row[index].ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("  ");
                }

                builder.Append(index == 7 ? "  " : " ");
            }

            builder.Append(" | ");
            foreach (var value in row)
            {
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            builder.AppendLine();
        }

        if (providerTruncated || shown.Length < content.Length)
        {
            builder.AppendLine("[preview truncated]");
        }

        return builder.ToString();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
