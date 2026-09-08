using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using GhostShell.Application;
using GhostShell.Application.Previews;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// A generic multi-driver database viewer: pick a driver, connect with an
/// ADO.NET connection string, browse tables, and run bounded queries. All
/// engine specifics stay behind <see cref="IDatabasePanelClient"/>; the panel
/// holds no open connection between operations.
/// </summary>
public sealed class DatabaseRuntimePanelViewModel : RuntimePanelViewModel
{
    private enum DatabaseResultSource
    {
        None,
        StructuredTable,
        RawQuery,
    }

    /// <summary>Result sets are display pages, not exports; the cap keeps the grid honest.</summary>
    public const int MaxRows = 500;

    /// <summary>Largest page the database browser will materialize into the grid.</summary>
    public const int MaximumPageRows = 5000;

    /// <summary>Maximum UTF-8 payload returned by a clipboard-oriented string builder.</summary>
    public const int MaximumClipboardUtf8Bytes = DatabaseGridExport.MaximumClipboardUtf8Bytes;

    private const int PreviewRows = 200;
    private const int MaximumFilterListValues = 500;
    private const int MaximumFilterListCharacters = 64 * 1024;

    private readonly IDatabasePanelClient _client;
    private DatabaseValueContentStore? _resultContent;
    private IDisposable? _resultContentLease;
    private CancellationTokenSource? _clipboardBuildCancellation;
    private long _clipboardRevision;
    private readonly ISqlLanguageService? _sqlLanguageService;
    private readonly Func<DatabaseConnectionProfile, CancellationToken, Task<string?>>? _passwordResolver;
    private readonly Func<DatabaseConnectionProfileId, string, CancellationToken,
        Task<DatabaseConnectionProfile?>>? _passwordPersister;
    private readonly string? _forcedReadOnlyReason;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _hostBindingId = SessionId.New().Value;
    private HostedPanelSessionLink? _hostedSession;
    private ISessionHostClient? _hostSessionClient;
    private Task _hostInitialization = Task.CompletedTask;
    private long _hostBindingRevision;
    private bool _disposed;
    private ConnectionProfile? _tunnelConnection;
    private IReadOnlyList<string> _databases = [];
    private string? _selectedDatabase;
    private bool _suppressDatabaseSwitch;
    private DatabaseSessionInfo _sessionInfo = new();
    private bool _isPersistedConnection = true;
    private DatabaseConnectionProfile? _savedConnection;
    private string? _sessionPassword;
    private readonly DatabaseRecoveryPayload _initialRecoveryInput;
    private DatabaseRecoveryPayload? _acceptedInitialRecoveryBinding;
    private DatabaseRecoveryPayload? _latestResolvedRecoveryBinding;
    private readonly DatabaseRecoveryState? _recovery;
    private ConnectionProfile? _savedTunnel;
    private readonly bool _deferStoredCredentialAccess;
    private bool _initializationStarted;
    private bool _importedConnectionRequired;
    private DatabaseDriverOptionViewModel _selectedDriver;
    private string _connectionString = string.Empty;
    private string _queryText = string.Empty;
    private bool _isBusy;
    private bool _isConnected;
    private string? _errorMessage;
    private string? _interchangeNotice;
    private string _resultSummary = string.Empty;
    private IReadOnlyList<DatabaseTableItemViewModel> _allTables = [];
    private string _tableFilter = string.Empty;
    private IReadOnlyList<DatabaseResultColumnViewModel> _resultColumns = [];
    private IReadOnlyList<DatabaseResultRowViewModel> _resultRows = [];
    private DatabaseResultRowViewModel? _selectedRow;
    private IReadOnlyList<DatabaseRowFieldViewModel> _selectedRowFields = [];
    private DatabaseTableItemViewModel? _selectedObject;
    private DatabaseObjectDetails? _selectedObjectDetails;
    private DatabaseObjectDetails? _queryProvenanceCandidate;
    private DatabaseWorkspaceMode _selectedMode;
    private IReadOnlyList<DatabaseStructureColumnViewModel> _structureColumns = [];
    private IReadOnlyList<DatabaseIndexViewModel> _indexes = [];
    private IReadOnlyList<DatabaseFilterColumnViewModel> _filterColumns = [];
    private bool _isDatabaseOverview;
    private DatabaseObjectId? _pendingInitialObject;
    private DatabaseOverviewMode _databaseOverviewMode;
    private string _mermaidDiagramSource = string.Empty;
    private string _mermaidDiagramText = string.Empty;
    private DatabaseTableQuery _tableQuery = DatabaseTableQuery.FirstPage(PreviewRows);
    private DatabaseResultSource _resultSource;
    private string? _rawQuerySql;
    private IReadOnlyList<DatabaseColumnDescriptor> _rawQueryColumns = [];
    private bool _rawQueryCanBrowse;
    private bool _hasNextPage;
    private long _totalRows;
    private string _pageLimitText = PreviewRows.ToString(CultureInfo.InvariantCulture);
    private readonly List<DatabaseResultRowViewModel> _deletedRows = [];
    private CancellationTokenSource? _tableLoadCancellation;
    private long _tableLoadGeneration;
    private CancellationTokenSource? _sqlLanguageLoadCancellation;
    private ISqlLanguageSession? _sqlLanguageSession;
    private long _sqlLanguageLoadGeneration;
    private string _sqlLanguageStatus = "SQL intelligence is unavailable in this build.";

    private static readonly IReadOnlyList<DatabaseFilterOperatorViewModel> AllFilterOperators =
    [
        new(DatabaseFilterOperator.Equal, "Equals"),
        new(DatabaseFilterOperator.NotEqual, "Does not equal"),
        new(DatabaseFilterOperator.LessThan, "Less than"),
        new(DatabaseFilterOperator.GreaterThan, "Greater than"),
        new(DatabaseFilterOperator.LessThanOrEqual, "At most"),
        new(DatabaseFilterOperator.GreaterThanOrEqual, "At least"),
        new(DatabaseFilterOperator.Contains, "Contains"),
        new(DatabaseFilterOperator.NotContains, "Does not contain"),
        new(DatabaseFilterOperator.StartsWith, "Starts with"),
        new(DatabaseFilterOperator.EndsWith, "Ends with"),
        new(DatabaseFilterOperator.In, "In"),
        new(DatabaseFilterOperator.NotIn, "Not in"),
        new(DatabaseFilterOperator.IsNull, "Is NULL"),
        new(DatabaseFilterOperator.IsNotNull, "Is not NULL"),
    ];

