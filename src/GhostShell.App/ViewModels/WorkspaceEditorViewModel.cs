using System.Collections.ObjectModel;
using System.ComponentModel;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Owns an isolated edit of a durable workspace definition. The editor keeps missing
/// references as explicit options, and only emits a save request after the complete
/// ordered definition and every workspace-only tab validate together.
/// </summary>
public sealed class WorkspaceEditorViewModel : ObservableObject, IDisposable
{
    private static readonly FileProviderProfileId BuiltInHomeId = new("builtin.files.home");
    private readonly WorkspaceDefinition _original;
    private readonly IReadOnlyDictionary<ScreenId, ScreenDefinition> _screens;
    private readonly IReadOnlyList<AiProviderProfileDescriptor>? _aiProviders;
    private IReadOnlyList<NetworkConnectionProfile> _networkConnections;
    private ApplicationNetworkSettings _applicationNetworkSettings;
    private readonly ObservableCollection<WorkspaceEntryEditorViewModel> _entries = [];
    private readonly ReadOnlyObservableCollection<WorkspaceEntryEditorViewModel> _readOnlyEntries;
    private readonly ObservableCollection<WorkspaceIsolationMountEditorViewModel> _isolationMounts = [];
    private readonly ReadOnlyObservableCollection<WorkspaceIsolationMountEditorViewModel>
        _readOnlyIsolationMounts;
    private string _name;
    private string _description;
    private string _accent;
    private string _color;
    private string _icon;
    private bool _autoSave;
    private bool _isIsolated;
    private bool _runAgentInIsolation;
    private bool _overridesNetworkSettings;
    private string _isolationImageReference;
    private readonly string _initialIsolationImageReference;
    private WorkspaceTerminalMultiplexingOption _selectedTerminalMultiplexing;
    private WorkspaceBrowserProfileOption _selectedBrowserProfile;
    private string _iconSearch = string.Empty;
    private bool _showAllIcons;
    private bool _isDirty;
    private IReadOnlyList<DefinitionValidationIssue> _validationIssues = [];
    private string? _lastOperationError;
    private bool _disposed;
    private readonly bool _isIsolationAvailable;
    private readonly string? _isolationRuntimeDisplayName;

    public static WorkspaceEditorViewModel CreateNew(
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<ScreenDefinition> screens,
        IReadOnlyList<LayoutDefinition> layouts,
        IReadOnlyList<FileProviderProfile>? fileProviders = null,
        IReadOnlyList<AiProviderProfileDescriptor>? aiProviders = null,
        string name = "New workspace")
    {
        var workspace = new WorkspaceDefinition(
            WorkspaceId.New(),
            WorkspaceDefinition.CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(name) ? "New workspace" : name.Trim(),
            null,
            null,
            []);
        return new WorkspaceEditorViewModel(
            workspace,
            expectedRevision: null,
            connections,
            screens,
            layouts,
            fileProviders ?? [],
            aiProviders);
    }

    public WorkspaceEditorViewModel(
        WorkspaceDefinition workspace,
        long? expectedRevision,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<ScreenDefinition> screens,
        IReadOnlyList<LayoutDefinition> layouts)
        : this(workspace, expectedRevision, connections, screens, layouts, [], null)
    {
    }

    public WorkspaceEditorViewModel(
        WorkspaceDefinition workspace,
        long? expectedRevision,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<ScreenDefinition> screens,
        IReadOnlyList<LayoutDefinition> layouts,
        IReadOnlyList<FileProviderProfile> fileProviders,
        IReadOnlyList<AiProviderProfileDescriptor>? aiProviders = null,
        bool isIsolationAvailable = true,
        string? isolationRuntimeDisplayName = null,
        string? effectiveIsolationImageReference = null,
        string defaultIsolationImageReference = WorkspaceIsolationImages.Default,
        IReadOnlyList<NetworkConnectionProfile>? networkConnections = null,
        ApplicationNetworkSettings? applicationNetworkSettings = null)
    {
        _original = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _isIsolationAvailable = isIsolationAvailable;
        _isolationRuntimeDisplayName = string.IsNullOrWhiteSpace(isolationRuntimeDisplayName)
            ? null
            : isolationRuntimeDisplayName.Trim();
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(screens);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(fileProviders);
        ExpectedRevision = expectedRevision;
        _name = workspace.Name;
        _description = workspace.Description ?? string.Empty;
        _accent = workspace.Accent ?? string.Empty;
        _color = workspace.Color ?? string.Empty;
        _icon = workspace.Icon;
        _autoSave = workspace.AutoSave;
        _isIsolated = workspace.IsIsolated;
        _runAgentInIsolation = workspace.RunAgentInIsolation;
        _overridesNetworkSettings = workspace.NetworkOverride is not null;
        _networkConnections = networkConnections ?? [];
        _applicationNetworkSettings = applicationNetworkSettings
            ?? ApplicationNetworkSettings.Default;
        DefaultIsolationImageReference = string.IsNullOrWhiteSpace(defaultIsolationImageReference)
            ? throw new ArgumentException(
                "A default isolation image reference is required.",
                nameof(defaultIsolationImageReference))
            : WorkspaceIsolationImages.ForDisplay(defaultIsolationImageReference);
        var selectedImageReference = string.IsNullOrWhiteSpace(effectiveIsolationImageReference)
            ? workspace.IsolationImageReference ?? DefaultIsolationImageReference
            : effectiveIsolationImageReference.Trim();
        _isolationImageReference = WorkspaceIsolationImages.ForDisplay(selectedImageReference);
        _initialIsolationImageReference = _isolationImageReference;
        _aiProviders = aiProviders;
        AgentPolicy = new SavedScreenAgentPolicyEditorViewModel(
            workspace.AgentPolicyOverride,
            AgentProvidersFor(workspace.RunAgentInIsolation));
        AgentPolicy.Changed += OnAgentPolicyChanged;
        NetworkPolicy = new NetworkPolicyEditorViewModel(
            _networkConnections,
            NetworkPolicyResolver.Resolve(
                _applicationNetworkSettings,
                workspace,
                _networkConnections));
        NetworkPolicy.Changed += OnNetworkPolicyChanged;
        TerminalMultiplexingOptions =
        [
            new(null, "Use application setting"),
            new(TerminalMultiplexingMode.Disabled, "Off for this workspace"),
            new(TerminalMultiplexingMode.Automatic, "tmux with Screen fallback"),
        ];
        _selectedTerminalMultiplexing = TerminalMultiplexingOptions.Single(option =>
            option.Mode == workspace.TerminalMultiplexingOverride);
        BrowserProfileOptions =
        [
            new(null, "Use application setting"),
            new(WorkspaceBrowserProfileMode.Shared, "Share the global profile"),
            new(WorkspaceBrowserProfileMode.Isolated, "Isolate this workspace"),
        ];
        _selectedBrowserProfile = BrowserProfileOptions.Single(option =>
            option.Mode == workspace.BrowserProfileOverride);
        _screens = screens.ToDictionary(screen => screen.Id);
        _readOnlyEntries = new(_entries);
        _readOnlyIsolationMounts = new(_isolationMounts);

        ConnectionOptions = BuildConnectionOptions(workspace, connections, screens);
        LayoutOptions = BuildLayoutOptions(workspace, layouts, screens);
        ScreenOptions = BuildScreenOptions(workspace, screens, LayoutOptions);
        FileProviderOptions = BuildFileProviderOptions(workspace, screens, fileProviders);
        RestoreEntries();
        RestoreIsolationMounts();
        RefreshIconChoices();
        RefreshChoiceSelection();
        PublishState();
    }

