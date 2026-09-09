using System.Collections.ObjectModel;
using System.ComponentModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record ScreenConnectionOption(
    ConnectionId Id,
    string Name,
    string Kind,
    bool IsAvailable,
    ConnectionKind? ConnectionKind = null)
{
    public string DisplayName => IsAvailable ? $"{Name} · {Kind}" : $"Missing · {Name}";
}

public sealed record ScreenFileProviderOption(
    FileProviderProfileId Id,
    string Name,
    string Kind,
    bool IsAvailable)
{
    public string DisplayName => IsAvailable ? $"{Name} · {Kind}" : $"Missing · {Name}";
}

public sealed record SavedScreenLayoutOption(
    LayoutId Id,
    string Name,
    bool IsAvailable,
    LayoutDefinition? Definition)
{
    public int SlotCount => Definition?.Slots.Count ?? 0;

    public string DisplayName => IsAvailable && Definition is { } layout
        ? $"{Name} · {layout.Grid.Columns}×{layout.Grid.Rows} · {SlotCount} panels"
        : $"Missing · {Name}";
}

public sealed record ScreenPanelKindOption(ScreenPanelKind Kind, string DisplayName);

public sealed class StartupCommandDeliveryFailurePolicyOption
{
    public static StartupCommandDeliveryFailurePolicyOption RetryWhileLive { get; } =
        new(
            StartupCommandDeliveryFailurePolicy.RetryWhileLive,
            "Retry while live");

    public static StartupCommandDeliveryFailurePolicyOption
        StopAfterFirstDeliveryFailure
    { get; } =
        new(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            "Stop after first delivery failure");

    public static IReadOnlyList<StartupCommandDeliveryFailurePolicyOption> All { get; } =
        Array.AsReadOnly(
        [
            RetryWhileLive,
            StopAfterFirstDeliveryFailure,
        ]);

    private StartupCommandDeliveryFailurePolicyOption(
        StartupCommandDeliveryFailurePolicy policy,
        string displayName)
    {
        Policy = policy;
        DisplayName = displayName;
    }

    public StartupCommandDeliveryFailurePolicy Policy { get; }

    public string DisplayName { get; }

    public static StartupCommandDeliveryFailurePolicyOption FromPolicy(
        StartupCommandDeliveryFailurePolicy policy) => policy switch
        {
            StartupCommandDeliveryFailurePolicy.RetryWhileLive => RetryWhileLive,
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure =>
                StopAfterFirstDeliveryFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
}

public sealed record SavedScreenEditorSaveRequest(
    ScreenDefinition Definition,
    long? ExpectedRevision);

public delegate ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
    SavedScreenPersistenceOperation(
        SavedScreenEditorSaveRequest request,
        CancellationToken cancellationToken);

public enum SavedScreenEditorCancelDisposition
{
    Close,
    ConfirmDiscard,
}

/// <summary>
/// Owns an isolated saved-screen draft. Layout changes reconcile panels by durable
/// slot identity, so an unchanged slot retains both its panel ID and unsaved edits.
/// </summary>
public sealed class SavedScreenEditorViewModel : ObservableObject, IDisposable
{
    private static readonly FileProviderProfileId BuiltInHomeId = BuiltInFileProviders.HomeId;
    private readonly ScreenDefinition _original;
    private readonly ObservableCollection<SavedScreenPanelEditorViewModel> _panels = [];
    private readonly ReadOnlyObservableCollection<SavedScreenPanelEditorViewModel> _readOnlyPanels;
    private readonly Dictionary<LayoutId, IReadOnlyList<SavedScreenPanelEditorViewModel>>
        _layoutDrafts = [];
    private readonly HashSet<SavedScreenPanelEditorViewModel> _subscribedPanels = [];
    private readonly SavedScreenAgentPolicyEditorViewModel _agentPolicy;
    private string _name;
    private string _description;
    private SavedScreenLayoutOption _selectedLayout;
    private DefinitionStoreError? _persistenceError;
    private bool _isDirty;
    private bool _isSaving;
    private bool _disposed;

