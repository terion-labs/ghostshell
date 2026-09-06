using System.Collections.Immutable;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class SqliteDefinitionRepositoryTests
{
    [Fact]
    public async Task LayoutSurvivesDatabaseRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = CreateLayoutRepository(temporary);
        var definition = DurableDefinitionFixtures.Layout();

        var saved = await repository.SaveAsync(definition, null, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(1, saved.Value!.Revision);

        await temporary.ReopenAsync();
        repository = CreateLayoutRepository(temporary);
        var loaded = await repository.GetAsync(definition.Key, CancellationToken.None);

        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(definition.Id, loaded.Value!.Value.Id);
        Assert.Equal(definition.Name, loaded.Value.Value.Name);
        Assert.Equal(definition.Grid, loaded.Value.Value.Grid);
        Assert.Equal(definition.Slots, loaded.Value.Value.Slots);
        Assert.Equal(1, loaded.Value.Revision);
    }

    [Fact]
    public async Task ConcurrentExpectedRevisionCanOnlyWinOnce()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = CreateLayoutRepository(temporary);
        var original = DurableDefinitionFixtures.Layout(name: "Initial");
        var saved = await repository.SaveAsync(original, null, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);

        var firstTask = repository.SaveAsync(
            DurableDefinitionFixtures.Layout(name: "First update"),
            1,
            CancellationToken.None).AsTask();
        var secondTask = repository.SaveAsync(
            DurableDefinitionFixtures.Layout(name: "Second update"),
            1,
            CancellationToken.None).AsTask();

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.IsSuccess);
        var conflict = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, conflict.Error!.Code);
        Assert.Equal(2, conflict.Error.CurrentRevision);
    }

    [Fact]
    public async Task DeleteRequiresCurrentRevision()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = CreateLayoutRepository(temporary);
        var definition = DurableDefinitionFixtures.Layout();
        Assert.True((await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await repository.SaveAsync(
            DurableDefinitionFixtures.Layout(name: "Updated"),
            1,
            CancellationToken.None)).IsSuccess);

        var stale = await repository.DeleteAsync(definition.Key, 1, CancellationToken.None);
        var deleted = await repository.DeleteAsync(definition.Key, 2, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.RevisionConflict, stale.Error!.Code);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
    }

    [Fact]
    public async Task InvalidLayoutIsRejectedBeforeItIsStored()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = CreateLayoutRepository(temporary);
        var invalid = new LayoutDefinition(
            new LayoutId("overlap"),
            LayoutDefinition.CurrentSchemaVersion,
            "Overlap",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("first"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("second"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(160, 100)),
            ]);

        var saved = await repository.SaveAsync(invalid, null, CancellationToken.None);
        var listed = await repository.ListAsync(CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, saved.Error!.Code);
        Assert.Empty(listed.Value!);
    }

    [Fact]
    public async Task ScreenWithMissingLayoutIsRejected()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);

        var saved = await repository.SaveAsync(
            DurableDefinitionFixtures.Screen(),
            null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, saved.Error!.Code);
    }

    [Fact]
    public async Task IntrinsicHomeFileProviderDoesNotRequireOrCreateAStoredDefinition()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        await AssertSavesAsync(temporary, layout);
        var panel = new ScreenPanelDefinition(
            new ScreenPanelId("home-files"),
            new LayoutSlotId("main"),
            ScreenPanelKind.FileViewer,
            "Home files",
            ConnectionId: null,
            PanelStartupBehavior.None,
            BuiltInFileProviders.HomeId);
        var screen = new ScreenDefinition(
            new ScreenId("home-files-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Home files",
            description: null,
            layout.Id,
            [panel]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("home-files-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Home files",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("home-files-tab"),
                    "Home files",
                    layout.Id,
                    [panel]),
            ]);

        await AssertSavesAsync(temporary, screen);
        await AssertSavesAsync(temporary, workspace);
        var providers = await new SqliteDefinitionRepository<FileProviderProfile>(
                temporary.Database,
                TimeProvider.System)
            .ListAsync(CancellationToken.None);

        Assert.True(providers.IsSuccess, providers.Error?.Message);
        Assert.Empty(providers.Value!);
    }

    [Fact]
    public async Task StructurallyMalformedScreenReturnsTypedFailure()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);
        var malformed = new ScreenDefinition(
            new ScreenId("malformed-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Malformed",
            description: null,
            new LayoutId("layout-one"),
            [null!]);

        var saved = await repository.SaveAsync(
            malformed,
            null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, saved.Error!.Code);
    }

    [Fact]
    public async Task MalformedAgentPoliciesAreRejectedBeforeDependencyResolution()
    {
        await using var temporary = TemporaryDatabase.Create();
        var incompletePolicy = new AgentPolicy(
            "provider",
            "model",
            ImmutableDictionary<AgentCapability, AgentPermission>.Empty.Add(
                AgentCapability.RunCommands,
                (AgentPermission)999))
        {
            CompactionModel = new AgentModelSelection("provider", "model"),
            TitleModel = new AgentModelSelection("provider", "model"),
        };
        var screen = new ScreenDefinition(
            new ScreenId("policy-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Policy screen",
            description: null,
            new LayoutId("missing-layout"),
            [ConnectedPanel(new ConnectionId("missing-connection"), "policy-panel")],
            agentPolicyOverride: incompletePolicy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("policy-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Policy workspace",
            description: null,
            accent: null,
            entries: [],
            agentPolicyOverride: new AgentPolicy(
                " ",
                "model",
                AgentPolicy.Default.Permissions)
            {
                CompactionModel = new AgentModelSelection("provider", "model"),
                TitleModel = new AgentModelSelection("provider", "model"),
            });

        var screenResult = await new SqliteDefinitionRepository<ScreenDefinition>(
                temporary.Database,
                TimeProvider.System)
            .SaveAsync(screen, null, CancellationToken.None);
        var workspaceResult = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                temporary.Database,
                TimeProvider.System)
            .SaveAsync(workspace, null, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, screenResult.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, workspaceResult.Error!.Code);
    }

    [Fact]
    public async Task DurableAgentPoliciesRejectYoloBeforeDependencyResolution()
    {
        await using var temporary = TemporaryDatabase.Create();
        var yoloPolicy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };
        var screen = new ScreenDefinition(
            new ScreenId("yolo-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "YOLO screen",
            description: null,
            new LayoutId("missing-layout"),
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    PanelStartupBehavior.None),
            ],
            agentPolicyOverride: yoloPolicy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("yolo-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "YOLO workspace",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("missing-screen-entry"),
                    new ScreenId("missing-screen")),
            ],
            agentPolicyOverride: yoloPolicy);

        var screenResult = await new SqliteDefinitionRepository<ScreenDefinition>(
                temporary.Database,
                TimeProvider.System)
            .SaveAsync(screen, null, CancellationToken.None);
        var workspaceResult = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                temporary.Database,
                TimeProvider.System)
            .SaveAsync(workspace, null, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, screenResult.Error!.Code);
        Assert.Contains("YOLO", screenResult.Error.Message, StringComparison.Ordinal);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, workspaceResult.Error!.Code);
        Assert.Contains("YOLO", workspaceResult.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaOneAgentPoliciesUsingOffAskAndAutoRoundTrip()
    {
        await using var temporary = TemporaryDatabase.Create();
        var policy = AgentPolicy.Default;
        Assert.Equal(
            [AgentPermission.Off, AgentPermission.Ask, AgentPermission.Auto],
            policy.Permissions.Values.Distinct().Order().ToArray());
        var layout = DurableDefinitionFixtures.Layout(
            id: "policy-layout",
            name: "Policy layout");
        var provider = DurableDefinitionFixtures.AiProvider(
            policy.Provider,
            "Policy provider",
            order: 0);
        var screen = new ScreenDefinition(
            new ScreenId("policy-round-trip-screen"),
            schemaVersion: 1,
            "Policy round-trip screen",
            description: null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("terminal"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.Terminal,
                    Title: null,
                    ConnectionId: null,
                    PanelStartupBehavior.None),
            ],
            agentPolicyOverride: policy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("policy-round-trip-workspace"),
            schemaVersion: 1,
            "Policy round-trip workspace",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("screen-entry"),
                    screen.Id),
            ],
            agentPolicyOverride: policy);
        await AssertSavesAsync(temporary, provider);
        await AssertSavesAsync(temporary, layout);
        await AssertSavesAsync(temporary, screen);
        await AssertSavesAsync(temporary, workspace);

        await temporary.ReopenAsync();

        var restoredScreen = await new SqliteDefinitionRepository<ScreenDefinition>(
                temporary.Database,
                TimeProvider.System)
            .GetAsync(screen.Key, CancellationToken.None);
        var restoredWorkspace = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                temporary.Database,
                TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None);

        Assert.True(restoredScreen.IsSuccess, restoredScreen.Error?.Message);
        Assert.True(restoredWorkspace.IsSuccess, restoredWorkspace.Error?.Message);
        Assert.Equal(1, restoredScreen.Value!.Value.SchemaVersion);
        Assert.Equal(1, restoredWorkspace.Value!.Value.SchemaVersion);
        foreach (var restoredPolicy in new[]
                 {
                     restoredScreen.Value.Value.AgentPolicyOverride,
                     restoredWorkspace.Value.Value.AgentPolicyOverride,
                 })
        {
            Assert.NotNull(restoredPolicy);
            Assert.Equal(policy.Provider, restoredPolicy.Provider);
            Assert.Equal(policy.Model, restoredPolicy.Model);
            Assert.Equal(
                policy.Permissions.OrderBy(item => item.Key).ToArray(),
                [.. restoredPolicy.Permissions.OrderBy(item => item.Key)]);
        }
    }

    [Fact]
    public async Task KeymapConflictValidatorRunsBeforePersistence()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<KeymapProfile>(
            temporary.Database,
            TimeProvider.System);
        var sequence = KeySequence.Of(new KeyStroke("K", KeyModifiers.Control));
        var keymap = new KeymapProfile(
            new KeymapProfileId("conflicting-keymap"),
            "Conflicting keymap",
            KeymapLayer.Application,
            [
                new CommandBinding(
                    new CommandId("app.first"),
                    sequence,
                    CommandContext.Global),
                new CommandBinding(
                    new CommandId("app.second"),
                    sequence,
                    CommandContext.Global),
            ]);

        var saved = await repository.SaveAsync(
            keymap,
            null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, saved.Error!.Code);
    }

    [Fact]
    public async Task KeymapDefaultStructValuesAreRejectedBeforePersistence()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<KeymapProfile>(
            temporary.Database,
            TimeProvider.System);
        var keymap = new KeymapProfile(
            new KeymapProfileId("invalid-defaults"),
            "Invalid defaults",
            KeymapLayer.Application,
            [
                new CommandBinding(
                    default,
                    KeySequence.Of(new KeyStroke("K", KeyModifiers.Control)),
                    CommandContext.Global),
            ],
            new PrefixConfiguration(
                default,
                TimeSpan.FromSeconds(1),
                repeatable: false,
                FailedSequenceBehavior.DiscardAndShowHint));

        var saved = await repository.SaveAsync(
            keymap,
            null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, saved.Error!.Code);
    }

    [Fact]
    public async Task TerminalRequiresTerminalLayerKeymapAndProtectsItFromInvalidReplacement()
    {
        await using var temporary = TemporaryDatabase.Create();
        var keymaps = new SqliteDefinitionRepository<KeymapProfile>(
            temporary.Database,
            TimeProvider.System);
        var terminals = new SqliteDefinitionRepository<TerminalProfile>(
            temporary.Database,
            TimeProvider.System);
        var applicationKeymap = new KeymapProfile(
            new KeymapProfileId("application-map"),
            "Application map",
            KeymapLayer.Application,
            bindings: []);
        Assert.True((await keymaps.SaveAsync(
            applicationKeymap,
            null,
            CancellationToken.None)).IsSuccess);
        var wrongLayer = await terminals.SaveAsync(
            Terminal("wrong-layer-terminal", applicationKeymap.Id),
            null,
            CancellationToken.None);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, wrongLayer.Error!.Code);

        var terminalKeymap = new KeymapProfile(
            new KeymapProfileId("terminal-map"),
            "Terminal map",
            KeymapLayer.Terminal,
            bindings: []);
        Assert.True((await keymaps.SaveAsync(
            terminalKeymap,
            null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await terminals.SaveAsync(
            Terminal("terminal-one", terminalKeymap.Id),
            null,
            CancellationToken.None)).IsSuccess);

        var replaced = await keymaps.SaveAsync(
            new KeymapProfile(
                terminalKeymap.Id,
                terminalKeymap.Name,
                KeymapLayer.Application,
                bindings: []),
            1,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, replaced.Error!.Code);
        var stored = await keymaps.GetAsync(terminalKeymap.Key, CancellationToken.None);
        Assert.Equal(KeymapLayer.Terminal, stored.Value!.Value.Layer);
        Assert.Equal(1, stored.Value.Revision);
    }

    [Fact]
    public async Task ReferencedLayoutCannotBeDeleted()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var layouts = CreateLayoutRepository(temporary);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await layouts.SaveAsync(layout, null, CancellationToken.None)).IsSuccess);
        Assert.True((await screens.SaveAsync(
            DurableDefinitionFixtures.Screen(),
            null,
            CancellationToken.None)).IsSuccess);

        var deleted = await layouts.DeleteAsync(layout.Key, 1, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, deleted.Error!.Code);
        Assert.True((await layouts.GetAsync(layout.Key, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task LayoutUpdateCannotInvalidateAnExistingScreen()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = CreateLayoutRepository(temporary);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await layouts.SaveAsync(
            DurableDefinitionFixtures.Layout(),
            null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await screens.SaveAsync(
            DurableDefinitionFixtures.Screen(),
            null,
            CancellationToken.None)).IsSuccess);

        var updated = await layouts.SaveAsync(
            DurableDefinitionFixtures.Layout(slotId: "replacement"),
            1,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, updated.Error!.Code);
        var stored = await layouts.GetAsync(
            DurableDefinitionFixtures.Layout().Key,
            CancellationToken.None);
        Assert.Equal("main", Assert.Single(stored.Value!.Value.Slots).Id.Value);
        Assert.Equal(1, stored.Value.Revision);
    }

    [Fact]
    public async Task LayoutUpdateCannotInvalidateAnExistingWorkspaceTab()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = CreateLayoutRepository(temporary);
        Assert.True((await layouts.SaveAsync(
            DurableDefinitionFixtures.Layout(),
            null,
            CancellationToken.None)).IsSuccess);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-tab"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Workspace tab",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("tab-one"),
                    "Tab one",
                    new LayoutId("layout-one"),
                    [
                        new ScreenPanelDefinition(
                            new ScreenPanelId("panel-one"),
                            new LayoutSlotId("main"),
                            ScreenPanelKind.Terminal,
                            Title: null,
                            ConnectionId: null,
                            PanelStartupBehavior.None),
                    ]),
            ]);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await workspaces.SaveAsync(
            workspace,
            null,
            CancellationToken.None)).IsSuccess);

        var updated = await layouts.SaveAsync(
            DurableDefinitionFixtures.Layout(slotId: "replacement"),
            1,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, updated.Error!.Code);
    }

    [Fact]
    public async Task CorruptStoredTimestampReturnsTypedFailure()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = CreateLayoutRepository(temporary);
        var definition = DurableDefinitionFixtures.Layout();
        Assert.True((await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                UPDATE definitions
                SET created_utc = 'not-a-timestamp'
                WHERE kind = $kind AND id = $id;
                """;
            corrupt.Parameters.AddWithValue("$kind", definition.Key.Kind.Value);
            corrupt.Parameters.AddWithValue("$id", definition.Key.Value);
            await corrupt.ExecuteNonQueryAsync();
        }

        var loaded = await repository.GetAsync(definition.Key, CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.StorageFailure, loaded.Error!.Code);
    }

    [Fact]
    public async Task StoredPayloadWhoseIdentityDiffersFromEnvelopeReturnsTypedFailure()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = CreateLayoutRepository(temporary);
        var definition = DurableDefinitionFixtures.Layout();
        Assert.True((await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                UPDATE definitions
                SET name = 'Envelope mismatch'
                WHERE kind = $kind AND id = $id;
                """;
            corrupt.Parameters.AddWithValue("$kind", definition.Key.Kind.Value);
            corrupt.Parameters.AddWithValue("$id", definition.Key.Value);
            await corrupt.ExecuteNonQueryAsync();
        }

        var loaded = await repository.GetAsync(definition.Key, CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, loaded.Error!.Code);
    }

    [Fact]
    public async Task RepositoryForUnknownDefinitionTypeFailsClosed()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<UnsupportedDefinition>(
            temporary.Database,
            TimeProvider.System);
        var definition = new UnsupportedDefinition("future-one");

        var saved = await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None);
        var listed = await repository.ListAsync(CancellationToken.None);
        var loaded = await repository.GetAsync(definition.Key, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.UnsupportedKind, saved.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.UnsupportedKind, listed.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.UnsupportedKind, loaded.Error!.Code);
    }

    [Fact]
    public async Task SaveIsUnaffectedByCallerCollectionMutation()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var connectionA = LocalConnection("connection-a");
        var connectionB = LocalConnection("connection-b");
        await AssertSavesAsync(temporary, layout);
        await AssertSavesAsync(temporary, connectionA);
        await AssertSavesAsync(temporary, connectionB);
        ScreenPanelDefinition[] sourcePanels = [ConnectedPanel(connectionA.Id, "screen-panel")];
        var screen = new ScreenDefinition(
            new ScreenId("mutable-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Mutable screen",
            description: null,
            layout.Id,
            sourcePanels);
        var mutatingTime = new MutatingTimeProvider(() =>
            sourcePanels[0] = ConnectedPanel(connectionB.Id, "screen-panel"));
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            mutatingTime);

        var saved = await screens.SaveAsync(screen, null, CancellationToken.None);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(connectionA.Id, Assert.Single(screen.Panels).ConnectionId);
        Assert.Equal(connectionA.Id, Assert.Single(saved.Value!.Value.Panels).ConnectionId);
        var connections = new SqliteDefinitionRepository<ConnectionProfile>(
            temporary.Database,
            TimeProvider.System);
        var referenced = await connections.DeleteAsync(
            connectionA.Key,
            1,
            CancellationToken.None);
        var unreferenced = await connections.DeleteAsync(
            connectionB.Key,
            1,
            CancellationToken.None);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, referenced.Error!.Code);
        Assert.True(unreferenced.IsSuccess, unreferenced.Error?.Message);
    }

    [Fact]
    public async Task EveryKnownDefinitionKindRoundTripsThroughStrictJson()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layout = DurableDefinitionFixtures.Layout();
        var connection = LocalConnection("local-one");
        var screen = new ScreenDefinition(
            new ScreenId("screen-one"),
            ScreenDefinition.CurrentSchemaVersion,
            "Screen One",
            description: null,
            layout.Id,
            [ConnectedPanel(connection.Id, "screen-panel")]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("workspace-one"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Workspace",
            description: null,
            accent: null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("connection-entry"),
                    connection.Id),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("screen-entry"),
                    screen.Id),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("tab-entry"),
                    "Tab",
                    layout.Id,
                    [ConnectedPanel(connection.Id, "tab-panel")]),
            ]);
        var baseKeymap = new KeymapProfile(
            new KeymapProfileId("base-keymap"),
            "Base keymap",
            KeymapLayer.Terminal,
            bindings: []);
        var keymap = new KeymapProfile(
            new KeymapProfileId("keymap-one"),
            "Keymap",
            KeymapLayer.Terminal,
            bindings: [],
            basedOn: baseKeymap.Id);
        var terminal = new TerminalProfile(
            new TerminalProfileId("terminal-one"),
            "Terminal",
            "monospace",
            13,
            1.2,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            10_000,
            TerminalPalette.GhostShellDark,
            keymap.Id);
        var theme = new ThemePreference(
            ThemePreference.Default.Id,
            ThemePreference.Default.Name,
            AppearanceMode.System,
            PlatformProfile.Automatic,
            AccentPreference.FollowHost,
            textScaleOverride: 2);
        var fileProvider = new FileProviderProfile(
            new FileProviderProfileId("files-local"),
            FileProviderProfile.CurrentSchemaVersion,
            "Local files",
            new FileProviderConfiguration.Local("/tmp"));
        var quickTerminal = QuickTerminalSettings.Default;

        await AssertSavesAsync(temporary, layout);
        await AssertSavesAsync(temporary, connection);
        await AssertSavesAsync(temporary, screen);
        await AssertSavesAsync(temporary, workspace);
        await AssertSavesAsync(temporary, baseKeymap);
        await AssertSavesAsync(temporary, keymap);
        await AssertSavesAsync(temporary, terminal);
        await AssertSavesAsync(temporary, theme);
        await AssertSavesAsync(temporary, fileProvider);
        await AssertSavesAsync(temporary, quickTerminal);

        await temporary.ReopenAsync();

        await AssertLoadsAsync(temporary, layout);
        await AssertLoadsAsync(temporary, screen);
        await AssertLoadsAsync(temporary, connection);
        await AssertLoadsAsync(temporary, workspace);
        await AssertLoadsAsync(temporary, baseKeymap);
        await AssertLoadsAsync(temporary, keymap);
        await AssertLoadsAsync(temporary, terminal);
        await AssertLoadsAsync(temporary, theme);
        await AssertLoadsAsync(temporary, fileProvider);
        await AssertLoadsAsync(temporary, quickTerminal);
    }

    [Fact]
    public async Task Workspace_isolation_round_trips_through_the_profile_database()
    {
        await using var temporary = TemporaryDatabase.Create();
        var isolationMount = new WorkspaceIsolationMountDefinition(
            Path.Combine(Path.GetTempPath(), "ghostshell-sqlite"),
            "/workspace",
            IsReadOnly: true);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("isolated-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Isolated workspace",
            description: null,
            accent: null,
            entries: [],
            isIsolated: true,
            isolationMounts: [isolationMount]);
        var repository = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database,
            TimeProvider.System);

        var saved = await repository.SaveAsync(workspace, null, CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
        await temporary.ReopenAsync();

        var restored = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                temporary.Database,
                TimeProvider.System)
            .GetAsync(workspace.Key, CancellationToken.None);

        Assert.True(restored.IsSuccess, restored.Error?.Message);
        Assert.True(restored.Value!.Value.IsIsolated);
        Assert.Equal([isolationMount], restored.Value.Value.IsolationMounts);
    }

    [Fact]
    public async Task SftpProviderProtectsItsSshConnectionDependency()
    {
        await using var temporary = TemporaryDatabase.Create();
        var connections = new SqliteDefinitionRepository<ConnectionProfile>(
            temporary.Database,
            TimeProvider.System);
        var providers = new SqliteDefinitionRepository<FileProviderProfile>(
            temporary.Database,
            TimeProvider.System);
        var ssh = new ConnectionProfile(
            new ConnectionId("ssh-files"),
            ConnectionProfile.CurrentSchemaVersion,
            "SSH files",
            new ConnectionEndpoint.Ssh("files.example.test", username: "operator"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.EnabledEvery(TimeSpan.FromSeconds(30)),
            SshHostKeyPolicy.Strict);
        Assert.True((await connections.SaveAsync(
            ssh,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var provider = new FileProviderProfile(
            new FileProviderProfileId("files-sftp"),
            FileProviderProfile.CurrentSchemaVersion,
            "SFTP files",
            new FileProviderConfiguration.Sftp(ssh.Id));
        Assert.True((await providers.SaveAsync(
            provider,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);

        var changedToLocal = new ConnectionProfile(
            ssh.Id,
            ConnectionProfile.CurrentSchemaVersion,
            ssh.Name,
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var replacement = await connections.SaveAsync(
            changedToLocal,
            expectedRevision: 1,
            CancellationToken.None);
        var deletion = await connections.DeleteAsync(
            ssh.Key,
            expectedRevision: 1,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, replacement.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, deletion.Error!.Code);
        Assert.IsType<ConnectionEndpoint.Ssh>(
            (await connections.GetAsync(ssh.Key, CancellationToken.None)).Value!.Value.Endpoint);
    }

    [Fact]
    public async Task HostConnectionReferenceProtectsItsSshConnectionDependency()
    {
        await using var temporary = TemporaryDatabase.Create();
        var connections = new SqliteDefinitionRepository<ConnectionProfile>(
            temporary.Database,
            TimeProvider.System);
        var ssh = new ConnectionProfile(
            new ConnectionId("ssh-bastion"),
            ConnectionProfile.CurrentSchemaVersion,
            "Build bastion",
            new ConnectionEndpoint.Ssh("bastion.example.test", username: "operator"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);
        Assert.True((await connections.SaveAsync(
            ssh,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        static ConnectionProfile GitReference(string id, ConnectionId hostId) => new(
            new ConnectionId(id),
            ConnectionProfile.CurrentSchemaVersion,
            $"Repo {id}",
            ConnectionProfile.DelegatedSshEndpoint,
            new ConnectionAuthentication.None(),
            new ConnectionStartup("/repo/app"),
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict,
            preferredPanel: PanelKind.Git,
            hostConnectionId: hostId);
        var git = GitReference("git-repo", ssh.Id);
        Assert.True((await connections.SaveAsync(
            git,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);

        // A reference to a connection that does not exist is refused outright.
        var dangling = await connections.SaveAsync(
            GitReference("git-dangling", new ConnectionId("ssh-vanished")),
            expectedRevision: null,
            CancellationToken.None);
        // A reference to a delegating connection would chain; also refused.
        var chained = await connections.SaveAsync(
            GitReference("git-chained", git.Id),
            expectedRevision: null,
            CancellationToken.None);
        // Turning the referenced connection non-SSH would break its dependents.
        var changedToLocal = await connections.SaveAsync(
            new ConnectionProfile(
                ssh.Id,
                ConnectionProfile.CurrentSchemaVersion,
                ssh.Name,
                new ConnectionEndpoint.Local(),
                new ConnectionAuthentication.None(),
                ConnectionStartup.Default,
                ConnectionKeepAlive.Disabled,
                SshHostKeyPolicy.NotApplicable),
            expectedRevision: 1,
            CancellationToken.None);
        // The referenced connection is protected from deletion.
        var deletion = await connections.DeleteAsync(
            ssh.Key,
            expectedRevision: 1,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, dangling.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, chained.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, changedToLocal.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, deletion.Error!.Code);
        Assert.IsType<ConnectionEndpoint.Ssh>(
            (await connections.GetAsync(ssh.Key, CancellationToken.None)).Value!.Value.Endpoint);

        // Deleting the referencing profile first releases the protection.
        Assert.True((await connections.DeleteAsync(
            git.Key,
            expectedRevision: 1,
            CancellationToken.None)).IsSuccess);
        Assert.True((await connections.DeleteAsync(
            ssh.Key,
            expectedRevision: 1,
            CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task AiProviderFallbackOrderIsUniqueAcrossDirectRepositorySaves()
    {
        await using var temporary = TemporaryDatabase.Create();
        var providers = new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System);
        var primary = DurableDefinitionFixtures.AiProvider(
            "ai-primary",
            "Primary",
            order: 0);
        var duplicate = DurableDefinitionFixtures.AiProvider(
            "ai-duplicate",
            "Duplicate",
            order: 0);
        var fallback = DurableDefinitionFixtures.AiProvider(
            "ai-fallback",
            "Fallback",
            order: 1);

        var primaryResult = await providers.SaveAsync(
            primary,
            expectedRevision: null,
            CancellationToken.None);
        var duplicateResult = await providers.SaveAsync(
            duplicate,
            expectedRevision: null,
            CancellationToken.None);
        var fallbackResult = await providers.SaveAsync(
            fallback,
            expectedRevision: null,
            CancellationToken.None);

        Assert.True(primaryResult.IsSuccess, primaryResult.Error?.Message);
        Assert.Equal(DefinitionStoreErrorCode.InvalidDefinition, duplicateResult.Error!.Code);
        Assert.Contains("display order 0", duplicateResult.Error.Message, StringComparison.Ordinal);
        Assert.True(fallbackResult.IsSuccess, fallbackResult.Error?.Message);
        var stored = await providers.ListAsync(CancellationToken.None);
        Assert.True(stored.IsSuccess, stored.Error?.Message);
        Assert.Equal(
            [0, 1],
            [.. stored.Value!.Select(item => item.Value.Order).Order()]);
    }

    [Fact]
    public async Task AgentPolicyProviderReferencesAreGraphValidatedAndBlockDisableOrDelete()
    {
        await using var temporary = TemporaryDatabase.Create();
        var layouts = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var screens = new SqliteDefinitionRepository<ScreenDefinition>(
            temporary.Database,
            TimeProvider.System);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database,
            TimeProvider.System);
        var providers = new SqliteDefinitionRepository<AiProviderProfile>(
            temporary.Database,
            TimeProvider.System);
        var layout = DurableDefinitionFixtures.Layout("policy-layout", "Policy layout");
        var provider = DurableDefinitionFixtures.AiProvider(
            "ai-policy-provider",
            "Policy provider",
            order: 0);
        Assert.True((await layouts.SaveAsync(
            layout,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await providers.SaveAsync(
            provider,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var screenShape = DurableDefinitionFixtures.Screen(
            "policy-screen",
            "Policy screen",
            layout.Id.Value);
        var policy = AgentPolicy.Default with
        {
            Provider = provider.Id.Value,
            Model = "policy-model",
        };
        var screen = new ScreenDefinition(
            screenShape.Id,
            screenShape.SchemaVersion,
            screenShape.Name,
            screenShape.Description,
            screenShape.LayoutId,
            screenShape.Panels,
            screenShape.Tags,
            policy);
        var savedScreen = await screens.SaveAsync(
            screen,
            expectedRevision: null,
            CancellationToken.None);
        Assert.True(savedScreen.IsSuccess, savedScreen.Error?.Message);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("policy-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Policy workspace",
            description: null,
            accent: null,
            entries: [],
            agentPolicyOverride: policy);
        Assert.True((await workspaces.SaveAsync(
            workspace,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        var missingShape = DurableDefinitionFixtures.Screen(
            "missing-policy-screen",
            "Missing policy screen",
            layout.Id.Value);
        var missingScreen = new ScreenDefinition(
            missingShape.Id,
            missingShape.SchemaVersion,
            missingShape.Name,
            missingShape.Description,
            missingShape.LayoutId,
            missingShape.Panels,
            missingShape.Tags,
            policy with { Provider = "ai-missing" });

        var missing = await screens.SaveAsync(
            missingScreen,
            expectedRevision: null,
            CancellationToken.None);
        var missingWorkspace = new WorkspaceDefinition(
            new WorkspaceId("missing-policy-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Missing policy workspace",
            description: null,
            accent: null,
            entries: [],
            agentPolicyOverride: policy with { Provider = "ai-missing" });
        var missingWorkspaceResult = await workspaces.SaveAsync(
            missingWorkspace,
            expectedRevision: null,
            CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, missing.Error!.Code);
        Assert.Equal(
            DefinitionStoreErrorCode.DependencyConflict,
            missingWorkspaceResult.Error!.Code);
        var disabled = new AiProviderProfile(
            provider.Id,
            provider.SchemaVersion,
            provider.Name,
            provider.ProviderKind,
            provider.Endpoint,
            provider.Authentication,
            provider.DefaultModel,
            provider.Order,
            isEnabled: false);

        var disabledResult = await providers.SaveAsync(
            disabled,
            expectedRevision: 1,
            CancellationToken.None);
        var deleteResult = await providers.DeleteAsync(
            provider.Key,
            expectedRevision: 1,
            CancellationToken.None);

        Assert.Equal(
            DefinitionStoreErrorCode.DependencyConflict,
            disabledResult.Error!.Code);
        Assert.Equal(
            DefinitionStoreErrorCode.DependencyConflict,
            deleteResult.Error!.Code);
        Assert.True((await providers.GetAsync(
            provider.Key,
            CancellationToken.None)).Value!.Value.IsEnabled);
    }

    [Fact]
    public async Task NetworkPoliciesRoundTripAndProtectTheirConnections()
    {
        await using var temporary = TemporaryDatabase.Create();
        var connections = new SqliteDefinitionRepository<NetworkConnectionProfile>(
            temporary.Database,
            TimeProvider.System);
        var applicationSettings = new SqliteDefinitionRepository<ApplicationNetworkSettings>(
            temporary.Database,
            TimeProvider.System);
        var workspaces = new SqliteDefinitionRepository<WorkspaceDefinition>(
            temporary.Database,
            TimeProvider.System);
        var connection = NetworkConnection();
        var policy = new NetworkPolicy([connection.Id], connection.Id, true, true);
        var settings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            policy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("networked-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Networked workspace",
            description: null,
            accent: null,
            entries: [],
            networkOverride: policy);

        Assert.True((await connections.SaveAsync(
            connection,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await applicationSettings.SaveAsync(
            settings,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);
        Assert.True((await workspaces.SaveAsync(
            workspace,
            expectedRevision: null,
            CancellationToken.None)).IsSuccess);

        await temporary.ReopenAsync();

        connections = new(temporary.Database, TimeProvider.System);
        applicationSettings = new(temporary.Database, TimeProvider.System);
        workspaces = new(temporary.Database, TimeProvider.System);
        var restoredConnection = await connections.GetAsync(
            connection.Key,
            CancellationToken.None);
        var restoredSettings = await applicationSettings.GetAsync(
            settings.Key,
            CancellationToken.None);
        var restoredWorkspace = await workspaces.GetAsync(
            workspace.Key,
            CancellationToken.None);

        Assert.True(restoredConnection.IsSuccess, restoredConnection.Error?.Message);
        Assert.Equal(connection, restoredConnection.Value!.Value);
        Assert.True(restoredSettings.IsSuccess, restoredSettings.Error?.Message);
        AssertNetworkPolicy(policy, restoredSettings.Value!.Value.Policy);
        Assert.True(restoredWorkspace.IsSuccess, restoredWorkspace.Error?.Message);
        AssertNetworkPolicy(policy, restoredWorkspace.Value!.Value.NetworkOverride);

        var deleted = await connections.DeleteAsync(
            connection.Key,
            restoredConnection.Value.Revision,
            CancellationToken.None);

        Assert.False(deleted.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, deleted.Error!.Code);

        var replacementCredential = new SecretRef("vault.proxy.password.replaced");
        var updatedConnection = new NetworkConnectionProfile(
            connection.Id,
            connection.SchemaVersion,
            connection.Name,
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Https,
                "proxy.example.com",
                8443,
                "operator",
                replacementCredential));
        Assert.True((await connections.SaveAsync(
            updatedConnection,
            restoredConnection.Value.Revision,
            CancellationToken.None)).IsSuccess);

        await temporary.ReopenAsync();

        connections = new(temporary.Database, TimeProvider.System);
        applicationSettings = new(temporary.Database, TimeProvider.System);
        workspaces = new(temporary.Database, TimeProvider.System);
        restoredConnection = await connections.GetAsync(
            connection.Key,
            CancellationToken.None);
        restoredSettings = await applicationSettings.GetAsync(
            settings.Key,
            CancellationToken.None);
        restoredWorkspace = await workspaces.GetAsync(
            workspace.Key,
            CancellationToken.None);
        var reloadedProxy = Assert.IsType<NetworkConnectionConfiguration.Proxy>(
            restoredConnection.Value!.Value.Configuration);
        Assert.Equal(replacementCredential, reloadedProxy.PasswordSecret);

        var directSettings = new ApplicationNetworkSettings(
            settings.Id,
            settings.SchemaVersion,
            settings.Name,
            NetworkPolicy.Direct);
        var inheritedWorkspace = new WorkspaceDefinition(
            workspace.Id,
            workspace.SchemaVersion,
            workspace.Name,
            workspace.Description,
            workspace.Accent,
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
            networkOverride: null);
        Assert.True((await applicationSettings.SaveAsync(
            directSettings,
            restoredSettings.Value!.Revision,
            CancellationToken.None)).IsSuccess);
        Assert.True((await workspaces.SaveAsync(
            inheritedWorkspace,
            restoredWorkspace.Value!.Revision,
            CancellationToken.None)).IsSuccess);
        Assert.True((await connections.DeleteAsync(
            connection.Key,
            restoredConnection.Value.Revision,
            CancellationToken.None)).IsSuccess);

        await temporary.ReopenAsync();

        connections = new(temporary.Database, TimeProvider.System);
        applicationSettings = new(temporary.Database, TimeProvider.System);
        workspaces = new(temporary.Database, TimeProvider.System);
        var missingConnection = await connections.GetAsync(
            connection.Key,
            CancellationToken.None);
        var reloadedSettings = await applicationSettings.GetAsync(
            settings.Key,
            CancellationToken.None);
        var reloadedWorkspace = await workspaces.GetAsync(
            workspace.Key,
            CancellationToken.None);

        Assert.False(missingConnection.IsSuccess);
        Assert.Equal(DefinitionStoreErrorCode.NotFound, missingConnection.Error!.Code);
        Assert.Equal(NetworkPolicy.Direct, reloadedSettings.Value!.Value.Policy);
        Assert.Null(reloadedWorkspace.Value!.Value.NetworkOverride);
    }

    [Fact]
    public async Task NetworkPoliciesRejectMissingConnections()
    {
        await using var temporary = TemporaryDatabase.Create();
        var missingId = new NetworkConnectionId("network.missing");
        var policy = new NetworkPolicy([missingId], missingId, true, false);
        var applicationSettings = new ApplicationNetworkSettings(
            ApplicationNetworkSettings.DefaultId,
            ApplicationNetworkSettings.CurrentSchemaVersion,
            "Application networking",
            policy);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("missing-network-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Missing network workspace",
            description: null,
            accent: null,
            entries: [],
            networkOverride: policy);

        var settingsResult = await new SqliteDefinitionRepository<ApplicationNetworkSettings>(
                temporary.Database,
                TimeProvider.System)
            .SaveAsync(applicationSettings, null, CancellationToken.None);
        var workspaceResult = await new SqliteDefinitionRepository<WorkspaceDefinition>(
                temporary.Database,
                TimeProvider.System)
            .SaveAsync(workspace, null, CancellationToken.None);

        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, settingsResult.Error!.Code);
        Assert.Equal(DefinitionStoreErrorCode.DependencyConflict, workspaceResult.Error!.Code);
    }

    private static SqliteDefinitionRepository<LayoutDefinition> CreateLayoutRepository(
        TemporaryDatabase temporary) =>
        new(temporary.Database, TimeProvider.System);

    private static ConnectionProfile LocalConnection(string id) =>
        new(
            new ConnectionId(id),
            ConnectionProfile.CurrentSchemaVersion,
            id,
            new ConnectionEndpoint.Local(),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);

    private static NetworkConnectionProfile NetworkConnection() =>
        new(
            new NetworkConnectionId("network.proxy"),
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Office proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Https,
                "proxy.example.com",
                8443,
                "operator",
                new SecretRef("vault.proxy.password")));

    private static void AssertNetworkPolicy(NetworkPolicy expected, NetworkPolicy? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Connections, actual.Connections);
        Assert.Equal(expected.SelectedConnectionId, actual.SelectedConnectionId);
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.KillSwitchEnabled, actual.KillSwitchEnabled);
    }

    private static ScreenPanelDefinition ConnectedPanel(
        ConnectionId connectionId,
        string panelId) =>
        new(
            new ScreenPanelId(panelId),
            new LayoutSlotId("main"),
            ScreenPanelKind.Terminal,
            Title: null,
            connectionId,
            PanelStartupBehavior.None);

    private static TerminalProfile Terminal(string id, KeymapProfileId keymapId) =>
        new(
            new TerminalProfileId(id),
            id,
            "monospace",
            13,
            1.2,
            TerminalCursorStyle.Block,
            cursorBlink: true,
            10_000,
            TerminalPalette.GhostShellDark,
            keymapId);

    private static async Task AssertSavesAsync<TDefinition>(
        TemporaryDatabase temporary,
        TDefinition definition)
        where TDefinition : IDurableDefinition
    {
        var repository = new SqliteDefinitionRepository<TDefinition>(
            temporary.Database,
            TimeProvider.System);
        var saved = await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None);
        Assert.True(saved.IsSuccess, saved.Error?.Message);
    }

    private static async Task AssertLoadsAsync<TDefinition>(
        TemporaryDatabase temporary,
        TDefinition definition)
        where TDefinition : IDurableDefinition
    {
        var repository = new SqliteDefinitionRepository<TDefinition>(
            temporary.Database,
            TimeProvider.System);
        var loaded = await repository.GetAsync(definition.Key, CancellationToken.None);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
    }

    private sealed record UnsupportedDefinition(string Id) : IDurableDefinition
    {
        public static DefinitionKind Kind { get; } = new("future-kind");

        public DefinitionKey Key => new(Kind, Id);

        public int SchemaVersion => 1;

        public string Name => "Unsupported";
    }

    private sealed class MutatingTimeProvider : TimeProvider
    {
        private Action? _mutation;

        public MutatingTimeProvider(Action mutation) => _mutation = mutation;

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _mutation, null)?.Invoke();
            return DateTimeOffset.Parse(
                "2026-07-22T00:00:00Z",
                global::System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
