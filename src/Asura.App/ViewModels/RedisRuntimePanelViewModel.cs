using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Asura.Application;
using Asura.Core;
using Avalonia.Threading;

namespace Asura.App.ViewModels;

public enum RedisWorkspacePerspective
{
    Browser,
    Search,
    PubSub,
}

public sealed record RedisDatabaseOption(int Index, string Label)
{
    public override string ToString() => Label;
}

public sealed class RedisKeyItemViewModel : ObservableObject
{
    private readonly TimeProvider _time;
    private DateTimeOffset? _expiresAt;

    public RedisKeyItemViewModel(RedisKeySummary summary, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Summary = summary;
        Apply(summary);
    }

    public RedisKeySummary Summary { get; private set; }

    public string Name => Summary.Key.DisplayName;

    public string Type => Summary.Type;

    /// <summary>Whether this key is on a clock, and so has a value that changes
    /// on its own between reads.</summary>
    public bool IsExpiring => _expiresAt is not null;

    /// <summary>
    /// What is left of the key's life, counted down from the moment the server
    /// told us. A TTL is a deadline, not a fact about the key: printed as the
    /// server's figure it is only true for the instant it arrived.
    /// </summary>
    public string Ttl
    {
        get
        {
            if (_expiresAt is not { } deadline)
            {
                return "persistent";
            }

            var left = deadline - _time.GetUtcNow();
            return left > TimeSpan.Zero
                ? $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}"
                : "expired";
        }
    }

    public string Memory => Summary.MemoryBytes is { } bytes ? FormatBytes(bytes) : "-";

    /// <summary>
    /// Takes a freshly read description of the same key. Reading or writing a
    /// key answers with its current TTL and size, and a row still showing the
    /// figures from the scan that found it is a row stating something the
    /// server has since contradicted.
    /// </summary>
    public void Apply(RedisKeySummary summary)
    {
        Summary = summary;
        _expiresAt = summary.TimeToLive is { } ttl ? _time.GetUtcNow() + ttl : null;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(IsExpiring));
        OnPropertyChanged(nameof(Memory));
        OnPropertyChanged(nameof(Ttl));
    }

    /// <summary>Re-reads the clock. Nothing about the key changed.</summary>
    public void Tick() => OnPropertyChanged(nameof(Ttl));

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.#} MB",
    };
}

/// <summary>
/// One row of a form that writes several entries at once: a hash field and its
/// value, a list value, a set member, or a member with its score. A collection
/// is filled a handful of entries at a time, and a form that takes one per
/// round trip is a form that makes the user do the repeating.
/// </summary>
public sealed class RedisEntryDraft : ObservableObject
{
    private string _field = string.Empty;
    private string _value = string.Empty;
    private string _score = "0";