    public static SavedScreenEditorViewModel CreateNew(
        string name,
        IReadOnlyList<LayoutDefinition> layouts,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<FileProviderProfile>? fileProviders = null,
        IReadOnlyList<AiProviderProfileDescriptor>? aiProviders = null)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(connections);
        var layout = layouts
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Create a layout before creating a saved screen.");
        var connectionId = connections
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => (ConnectionId?)candidate.Id)
            .FirstOrDefault();
        var panels = layout.Slots
            .Select(slot => new ScreenPanelDefinition(
                ScreenPanelId.New(),
                slot.Id,
                ScreenPanelKind.Terminal,
                "Terminal",
                connectionId,
                PanelStartupBehavior.None))
            .ToArray();
        var screen = new ScreenDefinition(
            ScreenId.New(),
            ScreenDefinition.CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(name) ? "Saved screen" : name.Trim(),
            "A reusable panel layout.",
            layout.Id,
            panels);
        return new SavedScreenEditorViewModel(
            screen,
            expectedRevision: null,
            connections,
            fileProviders ?? [],
            layouts,
            aiProviders);
    }

    public SavedScreenEditorViewModel(
        ScreenDefinition screen,
        long? expectedRevision,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<FileProviderProfile> fileProviders,
        IReadOnlyList<LayoutDefinition> layouts,
        IReadOnlyList<AiProviderProfileDescriptor>? aiProviders = null)
    {
        _original = screen ?? throw new ArgumentNullException(nameof(screen));
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(fileProviders);
        ArgumentNullException.ThrowIfNull(layouts);
        ExpectedRevision = expectedRevision;
        _name = screen.Name;
        _description = screen.Description ?? string.Empty;
        _agentPolicy = new SavedScreenAgentPolicyEditorViewModel(
            screen.AgentPolicyOverride,
            aiProviders);
        _agentPolicy.Changed += OnAgentPolicyChanged;
        _readOnlyPanels = new(_panels);

        LayoutOptions = BuildLayoutOptions(screen, layouts);
        _selectedLayout = LayoutOptions.Single(option => option.Id == screen.LayoutId);
        ConnectionOptions = BuildConnectionOptions(screen, connections);
        FileProviderOptions = BuildFileProviderOptions(screen, fileProviders);
        foreach (var panel in screen.Panels)
        {
            AddPanel(new SavedScreenPanelEditorViewModel(
                panel,
                ConnectionOptions,
                FileProviderOptions));
        }

        _layoutDrafts[_selectedLayout.Id] = [.. _panels];
    }

    public long? ExpectedRevision { get; }

    public bool IsNew => ExpectedRevision is null;

    public string EditorTitle => IsNew ? "Create saved screen" : "Edit saved screen";

    public string PrimaryActionLabel => IsNew ? "Create screen" : "Save screen";

    public bool CanDuplicate => !IsNew;

    public IReadOnlyList<SavedScreenLayoutOption> LayoutOptions { get; }

    public IReadOnlyList<ScreenConnectionOption> ConnectionOptions { get; }

    public IReadOnlyList<ScreenFileProviderOption> FileProviderOptions { get; }

    public ReadOnlyObservableCollection<SavedScreenPanelEditorViewModel> Panels => _readOnlyPanels;

    public SavedScreenAgentPolicyEditorViewModel AgentPolicy => _agentPolicy;

    public SavedScreenLayoutOption SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _layoutDrafts[_selectedLayout.Id] = [.. _panels];
            if (!SetProperty(ref _selectedLayout, value))
            {
                return;
            }

            RestoreLayoutDraft(value);

            Changed();
            OnPropertyChanged(nameof(LayoutSummary));
            OnPropertyChanged(nameof(HasMissingLayout));
        }
    }

    public string LayoutSummary => SelectedLayout.Definition is { } layout
        ? $"{layout.Grid.Columns} × {layout.Grid.Rows} grid · {layout.Slots.Count} panel slots"
        : "The selected layout is no longer available.";

    public bool HasMissingLayout => !SelectedLayout.IsAvailable;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Changed();
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
            {
                Changed();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DirtyStatus));
            }
        }
    }

    public bool IsSaving => _isSaving;

    public bool CanEdit => !IsSaving;

    public string DirtyStatus => IsSaving
        ? "Saving"
        : IsNew
        ? "Unsaved new screen"
        : IsDirty
            ? "Unsaved changes"
            : "Saved definition";

    public int MissingConnectionCount => _panels.Count(panel => panel.HasMissingConnection);

    public bool HasMissingConnections => MissingConnectionCount > 0;

    public int MissingDefinitionCount => _panels.Count(panel => panel.HasMissingDefinition);

    public bool HasMissingDefinitions => MissingDefinitionCount > 0;

    public int InvalidBrowserAddressCount =>
        _panels.Count(panel => panel.HasInvalidBrowserAddress);

    public bool HasInvalidBrowserAddresses => InvalidBrowserAddressCount > 0;

    public bool CanSave =>
        !IsSaving
        && !string.IsNullOrWhiteSpace(Name)
        && !HasMissingLayout
        && !HasMissingDefinitions
        && !HasInvalidBrowserAddresses
        && AgentPolicy.IsValid;

    public DefinitionStoreError? PersistenceError
    {
        get => _persistenceError;
        private set
        {
            if (SetProperty(ref _persistenceError, value))
            {
                OnPropertyChanged(nameof(HasPersistenceError));
                OnPropertyChanged(nameof(PersistenceErrorLabel));
            }
        }
    }

    public bool HasPersistenceError => PersistenceError is not null;

    public string PersistenceErrorLabel => PersistenceError?.Code switch
    {
        DefinitionStoreErrorCode.RevisionConflict => "Revision conflict",
        DefinitionStoreErrorCode.DependencyConflict => "Missing dependency",
        DefinitionStoreErrorCode.Cancelled => "Save cancelled",
        DefinitionStoreErrorCode.StorageUnavailable => "Storage unavailable",
        DefinitionStoreErrorCode.StorageFailure => "Storage failure",
        null => string.Empty,
        _ => "Save failed",
    };

    public IReadOnlyList<LauncherScreenPanelPreviewViewModel> PreviewPanels
    {
        get
        {
            if (SelectedLayout.Definition is not { } layout)
            {
                return [];
            }

            var slots = layout.Slots.ToDictionary(slot => slot.Id);
            return [.. _panels
                .Select((panel, index) => (panel, index))
                .Where(item => slots.ContainsKey(item.panel.SlotId))
                .Select(item =>
                {
                    var bounds = slots[item.panel.SlotId].Bounds;
                    return new LauncherScreenPanelPreviewViewModel(
                        layout.Grid.Columns,
                        layout.Grid.Rows,
                        bounds.Column,
                        bounds.Row,
                        bounds.ColumnSpan,
                        bounds.RowSpan,
                        item.index == 0);
                })];
        }
    }

    public SavedScreenEditorCancelDisposition RequestCancel() => IsNew || IsDirty
        ? SavedScreenEditorCancelDisposition.ConfirmDiscard
        : SavedScreenEditorCancelDisposition.Close;

    public SavedScreenEditorSaveRequest CreateSaveRequest()
    {
        return CreateSaveRequest(_original.Id, Name, ExpectedRevision);
    }

    public SavedScreenEditorSaveRequest CreateDuplicateRequest()
    {
        var duplicateName = string.IsNullOrWhiteSpace(Name)
            ? "Saved screen copy"
            : $"{Name.Trim()} copy";
        return CreateSaveRequest(ScreenId.New(), duplicateName, null);
    }

    public ValueTask<bool> SaveAsync(
        SavedScreenPersistenceOperation persist,
        CancellationToken cancellationToken) =>
        PersistAsync(CreateSaveRequest, persist, cancellationToken);

    public ValueTask<bool> DuplicateAsync(
        SavedScreenPersistenceOperation persist,
        CancellationToken cancellationToken) =>
        PersistAsync(CreateDuplicateRequest, persist, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var panel in _subscribedPanels)
        {
            panel.PropertyChanged -= OnPanelChanged;
        }

        _agentPolicy.Changed -= OnAgentPolicyChanged;
        _agentPolicy.Dispose();
        _disposed = true;
    }

    private SavedScreenEditorSaveRequest CreateSaveRequest(
        ScreenId id,
        string name,
        long? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Saved-screen name is required.");
        }

        if (SelectedLayout.Definition is not { } layout)
        {
            throw new ArgumentException("Choose an available layout before saving.");
        }

        if (HasInvalidBrowserAddresses)
        {
            throw new ArgumentException(
                "Enter a complete HTTP or HTTPS startup address for each browser panel, or leave the address empty.");
        }

        var missing = _panels.Where(panel => panel.HasMissingDefinition).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Repair {missing.Length} missing panel definition{(missing.Length == 1 ? string.Empty : "s")} before saving.");
        }

        var definition = new ScreenDefinition(
            id,
            _original.SchemaVersion,
            name.Trim(),
            Description,
            layout.Id,
            [.. _panels.Select(panel => panel.Build())],
            _original.Tags,
            AgentPolicy.Build());
        var validation = ScreenValidator.Validate(definition, layout);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(
                " ",
                validation.Issues.Select(issue => issue.Message).Distinct(StringComparer.Ordinal)));
        }

        return new SavedScreenEditorSaveRequest(definition, expectedRevision);
    }

    private async ValueTask<bool> PersistAsync(
        Func<SavedScreenEditorSaveRequest> createRequest,
        SavedScreenPersistenceOperation persist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persist);
        if (IsSaving)
        {
            return false;
        }

        var request = createRequest();
        PersistenceError = null;
        SetSaving(true);
        try
        {
            var result = await persist(request, cancellationToken);
            if (result.IsSuccess)
            {
                return true;
            }

            PersistenceError = result.Error ?? new DefinitionStoreError(
                DefinitionStoreErrorCode.StorageFailure,
                "The saved screen could not be persisted.");
            return false;
        }
        catch (OperationCanceledException)
        {
            PersistenceError = new DefinitionStoreError(
                DefinitionStoreErrorCode.Cancelled,
                "Saving was cancelled. The draft remains open.");
            return false;
        }
        finally
        {
            SetSaving(false);
        }
    }

    private void RestoreLayoutDraft(SavedScreenLayoutOption option)
    {
        if (_layoutDrafts.TryGetValue(option.Id, out var cached))
        {
            ReplacePanels(cached);
            return;
        }

        if (option.Definition is { } layout)
        {
            ReconcilePanels(layout);
        }
    }

    private void ReconcilePanels(LayoutDefinition layout)
    {
        var existingPanels = _panels.ToArray();
        var panelsBySlot = existingPanels.ToDictionary(panel => panel.SlotId);
        var reconciled = new List<SavedScreenPanelEditorViewModel>(layout.Slots.Count);
        foreach (var slot in layout.Slots)
        {
            if (panelsBySlot.Remove(slot.Id, out var existing))
            {
                reconciled.Add(existing);
                continue;
            }

            var connectionId = ConnectionOptions
                .FirstOrDefault(option => option.IsAvailable)
                ?.Id;
            reconciled.Add(new SavedScreenPanelEditorViewModel(
                new ScreenPanelDefinition(
                    ScreenPanelId.New(),
                    slot.Id,
                    ScreenPanelKind.Terminal,
                    "Terminal",
                    connectionId,
                    PanelStartupBehavior.None),
                ConnectionOptions,
                FileProviderOptions));
        }

        _layoutDrafts[layout.Id] = reconciled;
        ReplacePanels(reconciled);
    }

    private void ReplacePanels(IReadOnlyList<SavedScreenPanelEditorViewModel> panels)
    {
        _panels.Clear();
        foreach (var panel in panels)
        {
            AddPanel(panel);
        }

        OnPropertyChanged(nameof(Panels));
        OnPropertyChanged(nameof(PreviewPanels));
    }

    private void AddPanel(SavedScreenPanelEditorViewModel panel)
    {
        if (_subscribedPanels.Add(panel))
        {
            panel.PropertyChanged += OnPanelChanged;
        }

        _panels.Add(panel);
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        Changed();
    }

    private void OnAgentPolicyChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Changed();
    }

    private void Changed()
    {
        if (!IsSaving)
        {
            PersistenceError = null;
        }

        IsDirty = true;
        OnPropertyChanged(nameof(MissingConnectionCount));
        OnPropertyChanged(nameof(HasMissingConnections));
        OnPropertyChanged(nameof(MissingDefinitionCount));
        OnPropertyChanged(nameof(HasMissingDefinitions));
        OnPropertyChanged(nameof(InvalidBrowserAddressCount));
        OnPropertyChanged(nameof(HasInvalidBrowserAddresses));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(PreviewPanels));
    }

    private void SetSaving(bool value)
    {
        if (!SetProperty(ref _isSaving, value, nameof(IsSaving)))
        {
            return;
        }

        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(DirtyStatus));
    }

    private static IReadOnlyList<SavedScreenLayoutOption> BuildLayoutOptions(
        ScreenDefinition screen,
        IReadOnlyList<LayoutDefinition> layouts)
    {
        var options = layouts
            .OrderBy(layout => layout.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(layout => layout.Id.Value, StringComparer.Ordinal)
            .Select(layout => new SavedScreenLayoutOption(
                layout.Id,
                layout.Name,
                true,
                layout))
            .ToList();
        if (options.All(option => option.Id != screen.LayoutId))
        {
            options.Add(new SavedScreenLayoutOption(
                screen.LayoutId,
                screen.LayoutId.Value,
                false,
                null));
        }

        return options.AsReadOnly();
    }

    private static IReadOnlyList<ScreenConnectionOption> BuildConnectionOptions(
        ScreenDefinition screen,
        IReadOnlyList<ConnectionProfile> connections)
    {
        var options = connections
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .Select(connection => new ScreenConnectionOption(
                connection.Id,
                connection.Name,
                KindBadges.Connection(connection.ConnectionKind),
                true,
                connection.ConnectionKind))
            .ToList();
        foreach (var missingId in screen.Panels
            .Select(panel => panel.ConnectionId)
            .OfType<ConnectionId>()
            .Where(id => options.All(option => option.Id != id))
            .Distinct())
        {
            options.Add(new ScreenConnectionOption(
                missingId,
                missingId.Value,
                "Unavailable",
                false));
        }

        return options.AsReadOnly();
    }

    private static IReadOnlyList<ScreenFileProviderOption> BuildFileProviderOptions(
        ScreenDefinition screen,
        IReadOnlyList<FileProviderProfile> fileProviders)
    {
        var options = new List<ScreenFileProviderOption>
        {
            new(BuiltInHomeId, "Home", "LOCAL", true),
        };
        options.AddRange(fileProviders
            .Where(profile => profile.Id != BuiltInHomeId)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new ScreenFileProviderOption(
                profile.Id,
                profile.Name,
                KindBadges.FileProvider(profile.ProviderKind),
                true)));
        foreach (var missingId in screen.Panels
            .Select(panel => panel.FileProviderProfileId)
            .OfType<FileProviderProfileId>()
            .Where(id => options.All(option => option.Id != id))
            .Distinct())
        {
            options.Add(new ScreenFileProviderOption(
                missingId,
                missingId.Value,
                "Unavailable",
                false));
        }

        return options.AsReadOnly();
    }
}