    public WorkspaceId Id => _original.Id;

    public int SchemaVersion => _original.SchemaVersion;

    public long? ExpectedRevision { get; }

    public bool IsNew => ExpectedRevision is null;

    public IReadOnlyList<WorkspaceIconOption> IconOptions => WorkspaceIcons.All;

    /// <summary>
    /// The catalog size as its own property, for the same reason as
    /// <see cref="LayoutDesignerViewModel.PanelCount"/>: the catalog is an array,
    /// so a <c>Count</c> binding over it resolves to nothing.
    /// </summary>
    public int IconCount => WorkspaceIcons.All.Count;

    /// <summary>The icon grid's own tiles, filtered by <see cref="IconSearch"/>.</summary>
    public ObservableCollection<WorkspaceIconChoiceViewModel> IconChoices { get; } = [];

    /// <summary>
    /// The identity-colour presets. Free choice stays available through the
    /// custom picker, so this is a shortcut rather than a restriction.
    /// </summary>
    public IReadOnlyList<WorkspaceAccentChoiceViewModel> ColorChoices { get; } =
        [.. WorkspaceAccents.All.Select(option => new WorkspaceAccentChoiceViewModel(option))];

    /// <summary>
    /// The accent presets. Deliberately a separate row from
    /// <see cref="ColorChoices"/>: the colour marks the workspace, the accent
    /// retints the shell, and one is not the other.
    /// </summary>
    public IReadOnlyList<WorkspaceAccentChoiceViewModel> AccentChoices { get; } =
        [.. WorkspaceAccents.All.Select(option => new WorkspaceAccentChoiceViewModel(option))];

    public IReadOnlyList<WorkspaceTerminalMultiplexingOption> TerminalMultiplexingOptions { get; }

    public IReadOnlyList<WorkspaceBrowserProfileOption> BrowserProfileOptions { get; }

    public SavedScreenAgentPolicyEditorViewModel AgentPolicy { get; private set; }

    public NetworkPolicyEditorViewModel NetworkPolicy { get; private set; }

    public bool OverridesNetworkSettings
    {
        get => _overridesNetworkSettings;
        set
        {
            if (SetProperty(ref _overridesNetworkSettings, value))
            {
                PublishNetworkMode();
                Changed();
            }
        }
    }

    /// <summary>The same choice from the other segment of the picker.</summary>
    public bool FollowsApplicationNetwork
    {
        get => !_overridesNetworkSettings;
        set => OverridesNetworkSettings = !value;
    }

    public string InheritedNetworkSummary =>
        NetworkPolicySummary(_applicationNetworkSettings.Policy);

    /// <summary>
    /// What the mode currently means for this workspace, so the row that
    /// chooses it also says what was chosen.
    /// </summary>
    public string NetworkModeSummary => OverridesNetworkSettings
        ? "This workspace keeps its own connection list, active route, and kill switch."
        : InheritedNetworkSummary;

    public string DefaultIsolationImageReference { get; }

    public string IsolationImageDescription =>
        $"Bootable OCI image used by this workspace environment. It must provide /sbin/init. Default on this host: {DefaultIsolationImageReference}. Changing it recreates the environment and removes installed packages and guest-only files.";