    public DatabaseRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IDatabasePanelClient client,
        string? driverId = null,
        string? connectionString = null,
        ConnectionProfile? tunnelConnection = null,
        DatabaseConnectionProfile? savedConnection = null,
        Func<DatabaseConnectionProfile, CancellationToken, Task<string?>>? passwordResolver = null,
        string? forcedReadOnlyReason = null,
        DatabaseObjectId? initialObject = null,
        ISqlLanguageService? sqlLanguageService = null,
        Func<DatabaseConnectionProfileId, string, CancellationToken,
            Task<DatabaseConnectionProfile?>>? passwordPersister = null,
        string passwordStoreLabel = "Save in system credential store",
        bool deferStoredCredentialAccess = false,
        string? sessionPassword = null,
        DatabaseRecoveryState? recovery = null,
        bool persistedConnection = true)
        : base(id, PanelKind.DatabaseViewer, title, "Database")
    {
        _pendingInitialObject = initialObject;
        _tunnelConnection = tunnelConnection?.Endpoint is ConnectionEndpoint.Ssh
            ? tunnelConnection
            : null;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sqlLanguageService = sqlLanguageService;
        _passwordResolver = passwordResolver;
        _sessionPassword = sessionPassword;
        _recovery = recovery;
        _savedTunnel = tunnelConnection;
        _isPersistedConnection = persistedConnection;
        _deferStoredCredentialAccess = deferStoredCredentialAccess;
        _passwordPersister = passwordPersister;
        PasswordStoreLabel = string.IsNullOrWhiteSpace(passwordStoreLabel)
            ? "Save in system credential store"
            : passwordStoreLabel;
        _forcedReadOnlyReason = string.IsNullOrWhiteSpace(forcedReadOnlyReason)
            ? null
            : forcedReadOnlyReason;
        DriverOptions = [.. client.Drivers.Select(descriptor => new DatabaseDriverOptionViewModel(descriptor))];
        if (DriverOptions.Count == 0)
        {
            throw new ArgumentException(
                "The database client exposes no drivers.",
                nameof(client));
        }

        _savedConnection = savedConnection;
        var effectiveDriverId = driverId ?? savedConnection?.DriverId;
        _selectedDriver = DriverOptions.FirstOrDefault(option =>
                string.Equals(option.Id, effectiveDriverId, StringComparison.Ordinal))
            ?? DriverOptions[0];
        _connectionString = connectionString ?? savedConnection?.ConnectionString ?? string.Empty;
        _initialRecoveryInput = CaptureRecoveryInput();
        ConnectCommand = new AsyncActionCommand(
            ConnectAsync,
            () => CanChangeConnection && HasConnectionTarget);
        DisconnectCommand = new AsyncActionCommand(
            () =>
            {
                Disconnect();
                return Task.CompletedTask;
            },
            () => IsConnected && CanChangeConnection);
        RunQueryCommand = new AsyncActionCommand(
            RunQueryAsync,
            () => !IsBusy && IsConnected && !HasPendingChanges);
        // Recovery may construct this panel during application startup. A
        // stored database or tunnel credential must not make that construction
        // open the OS credential store before the user presses Connect.
        Initialization = recovery is null && !string.IsNullOrWhiteSpace(_connectionString)
            && (savedConnection is not null || driverId is not null)
            && !NeedsPasswordPrompt
            && !(deferStoredCredentialAccess && RequiresStoredCredentialAccess)
            ? ConnectAsync()
            : Task.CompletedTask;
    }

    /// <summary>Raised when connecting needs a password only the user can supply.</summary>
    public event EventHandler? PasswordRequested;

    public SessionId? HostedSessionId => _hostedSession?.SessionId;

    public CapabilitySet HostedCapabilities =>
        _hostedSession?.Capabilities ?? CapabilitySet.Empty;

    public bool HasHostedSession => _hostedSession?.IsLinked == true;

    /// <summary>
    /// Admits agent reachability only after MainWindow has registered the
    /// panel's exact workspace owner with SessionHost.
    /// </summary>
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
            PanelKind.DatabaseViewer);
        _hostInitialization = InitializeHostedSessionAsync();
        return _hostInitialization;
    }

    public bool IsSavedConnection => _savedConnection is not null;

    public DatabaseConnectionProfileId? SavedConnectionId => _savedConnection?.Id;

    public string? SavedConnectionName => _savedConnection?.Name;

    public bool CanStorePassword =>
        _passwordPersister is not null
        && _isPersistedConnection
        && _savedConnection is { PasswordSecret: null };

    public string PasswordStoreLabel { get; }

    /// <summary>What the address bar shows: the saved name, or the masked string.</summary>
    public string AddressBarText => _savedConnection?.Name ?? MaskedConnectionString;

    /// <summary>
    /// Binds this panel to a connection profile: driver, address, and tunnel
    /// become the profile's, and connecting resolves the stored password — or
    /// asks for one. A session password supplied by the editor avoids
    /// re-asking for what the user just typed. A non-persisted profile (the
    /// editor's "connect without saving") behaves identically but recovers as
    /// a raw target rather than a dangling saved reference.
    /// </summary>
    public void ApplySavedConnection(
        DatabaseConnectionProfile profile,
        string? sessionPassword = null,
        ConnectionProfile? tunnel = null,
        bool persisted = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before changing the connection.";
            return;
        }

        InvalidateHostedBinding();
        _savedConnection = profile;
        _isPersistedConnection = persisted;
        _savedTunnel = tunnel;
        _sessionPassword = string.IsNullOrEmpty(sessionPassword) ? null : sessionPassword;
        _tunnelConnection = tunnel?.Endpoint is ConnectionEndpoint.Ssh ? tunnel : null;
        var driver = DriverOptions.FirstOrDefault(option =>
            string.Equals(option.Id, profile.DriverId, StringComparison.Ordinal));
        if (driver is not null)
        {
            _selectedDriver = driver;
            OnPropertyChanged(nameof(SelectedDriver));
        }

        SetDatabases([]);
        SessionInfo = new DatabaseSessionInfo();
        ConnectionString = profile.ConnectionString;
        OnPropertyChanged(nameof(IsSavedConnection));
        OnPropertyChanged(nameof(SavedConnectionName));
        OnPropertyChanged(nameof(CanStorePassword));
        OnPropertyChanged(nameof(AddressBarText));
        OnPropertyChanged(nameof(RecoveryTarget));
        OnPropertyChanged(nameof(TunnelConnectionId));
        OnPropertyChanged(nameof(ConnectionDisplayName));
        _ = ConnectAsync();
    }

    /// <summary>The prompt's answer; an empty value means connect without one.</summary>
    public void SetSessionPassword(string password)
    {
        InvalidateHostedBinding();
        _sessionPassword = password ?? string.Empty;
    }

    public async Task<bool> StoreSessionPasswordAsync(
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!CanStorePassword
            || _savedConnection is not { } profile
            || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var saved = await _passwordPersister!(profile.Id, password, cancellationToken);
        if (saved is null)
        {
            return false;
        }

        InvalidateHostedBinding();
        _savedConnection = saved;
        _sessionPassword = null;
        OnPropertyChanged(nameof(CanStorePassword));
        return true;
    }

    private bool NeedsPasswordPrompt =>
        _savedConnection is not null
        && !SelectedDriver.IsFileBased
        && _savedConnection.PasswordSecret is null
        && _sessionPassword is null
        && _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString).Password is null;

    private bool RequiresStoredCredentialAccess =>
        _savedConnection?.PasswordSecret is not null
        || _tunnelConnection?.Authentication is
            ConnectionAuthentication.Password or ConnectionAuthentication.PrivateKey;

    /// <summary>
    /// The string handed to the engine: a saved connection gets its password
    /// injected from the session or the vault; everything else passes through.
    /// </summary>
    private async Task<string> ResolveEffectiveConnectionStringAsync(
        CancellationToken cancellationToken)
    {
        if (SelectedDriver.IsFileBased)
        {
            return ConnectionString;
        }

        var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
        if (details.Password is not null)
        {
            return ConnectionString;
        }

        var password = _sessionPassword;
        if (string.IsNullOrEmpty(password)
            && _savedConnection?.PasswordSecret is not null
            && _passwordResolver is not null)
        {
            password = await _passwordResolver(_savedConnection, cancellationToken);
        }

        if (string.IsNullOrEmpty(password))
        {
            return ConnectionString;
        }

        return _client.BuildConnectionString(SelectedDriver.Id, details with { Password = password });
    }

    public IReadOnlyList<DatabaseDriverOptionViewModel> DriverOptions { get; }

    public ObservableCollection<DatabaseTableItemViewModel> Tables { get; } = [];

    /// <summary>Lets tests and restore await the initial automatic connection.</summary>
    public Task Initialization { get; private set; }

    public void StartInitialization()
    {
        if (_initializationStarted || _recovery is null || _disposed)
        {
            return;
        }
        _initializationStarted = true;
        if (HasConnectionTarget && !NeedsPasswordPrompt
            && !(_deferStoredCredentialAccess && RequiresStoredCredentialAccess))
        {
            Initialization = ConnectAsync();
        }
    }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand RunQueryCommand { get; }

    public DatabaseWorkspaceMode SelectedMode
    {
        get => _selectedMode;
        private set
        {
            if (SetProperty(ref _selectedMode, value))
            {
                OnPropertyChanged(nameof(ShowData));
                OnPropertyChanged(nameof(ShowStructure));
                OnPropertyChanged(nameof(ShowIndexes));
                // The data surface derives from the mode too; without this the
                // data grid stays visible underneath the structure view.
                OnPropertyChanged(nameof(ShowDataSurface));
            }
        }
    }

    public bool ShowData => SelectedMode == DatabaseWorkspaceMode.Data;

    public bool ShowStructure => SelectedMode == DatabaseWorkspaceMode.Structure;

    public bool ShowIndexes => SelectedMode == DatabaseWorkspaceMode.Indexes;

    public DatabaseOverviewMode SelectedDatabaseOverviewMode
    {
        get => _databaseOverviewMode;
        private set
        {
            if (SetProperty(ref _databaseOverviewMode, value))
            {
                OnPropertyChanged(nameof(IsDatabaseObjectsOverview));
                OnPropertyChanged(nameof(IsDatabaseDiagramOverview));
                OnPropertyChanged(nameof(ShowQueryEditor));
                OnPropertyChanged(nameof(ShowDataSurface));
            }
        }
    }

    public bool IsDatabaseObjectsOverview => IsDatabaseOverview
        && SelectedDatabaseOverviewMode == DatabaseOverviewMode.Objects;

    public bool IsDatabaseDiagramOverview => IsDatabaseOverview
        && SelectedDatabaseOverviewMode == DatabaseOverviewMode.ErDiagram;

    public bool ShowQueryEditor => ShowData && !IsDatabaseDiagramOverview;

    public bool ShowDataSurface => ShowData && !IsDatabaseDiagramOverview;

    /// <summary>The raw Mermaid source consumed by the native SVG renderer.</summary>
    public string MermaidDiagramSource
    {
        get => _mermaidDiagramSource;
        private set => SetProperty(ref _mermaidDiagramSource, value);
    }

    public string MermaidDiagramText
    {
        get => _mermaidDiagramText;
        private set => SetProperty(ref _mermaidDiagramText, value);
    }

    private IDatabaseDiagramSession? _diagramSession;
    private CancellationTokenSource? _diagramCancellation;

    public IDatabaseDiagramSession? DiagramSession => _diagramSession;

    public bool HasMermaidDiagram => _diagramSession is not null;

    public void SuspendDatabaseDiagram()
    {
        var mode = SelectedDatabaseOverviewMode;
        ResetDatabaseDiagram();
        SelectedDatabaseOverviewMode = mode;
    }

    public async Task ExportDatabaseDiagramAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (_diagramSession is { } session)
        {
            await session.ExportAsync(destination, format, linked.Token);
            return;
        }

        if (format != DatabaseDiagramExport.MermaidMarkdown)
        {
            throw new InvalidOperationException("The diagram must render before SVG can be exported. Choose Mermaid Markdown to export the complete schema without rendering.");
        }

        await _client.ExportDatabaseSchemaSourceAsync(SelectedDriver.Id,
            await ResolveEffectiveConnectionStringAsync(linked.Token), _tunnelConnection, destination, linked.Token);
    }

    /// <summary>
    /// The optional, credential-free Calcite session consumed directly by the
    /// AvaloniaEdit surface. It appears only after the detached catalog loads;
    /// database browsing remains usable when the native worker is absent.
    /// </summary>
    public ISqlLanguageSession? SqlLanguageSession
    {
        get => _sqlLanguageSession;
        private set
        {
            if (SetProperty(ref _sqlLanguageSession, value))
            {
                OnPropertyChanged(nameof(HasSqlLanguageSession));
            }
        }
    }

    public bool HasSqlLanguageSession => SqlLanguageSession?.IsAvailable == true;

    public string SqlLanguageStatus
    {
        get => _sqlLanguageStatus;
        private set => SetProperty(ref _sqlLanguageStatus, value);
    }

    /// <summary>
    /// Request-scoped completion preference from the sidebar. The selected
    /// object never changes SQL validation or the worker catalog's defaults.
    /// </summary>
    public SqlCompletionContext SqlLanguageCompletionContext =>
        new(SelectedObject?.Descriptor.Id);

    /// <summary>Observable by tests and diagnostics; never blocks connecting.</summary>
    public Task SqlLanguageInitialization { get; private set; } = Task.CompletedTask;

    public DatabaseTableItemViewModel? SelectedObject => _selectedObject;

    public bool HasSelectedObject => _selectedObject is not null;

    public string SelectedObjectName => _selectedObject?.Name ?? "Query results";

    public string ObjectPickerLabel => _selectedObject?.Name ?? "Objects";

    public IReadOnlyList<DatabaseStructureColumnViewModel> StructureColumns
    {
        get => _structureColumns;
        private set => SetProperty(ref _structureColumns, value);
    }

    public IReadOnlyList<DatabaseIndexViewModel> Indexes
    {
        get => _indexes;
        private set => SetProperty(ref _indexes, value);
    }

    public IReadOnlyList<DatabaseFilterColumnViewModel> FilterColumns
    {
        get => _filterColumns;
        private set
        {
            if (SetProperty(ref _filterColumns, value))
            {
                RebuildFilterRows();
            }
        }
    }

    /// <summary>
    /// The stackable filter bar: one row per condition, each with its own
    /// include switch. Applying reads every included, complete row.
    /// </summary>
    public ObservableCollection<DatabaseFilterRowViewModel> FilterRows { get; } = [];

    /// <summary>The first row, which the single-filter surface reads and writes.</summary>
    private DatabaseFilterRowViewModel PrimaryFilterRow
    {
        get
        {
            if (FilterRows.Count == 0)
            {
                FilterRows.Add(CreateFilterRow());
            }

            return FilterRows[0];
        }
    }

    public void AddFilterRow(DatabaseFilterRowViewModel? after = null)
    {
        var index = after is null ? FilterRows.Count - 1 : FilterRows.IndexOf(after);
        FilterRows.Insert(index + 1, CreateFilterRow());
    }

    /// <summary>Removing the last row leaves one blank row, never an empty bar.</summary>
    public void RemoveFilterRow(DatabaseFilterRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        FilterRows.Remove(row);
        if (FilterRows.Count == 0)
        {
            FilterRows.Add(CreateFilterRow());
        }

        RaisePrimaryFilterChanged();
    }

    private DatabaseFilterRowViewModel CreateFilterRow()
    {
        var row = new DatabaseFilterRowViewModel(
            _filterColumns,
            kind => FilterOperatorsFor(kind, includeListOperators: true));
        row.PropertyChanged += (sender, _) =>
        {
            if (FilterRows.Count > 0 && ReferenceEquals(sender, FilterRows[0]))
            {
                RaisePrimaryFilterChanged();
            }
        };
        return row;
    }

    /// <summary>
    /// The columns changed under the bar: keep every condition that still names
    /// a live column, drop the rest, and never present an empty bar.
    /// </summary>
    private void RebuildFilterRows()
    {
        var kept = FilterRows
            .Where(row => row.Column is not null)
            .Select(row => (
                ColumnName: row.Column!.Name,
                Operator: row.Operator?.Operator,
                row.Value,
                row.IsIncluded))
            .ToArray();
        FilterRows.Clear();
        foreach (var previous in kept)
        {
            var column = _filterColumns.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, previous.ColumnName, StringComparison.Ordinal));
            if (column is null)
            {
                continue;
            }

            var row = CreateFilterRow();
            row.Column = column;
            if (previous.Operator is { } filterOperator)
            {
                row.Operator = row.Operators.FirstOrDefault(option =>
                        option.Operator == filterOperator)
                    ?? row.Operator;
            }

            row.Value = previous.Value;
            row.IsIncluded = previous.IsIncluded;
            FilterRows.Add(row);
        }

        if (FilterRows.Count == 0)
        {
            FilterRows.Add(CreateFilterRow());
        }

        RaisePrimaryFilterChanged();
    }

    private void RaisePrimaryFilterChanged()
    {
        OnPropertyChanged(nameof(FilterColumn));
        OnPropertyChanged(nameof(FilterOperator));
        OnPropertyChanged(nameof(FilterValue));
        OnPropertyChanged(nameof(FilterOperators));
        OnPropertyChanged(nameof(FilterNeedsValue));
    }

    public IReadOnlyList<DatabaseFilterOperatorViewModel> FilterOperators =>
        PrimaryFilterRow.Operators;

    public DatabaseFilterColumnViewModel? FilterColumn
    {
        get => PrimaryFilterRow.Column;
        set => PrimaryFilterRow.Column = value;
    }

    public DatabaseFilterOperatorViewModel? FilterOperator
    {
        get => PrimaryFilterRow.Operator;
        set => PrimaryFilterRow.Operator = value;
    }

    public string FilterValue
    {
        get => PrimaryFilterRow.Value;
        set => PrimaryFilterRow.Value = value;
    }

    public bool FilterNeedsValue => PrimaryFilterRow.NeedsValue;

    public bool CanEditRows => _forcedReadOnlyReason is null
        && _selectedObjectDetails?.CanEdit == true;

    public bool CanMutateRows => CanEditRows && !IsBusy;

    public bool CanDeleteSelectedRow => CanMutateRows && SelectedRow is not null;

    public bool CanDuplicateSelectedRow => CanMutateRows && SelectedRow?.IsValid == true;

    public bool CanCopySelectedRowAsInsert => _selectedObjectDetails?.Object.Kind
        == DatabaseTableKind.Table
        && SelectedRow?.IsValid == true;

    public bool CanSetSelectedCellNull => CanMutateRows
        && SelectedRow?.Cells.Any(cell => cell.CanSetNull) == true;

    public bool CanSetSelectedCellDefault => CanMutateRows
        && SelectedRow is { IsNew: true } row
        && row.Cells.Any(cell => cell.CanSetDefault);

    public bool CanChangeSelectedObject => !HasPendingChanges && !IsBusy;

    public bool CanChangeConnection => CanChangeSelectedObject;

    public bool CanFilterTable => CanBrowseCurrentResults
        && FilterColumns.Count > 0
        && !HasPendingChanges
        && !IsBusy;

    public bool CanRefreshTable => CanBrowseCurrentResults
        && !HasPendingChanges
        && !IsBusy;

    public bool CanSortTable => CanBrowseCurrentResults
        && ResultColumns.Count > 0
        && !HasPendingChanges
        && !IsBusy;

    public bool CanChangePageLimit => CanBrowseCurrentResults
        && ResultColumns.Count > 0
        && !HasPendingChanges
        && !IsBusy;

    private bool CanBrowseCurrentResults =>
        _resultSource == DatabaseResultSource.StructuredTable
        || (_resultSource == DatabaseResultSource.RawQuery && _rawQueryCanBrowse);

    public bool CanGoToPreviousPage => HasPreviousPage
        && CanFilterTable
        && CanPageCurrentResults;

    public bool CanGoToNextPage => HasNextPage
        && CanFilterTable
        && CanPageCurrentResults;

    private bool CanPageCurrentResults => _resultSource == DatabaseResultSource.StructuredTable
        || (_resultSource == DatabaseResultSource.RawQuery
            && _rawQueryCanBrowse
            && _tableQuery.Sorts.Count > 0
            && HasCompleteRawResultKey);

    private bool HasCompleteRawResultKey
    {
        get
        {
            var expectedKeys = _queryProvenanceCandidate?.PrimaryKey;
            if (expectedKeys is null || expectedKeys.Count == 0)
            {
                return false;
            }

            var projectedKeys = _rawQueryColumns
                .Where(column => column.IsKey)
                .Select(column => column.BaseColumnName ?? column.Name)
                .ToHashSet(StringComparer.Ordinal);
            return projectedKeys.Count == expectedKeys.Count
                && expectedKeys.All(key => projectedKeys.Contains(key.Name));
        }
    }

    public bool CanRevertChanges => HasPendingChanges && !IsBusy;

    public string? ReadOnlyReason => _forcedReadOnlyReason
        ?? (_selectedObjectDetails is null
            ? _resultSource == DatabaseResultSource.RawQuery
                ? _rawQueryCanBrowse
                    ? "This query result does not map exactly to one editable table."
                    : "This statement result can be copied or exported, but not rerun, "
                        + "filtered, sorted, or edited safely."
                : "Run a table preview to edit rows."
            : _selectedObjectDetails.ReadOnlyReason);

    // A forced reason is a mode of the whole panel, not news about the
    // selected object: the embedded preview is read-only by construction, and
    // saying so beside disabled buttons was noise. Object-specific reasons
    // still show — they explain something the person could change.
    public bool HasReadOnlyReason => !CanEditRows
        && ReadOnlyReason is not null
        && _forcedReadOnlyReason is null;

    public bool HasPreviousPage => _tableQuery.Offset > 0;

    public bool HasNextPage => _hasNextPage;

    public long TotalRows => _totalRows;

    public string TotalRowsText => TotalRows.ToString(CultureInfo.InvariantCulture);

    public string PageLimitText
    {
        get => _pageLimitText;
        set => SetProperty(ref _pageLimitText, value ?? string.Empty);
    }

    public bool HasPendingChanges => _deletedRows.Count > 0
        || ResultRows.Any(row => row.IsDirty);

    public bool CanSaveChanges => CanEditRows
        && !IsBusy
        && HasPendingChanges
        && ResultRows.All(row => row.IsValid);

    public DatabaseDriverOptionViewModel SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (HasPendingChanges)
            {
                ErrorMessage = "Save or revert the pending row changes before changing database driver.";
                OnPropertyChanged(nameof(SelectedDriver));
                return;
            }

            if (SetProperty(ref _selectedDriver, value))
            {
                InvalidateHostedBinding();
                SetConnected(false);
                ClearSelectedObject();
                OnPropertyChanged(nameof(RecoveryTarget));
            }
        }
    }

    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (HasPendingChanges)
            {
                ErrorMessage = "Save or revert the pending row changes before changing the connection.";
                OnPropertyChanged(nameof(ConnectionString));
                OnPropertyChanged(nameof(MaskedConnectionString));
                OnPropertyChanged(nameof(AddressBarText));
                return;
            }

            if (SetProperty(ref _connectionString, value ?? string.Empty))
            {
                InvalidateHostedBinding();
                SetConnected(false);
                ClearSelectedObject();
                OnPropertyChanged(nameof(MaskedConnectionString));
                OnPropertyChanged(nameof(AddressBarText));
            }
        }
    }

    private static readonly Regex PasswordAssignment = new(
        "(?<key>\\b(?:password|pwd|passphrase)\\s*=\\s*)[^;]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// What the address bar shows while not being edited: the connection
    /// string with any password value replaced by dots. The real value stays
    /// in <see cref="ConnectionString"/>.
    /// </summary>
    public string MaskedConnectionString =>
        PasswordAssignment.Replace(ConnectionString, match =>
            match.Groups["key"].Value + "••••••");

    /// <summary>The current string decomposed for the details dialog.</summary>
    public DatabaseConnectionDetails ParseConnectionDetails() =>
        _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);

    /// <summary>
    /// Applies dialog fields and probes the connection right away. Editing raw
    /// fields detaches the panel from any saved connection.
    /// </summary>
    public Task ApplyConnectionDetailsAsync(DatabaseConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before changing the connection.";
            return Task.CompletedTask;
        }

        _savedConnection = null;
        _sessionPassword = null;
        OnPropertyChanged(nameof(IsSavedConnection));
        OnPropertyChanged(nameof(SavedConnectionName));
        OnPropertyChanged(nameof(CanStorePassword));
        ConnectionString = _client.BuildConnectionString(SelectedDriver.Id, details);
        OnPropertyChanged(nameof(AddressBarText));
        OnPropertyChanged(nameof(RecoveryTarget));
        return string.IsNullOrWhiteSpace(ConnectionString)
            ? Task.CompletedTask
            : ConnectAsync();
    }

    public string QueryText
    {
        get => _queryText;
        set => SetProperty(ref _queryText, value ?? string.Empty);
    }

    /// <summary>Filters the objects sidebar by substring, TablePlus-style.</summary>
    public string TableFilter
    {
        get => _tableFilter;
        set
        {
            if (SetProperty(ref _tableFilter, value ?? string.Empty))
            {
                RefreshTables();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
                PublishInteractionStates();
            }
        }
    }

    public bool IsConnected => _isConnected;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    public string? InterchangeNotice
    {
        get => _interchangeNotice;
        private set
        {
            if (SetProperty(ref _interchangeNotice, value))
            {
                OnPropertyChanged(nameof(HasInterchangeNotice));
            }
        }
    }

    public bool HasInterchangeNotice => InterchangeNotice is not null;

    public string StatusText => IsBusy
        ? "Working…"
        : IsConnected
            ? $"Connected · {Tables.Count} objects"
            : "Not connected";

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public IReadOnlyList<DatabaseResultColumnViewModel> ResultColumns
    {
        get => _resultColumns;
        private set
        {
            if (SetProperty(ref _resultColumns, value))
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }
    }

    public IReadOnlyList<DatabaseResultRowViewModel> ResultRows
    {
        get => _resultRows;
        private set
        {
            if (SetProperty(ref _resultRows, value))
            {
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(ShowEmptyHint));
            }
        }
    }

    public bool HasResults => ResultColumns.Count > 0;

    public bool ShowEmptyHint => !HasResults;

    public DatabaseResultRowViewModel? SelectedRow => _selectedRow;

    public bool HasSelectedRow => _selectedRow is not null;

    public string SelectedRowTitle => _selectedRow is { } row ? $"Row {row.Number}" : string.Empty;

    public IReadOnlyList<DatabaseRowFieldViewModel> SelectedRowFields
    {
        get => _selectedRowFields;
        private set => SetProperty(ref _selectedRowFields, value);
    }

    /// <summary>
    /// Selects one row for the field inspector; selecting the current row again
    /// or passing null clears the inspector.
    /// </summary>
    public void SelectRow(DatabaseResultRowViewModel? row)
    {
        if (ReferenceEquals(_selectedRow, row))
        {
            row = null;
        }

        _selectedRow?.IsSelected = false;

        _selectedRow = row;
        row?.IsSelected = true;

        RefreshSelectedRowFields();
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasSelectedRow));
        OnPropertyChanged(nameof(SelectedRowTitle));
        PublishInteractionStates();
    }

    private void RefreshSelectedRowFields()
    {
        foreach (var field in SelectedRowFields)
        {
            field.Dispose();
        }

        SelectedRowFields = _selectedRow is null
            ? []
            : ResultColumns
                .Zip(
                    _selectedRow.Cells,
                    (column, cell) => new DatabaseRowFieldViewModel(column, cell))
                .ToArray();
    }

    /// <summary>
    /// An unchanged saved profile can retain its catalog address. Any live target
    /// change needs an immutable confidential recovery entry instead.
    /// </summary>
    private bool UsesUnmodifiedSavedTarget => _savedConnection is { } saved && _isPersistedConnection
        && string.Equals(SelectedDriver.Id, saved.DriverId, StringComparison.Ordinal)
        && string.Equals(ConnectionString, saved.ConnectionString, StringComparison.Ordinal)
        && _tunnelConnection == _savedTunnel
        && _sessionPassword is null;

    public string? RecoveryTarget => UsesUnmodifiedSavedTarget && _savedConnection is { } saved
        ? $"saved:{saved.Id.Value}"
        : _importedConnectionRequired && !HasConnectionTarget
            ? DatabaseRecoveryToken.ReconnectTarget
            : _recovery?.Target;

    internal bool CanPreserveInitialSourceConnection => _acceptedInitialRecoveryBinding is not null
        && _latestResolvedRecoveryBinding == _acceptedInitialRecoveryBinding
        && CaptureRecoveryInput() == _initialRecoveryInput
        && SourceDefinition is { } source
        && _initialRecoveryInput.MatchesSourceTarget(source, RecoveryTarget);

    private DatabaseRecoveryPayload CaptureRecoveryInput() =>
        new(SelectedDriver.Id, ConnectionString, _sessionPassword, _tunnelConnection, _savedConnection);

    internal void RequireImportedConnection()
    {
        _importedConnectionRequired = true;
        ErrorMessage = "This imported database panel needs a connection. Its device-local target and credentials were not imported.";
        OnPropertyChanged(nameof(RecoveryTarget));
    }

    public Task RecoveryPersistence { get; private set; } = Task.CompletedTask;

    private async Task PersistRecoveryAsync(string? effectiveConnectionString = null)
    {
        if (_recovery is null || UsesUnmodifiedSavedTarget || !HasConnectionTarget || _disposed)
        {
            return;
        }
        try
        {
            var saved = await _recovery.SaveAsync(new(SelectedDriver.Id, effectiveConnectionString ?? ConnectionString,
                _sessionPassword, _tunnelConnection, _savedConnection), _lifetime.Token);
            if (!_disposed)
            {
                if (!saved)
                {
                    ErrorMessage = "The database target could not be saved in the credential store. The previous saved target is unchanged.";
                    if (_recovery.CleanupIncomplete)
                    {
                        ErrorMessage += " An unused recovery credential may need removal in Security & secrets.";
                    }
                }
                OnPropertyChanged(nameof(RecoveryTarget));
            }
        }
        catch (OperationCanceledException) when (_disposed || _lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>The SSH connection queries tunnel through, or null for direct.</summary>
    public ConnectionId? TunnelConnectionId => _tunnelConnection?.Id;

    /// <summary>
    /// The tunnel profile itself — a twin panel on the same connection needs
    /// the profile, because an inline tunnel is not in any catalog.
    /// </summary>
    public ConnectionProfile? TunnelConnection => _tunnelConnection;

    internal DatabaseConnectionProfile? BoundConnectionProfile => _savedConnection;

    internal string? SessionPassword => _sessionPassword;

    /// <summary>
    /// The connection pill's label: the profile this panel is bound to, or an
    /// invitation when it has nothing to connect to yet.
    /// </summary>
    public string ConnectionDisplayName => _savedConnection?.Name
        ?? (string.IsNullOrWhiteSpace(ConnectionString)
            ? "Select connection"
            : SelectedDriver.DisplayName);

    /// <summary>Something to connect to exists — a target, saved or raw.</summary>
    public bool HasConnectionTarget => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>One button reads as the action it would perform.</summary>
    public string ConnectButtonLabel => IsConnected ? "Reconnect" : "Connect";

    /// <summary>Session facts read after connecting; empty when unknown.</summary>
    public DatabaseSessionInfo SessionInfo
    {
        get => _sessionInfo;
        private set
        {
            if (SetProperty(ref _sessionInfo, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    /// <summary>Databases the connected principal may switch to.</summary>
    public IReadOnlyList<string> Databases => _databases;

    /// <summary>The selector shows only when there is a real choice to make.</summary>
    public bool HasDatabaseChoices => _databases.Count > 0;

    /// <summary>
    /// The database the session is in. Picking another rebuilds the address
    /// with it and reconnects — which is what USE means everywhere.
    /// </summary>
    public string? SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            if (!SetProperty(ref _selectedDatabase, value)
                || _suppressDatabaseSwitch
                || value is null)
            {
                return;
            }

            _ = SwitchDatabaseAsync(value);
        }
    }

    /// <summary>
    /// The status bar's account of the session: engine and version, transport
    /// security, route, principal, database, and selected object. Never the
    /// connection string.
    /// </summary>
    public string ConnectionSummary
    {
        get
        {
            if (!IsConnected)
            {
                return string.Empty;
            }

            var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
            var facts = new List<string>
            {
                SessionInfo.ServerVersion is { } version
                    ? $"{SelectedDriver.DisplayName} {version}"
                    : SelectedDriver.DisplayName,
            };
            if (SessionInfo.TlsProtocol is { } tls)
            {
                facts.Add(tls);
            }

            if (_tunnelConnection is { } tunnel)
            {
                facts.Add($"SSH:{tunnel.Name}");
            }

            if (details.Username is { } user)
            {
                facts.Add(user);
            }

            var database = SelectedDatabase ?? details.Database;
            if (!string.IsNullOrEmpty(database))
            {
                facts.Add(database);
            }

            if (_selectedObject is { } selected)
            {
                facts.Add(selected.Name);
            }

            return string.Join(" : ", facts);
        }
    }

    private void SetDatabases(IReadOnlyList<string> databases)
    {
        _databases = databases;
        OnPropertyChanged(nameof(Databases));
        OnPropertyChanged(nameof(HasDatabaseChoices));
    }

    /// <summary>
    /// Reads the optional session facts after a proven connection: version and
    /// TLS for the status bar, the database list for the selector. A probe the
    /// server refuses leaves the facts empty — the connection itself already
    /// succeeded.
    /// </summary>
    private async Task RefreshSessionFactsAsync(CancellationToken cancellationToken)
    {
        var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
        try
        {
            SessionInfo = await _client.DescribeSessionAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            SessionInfo = new DatabaseSessionInfo();
        }

        var databases = Array.Empty<string>() as IReadOnlyList<string>;
        if (SelectedDriver.CanListDatabases)
        {
            try
            {
                databases = await _client.ListDatabasesAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                databases = [];
            }
        }

        SetDatabases(databases);
        _suppressDatabaseSwitch = true;
        try
        {
            SelectedDatabase = _client
                .ParseConnectionDetails(SelectedDriver.Id, ConnectionString)
                .Database;
        }
        finally
        {
            _suppressDatabaseSwitch = false;
        }

        OnPropertyChanged(nameof(ConnectionSummary));
    }

    private async Task SwitchDatabaseAsync(string database)
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before switching databases.";
            return;
        }

        var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
        if (string.Equals(details.Database, database, StringComparison.Ordinal))
        {
            return;
        }

        // Keep the saved profile unchanged; persist the selected database as a
        // separate confidential session target.
        ConnectionString = _client.BuildConnectionString(
            SelectedDriver.Id,
            details with { Database = database });
        await ConnectAsync();
    }

    /// <summary>
    /// The database overview is on screen: the sidebar header highlights and
    /// the object perspectives (Data/Structure/Indexes) do not apply.
    /// </summary>
    public bool IsDatabaseOverview => _isDatabaseOverview;

    /// <summary>
    /// The one place the selected object changes: the sidebar highlight and
    /// the overview flag follow it everywhere.
    /// </summary>
    private void SetSelectedObjectItem(DatabaseTableItemViewModel? item)
    {
        _selectedObject = item;
        _isDatabaseOverview = false;
        foreach (var table in _allTables)
        {
            table.IsSelected = ReferenceEquals(table, item);
        }

        // A restored object may not be in the sidebar list; it still reads
        // as selected wherever it is shown.
        item?.IsSelected = true;

        OnPropertyChanged(nameof(IsDatabaseOverview));
        OnPropertyChanged(nameof(IsDatabaseObjectsOverview));
        OnPropertyChanged(nameof(IsDatabaseDiagramOverview));
        OnPropertyChanged(nameof(ShowQueryEditor));
        OnPropertyChanged(nameof(ShowDataSurface));
        OnPropertyChanged(nameof(SqlLanguageCompletionContext));
    }

    /// <summary>The name the sidebar's database header wears.</summary>
    public string CurrentDatabaseLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
            {
                return SelectedDriver.DisplayName;
            }

            try
            {
                var details = _client.ParseConnectionDetails(SelectedDriver.Id, ConnectionString);
                return SelectedDatabase
                    ?? details.Database
                    ?? (details.FilePath is { } path ? Path.GetFileName(path) : null)
                    ?? SelectedDriver.DisplayName;
            }
            catch (Exception exception)
                when (exception is ArgumentException or FormatException)
            {
                return SelectedDriver.DisplayName;
            }
        }
    }

    /// <summary>
    /// The database itself as the selected object: a read-only catalog of its
    /// objects — schema, name, kind — in the result grid. Built from what
    /// connecting already listed, so every engine answers identically.
    /// </summary>
    public void ShowDatabaseOverview()
    {
        if (IsDatabaseDiagramOverview)
        {
            ResetDatabaseDiagram();
        }

        if (IsBusy || !IsConnected)
        {
            return;
        }

        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before leaving this object.";
            return;
        }

        ClearSelectedObject();
        SelectedDatabaseOverviewMode = DatabaseOverviewMode.Objects;
        var columns = new[]
        {
            new DatabaseColumnDescriptor("Schema", "TEXT", DatabaseValueKind.Text),
            new DatabaseColumnDescriptor("Name", "TEXT", DatabaseValueKind.Text),
            new DatabaseColumnDescriptor("Kind", "TEXT", DatabaseValueKind.Text),
        };
        double[] widths = [180, 280, 100];
        ResultColumns = [.. columns.Select((column, ordinal) => new DatabaseResultColumnViewModel(column, widths[ordinal]))];
        ResultRows = [.. _allTables
            .Select((table, index) => new DatabaseResultRowViewModel(
                index + 1,
                [
                    table.Descriptor.Schema ?? table.Descriptor.Catalog,
                    table.Descriptor.Name,
                    table.KindLabel,
                ],
                widths))];
        ResultSummary = $"{_allTables.Count} objects · {CurrentDatabaseLabel}";
        _isDatabaseOverview = true;
        OnPropertyChanged(nameof(IsDatabaseOverview));
        OnPropertyChanged(nameof(IsDatabaseObjectsOverview));
        OnPropertyChanged(nameof(IsDatabaseDiagramOverview));
        OnPropertyChanged(nameof(ShowQueryEditor));
        OnPropertyChanged(nameof(ShowDataSurface));
    }

    /// <summary>
    /// Opens the database-wide Mermaid ER source. The graph is loaded lazily
    /// and cached for this connected catalog; reconnecting invalidates it.
    /// </summary>
    public async Task ShowDatabaseDiagramAsync()
    {
        if (IsBusy || !IsConnected)
        {
            return;
        }

        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before leaving this object.";
            return;
        }

        if (!IsDatabaseOverview)
        {
            ShowDatabaseOverview();
        }

        SelectedDatabaseOverviewMode = DatabaseOverviewMode.ErDiagram;
        if (HasMermaidDiagram)
        {
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            _diagramCancellation?.Cancel();
            _diagramCancellation?.Dispose();
            _diagramCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var diagramToken = _diagramCancellation.Token;
            IDatabaseDiagramSession session;
            try
            {
                session = await _client.OpenDatabaseDiagramAsync(
                    SelectedDriver.Id,
                    await ResolveEffectiveConnectionStringAsync(diagramToken),
                    _tunnelConnection,
                    diagramToken);
            }
            catch (OperationCanceledException) when (diagramToken.IsCancellationRequested)
            {
                return;
            }

            if (diagramToken.IsCancellationRequested || !IsDatabaseDiagramOverview)
            {
                await session.DisposeAsync();
                return;
            }

            _diagramSession = session;
            OnPropertyChanged(nameof(DiagramSession));
            OnPropertyChanged(nameof(HasMermaidDiagram));
        });
    }

    /// <summary>
    /// Forgets the session without touching what it pointed at: tables, the
    /// database list, and session facts clear; the bound connection stays so
    /// Connect brings it back.
    /// </summary>
    public void Disconnect()
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before disconnecting.";
            return;
        }

        SetConnected(false);
        ClearSelectedObject();
        _allTables = [];
        RefreshTables();
        SetDatabases([]);
        SessionInfo = new DatabaseSessionInfo();
        ErrorMessage = null;
        InvalidateHostedSession();
    }

    /// <summary>
    /// Routes queries through an SSH local port-forward over the given
    /// connection; a null or non-SSH connection means a direct connection. A
    /// connected panel re-probes through the new route immediately.
    /// </summary>
    public void SetTunnel(ConnectionProfile? connection)
    {
        var tunnel = connection?.Endpoint is ConnectionEndpoint.Ssh ? connection : null;
        if (tunnel == _tunnelConnection)
        {
            return;
        }

        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before changing the connection route.";
            return;
        }

        _tunnelConnection = tunnel;
        InvalidateHostedBinding();
        OnPropertyChanged(nameof(TunnelConnectionId));
        OnPropertyChanged(nameof(ConnectionDisplayName));
        SetConnected(false);
        ClearSelectedObject();
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            _ = ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before reconnecting.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            ErrorMessage = "Enter a connection string first.";
            return;
        }

        if (NeedsPasswordPrompt)
        {
            PasswordRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            _latestResolvedRecoveryBinding = null;
            var input = CaptureRecoveryInput();
            var connectionString =
                await ResolveEffectiveConnectionStringAsync(cancellationToken);
            RecoveryPersistence = PersistRecoveryAsync(connectionString);
            await RecoveryPersistence;
            var tables = await _client.ListTablesAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                cancellationToken);
            _latestResolvedRecoveryBinding = input with { ConnectionString = connectionString };
            if (input == _initialRecoveryInput && CaptureRecoveryInput() == input)
            {
                _acceptedInitialRecoveryBinding ??= _latestResolvedRecoveryBinding;
            }
            ResetDatabaseDiagram();
            _allTables = [.. tables.Select(table => new DatabaseTableItemViewModel(table))];
            RefreshTables();
            SetConnected(true);
            OnPropertyChanged(nameof(RecoveryTarget));
            await RefreshSessionFactsAsync(cancellationToken);
            QueueHostedSessionEnsure(connectionString);
        });

        if (IsConnected)
        {
            BeginSqlLanguageInitialization();

            // A panel born pointing at one object opens straight onto it —
            // "open in new tab/panel" means that object, not the overview.
            if (_pendingInitialObject is { } target)
            {
                _pendingInitialObject = null;
                if (_allTables.FirstOrDefault(table => table.Descriptor.Id == target)
                    is { } table)
                {
                    await PreviewTableAsync(table);
                    return;
                }
            }

            // A connected database is itself the initial selection. This
            // avoids the contradictory startup state where the database was
            // highlighted in the sidebar but table perspectives were active.
            ShowDatabaseOverview();
        }
    }

    public async Task RunQueryAsync()
    {
        if (HasPendingChanges)
        {
            ErrorMessage = "Save or revert the pending row changes before running SQL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            ErrorMessage = "Enter a statement to run.";
            return;
        }

        await ExecuteQueryAsync(QueryText);
    }

    public async Task PreviewTableAsync(DatabaseTableItemViewModel table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (IsBusy)
        {
            return;
        }

        if (HasPendingChanges)
        {
            ErrorMessage ??= "Save or revert the pending row changes before opening another object.";
            return;
        }

        var preview = _client.BuildTablePreviewQuery(
            SelectedDriver.Id,
            table.Descriptor.Id,
            PreviewRows);
        SetSelectedObjectItem(table);
        _selectedObjectDetails = null;
        _queryProvenanceCandidate = null;
        _tableQuery = DatabaseTableQuery.FirstPage(PreviewRows);
        _resultSource = DatabaseResultSource.None;
        _rawQuerySql = null;
        _rawQueryColumns = [];
        _rawQueryCanBrowse = false;
        ResultRows = [];
        ResultColumns = [];
        SelectRow(null);
        QueryText = preview;
        SelectedMode = DatabaseWorkspaceMode.Data;
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(SelectedObjectName));
        OnPropertyChanged(nameof(ObjectPickerLabel));
        PublishTableCapabilities();
        await LoadSelectedTableAsync(loadDetails: true);
    }

    public void SetMode(DatabaseWorkspaceMode mode)
    {
        SelectedMode = mode == DatabaseWorkspaceMode.Data || SelectedObject is not null
            ? mode
            : DatabaseWorkspaceMode.Data;
    }

    public async Task ApplyFilterAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage())
        {
            return;
        }

        if (FilterRows.All(row => !row.IsComplete))
        {
            ErrorMessage = "Choose a column and operator first.";
            return;
        }

        var conditions = new List<DatabaseFilterCondition>();
        foreach (var row in FilterRows)
        {
            if (!row.IsIncluded || !row.IsComplete)
            {
                continue;
            }

            if (!TryParseFilterValue(
                    row.Value,
                    row.Column!.ValueKind,
                    row.Operator!.Operator,
                    out var value,
                    out var validationError))
            {
                ErrorMessage = $"{row.Column.Name}: {validationError}";
                return;
            }

            conditions.Add(new DatabaseFilterCondition(
                row.Column.Name,
                row.Operator.Operator,
                value));
        }

        // Every row unchecked is a deliberate "none of these": the query runs
        // unfiltered rather than refusing.
        var query = new DatabaseTableQuery(
            conditions,
            _tableQuery.Sorts,
            Offset: 0,
            Limit: _tableQuery.Limit);
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    /// <summary>
    /// Operators valid for the selected cell's semantic type. NULL has its own
    /// two predicates; presenting comparisons against a fake text token would
    /// change its database meaning.
    /// </summary>
    public IReadOnlyList<DatabaseFilterOperatorViewModel> GetQuickFilterOperators(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (cell is null || cell.IsDefault)
        {
            return [];
        }

        return cell.IsNull
            ? [.. AllFilterOperators.Where(option => option.Operator is
                DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull)]
            : FilterOperatorsFor(cell.Column.ValueKind, includeListOperators: true);
    }

    public async Task ApplyQuickFilterAsync(
        int ordinal,
        DatabaseFilterOperator filterOperator)
    {
        if (IsBusy || !CanDiscardCurrentPage())
        {
            return;
        }

        var cell = GetSelectedCell(ordinal);
        if (cell is null
            || ordinal >= ResultColumns.Count)
        {
            return;
        }

        var option = GetQuickFilterOperators(ordinal).FirstOrDefault(candidate =>
            candidate.Operator == filterOperator);
        if (option is null)
        {
            ErrorMessage = $"{filterOperator} is not available for {cell.Column.Name}.";
            return;
        }

        var filterColumn = FilterColumns.FirstOrDefault(column =>
            string.Equals(column.Name, cell.Column.Name, StringComparison.Ordinal));
        if (filterColumn is null)
        {
            ErrorMessage = $"Column '{cell.Column.Name}' is no longer available.";
            return;
        }

        object? value = filterOperator switch
        {
            DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull => null,
            DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn =>
                new object?[] { cell.RawValue },
            _ => cell.RawValue,
        };
        if (value is null
            && filterOperator is not (DatabaseFilterOperator.IsNull
                or DatabaseFilterOperator.IsNotNull))
        {
            ErrorMessage = $"{cell.Column.Name} does not contain a valid filter value.";
            return;
        }

        // A quick filter replaces the whole stack: it is "show me rows like
        // this cell", not another condition on top of the current ones.
        FilterRows.Clear();
        var quickRow = CreateFilterRow();
        quickRow.Column = filterColumn;
        quickRow.Operator = option;
        quickRow.Value = cell.IsNull
            ? string.Empty
            : filterOperator is DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn
                ? QuoteCsvField(cell.EditText)
                : cell.EditText;
        FilterRows.Add(quickRow);
        RaisePrimaryFilterChanged();
        var query = new DatabaseTableQuery(
            [new DatabaseFilterCondition(cell.Column.Name, filterOperator, value)],
            _tableQuery.Sorts,
            Offset: 0,
            Limit: _tableQuery.Limit);
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task ClearFilterAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage())
        {
            return;
        }

        FilterRows.Clear();
        FilterRows.Add(CreateFilterRow());
        RaisePrimaryFilterChanged();
        var query = new DatabaseTableQuery([], _tableQuery.Sorts, 0, _tableQuery.Limit);
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task ToggleTableSortAsync(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!CanSortTable
            || ResultColumns.All(column =>
                !string.Equals(column.Name, columnName, StringComparison.Ordinal)))
        {
            return;
        }

        var current = _tableQuery.Sorts.Count == 1
            && string.Equals(
                _tableQuery.Sorts[0].ColumnName,
                columnName,
                StringComparison.Ordinal)
                ? _tableQuery.Sorts[0]
                : null;
        var query = _tableQuery with
        {
            Sorts = [new DatabaseSort(columnName, current is { Descending: false })],
            Offset = 0,
        };
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task ApplyPageLimitAsync()
    {
        if (!CanChangePageLimit)
        {
            return;
        }

        if (!int.TryParse(
                PageLimitText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var limit)
            || limit is < 1 or > MaximumPageRows)
        {
            ErrorMessage = $"Page size must be between 1 and {MaximumPageRows}.";
            return;
        }

        if (limit == _tableQuery.Limit)
        {
            PageLimitText = limit.ToString(CultureInfo.InvariantCulture);
            ErrorMessage = null;
            return;
        }

        var query = _tableQuery with { Offset = 0, Limit = limit };
        await LoadResultQueryAsync(query, loadDetails: false);
        if (_tableQuery.Limit != limit)
        {
            PageLimitText = _tableQuery.Limit.ToString(CultureInfo.InvariantCulture);
        }
    }

    public async Task NextPageAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage() || !HasNextPage)
        {
            return;
        }

        var query = _tableQuery with { Offset = _tableQuery.Offset + _tableQuery.Limit };
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    public async Task PreviousPageAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage() || !HasPreviousPage)
        {
            return;
        }

        var query = _tableQuery with
        {
            Offset = Math.Max(0, _tableQuery.Offset - _tableQuery.Limit),
        };
        await LoadResultQueryAsync(query, loadDetails: false);
    }

    /// <summary>Re-reads the current structured page or successful result query.</summary>
    public async Task RefreshTableAsync()
    {
        if (IsBusy || !CanDiscardCurrentPage() || !CanRefreshTable)
        {
            return;
        }

        await LoadResultQueryAsync(_tableQuery, loadDetails: true);
    }

    public void AddRow()
    {
        if (!CanMutateRows || ResultColumns.Count == 0)
        {
            return;
        }

        var row = CreateNewRow(
            ResultRows.Select(candidate => candidate.Number).DefaultIfEmpty().Max() + 1);
        ObserveRow(row);
        ResultRows = [.. ResultRows, row];
        SelectRow(row);
        PublishPendingChanges();
    }

    public void DuplicateSelectedRow()
    {
        if (!CanDuplicateSelectedRow || SelectedRow is not { } source)
        {
            return;
        }

        var number = ResultRows.Select(candidate => candidate.Number).DefaultIfEmpty().Max() + 1;
        var duplicate = source.DuplicateAsNew(number);
        ObserveRow(duplicate);
        ResultRows = [.. ResultRows, duplicate];
        SelectRow(duplicate);
        PublishPendingChanges();
    }

    /// <summary>
    /// Stages a complete CSV document only after every header and value has
    /// validated. A malformed later row therefore cannot leave earlier rows dirty.
    /// </summary>
    public bool ImportCsv(string text)
    {
        if (!CanMutateRows || ResultColumns.Count == 0)
        {
            ErrorMessage = ReadOnlyReason ?? "Rows cannot be imported right now.";
            return false;
        }

        DatabaseGridCsvDocument document;
        try
        {
            document = DatabaseGridCsv.Parse(
                text,
                Math.Min(ResultColumns.Count, DatabaseGridCsv.MaximumColumns));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            ErrorMessage = exception.Message;
            return false;
        }

        if (document.Rows.Count == 0)
        {
            ErrorMessage = "The CSV file has no data rows.";
            return false;
        }

        try
        {
            DatabaseGridCsv.ValidateStagingSize(
                document.Rows.Count,
                ResultColumns.Count);
        }
        catch (InvalidDataException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }

        var columnsByName = ResultColumns
            .Select((column, ordinal) => (column, ordinal))
            .ToDictionary(item => item.column.Name, item => item, StringComparer.Ordinal);
        var importedColumns = new (DatabaseResultColumnViewModel Column, int Ordinal)[
            document.Headers.Count];
        for (var index = 0; index < document.Headers.Count; index++)
        {
            var header = document.Headers[index];
            if (!columnsByName.TryGetValue(header, out var match))
            {
                ErrorMessage = $"CSV column '{DatabaseGridCsv.DescribeHeader(header)}' does not exist in this table.";
                return false;
            }

            if (match.column.Descriptor.IsReadOnly || match.column.Descriptor.IsIdentity)
            {
                ErrorMessage = $"CSV column '{DatabaseGridCsv.DescribeHeader(header)}' is owned by the database and cannot be imported.";
                return false;
            }

            importedColumns[index] = (match.column, match.ordinal);
        }

        var firstNumber = ResultRows.Select(row => row.Number).DefaultIfEmpty().Max() + 1;
        var staged = new List<DatabaseResultRowViewModel>(document.Rows.Count);
        for (var rowIndex = 0; rowIndex < document.Rows.Count; rowIndex++)
        {
            var row = CreateNewRow(firstNumber + rowIndex);
            for (var columnIndex = 0; columnIndex < importedColumns.Length; columnIndex++)
            {
                var imported = importedColumns[columnIndex];
                var cell = row.Cells[imported.Ordinal];
                if (!cell.CanSetText)
                {
                    ErrorMessage = $"CSV column '{imported.Column.Name}' cannot accept text values.";
                    return false;
                }

                var importedText = document.Rows[rowIndex][columnIndex];
                if (!TryValidateProviderTextValue(cell, importedText, out var cellProviderError))
                {
                    ErrorMessage = $"CSV row {rowIndex + 2}: {cellProviderError}";
                    return false;
                }

                cell.SetText(importedText);
            }

            if (!TryValidateProviderTextCells(
                    row.Cells.Where(cell => !cell.Column.IsReadOnly),
                    out var rowProviderError))
            {
                ErrorMessage = $"CSV row {rowIndex + 2}: {rowProviderError}";
                return false;
            }

            if (!row.IsValid)
            {
                var error = row.Cells.First(cell => !cell.IsValid).ValidationError;
                ErrorMessage = $"CSV row {rowIndex + 2}: {error}";
                return false;
            }

            staged.Add(row);
        }

        foreach (var row in staged)
        {
            ObserveRow(row);
        }

        ResultRows = [.. ResultRows, .. staged];
        SelectRow(staged[^1]);
        ErrorMessage = null;
        PublishPendingChanges();
        return true;
    }

    public void DeleteSelectedRow()
    {
        if (!CanDeleteSelectedRow || SelectedRow is not { } row)
        {
            return;
        }

        if (!row.IsNew)
        {
            _deletedRows.Add(row);
        }

        ResultRows = [.. ResultRows.Where(candidate => !ReferenceEquals(candidate, row))];
        SelectRow(null);
        PublishPendingChanges();
    }

    public void SetSelectedCellNull(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (CanMutateRows && cell?.CanSetNull == true)
        {
            cell.SetNull();
            RefreshSelectedRowFields();
            PublishPendingChanges();
        }
    }

    public void SetSelectedCellDefault(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (CanMutateRows
            && SelectedRow is { IsNew: true }
            && cell?.CanSetDefault == true)
        {
            cell.SetDefault();
            RefreshSelectedRowFields();
            PublishPendingChanges();
        }
    }

    public bool CanSetSelectedCellEmpty(int ordinal) => CanMutateRows
        && !string.Equals(SelectedDriver.Id, "oracle", StringComparison.Ordinal)
        && GetSelectedCell(ordinal)?.CanSetEmpty == true;

    public void SetSelectedCellEmpty(int ordinal)
    {
        var cell = GetSelectedCell(ordinal);
        if (!CanMutateRows || cell?.CanSetEmpty != true)
        {
            return;
        }

        SetSelectedCellText(ordinal, string.Empty);
    }

    public void SetSelectedCellText(int ordinal, string text)
    {
        var cell = GetSelectedCell(ordinal);
        if (!CanMutateRows || cell?.CanSetText != true)
        {
            return;
        }

        var normalized = text ?? string.Empty;
        if (!TryValidateProviderTextValue(cell, normalized, out var providerError))
        {
            ErrorMessage = providerError;
            return;
        }

        cell.SetText(normalized);
        RefreshSelectedRowFields();
        PublishPendingChanges();
    }

    public void SetSelectedCellBinary(int ordinal, ReadOnlyMemory<byte> value)
    {
        var cell = GetSelectedCell(ordinal);
        if (!CanMutateRows || cell?.CanSetBinary != true)
        {
            return;
        }

        cell.SetBinary(value);
        RefreshSelectedRowFields();
        PublishPendingChanges();
    }

    public void ReportInteractionError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ErrorMessage = message;
    }

    public async Task RevertChangesAsync()
    {
        if (CanRevertChanges)
        {
            await LoadResultQueryAsync(_tableQuery, loadDetails: false);
        }
    }

    public async Task SaveChangesAsync()
    {
        if (!CanSaveChanges || SelectedObject is null)
        {
            return;
        }

        if (!TryValidatePendingChangesForProvider(out var providerError))
        {
            ErrorMessage = providerError;
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            var selectedObject = SelectedObject;
            if (selectedObject is null)
            {
                return;
            }

            var inserts = ResultRows.Where(row => row.IsNew).Select(row => row.BuildInsert()).ToArray();
            var updates = ResultRows
                .Where(row => !row.IsNew && row.IsDirty)
                .Select(row => row.BuildUpdate())
                .ToArray();
            var deletes = _deletedRows.Select(row => row.BuildDelete()).ToArray();
            var result = await _client.ApplyTableChangesAsync(
                SelectedDriver.Id,
                await ResolveEffectiveConnectionStringAsync(cancellationToken),
                _tunnelConnection,
                selectedObject.Descriptor,
                new DatabaseTableChanges(inserts, updates, deletes),
                cancellationToken);
            if (result.HasConflict)
            {
                ErrorMessage = result.Message
                    ?? "The row changed in the database. Reload it before saving again.";
                return;
            }

            AcceptCommittedChanges();
            ResultSummary = $"Saved {result.TotalAffected} change(s).";
            await ReloadResultsWithinOperationAsync(cancellationToken);
        });
    }

    private bool TryValidatePendingChangesForProvider(out string? error)
    {
        foreach (var row in ResultRows.Where(row => row.IsDirty))
        {
            var cells = row.IsNew
                ? row.Cells.Where(cell => !cell.Column.IsReadOnly)
                : row.Cells.Where(cell => cell.IsDirty && !cell.Column.IsReadOnly);
            if (!TryValidateProviderTextCells(cells, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool TryValidateProviderTextCells(
        IEnumerable<DatabaseResultCellViewModel> cells,
        out string? error)
    {
        foreach (var cell in cells)
        {
            if (cell.State == DatabaseEditValueState.Value
                && cell.RawValue is string text
                && !TryValidateProviderTextValue(cell, text, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool TryValidateProviderTextValue(
        DatabaseResultCellViewModel cell,
        string text,
        out string? error)
    {
        if (text.Length == 0
            && cell.Column.ValueKind == DatabaseValueKind.Text
            && string.Equals(SelectedDriver.Id, "oracle", StringComparison.Ordinal))
        {
            error = $"Oracle stores empty text as SQL NULL. Column '{cell.Column.Name}' "
                + "must use explicit SQL NULL or a non-empty value.";
            return false;
        }

        error = null;
        return true;
    }

    public override void Dispose()
    {
        // Both the closing tab and the disposing window sweep panels, so a
        // second call must be a no-op.
        if (!_disposed)
        {
            _disposed = true;
            RetainResultContent(null);
            ResetDatabaseDiagram();
            _hostedSession?.Dispose();
            _tableLoadCancellation?.Cancel();
            _tableLoadCancellation?.Dispose();
            ResetSqlLanguageSession();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        base.Dispose();
    }

    private async Task InitializeHostedSessionAsync()
    {
        try
        {
            await Initialization.ConfigureAwait(true);
            if (_disposed || !IsConnected || _hostedSession?.IsLinked == true)
            {
                return;
            }

            var connectionString =
                await ResolveEffectiveConnectionStringAsync(_lifetime.Token)
                    .ConfigureAwait(true);
            await EnsureHostedSessionAsync(connectionString, _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Hosting is an optional governed projection. Provider and secret
            // failures must not replace the direct human panel's own state.
        }
    }

    private void QueueHostedSessionEnsure(string connectionString)
    {
        if (_hostedSession is not null && _hostSessionClient is not null)
        {
            _ = EnsureHostedSessionAsync(connectionString, _lifetime.Token);
        }
    }

    private Task<bool> EnsureHostedSessionAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var hosted = _hostedSession;
        var sessionClient = _hostSessionClient;
        if (hosted is null || sessionClient is null || _disposed)
        {
            return Task.FromResult(false);
        }

        var target = new DatabaseSessionTarget(
            SelectedDriver.Id,
            connectionString,
            _hostBindingId,
            _hostBindingRevision,
            _tunnelConnection,
            _savedConnection?.PasswordSecret);
        return hosted.EnsureAsync(
            (sessionId, context, token) =>
                sessionClient.EnsureDatabaseSessionAsync(
                    new EnsureDatabaseSessionRequest(
                        sessionId,
                        hosted.Owner,
                        Title,
                        target),
                    context,
                    token),
            cancellationToken);
    }

    private void InvalidateHostedBinding()
    {
        _hostBindingRevision = checked(_hostBindingRevision + 1);
        InvalidateHostedSession();
    }

    private void InvalidateHostedSession()
    {
        if (_hostedSession is not null)
        {
            _ = _hostedSession.InvalidateAsync();
        }
    }

    private void BeginSqlLanguageInitialization()
    {
        ResetSqlLanguageSession();
        if (_sqlLanguageService?.IsAvailable != true || !IsConnected)
        {
            SqlLanguageStatus = "SQL intelligence worker is not installed.";
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sqlLanguageLoadCancellation = cancellation;
        var generation = _sqlLanguageLoadGeneration;
        SqlLanguageStatus = "Loading the database catalog for SQL intelligence…";
        SqlLanguageInitialization = InitializeSqlLanguageAsync(
            generation,
            cancellation.Token);
        OnPropertyChanged(nameof(SqlLanguageInitialization));
    }

    private async Task InitializeSqlLanguageAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        ISqlLanguageSession? opened = null;
        try
        {
            var catalog = await _client.GetSqlCatalogAsync(
                SelectedDriver.Id,
                await ResolveEffectiveConnectionStringAsync(cancellationToken),
                _tunnelConnection,
                cancellationToken);

            opened = await _sqlLanguageService!.OpenSessionAsync(
                catalog,
                cancellationToken);
            if (generation != _sqlLanguageLoadGeneration
                || cancellationToken.IsCancellationRequested
                || !IsConnected)
            {
                var stale = opened;
                opened = null;
                await stale.DisposeAsync();
                return;
            }

            SqlLanguageSession = opened;
            opened = null;
            var routineCount = catalog.Routines.Count == 1
                ? "1 server routine"
                : $"{catalog.Routines.Count} server routines";
            var objectCount = catalog.Objects.Count == 1
                ? "1 database object"
                : $"{catalog.Objects.Count} database objects";
            var catalogFacts = $"{objectCount} and {routineCount}";
            var coverageStatus = SqlLanguageCoverageStatus(catalog);
            SqlLanguageStatus = HasSqlLanguageSession
                ? catalog.IsPartial
                    ? $"SQL completion and validation know {catalogFacts} from a limited catalog. {catalog.Limitation} {coverageStatus}".TrimEnd()
                    : $"SQL completion and validation know {catalogFacts}. {coverageStatus}".TrimEnd()
                : SqlLanguageSession?.UnavailableReason
                    ?? "SQL intelligence worker is unavailable.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A reconnect, disconnect, or closing panel superseded this catalog.
        }
        catch (Exception exception)
        {
            if (generation == _sqlLanguageLoadGeneration
                && !cancellationToken.IsCancellationRequested
                && IsConnected)
            {
                SqlLanguageStatus = $"SQL intelligence is unavailable: {exception.Message}";
            }
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync();
            }
        }
    }

    private static string SqlLanguageCoverageStatus(SqlCatalogSnapshot catalog)
    {
        if (catalog.RoutineCoverage == SqlCatalogCoverage.None
            && catalog.IntrinsicCoverage == SqlCatalogCoverage.None)
        {
            return "Server function metadata is unavailable; unverified built-ins are omitted.";
        }

        if (catalog.RoutineCoverage == SqlCatalogCoverage.UserDefinedOnly
            && catalog.IntrinsicCoverage == SqlCatalogCoverage.None)
        {
            return "Server metadata covers user-defined routines only; unverified built-ins are omitted.";
        }

        if (catalog.RoutineCoverage == SqlCatalogCoverage.Partial
            || catalog.IntrinsicCoverage == SqlCatalogCoverage.Partial)
        {
            return "Server function metadata is incomplete; unverified built-ins are omitted.";
        }

        if (catalog.IntrinsicCoverage == SqlCatalogCoverage.None)
        {
            return "Intrinsic-operator metadata is unavailable; unverified bare built-ins are omitted.";
        }

        return string.Empty;
    }

    private void ResetSqlLanguageSession()
    {
        _sqlLanguageLoadGeneration++;
        _sqlLanguageLoadCancellation?.Cancel();
        _sqlLanguageLoadCancellation?.Dispose();
        _sqlLanguageLoadCancellation = null;
        if (SqlLanguageSession is { } session)
        {
            SqlLanguageSession = null;
            _ = DisposeSqlLanguageSessionAsync(session);
        }

        SqlLanguageStatus = "Connect a database to enable SQL completion and validation.";
    }

    private static async Task DisposeSqlLanguageSessionAsync(ISqlLanguageSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch
        {
            // The optional worker may already have exited. Panel teardown must
            // remain reliable and the process service owns final cleanup.
        }
    }

    private async Task ExecuteQueryAsync(string sql) =>
        await RunGuardedAsync(async cancellationToken =>
        {
            var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
            var canBrowse = IsBrowsableResultQuery(sql);
            using var page = _queryProvenanceCandidate is null || !canBrowse
                ? await _client.QueryAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sql,
                    MaxRows,
                    cancellationToken)
                : await _client.QueryWithProvenanceAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sql,
                    MaxRows,
                    cancellationToken);
            var match = _queryProvenanceCandidate is null || !canBrowse
                ? null
                : DatabaseQueryProvenanceResolver.ResolveExactTableProjection(
                    page,
                    [.. _allTables.Select(table => table.Descriptor)],
                    _queryProvenanceCandidate);
            var query = DatabaseTableQuery.FirstPage(MaxRows);
            var totalRows = canBrowse && page.Columns.Count > 0
                ? await _client.CountQueryRowsAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sql,
                    page.Columns,
                    query.Filters,
                    cancellationToken)
                : page.ValueRows.Count;
            cancellationToken.ThrowIfCancellationRequested();
            ApplyRawQueryPage(
                new DatabaseTablePage(
                    page,
                    0,
                    MaxRows,
                    page.Truncated,
                    totalRows),
                match?.Details,
                sql,
                query,
                canBrowse);
            // Re-reading every table after ordinary DML makes editor
            // intelligence disappear precisely while the user is iterating.
            // Invalidate schema-derived features for explicit DDL/catalog
            // statements; unusual provider-specific mutations can still be
            // picked up by reconnecting.
            if (MayChangeDatabaseSchema(sql))
            {
                ResetDatabaseDiagram();
                BeginSqlLanguageInitialization();
            }
        });

    private async Task LoadResultQueryAsync(
        DatabaseTableQuery query,
        bool loadDetails)
    {
        if (_resultSource == DatabaseResultSource.RawQuery)
        {
            await LoadRawQueryAsync(query);
            return;
        }

        await LoadSelectedTableAsync(loadDetails, query);
    }

    private async Task LoadRawQueryAsync(DatabaseTableQuery requestedQuery)
    {
        if (_rawQuerySql is null
            || _rawQueryColumns.Count == 0
            || _lifetime.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _tableLoadGeneration);
        var previous = _tableLoadCancellation;
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tableLoadCancellation = loadCancellation;
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = loadCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var page = await ReadRawQueryPageAsync(requestedQuery, cancellationToken);
            using var pageContent = page.Result;
            if (generation == _tableLoadGeneration && !cancellationToken.IsCancellationRequested)
            {
                ApplyRawQueryPage(page, _selectedObjectDetails, _rawQuerySql, requestedQuery);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableDatabaseException(exception))
        {
            if (generation == _tableLoadGeneration)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (generation == _tableLoadGeneration)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(StatusText));
                if (ReferenceEquals(_tableLoadCancellation, loadCancellation))
                {
                    _tableLoadCancellation = null;
                    loadCancellation.Dispose();
                }
            }
        }
    }

    private async Task LoadSelectedTableAsync(
        bool loadDetails,
        DatabaseTableQuery? requestedQuery = null)
    {
        var selectedObject = SelectedObject;
        if (selectedObject is null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _tableLoadGeneration);
        var previous = _tableLoadCancellation;
        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tableLoadCancellation = loadCancellation;
        previous?.Cancel();
        previous?.Dispose();
        var cancellationToken = loadCancellation.Token;
        requestedQuery ??= _tableQuery;
        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
            var details = loadDetails || _selectedObjectDetails is null
                ? await _client.GetObjectDetailsAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    selectedObject.Descriptor,
                    cancellationToken)
                : _selectedObjectDetails;
            var page = await _client.ReadTableAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                selectedObject.Descriptor,
                requestedQuery,
                cancellationToken);
            using var pageContent = page.Result;
            if (generation == _tableLoadGeneration && !cancellationToken.IsCancellationRequested)
            {
                ApplyTablePage(details, page, requestedQuery);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableDatabaseException(exception))
        {
            if (generation == _tableLoadGeneration)
            {
                ErrorMessage = exception.Message;
            }
        }
        finally
        {
            if (generation == _tableLoadGeneration)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(StatusText));
                if (ReferenceEquals(_tableLoadCancellation, loadCancellation))
                {
                    _tableLoadCancellation = null;
                    loadCancellation.Dispose();
                }
            }
        }
    }

    private async Task ReloadResultsWithinOperationAsync(CancellationToken cancellationToken)
    {
        if (_resultSource == DatabaseResultSource.RawQuery)
        {
            if (_rawQuerySql is null || _rawQueryColumns.Count == 0)
            {
                return;
            }

            var rawPage = await ReadRawQueryPageAsync(_tableQuery, cancellationToken);
            using var pageContent = rawPage.Result;
            cancellationToken.ThrowIfCancellationRequested();
            ApplyRawQueryPage(rawPage, _selectedObjectDetails, _rawQuerySql, _tableQuery);
            return;
        }

        var selectedObject = SelectedObject;
        if (selectedObject is null)
        {
            return;
        }

        var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
        var details = _selectedObjectDetails
            ?? await _client.GetObjectDetailsAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                selectedObject.Descriptor,
                cancellationToken);
        var page = await _client.ReadTableAsync(
            SelectedDriver.Id,
            connectionString,
            _tunnelConnection,
            selectedObject.Descriptor,
            _tableQuery,
            cancellationToken);
        using var resultContent = page.Result;
        cancellationToken.ThrowIfCancellationRequested();
        ApplyTablePage(details, page, _tableQuery);
    }

    private async Task<DatabaseTablePage> ReadRawQueryPageAsync(
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        var sourceSql = _rawQuerySql
            ?? throw new InvalidOperationException("The result query is no longer available.");
        var connectionString = await ResolveEffectiveConnectionStringAsync(cancellationToken);
        if (query.Offset == 0 && query.Filters.Count == 0 && query.Sorts.Count == 0)
        {
            var result = await _client.QueryAsync(
                SelectedDriver.Id,
                connectionString,
                _tunnelConnection,
                sourceSql,
                query.Limit,
                cancellationToken);
            try
            {
                var totalRows = await _client.CountQueryRowsAsync(
                    SelectedDriver.Id,
                    connectionString,
                    _tunnelConnection,
                    sourceSql,
                    _rawQueryColumns,
                    query.Filters,
                    cancellationToken);
                return new DatabaseTablePage(
                    PreserveRawQueryColumnContext(result),
                    0,
                    query.Limit,
                    result.Truncated,
                    totalRows);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        var page = await _client.ReadQueryAsync(
            SelectedDriver.Id,
            connectionString,
            _tunnelConnection,
            sourceSql,
            _rawQueryColumns,
            query,
            cancellationToken);
        return page with { Result = PreserveRawQueryColumnContext(page.Result) };
    }

    private DatabaseQueryPage PreserveRawQueryColumnContext(DatabaseQueryPage result)
    {
        if (result.Columns.Count != _rawQueryColumns.Count)
        {
            return result;
        }

        var columns = result.Columns
            .Select((column, ordinal) =>
            {
                var source = _rawQueryColumns[ordinal];
                return string.Equals(column.Name, source.Name, StringComparison.Ordinal)
                    ? source with
                    {
                        DataTypeName = column.DataTypeName,
                        ValueKind = column.ValueKind,
                        ClrTypeName = column.ClrTypeName,
                        // This SELECT was already proven as the exact table
                        // projection. Some providers describe an ordinary
                        // ORDER BY/LIMIT execution as read-only even though
                        // its catalog-backed source remains writable. Row
                        // materialization is still revalidated below, so an
                        // unsafe provider-owned value continues to fail closed.
                        IsReadOnly = source.IsReadOnly,
                    }
                    : column;
            })
            .ToArray();
        return result with { Columns = columns };
    }

    private void ApplyRawQueryPage(
        DatabaseTablePage page,
        DatabaseObjectDetails? details,
        string sourceSql,
        DatabaseTableQuery requestedQuery,
        bool? canBrowse = null)
    {
        var result = page.Result;
        var isNewSource = !string.Equals(_rawQuerySql, sourceSql, StringComparison.Ordinal);
        var isExplicitSourceExecution = canBrowse is not null;
        _rawQuerySql = sourceSql;
        if (canBrowse is not null)
        {
            _rawQueryCanBrowse = canBrowse.Value;
        }
        if (isNewSource || isExplicitSourceExecution || _rawQueryColumns.Count == 0)
        {
            _rawQueryColumns = [.. result.Columns];
        }

        if (result.Columns.Count == 0)
        {
            ClearSelectedObject();
            var elapsed = result.Elapsed.TotalMilliseconds
                .ToString("0", CultureInfo.InvariantCulture);
            ResultSummary = $"{result.RowsAffected} row(s) affected · {elapsed} ms";
            return;
        }

        if (details is not null)
        {
            SetSelectedObjectItem(_allTables
                .FirstOrDefault(table => table.Descriptor == details.Object)
                ?? new DatabaseTableItemViewModel(details.Object));
            OnPropertyChanged(nameof(SelectedObject));
            OnPropertyChanged(nameof(HasSelectedObject));
            OnPropertyChanged(nameof(SelectedObjectName));
            OnPropertyChanged(nameof(ObjectPickerLabel));
            ApplyTablePage(
                details,
                page,
                requestedQuery,
                DatabaseResultSource.RawQuery);
            if (isNewSource || isExplicitSourceExecution)
            {
                _rawQueryColumns = [.. ResultColumns.Select(column => column.Descriptor)];
            }
            return;
        }

        SetSelectedObjectItem(null);
        _selectedObjectDetails = null;
        RetainResultContent(result.ContentStore);
        StructureColumns = [];
        Indexes = [];
        _deletedRows.Clear();
        SelectRow(null);
        ApplyPagerState(page, requestedQuery);
        _resultSource = DatabaseResultSource.RawQuery;
        var selectedFilterColumnName = FilterColumn?.Name;
        var selectedFilterOperator = FilterOperator?.Operator;
        FilterColumns = [.. result.Columns
            .Select((column, ordinal) => new DatabaseFilterColumnViewModel(
                new DatabaseColumnSchema(
                    column.Name,
                    ordinal,
                    column.DataTypeName,
                    column.ValueKind,
                    column.ClrTypeName,
                    column.IsNullable,
                    IsPrimaryKey: column.IsKey,
                    IsIdentity: column.IsIdentity,
                    IsReadOnly: true,
                    DefaultExpression: column.DefaultExpression)))];
        FilterColumn = FilterColumns.FirstOrDefault(column =>
                string.Equals(column.Name, selectedFilterColumnName, StringComparison.Ordinal))
            ?? FilterColumns.FirstOrDefault();
        FilterOperator = FilterOperators.FirstOrDefault(option =>
                option.Operator == selectedFilterOperator)
            ?? FilterOperators.FirstOrDefault();

        var widths = ComputeColumnWidths(result);
        var nextColumns = result.Columns
            .Select((column, index) => new DatabaseResultColumnViewModel(
                column,
                widths[index],
                canEdit: false,
                SortDirectionFor(column.Name, requestedQuery)))
            .ToArray();
        // Detach realized rows only when the cell-template shape changes.
        // Same-shape filters/sorts can update their header state in place;
        // clearing and rebuilding a live DataGrid for every page causes
        // re-entrant layout with synchronously completing file providers.
        if (!HasCompatibleResultColumnShape(nextColumns))
        {
            ResultRows = [];
        }

        ResultColumns = nextColumns;
        ResultRows = [.. result.ValueRows
            .Select((row, index) => new DatabaseResultRowViewModel(
                page.Offset + index + 1,
                row,
                result.Columns,
                widths,
                canEdit: false))];
        var elapsedText = result.Elapsed.TotalMilliseconds
            .ToString("0", CultureInfo.InvariantCulture);
        ResultSummary = result.Truncated
            ? $"First {result.ValueRows.Count} rows (truncated) · {elapsedText} ms"
            : $"{result.ValueRows.Count} rows · {elapsedText} ms";
        SelectedMode = DatabaseWorkspaceMode.Data;
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(SelectedObjectName));
        OnPropertyChanged(nameof(ObjectPickerLabel));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        PublishTableCapabilities();
        PublishPendingChanges();
    }

    private void ApplyTablePage(
        DatabaseObjectDetails details,
        DatabaseTablePage page,
        DatabaseTableQuery requestedQuery,
        DatabaseResultSource resultSource = DatabaseResultSource.StructuredTable)
    {
        var result = page.Result;
        RetainResultContent(result.ContentStore);
        var valueRows = result.ValueRows;
        var normalizedColumns = details.Columns.Select(column =>
        {
            var materializedOrdinal = FindColumnOrdinal(result.Columns, column.Name);
            var materialized = materializedOrdinal >= 0
                ? result.Columns[materializedOrdinal]
                : null;
            if (materializedOrdinal >= 0 && HasDisplayOnlyValue(valueRows, materializedOrdinal))
            {
                // A provider-owned value that could not be detached safely is a
                // stronger signal than catalog metadata. Never turn its bounded
                // display text back into an editable/provider-bound value.
                return column with
                {
                    ValueKind = DatabaseValueKind.Other,
                    IsReadOnly = true,
                };
            }

            return column.ValueKind == DatabaseValueKind.Other
                && materialized is { ValueKind: not DatabaseValueKind.Other }
                    ? column with
                    {
                        ValueKind = materialized.ValueKind,
                        ClrTypeName = materialized.ClrTypeName ?? column.ClrTypeName,
                    }
                    : column;
        }).ToArray();
        var hasDisplayOnlyKey = normalizedColumns.Any(column =>
            column.IsPrimaryKey && column.ValueKind == DatabaseValueKind.Other);
        var disableForDisplayOnlyKey = details.CanEdit && hasDisplayOnlyKey;
        details = details with
        {
            Columns = normalizedColumns,
            CanEdit = details.CanEdit && !hasDisplayOnlyKey,
            ReadOnlyReason = disableForDisplayOnlyKey
                ? "This primary-key value cannot be edited safely."
                : details.ReadOnlyReason,
        };
        var effectiveColumns = result.Columns.Select((column, ordinal) =>
        {
            var metadata = details.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
            if (HasDisplayOnlyValue(valueRows, ordinal))
            {
                return column with
                {
                    ValueKind = DatabaseValueKind.Other,
                    IsReadOnly = true,
                };
            }

            return metadata is null
                ? column
                : column with
                {
                    ValueKind = metadata.ValueKind == DatabaseValueKind.Other
                        ? column.ValueKind
                        : metadata.ValueKind,
                    ClrTypeName = column.ClrTypeName ?? metadata.ClrTypeName,
                    IsNullable = metadata.IsNullable,
                    IsKey = metadata.IsPrimaryKey,
                    IsIdentity = metadata.IsIdentity,
                    IsReadOnly = column.IsReadOnly || !metadata.CanEdit,
                    BaseColumnName = metadata.Name,
                    DefaultExpression = metadata.DefaultExpression,
                };
        }).ToArray();
        result = result with { Columns = effectiveColumns };
        _selectedObjectDetails = details;
        _queryProvenanceCandidate = details;
        StructureColumns = [.. details.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column => new DatabaseStructureColumnViewModel(column))];
        Indexes = [.. details.Indexes.Select(index => new DatabaseIndexViewModel(index))];
        var selectedFilterColumnName = FilterColumn?.Name;
        var selectedFilterOperator = FilterOperator?.Operator;
        FilterColumns = [.. details.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column => new DatabaseFilterColumnViewModel(column))];
        FilterColumn = FilterColumns.FirstOrDefault(column =>
                string.Equals(column.Name, selectedFilterColumnName, StringComparison.Ordinal))
            ?? FilterColumns.FirstOrDefault();
        FilterOperator = FilterOperators.FirstOrDefault(option =>
                option.Operator == selectedFilterOperator)
            ?? FilterOperators.FirstOrDefault();
        ApplyPagerState(page, requestedQuery);
        _resultSource = resultSource;
        if (resultSource == DatabaseResultSource.StructuredTable)
        {
            _rawQuerySql = null;
            _rawQueryColumns = [];
            _rawQueryCanBrowse = false;
        }
        _deletedRows.Clear();
        SelectRow(null);

        var widths = ComputeColumnWidths(result);
        var nextColumns = result.Columns
            .Select((column, index) => new DatabaseResultColumnViewModel(
                column,
                widths[index],
                CanEditRows,
                SortDirectionFor(column.Name, requestedQuery)))
            .ToArray();
        if (!HasCompatibleResultColumnShape(nextColumns))
        {
            ResultRows = [];
        }

        ResultColumns = nextColumns;
        var rows = valueRows
            .Select((values, index) => new DatabaseResultRowViewModel(
                page.Offset + index + 1,
                values,
                result.Columns,
                widths,
                CanEditRows))
            .ToArray();
        foreach (var row in rows)
        {
            ObserveRow(row);
        }

        ResultRows = rows;
        var elapsed = result.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        ResultSummary = result.Truncated && !page.HasMore
            ? $"First {valueRows.Count} rows · paging requires a primary key · {elapsed} ms"
            : valueRows.Count == 0
                ? $"No rows · {elapsed} ms"
                : $"Rows {page.Offset + 1}–{page.Offset + valueRows.Count} · {elapsed} ms";
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        PublishTableCapabilities();
        PublishPendingChanges();
    }

    private bool HasCompatibleResultColumnShape(
        IReadOnlyList<DatabaseResultColumnViewModel> replacements)
    {
        if (ResultColumns.Count != replacements.Count)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < replacements.Count; ordinal++)
        {
            var current = ResultColumns[ordinal];
            var replacement = replacements[ordinal];
            if (!string.Equals(current.Name, replacement.Name, StringComparison.Ordinal)
                || current.ValueKind != replacement.ValueKind
                || current.IsEditable != replacement.IsEditable)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyPagerState(DatabaseTablePage page, DatabaseTableQuery requestedQuery)
    {
        _hasNextPage = page.HasMore;
        _totalRows = page.TotalRows;
        _tableQuery = requestedQuery with { Offset = page.Offset, Limit = page.Limit };
        PageLimitText = page.Limit.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(TotalRows));
        OnPropertyChanged(nameof(TotalRowsText));
    }

    private static int FindColumnOrdinal(
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        string name)
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (string.Equals(columns[ordinal].Name, name, StringComparison.Ordinal))
            {
                return ordinal;
            }
        }

        return -1;
    }

    private static bool? SortDirectionFor(
        string columnName,
        DatabaseTableQuery query)
    {
        var sort = query.Sorts.Count == 1
            && string.Equals(
                query.Sorts[0].ColumnName,
                columnName,
                StringComparison.Ordinal)
                ? query.Sorts[0]
                : null;
        return sort?.Descending;
    }

    private static bool HasDisplayOnlyValue(
        IReadOnlyList<IReadOnlyList<DatabaseValue>> rows,
        int ordinal) =>
        rows.Any(row =>
            ordinal < row.Count
            && !row[ordinal].IsNull
            && row[ordinal].Kind == DatabaseValueKind.Other);

    private void ClearSelectedObject()
    {
        RetainResultContent(null);
        Interlocked.Increment(ref _tableLoadGeneration);
        var activeLoad = _tableLoadCancellation;
        _tableLoadCancellation = null;
        activeLoad?.Cancel();
        activeLoad?.Dispose();
        if (activeLoad is not null)
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
        SetSelectedObjectItem(null);
        _selectedObjectDetails = null;
        _queryProvenanceCandidate = null;
        _tableQuery = DatabaseTableQuery.FirstPage(PreviewRows);
        _resultSource = DatabaseResultSource.None;
        _rawQuerySql = null;
        _rawQueryColumns = [];
        _rawQueryCanBrowse = false;
        _hasNextPage = false;
        _totalRows = 0;
        PageLimitText = PreviewRows.ToString(CultureInfo.InvariantCulture);
        _deletedRows.Clear();
        StructureColumns = [];
        Indexes = [];
        FilterColumns = [];
        FilterColumn = null;
        ResultRows = [];
        ResultColumns = [];
        SelectRow(null);
        ResultSummary = string.Empty;
        SelectedMode = DatabaseWorkspaceMode.Data;
        OnPropertyChanged(nameof(SelectedObject));
        OnPropertyChanged(nameof(HasSelectedObject));
        OnPropertyChanged(nameof(SelectedObjectName));
        OnPropertyChanged(nameof(ObjectPickerLabel));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(TotalRows));
        OnPropertyChanged(nameof(TotalRowsText));
        PublishTableCapabilities();
        PublishPendingChanges();
    }

    private void ObserveRow(DatabaseResultRowViewModel row) =>
        row.DirtyStateChanged += OnRowDirtyStateChanged;

    private void RetainResultContent(DatabaseValueContentStore? content)
    {
        // Acquire the replacement before releasing the old lease; record
        // projections may legitimately share the same result store.
        var retained = content?.Retain();
        _resultContentLease?.Dispose();
        _resultContent = content;
        _resultContentLease = retained;
    }

    internal async Task<bool> PrepareCellForEditingAsync(DatabaseResultCellViewModel cell)
    {
        if (!cell.NeedsFullTextForEditing)
        {
            return true;
        }

        var loaded = false;
        await RunGuardedAsync(async cancellationToken =>
        {
            var memory = GC.GetGCMemoryInfo();
            using var process = Process.GetCurrentProcess();
            await cell.LoadFullTextForEditingAsync(EditorMemoryHeadroom(
                memory.TotalAvailableMemoryBytes, memory.MemoryLoadBytes, process.WorkingSet64), cancellationToken);
            loaded = !cell.NeedsFullTextForEditing;
        });
        return loaded;
    }

    // Samples are not reservations. System load already includes this process;
    // use the larger observation rather than count its working set twice.
    internal static long EditorMemoryHeadroom(long available, long systemLoad, long workingSet) =>
        Math.Max(0, available - Math.Max(0, Math.Max(systemLoad, workingSet)));

    private DatabaseResultRowViewModel CreateNewRow(int number)
    {
        var columns = ResultColumns.Select(column => column.Descriptor).ToArray();
        var values = columns.Select(column => new DatabaseValue(
            null,
            column.ValueKind,
            "DEFAULT")).ToArray();
        var widths = ResultColumns.Select(column => column.Width).ToArray();
        return new DatabaseResultRowViewModel(
            number,
            values,
            columns,
            widths,
            canEdit: true,
            isNew: true);
    }

    private DatabaseResultCellViewModel? GetSelectedCell(int ordinal) =>
        SelectedRow is { } row && ordinal >= 0 && ordinal < row.Cells.Count
            ? row.Cells[ordinal]
            : null;

    private void OnRowDirtyStateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshSelectedRowFields();
        PublishPendingChanges();
    }

    private void PublishTableCapabilities()
    {
        OnPropertyChanged(nameof(CanEditRows));
        OnPropertyChanged(nameof(CanMutateRows));
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        OnPropertyChanged(nameof(CanDuplicateSelectedRow));
        OnPropertyChanged(nameof(CanCopySelectedRowAsInsert));
        OnPropertyChanged(nameof(CanSetSelectedCellNull));
        OnPropertyChanged(nameof(CanSetSelectedCellDefault));
        OnPropertyChanged(nameof(ReadOnlyReason));
        OnPropertyChanged(nameof(HasReadOnlyReason));
        OnPropertyChanged(nameof(CanSaveChanges));
        PublishInteractionStates();
    }

    private void PublishPendingChanges()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanSaveChanges));
        RaiseCommandStates();
        PublishInteractionStates();
    }

    private void PublishInteractionStates()
    {
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(CurrentDatabaseLabel));
        OnPropertyChanged(nameof(CanMutateRows));
        OnPropertyChanged(nameof(CanDeleteSelectedRow));
        OnPropertyChanged(nameof(CanDuplicateSelectedRow));
        OnPropertyChanged(nameof(CanCopySelectedRowAsInsert));
        OnPropertyChanged(nameof(CanSetSelectedCellNull));
        OnPropertyChanged(nameof(CanSetSelectedCellDefault));
        OnPropertyChanged(nameof(CanChangeSelectedObject));
        OnPropertyChanged(nameof(CanChangeConnection));
        OnPropertyChanged(nameof(CanFilterTable));
        OnPropertyChanged(nameof(CanRefreshTable));
        OnPropertyChanged(nameof(CanSortTable));
        OnPropertyChanged(nameof(CanChangePageLimit));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        OnPropertyChanged(nameof(CanRevertChanges));
        OnPropertyChanged(nameof(CanSaveChanges));
    }


    private static IReadOnlyList<DatabaseFilterOperatorViewModel> FilterOperatorsFor(
        DatabaseValueKind? kind,
        bool includeListOperators)
    {
        var supportsValueFilters = kind is not (DatabaseValueKind.Other
            or DatabaseValueKind.Binary
            or DatabaseValueKind.Collection
            or DatabaseValueKind.Json
            or DatabaseValueKind.Network);
        var supportsTextMatching = kind is DatabaseValueKind.Text
            && supportsValueFilters;
        var supportsOrdering = kind is DatabaseValueKind.SignedInteger
            or DatabaseValueKind.Text
            or DatabaseValueKind.UnsignedInteger
            or DatabaseValueKind.Decimal
            or DatabaseValueKind.FloatingPoint
            or DatabaseValueKind.Date
            or DatabaseValueKind.Time
            or DatabaseValueKind.Timestamp
            or DatabaseValueKind.TimestampWithZone
            or DatabaseValueKind.Duration;
        return [.. AllFilterOperators.Where(option => option.Operator switch
        {
            DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull => true,
            _ when !supportsValueFilters => false,
            DatabaseFilterOperator.Contains
                or DatabaseFilterOperator.NotContains
                or DatabaseFilterOperator.StartsWith
                or DatabaseFilterOperator.EndsWith => supportsTextMatching,
            DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn => includeListOperators,
            DatabaseFilterOperator.LessThan
                or DatabaseFilterOperator.LessThanOrEqual
                or DatabaseFilterOperator.GreaterThan
                or DatabaseFilterOperator.GreaterThanOrEqual => supportsOrdering,
            _ => true,
        })];
    }

    private static bool TryParseFilterValue(
        string text,
        DatabaseValueKind kind,
        DatabaseFilterOperator filterOperator,
        out object? value,
        out string? error)
    {
        if (filterOperator is DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull)
        {
            value = null;
            error = null;
            return true;
        }

        if (filterOperator is not (DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn))
        {
            return DatabaseResultCellViewModel.TryParse(text, kind, out value, out error);
        }

        if (text.Length > MaximumFilterListCharacters)
        {
            value = null;
            error = "Filter lists are limited to 64 KiB of text.";
            return false;
        }

        var rows = DelimitedText.Parse(text, ',', maximumRows: 2);
        if (rows.Count != 1 || rows[0].Count is 0 or > MaximumFilterListValues)
        {
            value = null;
            error = $"IN filters require between 1 and {MaximumFilterListValues} comma-separated values.";
            return false;
        }

        var parsed = new object?[rows[0].Count];
        for (var index = 0; index < rows[0].Count; index++)
        {
            var token = kind == DatabaseValueKind.Text
                ? rows[0][index]
                : rows[0][index].Trim();
            if (!DatabaseResultCellViewModel.TryParse(token, kind, out parsed[index], out error))
            {
                value = null;
                return false;
            }
        }

        value = parsed;
        error = null;
        return true;
    }

    private void AcceptCommittedChanges()
    {
        _deletedRows.Clear();
        foreach (var row in ResultRows)
        {
            row.AcceptChanges();
        }

        PublishPendingChanges();
    }

    private bool CanDiscardCurrentPage()
    {
        if (!HasPendingChanges)
        {
            return true;
        }

        ErrorMessage = "Save or revert the pending row changes first.";
        return false;
    }

    private static bool IsRecoverableDatabaseException(Exception exception) => exception
        is System.Data.Common.DbException
        or InvalidOperationException
        or NotSupportedException
        or ArgumentException
        or TimeoutException
        or IOException;

    private async Task RunGuardedAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy || _lifetime.IsCancellationRequested)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableDatabaseException(exception))
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string BuildCellValue(DatabaseResultRowViewModel row, int ordinal)
    {
        ValidateRowWidth(row);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, row.Cells.Count);
        ReportSpreadsheetRisk([row.Cells[ordinal]]);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCellText(writer, row.Cells[ordinal]));
    }

    internal Task<(string Text, long Revision)> BuildCellValueAsync(DatabaseResultRowViewModel row, int ordinal)
    {
        ValidateRowWidth(row);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, row.Cells.Count);
        var cell = row.Cells[ordinal];
        return BuildClipboardSnapshotAsync((writer, _) => DatabaseGridExport.WriteCellText(writer, cell), [cell]);
    }

    internal Task<(string Text, long Revision)> BuildRowTsvAsync(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        return BuildClipboardSnapshotAsync((writer, _) => DatabaseGridExport.WriteRowTsv(writer, row), [.. row.Cells]);
    }

    internal Task<(string Text, long Revision)> BuildColumnValuesAsync(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ResultColumns.Count);
        var rows = ResultRows.ToArray();
        return BuildClipboardSnapshotAsync((writer, _) => DatabaseGridExport.WriteColumnValues(writer, rows, ordinal),
            [.. rows.Select(row => row.Cells[ordinal])]);
    }

    internal Task<(string Text, long Revision)> BuildRowCsvAsync(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        var columns = ResultColumns.ToArray();
        return BuildClipboardSnapshotAsync((writer, _) => DatabaseGridExport.WriteCsv(writer, columns, [row]),
            [.. row.Cells], [.. columns.Select(column => column.Name)]);
    }

    internal Task<(string Text, long Revision)> BuildRowJsonAsync(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        var columns = ResultColumns.ToArray();
        return BuildClipboardSnapshotAsync((writer, _) => DatabaseGridExport.WriteJsonRow(writer, columns, row), null);
    }

    internal Task<(string Text, long Revision)> BuildRowSqlInsertAsync(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        var details = _selectedObjectDetails;
        if (details?.Object.Kind != DatabaseTableKind.Table)
        {
            throw new InvalidOperationException("INSERT copy is only available for a physical table result.");
        }
        var driver = SelectedDriver.Id;
        var insert = row.BuildInsert();
        return BuildClipboardSnapshotAsync((writer, token) =>
        {
            var values = new List<DatabaseColumnEdit>(insert.Values.Count);
            var remaining = MaximumClipboardUtf8Bytes;
            foreach (var edit in insert.Values)
            {
                token.ThrowIfCancellationRequested();
                if (edit.Value is not DatabaseValueContent content)
                {
                    var size = edit.Value switch
                    {
                        string text => Encoding.UTF8.GetByteCount(text),
                        byte[] bytes => bytes.Length,
                        ReadOnlyMemory<byte> bytes => bytes.Length,
                        Memory<byte> bytes => bytes.Length,
                        _ => 0,
                    };
                    if (size > remaining)
                    {
                        throw new InvalidDataException("Clipboard output exceeds the 16 MiB UTF-8 limit. Export the current page to a file instead.");
                    }
                    remaining -= size;
                    values.Add(edit);
                    continue;
                }
                if (content.Kind is not (DatabaseValueKind.Text or DatabaseValueKind.Json or DatabaseValueKind.Binary))
                {
                    throw new InvalidOperationException($"Column '{edit.ColumnName}' uses a value that cannot be represented safely in an INSERT script.");
                }
                using var source = content.OpenRead();
                using var complete = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (count > remaining)
                    {
                        throw new InvalidDataException("Clipboard output exceeds the 16 MiB UTF-8 limit. Export the current page to a file instead.");
                    }
                    remaining -= count;
                    complete.Write(buffer, 0, count);
                }
                var completeBytes = complete.ToArray();
                object value = content.Kind == DatabaseValueKind.Binary ? completeBytes : new UTF8Encoding(false, true).GetString(completeBytes);
                values.Add(edit with { Value = value });
            }
            // Preserve driver-specific escaping. The shared writer checks the
            // final expanded SQL against the same existing clipboard budget.
            writer.Write(_client.BuildInsertStatement(driver, details, new DatabaseInsertedRow(values), MaximumClipboardUtf8Bytes));
        }, null);
    }

    internal bool IsClipboardRequestCurrent(long revision) => !_disposed && revision == _clipboardRevision;

    internal async Task<(string Text, long Revision)> BuildDiagramClipboardAsync(
        IDatabaseDiagramSession session, DatabaseDiagramExport format)
    {
        if (!ReferenceEquals(session, _diagramSession))
        {
            throw new OperationCanceledException("The database diagram changed.");
        }

        var snapshot = await BuildClipboardSnapshotAsync(async token =>
        {
            using var destination = new DiagramClipboardBuffer();
            await session.ExportAsync(destination, format, token);
            token.ThrowIfCancellationRequested();
            return (Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length)), false);
        });
        if (!ReferenceEquals(session, _diagramSession))
        {
            throw new OperationCanceledException("The database diagram changed.");
        }
        return snapshot;
    }

    private Task<(string Text, long Revision)> BuildClipboardSnapshotAsync(
        Action<TextWriter, CancellationToken> write, IReadOnlyList<DatabaseResultCellViewModel>? cells,
        IReadOnlyList<string>? headings = null) => BuildClipboardSnapshotAsync(token => Task.Run(() =>
        {
            // Enforce the clipboard budget before scanning formula risk.
            var text = DatabaseGridExport.BuildClipboardText(writer => write(writer, token), token);
            var risk = cells is not null && DatabaseSpreadsheetRisk.ContainsFormula(cells, headings, token);
            token.ThrowIfCancellationRequested();
            return (text, risk);
        }, token));

    private async Task<(string Text, long Revision)> BuildClipboardSnapshotAsync(
        Func<CancellationToken, Task<(string Text, bool Risk)>> build)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy && _clipboardBuildCancellation is null)
        {
            throw new InvalidOperationException("Another database operation is already running.");
        }
        _clipboardBuildCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _clipboardBuildCancellation = cancellation;
        var revision = ++_clipboardRevision;
        using var retainedContent = _resultContent?.Retain();
        IsBusy = true;
        try
        {
            var result = await build(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            InterchangeNotice = result.Risk ? DatabaseSpreadsheetRisk.Notice : null;
            return (result.Text, revision);
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            // Obsolete work must not publish a late error over its successor.
            throw new OperationCanceledException(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_clipboardBuildCancellation, cancellation))
            {
                _clipboardBuildCancellation = null;
                IsBusy = false;
            }
        }
    }

    private sealed class DiagramClipboardBuffer : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Length + buffer.Length > MaximumClipboardUtf8Bytes)
            {
                throw new InvalidOperationException("This diagram is too large for the clipboard. Save it to a file to retain the complete diagram.");
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Every value in one column on the current page, one per line.</summary>
    public string BuildColumnValues(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ResultColumns.Count);
        ReportSpreadsheetRisk(ResultRows.Select(row => row.Cells[ordinal]));
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteColumnValues(writer, ResultRows, ordinal));
    }

    /// <summary>One selected row as exact tab-separated interchange text.</summary>
    public string BuildRowTsv(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        ReportSpreadsheetRisk(row.Cells);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteRowTsv(writer, row));
    }

    public string BuildCurrentPageTsv()
    {
        ReportSpreadsheetRisk(ResultRows.SelectMany(row => row.Cells), includeHeadings: true);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCurrentPageTsv(writer, ResultColumns, ResultRows));
    }

    /// <summary>One row as a JSON object, numbers and booleans unquoted.</summary>
    public string BuildRowJson(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteJsonRow(writer, ResultColumns, row));
    }

    public string BuildCurrentPageJson() => DatabaseGridExport.BuildClipboardText(writer =>
        DatabaseGridExport.WriteCurrentPageJson(writer, ResultColumns, ResultRows));

    /// <summary>One row as a two-line CSV: header and RFC-quoted values.</summary>
    public string BuildRowCsv(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        ReportSpreadsheetRisk(row.Cells, includeHeadings: true);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCsv(writer, ResultColumns, [row]));
    }

    public string BuildCurrentPageCsv()
    {
        ReportSpreadsheetRisk(ResultRows.SelectMany(row => row.Cells), includeHeadings: true);
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCsv(writer, ResultColumns, ResultRows));
    }

    private void ReportSpreadsheetRisk(IEnumerable<DatabaseResultCellViewModel> cells, bool includeHeadings = false) =>
        InterchangeNotice = DatabaseSpreadsheetRisk.ContainsFormula(
            cells, includeHeadings ? ResultColumns.Select(column => column.Name) : null)
                ? DatabaseSpreadsheetRisk.Notice
                : null;

    /// <summary>One row as an executable INSERT for the active database driver.</summary>
    internal string BuildRowSqlInsert(DatabaseResultRowViewModel row)
    {
        ValidateRowWidth(row);
        var details = _selectedObjectDetails
            ?? throw new InvalidOperationException(
                "INSERT copy is only available for a physical table result.");
        if (details.Object.Kind != DatabaseTableKind.Table)
        {
            throw new InvalidOperationException(
                "INSERT copy is only available for a physical table result.");
        }

        var statement = _client.BuildInsertStatement(
            SelectedDriver.Id,
            details,
            row.BuildInsert());
        return DatabaseGridExport.BuildClipboardText(writer => writer.Write(statement));
    }

    internal string BuildCurrentPageSql()
    {
        var table = RequireSqlExportTable();
        return DatabaseGridExport.BuildClipboardText(writer =>
            DatabaseGridExport.WriteCurrentPageSql(
                writer,
                table,
                ResultColumns,
                ResultRows));
    }

    /// <summary>Streams the current page as RFC 4180-style CSV without a page-sized string.</summary>
    public void WriteCurrentPageCsv(TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ReportSpreadsheetRisk(ResultRows.SelectMany(row => row.Cells), includeHeadings: true);
        DatabaseGridExport.WriteCsv(destination, ResultColumns, ResultRows);
    }

    /// <summary>Streams UTF-8 CSV directly to a writable stream and leaves it open.</summary>
    public void WriteCurrentPageCsv(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        WriteCurrentPageCsv(writer);
        writer.Flush();
    }

    /// <summary>Writes one complete JSON array to an existing UTF-8 JSON writer.</summary>
    public void WriteCurrentPageJson(Utf8JsonWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCurrentPageJson(destination, ResultColumns, ResultRows);
    }

    /// <summary>Streams the current page as indented JSON to an existing text writer.</summary>
    public void WriteCurrentPageJson(TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCurrentPageJson(destination, ResultColumns, ResultRows);
    }

    /// <summary>Streams the current page as indented UTF-8 JSON and leaves the stream open.</summary>
    public void WriteCurrentPageJson(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        WriteCurrentPageJson(writer);
        writer.Flush();
    }

    /// <summary>Streams ANSI INSERT statements without a page-sized string.</summary>
    internal void WriteCurrentPageSql(TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        DatabaseGridExport.WriteCurrentPageSql(
            destination,
            RequireSqlExportTable(),
            ResultColumns,
            ResultRows);
    }

    /// <summary>Streams UTF-8 ANSI INSERT statements and leaves the stream open.</summary>
    internal void WriteCurrentPageSql(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 16 * 1024,
            leaveOpen: true);
        WriteCurrentPageSql(writer);
        writer.Flush();
    }

    /// <summary>
    /// Streams a snapshot of the current page off the UI thread while the panel
    /// is interaction-locked. Cell values are detached before they reach this
    /// layer, and the busy gate prevents an editor from changing their state
    /// while the snapshot is being serialized.
    /// </summary>
    internal async Task WriteCurrentPageExportAsync(
        Stream destination,
        DatabaseGridExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The export destination is not writable.", nameof(destination));
        }

        if (IsBusy)
        {
            throw new InvalidOperationException("Another database operation is already running.");
        }

        if (!HasResults)
        {
            throw new InvalidOperationException("There is no database page to export.");
        }

        var columns = ResultColumns.ToArray();
        var rows = ResultRows.ToArray();
        using var retainedContent = _resultContent?.Retain();
        var table = format == DatabaseGridExportFormat.Sql
            ? RequireSqlExportTable()
            : null;
        var spreadsheetRisk = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);

        IsBusy = true;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            await Task.Run(
                () =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    switch (format)
                    {
                        case DatabaseGridExportFormat.Csv:
                            spreadsheetRisk = DatabaseSpreadsheetRisk.ContainsFormula(
                                rows.SelectMany(row => row.Cells), columns.Select(column => column.Name), linked.Token);
                            using (var writer = DatabaseGridExport.CreateFileWriter(destination, linked.Token))
                            {
                                DatabaseGridExport.WriteCsv(writer, columns, rows);
                                writer.Flush();
                            }

                            break;
                        case DatabaseGridExportFormat.Json:
                            using (var writer = DatabaseGridExport.CreateFileWriter(destination, linked.Token))
                            {
                                DatabaseGridExport.WriteCurrentPageJson(writer, columns, rows);
                                writer.Flush();
                            }

                            break;
                        case DatabaseGridExportFormat.Sql:
                            using (var writer = DatabaseGridExport.CreateFileWriter(destination, linked.Token))
                            {
                                DatabaseGridExport.WriteCurrentPageSql(
                                    writer,
                                    table!,
                                    columns,
                                    rows);
                                writer.Flush();
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(format));
                    }

                    linked.Token.ThrowIfCancellationRequested();
                },
                linked.Token);
            if (format == DatabaseGridExportFormat.Csv)
            {
                InterchangeNotice = spreadsheetRisk ? DatabaseSpreadsheetRisk.Notice : null;
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private DatabaseObjectId RequireSqlExportTable() =>
        _selectedObject?.Descriptor.Id
        ?? throw new InvalidOperationException(
            "SQL INSERT export is only available for a table preview.");

    private void ValidateRowWidth(DatabaseResultRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Cells.Count != ResultColumns.Count)
        {
            throw new ArgumentException(
                "The row does not match the current result columns.",
                nameof(row));
        }
    }

    private static string QuoteCsvField(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsBrowsableResultQuery(string sql)
    {
        var statement = sql.AsSpan().TrimStart();
        const string keyword = "SELECT";
        if (!statement.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (statement.Length == keyword.Length
                || !(char.IsLetterOrDigit(statement[keyword.Length])
                    || statement[keyword.Length] == '_'))
            && HasNoAdditionalStatement(statement[keyword.Length..]);
    }

    private static bool HasNoAdditionalStatement(ReadOnlySpan<char> sql)
    {
        var parenthesisDepth = 0;
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            if (current is '\'' or '"' or '`' or '[')
            {
                if (!SkipQuotedSql(sql, ref index, current))
                {
                    return false;
                }

                continue;
            }

            if (IsDashCommentStart(sql, index))
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql[(index + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                index += end + 3;
                continue;
            }

            if (current == '(')
            {
                parenthesisDepth++;
                continue;
            }

            if (current == ')')
            {
                parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
                continue;
            }

            if (parenthesisDepth != 0)
            {
                continue;
            }

            if (current == ';')
            {
                // Keep the source suitable for a generated outer SELECT. A
                // comment after the terminator would either leave the
                // semicolon inside the derived table or swallow its closing
                // syntax, so this deliberately fails closed.
                return sql[(index + 1)..].Trim().IsEmpty;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index;
                while (index + 1 < sql.Length
                       && (char.IsLetterOrDigit(sql[index + 1]) || sql[index + 1] == '_'))
                {
                    index++;
                }

                if (IsDataChangingKeyword(sql[start..(index + 1)]))
                {
                    return false;
                }
            }
        }

        return parenthesisDepth == 0;
    }

    private static bool SkipQuotedSql(
        ReadOnlySpan<char> sql,
        ref int index,
        char opener)
    {
        var closer = opener == '[' ? ']' : opener;
        for (index++; index < sql.Length; index++)
        {
            if (sql[index] != closer)
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == closer)
            {
                index++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsDataChangingKeyword(ReadOnlySpan<char> token) =>
        token.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
        || token.Equals("INTO", StringComparison.OrdinalIgnoreCase)
        || token.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("MERGE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
        || token.Equals("DROP", StringComparison.OrdinalIgnoreCase)
        || token.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("GRANT", StringComparison.OrdinalIgnoreCase)
        || token.Equals("REVOKE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("CALL", StringComparison.OrdinalIgnoreCase)
        || token.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
        || token.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)
        || token.Equals("COPY", StringComparison.OrdinalIgnoreCase)
        || token.Equals("ATTACH", StringComparison.OrdinalIgnoreCase)
        || token.Equals("DETACH", StringComparison.OrdinalIgnoreCase)
        || token.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase)
        || token.Equals("VACUUM", StringComparison.OrdinalIgnoreCase);

    private static bool MayChangeDatabaseSchema(string sql)
    {
        var statement = sql.AsSpan();
        var index = 0;
        while (index < statement.Length)
        {
            if (char.IsWhiteSpace(statement[index]))
            {
                index++;
                continue;
            }

            if (statement[index] is '\'' or '"' or '`' or '[')
            {
                if (!SkipQuotedSql(statement, ref index, statement[index]))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (IsDashCommentStart(statement, index))
            {
                index += 2;
                while (index < statement.Length && statement[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                continue;
            }

            if (statement[index] == '/'
                && index + 1 < statement.Length
                && statement[index + 1] == '*')
            {
                var end = statement[(index + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                index += end + 4;
                continue;
            }

            if (!char.IsLetter(statement[index]) && statement[index] != '_')
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < statement.Length
                   && (char.IsLetterOrDigit(statement[index]) || statement[index] == '_'))
            {
                index++;
            }

            var keyword = statement[start..index];
            if (keyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("DROP", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("RENAME", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("ATTACH", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("DETACH", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDashCommentStart(ReadOnlySpan<char> sql, int index) =>
        index + 1 < sql.Length
        && sql[index] == '-'
        && sql[index + 1] == '-'
        && (index + 2 == sql.Length
            || char.IsWhiteSpace(sql[index + 2])
            || char.IsControl(sql[index + 2]));

    private void RefreshTables()
    {
        Tables.Clear();
        foreach (var table in _allTables)
        {
            if (string.IsNullOrWhiteSpace(_tableFilter)
                || table.Name.Contains(_tableFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Tables.Add(table);
            }
        }

        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Content-fitted column widths, TablePlus-style: wide enough for the
    /// header and the widest visible value, clamped so one long cell cannot
    /// push every other column off screen.
    /// </summary>
    private static double[] ComputeColumnWidths(DatabaseQueryPage page)
    {
        const double CharacterWidth = 6.6;
        const double CellPadding = 22;
        var widths = new double[page.Columns.Count];
        for (var index = 0; index < page.Columns.Count; index++)
        {
            var longest = page.Columns[index].Name.Length;
            foreach (var row in page.ValueRows)
            {
                longest = Math.Max(longest, row[index].DisplayText.Length);
            }

            widths[index] = Math.Clamp(
                CellPadding + (CharacterWidth * longest),
                76,
                340);
        }

        return widths;
    }

    private void SetConnected(bool value)
    {
        if (_isConnected == value)
        {
            return;
        }

        _isConnected = value;
        if (!value)
        {
            ResetSqlLanguageSession();
            ResetDatabaseDiagram();
        }
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConnectButtonLabel));
        OnPropertyChanged(nameof(ConnectionSummary));
        RaiseCommandStates();
    }

    private void ResetDatabaseDiagram()
    {
        _diagramCancellation?.Cancel();
        _diagramCancellation?.Dispose();
        _diagramCancellation = null;
        var previous = _diagramSession;
        _diagramSession = null;
        if (previous is not null)
        {
            _clipboardBuildCancellation?.Cancel();
            ++_clipboardRevision;
            _ = previous.DisposeAsync().AsTask();
        }

        OnPropertyChanged(nameof(DiagramSession));
        MermaidDiagramSource = string.Empty;
        MermaidDiagramText = string.Empty;
        SelectedDatabaseOverviewMode = DatabaseOverviewMode.Objects;
        OnPropertyChanged(nameof(HasMermaidDiagram));
    }

    private void RaiseCommandStates()
    {
        (ConnectCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
        (RunQueryCommand as AsyncActionCommand)?.RaiseCanExecuteChanged();
    }
}
