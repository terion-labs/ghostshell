using GhostShell.Core;

namespace GhostShell.Application;

public sealed class DefinitionCatalog : IDefinitionCatalog, IDisposable
{
    private const string DefaultTerminalProfileId = "builtin.terminal.default";
    private static readonly TerminalPalette LegacyGhostShellDarkPalette = new(
        "GhostSHELL Dark",
        RgbColor.Parse("#E8E4DE"),
        RgbColor.Parse("#12100E"),
        RgbColor.Parse("#D9944D"),
        RgbColor.Parse("#4A3828"),
        [
            RgbColor.Parse("#1F1C19"),
            RgbColor.Parse("#D26060"),
            RgbColor.Parse("#72B57B"),
            RgbColor.Parse("#D1A85A"),
            RgbColor.Parse("#6B9BD2"),
            RgbColor.Parse("#B17AC5"),
            RgbColor.Parse("#66B8B2"),
            RgbColor.Parse("#D8D2C8"),
            RgbColor.Parse("#69625B"),
            RgbColor.Parse("#EE7B72"),
            RgbColor.Parse("#91D39A"),
            RgbColor.Parse("#EBC574"),
            RgbColor.Parse("#86B6EA"),
            RgbColor.Parse("#CD98DF"),
            RgbColor.Parse("#83D5CF"),
            RgbColor.Parse("#FFF9F0"),
        ]);

    private readonly IDefinitionRepository<ConnectionProfile> _connections;
    private readonly IDefinitionRepository<LayoutDefinition> _layouts;
    private readonly IDefinitionRepository<ScreenDefinition> _screens;
    private readonly IDefinitionRepository<WorkspaceDefinition> _workspaces;
    private readonly IDefinitionRepository<ThemePreference> _themes;
    private readonly IDefinitionRepository<TerminalProfile> _terminalProfiles;
    private readonly IDefinitionRepository<KeymapProfile> _keymaps;
    private readonly IDefinitionRepository<FileProviderProfile> _fileProviderProfiles;
    private readonly IDefinitionRepository<AiProviderProfile> _aiProviderProfiles;
    private readonly IDefinitionRepository<McpServerProfile> _mcpServerProfiles;
    private readonly IDefinitionRepository<DatabaseConnectionProfile> _databaseConnections;
    private readonly IDefinitionRepository<QuickTerminalSettings> _quickTerminalSettings;
    private readonly IDefinitionRepository<BrowserProfileDefinition> _browserProfiles;
    private readonly IDefinitionRepository<NetworkConnectionProfile> _networkConnections;
    private readonly IDefinitionRepository<ApplicationNetworkSettings> _applicationNetworkSettings;
    private readonly ILayoutGraphStore? _layoutGraph;
    private readonly ThemePreference _defaultTheme;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private DefinitionCatalogSnapshot _snapshot = DefinitionCatalogSnapshot.Empty;
    private bool _initialized;

    public DefinitionCatalog(
        IDefinitionRepository<ConnectionProfile> connections,
        IDefinitionRepository<LayoutDefinition> layouts,
        IDefinitionRepository<ScreenDefinition> screens,
        IDefinitionRepository<WorkspaceDefinition> workspaces,
        IDefinitionRepository<ThemePreference> themes,
        IDefinitionRepository<TerminalProfile> terminalProfiles,
        IDefinitionRepository<KeymapProfile> keymaps,
        IDefinitionRepository<FileProviderProfile> fileProviderProfiles,
        IDefinitionRepository<AiProviderProfile> aiProviderProfiles,
        IDefinitionRepository<McpServerProfile> mcpServerProfiles,
        IDefinitionRepository<QuickTerminalSettings> quickTerminalSettings,
        ILayoutGraphStore? layoutGraph = null,
        IDefinitionRepository<DatabaseConnectionProfile>? databaseConnections = null,
        ThemePreference? defaultTheme = null,
        IDefinitionRepository<BrowserProfileDefinition>? browserProfiles = null,
        IDefinitionRepository<NetworkConnectionProfile>? networkConnections = null,
        IDefinitionRepository<ApplicationNetworkSettings>? applicationNetworkSettings = null)
    {
        _layoutGraph = layoutGraph;
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _themes = themes ?? throw new ArgumentNullException(nameof(themes));
        _terminalProfiles = terminalProfiles
            ?? throw new ArgumentNullException(nameof(terminalProfiles));
        _keymaps = keymaps ?? throw new ArgumentNullException(nameof(keymaps));
        _fileProviderProfiles = fileProviderProfiles
            ?? throw new ArgumentNullException(nameof(fileProviderProfiles));
        _aiProviderProfiles = aiProviderProfiles
            ?? throw new ArgumentNullException(nameof(aiProviderProfiles));
        _mcpServerProfiles = mcpServerProfiles
            ?? throw new ArgumentNullException(nameof(mcpServerProfiles));
        _quickTerminalSettings = quickTerminalSettings
            ?? throw new ArgumentNullException(nameof(quickTerminalSettings));
        _databaseConnections = databaseConnections
            ?? new EphemeralRepository<DatabaseConnectionProfile>();
        _browserProfiles = browserProfiles
            ?? new EphemeralRepository<BrowserProfileDefinition>();
        _networkConnections = networkConnections
            ?? new EphemeralRepository<NetworkConnectionProfile>();
        _applicationNetworkSettings = applicationNetworkSettings
            ?? new EphemeralRepository<ApplicationNetworkSettings>();
        _defaultTheme = defaultTheme ?? ThemePreference.Default;
    }

    /// <summary>
    /// Keeps hosts without persistent database-connection storage working:
    /// saved connections live for the process only. The desktop composition
    /// always supplies the SQLite repository instead.
    /// </summary>
    private sealed class EphemeralRepository<TDefinition> : IDefinitionRepository<TDefinition>
        where TDefinition : IDurableDefinition
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, StoredDefinition<TDefinition>> _items = [];