    public string Field
    {
        get => _field;
        set => SetProperty(ref _field, value ?? string.Empty);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    public string Score
    {
        get => _score;
        set => SetProperty(ref _score, value ?? string.Empty);
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Field)
        && string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// What a form for one Redis type is made of. A hash needs a field beside its
/// value, a sorted set needs a score, a set has members rather than values, and
/// the button does something different in each case — "Apply" over a row of
/// generically named boxes describes none of them.
/// </summary>
public sealed record RedisKeyForm(
    string ValueLabel,
    string? FieldLabel,
    string? ScoreLabel,
    string ActionLabel,
    bool IsValueMultiline,
    string? EntriesLabel = null,
    bool IsJson = false,
    bool CanRemoveEntries = false,
    bool HasWholeValue = false,
    string? EditLabel = null,
    string? EntryIdentityLabel = null)
{
    public bool HasField => FieldLabel is not null;

    public bool HasScore => ScoreLabel is not null;

    /// <summary>Whether the add form is written a collection at a time.</summary>
    public bool HasEntries => EntriesLabel is not null;

    /// <summary>
    /// Whether the value is one plain box rather than rows or a document: a
    /// string, a stream entry, a time-series sample.
    /// </summary>
    public bool HasSingleValue => !HasEntries && !IsJson;

    /// <summary>Whether a field belongs beside that one box. A collection also
    /// has fields, but they belong to its rows, not to the form.</summary>
    public bool HasSingleField => HasField && HasSingleValue;

    /// <summary>Whether there is anything to add at all. A string and a JSON
    /// document have one value; writing it is editing it.</summary>
    public bool HasAddForm => !HasWholeValue;

    /// <summary>
    /// Whether an entry already in the key can be rewritten. Stream entries
    /// cannot: Redis has no command that edits one.
    /// </summary>
    public bool CanEditEntry => EditLabel is not null;

    /// <summary>Whether there is anything to do to what is already in the key —
    /// a stream can only have entries taken out of it.</summary>
    public bool HasEntryForm => CanEditEntry || CanRemoveEntries;

    public bool EditsPlainValue => CanEditEntry && !IsJson;

    /// <summary>
    /// Whether the entry form is about a row the table points at. A string and
    /// a document are the key's whole value; there is nothing to point at.
    /// </summary>
    public bool HasEntryIdentity => EntryIdentityLabel is not null && !HasWholeValue;

    /// <summary>What the entry form is, in the type's own terms.</summary>
    public string EntryFormHeading => HasWholeValue
        ? IsJson ? "Document" : "Value"
        : "Selected entry";

    public bool EditsJson => CanEditEntry && IsJson;

    public static RedisKeyForm For(string type) => type switch
    {
        "hash" => new(
            "Value", "Field", null, "Add fields", true,
            EntriesLabel: "Fields",
            CanRemoveEntries: true,
            EditLabel: "Save field",
            EntryIdentityLabel: "Field"),
        "list" => new(
            "Value", null, null, "Append", true,
            EntriesLabel: "Values",
            CanRemoveEntries: true,
            EditLabel: "Save value",
            EntryIdentityLabel: "Position"),
        "set" => new(
            "Member", null, null, "Add members", false,
            EntriesLabel: "Members",
            CanRemoveEntries: true,
            EditLabel: "Replace member",
            EntryIdentityLabel: "Member"),
        "zset" => new(
            "Member", null, "Score", "Add members", false,
            EntriesLabel: "Members",
            CanRemoveEntries: true,
            EditLabel: "Save member",
            EntryIdentityLabel: "Member"),
        "stream" => new(
            "Value", "Field", null, "Append entry", true,
            CanRemoveEntries: true,
            EntryIdentityLabel: "Entry"),
        "json" => new(
            "JSON", null, null, "Replace document", true,
            IsJson: true,
            HasWholeValue: true,
            EditLabel: "Replace document"),
        "timeseries" => new("Sample", null, null, "Add sample", false),
        _ => new(
            "Value", null, null, "Set value", true,
            HasWholeValue: true,
            EditLabel: "Set value"),
    };
}

public sealed record RedisValueEntryViewModel(RedisValueEntry Entry)
{
    public string Identity => Entry.Identity;
    public string Field => Entry.Field ?? string.Empty;
    public string Value => Entry.Value;
    public string Score => Entry.Score?.ToString("G", CultureInfo.InvariantCulture) ?? string.Empty;
}

public sealed record RedisPubSubMessageViewModel(RedisPubSubMessage Message)
{
    public string Time => Message.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Channel => Message.Channel;
    public string Payload => Message.Payload;
    public string Kind => Message.Subscription.Kind.ToString();
}

/// <summary>
/// The Redis-specific runtime behind the Database panel shell. It owns a live
/// session because SCAN cursors, topology and subscriptions cannot be modeled
/// as the relational client's independent, pooled calls.
/// </summary>
public sealed class RedisRuntimePanelViewModel : RuntimePanelViewModel
{
    private const int ScanBatchSize = 200;
    private const int MaximumValueEntries = 500;
    private const int MaximumPubSubMessages = 500;

    private readonly IRedisPanelSessionFactory _sessions;
    private readonly IDatabaseConnectionCatalog _connections;
    private readonly Func<DatabaseConnectionProfile, CancellationToken, Task<string?>>? _passwordResolver;
    private readonly Func<DatabaseConnectionProfileId, string, CancellationToken,
        Task<DatabaseConnectionProfile?>>? _passwordPersister;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _hostBindingId = SessionId.New().Value;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer? _expiryTimer;
    private HostedPanelSessionLink? _hostedSession;
    private ISessionHostClient? _hostSessionClient;
    private Task _hostInitialization = Task.CompletedTask;
    private long _hostBindingRevision;
    private IRedisPanelSession? _session;
    private DatabaseConnectionProfile? _savedConnection;
    private ConnectionProfile? _tunnelConnection;
    private string? _sessionPassword;
    private readonly DatabaseRecoveryPayload _initialRecoveryInput;
    private DatabaseRecoveryPayload? _acceptedInitialRecoveryBinding;
    private DatabaseRecoveryPayload? _latestResolvedRecoveryBinding;
    private readonly DatabaseRecoveryState? _recovery;
    private ConnectionProfile? _savedTunnel;
    private readonly bool _deferStoredCredentialAccess;
    private bool _initializationStarted;
    private bool _persistedConnection;
    private bool _isBusy;
    private string _statusText = "Disconnected";
    private string? _errorMessage;
    private string _scanPattern = "*";
    private string? _scanCursor;
    private bool _scanComplete;
    private string _newKeyName = string.Empty;
    private string _newKeyType = "string";
    private string _newKeyField = string.Empty;
    private string _newKeyValue = string.Empty;
    private string _newKeyScore = "0";
    private string _newKeyExpirySeconds = string.Empty;
    private bool _isCreatingKey;
    private RedisKeyItemViewModel? _selectedKey;
    private RedisValueEntryViewModel? _selectedValueEntry;
    private string _editValue = string.Empty;
    private string _editScore = "0";
    private RedisKeySnapshot? _selectedSnapshot;
    private RedisDatabaseOption? _selectedDatabase;
    private RedisWorkspacePerspective _perspective;
    private string _mutationField = string.Empty;
    private string _mutationValue = string.Empty;
    private string _mutationScore = "0";
    private string _expirySeconds = string.Empty;
    private bool _deleteArmed;
    private string _subscriptionName = string.Empty;
    private RedisSubscriptionKind _subscriptionKind;
    private RedisSubscription? _selectedSubscription;
    private string _publishChannel = string.Empty;
    private string _publishPayload = string.Empty;
    private bool _publishSharded;
    private string _searchIndex = string.Empty;
    private string _searchQuery = "*";
    private bool _disposed;

    public RedisRuntimePanelViewModel(
        PanelInstanceId id,
        string title,
        IRedisPanelSessionFactory sessions,
        IDatabaseConnectionCatalog connections,
        string? connectionString = null,
        ConnectionProfile? tunnelConnection = null,
        DatabaseConnectionProfile? savedConnection = null,
        Func<DatabaseConnectionProfile, CancellationToken, Task<string?>>? passwordResolver = null,
        Func<DatabaseConnectionProfileId, string, CancellationToken,
            Task<DatabaseConnectionProfile?>>? passwordPersister = null,
        string passwordStoreLabel = "Save in system credential store",
        TimeProvider? timeProvider = null,
        bool deferStoredCredentialAccess = false,
        string? sessionPassword = null,
        DatabaseRecoveryState? recovery = null,
        bool persistedConnection = true)
        : base(id, PanelKind.DatabaseViewer, title, "Database")
    {
        _time = timeProvider ?? TimeProvider.System;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _passwordResolver = passwordResolver;
        _passwordPersister = passwordPersister;
        _savedConnection = savedConnection;
        _persistedConnection = savedConnection is not null && persistedConnection;
        _tunnelConnection = tunnelConnection;
        _savedTunnel = tunnelConnection;
        _sessionPassword = sessionPassword;
        _recovery = recovery;
        _deferStoredCredentialAccess = deferStoredCredentialAccess;
        ConnectionString = connectionString ?? savedConnection?.ConnectionString ?? string.Empty;
        PasswordStoreLabel = passwordStoreLabel;

        ConnectCommand = new AsyncActionCommand(ConnectAsync, () => !IsBusy && HasConnectionTarget);
        DisconnectCommand = new AsyncActionCommand(DisconnectAsync, () => IsConnected);
        ScanCommand = new AsyncActionCommand(RestartScanAsync, () => IsConnected && !IsBusy);
        LoadMoreCommand = new AsyncActionCommand(LoadMoreAsync, () => IsConnected && !IsBusy && !ScanComplete);
        CreateKeyCommand = new AsyncActionCommand(CreateKeyAsync, () => IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(NewKeyName));
        SaveValueCommand = new AsyncActionCommand(SaveValueAsync, () => CanMutateSelectedKey);
        DeleteKeyCommand = new AsyncActionCommand(DeleteSelectedKeyAsync, () => SelectedKey is not null && IsConnected);
        SaveEntryCommand = new AsyncActionCommand(
            SaveEntryAsync,
            () => IsConnected
                && !IsBusy
                && MutationForm.CanEditEntry
                && (MutationForm.HasWholeValue || SelectedValueEntry is not null));
        SetExpiryCommand = new AsyncActionCommand(
            ApplyExpiryAsync,
            () => IsConnected && !IsBusy && SelectedKey is not null);
        RemoveEntryCommand = new AsyncActionCommand(
            RemoveSelectedEntryAsync,
            () => IsConnected
                && !IsBusy
                && SelectedValueEntry is not null
                && MutationForm.CanRemoveEntries);
        SubscribeCommand = new AsyncActionCommand(SubscribeAsync, () => IsConnected && !string.IsNullOrWhiteSpace(SubscriptionName));
        UnsubscribeCommand = new AsyncActionCommand(UnsubscribeAsync, () => IsConnected && SelectedSubscription is not null);
        PublishCommand = new AsyncActionCommand(PublishAsync, () => IsConnected && !string.IsNullOrWhiteSpace(PublishChannel));
        RefreshIndexesCommand = new AsyncActionCommand(LoadIndexesAsync, () => IsConnected && Facts?.SearchAvailable == true);
        SearchCommand = new AsyncActionCommand(SearchAsync, () => IsConnected && !string.IsNullOrWhiteSpace(SearchIndex));

        // The scan summary counts keys, so it is stale until the collection that
        // holds them says it changed; the empty states are the same statement
        // for the other four surfaces.
        Keys.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasKeys));
            OnPropertyChanged(nameof(ScanProgressText));
        };
        ValueEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasValueEntries));
        PubSubMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPubSubMessages));
        Subscriptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSubscriptions));
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSearchResults));

        // A TTL is the one figure on screen that changes with nothing
        // happening, so a second's tick is what keeps it true. Only keys that
        // are actually on a clock are told; a list of persistent keys costs
        // nothing. Runs only where a UI thread exists — the view models are
        // also composed headless in tests.
        if (Dispatcher.UIThread.CheckAccess())
        {
            _expiryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _expiryTimer.Tick += (_, _) => TickExpiries();
            _expiryTimer.Start();
        }

        // Session recovery runs during startup. Keep a credential-backed
        // restored panel disconnected until Connect supplies user intent.
        _initialRecoveryInput = CaptureRecoveryInput();
        Initialization = recovery is null && HasConnectionTarget
            && !(deferStoredCredentialAccess && RequiresStoredCredentialAccess)
            ? ConnectAsync()
            : Task.CompletedTask;
    }

    public event EventHandler? PasswordRequested;

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
            PanelKind.DatabaseViewer);
        _hostInitialization = InitializeHostedSessionAsync();
        return _hostInitialization;
    }

    public Task Initialization { get; private set; }

    public void StartInitialization()
    {
        if (_initializationStarted || _recovery is null || _disposed)
        {
            return;
        }
        _initializationStarted = true;
        if (HasConnectionTarget && !(_deferStoredCredentialAccess && RequiresStoredCredentialAccess))
        {
            Initialization = ConnectAsync();
        }
    }

    private bool RequiresStoredCredentialAccess =>
        _savedConnection?.PasswordSecret is not null
        || _tunnelConnection?.Authentication is
            ConnectionAuthentication.Password or ConnectionAuthentication.PrivateKey;

    public string ConnectionString { get; private set; }

    public bool IsConnected => _session is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

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

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public RedisServerFacts? Facts { get; private set; }

    public string ServerFactsText => Facts is null
        ? "Redis"
        : $"Redis {Facts.Version ?? "unknown"} · {Facts.Topology} · DB {Facts.SelectedDatabase}";

    public bool SearchAvailable => Facts?.SearchAvailable == true;

    public bool HasConnectionTarget => !string.IsNullOrWhiteSpace(ConnectionString);

    public bool IsSavedConnection => _savedConnection is not null;

    public bool CanStorePassword =>
        _passwordPersister is not null
        && _persistedConnection
        && _savedConnection is { PasswordSecret: null };

    public string PasswordStoreLabel { get; }

    public DatabaseConnectionProfileId? SavedConnectionId => _savedConnection?.Id;

    public string? SavedConnectionName => _savedConnection?.Name;

    public string ConnectionDisplayName => _savedConnection?.Name ?? (HasConnectionTarget ? "Redis" : "Select connection");

    private bool UsesUnmodifiedSavedTarget => _savedConnection is { } saved && _persistedConnection
        && string.Equals(ConnectionString, saved.ConnectionString, StringComparison.Ordinal)
        && _tunnelConnection == _savedTunnel
        && _sessionPassword is null;

    public string? RecoveryTarget => UsesUnmodifiedSavedTarget && _savedConnection is { } saved
        ? $"saved:{saved.Id.Value}"
        : _recovery?.Target;

    internal bool CanPreserveInitialSourceConnection => _acceptedInitialRecoveryBinding is not null
        && _latestResolvedRecoveryBinding == _acceptedInitialRecoveryBinding
        && CaptureRecoveryInput() == _initialRecoveryInput
        && SourceDefinition is { } source
        && _initialRecoveryInput.MatchesSourceTarget(source, RecoveryTarget);

    private DatabaseRecoveryPayload CaptureRecoveryInput() =>
        new(RedisDatabase.DriverId, ConnectionString, _sessionPassword, _tunnelConnection, _savedConnection);

    public Task RecoveryPersistence { get; private set; } = Task.CompletedTask;

    private async Task PersistRecoveryAsync(string effectiveConnectionString)
    {
        if (_recovery is null || UsesUnmodifiedSavedTarget || !HasConnectionTarget || _disposed)
        {
            return;
        }
        try
        {
            var saved = await _recovery.SaveAsync(new(RedisDatabase.DriverId, effectiveConnectionString,
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

    public ConnectionId? TunnelConnectionId => _tunnelConnection?.Id;

    public IReadOnlyList<RedisDatabaseOption> Databases { get; private set; } = [];

    public RedisDatabaseOption? SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            if (!SetProperty(ref _selectedDatabase, value) || value is null || !IsConnected)
            {
                return;
            }

            _ = ChangeDatabaseAsync(value.Index);
        }
    }

    public RedisWorkspacePerspective Perspective
    {
        get => _perspective;
        set
        {
            if (SetProperty(ref _perspective, value))
            {
                OnPropertyChanged(nameof(ShowBrowser));
                OnPropertyChanged(nameof(ShowSearch));
                OnPropertyChanged(nameof(ShowPubSub));
            }
        }
    }

    public bool ShowBrowser => Perspective == RedisWorkspacePerspective.Browser;
    public bool ShowSearch => Perspective == RedisWorkspacePerspective.Search;
    public bool ShowPubSub => Perspective == RedisWorkspacePerspective.PubSub;

    public ObservableCollection<RedisKeyItemViewModel> Keys { get; } = [];

    public ObservableCollection<RedisValueEntryViewModel> ValueEntries { get; } = [];

    public ObservableCollection<RedisPubSubMessageViewModel> PubSubMessages { get; } = [];

    public ObservableCollection<RedisSubscription> Subscriptions { get; } = [];

    public ObservableCollection<RedisSearchIndex> SearchIndexes { get; } = [];

    public ObservableCollection<RedisValueEntryViewModel> SearchResults { get; } = [];

    // What each surface shows when its collection is empty is a state the view
    // draws, so each collection says whether it has anything in it.
    public bool HasKeys => Keys.Count > 0;

    public bool HasValueEntries => ValueEntries.Count > 0;

    public bool HasPubSubMessages => PubSubMessages.Count > 0;

    public bool HasSubscriptions => Subscriptions.Count > 0;

    public bool HasSearchResults => SearchResults.Count > 0;

    public string ScanPattern
    {
        get => _scanPattern;
        set => SetProperty(ref _scanPattern, value ?? string.Empty);
    }

    public bool ScanComplete
    {
        get => _scanComplete;
        private set
        {
            if (SetProperty(ref _scanComplete, value))
            {
                OnPropertyChanged(nameof(ScanProgressText));
                LoadMoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ScanProgressText => ScanComplete
        ? $"Scan complete · {Keys.Count} keys loaded"
        : $"{Keys.Count} keys loaded · live SCAN";

    public IReadOnlyList<string> NewKeyTypeOptions { get; } =
        ["string", "hash", "list", "set", "zset", "stream", "json", "timeseries"];

    public string NewKeyName { get => _newKeyName; set { if (SetProperty(ref _newKeyName, value ?? string.Empty)) { CreateKeyCommand.RaiseCanExecuteChanged(); } } }
    public string NewKeyType
    {
        get => _newKeyType;
        set
        {
            if (SetProperty(ref _newKeyType, value ?? "string"))
            {
                OnPropertyChanged(nameof(NewKeyForm));
            }
        }
    }
    public string NewKeyField { get => _newKeyField; set => SetProperty(ref _newKeyField, value ?? string.Empty); }
    public string NewKeyValue { get => _newKeyValue; set => SetProperty(ref _newKeyValue, value ?? string.Empty); }
    public string NewKeyScore { get => _newKeyScore; set => SetProperty(ref _newKeyScore, value ?? string.Empty); }

    /// <summary>Seconds the new key should live for. Empty creates it without
    /// a deadline, which is Redis's own default.</summary>
    public string NewKeyExpirySeconds { get => _newKeyExpirySeconds; set => SetProperty(ref _newKeyExpirySeconds, value ?? string.Empty); }
    /// <summary>The shape of the form that creates a key of the chosen type.</summary>
    public RedisKeyForm NewKeyForm => RedisKeyForm.For(NewKeyType);

    /// <summary>
    /// Whether the create-key sheet is open. It is a region of the panel rather
    /// than a popup: a flyout is a window of its own and can be shown outside
    /// the frame it belongs to, which is where this one kept ending up.
    /// </summary>
    public bool IsCreatingKey
    {
        get => _isCreatingKey;
        private set => SetProperty(ref _isCreatingKey, value);
    }

    public void BeginCreateKey() => IsCreatingKey = true;

    public void CancelCreateKey() => IsCreatingKey = false;

    /// <summary>The rows a new collection is created from.</summary>
    public ObservableCollection<RedisEntryDraft> NewKeyEntries { get; } = [new()];

    public void AddNewKeyEntry() => NewKeyEntries.Add(new RedisEntryDraft());

    /// <summary>Takes a row out, keeping the one the form is made of.</summary>
    public void RemoveNewKeyEntry(RedisEntryDraft entry)
    {
        if (NewKeyEntries.Count > 1)
        {
            NewKeyEntries.Remove(entry);
        }
        else
        {
            NewKeyEntries[0].Field = string.Empty;
            NewKeyEntries[0].Value = string.Empty;
        }
    }

    public RedisKeyItemViewModel? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (!SetProperty(ref _selectedKey, value))
            {
                return;
            }

            _deleteArmed = false;
            OnPropertyChanged(nameof(DeleteKeyLabel));
            OnPropertyChanged(nameof(IsDeleteArmed));
            OnPropertyChanged(nameof(HasSelectedKey));
            OnPropertyChanged(nameof(CanMutateSelectedKey));
            OnPropertyChanged(nameof(MutationForm));
            _ = value is null ? ClearSelectionAsync() : ReadSelectedKeyAsync();
        }
    }

    public bool HasSelectedKey => SelectedKey is not null;

    /// <summary>The entry the value table is pointing at, which is the one the
    /// edit form acts on.</summary>
    public RedisValueEntryViewModel? SelectedValueEntry
    {
        get => _selectedValueEntry;
        set
        {
            if (!SetProperty(ref _selectedValueEntry, value))
            {
                return;
            }

            // The edit form is about this entry, so it starts from what the
            // entry currently holds.
            EditValue = value?.Value ?? string.Empty;
            EditScore = value?.Score is { Length: > 0 } score ? score : "0";
            OnPropertyChanged(nameof(HasSelectedValueEntry));
            OnPropertyChanged(nameof(EditIdentity));
            RemoveEntryCommand.RaiseCanExecuteChanged();
            SaveEntryCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedValueEntry => SelectedValueEntry is not null;

    /// <summary>What the selected entry is addressed by — a field name, a
    /// position, a member, a stream id.</summary>
    public string EditIdentity => SelectedValueEntry?.Identity ?? string.Empty;

    /// <summary>The value the edit form will write back.</summary>
    public string EditValue
    {
        get => _editValue;
        set => SetProperty(ref _editValue, value ?? string.Empty);
    }

    public string EditScore
    {
        get => _editScore;
        set => SetProperty(ref _editScore, value ?? string.Empty);
    }

    /// <summary>What removing the pointed-at entry does, in the type's words.</summary>
    public string RemoveEntryLabel => SelectedKeyType switch
    {
        "hash" => "Remove field",
        "stream" => "Remove entry",
        "set" or "zset" => "Remove member",
        _ => "Remove value",
    };

    public string SelectedKeyType => _selectedSnapshot?.Summary.Type ?? string.Empty;

    public string SelectedKeyMetadata => _selectedSnapshot is null
        ? "Select a key to inspect its value."
        : string.Join(
            " · ",
            _selectedSnapshot.Summary.Type,
            // A string's length is its bytes; everything else counts members.
            _selectedSnapshot.Length is not { } length
                ? "size unknown"
                : string.Equals(_selectedSnapshot.Summary.Type, "string"
, StringComparison.Ordinal) ? $"{length.ToString(CultureInfo.InvariantCulture)} bytes"
                    : $"{length.ToString(CultureInfo.InvariantCulture)} items",
            SelectedKey?.IsExpiring == true ? $"TTL {SelectedKey.Ttl}" : "persistent",
            SelectedKey?.Memory ?? "-");

    public string? SelectedKeyLimitation => _selectedSnapshot?.Limitation;

    public bool HasSelectedKeyLimitation => !string.IsNullOrWhiteSpace(SelectedKeyLimitation);

    public bool CanMutateSelectedKey => IsConnected
        && SelectedKeyType is "string" or "hash" or "list" or "set" or "zset" or "stream" or "json" or "timeseries"
        && !IsBusy;

    /// <summary>The shape of the form that writes to the selected key.</summary>
    public RedisKeyForm MutationForm => RedisKeyForm.For(SelectedKeyType);

    /// <summary>The rows the next write to a collection is made of.</summary>
    public ObservableCollection<RedisEntryDraft> MutationEntries { get; } = [new()];

    public void AddMutationEntry() => MutationEntries.Add(new RedisEntryDraft());

    public void RemoveMutationEntry(RedisEntryDraft entry)
    {
        if (MutationEntries.Count > 1)
        {
            MutationEntries.Remove(entry);
        }
        else
        {
            MutationEntries[0].Field = string.Empty;
            MutationEntries[0].Value = string.Empty;
        }
    }

    public string MutationField { get => _mutationField; set => SetProperty(ref _mutationField, value ?? string.Empty); }
    public string MutationValue { get => _mutationValue; set => SetProperty(ref _mutationValue, value ?? string.Empty); }
    public string MutationScore { get => _mutationScore; set => SetProperty(ref _mutationScore, value ?? string.Empty); }
    public string ExpirySeconds { get => _expirySeconds; set => SetProperty(ref _expirySeconds, value ?? string.Empty); }
    public string DeleteKeyLabel => _deleteArmed ? "Confirm delete" : "Delete key";

    /// <summary>
    /// Whether the delete action is one press from running. Quiet until then:
    /// a permanently loud destructive button in a header trains people to
    /// ignore the colour that is supposed to stop them.
    /// </summary>
    public bool IsDeleteArmed => _deleteArmed;

    public IReadOnlyList<RedisSubscriptionKind> SubscriptionKinds { get; } = Enum.GetValues<RedisSubscriptionKind>();
    public string SubscriptionName { get => _subscriptionName; set { if (SetProperty(ref _subscriptionName, value ?? string.Empty)) { SubscribeCommand.RaiseCanExecuteChanged(); } } }
    public RedisSubscriptionKind SubscriptionKind { get => _subscriptionKind; set => SetProperty(ref _subscriptionKind, value); }
    public RedisSubscription? SelectedSubscription { get => _selectedSubscription; set { if (SetProperty(ref _selectedSubscription, value)) { UnsubscribeCommand.RaiseCanExecuteChanged(); } } }
    public string PublishChannel { get => _publishChannel; set { if (SetProperty(ref _publishChannel, value ?? string.Empty)) { PublishCommand.RaiseCanExecuteChanged(); } } }
    public string PublishPayload { get => _publishPayload; set => SetProperty(ref _publishPayload, value ?? string.Empty); }
    public bool PublishSharded { get => _publishSharded; set => SetProperty(ref _publishSharded, value); }
    public string SearchIndex { get => _searchIndex; set { if (SetProperty(ref _searchIndex, value ?? string.Empty)) { SearchCommand.RaiseCanExecuteChanged(); } } }
    public string SearchQuery { get => _searchQuery; set => SetProperty(ref _searchQuery, value ?? string.Empty); }

    public AsyncActionCommand ConnectCommand { get; }
    public AsyncActionCommand DisconnectCommand { get; }
    public AsyncActionCommand ScanCommand { get; }
    public AsyncActionCommand LoadMoreCommand { get; }
    public AsyncActionCommand CreateKeyCommand { get; }
    public AsyncActionCommand SaveValueCommand { get; }
    public AsyncActionCommand DeleteKeyCommand { get; }

    public AsyncActionCommand RemoveEntryCommand { get; }

    /// <summary>Writes the edit form back over what it was opened on.</summary>
    public AsyncActionCommand SaveEntryCommand { get; }

    /// <summary>Applies the key's deadline on its own, without writing a value.</summary>
    public AsyncActionCommand SetExpiryCommand { get; }
    public AsyncActionCommand SubscribeCommand { get; }
    public AsyncActionCommand UnsubscribeCommand { get; }
    public AsyncActionCommand PublishCommand { get; }
    public AsyncActionCommand RefreshIndexesCommand { get; }
    public AsyncActionCommand SearchCommand { get; }

    public async Task ConnectAsync()
    {
        if (IsBusy || !HasConnectionTarget)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusText = "Connecting";
        try
        {
            _latestResolvedRecoveryBinding = null;
            await DisconnectCoreAsync().ConfigureAwait(true);
            var input = CaptureRecoveryInput();
            var connectionString = await ResolveConnectionStringAsync(_lifetime.Token).ConfigureAwait(true);
            if (connectionString is null)
            {
                StatusText = "Password required";
                PasswordRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            RecoveryPersistence = PersistRecoveryAsync(connectionString);
            await RecoveryPersistence;
            _session = await _sessions.OpenAsync(connectionString, _tunnelConnection, _lifetime.Token)
                .ConfigureAwait(true);
            _latestResolvedRecoveryBinding = input with { ConnectionString = connectionString };
            if (input == _initialRecoveryInput && CaptureRecoveryInput() == input)
            {
                _acceptedInitialRecoveryBinding ??= _latestResolvedRecoveryBinding;
            }
            _session.MessageReceived += OnMessageReceived;
            Facts = _session.Facts;
            BuildDatabaseOptions();
            StatusText = "Connected";
            OnConnectionChanged();
            await RestartScanCoreAsync(_lifetime.Token).ConfigureAwait(true);
            if (Facts.SearchAvailable)
            {
                await LoadIndexesCoreAsync(_lifetime.Token).ConfigureAwait(true);
            }

            QueueHostedSessionEnsure(WithSelectedDatabase(
                connectionString,
                Facts.SelectedDatabase));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsAuthenticationFailure(exception))
        {
            ErrorMessage = null;
            StatusText = "Password required";
            await DisconnectCoreAsync().ConfigureAwait(true);
            PasswordRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = "Connection failed";
            await DisconnectCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            OnConnectionChanged();
        }
    }

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

        var saved = await _passwordPersister!(profile.Id, password, cancellationToken)
            .ConfigureAwait(false);
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

    public void ApplySavedConnection(
        DatabaseConnectionProfile profile,
        string? sessionPassword = null,
        ConnectionProfile? tunnel = null,
        bool persisted = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        InvalidateHostedBinding();
        _savedConnection = profile;
        _persistedConnection = persisted;
        _sessionPassword = string.IsNullOrEmpty(sessionPassword) ? null : sessionPassword;
        _savedTunnel = tunnel;
        _tunnelConnection = tunnel;
        ConnectionString = profile.ConnectionString;
        OnPropertyChanged(nameof(IsSavedConnection));
        OnPropertyChanged(nameof(CanStorePassword));
        OnPropertyChanged(nameof(SavedConnectionId));
        OnPropertyChanged(nameof(SavedConnectionName));
        OnPropertyChanged(nameof(ConnectionDisplayName));
        OnPropertyChanged(nameof(RecoveryTarget));
        OnPropertyChanged(nameof(TunnelConnectionId));
        _ = ConnectAsync();
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _hostedSession?.Dispose();
            _expiryTimer?.Stop();
            _lifetime.Cancel();
            _ = DisconnectCoreAsync();
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

            var connectionString = await ResolveConnectionStringAsync(_lifetime.Token)
                .ConfigureAwait(true);
            if (connectionString is null)
            {
                return;
            }

            var selectedDatabase = Facts?.SelectedDatabase ?? 0;
            await EnsureHostedSessionAsync(
                    WithSelectedDatabase(connectionString, selectedDatabase),
                    _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // The direct Redis panel remains authoritative for human use;
            // hosted projection failures only remove agent reachability.
        }
    }

    private string WithSelectedDatabase(string connectionString, int database)
    {
        var details = _connections.ParseConnectionDetails(
            RedisDatabase.DriverId,
            connectionString);
        return _connections.BuildConnectionString(
            RedisDatabase.DriverId,
            details with
            {
                Database = database.ToString(CultureInfo.InvariantCulture),
            });
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
            RedisDatabase.DriverId,
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

    private async Task<string?> ResolveConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (_connections.ParseConnectionDetails(RedisDatabase.DriverId, ConnectionString).Password is not null)
        {
            return ConnectionString;
        }
        if (_sessionPassword is not null)
        {
            return WithPassword(_sessionPassword);
        }

        if (_savedConnection?.PasswordSecret is null || _passwordResolver is null)
        {
            return ConnectionString;
        }

        var password = await _passwordResolver(_savedConnection, cancellationToken).ConfigureAwait(false);
        return password is null ? null : WithPassword(password);
    }

    private string WithPassword(string password)
    {
        var details = _connections.ParseConnectionDetails(RedisDatabase.DriverId, ConnectionString);
        return _connections.BuildConnectionString(
            RedisDatabase.DriverId,
            details with { Password = password });
    }

    private async Task DisconnectAsync()
    {
        await DisconnectCoreAsync().ConfigureAwait(true);
        InvalidateHostedSession();
        StatusText = "Disconnected";
        OnConnectionChanged();
    }

    private async Task DisconnectCoreAsync()
    {
        if (_session is null)
        {
            return;
        }

        _session.MessageReceived -= OnMessageReceived;
        await _session.DisposeAsync().ConfigureAwait(true);
        _session = null;
        Facts = null;
        Databases = [];
        _selectedDatabase = null;
        Keys.Clear();
        ValueEntries.Clear();
        Subscriptions.Clear();
        SelectedSubscription = null;
    }

    private void BuildDatabaseOptions()
    {
        var count = Facts?.LogicalDatabases == RedisLogicalDatabaseMode.DatabaseZeroOnly
            ? 1
            : Math.Clamp(Facts?.ConfiguredDatabaseCount ?? 16, 1, 256);
        Databases = [.. Enumerable.Range(0, count).Select(index => new RedisDatabaseOption(index, $"DB {index}"))];
        _selectedDatabase = Databases.First(option => option.Index == (Facts?.SelectedDatabase ?? 0));
        OnPropertyChanged(nameof(Databases));
        OnPropertyChanged(nameof(SelectedDatabase));
    }

    private async Task ChangeDatabaseAsync(int database)
    {
        if (_session is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            await _session.SelectDatabaseAsync(database, token).ConfigureAwait(true);
            Facts = _session.Facts;
            OnPropertyChanged(nameof(Facts));
            OnPropertyChanged(nameof(ServerFactsText));
            await RestartScanCoreAsync(token).ConfigureAwait(true);
            InvalidateHostedBinding();
            var connectionString = await ResolveConnectionStringAsync(token)
                .ConfigureAwait(true);
            if (connectionString is not null)
            {
                ConnectionString = WithSelectedDatabase(connectionString, database);
                RecoveryPersistence = PersistRecoveryAsync(ConnectionString);
                await RecoveryPersistence;
                QueueHostedSessionEnsure(
                    ConnectionString);
            }
        }).ConfigureAwait(true);
    }

    private Task RestartScanAsync() => RunAsync(RestartScanCoreAsync);

    private async Task RestartScanCoreAsync(CancellationToken cancellationToken)
    {
        Keys.Clear();
        ValueEntries.Clear();
        _selectedKey = null;
        _selectedSnapshot = null;
        _scanCursor = null;
        ScanComplete = false;
        await LoadScanPageCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    private Task LoadMoreAsync() => RunAsync(LoadScanPageCoreAsync);

    private Task CreateKeyAsync() => RunAsync(async token =>
    {
        var name = NewKeyName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Enter a key name.");
        }

        var key = new RedisKeyReference(name, Encoding.UTF8.GetBytes(name));
        switch (NewKeyType)
        {
            case "string":
                await _session!.SetStringAsync(key, NewKeyValue, expiry: null, token).ConfigureAwait(true);
                break;
            case "hash":
            case "list":
            case "set":
            case "zset":
                await WriteEntriesAsync(key, NewKeyType, NewKeyEntries, token).ConfigureAwait(true);
                break;
            case "stream":
                RequireNewKeyField();
                await _session!.AddStreamEntryAsync(key, NewKeyField, NewKeyValue, token).ConfigureAwait(true);
                break;
            case "json":
                await _session!.SetJsonAsync(key, NewKeyValue, token).ConfigureAwait(true);
                break;
            case "timeseries":
                if (!double.TryParse(NewKeyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var sample))
                {
                    throw new ArgumentException("Enter a valid time-series sample value.");
                }

                await _session!.AddTimeSeriesSampleAsync(key, sample, token).ConfigureAwait(true);
                break;
            default:
                throw new ArgumentException($"Unsupported Redis key type: {NewKeyType}");
        }

        if (ParseExpiry(NewKeyExpirySeconds) is { } expiry)
        {
            await _session!.SetExpiryAsync(key, expiry, token).ConfigureAwait(true);
        }

        RedisKeySummary? summary = null;
        string? cursor = null;
        do
        {
            var page = await _session!.ScanKeysAsync(name, cursor, count: 10, token).ConfigureAwait(true);
            summary = page.Keys.FirstOrDefault(candidate => candidate.Key.Bytes.SequenceEqual(key.Bytes));
            cursor = page.NextCursor;
            if (summary is not null || page.IsComplete)
            {
                break;
            }
        }
        while (true);

        if (summary is not null)
        {
            var item = Keys.FirstOrDefault(candidate => candidate.Summary.Key.Bytes.SequenceEqual(key.Bytes));
            if (item is null)
            {
                item = new RedisKeyItemViewModel(summary, _time);
                Keys.Insert(0, item);
            }

            SelectedKey = item;
            await ReadSelectedKeyCoreAsync(key, token).ConfigureAwait(true);
        }

        NewKeyName = string.Empty;
        NewKeyValue = string.Empty;
        NewKeyExpirySeconds = string.Empty;
        NewKeyEntries.Clear();
        NewKeyEntries.Add(new RedisEntryDraft());
        IsCreatingKey = false;
        OnPropertyChanged(nameof(ScanProgressText));
    });

    private async Task LoadScanPageCoreAsync(CancellationToken cancellationToken)
    {
        if (_session is null || ScanComplete)
        {
            return;
        }

        var page = await _session.ScanKeysAsync(ScanPattern, _scanCursor, ScanBatchSize, cancellationToken)
            .ConfigureAwait(true);
        var existing = Keys.Select(item => Convert.ToBase64String(item.Summary.Key.Bytes)).ToHashSet(StringComparer.Ordinal);
        foreach (var summary in page.Keys)
        {
            if (existing.Add(Convert.ToBase64String(summary.Key.Bytes)))
            {
                Keys.Add(new RedisKeyItemViewModel(summary, _time));
            }
        }

        _scanCursor = page.NextCursor;
        ScanComplete = page.IsComplete;
        OnPropertyChanged(nameof(ScanProgressText));
    }

    private async Task ReadSelectedKeyAsync()
    {
        if (_session is null || SelectedKey is null)
        {
            return;
        }

        await RunAsync(async token =>
        {
            _selectedSnapshot = await _session.ReadKeyAsync(SelectedKey.Summary.Key, MaximumValueEntries, token)
                .ConfigureAwait(true);
            SelectedKey?.Apply(_selectedSnapshot.Summary);
            ExpirySeconds = _selectedSnapshot.Summary.TimeToLive is { } remaining
                ? ((long)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            ValueEntries.Clear();
            foreach (var entry in _selectedSnapshot.Entries)
            {
                ValueEntries.Add(new RedisValueEntryViewModel(entry));
            }

            OnSelectedSnapshotChanged();
        }).ConfigureAwait(true);
    }

    private Task ClearSelectionAsync()
    {
        ValueEntries.Clear();
        _selectedSnapshot = null;
        OnSelectedSnapshotChanged();
        return Task.CompletedTask;
    }

    private Task SaveValueAsync() => RunAsync(async token =>
    {
        if (_session is null || SelectedKey is null)
        {
            return;
        }

        var key = SelectedKey.Summary.Key;
        switch (SelectedKeyType)
        {
            case "hash":
            case "list":
            case "set":
            case "zset":
                await WriteEntriesAsync(key, SelectedKeyType, MutationEntries, token).ConfigureAwait(true);
                break;
            case "stream":
                RequireField();
                await _session.AddStreamEntryAsync(key, MutationField, MutationValue, token).ConfigureAwait(true);
                break;
            case "json":
                await _session.SetJsonAsync(key, MutationValue, token).ConfigureAwait(true);
                break;
            case "timeseries":
                if (!double.TryParse(MutationValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var sample))
                {
                    throw new ArgumentException("Enter a valid time-series sample value.");
                }

                await _session.AddTimeSeriesSampleAsync(key, sample, token).ConfigureAwait(true);
                break;
        }

        await ReadSelectedKeyCoreAsync(key, token).ConfigureAwait(true);
    });

    /// <summary>
    /// Writes the edit form over what it was opened on: the key's whole value
    /// for a string or a document, otherwise the entry the table points at.
    /// Each collection rewrites an entry its own way, and a set has no way at
    /// all — its members are their own identity, so replacing one is removing
    /// it and adding the other.
    /// </summary>
    private Task SaveEntryAsync() => RunAsync(async token =>
    {
        if (_session is null || SelectedKey is null)
        {
            return;
        }

        var key = SelectedKey.Summary.Key;
        var entry = SelectedValueEntry?.Entry;
        switch (SelectedKeyType)
        {
            case "string":
                await _session.SetStringAsync(key, EditValue, expiry: null, token).ConfigureAwait(true);
                break;
            case "json":
                await _session.SetJsonAsync(key, EditValue, token).ConfigureAwait(true);
                break;
            case "hash" when entry is not null:
                await _session.SetHashFieldAsync(key, entry.Field ?? entry.Identity, EditValue, token)
                    .ConfigureAwait(true);
                break;
            case "list" when entry is not null:
                if (!long.TryParse(entry.Identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    throw new InvalidOperationException("A list element is addressed by its position.");
                }

                await _session.SetListValueAsync(key, index, EditValue, token).ConfigureAwait(true);
                break;
            case "set" when entry is not null:
                if (!string.Equals(entry.Value, EditValue, StringComparison.Ordinal))
                {
                    await _session.RemoveEntryAsync(key, "set", entry, token).ConfigureAwait(true);
                    await _session.AddSetValueAsync(key, EditValue, token).ConfigureAwait(true);
                }

                break;
            case "zset" when entry is not null:
                if (!double.TryParse(EditScore, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                {
                    throw new ArgumentException("Enter a valid sorted-set score.");
                }

                if (!string.Equals(entry.Value, EditValue, StringComparison.Ordinal))
                {
                    await _session.RemoveEntryAsync(key, "zset", entry, token).ConfigureAwait(true);
                }

                await _session.AddSortedSetValueAsync(key, EditValue, score, token).ConfigureAwait(true);
                break;
            default:
                throw new NotSupportedException(
                    $"A {SelectedKeyType} entry cannot be rewritten in place.");
        }

        await ReadSelectedKeyCoreAsync(key, token).ConfigureAwait(true);
    });

    /// <summary>
    /// Every Redis key can carry a deadline, and it is a property of the key
    /// rather than of anything written into it: the box states what the key
    /// should have, a figure sets it and an empty box takes it away.
    /// </summary>
    private Task ApplyExpiryAsync() => RunAsync(async token =>
    {
        if (_session is null || SelectedKey is null)
        {
            return;
        }

        var key = SelectedKey.Summary.Key;
        await _session.SetExpiryAsync(key, ParseExpiry(ExpirySeconds), token).ConfigureAwait(true);
        await ReadSelectedKeyCoreAsync(key, token).ConfigureAwait(true);
    });

    private async Task ReadSelectedKeyCoreAsync(RedisKeyReference key, CancellationToken token)
    {
        _selectedSnapshot = await _session!.ReadKeyAsync(key, MaximumValueEntries, token).ConfigureAwait(true);
        SelectedKey?.Apply(_selectedSnapshot.Summary);
        SelectedValueEntry = null;
        ExpirySeconds = _selectedSnapshot.Summary.TimeToLive is { } remaining
            ? ((long)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        ValueEntries.Clear();
        foreach (var entry in _selectedSnapshot.Entries)
        {
            ValueEntries.Add(new RedisValueEntryViewModel(entry));
        }

        OnSelectedSnapshotChanged();
    }

    /// <summary>
    /// Takes one entry out of a collection. The key itself stays, even when the
    /// entry removed was its last — Redis drops an emptied collection, and the
    /// re-read that follows is what says which happened.
    /// </summary>
    private Task RemoveSelectedEntryAsync() => RunAsync(async token =>
    {
        if (_session is null || SelectedKey is null || SelectedValueEntry is null)
        {
            return;
        }

        var key = SelectedKey.Summary.Key;
        var outcome = await _session.RemoveEntryAsync(
                key,
                SelectedKeyType,
                SelectedValueEntry.Entry,
                token)
            .ConfigureAwait(true);
        SelectedValueEntry = null;
        await ReadSelectedKeyCoreAsync(key, token).ConfigureAwait(true);
        if (outcome == RedisEntryRemovalOutcome.Stale)
        {
            throw new InvalidOperationException(
                "The Redis entry changed after it was displayed. The value was refreshed without deleting it.");
        }
    });

    private Task DeleteSelectedKeyAsync()
    {
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            OnPropertyChanged(nameof(DeleteKeyLabel));
            OnPropertyChanged(nameof(IsDeleteArmed));
            return Task.CompletedTask;
        }

        return RunAsync(async token =>
        {
            if (_session is null || SelectedKey is null)
            {
                return;
            }

            await _session.DeleteKeyAsync(SelectedKey.Summary.Key, token).ConfigureAwait(true);
            Keys.Remove(SelectedKey);
            SelectedKey = null;
        });
    }

    private Task SubscribeAsync() => RunAsync(async token =>
    {
        var subscription = new RedisSubscription(SubscriptionKind, SubscriptionName.Trim());
        await _session!.SubscribeAsync(subscription, token).ConfigureAwait(true);
        if (!Subscriptions.Contains(subscription))
        {
            Subscriptions.Add(subscription);
        }

        SelectedSubscription = subscription;
    });

    private Task UnsubscribeAsync() => RunAsync(async token =>
    {
        if (SelectedSubscription is not { } subscription)
        {
            return;
        }

        await _session!.UnsubscribeAsync(subscription, token).ConfigureAwait(true);
        Subscriptions.Remove(subscription);
        SelectedSubscription = Subscriptions.FirstOrDefault();
    });

    private Task PublishAsync() => RunAsync(async token =>
    {
        await _session!.PublishAsync(PublishChannel.Trim(), PublishPayload, PublishSharded, token)
            .ConfigureAwait(true);
    });

    private Task LoadIndexesAsync() => RunAsync(LoadIndexesCoreAsync);

    private async Task LoadIndexesCoreAsync(CancellationToken token)
    {
        var indexes = await _session!.ListSearchIndexesAsync(token).ConfigureAwait(true);
        SearchIndexes.Clear();
        foreach (var index in indexes)
        {
            SearchIndexes.Add(index);
        }

        if (string.IsNullOrWhiteSpace(SearchIndex) && indexes.Count > 0)
        {
            SearchIndex = indexes[0].Name;
        }
    }

    private Task SearchAsync() => RunAsync(async token =>
    {
        var result = await _session!.SearchAsync(SearchIndex.Trim(), SearchQuery, 200, token)
            .ConfigureAwait(true);
        SearchResults.Clear();
        foreach (var entry in result.Values)
        {
            SearchResults.Add(new RedisValueEntryViewModel(entry));
        }

        StatusText = result.Truncated
            ? $"Search matched {result.Total}; showing first 200"
            : $"Search matched {result.Total}";
    });

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await operation(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnMessageReceived(object? sender, RedisPubSubMessage message)
    {
        _ = sender;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PubSubMessages.Insert(0, new RedisPubSubMessageViewModel(message));
            while (PubSubMessages.Count > MaximumPubSubMessages)
            {
                PubSubMessages.RemoveAt(PubSubMessages.Count - 1);
            }
        });
    }

    private static TimeSpan? ParseExpiry(string seconds)
    {
        if (string.IsNullOrWhiteSpace(seconds))
        {
            return null;
        }

        return double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
                ? TimeSpan.FromSeconds(parsed)
                : throw new ArgumentException("Expiry must be a positive number of seconds.");
    }

    /// <summary>
    /// Writes every row of a collection form. Rows left blank are skipped, so a
    /// form that offers more rows than the user needed does not write empties;
    /// a form with nothing in it at all is an error rather than a silent
    /// success.
    /// </summary>
    private async Task WriteEntriesAsync(
        RedisKeyReference key,
        string type,
        IReadOnlyList<RedisEntryDraft> entries,
        CancellationToken token)
    {
        var written = 0;
        foreach (var entry in entries)
        {
            if (entry.IsEmpty)
            {
                continue;
            }

            switch (type)
            {
                case "hash":
                    if (string.IsNullOrWhiteSpace(entry.Field))
                    {
                        throw new ArgumentException("Enter a field name for every row.");
                    }

                    await _session!.SetHashFieldAsync(key, entry.Field, entry.Value, token)
                        .ConfigureAwait(true);
                    break;
                case "list":
                    await _session!.AppendListValueAsync(key, entry.Value, token).ConfigureAwait(true);
                    break;
                case "set":
                    await _session!.AddSetValueAsync(key, entry.Value, token).ConfigureAwait(true);
                    break;
                case "zset":
                    if (!double.TryParse(
                            entry.Score,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var score))
                    {
                        throw new ArgumentException("Enter a valid sorted-set score for every row.");
                    }

                    await _session!.AddSortedSetValueAsync(key, entry.Value, score, token)
                        .ConfigureAwait(true);
                    break;
            }

            written++;
        }

        if (written == 0)
        {
            throw new ArgumentException("Enter at least one entry.");
        }
    }

    private void RequireField()
    {
        if (string.IsNullOrWhiteSpace(MutationField))
        {
            throw new ArgumentException("Enter a field name.");
        }
    }

    private void RequireNewKeyField()
    {
        if (string.IsNullOrWhiteSpace(NewKeyField))
        {
            throw new ArgumentException("Enter a field name for the new key.");
        }
    }

    private static bool IsAuthenticationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("authentication required", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void OnConnectionChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(Facts));
        OnPropertyChanged(nameof(ServerFactsText));
        OnPropertyChanged(nameof(SearchAvailable));
        OnPropertyChanged(nameof(Databases));
        OnPropertyChanged(nameof(SelectedDatabase));
        OnPropertyChanged(nameof(CanMutateSelectedKey));
        RaiseCommands();
    }

    /// <summary>
    /// Moves every countdown on by whatever the clock says has passed. The
    /// timer only asks; each row works out its own remaining time, so a late
    /// or missed tick shows the right figure rather than a drifting one.
    /// </summary>
    internal void TickExpiries()
    {
        foreach (var key in Keys)
        {
            if (key.IsExpiring)
            {
                key.Tick();
            }
        }

        if (SelectedKey?.IsExpiring == true)
        {
            OnPropertyChanged(nameof(SelectedKeyMetadata));
        }
    }

    private void OnSelectedSnapshotChanged()
    {
        OnPropertyChanged(nameof(SelectedKeyType));
        OnPropertyChanged(nameof(SelectedKeyMetadata));
        OnPropertyChanged(nameof(SelectedKeyLimitation));
        OnPropertyChanged(nameof(HasSelectedKeyLimitation));
        OnPropertyChanged(nameof(CanMutateSelectedKey));
        OnPropertyChanged(nameof(MutationForm));
        OnPropertyChanged(nameof(RemoveEntryLabel));
        SaveValueCommand.RaiseCanExecuteChanged();
        RemoveEntryCommand.RaiseCanExecuteChanged();
        SaveEntryCommand.RaiseCanExecuteChanged();
        SetExpiryCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        LoadMoreCommand.RaiseCanExecuteChanged();
        CreateKeyCommand.RaiseCanExecuteChanged();
        SaveValueCommand.RaiseCanExecuteChanged();
        DeleteKeyCommand.RaiseCanExecuteChanged();
        RemoveEntryCommand.RaiseCanExecuteChanged();
        SaveEntryCommand.RaiseCanExecuteChanged();
        SetExpiryCommand.RaiseCanExecuteChanged();
        SubscribeCommand.RaiseCanExecuteChanged();
        UnsubscribeCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
        RefreshIndexesCommand.RaiseCanExecuteChanged();
        SearchCommand.RaiseCanExecuteChanged();
    }
}