    public WorkspaceTerminalMultiplexingOption SelectedTerminalMultiplexing
    {
        get => _selectedTerminalMultiplexing;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedTerminalMultiplexing, value))
            {
                Changed();
            }
        }
    }

    public WorkspaceBrowserProfileOption SelectedBrowserProfile
    {
        get => _selectedBrowserProfile;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedBrowserProfile, value))
            {
                Changed();
            }
        }
    }

    /// <summary>
    /// Filters the icon grid. The catalog is large enough that scanning it is
    /// slower than naming what you want.
    /// </summary>
    public string IconSearch
    {
        get => _iconSearch;
        set
        {
            if (SetProperty(ref _iconSearch, value))
            {
                RefreshIconChoices();
            }
        }
    }

    /// <summary>
    /// Whether the grid shows the whole catalog. Off, it shows the common
    /// icons and the workspace's own — so the icon you already chose is never
    /// missing from the row that claims to show your choice.
    /// </summary>
    public bool ShowAllIcons
    {
        get => _showAllIcons;
        set
        {
            if (SetProperty(ref _showAllIcons, value))
            {
                RefreshIconChoices();
            }
        }
    }

    public bool HasNoMatchingIcons => IconChoices.Count == 0;

    /// <summary>
    /// The line under the icon grid. A search that found nothing has to say so
    /// where the icons would have been — the alternative is an empty row and an
    /// invitation to browse a set that is plainly not being shown.
    /// </summary>
    public string IconHint => HasNoMatchingIcons
        ? "No icon matches that search."
        : _showAllIcons || !string.IsNullOrWhiteSpace(_iconSearch)
            ? "Pick one, or narrow the search."
            : "Search to reach the rest of the set.";

    private void RefreshIconChoices()
    {
        var matches = string.IsNullOrWhiteSpace(_iconSearch) && !_showAllIcons
            ? CommonIconsIncludingCurrent()
            : WorkspaceIcons.Search(_iconSearch);
        IconChoices.Clear();
        foreach (var option in matches)
        {
            IconChoices.Add(new WorkspaceIconChoiceViewModel(option)
            {
                IsSelected = string.Equals(option.Id, _icon, StringComparison.Ordinal),
            });
        }

        OnPropertyChanged(nameof(HasNoMatchingIcons));
        OnPropertyChanged(nameof(IconHint));
    }

    private IReadOnlyList<WorkspaceIconOption> CommonIconsIncludingCurrent()
    {
        var current = WorkspaceIcons.OptionFor(_icon);
        return WorkspaceIcons.Common.Any(option =>
            string.Equals(option.Id, current.Id, StringComparison.Ordinal))
            ? WorkspaceIcons.Common
            : [current, .. WorkspaceIcons.Common];
    }

    private void RefreshChoiceSelection()
    {
        foreach (var choice in IconChoices)
        {
            choice.IsSelected = string.Equals(choice.Id, _icon, StringComparison.Ordinal);
        }

        // Against the effective colour, not the stored one: the tile is already
        // painted with the fallback, and a row of swatches with none of them
        // marked would say the tile's colour came from nowhere.
        foreach (var choice in ColorChoices)
        {
            choice.IsSelected = string.Equals(
                choice.Hex,
                EffectiveColor,
                StringComparison.OrdinalIgnoreCase);
        }

        foreach (var choice in AccentChoices)
        {
            choice.IsSelected = string.Equals(choice.Hex, _accent, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The other workspaces, for the editor's rail. Supplied by the host rather
    /// than read here, because the editor owns one isolated snapshot and the
    /// catalog is the host's to know.
    /// </summary>
    public ObservableCollection<WorkspaceRailItemViewModel> Peers { get; } = [];

    public int PeerCount => Peers.Count;

    public void SetPeers(IReadOnlyList<WorkspaceDefinition> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        Peers.Clear();
        foreach (var workspace in workspaces.OrderBy(
            item => item.Name,
            StringComparer.OrdinalIgnoreCase))
        {
            Peers.Add(WorkspaceRailItemViewModel.From(workspace, workspace.Id == Id));
        }

        // A workspace being created is not in the catalog yet, so the rail would
        // show every workspace except the one on screen.
        if (Peers.All(peer => peer.Id != Id))
        {
            Peers.Add(new WorkspaceRailItemViewModel(
                Id,
                Name,
                "New",
                EffectiveColor,
                TileSymbol,
                IsCurrent: true));
        }

        OnPropertyChanged(nameof(Peers));
        OnPropertyChanged(nameof(PeerCount));
    }

    public IReadOnlyList<ScreenConnectionOption> ConnectionOptions { get; }

    public IReadOnlyList<WorkspaceScreenOption> ScreenOptions { get; }

    public IReadOnlyList<WorkspaceLayoutOption> LayoutOptions { get; }

    public IReadOnlyList<ScreenFileProviderOption> FileProviderOptions { get; }

    /// <summary>The durable launcher and keyboard traversal order.</summary>
    public ReadOnlyObservableCollection<WorkspaceEntryEditorViewModel> Entries => _readOnlyEntries;

    public ReadOnlyObservableCollection<WorkspaceIsolationMountEditorViewModel> IsolationMounts =>
        _readOnlyIsolationMounts;

    public int IsolationMountCount => _isolationMounts.Count;

    public bool HasNoIsolationMounts => _isolationMounts.Count == 0;

    public bool CanAddIsolationMount =>
        _isolationMounts.Count < WorkspaceDefinition.MaximumIsolationMountCount;

    public bool CanRecreateIsolationEnvironment =>
        IsIsolated && !IsNew && IsIsolationAvailable && !IsDirty;

    public bool IsIsolationAvailable => _isIsolationAvailable;

    public bool CanToggleIsolation =>
        IsIsolationAvailable || IsIsolated;

    public bool IsIsolationUnavailable => !IsIsolationAvailable && !IsIsolated;

    public bool CanInstallIsolationRuntime =>
        IsIsolationUnavailable && _isolationRuntimeDisplayName is not null;

    public string IsolationRuntimeRequirementLabel =>
        _isolationRuntimeDisplayName is null
            ? "Workspace isolation unavailable"
            : $"Install {_isolationRuntimeDisplayName} to enable isolation";

    public string IsolationRuntimeRequirementDescription =>
        _isolationRuntimeDisplayName is null
            ? "No workspace isolation runtime is available for this platform."
            : $"GhostSHELL uses {_isolationRuntimeDisplayName} to create this workspace's persistent isolated environment. Install and start it, then restart GhostSHELL.";

    public string InstallIsolationRuntimeLabel =>
        _isolationRuntimeDisplayName is null
            ? "Install isolation runtime"
            : $"Install {_isolationRuntimeDisplayName}\u2026";

    public string InstallIsolationRuntimeAccessibleName =>
        _isolationRuntimeDisplayName is null
            ? "Install workspace isolation runtime"
            : $"Install {_isolationRuntimeDisplayName} runtime";

    public IReadOnlyList<WorkspaceEntryEditorViewModel> ConnectionEntries =>
        [.. _entries.Where(entry => entry.IsConnection)];

    public IReadOnlyList<WorkspaceEntryEditorViewModel> SavedScreenEntries =>
        [.. _entries.Where(entry => entry.IsSavedScreen)];

    public IReadOnlyList<WorkspaceEntryEditorViewModel> WorkspaceTabEntries =>
        [.. _entries.Where(entry => entry.IsWorkspaceTab)];

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

    /// <summary>
    /// The colour this workspace is recognised by. Empty follows the accent,
    /// and then the shell's own colour, so a workspace always has a mark.
    /// </summary>
    public string Color
    {
        get => _color;
        set
        {
            if (SetProperty(ref _color, value))
            {
                RefreshChoiceSelection();
                OnPropertyChanged(nameof(EffectiveColor));
                Changed();
            }
        }
    }

    /// <summary>What the tile actually paints — never empty. See <see cref="Color"/>.</summary>
    public string EffectiveColor => WorkspaceTints.Of(_color, _accent);

    /// <summary>The icon the header tile draws, resolved from the stored identifier.</summary>
    public FluentIcons.Common.Symbol TileSymbol => WorkspaceIcons.SymbolFor(_icon);

    /// <summary>Whether this workspace retints the shell rather than following it.</summary>
    public bool HasAccent => !string.IsNullOrWhiteSpace(_accent);

    public string AccentSummary => HasAccent
        ? "This workspace retints the shell while it is open."
        : "Following the application accent.";

    public int TabCount => _entries.Count(entry => entry.IsWorkspaceTab);

    public int EntryCount => _entries.Count;

    public bool HasNoEntries => _entries.Count == 0;

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

    /// <summary>An empty value follows the effective application accent.</summary>
    public string Accent
    {
        get => _accent;
        set
        {
            if (SetProperty(ref _accent, value))
            {
                RefreshChoiceSelection();
                OnPropertyChanged(nameof(HasAccent));
                OnPropertyChanged(nameof(AccentSummary));
                OnPropertyChanged(nameof(EffectiveColor));
                Changed();
            }
        }
    }

    public string Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                RefreshChoiceSelection();
                OnPropertyChanged(nameof(TileSymbol));
                Changed();
            }
        }
    }

    /// <summary>
    /// When on, tab and panel changes made while working inside the open
    /// workspace are written back to this definition automatically.
    /// </summary>
    public bool AutoSave
    {
        get => _autoSave;
        set
        {
            if (SetProperty(ref _autoSave, value))
            {
                Changed();
            }
        }
    }

    /// <summary>
    /// Whether supported workspace processes run inside this workspace's one
    /// persistent platform isolation environment.
    /// </summary>
    public bool IsIsolated
    {
        get => _isIsolated;
        set
        {
            if (!CanToggleIsolation)
            {
                return;
            }

            if (SetProperty(ref _isIsolated, value))
            {
                if (!value && _runAgentInIsolation)
                {
                    var policy = AgentPolicy.Build();
                    _runAgentInIsolation = false;
                    OnPropertyChanged(nameof(RunAgentInIsolation));
                    ReplaceAgentPolicy(policy);
                }

                OnPropertyChanged(nameof(CanToggleIsolation));
                OnPropertyChanged(nameof(IsIsolationUnavailable));
                OnPropertyChanged(nameof(CanInstallIsolationRuntime));
                OnPropertyChanged(nameof(CanRecreateIsolationEnvironment));
                Changed();
            }
        }
    }

    public bool RunAgentInIsolation
    {
        get => _runAgentInIsolation;
        set
        {
            var normalized = value && IsIsolated;
            if (SetProperty(ref _runAgentInIsolation, normalized))
            {
                ReplaceAgentPolicy(AgentPolicy.Build());
                Changed();
            }
        }
    }

    /// <summary>
    /// The concrete OCI image used by this workspace environment.
    /// </summary>
    public string IsolationImageReference
    {
        get => _isolationImageReference;
        set
        {
            if (SetProperty(ref _isolationImageReference, value))
            {
                Changed();
            }
        }
    }

    public void AddIsolationMount()
    {
        if (!CanAddIsolationMount)
        {
            _ = Reject(
                $"A workspace cannot define more than {WorkspaceDefinition.MaximumIsolationMountCount} host mounts.");
            return;
        }

        var mount = new WorkspaceIsolationMountEditorViewModel(
            string.Empty,
            NextGuestMountPath(),
            isReadOnly: true);
        AddIsolationMount(mount);
        Changed();
    }

    public void RemoveIsolationMount(WorkspaceIsolationMountEditorViewModel mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (!_isolationMounts.Remove(mount))
        {
            return;
        }

        mount.PropertyChanged -= OnIsolationMountChanged;
        PublishIsolationMountState();
        Changed();
    }

    public bool IsDirty => _isDirty;

    public string DirtyStatus => IsNew
        ? "Unsaved new workspace"
        : IsDirty
            ? "Unsaved changes"
            : "Saved definition";

    public IReadOnlyList<DefinitionValidationIssue> ValidationIssues => _validationIssues;

    public bool IsValid => ValidationIssues.Count == 0;

    public bool CanSave => (IsNew || IsDirty) && IsValid;

    public string ValidationSummary => IsValid
        ? "Workspace is valid."
        : string.Join(" ", ValidationIssues.Select(issue => issue.Message).Distinct(StringComparer.Ordinal));

    public int MissingReferenceCount => _entries.Count(entry => entry.HasMissingReference);

    public bool HasMissingReferences => MissingReferenceCount > 0;

    public string? LastOperationError
    {
        get => _lastOperationError;
        private set
        {
            if (SetProperty(ref _lastOperationError, value))
            {
                OnPropertyChanged(nameof(HasOperationError));
            }
        }
    }

    public bool HasOperationError => LastOperationError is not null;

    public WorkspaceEditorOperationResult AddConnection(
        ConnectionId connectionId,
        string? alias = null)
    {
        var option = ConnectionOptions.SingleOrDefault(candidate =>
            candidate.Id == connectionId && candidate.IsAvailable);
        if (option is null)
        {
            return Reject($"Connection '{connectionId}' is not available.");
        }

        return AddEntry(new WorkspaceEntry.ConnectionReference(
            WorkspaceEntryId.New(),
            option.Id,
            alias));
    }

    public WorkspaceEditorOperationResult AddSavedScreen(
        ScreenId screenId,
        string? alias = null)
    {
        var option = ScreenOptions.SingleOrDefault(candidate =>
            candidate.Id == screenId && candidate.IsAvailable);
        if (option is null)
        {
            return Reject($"Saved screen '{screenId}' is not available.");
        }

        return AddEntry(new WorkspaceEntry.ScreenReference(
            WorkspaceEntryId.New(),
            option.Id,
            alias));
    }

    public WorkspaceEditorOperationResult AddWorkspaceTabFromScreen(
        ScreenId screenId,
        string? name = null)
    {
        if (!_screens.TryGetValue(screenId, out var screen))
        {
            return Reject($"Saved screen '{screenId}' is not available.");
        }

        var tab = new WorkspaceEntry.Tab(
            WorkspaceEntryId.New(),
            string.IsNullOrWhiteSpace(name) ? screen.Name : name.Trim(),
            screen.LayoutId,
            screen.Panels);
        return AddEntry(tab);
    }

    public WorkspaceEditorOperationResult AddWorkspaceTab(
        LayoutId layoutId,
        string name = "New tab")
    {
        var layout = LayoutOptions.SingleOrDefault(option =>
            option.Id == layoutId && option.IsAvailable)?.Definition;
        if (layout is null)
        {
            return Reject($"Layout '{layoutId}' is not available.");
        }

        var connectionId = ConnectionOptions.FirstOrDefault(option => option.IsAvailable)?.Id;
        var panels = layout.Slots
            .Select(slot => new ScreenPanelDefinition(
                ScreenPanelId.New(),
                slot.Id,
                ScreenPanelKind.Terminal,
                "Terminal",
                connectionId,
                PanelStartupBehavior.None))
            .ToArray();
        return AddEntry(new WorkspaceEntry.Tab(
            WorkspaceEntryId.New(),
            string.IsNullOrWhiteSpace(name) ? "New tab" : name.Trim(),
            layout.Id,
            panels));
    }

    public WorkspaceEditorOperationResult RemoveEntry(WorkspaceEntryId entryId)
    {
        var entry = _entries.SingleOrDefault(item => item.Id == entryId);
        if (entry is null)
        {
            return Reject($"Workspace entry '{entryId}' does not exist.");
        }

        entry.PropertyChanged -= OnEntryChanged;
        _entries.Remove(entry);
        entry.Dispose();
        LastOperationError = null;
        Changed(entriesChanged: true);
        return WorkspaceEditorOperationResult.Applied(entryId);
    }

    public WorkspaceEditorOperationResult MoveEntry(
        WorkspaceEntryId entryId,
        int destinationIndex)
    {
        var sourceIndex = _entries
            .Select((entry, index) => (entry, index))
            .Where(item => item.entry.Id == entryId)
            .Select(item => item.index)
            .SingleOrDefault(-1);
        if (sourceIndex < 0)
        {
            return Reject($"Workspace entry '{entryId}' does not exist.");
        }

        if (destinationIndex < 0 || destinationIndex >= _entries.Count)
        {
            return Reject("The destination is outside the workspace entry order.");
        }

        LastOperationError = null;
        if (sourceIndex != destinationIndex)
        {
            _entries.Move(sourceIndex, destinationIndex);
            Changed(entriesChanged: true);
        }

        return WorkspaceEditorOperationResult.Applied(entryId);
    }

    public WorkspaceEditorOperationResult MoveEntryEarlier(WorkspaceEntryId entryId)
    {
        var index = IndexOf(entryId);
        return index <= 0
            ? Reject("The entry is already first or is no longer available.")
            : MoveEntry(entryId, index - 1);
    }

    public WorkspaceEditorOperationResult MoveEntryLater(WorkspaceEntryId entryId)
    {
        var index = IndexOf(entryId);
        return index < 0 || index >= _entries.Count - 1
            ? Reject("The entry is already last or is no longer available.")
            : MoveEntry(entryId, index + 1);
    }

    public void Reset()
    {
        var nameChanged = !StringComparer.Ordinal.Equals(_name, _original.Name);
        var description = _original.Description ?? string.Empty;
        var descriptionChanged = !StringComparer.Ordinal.Equals(_description, description);
        var accent = _original.Accent ?? string.Empty;
        var accentChanged = !StringComparer.Ordinal.Equals(_accent, accent);
        var color = _original.Color ?? string.Empty;
        var colorChanged = !StringComparer.Ordinal.Equals(_color, color);
        var iconChanged = !StringComparer.Ordinal.Equals(_icon, _original.Icon);
        var autoSaveChanged = _autoSave != _original.AutoSave;
        var isolationChanged = _isIsolated != _original.IsIsolated;
        var agentIsolationChanged = _runAgentInIsolation != _original.RunAgentInIsolation;
        var isolationImage = _initialIsolationImageReference;
        var isolationImageChanged = !StringComparer.Ordinal.Equals(
            _isolationImageReference,
            isolationImage);
        var multiplexing = TerminalMultiplexingOptions.Single(option =>
            option.Mode == _original.TerminalMultiplexingOverride);
        var multiplexingChanged = _selectedTerminalMultiplexing != multiplexing;
        var browserProfile = BrowserProfileOptions.Single(option =>
            option.Mode == _original.BrowserProfileOverride);
        var browserProfileChanged = _selectedBrowserProfile != browserProfile;
        var networkOverrideChanged =
            _overridesNetworkSettings != (_original.NetworkOverride is not null);
        _name = _original.Name;
        _description = description;
        _accent = accent;
        _color = color;
        _icon = _original.Icon;
        _autoSave = _original.AutoSave;
        _isIsolated = _original.IsIsolated;
        _runAgentInIsolation = _original.RunAgentInIsolation;
        _isolationImageReference = isolationImage;
        _selectedTerminalMultiplexing = multiplexing;
        _selectedBrowserProfile = browserProfile;
        _overridesNetworkSettings = _original.NetworkOverride is not null;
        if (nameChanged)
        {
            OnPropertyChanged(nameof(Name));
        }

        if (descriptionChanged)
        {
            OnPropertyChanged(nameof(Description));
        }

        if (accentChanged)
        {
            OnPropertyChanged(nameof(Accent));
            OnPropertyChanged(nameof(HasAccent));
            OnPropertyChanged(nameof(AccentSummary));
        }

        if (colorChanged)
        {
            OnPropertyChanged(nameof(Color));
        }

        if (accentChanged || colorChanged)
        {
            OnPropertyChanged(nameof(EffectiveColor));
        }

        if (iconChanged)
        {
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(TileSymbol));
        }

        RefreshChoiceSelection();

        if (autoSaveChanged)
        {
            OnPropertyChanged(nameof(AutoSave));
        }

        if (isolationChanged)
        {
            OnPropertyChanged(nameof(IsIsolated));
            OnPropertyChanged(nameof(CanToggleIsolation));
            OnPropertyChanged(nameof(IsIsolationUnavailable));
            OnPropertyChanged(nameof(CanInstallIsolationRuntime));
        }

        if (agentIsolationChanged)
        {
            OnPropertyChanged(nameof(RunAgentInIsolation));
        }

        if (isolationImageChanged)
        {
            OnPropertyChanged(nameof(IsolationImageReference));
        }

        if (multiplexingChanged)
        {
            OnPropertyChanged(nameof(SelectedTerminalMultiplexing));
        }


        if (browserProfileChanged)
        {
            OnPropertyChanged(nameof(SelectedBrowserProfile));
        }

        if (networkOverrideChanged)
        {
            OnPropertyChanged(nameof(OverridesNetworkSettings));
            PublishNetworkMode();
        }

        AgentPolicy.Changed -= OnAgentPolicyChanged;
        AgentPolicy.Dispose();
        AgentPolicy = new SavedScreenAgentPolicyEditorViewModel(
            _original.AgentPolicyOverride,
            AgentProvidersFor(_original.RunAgentInIsolation));
        AgentPolicy.Changed += OnAgentPolicyChanged;
        OnPropertyChanged(nameof(AgentPolicy));

        NetworkPolicy.Changed -= OnNetworkPolicyChanged;
        NetworkPolicy.Dispose();
        NetworkPolicy = new NetworkPolicyEditorViewModel(
            _networkConnections,
            NetworkPolicyResolver.Resolve(
                _applicationNetworkSettings,
                _original,
                _networkConnections));
        NetworkPolicy.Changed += OnNetworkPolicyChanged;
        OnPropertyChanged(nameof(NetworkPolicy));

        RestoreEntries();
        RestoreIsolationMounts();
        LastOperationError = null;
        SetDirty(false);
        PublishState(entriesChanged: true);
    }

    public WorkspaceEditorCancelDisposition RequestCancel() => IsDirty
        ? WorkspaceEditorCancelDisposition.ConfirmDiscard
        : WorkspaceEditorCancelDisposition.Close;

    public WorkspaceEditorSaveRequest CreateSaveRequest()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"The workspace cannot be saved: {ValidationSummary}");
        }

        return new(BuildDefinition(), ExpectedRevision);
    }

    public void ClearOperationError() => LastOperationError = null;

    public void ApplyNetworkCatalog(
        IReadOnlyList<NetworkConnectionProfile> networkConnections,
        ApplicationNetworkSettings applicationNetworkSettings)
    {
        ArgumentNullException.ThrowIfNull(networkConnections);
        ArgumentNullException.ThrowIfNull(applicationNetworkSettings);
        var previous = NetworkPolicy;
        NetworkPolicy policy;
        if (OverridesNetworkSettings)
        {
            var available = previous.Connections
                .Where(option => option.IsAvailable)
                .Select(option => option.Id)
                .ToArray();
            var selected = previous.SelectedConnection?.Id;
            policy = new NetworkPolicy(
                available,
                selected,
                previous.IsEnabled && selected is not null,
                previous.KillSwitchEnabled);
        }
        else
        {
            policy = NetworkPolicyResolver.ResolveApplication(
                applicationNetworkSettings.Policy,
                networkConnections);
        }

        _networkConnections = [.. networkConnections];
        _applicationNetworkSettings = applicationNetworkSettings;
        previous.Changed -= OnNetworkPolicyChanged;
        NetworkPolicy = new NetworkPolicyEditorViewModel(
            _networkConnections,
            policy,
            isDirty: previous.IsDirty);
        NetworkPolicy.Changed += OnNetworkPolicyChanged;
        previous.Dispose();
        OnPropertyChanged(nameof(NetworkPolicy));
        PublishNetworkMode();
        PublishState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ClearEntries();
        ClearIsolationMounts();
        AgentPolicy.Changed -= OnAgentPolicyChanged;
        AgentPolicy.Dispose();
        NetworkPolicy.Changed -= OnNetworkPolicyChanged;
        NetworkPolicy.Dispose();
        _disposed = true;
    }

    private WorkspaceEditorOperationResult AddEntry(WorkspaceEntry entry)
    {
        var editor = CreateEntryEditor(entry);
        editor.PropertyChanged += OnEntryChanged;
        _entries.Add(editor);
        LastOperationError = null;
        Changed(entriesChanged: true);
        return WorkspaceEditorOperationResult.Applied(entry.Id);
    }

    private WorkspaceEntryEditorViewModel CreateEntryEditor(WorkspaceEntry entry) =>
        WorkspaceEntryEditorViewModel.Create(
            entry,
            ConnectionOptions,
            ScreenOptions,
            LayoutOptions,
            FileProviderOptions);

    private void RestoreEntries()
    {
        ClearEntries();
        foreach (var entry in _original.Entries)
        {
            var editor = CreateEntryEditor(entry);
            editor.PropertyChanged += OnEntryChanged;
            _entries.Add(editor);
        }
    }

    private void ClearEntries()
    {
        foreach (var entry in _entries)
        {
            entry.PropertyChanged -= OnEntryChanged;
            entry.Dispose();
        }

        _entries.Clear();
    }

    private WorkspaceDefinition BuildDefinition() => new(
        Id,
        SchemaVersion,
        (Name ?? string.Empty).Trim(),
        Description,
        Accent,
        [.. _entries.Select(entry => entry.Build())],
        AgentPolicy.Build(),
        Icon,
        AutoSave,
        Color,
        _original.AgentPanelPinned,
        SelectedTerminalMultiplexing.Mode,
        SelectedBrowserProfile.Mode,
        !string.IsNullOrWhiteSpace(Accent),
        IsIsolated,
        [.. _isolationMounts.Select(mount => mount.Build())],
        string.Equals(
            IsolationImageReference,
            _initialIsolationImageReference,
            StringComparison.Ordinal)
            ? _original.IsolationImageReference
            : IsolationImageReference,
        RunAgentInIsolation,
        OverridesNetworkSettings ? NetworkPolicy.CreatePolicy() : null,
        _original.SortOrder);

    private IReadOnlyList<DefinitionValidationIssue> Validate()
    {
        if (!AgentPolicy.IsValid)
        {
            return
            [
                new(
                    DefinitionValidationCode.InvalidAgentPolicy,
                    "Choose an enabled AI provider and a valid default model for this workspace.",
                    Id.Value),
            ];
        }

        if (OverridesNetworkSettings && !NetworkPolicy.IsValid)
        {
            return
            [
                new(
                    DefinitionValidationCode.InvalidEntry,
                    "Remove missing network connections and choose an available route.",
                    Id.Value),
            ];
        }

        var definition = BuildDefinition();
        List<DefinitionValidationIssue> issues = [.. WorkspaceValidator.Validate(definition).Issues.Select(issue =>
            _entries.FirstOrDefault(entry => string.Equals(entry.Id.Value, issue.Target, StringComparison.Ordinal)) is { } entry
                ? issue with { Message = $"'{entry.DisplayName}': {issue.Message}" }
                : issue)];
        foreach (var (value, label) in new[] { (Accent, "accent"), (Color, "color") })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                _ = RgbColor.Parse(value);
            }
            catch (FormatException)
            {
                issues.Add(new(
                    DefinitionValidationCode.InvalidEntry,
                    $"The workspace {label} must contain six hexadecimal digits.",
                    Id.Value));
            }
        }

        foreach (var entry in _entries)
        {
            if (entry.HasMissingReference)
            {
                issues.Add(new(
                    DefinitionValidationCode.MissingDependency,
                    $"Repair the missing definition used by '{entry.DisplayName}'.",
                    entry.Id.Value));
            }

            if (entry.Tab is not { SelectedLayout.Definition: { } layout } tab)
            {
                continue;
            }

            var tabDefinition = tab.Build();
            var screen = new ScreenDefinition(
                new ScreenId($"workspace-tab-{entry.Id.Value}"),
                ScreenDefinition.CurrentSchemaVersion,
                tabDefinition.Name,
                null,
                tabDefinition.LayoutId,
                tabDefinition.Panels);
            issues.AddRange(ScreenValidator.Validate(screen, layout).Issues.Select(issue =>
                issue with { Message = $"'{entry.DisplayName}': {issue.Message}" }));
        }

        return [.. issues.DistinctBy(issue => (issue.Code, issue.Message, issue.Target))];
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        Changed();
    }

    private void OnIsolationMountChanged(object? sender, PropertyChangedEventArgs e)
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

    private void OnNetworkPolicyChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Changed();
    }

    private IReadOnlyList<AiProviderProfileDescriptor>? AgentProvidersFor(
        bool isolatedAgent)
    {
        if (!isolatedAgent || _aiProviders is null)
        {
            return _aiProviders;
        }

        return [.. _aiProviders.Where(provider => !provider.Endpoint.IsLoopback)];
    }

    private void ReplaceAgentPolicy(AgentPolicy? policy)
    {
        AgentPolicy.Changed -= OnAgentPolicyChanged;
        AgentPolicy.Dispose();
        AgentPolicy = new SavedScreenAgentPolicyEditorViewModel(
            policy,
            AgentProvidersFor(RunAgentInIsolation));
        AgentPolicy.Changed += OnAgentPolicyChanged;
        OnPropertyChanged(nameof(AgentPolicy));
    }

    private void Changed(bool entriesChanged = false)
    {
        SetDirty(true);
        PublishState(entriesChanged);
    }

    private void SetDirty(bool value)
    {
        if (_isDirty == value)
        {
            return;
        }

        _isDirty = value;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DirtyStatus));
        OnPropertyChanged(nameof(CanRecreateIsolationEnvironment));
    }

    private void PublishState(bool entriesChanged = false)
    {
        _validationIssues = Validate();
        if (entriesChanged)
        {
            OnPropertyChanged(nameof(Entries));
            OnPropertyChanged(nameof(ConnectionEntries));
            OnPropertyChanged(nameof(SavedScreenEntries));
            OnPropertyChanged(nameof(WorkspaceTabEntries));
            OnPropertyChanged(nameof(TabCount));
            OnPropertyChanged(nameof(EntryCount));
            OnPropertyChanged(nameof(HasNoEntries));
        }

        OnPropertyChanged(nameof(ValidationIssues));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(MissingReferenceCount));
        OnPropertyChanged(nameof(HasMissingReferences));
    }

    private WorkspaceEditorOperationResult Reject(string error)
    {
        LastOperationError = error;
        return WorkspaceEditorOperationResult.Rejected(error);
    }

    private int IndexOf(WorkspaceEntryId entryId) => _entries
        .Select((entry, index) => (entry, index))
        .Where(item => item.entry.Id == entryId)
        .Select(item => item.index)
        .SingleOrDefault(-1);

    private void RestoreIsolationMounts()
    {
        ClearIsolationMounts();
        foreach (var mount in _original.IsolationMounts)
        {
            AddIsolationMount(new WorkspaceIsolationMountEditorViewModel(
                mount.HostPath,
                mount.GuestPath,
                mount.IsReadOnly));
        }

        PublishIsolationMountState();
    }

    private void ClearIsolationMounts()
    {
        foreach (var mount in _isolationMounts)
        {
            mount.PropertyChanged -= OnIsolationMountChanged;
        }

        _isolationMounts.Clear();
    }

    private void AddIsolationMount(WorkspaceIsolationMountEditorViewModel mount)
    {
        mount.PropertyChanged += OnIsolationMountChanged;
        _isolationMounts.Add(mount);
        PublishIsolationMountState();
    }

    private string NextGuestMountPath()
    {
        const string stem = "/workspace";
        if (_isolationMounts.All(mount =>
                !string.Equals(mount.GuestPath, stem, StringComparison.Ordinal)))
        {
            return stem;
        }

        for (var suffix = 2; suffix <= WorkspaceDefinition.MaximumIsolationMountCount; suffix++)
        {
            var candidate = $"{stem}-{suffix}";
            if (_isolationMounts.All(mount =>
                    !string.Equals(mount.GuestPath, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private void PublishIsolationMountState()
    {
        OnPropertyChanged(nameof(IsolationMounts));
        OnPropertyChanged(nameof(IsolationMountCount));
        OnPropertyChanged(nameof(HasNoIsolationMounts));
        OnPropertyChanged(nameof(CanAddIsolationMount));
    }

    private void PublishNetworkMode()
    {
        OnPropertyChanged(nameof(FollowsApplicationNetwork));
        OnPropertyChanged(nameof(InheritedNetworkSummary));
        OnPropertyChanged(nameof(NetworkModeSummary));
    }

    private string NetworkPolicySummary(NetworkPolicy policy)
    {
        if (!policy.IsEnabled || policy.SelectedConnectionId is not { } selectedId)
        {
            return "The application default connects directly.";
        }

        var profile = _networkConnections.SingleOrDefault(item => item.Id == selectedId);
        return profile is null
            ? "The application default points to an unavailable connection."
            : $"The application default uses {profile.Name}.";
    }

    private static IReadOnlyList<ScreenConnectionOption> BuildConnectionOptions(
        WorkspaceDefinition workspace,
        IReadOnlyList<ConnectionProfile> connections,
        IReadOnlyList<ScreenDefinition> screens)
    {
        var options = connections
            .Select(connection => new ScreenConnectionOption(
                connection.Id,
                connection.Name,
                KindBadges.Connection(connection.ConnectionKind),
                true,
                connection.ConnectionKind))
            .ToList();
        var referencedIds = workspace.Entries
            .OfType<WorkspaceEntry.ConnectionReference>()
            .Select(entry => entry.ConnectionId)
            .Concat(workspace.Entries
                .OfType<WorkspaceEntry.Tab>()
                .SelectMany(tab => tab.Panels)
                .Select(panel => panel.ConnectionId)
                .OfType<ConnectionId>())
            .Concat(screens
                .SelectMany(screen => screen.Panels)
                .Select(panel => panel.ConnectionId)
                .OfType<ConnectionId>())
            .Distinct();
        foreach (var missingId in referencedIds.Where(id =>
            options.All(option => option.Id != id)))
        {
            options.Add(new ScreenConnectionOption(
                missingId,
                missingId.Value,
                "Unavailable",
                false));
        }

        return [.. options
            .OrderByDescending(option => option.IsAvailable)
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<WorkspaceLayoutOption> BuildLayoutOptions(
        WorkspaceDefinition workspace,
        IReadOnlyList<LayoutDefinition> layouts,
        IReadOnlyList<ScreenDefinition> screens)
    {
        var options = layouts
            .Select(layout => new WorkspaceLayoutOption(
                layout.Id,
                layout.Name,
                true,
                layout))
            .ToList();
        var referencedIds = workspace.Entries
            .OfType<WorkspaceEntry.Tab>()
            .Select(tab => tab.LayoutId)
            .Concat(screens.Select(screen => screen.LayoutId))
            .Distinct();
        foreach (var missingId in referencedIds.Where(id =>
            options.All(option => option.Id != id)))
        {
            options.Add(new WorkspaceLayoutOption(
                missingId,
                missingId.Value,
                false,
                null));
        }

        return [.. options
            .OrderByDescending(option => option.IsAvailable)
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<WorkspaceScreenOption> BuildScreenOptions(
        WorkspaceDefinition workspace,
        IReadOnlyList<ScreenDefinition> screens,
        IReadOnlyList<WorkspaceLayoutOption> layoutOptions)
    {
        var options = screens
            .Select(screen => new WorkspaceScreenOption(
                screen.Id,
                screen.Name,
                layoutOptions.Single(option => option.Id == screen.LayoutId).DisplayName,
                true))
            .ToList();
        var referencedIds = workspace.Entries
            .OfType<WorkspaceEntry.ScreenReference>()
            .Select(entry => entry.ScreenId)
            .Distinct();
        foreach (var missingId in referencedIds.Where(id =>
            options.All(option => option.Id != id)))
        {
            options.Add(new WorkspaceScreenOption(
                missingId,
                missingId.Value,
                "Unavailable",
                false));
        }

        return [.. options
            .OrderByDescending(option => option.IsAvailable)
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<ScreenFileProviderOption> BuildFileProviderOptions(
        WorkspaceDefinition workspace,
        IReadOnlyList<ScreenDefinition> screens,
        IReadOnlyList<FileProviderProfile> fileProviders)
    {
        List<ScreenFileProviderOption> options =
        [
            new(BuiltInHomeId, "Home", "LOCAL", true),
        ];
        options.AddRange(fileProviders
            .Where(profile => profile.Id != BuiltInHomeId)
            .Select(profile => new ScreenFileProviderOption(
                profile.Id,
                profile.Name,
                KindBadges.FileProvider(profile.ProviderKind),
                true)));
        var referencedIds = workspace.Entries
            .OfType<WorkspaceEntry.Tab>()
            .SelectMany(tab => tab.Panels)
            .Concat(screens.SelectMany(screen => screen.Panels))
            .Select(panel => panel.FileProviderProfileId)
            .OfType<FileProviderProfileId>()
            .Distinct();
        foreach (var missingId in referencedIds.Where(id =>
            options.All(option => option.Id != id)))
        {
            options.Add(new ScreenFileProviderOption(
                missingId,
                missingId.Value,
                "Unavailable",
                false));
        }

        return [.. options
            .OrderByDescending(option => option.IsAvailable)
            .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