public sealed class SavedScreenPanelEditorViewModel : ObservableObject
{
    private static readonly IReadOnlyList<ScreenPanelKindOption> SupportedKinds =
    [
        new(ScreenPanelKind.Terminal, "Terminal"),
        new(ScreenPanelKind.Browser, "Browser"),
        new(ScreenPanelKind.FileViewer, "File viewer"),
        new(ScreenPanelKind.Statistics, "Statistics"),
        new(ScreenPanelKind.ProcessMonitor, "Process monitor"),
        new(ScreenPanelKind.DatabaseViewer, "Database"),
        new(ScreenPanelKind.Docker, "Docker"),
        new(ScreenPanelKind.Git, "Git"),
    ];

    private readonly ScreenPanelDefinition _original;
    private ScreenPanelKindOption _selectedKind;
    private string _title;
    private ScreenConnectionOption? _selectedConnection;
    private ScreenFileProviderOption? _selectedFileProvider;
    private string _startupLocation;
    private string _startupCommands;
    private StartupCommandDeliveryFailurePolicyOption _selectedDeliveryFailurePolicy;

    public SavedScreenPanelEditorViewModel(
        ScreenPanelDefinition panel,
        IReadOnlyList<ScreenConnectionOption> connectionOptions,
        IReadOnlyList<ScreenFileProviderOption> fileProviderOptions)
    {
        _original = panel ?? throw new ArgumentNullException(nameof(panel));
        ConnectionOptions = connectionOptions ?? throw new ArgumentNullException(nameof(connectionOptions));
        FileProviderOptions = fileProviderOptions
            ?? throw new ArgumentNullException(nameof(fileProviderOptions));
        _selectedKind = KindOptions.Single(option => option.Kind == panel.Kind);
        _title = panel.Title ?? panel.Kind.ToString();
        _selectedConnection = SupportsConnection
            && panel.ConnectionId is { } connectionId
            ? ConnectionOptions.SingleOrDefault(option => option.Id == connectionId)
            : panel.Kind == ScreenPanelKind.Browser
                ? ConnectionOptions.FirstOrDefault(option =>
                    option.IsAvailable && option.ConnectionKind == ConnectionKind.Local)
                : null;
        FileProviderProfileId? fileProviderId = panel.Kind == ScreenPanelKind.FileViewer
            ? panel.FileProviderProfileId ?? BuiltInFileProviders.HomeId
            : null;
        _selectedFileProvider = fileProviderId is { } selectedFileProviderId
            ? FileProviderOptions.SingleOrDefault(option => option.Id == selectedFileProviderId)
            : null;
        var startup = panel.Startup ?? PanelStartupBehavior.None;
        _startupLocation = SupportsLocation ? startup.Location ?? string.Empty : string.Empty;
        _startupCommands = IsTerminal
            ? string.Join(Environment.NewLine, startup.Commands)
            : string.Empty;
        _selectedDeliveryFailurePolicy = StartupCommandDeliveryFailurePolicyOption.FromPolicy(
            IsTerminal
                ? startup.DeliveryFailurePolicy
                : StartupCommandDeliveryFailurePolicy.RetryWhileLive);
    }