        public ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> GetAsync(
            DefinitionKey key,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_items.TryGetValue(key.Value, out var stored)
                    ? DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(stored)
                    : DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(new(
                        DefinitionStoreErrorCode.NotFound,
                        "The requested definition does not exist.")));
            }
        }

        public ValueTask<DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>> ListAsync(
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(
                    DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>.Success(
                        [.. _items.Values.OrderBy(item => item.Value.Name, StringComparer.Ordinal)]));
            }
        }

        public ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveAsync(
            TDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var existing = _items.TryGetValue(definition.Key.Value, out var stored)
                    ? stored
                    : null;
                if (expectedRevision is null && existing is not null
                    || expectedRevision is { } expected
                        && existing?.Revision != expected)
                {
                    return ValueTask.FromResult(
                        DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(new(
                            DefinitionStoreErrorCode.RevisionConflict,
                            "The definition changed before it could be saved.")));
                }

                var now = DateTimeOffset.UtcNow;
                var next = new StoredDefinition<TDefinition>(
                    definition,
                    (existing?.Revision ?? 0) + 1,
                    existing?.CreatedAt ?? now,
                    now);
                _items[definition.Key.Value] = next;
                return ValueTask.FromResult(
                    DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(next));
            }
        }

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _items.TryGetValue(key.Value, out var stored)
                        && stored.Revision == expectedRevision
                        && _items.Remove(key.Value)
                        ? DefinitionStoreResult<Unit>.Success(Unit.Value)
                        : DefinitionStoreResult<Unit>.Failure(new(
                            DefinitionStoreErrorCode.RevisionConflict,
                            "The definition changed before it could be deleted.")));
            }
        }
    }

    public DefinitionCatalogSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler? Changed;

    public async ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(Snapshot);
            }

            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                return refreshed;
            }

            var seeded = await SeedDefaultsAsync(cancellationToken).ConfigureAwait(false);
            if (!seeded.IsSuccess)
            {
                return DefinitionStoreResult<DefinitionCatalogSnapshot>.Failure(seeded.Error!);
            }

            refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                _initialized = true;
                // Ordinarily nobody is subscribed yet. With keys sealed under
                // the startup PIN the whole presentation exists first and
                // initialization happens behind the lock screen — this is
                // how every projection learns the catalog now has content.
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return refreshed;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Rebuilds the published snapshot from durable storage without running first-use seeding.
    /// A failed reload leaves the previously published snapshot intact.
    /// </summary>
    public async ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed.IsSuccess)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return refreshed;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _connections,
            ValidateConnectionName,
            cancellationToken);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
        LayoutDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var error = ValidateLayout(definition);
            if (error is not null)
            {
                return DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Failure(error);
            }

            // A layout edit changes the slot set, and saved screens follow the
            // layout rather than vetoing it: mappings for removed slots are
            // dropped, and added slots arrive as unassigned terminal panels the
            // user fills in when the screen next opens. Refusing the save here
            // made the designer a dead end for any layout a screen had adopted.
            var reconciled = new List<(ScreenDefinition Screen, long Revision)>();
            foreach (var screen in Snapshot.Screens
                         .Where(item => item.Value.LayoutId == definition.Id))
            {
                if (ScreenValidator.Validate(screen.Value, definition).IsValid)
                {
                    continue;
                }

                var updated = ReconcileScreenWithLayout(screen.Value, definition);
                if (!ScreenValidator.Validate(updated, definition).IsValid)
                {
                    return DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Failure(
                        new DefinitionStoreError(
                            DefinitionStoreErrorCode.DependencyConflict,
                            $"The layout change would invalidate saved screen '{screen.Value.Name}'."));
                }

                reconciled.Add((updated, screen.Revision));
            }

            DefinitionStoreResult<StoredDefinition<LayoutDefinition>> result;
            if (reconciled.Count > 0 && _layoutGraph is not null)
            {
                // The storage graph validates dependents on every write, so the
                // layout and its reconciled screens must land as one batch —
                // saved separately, either order is rejected for the other's
                // sake.
                result = await _layoutGraph.SaveLayoutWithScreensAsync(
                        definition,
                        expectedRevision,
                        [.. reconciled.Select(item => new ScreenRevisionUpdate(item.Screen, item.Revision))],
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return result;
                }
            }
            else
            {
                result = await _layouts.SaveAsync(
                        definition,
                        expectedRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return result;
                }

                foreach (var (screen, revision) in reconciled)
                {
                    var screenResult = await _screens.SaveAsync(
                            screen,
                            revision,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!screenResult.IsSuccess)
                    {
                        // The layout is already durable. Publish what happened
                        // and surface the screen failure instead of pretending
                        // the layout save itself failed.
                        _ = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                        Changed?.Invoke(this, EventArgs.Empty);
                        return DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Failure(
                            screenResult.Error!);
                    }
                }
            }

            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                return DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Failure(
                    refreshed.Error!);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Projects a screen's panel mappings onto an edited layout: panels whose
    /// slots are gone are dropped, and slots the screen does not map yet are
    /// filled with unassigned terminal panels using default startup behavior.
    /// </summary>
    private static ScreenDefinition ReconcileScreenWithLayout(
        ScreenDefinition screen,
        LayoutDefinition layout)
    {
        var knownSlots = layout.Slots.Select(slot => slot.Id).ToHashSet();
        var panels = screen.Panels
            .Where(panel => knownSlots.Contains(panel.SlotId))
            .ToList();
        var usedPanelIds = panels
            .Select(panel => panel.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        var mappedSlots = panels.Select(panel => panel.SlotId).ToHashSet();
        foreach (var slot in layout.Slots)
        {
            if (!mappedSlots.Add(slot.Id))
            {
                continue;
            }

            var panelId = $"panel-{slot.Id.Value}";
            var suffix = 2;
            while (!usedPanelIds.Add(panelId))
            {
                panelId = $"panel-{slot.Id.Value}-{suffix++}";
            }

            panels.Add(new ScreenPanelDefinition(
                new ScreenPanelId(panelId),
                slot.Id,
                ScreenPanelKind.Terminal,
                Title: null,
                ConnectionId: null,
                PanelStartupBehavior.None));
        }

        return new ScreenDefinition(
            screen.Id,
            screen.SchemaVersion,
            screen.Name,
            screen.Description,
            screen.LayoutId,
            panels,
            screen.Tags,
            screen.AgentPolicyOverride);
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
        ScreenDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _screens,
            ValidateScreen,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
        WorkspaceDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _workspaces,
            ValidateWorkspace,
            cancellationToken);

    /// <summary>
    /// Saves a workspace together with the auto-saved tab layouts its entries
    /// reference. The two must land as one batch: the storage graph rejects a
    /// workspace whose tab layouts are not stored yet, and would strand the
    /// layouts if the workspace write failed after them. Auto-saved layouts skip
    /// the duplicate-name rule because catalog listings hide them.
    /// </summary>
    public async ValueTask<DefinitionStoreError?> SaveWorkspaceWithLayoutsAsync(
        WorkspaceDefinition workspace,
        long? expectedWorkspaceRevision,
        IReadOnlyList<(LayoutDefinition Definition, long? ExpectedRevision)> layouts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(layouts);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (layout, _) in layouts)
            {
                var layoutValidation = LayoutValidator.Validate(layout);
                if (!layoutValidation.IsValid)
                {
                    return Invalid(layoutValidation);
                }
            }

            var error = ValidateWorkspace(
                workspace,
                [.. layouts.Select(item => item.Definition)]);
            if (error is not null)
            {
                return error;
            }

            if (_layoutGraph is not null)
            {
                var writes = layouts
                    .Select(item => new DefinitionGraphWrite(item.Definition, item.ExpectedRevision))
                    .Append(new DefinitionGraphWrite(workspace, expectedWorkspaceRevision))
                    .ToArray();
                var saveError = await _layoutGraph.SaveGraphAsync(writes, cancellationToken)
                    .ConfigureAwait(false);
                if (saveError is not null)
                {
                    return saveError;
                }
            }
            else
            {
                foreach (var (layout, expectedRevision) in layouts)
                {
                    var layoutResult = await _layouts.SaveAsync(
                            layout,
                            expectedRevision,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!layoutResult.IsSuccess)
                    {
                        _ = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                        Changed?.Invoke(this, EventArgs.Empty);
                        return layoutResult.Error;
                    }
                }

                var workspaceResult = await _workspaces.SaveAsync(
                        workspace,
                        expectedWorkspaceRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!workspaceResult.IsSuccess)
                {
                    _ = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                    Changed?.Invoke(this, EventArgs.Empty);
                    return workspaceResult.Error;
                }
            }

            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                return refreshed.Error;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        ThemePreference definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _themes,
            ValidateThemeName,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        TerminalProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _terminalProfiles,
            ValidateTerminalProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
        KeymapProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _keymaps,
            ValidateKeymap,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>> SaveFileProviderProfileAsync(
        FileProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _fileProviderProfiles,
            ValidateFileProviderProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>> SaveAiProviderProfileAsync(
        AiProviderProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _aiProviderProfiles,
            ValidateAiProviderProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>> SaveMcpServerProfileAsync(
        McpServerProfile definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _mcpServerProfiles,
            ValidateMcpServerProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<BrowserProfileDefinition>>> SaveBrowserProfileAsync(
        BrowserProfileDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _browserProfiles,
            ValidateBrowserProfile,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        QuickTerminalSettings definition,
        long? expectedRevision,
        CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _quickTerminalSettings,
            ValidateQuickTerminalSettings,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<DatabaseConnectionProfile>>>
        SaveDatabaseConnectionAsync(
            DatabaseConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _databaseConnections,
            ValidateDatabaseConnection,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<NetworkConnectionProfile>>>
        SaveNetworkConnectionAsync(
            NetworkConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _networkConnections,
            ValidateNetworkConnection,
            cancellationToken);

    public ValueTask<DefinitionStoreResult<StoredDefinition<ApplicationNetworkSettings>>>
        SaveApplicationNetworkSettingsAsync(
            ApplicationNetworkSettings definition,
            long? expectedRevision,
            CancellationToken cancellationToken) =>
        SaveValidatedAsync(
            definition,
            expectedRevision,
            _applicationNetworkSettings,
            ValidateApplicationNetworkSettings,
            cancellationToken);

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (key.Kind == AiProviderProfile.Kind
                && IsAiProviderPolicyDependency(key.Value))
            {
                return DefinitionStoreResult<Unit>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.DependencyConflict,
                        "This AI-provider profile is referenced by a saved screen or workspace agent policy."));
            }

            if (key.Kind == WorkspaceDefinition.Kind
                && string.Equals(
                    key.Value,
                    WorkspaceDefinition.DefaultWorkspaceId,
                    StringComparison.Ordinal))
            {
                return DefinitionStoreResult<Unit>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.DependencyConflict,
                        $"The {WorkspaceDefinition.DefaultWorkspaceName} workspace always exists and cannot be deleted."));
            }

            if (key.Kind == NetworkConnectionProfile.Kind
                && IsNetworkPolicyDependency(key.Value))
            {
                return DefinitionStoreResult<Unit>.Failure(
                    new DefinitionStoreError(
                        DefinitionStoreErrorCode.DependencyConflict,
                        "This network connection is referenced by application or workspace network settings."));
            }

            var result = key.Kind switch
            {
                var kind when kind == ConnectionProfile.Kind =>
                    await _connections.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == LayoutDefinition.Kind =>
                    await _layouts.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == ScreenDefinition.Kind =>
                    await _screens.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == WorkspaceDefinition.Kind =>
                    await _workspaces.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == ThemePreference.Kind =>
                    await _themes.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == TerminalProfile.Kind =>
                    await _terminalProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == KeymapProfile.Kind =>
                    await _keymaps.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == FileProviderProfile.Kind =>
                    await _fileProviderProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == AiProviderProfile.Kind =>
                    await _aiProviderProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == McpServerProfile.Kind =>
                    await _mcpServerProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == QuickTerminalSettings.Kind =>
                    await _quickTerminalSettings.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == DatabaseConnectionProfile.Kind =>
                    await _databaseConnections.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == NetworkConnectionProfile.Kind =>
                    await _networkConnections.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == ApplicationNetworkSettings.Kind
                    && string.Equals(
                        key.Value,
                        ApplicationNetworkSettings.DefaultId.Value,
                        StringComparison.Ordinal) =>
                    DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                        DefinitionStoreErrorCode.DependencyConflict,
                        "Application network settings always exist and cannot be deleted.")),
                var kind when kind == ApplicationNetworkSettings.Kind =>
                    await _applicationNetworkSettings.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                var kind when kind == BrowserProfileDefinition.Kind
                    && string.Equals(
                        key.Value,
                        BuiltInBrowserProfiles.DefaultProfileId,
                        StringComparison.Ordinal) =>
                    DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                        DefinitionStoreErrorCode.DependencyConflict,
                        "The built-in default browser profile cannot be deleted.")),
                var kind when kind == BrowserProfileDefinition.Kind =>
                    await _browserProfiles.DeleteAsync(key, expectedRevision, cancellationToken)
                        .ConfigureAwait(false),
                _ => DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                    DefinitionStoreErrorCode.UnsupportedKind,
                    "This definition kind cannot be deleted by the current application.")),
            };
            if (result.IsSuccess)
            {
                var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!refreshed.IsSuccess)
                {
                    return DefinitionStoreResult<Unit>.Failure(refreshed.Error!);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveValidatedAsync<TDefinition>(
        TDefinition definition,
        long? expectedRevision,
        IDefinitionRepository<TDefinition> repository,
        Func<TDefinition, DefinitionStoreError?> validate,
        CancellationToken cancellationToken)
        where TDefinition : IDurableDefinition
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var error = validate(definition);
            if (error is not null)
            {
                return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(error);
            }

            var result = await repository.SaveAsync(
                    definition,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (!refreshed.IsSuccess)
                {
                    return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(
                        refreshed.Error!);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }

            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private DefinitionStoreError? ValidateConnectionName(ConnectionProfile definition) =>
        ValidateName(Snapshot.Connections, definition);

    private DefinitionStoreError? ValidateThemeName(ThemePreference definition) =>
        ValidateName(Snapshot.Themes, definition);

    private DefinitionStoreError? ValidateLayout(LayoutDefinition definition)
    {
        var duplicate = ValidateName(Snapshot.Layouts, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var validation = LayoutValidator.Validate(definition);
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        // Dependent screens are not validated here: the layout save reconciles
        // them to the edited slot set instead of refusing the edit.
        return null;
    }

    private DefinitionStoreError? ValidateScreen(ScreenDefinition definition)
    {
        var duplicate = ValidateName(Snapshot.Screens, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var layout = Snapshot.Layouts
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == definition.LayoutId);
        if (layout is null)
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "The selected layout no longer exists.");
        }

        var validation = ScreenValidator.Validate(definition, layout);
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        var policyDependency = ValidateAgentPolicyProviderDependency(
            definition.AgentPolicyOverride);
        if (policyDependency is not null)
        {
            return policyDependency;
        }

        var connectionIds = Snapshot.Connections.Select(item => item.Value.Id).ToHashSet();
        if (definition.Panels.Any(panel =>
                panel.ConnectionId is { } id && !connectionIds.Contains(id)))
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "At least one panel references a connection that no longer exists.");
        }

        var fileProviderIds = Snapshot.FileProviderProfiles
            .Select(item => item.Value.Id)
            .ToHashSet();
        return definition.Panels.Any(panel =>
            panel.FileProviderProfileId is { } id
            && !BuiltInFileProviders.IsIntrinsic(id)
            && !fileProviderIds.Contains(id))
            ? new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "At least one panel references a file-provider profile that no longer exists.")
            : null;
    }

    private DefinitionStoreError? ValidateWorkspace(WorkspaceDefinition definition) =>
        ValidateWorkspace(definition, pendingLayouts: null);

    private DefinitionStoreError? ValidateWorkspace(
        WorkspaceDefinition definition,
        IReadOnlyList<LayoutDefinition>? pendingLayouts)
    {
        var duplicate = ValidateName(Snapshot.Workspaces, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var validation = WorkspaceValidator.Validate(definition);
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        var policyDependency = ValidateAgentPolicyProviderDependency(
            definition.AgentPolicyOverride);
        if (policyDependency is not null)
        {
            return policyDependency;
        }

        var networkPolicyDependency = ValidateNetworkPolicyDependencies(
            definition.NetworkOverride);
        if (networkPolicyDependency is not null)
        {
            return networkPolicyDependency;
        }

        var connectionIds = Snapshot.Connections.Select(item => item.Value.Id).ToHashSet();
        var screenIds = Snapshot.Screens.Select(item => item.Value.Id).ToHashSet();
        var fileProviderIds = Snapshot.FileProviderProfiles
            .Select(item => item.Value.Id)
            .ToHashSet();
        var layouts = Snapshot.Layouts.ToDictionary(item => item.Value.Id, item => item.Value);
        foreach (var pending in pendingLayouts ?? [])
        {
            // Layouts that land in the same batch as the workspace are valid
            // dependency targets even though the snapshot has not seen them yet.
            layouts[pending.Id] = pending;
        }
        foreach (var entry in definition.Entries)
        {
            switch (entry)
            {
                case WorkspaceEntry.ConnectionReference connection
                    when !connectionIds.Contains(connection.ConnectionId):
                    return MissingWorkspaceDependency("connection", connection.ConnectionId.Value);
                case WorkspaceEntry.ScreenReference screen when !screenIds.Contains(screen.ScreenId):
                    return MissingWorkspaceDependency("screen", screen.ScreenId.Value);
                case WorkspaceEntry.Tab tab when !layouts.TryGetValue(tab.LayoutId, out _):
                    return MissingWorkspaceDependency("layout", tab.LayoutId.Value);
                case WorkspaceEntry.Tab tab:
                    var resolvedLayout = layouts[tab.LayoutId];
                    var screenShape = new ScreenDefinition(
                        new ScreenId($"workspace-tab-{tab.Id.Value}"),
                        ScreenDefinition.CurrentSchemaVersion,
                        tab.Name,
                        null,
                        tab.LayoutId,
                        tab.Panels);
                    var tabValidation = ScreenValidator.Validate(screenShape, resolvedLayout);
                    if (!tabValidation.IsValid)
                    {
                        return Invalid(tabValidation);
                    }

                    if (tab.Panels.Any(panel =>
                        panel.ConnectionId is { } id && !connectionIds.Contains(id)))
                    {
                        return MissingWorkspaceDependency("connection", "workspace-only tab");
                    }

                    if (tab.Panels.Any(panel =>
                        panel.FileProviderProfileId is { } id
                        && !BuiltInFileProviders.IsIntrinsic(id)
                        && !fileProviderIds.Contains(id)))
                    {
                        return MissingWorkspaceDependency(
                            "file-provider profile",
                            "workspace-only tab");
                    }

                    break;
            }
        }

        return null;
    }

    private DefinitionStoreError? ValidateAgentPolicyProviderDependency(
        AgentPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        AiProviderProfileId profileId;
        try
        {
            profileId = new AiProviderProfileId(policy.Provider);
        }
        catch (ArgumentException)
        {
            return MissingAgentPolicyProvider();
        }

        return Snapshot.AiProviderProfiles.Any(item =>
            item.Value.Id == profileId && item.Value.IsEnabled)
            ? null
            : MissingAgentPolicyProvider();
    }

    private bool IsAiProviderPolicyDependency(string providerId) =>
        Snapshot.Screens.Any(item =>
            string.Equals(
                item.Value.AgentPolicyOverride?.Provider,
                providerId,
                StringComparison.Ordinal))
        || Snapshot.Workspaces.Any(item =>
            string.Equals(
                item.Value.AgentPolicyOverride?.Provider,
                providerId,
                StringComparison.Ordinal));

    private static DefinitionStoreError MissingAgentPolicyProvider() =>
        new(
            DefinitionStoreErrorCode.DependencyConflict,
            "The agent policy requires an existing enabled AI-provider profile.");

    private DefinitionStoreError? ValidateTerminalProfile(TerminalProfile definition)
    {
        var duplicate = ValidateName(Snapshot.TerminalProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        return Snapshot.Keymaps.Any(item => item.Value.Id == definition.KeymapId)
            || BuiltInKeymaps.All.Any(item => item.Id == definition.KeymapId)
            ? null
            : new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "The selected terminal keymap does not exist.");
    }

    private DefinitionStoreError? ValidateKeymap(KeymapProfile definition)
    {
        var duplicate = ValidateName(Snapshot.Keymaps, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var issues = KeymapConflictValidator.Validate(definition, BuiltInCommands.Registry);
        var errors = issues.Where(issue => issue.Severity == KeymapIssueSeverity.Error).ToArray();
        return errors.Length == 0
            ? null
            : Invalid(string.Join(" ", errors.Select(issue => issue.Message)));
    }

    private DefinitionStoreError? ValidateFileProviderProfile(FileProviderProfile definition)
    {
        var duplicate = ValidateName(Snapshot.FileProviderProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (definition.Configuration is not FileProviderConfiguration.Sftp sftp)
        {
            return null;
        }

        var connection = Snapshot.Connections
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == sftp.ConnectionId);
        return connection?.Endpoint is ConnectionEndpoint.Ssh
            ? null
            : new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "An SFTP provider requires an existing SSH connection profile.");
    }

    private DefinitionStoreError? ValidateQuickTerminalSettings(QuickTerminalSettings definition) =>
        ValidateName(Snapshot.QuickTerminalSettings, definition);

    private DefinitionStoreError? ValidateAiProviderProfile(AiProviderProfile definition)
    {
        var duplicate = ValidateName(Snapshot.AiProviderProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (!definition.IsEnabled
            && IsAiProviderPolicyDependency(definition.Id.Value))
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "This AI-provider profile cannot be disabled while a saved screen or workspace agent policy references it.");
        }

        return Snapshot.AiProviderProfiles.Any(item =>
            item.Value.Key != definition.Key
            && item.Value.Order == definition.Order)
            ? Invalid("Each AI provider must have a distinct display order.")
            : null;
    }

    private DefinitionStoreError? ValidateMcpServerProfile(McpServerProfile definition) =>
        ValidateName(Snapshot.McpServerProfiles, definition);

    private DefinitionStoreError? ValidateBrowserProfile(BrowserProfileDefinition definition)
    {
        var duplicate = ValidateName(Snapshot.BrowserProfiles, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        return definition.Id == BuiltInBrowserProfiles.Default.Id
            && (!definition.IsEnabled
                || definition.Persistence != BrowserProfilePersistence.DurableMetadata
                || definition.Authentication is not null)
            ? Invalid(
                "The built-in default browser profile must remain enabled, use durable metadata, and have no credential binding.")
            : null;
    }

    private DefinitionStoreError? ValidateDatabaseConnection(
        DatabaseConnectionProfile definition)
    {
        var duplicate = ValidateName(Snapshot.DatabaseConnections, definition);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var validation = definition.Validate();
        if (!validation.IsValid)
        {
            return Invalid(validation);
        }

        return definition.TunnelConnectionId is { } tunnelId
            && Snapshot.Connections.All(item => item.Value.Id != tunnelId)
            ? new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "The tunnel connection no longer exists.")
            : null;
    }

    private DefinitionStoreError? ValidateNetworkConnection(NetworkConnectionProfile definition) =>
        ValidateName(Snapshot.NetworkConnections, definition);

    private DefinitionStoreError? ValidateApplicationNetworkSettings(
        ApplicationNetworkSettings definition)
    {
        if (definition.Id != ApplicationNetworkSettings.DefaultId)
        {
            return Invalid("Application network settings must use the built-in identity.");
        }

        return ValidateNetworkPolicyDependencies(definition.Policy);
    }

    private DefinitionStoreError? ValidateNetworkPolicyDependencies(NetworkPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        var knownIds = Snapshot.NetworkConnections
            .Select(item => item.Value.Id)
            .ToHashSet();
        return policy.Connections.Any(id => !knownIds.Contains(id))
            ? new DefinitionStoreError(
                DefinitionStoreErrorCode.DependencyConflict,
                "The network policy references a network connection that does not exist.")
            : null;
    }

    private bool IsNetworkPolicyDependency(string connectionId) =>
        Snapshot.ApplicationNetworkSettings.Any(item =>
            item.Value.Policy.Connections.Any(id =>
                string.Equals(id.Value, connectionId, StringComparison.Ordinal)))
        || Snapshot.Workspaces.Any(item =>
            item.Value.NetworkOverride?.Connections.Any(id =>
                string.Equals(id.Value, connectionId, StringComparison.Ordinal)) == true);

    private static DefinitionStoreError? ValidateName<TDefinition>(
        IReadOnlyList<StoredDefinition<TDefinition>> definitions,
        TDefinition candidate)
        where TDefinition : IDurableDefinition =>
        definitions.Any(item =>
            item.Value.Key != candidate.Key
            && string.Equals(item.Value.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            ? Invalid("A definition with this name already exists.")
            : null;

    private async ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> RefreshCoreAsync(
        CancellationToken cancellationToken)
    {
        var connectionsTask = _connections.ListAsync(cancellationToken).AsTask();
        var layoutsTask = _layouts.ListAsync(cancellationToken).AsTask();
        var screensTask = _screens.ListAsync(cancellationToken).AsTask();
        var workspacesTask = _workspaces.ListAsync(cancellationToken).AsTask();
        var themesTask = _themes.ListAsync(cancellationToken).AsTask();
        var terminalsTask = _terminalProfiles.ListAsync(cancellationToken).AsTask();
        var keymapsTask = _keymaps.ListAsync(cancellationToken).AsTask();
        var fileProvidersTask = _fileProviderProfiles.ListAsync(cancellationToken).AsTask();
        var aiProvidersTask = _aiProviderProfiles.ListAsync(cancellationToken).AsTask();
        var mcpServersTask = _mcpServerProfiles.ListAsync(cancellationToken).AsTask();
        var quickTerminalTask = _quickTerminalSettings.ListAsync(cancellationToken).AsTask();
        var databaseConnectionsTask = _databaseConnections.ListAsync(cancellationToken).AsTask();
        var browserProfilesTask = _browserProfiles.ListAsync(cancellationToken).AsTask();
        var networkConnectionsTask = _networkConnections.ListAsync(cancellationToken).AsTask();
        var applicationNetworkSettingsTask = _applicationNetworkSettings
            .ListAsync(cancellationToken)
            .AsTask();
        await Task.WhenAll(
                connectionsTask,
                layoutsTask,
                screensTask,
                workspacesTask,
                themesTask,
                terminalsTask,
                keymapsTask,
                fileProvidersTask,
                aiProvidersTask,
                mcpServersTask,
                quickTerminalTask,
                databaseConnectionsTask,
                browserProfilesTask,
                networkConnectionsTask,
                applicationNetworkSettingsTask)
            .ConfigureAwait(false);

        var connections = await connectionsTask.ConfigureAwait(false);
        var layouts = await layoutsTask.ConfigureAwait(false);
        var screens = await screensTask.ConfigureAwait(false);
        var workspaces = await workspacesTask.ConfigureAwait(false);
        var themes = await themesTask.ConfigureAwait(false);
        var terminals = await terminalsTask.ConfigureAwait(false);
        var keymaps = await keymapsTask.ConfigureAwait(false);
        var fileProviders = await fileProvidersTask.ConfigureAwait(false);
        var aiProviders = await aiProvidersTask.ConfigureAwait(false);
        var mcpServers = await mcpServersTask.ConfigureAwait(false);
        var quickTerminal = await quickTerminalTask.ConfigureAwait(false);
        var databaseConnections = await databaseConnectionsTask.ConfigureAwait(false);
        var browserProfiles = await browserProfilesTask.ConfigureAwait(false);
        var networkConnections = await networkConnectionsTask.ConfigureAwait(false);
        var applicationNetworkSettings = await applicationNetworkSettingsTask.ConfigureAwait(false);

        var errors = new DefinitionStoreError?[]
        {
            connections.Error,
            layouts.Error,
            screens.Error,
            workspaces.Error,
            themes.Error,
            terminals.Error,
            keymaps.Error,
            fileProviders.Error,
            aiProviders.Error,
            mcpServers.Error,
            quickTerminal.Error,
            databaseConnections.Error,
            browserProfiles.Error,
            networkConnections.Error,
            applicationNetworkSettings.Error,
        };
        var error = errors.FirstOrDefault(item => item is not null);
        if (error is not null)
        {
            return DefinitionStoreResult<DefinitionCatalogSnapshot>.Failure(error);
        }

        var snapshot = new DefinitionCatalogSnapshot(
            connections.Value!,
            layouts.Value!,
            screens.Value!,
            workspaces.Value!,
            themes.Value!,
            terminals.Value!,
            keymaps.Value!,
            fileProviders.Value!,
            quickTerminal.Value!)
        {
            AiProviderProfiles = aiProviders.Value!,
            McpServerProfiles = mcpServers.Value!,
            DatabaseConnections = databaseConnections.Value!,
            BrowserProfiles = browserProfiles.Value!,
            NetworkConnections = networkConnections.Value!,
            ApplicationNetworkSettings = applicationNetworkSettings.Value!,
        };
        Volatile.Write(ref _snapshot, snapshot);
        return DefinitionStoreResult<DefinitionCatalogSnapshot>.Success(snapshot);
    }

    private async ValueTask<DefinitionStoreResult<Unit>> SeedDefaultsAsync(
        CancellationToken cancellationToken)
    {
        if (Snapshot.ApplicationNetworkSettings.All(item =>
                item.Value.Id != ApplicationNetworkSettings.DefaultId))
        {
            var saved = await _applicationNetworkSettings.SaveAsync(
                    ApplicationNetworkSettings.Default,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (Snapshot.BrowserProfiles.All(item =>
                item.Value.Id != BuiltInBrowserProfiles.Default.Id))
        {
            var saved = await _browserProfiles.SaveAsync(
                    BuiltInBrowserProfiles.Default,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        foreach (var keymap in BuiltInKeymaps.All)
        {
            var existing = Snapshot.Keymaps.SingleOrDefault(item => item.Value.Id == keymap.Id);
            if (existing is not null && BuiltInKeymapMatches(existing.Value, keymap))
            {
                continue;
            }

            var saved = await _keymaps.SaveAsync(
                    keymap,
                    existing?.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (!Snapshot.Themes.Any(item => item.Value.Id == _defaultTheme.Id))
        {
            var saved = await _themes.SaveAsync(
                    _defaultTheme,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (Snapshot.TerminalProfiles.Count == 0)
        {
            var terminal = CreateDefaultTerminalProfile();
            var saved = await _terminalProfiles.SaveAsync(terminal, null, cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        var migratedTerminal = await MigrateLegacyDefaultTerminalPaletteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!migratedTerminal.IsSuccess)
        {
            return migratedTerminal;
        }

        if (Snapshot.QuickTerminalSettings.Count == 0)
        {
            var saved = await _quickTerminalSettings.SaveAsync(
                    QuickTerminalSettings.Default,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return DefinitionStoreResult<Unit>.Failure(saved.Error!);
            }
        }

        if (Snapshot.Connections.Count != 0
            || Snapshot.Layouts.Count != 0
            || Snapshot.Screens.Count != 0
            || Snapshot.Workspaces.Count != 0)
        {
            // The always-present workspace exists whatever else the profile
            // holds — a profile that predates the guarantee gets it back here.
            if (Snapshot.Workspaces.Any(item =>
                    string.Equals(
                        item.Value.Id.Value,
                        WorkspaceDefinition.DefaultWorkspaceId,
                        StringComparison.Ordinal)))
            {
                return await MigrateLegacyDefaultWorkspaceAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var restored = await _workspaces.SaveAsync(
                    new WorkspaceDefinition(
                        new WorkspaceId(WorkspaceDefinition.DefaultWorkspaceId),
                        WorkspaceDefinition.CurrentSchemaVersion,
                        WorkspaceDefinition.DefaultWorkspaceName,
                        "Your local GhostSHELL workspace.",
                        null,
                        []),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return restored.IsSuccess
                ? DefinitionStoreResult<Unit>.Success(Unit.Value)
                : DefinitionStoreResult<Unit>.Failure(restored.Error!);
        }

        var defaults = CreateFirstRunDefinitions();
        var connectionResult = await _connections.SaveAsync(
                defaults.Connection,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!connectionResult.IsSuccess)
        {
            return DefinitionStoreResult<Unit>.Failure(connectionResult.Error!);
        }

        var layoutResult = await _layouts.SaveAsync(defaults.Layout, null, cancellationToken)
            .ConfigureAwait(false);
        if (!layoutResult.IsSuccess)
        {
            return DefinitionStoreResult<Unit>.Failure(layoutResult.Error!);
        }

        var screenResult = await _screens.SaveAsync(defaults.Screen, null, cancellationToken)
            .ConfigureAwait(false);
        if (!screenResult.IsSuccess)
        {
            return DefinitionStoreResult<Unit>.Failure(screenResult.Error!);
        }

        var workspaceResult = await _workspaces.SaveAsync(
                defaults.Workspace,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        return workspaceResult.IsSuccess
            ? DefinitionStoreResult<Unit>.Success(Unit.Value)
            : DefinitionStoreResult<Unit>.Failure(workspaceResult.Error!);
    }

    private static TerminalProfile CreateDefaultTerminalProfile()
    {
        var keymap = OperatingSystem.IsMacOS()
            ? BuiltInKeymaps.MacOsTerminalId
            : OperatingSystem.IsWindows()
                ? BuiltInKeymaps.WindowsTerminalId
                : BuiltInKeymaps.LinuxTerminalId;
        return new TerminalProfile(
            new TerminalProfileId(DefaultTerminalProfileId),
            "Default terminal",
            "JetBrains Mono",
            14,
            1.4,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            100_000,
            TerminalPalette.GhostShellDark,
            keymap);
    }

    private static bool BuiltInKeymapMatches(
        KeymapProfile stored,
        KeymapProfile current)
    {
        if (stored.Id != current.Id
            || !string.Equals(stored.Name, current.Name, StringComparison.Ordinal)
            || stored.Layer != current.Layer
            || stored.Prefix != current.Prefix
            || stored.BasedOn != current.BasedOn
            || stored.Bindings.Count != current.Bindings.Count)
        {
            return false;
        }

        return stored.Bindings.Zip(current.Bindings).All(pair =>
            pair.First.CommandId == pair.Second.CommandId
            && pair.First.Sequence.Equals(pair.Second.Sequence)
            && pair.First.Contexts == pair.Second.Contexts
            && pair.First.Arguments.Count == pair.Second.Arguments.Count
            && pair.First.Arguments.All(argument =>
                pair.Second.Arguments.TryGetValue(argument.Key, out var value)
                && string.Equals(argument.Value, value, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Renames the always-present workspace from what it used to be seeded as.
    ///
    /// Only that exact name is touched. A name the user chose — including
    /// choosing to call it "Default" — is a decision, and the migration has no
    /// business overwriting one; a profile still carrying the old seed value
    /// never made a decision at all.
    /// </summary>
    private async ValueTask<DefinitionStoreResult<Unit>>
        MigrateLegacyDefaultWorkspaceAsync(CancellationToken cancellationToken)
    {
        var stored = Snapshot.Workspaces.FirstOrDefault(item => string.Equals(item.Value.Id.Value, WorkspaceDefinition.DefaultWorkspaceId, StringComparison.Ordinal));
        if (stored is null)
        {
            return DefinitionStoreResult<Unit>.Success(Unit.Value);
        }

        var workspace = stored.Value;
        var hasLegacyName = string.Equals(
            workspace.Name,
            WorkspaceDefinition.LegacyDefaultWorkspaceName,
            StringComparison.Ordinal);
        // Older builds could not record accent provenance. The default id/name,
        // the old bronze value, and the missing explicit marker are the narrow
        // one-time fingerprint. Unrelated workspace customization must not keep
        // the accidental seed alive; a bronze selected after this migration is
        // written with HasExplicitAccent and survives later starts.
        var hasLegacySeedAccent = !workspace.HasExplicitAccent
            && IsKnownDefaultWorkspaceSeed(workspace)
            && string.Equals(
            workspace.Accent,
            ThemePreference.BronzeFallback.ToString(),
            StringComparison.OrdinalIgnoreCase);
        if (!hasLegacyName && !hasLegacySeedAccent)
        {
            return DefinitionStoreResult<Unit>.Success(Unit.Value);
        }

        var saved = await _workspaces.SaveAsync(
                new WorkspaceDefinition(
                    workspace.Id,
                    workspace.SchemaVersion,
                    hasLegacyName
                        ? WorkspaceDefinition.DefaultWorkspaceName
                        : workspace.Name,
                    workspace.Description,
                    hasLegacySeedAccent ? null : workspace.Accent,
                    workspace.Entries,
                    workspace.AgentPolicyOverride,
                    workspace.Icon,
                    workspace.AutoSave,
                    workspace.Color,
                    workspace.AgentPanelPinned,
                    workspace.TerminalMultiplexingOverride,
                    workspace.BrowserProfileOverride,
                    workspace.HasExplicitAccent,
                    workspace.IsIsolated,
                    workspace.IsolationMounts,
                    workspace.IsolationImageReference,
                    workspace.RunAgentInIsolation,
                    workspace.NetworkOverride),
                stored.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        return saved.IsSuccess
            ? DefinitionStoreResult<Unit>.Success(Unit.Value)
            : DefinitionStoreResult<Unit>.Failure(saved.Error!);
    }

    private static bool IsKnownDefaultWorkspaceSeed(WorkspaceDefinition workspace) =>
        string.Equals(
            workspace.Id.Value,
            WorkspaceDefinition.DefaultWorkspaceId,
            StringComparison.Ordinal)
        && (string.Equals(
                workspace.Name,
                WorkspaceDefinition.DefaultWorkspaceName,
                StringComparison.Ordinal)
            || string.Equals(
                workspace.Name,
                WorkspaceDefinition.LegacyDefaultWorkspaceName,
                StringComparison.Ordinal));

    private async ValueTask<DefinitionStoreResult<Unit>>
        MigrateLegacyDefaultTerminalPaletteAsync(CancellationToken cancellationToken)
    {
        var stored = Snapshot.TerminalProfiles.FirstOrDefault(item => string.Equals(item.Value.Id.Value, DefaultTerminalProfileId, StringComparison.Ordinal));
        if (stored is null
            || !string.Equals(
                stored.Value.Palette.Name,
                LegacyGhostShellDarkPalette.Name,
                StringComparison.Ordinal)
            || !stored.Value.Palette.Matches(LegacyGhostShellDarkPalette))
        {
            return DefinitionStoreResult<Unit>.Success(Unit.Value);
        }

        // The built-in profile is editable. Replace only the exact palette that
        // shipped before the neutral background; every other profile value stays
        // under user ownership.
        var profile = stored.Value;
        var migrated = new TerminalProfile(
            profile.Id,
            profile.Name,
            profile.FontFamily,
            profile.FontSize,
            profile.LineHeight,
            profile.CursorStyle,
            profile.CursorBlink,
            profile.ScrollbackLines,
            TerminalPalette.GhostShellDark,
            profile.KeymapId,
            clipboardPolicy: profile.ClipboardPolicy,
            linkPolicy: profile.LinkPolicy,
            imeEnabled: profile.ImeEnabled,
            shellIntegration: profile.ShellIntegration,
            bellMode: profile.BellMode,
            compatibility: profile.Compatibility);
        var saved = await _terminalProfiles.SaveAsync(
                migrated,
                stored.Revision,
                cancellationToken)
            .ConfigureAwait(false);
        return saved.IsSuccess
            ? DefinitionStoreResult<Unit>.Success(Unit.Value)
            : DefinitionStoreResult<Unit>.Failure(saved.Error!);
    }

    private static FirstRunDefinitions CreateFirstRunDefinitions()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("builtin.local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local terminal",
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            ["local"]);
        var slotId = new LayoutSlotId("main");
        var layout = new LayoutDefinition(
            new LayoutId("builtin.single-panel"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single panel",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    slotId,
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(320, 180)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("builtin.local-terminal"),
            ScreenDefinition.CurrentSchemaVersion,
            "Local terminal",
            "A local shell using the default terminal profile.",
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    slotId,
                    ScreenPanelKind.Terminal,
                    "Local terminal",
                    connection.Id,
                    PanelStartupBehavior.None),
            ],
            ["local"]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId(WorkspaceDefinition.DefaultWorkspaceId),
            WorkspaceDefinition.CurrentSchemaVersion,
            WorkspaceDefinition.DefaultWorkspaceName,
            "Your local GhostSHELL workspace.",
            null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("local-connection"),
                    connection.Id),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("local-screen"),
                    screen.Id),
            ]);
        return new FirstRunDefinitions(connection, layout, screen, workspace);
    }

    private static DefinitionStoreError MissingWorkspaceDependency(string kind, string id) =>
        new(
            DefinitionStoreErrorCode.DependencyConflict,
            $"The workspace references missing {kind} '{id}'.");

    private static DefinitionStoreError Invalid(DefinitionValidationResult validation) =>
        Invalid(string.Join(" ", validation.Issues.Select(issue => issue.Message)));

    private static DefinitionStoreError Invalid(string message) =>
        new(DefinitionStoreErrorCode.InvalidDefinition, message);

    public void Dispose() => _mutationGate.Dispose();

    private sealed record FirstRunDefinitions(
        ConnectionProfile Connection,
        LayoutDefinition Layout,
        ScreenDefinition Screen,
        WorkspaceDefinition Workspace);
}