    public ScreenPanelId Id => _original.Id;

    public LayoutSlotId SlotId => _original.SlotId;

    public IReadOnlyList<ScreenPanelKindOption> KindOptions => SupportedKinds;

    public ScreenPanelKindOption SelectedKind
    {
        get => _selectedKind;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedKind, value))
            {
                return;
            }

            NormalizeKindSpecificState();
            PublishKindState();
        }
    }

    public ScreenPanelKind Kind
    {
        get => SelectedKind.Kind;
        set => SelectedKind = KindOptions.Single(option => option.Kind == value);
    }

    public string PanelLabel => $"{KindBadges.Panel(Kind)} · {SlotId.Value}";

    public IReadOnlyList<ScreenConnectionOption> ConnectionOptions { get; }

    public IReadOnlyList<ScreenConnectionOption> ApplicableConnectionOptions =>
        Kind == ScreenPanelKind.Browser
            ? [.. ConnectionOptions
                .Where(option => option.ConnectionKind is ConnectionKind.Local or ConnectionKind.Ssh
                    || !option.IsAvailable && option.Id == _original.ConnectionId)]
            : ConnectionOptions;

    public IReadOnlyList<ScreenFileProviderOption> FileProviderOptions { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public ScreenConnectionOption? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            var compatible = SupportsConnection && IsApplicableConnection(value)
                ? value
                : null;
            if (SetProperty(ref _selectedConnection, compatible))
            {
                PublishReferenceState();
            }
        }
    }

    public ScreenFileProviderOption? SelectedFileProvider
    {
        get => _selectedFileProvider;
        set
        {
            var compatible = IsFileViewer ? value : null;
            if (SetProperty(ref _selectedFileProvider, compatible))
            {
                PublishReferenceState();
            }
        }
    }

    public string StartupLocation
    {
        get => _startupLocation;
        set
        {
            if (SetProperty(
                    ref _startupLocation,
                    SupportsLocation ? value : string.Empty))
            {
                OnPropertyChanged(nameof(HasInvalidBrowserAddress));
            }
        }
    }

    public string StartupCommands
    {
        get => _startupCommands;
        set => SetProperty(ref _startupCommands, IsTerminal ? value : string.Empty);
    }

    public IReadOnlyList<StartupCommandDeliveryFailurePolicyOption>
        DeliveryFailurePolicyOptions => StartupCommandDeliveryFailurePolicyOption.All;

    public StartupCommandDeliveryFailurePolicyOption SelectedDeliveryFailurePolicy
    {
        get => _selectedDeliveryFailurePolicy;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetProperty(
                ref _selectedDeliveryFailurePolicy,
                IsTerminal
                    ? value
                    : StartupCommandDeliveryFailurePolicyOption.RetryWhileLive);
        }
    }

    public bool HasMissingConnection => SupportsConnection
        && (SelectedConnection?.IsAvailable != true
            || !IsApplicableConnection(SelectedConnection));

    public bool HasMissingFileProvider => IsFileViewer && SelectedFileProvider?.IsAvailable != true;

    public bool HasMissingDefinition => HasMissingConnection || HasMissingFileProvider;

    public bool HasInvalidBrowserAddress =>
        Kind == ScreenPanelKind.Browser
        && !string.IsNullOrWhiteSpace(StartupLocation)
        && !BrowserAddress.TryParse(StartupLocation, out _);

    public bool IsTerminal => Kind == ScreenPanelKind.Terminal;

    public bool IsFileViewer => Kind == ScreenPanelKind.FileViewer;

    public bool SupportsConnection => Kind is ScreenPanelKind.Terminal
        or ScreenPanelKind.Browser
        or ScreenPanelKind.Docker
        or ScreenPanelKind.Git;

    public bool SupportsLocation => Kind is ScreenPanelKind.Terminal
        or ScreenPanelKind.Browser
        or ScreenPanelKind.FileViewer
        or ScreenPanelKind.DatabaseViewer
        or ScreenPanelKind.Git;

    public ScreenPanelDefinition Build()
    {
        var commands = IsTerminal
            ? StartupCommands
                .Split('\n', StringSplitOptions.None)
                .Select(command => command.TrimEnd('\r'))
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .ToArray()
            : [];
        return new ScreenPanelDefinition(
            Id,
            SlotId,
            Kind,
            string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
            SupportsConnection ? SelectedConnection?.Id : null,
            new PanelStartupBehavior(
                SupportsLocation ? StartupLocation : null,
                commands,
                IsTerminal
                    ? SelectedDeliveryFailurePolicy.Policy
                    : StartupCommandDeliveryFailurePolicy.RetryWhileLive),
            IsFileViewer ? SelectedFileProvider?.Id : null);
    }

    private void NormalizeKindSpecificState()
    {
        if (SupportsConnection)
        {
            if (_selectedConnection?.IsAvailable != true
                || !IsApplicableConnection(_selectedConnection))
            {
                _selectedConnection = ApplicableConnectionOptions.FirstOrDefault(option =>
                    option.IsAvailable);
                OnPropertyChanged(nameof(SelectedConnection));
            }
        }
        else if (_selectedConnection is not null)
        {
            _selectedConnection = null;
            OnPropertyChanged(nameof(SelectedConnection));
        }

        if (IsFileViewer)
        {
            if (_selectedFileProvider?.IsAvailable != true)
            {
                _selectedFileProvider = FileProviderOptions.FirstOrDefault(option =>
                    option.IsAvailable && option.Id == BuiltInFileProviders.HomeId)
                    ?? FileProviderOptions.FirstOrDefault(option => option.IsAvailable);
                OnPropertyChanged(nameof(SelectedFileProvider));
            }
        }
        else if (_selectedFileProvider is not null)
        {
            _selectedFileProvider = null;
            OnPropertyChanged(nameof(SelectedFileProvider));
        }

        if (!IsTerminal && _startupCommands.Length > 0)
        {
            _startupCommands = string.Empty;
            OnPropertyChanged(nameof(StartupCommands));
        }

        if (!IsTerminal
            && _selectedDeliveryFailurePolicy.Policy
                != StartupCommandDeliveryFailurePolicy.RetryWhileLive)
        {
            _selectedDeliveryFailurePolicy =
                StartupCommandDeliveryFailurePolicyOption.RetryWhileLive;
            OnPropertyChanged(nameof(SelectedDeliveryFailurePolicy));
        }

        if (!SupportsLocation && _startupLocation.Length > 0)
        {
            _startupLocation = string.Empty;
            OnPropertyChanged(nameof(StartupLocation));
        }
    }

    private void PublishKindState()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(PanelLabel));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(IsFileViewer));
        OnPropertyChanged(nameof(SupportsConnection));
        OnPropertyChanged(nameof(ApplicableConnectionOptions));
        OnPropertyChanged(nameof(SupportsLocation));
        OnPropertyChanged(nameof(HasInvalidBrowserAddress));
        PublishReferenceState();
    }

    private void PublishReferenceState()
    {
        OnPropertyChanged(nameof(HasMissingConnection));
        OnPropertyChanged(nameof(HasMissingFileProvider));
        OnPropertyChanged(nameof(HasMissingDefinition));
    }

    private bool IsApplicableConnection(ScreenConnectionOption? option) =>
        option is null
        || Kind is not (ScreenPanelKind.Browser or ScreenPanelKind.Docker or ScreenPanelKind.Git)
        || option.ConnectionKind is ConnectionKind.Local or ConnectionKind.Ssh
        || !option.IsAvailable && option.Id == _original.ConnectionId;
}
