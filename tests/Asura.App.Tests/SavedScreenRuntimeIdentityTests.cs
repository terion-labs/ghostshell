using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.Application;
using Asura.Core;
using Asura.SessionHost;
using Asura.SessionHost.Tests;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Asura.App.Tests;

public sealed class SavedScreenRuntimeIdentityTests
{
    [Fact]
    public async Task New_terminal_policy_preserves_an_existing_runtime_opened_behind_settings()
    {
        var connection = LocalConnection("settings-terminal", "Settings terminal");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());
        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var workspaceId = runtime.Id;

        viewModel.ShowSettings();

        Assert.True(viewModel.HasRuntimeWorkspace);
        Assert.False(viewModel.IsWorkspaceVisible);
        Assert.Equal(
            CommandContext.None,
            viewModel.ActiveCommandContexts & CommandContext.Workspace);
        Assert.Equal(
            NewTerminalTarget.ExistingRuntimeWorkspace,
        MainWindow.ResolveNewTerminalTarget(viewModel.HasRuntimeWorkspace));

        viewModel.ShowWorkspace();
        Assert.True(await viewModel.AddLocalTerminalTabAsync());

        Assert.Equal(workspaceId, viewModel.RuntimeWorkspace?.Id);
        Assert.Equal(2, viewModel.RuntimeWorkspace?.Tabs.Count);
        Assert.True(viewModel.IsWorkspaceVisible);
        Assert.NotEqual(
            CommandContext.None,
            viewModel.ActiveCommandContexts & CommandContext.Workspace);
    }

    [Fact]
    public async Task Direct_new_terminal_mutation_cannot_close_a_dirty_layout_overlay()
    {
        var connection = LocalConnection("modal-terminal", "Modal terminal");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());
        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        viewModel.BeginCreateLayout();
        var editor = Assert.IsType<LayoutDesignerViewModel>(viewModel.LayoutDesignerEditor);
        editor.Name = "Unsaved layout";

        Assert.False(await viewModel.AddLocalTerminalTabAsync());

        Assert.Single(runtime.Tabs);
        Assert.Same(editor, viewModel.LayoutDesignerEditor);
        Assert.Equal(ShellOverlay.LayoutDesigner, viewModel.Overlay);
        Assert.Contains("overlay", viewModel.OperationError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A restored workspace is the workspace, not an anonymous copy of it.
    ///
    /// This is the reported bug: with session restore on, the workspace came
    /// back running, and clicking its own rail tile built a second one — its
    /// terminals replaced by fresh shells. The restore path set the runtime by
    /// hand, so the workspace was never in the open set and carried no record
    /// of the definition it came from.
    /// </summary>
    [Fact]
    public async Task A_restored_workspace_is_found_again_instead_of_rebuilt()
    {
        var connection = LocalConnection("restored-connection", "Connection");
        var workspace = WorkspaceOver(connection, "restored-workspace", "Restored");
        var definitions = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(workspace)],
            [], [], [], [], []);
        var files = new EmptyFileClients();

        var sourceStartup = InitializeRun("interrupted-run");
        var sourceStore = new RecordingRecoveryStore();
        var sourceWriter = new RuntimeRecoveryWriter(
            sourceStore,
            sourceStartup,
            TimeProvider.System);
        using (var source = CreateViewModel(definitions, files, sourceWriter))
        {
            Assert.True(await source.OpenWorkspaceAsync(workspace.Id));
            Assert.True((await sourceWriter.FlushAsync(CancellationToken.None)).IsSuccess);
        }

        var saved = sourceStore.Snapshots.Last();
        using var restored = CreateViewModel(
            definitions,
            files,
            new RuntimeRecoveryWriter(
                new RecordingRecoveryStore(),
                InitializeRun("recovery-run"),
                TimeProvider.System));

        Assert.True(await restored.RestoreRuntimeSnapshotsAsync([saved]));
        var runtime = restored.RuntimeWorkspace!;
        var panel = runtime.Tabs[0].Panels[0];

        // The rail knows it is running before anything is clicked.
        Assert.Single(restored.OpenWorkspaces);
        Assert.True(restored.Workspaces.Single(item => item.Id == workspace.Id).IsOpen);

        // And clicking its tile returns to it rather than building a second.
        Assert.True(await restored.OpenWorkspaceAsync(workspace.Id));
        Assert.Same(runtime, restored.RuntimeWorkspace);
        Assert.Same(panel, restored.RuntimeWorkspace!.Tabs[0].Panels[0]);
        Assert.Single(restored.OpenWorkspaces);
    }

    /// <summary>
    /// The same switch against the real session host rather than a fake.
    ///
    /// This exists because the bug survived two fixes. Both times the test
    /// below passed — it uses a session-client fake with no rules of its own,
    /// so it proved the client had stopped disposing panels and said nothing
    /// about the host removing the workspace underneath it. A test that cannot
    /// be wrong about the host cannot report on the host.
    ///
    /// It asserts the workspace graphs, not the sessions: a panel's session is
    /// established by its view, and there are no views here. Losing the graph
    /// is what killed the sessions, because the client closes a workspace the
    /// host says is gone.
    /// </summary>
    [Fact]
    public async Task Both_workspace_graphs_survive_a_switch_against_the_real_host()
    {
        await using var host = new InMemorySessionHostClient(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            TimeProvider.System);
        var connection = LocalConnection("survival-connection", "Connection");
        var first = WorkspaceOver(connection, "survival-first", "First");
        var second = WorkspaceOver(connection, "survival-second", "Second");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(first), Store(second)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            sessionClient: host);

        Assert.True(await viewModel.OpenWorkspaceAsync(first.Id));
        var firstRuntime = viewModel.RuntimeWorkspace!;
        var firstPanel = firstRuntime.Tabs[0].Panels[0];

        Assert.True(await viewModel.OpenWorkspaceAsync(second.Id));
        var secondRuntime = viewModel.RuntimeWorkspace!;

        // The host still knows both. Before this was fixed, registering the
        // second removed the first and told the client so, which is why every
        // session in the workspace you left died.
        await AssertGraphRegisteredAsync(host, firstRuntime.Id);
        await AssertGraphRegisteredAsync(host, secondRuntime.Id);

        // And the client still has the workspace it left, panels and all.
        Assert.Equal(2, viewModel.OpenWorkspaces.Count);
        Assert.Contains(firstPanel, firstRuntime.Tabs[0].Panels);

        Assert.True(await viewModel.OpenWorkspaceAsync(first.Id));
        Assert.Same(firstRuntime, viewModel.RuntimeWorkspace);
        await AssertGraphRegisteredAsync(host, firstRuntime.Id);
        await AssertGraphRegisteredAsync(host, secondRuntime.Id);
    }

    private static async Task AssertGraphRegisteredAsync(
        InMemorySessionHostClient host,
        WorkspaceInstanceId workspaceId)
    {
        var graph = await host.GetWorkspaceGraphAsync(
            workspaceId,
            OperationContext.ForHuman(new ClientId("survival-probe")),
            default);
        var registered = Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(graph);
        Assert.Equal(workspaceId, registered.Value.Workspace.Id);
    }

    /// <summary>
    /// Switching workspaces is changing view, not closing anything. The
    /// workspace left behind keeps the very same runtime — same instance, same
    /// tabs, same sessions — and coming back finds it rather than rebuilding it
    /// from the definition.
    /// </summary>
    [Fact]
    public async Task Switching_between_workspaces_keeps_both_alive()
    {
        var connection = LocalConnection("switch-connection", "Connection");
        var first = WorkspaceOver(connection, "switch-first", "First");
        var second = WorkspaceOver(connection, "switch-second", "Second");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(first), Store(second)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        Assert.True(await viewModel.OpenWorkspaceAsync(first.Id));
        var firstRuntime = viewModel.RuntimeWorkspace!;
        var firstPanel = firstRuntime.Tabs[0].Panels[0];
        var firstWorkspaceId = firstRuntime.Id;

        Assert.True(await viewModel.OpenWorkspaceAsync(second.Id));
        Assert.NotSame(firstRuntime, viewModel.RuntimeWorkspace);
        Assert.Equal(2, viewModel.OpenWorkspaces.Count);
        // The panel it left behind is still the panel, not a disposed shell of
        // one: this is what "my processes were killed" looked like.
        Assert.Contains(firstPanel, firstRuntime.Tabs[0].Panels);

        Assert.True(await viewModel.OpenWorkspaceAsync(first.Id));
        Assert.Same(firstRuntime, viewModel.RuntimeWorkspace);
        Assert.Same(firstPanel, viewModel.RuntimeWorkspace!.Tabs[0].Panels[0]);
        Assert.Equal(2, viewModel.OpenWorkspaces.Count);
        // Same instance throughout: it was never re-registered, so the host
        // never had cause to tear it down.
        Assert.Equal(firstWorkspaceId, firstRuntime.Id);
    }

    private static WorkspaceDefinition WorkspaceOver(
        ConnectionProfile connection,
        string id,
        string name) => new(
        new WorkspaceId(id),
        WorkspaceDefinition.CurrentSchemaVersion,
        name,
        null,
        null,
        [
            new WorkspaceEntry.ConnectionReference(
                new WorkspaceEntryId($"{id}-entry"),
                connection.Id),
        ]);

    /// <summary>
    /// The rails list saved definitions while "open" is a fact about running
    /// instances. Open and in-front are separate: several workspaces are alive
    /// at once and only one is the one you are looking at.
    /// </summary>
    [Fact]
    public async Task The_rail_says_which_workspaces_are_running_and_which_is_in_front()
    {
        var connection = LocalConnection("rail-connection", "Connection");
        var first = WorkspaceOver(connection, "rail-first", "First");
        var second = WorkspaceOver(connection, "rail-second", "Second");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(first), Store(second)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        LauncherWorkspaceViewModel Rail(WorkspaceId id) =>
            viewModel.Workspaces.Single(item => item.Id == id);

        Assert.All(viewModel.Workspaces, item => Assert.False(item.IsOpen));

        Assert.True(await viewModel.OpenWorkspaceAsync(first.Id));
        Assert.True(Rail(first.Id).IsOpen);
        Assert.True(Rail(first.Id).IsInFront);
        Assert.False(Rail(second.Id).IsOpen);

        Assert.True(await viewModel.OpenWorkspaceAsync(second.Id));
        // The first is still running — it just is not the one on screen.
        Assert.True(Rail(first.Id).IsOpen);
        Assert.False(Rail(first.Id).IsInFront);
        Assert.True(Rail(second.Id).IsInFront);
    }

    /// <summary>
    /// The window comes up on Main's launcher rather than the no-workspace
    /// empty state — but only when nothing with a better claim already has it.
    /// </summary>
    [Fact]
    public async Task An_idle_window_opens_Mains_launcher_and_leaves_a_restored_session_alone()
    {
        var connection = LocalConnection("startup-connection", "Connection");
        var main = new WorkspaceDefinition(
            new WorkspaceId(WorkspaceDefinition.DefaultWorkspaceId),
            WorkspaceDefinition.CurrentSchemaVersion,
            WorkspaceDefinition.DefaultWorkspaceName,
            null,
            null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("startup-entry"),
                    connection.Id),
            ]);
        var other = WorkspaceOver(connection, "startup-other", "Other");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(main), Store(other)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        Assert.True(await viewModel.OpenDefaultLauncherIfIdleAsync());
        var opened = viewModel.RuntimeWorkspace;
        Assert.NotNull(opened);
        Assert.True(viewModel.Workspaces.Single(item => item.Id == main.Id).IsInFront);
        var launcher = Assert.IsType<RuntimeTabViewModel>(opened.ActiveTab);
        Assert.IsType<PanelPlaceholderViewModel>(Assert.Single(launcher.Panels));

        // Something is already in the window, so this has no claim on it.
        Assert.True(await viewModel.OpenWorkspaceAsync(other.Id));
        Assert.False(await viewModel.OpenDefaultLauncherIfIdleAsync());
        Assert.NotSame(opened, viewModel.RuntimeWorkspace);
    }

    [Fact]
    public async Task An_empty_Main_workspace_opens_directly_on_its_launcher()
    {
        var main = new WorkspaceDefinition(
            new WorkspaceId(WorkspaceDefinition.DefaultWorkspaceId),
            WorkspaceDefinition.CurrentSchemaVersion,
            WorkspaceDefinition.DefaultWorkspaceName,
            null,
            null,
            []);
        var snapshot = new DefinitionCatalogSnapshot(
            [],
            [],
            [],
            [Store(main)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        Assert.True(await viewModel.OpenDefaultLauncherIfIdleAsync());

        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var launcher = Assert.Single(runtime.Tabs);
        Assert.Same(launcher, runtime.ActiveTab);
        Assert.IsType<PanelPlaceholderViewModel>(Assert.Single(launcher.Panels));
        Assert.Null(viewModel.OperationError);
    }

    [Fact]
    public async Task First_run_onboarding_opens_inside_Mains_launcher()
    {
        var main = new WorkspaceDefinition(
            new WorkspaceId(WorkspaceDefinition.DefaultWorkspaceId),
            WorkspaceDefinition.CurrentSchemaVersion,
            WorkspaceDefinition.DefaultWorkspaceName,
            null,
            null,
            []);
        var snapshot = new DefinitionCatalogSnapshot(
            [],
            [],
            [],
            [Store(main)],
            [], [], [], [], []);
        var onboarding = new OnboardingViewModel(
            new IncompleteOnboardingProgressStore(),
            new FixedDefinitionCatalog(snapshot),
            new SuccessfulConnectionRuntime(),
            new EmptySecretVault().Availability);
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            onboarding: onboarding);
        await onboarding.Initialization;
        Assert.True(onboarding.IsVisible);

        Assert.True(await viewModel.OpenDefaultLauncherIfIdleAsync());

        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        var launcher = Assert.Single(runtime.Tabs);
        Assert.Same(launcher, runtime.ActiveTab);
        Assert.IsType<PanelPlaceholderViewModel>(Assert.Single(launcher.Panels));
    }

    [Fact]
    public void Additional_windows_do_not_restart_process_onboarding()
    {
        var snapshot = new DefinitionCatalogSnapshot(
            [], [], [], [], [], [], [], [], []);
        var store = new IncompleteOnboardingProgressStore();
        var recentStore = new CountingRecentSessionStore();
        var onboarding = new OnboardingViewModel(
            store,
            new FixedDefinitionCatalog(snapshot),
            new SuccessfulConnectionRuntime(),
            new EmptySecretVault().Availability);

        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(recentStore),
            onboarding: onboarding,
            role: MainWindowRole.Additional);

        Assert.Equal(MainWindowRole.Additional, viewModel.Role);
        Assert.Null(viewModel.Onboarding);
        Assert.Equal(0, store.ReadCalls);
        Assert.Equal(0, recentStore.InitializationCount);
        Assert.Equal(0, recentStore.ListCount);
        onboarding.Dispose();
    }

    /// <summary>
    /// Which workspace you are in survives the workspace being saved.
    ///
    /// The rail's flags are derived, not stored, and a rail item is replaced
    /// whenever its definition changes — so saving the workspace you are
    /// working in handed the rail a fresh item with both flags cleared and
    /// nothing to put them back. Autosave does exactly that on every tab you
    /// add, which is how the shell forgot where you were mid-session.
    /// </summary>
    [Fact]
    public async Task Saving_the_open_workspace_does_not_lose_which_one_is_in_front()
    {
        var connection = LocalConnection("saved-connection", "Connection");
        var workspace = WorkspaceOver(connection, "saved-workspace", "Saved");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(workspace)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        Assert.True(await viewModel.OpenWorkspaceAsync(workspace.Id));
        Assert.True(viewModel.Workspaces.Single().IsInFront);

        // The same workspace at a later revision: what a save publishes.
        viewModel.RefreshCatalog(new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [new StoredDefinition<WorkspaceDefinition>(
                workspace,
                2,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch)],
            [], [], [], [], []));

        var railItem = viewModel.Workspaces.Single();
        Assert.True(railItem.IsOpen);
        Assert.True(railItem.IsInFront);
    }

    /// <summary>
    /// Closing the workspace in front puts you back in the one you were in
    /// before it — not the oldest one still running, which is what appending on
    /// first open would have given. The open set is kept in the order the
    /// workspaces were last looked at so that "the previous one" is a question
    /// it can answer.
    /// </summary>
    [Fact]
    public async Task Closing_the_workspace_in_front_returns_to_the_one_before_it()
    {
        var connection = LocalConnection("previous-connection", "Connection");
        var first = WorkspaceOver(connection, "previous-first", "First");
        var second = WorkspaceOver(connection, "previous-second", "Second");
        var third = WorkspaceOver(connection, "previous-third", "Third");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(first), Store(second), Store(third)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        Assert.True(await viewModel.OpenWorkspaceAsync(first.Id));
        Assert.True(await viewModel.OpenWorkspaceAsync(second.Id));
        var secondRuntime = viewModel.RuntimeWorkspace!;
        Assert.True(await viewModel.OpenWorkspaceAsync(third.Id));
        // Back to the second, so the second is now the most recent before the
        // third — and the first is the oldest, which is deliberately not it.
        Assert.True(await viewModel.OpenWorkspaceAsync(third.Id));
        Assert.True(await viewModel.OpenWorkspaceAsync(second.Id));
        Assert.True(await viewModel.OpenWorkspaceAsync(third.Id));
        var thirdRuntime = viewModel.RuntimeWorkspace!;

        viewModel.RemoveRuntimeWorkspace(thirdRuntime.Id);

        Assert.Same(secondRuntime, viewModel.RuntimeWorkspace);
        Assert.Equal(2, viewModel.OpenWorkspaces.Count);
        Assert.DoesNotContain(thirdRuntime, viewModel.OpenWorkspaces);
    }

    /// <summary>
    /// A workspace accent retints the shell while that workspace is open, and
    /// only while it is open. The view model cannot republish application
    /// resources, so what it owes the host is the announcement — and owing it
    /// on the way out matters as much as on the way in.
    /// </summary>
    [Fact]
    public async Task An_open_workspace_announces_its_accent_and_takes_it_back()
    {
        var connection = LocalConnection("accent-connection", "Connection");
        var accented = new WorkspaceDefinition(
            new WorkspaceId("accented"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Accented",
            null,
            "#5FA97A",
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("accent-entry"),
                    connection.Id),
            ]);
        var plain = new WorkspaceDefinition(
            new WorkspaceId("plain"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Plain",
            null,
            null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("plain-entry"),
                    connection.Id),
            ]);
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(accented), Store(plain)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());
        List<string?> announced = [];
        viewModel.WorkspaceAccentChanged += (_, accent) => announced.Add(accent);

        Assert.True(await viewModel.OpenWorkspaceAsync(accented.Id));
        Assert.Equal(["#5FA97A"], announced);

        var editedAccent = new WorkspaceDefinition(
            accented.Id,
            accented.SchemaVersion,
            accented.Name,
            accented.Description,
            "#35B779",
            accented.Entries);
        viewModel.RefreshCatalog(new DefinitionCatalogSnapshot(
            [Store(connection)],
            [],
            [],
            [Store(editedAccent), Store(plain)],
            [], [], [], [], []));
        Assert.Equal(["#5FA97A", "#35B779"], announced);

        Assert.True(await viewModel.OpenWorkspaceAsync(plain.Id));
        Assert.Equal(["#5FA97A", "#35B779", null], announced);
    }

    [Fact]
    public async Task Workspace_mixed_entry_order_opens_connection_screen_and_owned_tab()
    {
        var firstConnection = LocalConnection("workspace-first", "First connection");
        var secondConnection = LocalConnection("workspace-second", "Second connection");
        var layout = new LayoutDefinition(
            new LayoutId("workspace-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Single",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("workspace-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Saved screen",
            null,
            layout.Id,
            [TerminalPanel("screen-panel", firstConnection.Id)]);
        var workspace = new WorkspaceDefinition(
            new WorkspaceId("mixed-workspace"),
            WorkspaceDefinition.CurrentSchemaVersion,
            "Mixed workspace",
            null,
            null,
            [
                new WorkspaceEntry.ConnectionReference(
                    new WorkspaceEntryId("connection-entry"),
                    secondConnection.Id,
                    "Pinned connection"),
                new WorkspaceEntry.ScreenReference(
                    new WorkspaceEntryId("screen-entry"),
                    screen.Id,
                    "Pinned screen"),
                new WorkspaceEntry.Tab(
                    new WorkspaceEntryId("tab-entry"),
                    "Owned tab",
                    layout.Id,
                    [TerminalPanel("owned-panel", firstConnection.Id)]),
            ]);
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(firstConnection), Store(secondConnection)],
            [Store(layout)],
            [Store(screen)],
            [Store(workspace)],
            [], [], [], [], []);
        using var viewModel = CreateViewModel(snapshot, new EmptyFileClients());

        Assert.True(await viewModel.OpenWorkspaceAsync(workspace.Id));

        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await AwaitTerminalPanels(runtime);
        Assert.True(viewModel.IsDefinitionOpen(workspace.Key));
        Assert.False(viewModel.IsDefinitionOpen(screen.Key));
        Assert.Equal(
            ["Pinned connection", "Pinned screen", "Owned tab"],
            runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(
            [secondConnection.Id, firstConnection.Id, firstConnection.Id],
            runtime.Tabs.Select(tab =>
                Assert.IsType<TerminalRuntimePanelViewModel>(tab.ActivePanel).ConnectionId));
        Assert.Equal(
            [firstConnection.Id, secondConnection.Id],
            runtime.Connections.Select(connection => connection.Id).OrderBy(id => id.Value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OpeningTheSameDefinitionTwiceCreatesIndependentRuntimeIdentityGraphs()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("saved-screen-local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var layout = new LayoutDefinition(
            new LayoutId("saved-screen-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Two terminals",
            new LayoutGrid(2, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("right"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("saved-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Saved screen",
            null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("left-definition"),
                    new LayoutSlotId("left"),
                    ScreenPanelKind.Terminal,
                    "Left",
                    connection.Id,
                    PanelStartupBehavior.None),
                new ScreenPanelDefinition(
                    new ScreenPanelId("right-definition"),
                    new LayoutSlotId("right"),
                    ScreenPanelKind.Terminal,
                    "Right",
                    connection.Id,
                    PanelStartupBehavior.None),
            ]);
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [Store(layout)],
            [Store(screen)],
            [], [], [], [], [], []);
        var files = new EmptyFileClients();
        using var viewModel = new MainWindowViewModel(
            DispatchProxy.Create<ISessionHostClient, NullSessionClient>(),
            new FixedDefinitionCatalog(snapshot),
            new SuccessfulConnectionRuntime(),
            new EmptySecretVault(),
            files,
            files,
            new TerminalStartupCommandDispatcher(new SuccessfulAuditStore(), TimeProvider.System));

        Assert.True(await viewModel.OpenScreenAsync(screen.Id));
        var first = viewModel.RuntimeWorkspace!;
        await AwaitTerminalPanels(first);
        var firstIdentity = Capture(first);

        Assert.True(await viewModel.OpenScreenAsync(screen.Id));
        var second = viewModel.RuntimeWorkspace!;
        await AwaitTerminalPanels(second);
        var secondIdentity = Capture(second);

        Assert.NotEqual(firstIdentity.WorkspaceId, secondIdentity.WorkspaceId);
        Assert.Empty(firstIdentity.TabIds.Intersect(secondIdentity.TabIds));
        Assert.Empty(firstIdentity.PanelIds.Intersect(secondIdentity.PanelIds));
        Assert.Empty(firstIdentity.SessionIds.Intersect(secondIdentity.SessionIds));
        Assert.All(second.Tabs, tab => Assert.All(
            tab.Panels.OfType<TerminalRuntimePanelViewModel>(),
            panel =>
            {
                Assert.Equal(second.Id, panel.SessionRequest!.Owner.WorkspaceId);
                Assert.Equal(tab.Id, panel.SessionRequest.Owner.TabId);
                Assert.Equal(panel.Id, panel.SessionRequest.Owner.PanelId);
            }));
    }

    [Fact]
    public async Task SavedFileViewerStartsAtItsPersistedProviderLocation()
    {
        var layout = new LayoutDefinition(
            new LayoutId("saved-files-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Files",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var profileId = BuiltInFileProviders.HomeId;
        var root = new FilePanelLocation(
            profileId.Value,
            null,
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var expectedLocation = new FilePanelLocation(
            profileId.Value,
            null,
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(
                [
                    new FilePanelPathSegment("projects"),
                    new FilePanelPathSegment("asura"),
                ])));
        var screen = new ScreenDefinition(
            new ScreenId("saved-files-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Saved files",
            null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("saved-files-panel"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.FileViewer,
                    "Files",
                    null,
                    new PanelStartupBehavior("/projects/asura"),
                    profileId),
            ]);
        var snapshot = new DefinitionCatalogSnapshot(
            [],
            [Store(layout)],
            [Store(screen)],
            [], [], [], [], [], []);
        var files = new EmptyFileClients(
            [
                new FileProviderProfileDescriptor(
                    "files.distractor",
                    "Distractor",
                    FileProviderFamily.Posix,
                    new FilePanelLocation(
                        "files.distractor",
                        null,
                        new FilePanelAddress.Hierarchical(FilePanelPath.Root)),
                    FilePanelCapability.List,
                    250,
                    256 * 1024),
                new FileProviderProfileDescriptor(
                    profileId.Value,
                    "Home",
                    FileProviderFamily.Posix,
                    root,
                    FilePanelCapability.List,
                    250,
                    256 * 1024),
            ]);
        var sessionClient = DispatchProxy.Create<ISessionHostClient, NullSessionClient>();
        var host = (NullSessionClient)(object)sessionClient;
        host.FileListCompletion =
            new TaskCompletionSource<HostResult<FilePanelResult<FilePanelPage>>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = CreateViewModel(
            snapshot,
            files,
            sessionClient: sessionClient);

        Assert.True(await viewModel.OpenScreenAsync(screen.Id));
        var panel = Assert.IsType<FileRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        await WaitForFileListRequestAsync(host);
        Assert.True(panel.IsLoading);
        host.FileListCompletion.SetResult(NullSessionClient.SuccessfulFileList());
        await panel.Initialization;

        Assert.Equal(expectedLocation, panel.CurrentLocation);
        Assert.Equal(profileId.Value, panel.SelectedProfile?.Id);
        Assert.Null(panel.ContentIssue);
        Assert.False(panel.IsLoading);
        Assert.Equal(
            expectedLocation,
            Assert.Single(host.EnsureFilePanelRequests).InitialLocation);
        Assert.Equal(
            expectedLocation,
            Assert.Single(host.FileListRequests).Request.Location);
    }

    [Fact]
    public async Task SavedFileViewerWaitsForItsExactProviderAndBindsAfterCatalogRefresh()
    {
        var layout = new LayoutDefinition(
            new LayoutId("deferred-files-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Files",
            new LayoutGrid(1, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("main"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var profileId = new FileProviderProfileId("files.saved");
        var expectedLocation = new FilePanelLocation(
            profileId.Value,
            "saved-authority",
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(
                [
                    new FilePanelPathSegment("projects"),
                    new FilePanelPathSegment("asura"),
                ])));
        var screen = new ScreenDefinition(
            new ScreenId("deferred-files-screen"),
            ScreenDefinition.CurrentSchemaVersion,
            "Deferred files",
            null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("deferred-files-panel"),
                    new LayoutSlotId("main"),
                    ScreenPanelKind.FileViewer,
                    "Files",
                    null,
                    new PanelStartupBehavior("/projects/asura"),
                    profileId),
            ]);
        var files = new EmptyFileClients(
            [
                new FileProviderProfileDescriptor(
                    "files.other",
                    "Other provider",
                    FileProviderFamily.Posix,
                    new FilePanelLocation(
                        "files.other",
                        "other-authority",
                        new FilePanelAddress.Hierarchical(FilePanelPath.Root)),
                    FilePanelCapability.List,
                    250,
                    256 * 1024),
            ]);
        var sessionClient = DispatchProxy.Create<ISessionHostClient, NullSessionClient>();
        var host = (NullSessionClient)(object)sessionClient;
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot(
                [],
                [Store(layout)],
                [Store(screen)],
                [], [], [], [], [], []),
            files,
            sessionClient: sessionClient);

        Assert.True(await viewModel.OpenScreenAsync(screen.Id));
        var panel = Assert.IsType<FileRuntimePanelViewModel>(
            viewModel.RuntimeWorkspace!.ActiveTab!.ActivePanel);
        await panel.Initialization;

        Assert.Null(panel.SelectedProfile);
        Assert.Empty(host.EnsureFilePanelRequests);
        Assert.Contains(
            "not currently available",
            panel.ContentIssue?.Message,
            StringComparison.Ordinal);

        await panel.SelectProfileAsync(Assert.Single(panel.Profiles));
        Assert.Null(panel.SelectedProfile);
        Assert.Empty(host.EnsureFilePanelRequests);
        Assert.Contains(
            "will not substitute",
            panel.OperationIssue?.Message,
            StringComparison.Ordinal);

        var savedProfile = new FileProviderProfileDescriptor(
            profileId.Value,
            "Saved provider",
            FileProviderFamily.Posix,
            new FilePanelLocation(
                profileId.Value,
                "saved-authority",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root)),
            FilePanelCapability.List,
            250,
            256 * 1024);
        host.FileEnsureCompletion =
            new TaskCompletionSource<HostResult<SessionSnapshot>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        files.AddProfile(savedProfile);
        await WaitForFileEnsureRequestAsync(host);
        Assert.False(panel.CanSelectProfile);
        Assert.False(panel.CanEditLocation);
        Assert.False(panel.CanNavigateUp);
        panel.LocationText = "/";
        await panel.NavigateFromTextAsync();
        await panel.NavigateUpAsync();
        Assert.Equal(expectedLocation, panel.CurrentLocation);
        Assert.Equal("/projects/asura", panel.LocationText);
        Assert.Single(host.EnsureFilePanelRequests);
        Assert.Contains(
            "exact saved startup location",
            panel.OperationIssue?.Message,
            StringComparison.Ordinal);

        files.ReplaceProfiles(
            [.. files.Profiles.Select(profile => string.Equals(profile.Id, profileId.Value
, StringComparison.Ordinal) ? new FileProviderProfileDescriptor(
                        profile.Id,
                        "Refreshed saved provider",
                        profile.Family,
                        profile.Root,
                        profile.Capabilities,
                        profile.MaximumPageSize,
                        profile.MaximumPreviewBytes)
                    : profile)]);
        var delayedEnsure = host.FileEnsureCompletion!;
        host.FileEnsureCompletion = null;
        delayedEnsure.SetResult(HostResult<SessionSnapshot>.Fail(
            HostError.Create(
                HostErrorCode.EngineFailed,
                "The delayed first bind failed."),
            currentRevision: 0));
        await WaitForFileListCompletionAsync(host, panel);

        Assert.Equal(profileId.Value, panel.SelectedProfile?.Id);
        Assert.Equal(expectedLocation, panel.CurrentLocation);
        Assert.Null(panel.ContentIssue);
        Assert.True(panel.CanSelectProfile);
        Assert.True(panel.CanEditLocation);
        Assert.True(panel.CanNavigateUp);
        Assert.Equal(2, host.EnsureFilePanelRequests.Count);
        Assert.All(
            host.EnsureFilePanelRequests,
            request => Assert.Equal(expectedLocation, request.InitialLocation));
        Assert.Equal(
            expectedLocation,
            Assert.Single(host.FileListRequests).Request.Location);
    }

    [Fact]
    public async Task LaunchingSavedScreenTwiceAppendsIndependentTabsWithExactHistorySources()
    {
        var initialConnection = LocalConnection(
            "screen-append-initial",
            "Initial connection");
        var screenConnection = LocalConnection(
            "screen-append-saved",
            "Saved-screen connection");
        var layout = new LayoutDefinition(
            new LayoutId("screen-append-layout"),
            LayoutDefinition.CurrentSchemaVersion,
            "Two terminals",
            new LayoutGrid(2, 1),
            [
                new LayoutSlotDefinition(
                    new LayoutSlotId("left"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
                new LayoutSlotDefinition(
                    new LayoutSlotId("right"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(220, 140)),
            ]);
        var screen = new ScreenDefinition(
            new ScreenId("screen-append"),
            ScreenDefinition.CurrentSchemaVersion,
            "Appended screen",
            null,
            layout.Id,
            [
                new ScreenPanelDefinition(
                    new ScreenPanelId("screen-append-left"),
                    new LayoutSlotId("left"),
                    ScreenPanelKind.Terminal,
                    "Left terminal",
                    screenConnection.Id,
                    PanelStartupBehavior.None),
                new ScreenPanelDefinition(
                    new ScreenPanelId("screen-append-right"),
                    new LayoutSlotId("right"),
                    ScreenPanelKind.Terminal,
                    "Right terminal",
                    screenConnection.Id,
                    PanelStartupBehavior.None),
            ]);
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(initialConnection), Store(screenConnection)],
            [Store(layout)],
            [Store(screen)],
            [], [], [], [], [], []);
        var historyStore = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(historyStore));

        Assert.True(await viewModel.OpenConnectionAsync(initialConnection.Id));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await AwaitTerminalPanels(runtime);
        var originalTab = Assert.Single(runtime.Tabs);
        var originalPanel = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(originalTab.Panels));
        var originalSession = Assert.IsType<EnsureTerminalSessionRequest>(
            originalPanel.SessionRequest);

        Assert.True(await viewModel.LaunchScreenAsync(screen.Id));
        Assert.True(await viewModel.LaunchScreenAsync(screen.Id));
        await AwaitTerminalPanels(runtime);
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.Same(runtime, viewModel.RuntimeWorkspace);
        Assert.Equal(3, runtime.Tabs.Count);
        Assert.Same(originalTab, runtime.Tabs[0]);
        Assert.Same(originalPanel, runtime.Tabs[0].Panels[0]);
        Assert.Equal(originalSession.SessionId, originalPanel.SessionRequest!.SessionId);

        var appendedTabs = runtime.Tabs.Skip(1).ToArray();
        Assert.All(
            appendedTabs,
            tab =>
            {
                var historySource = Assert.IsType<RuntimeHistorySource>(tab.HistorySource);
                Assert.Equal(screen.Key, historySource.SourceDefinition);
                Assert.Equal(screen.Name, historySource.DurableTitle);
                Assert.Equal(2, tab.Columns);
                Assert.Equal(1, tab.Rows);
                Assert.Equal(2, tab.Panels.Count);
            });
        Assert.Equal(2, appendedTabs.Select(tab => tab.Id).Distinct().Count());

        var originalPanelIds = originalTab.Panels.Select(panel => panel.Id).ToHashSet();
        var appendedPanelIds = appendedTabs
            .SelectMany(tab => tab.Panels)
            .Select(panel => panel.Id)
            .ToArray();
        Assert.Equal(4, appendedPanelIds.Distinct().Count());
        Assert.Empty(originalPanelIds.Intersect(appendedPanelIds));

        var originalSessionIds = originalTab.Panels
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.SessionRequest!.SessionId)
            .ToHashSet();
        var appendedSessionIds = appendedTabs
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.SessionRequest!.SessionId)
            .ToArray();
        Assert.Equal(4, appendedSessionIds.Distinct().Count());
        Assert.Empty(originalSessionIds.Intersect(appendedSessionIds));

        Assert.All(
            runtime.Tabs,
            tab => Assert.All(
                tab.Panels.OfType<TerminalRuntimePanelViewModel>(),
                panel =>
                {
                    var request = Assert.IsType<EnsureTerminalSessionRequest>(panel.SessionRequest);
                    Assert.Equal(runtime.Id, request.Owner.WorkspaceId);
                    Assert.Equal(tab.Id, request.Owner.TabId);
                    Assert.Equal(panel.Id, request.Owner.PanelId);
                }));

        var historyBySession = historyStore.Snapshot.ToDictionary(item => item.SessionId);
        Assert.Equal(5, historyBySession.Count);
        var originalHistory = historyBySession[originalSession.SessionId];
        Assert.Equal(initialConnection.Key, originalHistory.SourceDefinition);
        Assert.Equal(initialConnection.Name, originalHistory.Title);
        foreach (var sessionId in appendedSessionIds)
        {
            var appendedHistory = historyBySession[sessionId];
            Assert.Equal(screen.Key, appendedHistory.SourceDefinition);
            Assert.Equal(screen.Name, appendedHistory.Title);
        }
    }

    [Fact]
    public async Task HostAcceptedSavedConnectionEventuallyRecordsDelayedTerminalWithExactHistorySource()
    {
        var initialConnection = LocalConnection(
            "delayed-history-initial",
            "Initial connection");
        var appendedConnection = LocalConnection(
            "delayed-history-appended",
            "Delayed saved connection");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(initialConnection), Store(appendedConnection)],
            [], [], [], [], [], [], [], []);
        var historyStore = new MemoryRecentSessionStore();
        var connectionRuntime = new DelayedConnectionRuntime(appendedConnection.Id);
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(historyStore),
            connectionRuntime: connectionRuntime);

        Assert.True(await viewModel.OpenConnectionAsync(initialConnection.Id));
        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await AwaitTerminalPanels(workspace);
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        var launch = viewModel.LaunchConnectionAsync(appendedConnection.Id);
        await connectionRuntime.DelayedPlanEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await launch);

        var appendedTab = workspace.Tabs[1];
        Assert.Same(workspace.ActiveTab, appendedTab);
        var appendedTerminal = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(appendedTab.Panels));
        Assert.False(appendedTerminal.Initialization.IsCompleted);
        Assert.Null(appendedTerminal.SessionRequest);
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        Assert.DoesNotContain(
            historyStore.Snapshot,
            item => item.SourceDefinition == appendedConnection.Key);

        connectionRuntime.CompleteDelayedPlan();
        await appendedTerminal.Initialization.WaitAsync(TimeSpan.FromSeconds(5));
        var session = Assert.IsType<EnsureTerminalSessionRequest>(
            appendedTerminal.SessionRequest);
        await WaitForRecentSessionAsync(
            viewModel,
            historyStore,
            session.SessionId);

        var history = Assert.Single(
            historyStore.Snapshot,
            item => item.SessionId == session.SessionId);
        Assert.Equal(appendedConnection.Key, history.SourceDefinition);
        Assert.Equal(appendedConnection.Name, history.Title);
    }

    [Fact]
    public async Task LiveRuntimeWorkspaceIsPersistedAndRestoredWithFreshRuntimeIdentities()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("recovery-local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Recovery local",
            new ConnectionEndpoint.Local("/bin/sh"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            ["credential-should-not-appear"]);
        var definitionSnapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        var fileRoot = new FilePanelLocation(
            "builtin.files.home",
            null,
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(
                [new FilePanelPathSegment("safe"), new FilePanelPathSegment("root")])));
        var files = new EmptyFileClients(
            [new FileProviderProfileDescriptor(
                "builtin.files.home",
                "Home",
                FileProviderFamily.Posix,
                fileRoot,
                FilePanelCapability.List,
                250,
                256 * 1024)]);
        var sourceStartup = InitializeRun("interrupted-run");
        var sourceStore = new RecordingRecoveryStore();
        var sourceWriter = new RuntimeRecoveryWriter(
            sourceStore,
            sourceStartup,
            TimeProvider.System);
        using var source = CreateViewModel(definitionSnapshot, files, sourceWriter);

        Assert.True(await source.OpenConnectionAsync(connection.Id));
        Assert.True(await source.AddLocalTerminalTabAsync());
        Assert.True(await source.AddFilePanelAsync());
        var sourceWorkspace = source.RuntimeWorkspace!;
        var sourceFile = Assert.IsType<FileRuntimePanelViewModel>(
            sourceWorkspace.ActiveTab!.ActivePanel);
        await sourceFile.Initialization;
        var sourceHostedFile = Assert.IsAssignableFrom<IHostedFilePanelClient>(
            sourceFile.HostedClient);
        Assert.Equal(sourceWorkspace.Id, sourceHostedFile.Owner.WorkspaceId);
        Assert.Equal(sourceWorkspace.ActiveTab.Id, sourceHostedFile.Owner.TabId);
        Assert.Equal(sourceFile.Id, sourceHostedFile.Owner.PanelId);
        Assert.True(source.ToggleActivePanelZoom());
        var sourceTabIds = sourceWorkspace.Tabs.Select(tab => tab.Id).ToHashSet();
        var sourcePanelIds = sourceWorkspace.Tabs
            .SelectMany(tab => tab.Panels)
            .Select(panel => panel.Id)
            .ToHashSet();
        var sourceHistory = Assert.IsType<RuntimeHistorySource>(
            sourceWorkspace.Tabs[0].HistorySource);
        Assert.Equal(connection.Key, sourceHistory.SourceDefinition);
        Assert.Equal(connection.Name, sourceHistory.DurableTitle);

        Assert.True((await sourceWriter.FlushAsync(CancellationToken.None)).IsSuccess);
        var saved = sourceStore.Snapshots.Last();
        Assert.Equal("interrupted-run", saved.RunId);
        Assert.Contains("\"historySource\"", saved.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("pathSegments", saved.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-should-not-appear", saved.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/sh", saved.PayloadJson, StringComparison.Ordinal);

        var recoveredStore = new RecordingRecoveryStore();
        var recoveredWriter = new RuntimeRecoveryWriter(
            recoveredStore,
            InitializeRun("recovery-run"),
            TimeProvider.System);
        using var recovered = CreateViewModel(definitionSnapshot, files, recoveredWriter);

        Assert.True(await recovered.RestoreRuntimeSnapshotsAsync([saved]));
        var workspace = recovered.RuntimeWorkspace!;
        Assert.Equal(ShellRoute.Workspace, recovered.Route);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Empty(sourceTabIds.Intersect(workspace.Tabs.Select(tab => tab.Id)));
        Assert.Empty(sourcePanelIds.Intersect(
            workspace.Tabs.SelectMany(tab => tab.Panels).Select(panel => panel.Id)));
        Assert.Same(workspace.Tabs[1], workspace.ActiveTab);
        var restoredFile = Assert.IsType<FileRuntimePanelViewModel>(
            workspace.ActiveTab!.ActivePanel);
        await restoredFile.Initialization;
        var restoredHostedFile = Assert.IsAssignableFrom<IHostedFilePanelClient>(
            restoredFile.HostedClient);
        Assert.Equal(workspace.Id, restoredHostedFile.Owner.WorkspaceId);
        Assert.Equal(workspace.ActiveTab.Id, restoredHostedFile.Owner.TabId);
        Assert.Equal(restoredFile.Id, restoredHostedFile.Owner.PanelId);
        Assert.NotEqual(sourceHostedFile.SessionId, restoredHostedFile.SessionId);
        Assert.Equal(fileRoot, restoredFile.CurrentLocation);
        Assert.True(workspace.ActiveTab.HasZoomedPanel);
        Assert.All(
            workspace.Tabs.SelectMany(tab => tab.Panels).OfType<TerminalRuntimePanelViewModel>(),
            panel => Assert.Equal(connection.Id, panel.ConnectionId));
        var restoredHistory = Assert.IsType<RuntimeHistorySource>(
            workspace.Tabs[0].HistorySource);
        Assert.Equal(connection.Key, restoredHistory.SourceDefinition);
        Assert.Equal(connection.Name, restoredHistory.DurableTitle);
        Assert.Equal(2, workspace.ActiveTab.Columns);
        Assert.Equal(1, workspace.ActiveTab.Rows);

        Assert.True((await recoveredWriter.FlushAsync(CancellationToken.None)).IsSuccess);
        Assert.Equal("recovery-run", recoveredStore.Snapshots.Last().RunId);
    }

    [Fact]
    public async Task SuccessfulRecoveryStartsGraphWatchAndTracksAlreadyPreparedSession()
    {
        var connection = LocalConnection("watched-recovery", "Watched recovery");
        var definitions = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        using var source = CreateViewModel(definitions, new EmptyFileClients());
        Assert.True(await source.OpenConnectionAsync(connection.Id));
        await AwaitTerminalPanels(source.RuntimeWorkspace!);
        var snapshot = new RuntimeRecoverySnapshot(
            "interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            RuntimeWorkspaceRecoveryCodec.Serialize(source.RuntimeWorkspace),
            DateTimeOffset.UtcNow);
        var historyStore = new MemoryRecentSessionStore();
        var sessionClient = DispatchProxy.Create<ISessionHostClient, NullSessionClient>();
        var observedClient = Assert.IsAssignableFrom<NullSessionClient>(sessionClient);
        using var recovered = CreateViewModel(
            definitions,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(historyStore),
            sessionClient: sessionClient);

        Assert.True(await recovered.RestoreRuntimeSnapshotsAsync([snapshot]));
        await observedClient.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await recovered.FlushRecentSessionHistoryAsync(CancellationToken.None);

        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(recovered.RuntimeWorkspace);
        var terminal = Assert.IsType<TerminalRuntimePanelViewModel>(
            Assert.Single(Assert.Single(workspace.Tabs).Panels));
        var session = Assert.IsType<EnsureTerminalSessionRequest>(terminal.SessionRequest);
        var recent = Assert.Single(historyStore.Snapshot);
        Assert.Equal(session.SessionId, recent.SessionId);
        Assert.Equal(connection.Key, recent.SourceDefinition);
        Assert.Equal(connection.Name, recent.Title);
        Assert.Equal(1, observedClient.WatchStartCount);
    }

    [Fact]
    public async Task RecoveryRestoresAnUnconfiguredDockPlaceholder()
    {
        var connection = LocalConnection("placeholder-recovery", "Placeholder recovery");
        var definitions = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        using var source = CreateViewModel(definitions, new EmptyFileClients());
        Assert.True(await source.OpenConnectionAsync(connection.Id));
        var sourceWorkspace = Assert.IsType<RuntimeWorkspaceViewModel>(source.RuntimeWorkspace);
        var sourceTab = Assert.IsType<RuntimeTabViewModel>(sourceWorkspace.ActiveTab);
        sourceTab.AddPlaceholder(PanelSide.Right);
        var snapshot = new RuntimeRecoverySnapshot(
            "placeholder-interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            RuntimeWorkspaceRecoveryCodec.Serialize(sourceWorkspace),
            DateTimeOffset.UtcNow);
        using var recovered = CreateViewModel(definitions, new EmptyFileClients());

        Assert.True(await recovered.RestoreRuntimeSnapshotsAsync([snapshot]));

        var workspace = Assert.IsType<RuntimeWorkspaceViewModel>(recovered.RuntimeWorkspace);
        var tab = Assert.Single(workspace.Tabs);
        Assert.Collection(
            tab.Panels,
            panel => Assert.IsType<TerminalRuntimePanelViewModel>(panel),
            panel => Assert.IsType<PanelPlaceholderViewModel>(panel));
    }

    [Fact]
    public async Task RecoveryPreservesAFloatingWindowFromAnAutomaticRuntimeTab()
    {
        var connection = LocalConnection("floating-recovery", "Floating recovery");
        var definitions = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        using var source = CreateViewModel(definitions, new EmptyFileClients());
        Assert.True(await source.OpenConnectionAsync(connection.Id));
        var sourceWorkspace = Assert.IsType<RuntimeWorkspaceViewModel>(source.RuntimeWorkspace);
        var sourceTab = Assert.IsType<RuntimeTabViewModel>(sourceWorkspace.ActiveTab);
        var document = FindDocument(sourceTab.DockLayout);
        sourceTab.DockFactory.RemoveDockable(document, collapse: true);
        var window = Assert.IsAssignableFrom<IDockWindow>(
            sourceTab.DockFactory.CreateWindowFrom(document));
        window.Id = "floating-recovery-window";
        window.X = 120;
        window.Y = 90;
        window.Width = 900;
        window.Height = 640;
        sourceTab.DockLayout.Windows!.Add(window);
        var snapshot = new RuntimeRecoverySnapshot(
            "floating-interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            RuntimeWorkspaceRecoveryCodec.Serialize(sourceWorkspace),
            DateTimeOffset.UtcNow);
        using var recovered = CreateViewModel(definitions, new EmptyFileClients());

        Assert.True(await recovered.RestoreRuntimeSnapshotsAsync([snapshot]));

        var recoveredWorkspace = Assert.IsType<RuntimeWorkspaceViewModel>(
            recovered.RuntimeWorkspace);
        var recoveredTab = Assert.Single(recoveredWorkspace.Tabs);
        var recoveredWindow = Assert.Single(recoveredTab.DockLayout.Windows!);
        Assert.Equal("floating-recovery-window", recoveredWindow.Id);
        Assert.Equal(120, recoveredWindow.X);
        Assert.Equal(90, recoveredWindow.Y);
        Assert.Equal(900, recoveredWindow.Width);
        Assert.Equal(640, recoveredWindow.Height);

        static IDocument FindDocument(IRootDock root)
        {
            var pending = new Stack<IDockable>();
            pending.Push(root);
            while (pending.TryPop(out var dockable))
            {
                if (dockable is IDocument document)
                {
                    return document;
                }

                if (dockable is not IDock { VisibleDockables: { } children })
                {
                    continue;
                }

                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }

            throw new InvalidOperationException("The runtime tab contains no document.");
        }
    }

    [Fact]
    public async Task DefinitionBackedRuntimeTracksSafeRecentMetadataReopenAndClear()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("recent-local"),
            ConnectionProfile.CurrentSchemaVersion,
            "Recent local",
            new ConnectionEndpoint.Local("/bin/secret-shell"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable,
            ["command-should-not-be-history"]);
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        var store = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        var terminal = Assert.IsType<TerminalRuntimePanelViewModel>(viewModel.ActivePanel);
        var started = Assert.Single(store.Snapshot);
        Assert.Equal(terminal.SessionRequest!.SessionId, started.SessionId);
        Assert.Equal(connection.Key, started.SourceDefinition);
        Assert.Equal(PanelKind.Terminal, started.Kind);
        Assert.Equal(connection.Name, started.Title);
        Assert.DoesNotContain("secret-shell", started.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("command", started.Title, StringComparison.Ordinal);
        var recent = Assert.Single(viewModel.RecentSessions);
        Assert.True(recent.CanOpen);

        Assert.True(await viewModel.RemovePanelAsync(terminal.Id));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        Assert.Equal(RecentSessionOutcome.GracefullyClosed, Assert.Single(store.Snapshot).Outcome);

        Assert.True(await viewModel.OpenRecentSessionAsync(recent));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        Assert.Equal(connection.Name, viewModel.RuntimeWorkspace!.Name);
        Assert.Equal(2, store.Snapshot.Count);

        Assert.True(await viewModel.ClearRecentSessionsAsync(CancellationToken.None));
        Assert.Empty(store.Snapshot);
        Assert.Empty(viewModel.RecentSessions);
        Assert.True(viewModel.HasNoRecentSessions);
    }

    [Fact]
    public async Task ShutdownQuiescenceCompletesRemainingRecentSessions()
    {
        var connection = LocalConnection("shutdown-history", "Shutdown history");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        var store = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        Assert.Equal(RecentSessionOutcome.Active, Assert.Single(store.Snapshot).Outcome);

        viewModel.TeardownPresentationForShutdown();
        await viewModel.QuiesceForShutdownAsync(CancellationToken.None);
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.Equal(
            RecentSessionOutcome.GracefullyClosed,
            Assert.Single(store.Snapshot).Outcome);
    }

    [Fact]
    public async Task ShutdownDoesNotRefreshHistoryAfterPersistingSessionCompletions()
    {
        var connection = LocalConnection("shutdown-no-refresh", "Shutdown no refresh");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        var store = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        Assert.True(
            (await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None)).IsSuccess);
        store.FailReadsUntilCleared = true;

        viewModel.TeardownPresentationForShutdown();
        await viewModel.QuiesceForShutdownAsync(CancellationToken.None);
        var drain = await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.True(drain.IsSuccess, drain.Error?.Message);
        Assert.Equal(
            RecentSessionOutcome.GracefullyClosed,
            Assert.Single(store.Snapshot).Outcome);
    }

    [Fact]
    public async Task ShutdownHistoryDrainReportsACompletionWriteFailure()
    {
        var connection = LocalConnection("shutdown-history-failure", "Shutdown history failure");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)], [], [], [], [], [], [], [], []);
        var store = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        Assert.True(
            (await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None)).IsSuccess);
        store.FailCompletionWrites = true;

        viewModel.TeardownPresentationForShutdown();
        await viewModel.QuiesceForShutdownAsync(CancellationToken.None);
        var drain = await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.False(drain.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, drain.Error!.Code);
        Assert.Contains(
            "Completion persistence failed",
            drain.Error.Message,
            StringComparison.Ordinal);
        Assert.Equal(RecentSessionOutcome.Active, Assert.Single(store.Snapshot).Outcome);
    }

    [Fact]
    public async Task MissingRecentDefinitionRemainsVisibleButCannotBeReopened()
    {
        var store = new MemoryRecentSessionStore(
            new RecentSessionRecord(
                new SessionId("missing-session"),
                new DefinitionKey(DefinitionKind.Connection, "deleted-connection"),
                PanelKind.Terminal,
                "Deleted connection",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddMinutes(-1),
                RecentSessionOutcome.GracefullyClosed));
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        var recent = Assert.Single(viewModel.RecentSessions);
        Assert.False(recent.CanOpen);
        viewModel.LauncherSearchQuery = "Deleted connection";
        var searchResult = Assert.Single(viewModel.LauncherSearchResults);
        Assert.Equal(
            new LauncherSearchTarget.RecentSession(recent.SessionId),
            searchResult.Target);
        Assert.False(searchResult.IsAvailable);
        Assert.Contains(
            "no longer exists",
            searchResult.DisplayDetail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(viewModel.SelectedLauncherSearchResult);
        Assert.Null(viewModel.ConfirmLauncherSearchSelection());
        Assert.False(await viewModel.OpenRecentSessionAsync(recent));
        Assert.True(viewModel.HasOperationError);
        Assert.Null(viewModel.RuntimeWorkspace);
    }

    [Fact]
    public async Task Recent_connection_reopen_uses_current_platform_availability()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("recent-wsl"),
            ConnectionProfile.CurrentSchemaVersion,
            "WSL shell",
            new ConnectionEndpoint.Wsl("Ubuntu"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.NotApplicable);
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var store = new MemoryRecentSessionStore(new RecentSessionRecord(
            new SessionId("recent-wsl-session"),
            connection.Key,
            PanelKind.Terminal,
            connection.Name,
            started,
            started.AddMinutes(1),
            RecentSessionOutcome.GracefullyClosed));
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot(
                [Store(connection)],
                [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        var recent = Assert.Single(viewModel.HistorySessions);
        Assert.Equal(OperatingSystem.IsWindows(), recent.CanOpen);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(await viewModel.OpenRecentSessionAsync(recent));
            Assert.Contains(
                "unavailable",
                viewModel.OperationError,
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(viewModel.RuntimeWorkspace);
        }
    }

    [Fact]
    public async Task Full_history_and_unified_search_include_records_older_than_launcher_preview()
    {
        var connection = LocalConnection("history-source", "History source");
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 9)
            .Select(index =>
            {
                var started = index == 0
                    ? now.AddDays(-2)
                    : now.AddMinutes(-index);
                return new RecentSessionRecord(
                    new SessionId($"history-session-{index}"),
                    connection.Key,
                    PanelKind.Terminal,
                    index == 0 ? "Needle oldest" : $"Recent {index}",
                    started,
                    started.AddMinutes(1),
                    RecentSessionOutcome.GracefullyClosed);
            })
            .ToArray();
        var store = new MemoryRecentSessionStore(records);
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot(
                [Store(connection)],
                [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.Equal(8, viewModel.RecentSessions.Count);
        Assert.Equal(9, viewModel.HistorySessions.Count);
        Assert.DoesNotContain(
            viewModel.RecentSessions,
            item => item.SessionId == new SessionId("history-session-0"));

        viewModel.HistorySearchQuery = "Needle";
        var historyResult = Assert.Single(viewModel.FilteredHistorySessions);
        Assert.Equal(new SessionId("history-session-0"), historyResult.SessionId);

        viewModel.LauncherSearchQuery = "Needle";
        var launcherResult = Assert.Single(viewModel.LauncherSearchResults);
        Assert.Equal(
            new LauncherSearchTarget.RecentSession(new SessionId("history-session-0")),
            launcherResult.Target);

        Assert.True(viewModel.TryBeginHistoryExport(HistoryExportScope.CurrentResults));
        Assert.False(viewModel.TryBeginHistoryExport(HistoryExportScope.CurrentResults));
        Assert.True(viewModel.IsHistoryExporting);
        Assert.False(viewModel.CanExportFilteredHistory);
        viewModel.EndHistoryExport("Export cancelled by test.");
        Assert.False(viewModel.IsHistoryExporting);
        Assert.True(viewModel.CanExportFilteredHistory);
    }

    [Fact]
    public async Task Disabling_history_persists_the_privacy_policy_and_prunes_loaded_metadata()
    {
        var connection = LocalConnection("retention-source", "Retention source");
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var store = new MemoryRecentSessionStore(new RecentSessionRecord(
            new SessionId("retained-session"),
            connection.Key,
            PanelKind.Terminal,
            connection.Name,
            started,
            started.AddMinutes(1),
            RecentSessionOutcome.GracefullyClosed));
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot(
                [Store(connection)],
                [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        viewModel.SelectedHistoryRetentionOption = Assert.Single(
            viewModel.HistoryRetentionOptions,
            option => !option.Policy.IsEnabled);

        Assert.True(viewModel.RequiresHistoryRetentionConfirmation);
        var result = await viewModel.SaveHistoryRetentionAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.PrunedSessionCount);
        Assert.False(result.Value.StoredPolicy.Policy.IsEnabled);
        Assert.Empty(store.Snapshot);
        Assert.Empty(viewModel.HistorySessions);
        Assert.Contains("disabled", viewModel.RecentSessionStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "1 retained record removed",
            viewModel.HistoryRetentionStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retention_conflict_reloads_the_current_policy_and_reports_the_conflict()
    {
        var store = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        viewModel.SelectedHistoryRetentionOption = Assert.Single(
            viewModel.HistoryRetentionOptions,
            option => !option.Policy.IsEnabled);
        var externallySelected = new RecentSessionRetentionPolicy(20, TimeSpan.FromDays(7));
        store.ChangeRetentionExternally(externallySelected);

        var result = await viewModel.SaveHistoryRetentionAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecentSessionStoreErrorCode.Conflict, result.Error!.Code);
        Assert.Equal(externallySelected, viewModel.SelectedHistoryRetentionOption!.Policy);
        Assert.Contains(
            "changed elsewhere",
            viewModel.HistoryRetentionStatus,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lifecycle_refresh_preserves_an_unsaved_retention_choice()
    {
        var connection = LocalConnection("retention-draft", "Retention draft");
        var store = new MemoryRecentSessionStore();
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot(
                [Store(connection)],
                [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        var draft = Assert.Single(
            viewModel.HistoryRetentionOptions,
            option => option.Policy.MaximumEntries == 20);
        viewModel.SelectedHistoryRetentionOption = draft;

        Assert.True(viewModel.HasPendingHistoryRetentionChange);
        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.Same(draft, viewModel.SelectedHistoryRetentionOption);
        Assert.True(viewModel.HasPendingHistoryRetentionChange);
        Assert.True(viewModel.CanApplyHistoryRetention);
    }

    [Fact]
    public async Task Unreadable_history_can_be_reset_without_exposing_malformed_rows()
    {
        var store = new MemoryRecentSessionStore
        {
            FailReadsUntilCleared = true,
        };
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.True(viewModel.HasRecentSessionFailure);
        Assert.True(viewModel.CanResetRecentSessionHistory);
        Assert.Empty(viewModel.HistorySessions);

        Assert.True(await viewModel.ResetUnreadableRecentSessionsAsync(CancellationToken.None));
        Assert.False(viewModel.HasRecentSessionFailure);
        Assert.False(viewModel.CanResetRecentSessionHistory);
        Assert.Empty(viewModel.HistorySessions);
    }

    [Fact]
    public async Task Transient_history_read_failure_exposes_a_retry_path()
    {
        var store = new MemoryRecentSessionStore
        {
            FailReadsUntilCleared = true,
            ReadFailureCode = RecentSessionStoreErrorCode.StorageUnavailable,
        };
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
        Assert.True(viewModel.CanRetryRecentSessionHistory);
        Assert.False(viewModel.CanResetRecentSessionHistory);
        store.FailReadsUntilCleared = false;

        Assert.True(await viewModel.RetryRecentSessionHistoryAsync(CancellationToken.None));
        Assert.False(viewModel.HasRecentSessionFailure);
        Assert.False(viewModel.CanRetryRecentSessionHistory);
        Assert.True(viewModel.HasNoHistorySessions);
    }

    [Fact]
    public async Task NothingStoredOpensNoRuntimeState()
    {
        var files = new EmptyFileClients();
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            files);

        Assert.False(await viewModel.RestoreRuntimeSnapshotsAsync([]));
        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
    }

    [Fact]
    public void RuntimeHistorySourceNormalizesDurableTitleAndRejectsPartialMetadata()
    {
        var definition = new DefinitionKey(DefinitionKind.Screen, "history-source-screen");

        var source = new RuntimeHistorySource(definition, "  Saved screen  ");

        Assert.Equal(definition, source.SourceDefinition);
        Assert.Equal("Saved screen", source.DurableTitle);
        Assert.Throws<ArgumentException>(() =>
            new RuntimeHistorySource(default, "Saved screen"));
        Assert.Throws<ArgumentException>(() =>
            new RuntimeHistorySource(definition, " "));
        Assert.Throws<ArgumentException>(() =>
            new RuntimeHistorySource(definition, "Bad\0title"));
    }

    [Fact]
    public void CurrentRecoverySnapshotRoundTripsHistorySourceAtomically()
    {
        var source = new RuntimeHistorySource(
            new DefinitionKey(DefinitionKind.Screen, "recovery-screen"),
            "Recovery screen");
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Recovery workspace",
            "Bronze",
            []);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Recovered tab",
            "SAVED SCREEN",
            historySource: source,
            agentPolicy: RuntimeAgentPolicyProvenance.Unconfigured);
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Browser,
            "Unavailable panel",
            "BROWSER",
            "The capability is unavailable."));
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        var json = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
        var snapshot = new RuntimeRecoverySnapshot(
            "interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            json,
            DateTimeOffset.UtcNow);

        Assert.True(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            snapshot,
            out var payload,
            out var error), error);
        var recoveredSource = Assert.IsType<RuntimeHistorySourceRecoveryPayload>(
            Assert.Single(payload!.Workspace!.Tabs).HistorySource);
        Assert.Equal(source, recoveredSource.ToHistorySource());
        Assert.Null(payload.Workspace.AgentPolicy);
        Assert.Null(Assert.Single(payload.Workspace.Tabs).AgentPolicy);

        var mislabeledVersionOneSnapshot = snapshot with { SchemaVersion = 1 };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            mislabeledVersionOneSnapshot,
            out _,
            out var mislabeledVersionOneError));
        Assert.Contains(
            "schema 1",
            Assert.IsType<string>(mislabeledVersionOneError),
            StringComparison.OrdinalIgnoreCase);

        var currentWorkspace = payload.Workspace;
        var versionOnePayload = new RuntimeWindowRecoveryPayload(
            currentWorkspace with
            {
                AgentPolicy = null,
                Tabs = [.. currentWorkspace.Tabs
                    .Select(currentTab => currentTab with
                    {
                        HistorySource = null,
                        AgentPolicy = null,
                    })],
            });
        var versionOneJson = JsonSerializer.Serialize(
            versionOnePayload,
            RuntimeWorkspaceRecoveryJsonContext.Default.RuntimeWindowRecoveryPayload);
        var versionOneSnapshot = snapshot with
        {
            SchemaVersion = 1,
            PayloadJson = versionOneJson,
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            versionOneSnapshot,
            out _,
            out var versionOneError));
        Assert.Contains("not supported", versionOneError, StringComparison.OrdinalIgnoreCase);

        var partialJson = json.Replace(
            "\"sourceValue\":\"recovery-screen\",",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(json, partialJson, StringComparer.Ordinal);
        var partialSnapshot = snapshot with { PayloadJson = partialJson };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            partialSnapshot,
            out _,
            out var partialError));
        Assert.NotNull(partialError);
        Assert.Contains("invalid", partialError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsolatedRecoverySnapshotPersistsMountScopeAndRejectsMissingMountMetadata()
    {
        var workspaceId = new WorkspaceId("isolated-recovery-workspace");
        var source = new RuntimeHistorySource(
            new DefinitionKey(WorkspaceDefinition.Kind, workspaceId.Value),
            "Isolated recovery workspace");
        var binding = new WorkspaceIsolationBinding(
            workspaceId,
            new WorkspaceIsolationProviderId("test-isolation"),
            WorkspaceIsolationCapability.StructuredProcessExecution,
            "isolated-recovery-resource",
            [new WorkspaceIsolationMount(
                "/host/projects",
                "/workspace",
                isReadOnly: true)],
            Guid.NewGuid());
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Isolated recovery workspace",
            "Bronze",
            [],
            isolationBinding: binding);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Recovered tab",
            "WORKSPACE TAB");
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Browser,
            "Unavailable panel",
            "BROWSER",
            "The capability is unavailable."));
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        var snapshot = new RuntimeRecoverySnapshot(
            "isolated-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            RuntimeWorkspaceRecoveryCodec.Serialize(workspace, source),
            DateTimeOffset.UtcNow);

        Assert.True(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            snapshot,
            out var recovered,
            out var error), error);
        Assert.True(recovered!.Workspace!.IsIsolated);
        var mount = Assert.Single(recovered.Workspace.IsolationMounts!);
        Assert.Equal("/host/projects", mount.HostSource);
        Assert.Equal("/workspace", mount.GuestDestination);
        Assert.True(mount.IsReadOnly);

        var missingMountsPayload = new RuntimeWindowRecoveryPayload(
            recovered.Workspace with { IsolationMounts = null });
        var missingMountsSnapshot = snapshot with
        {
            PayloadJson = JsonSerializer.Serialize(
                missingMountsPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default.RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            missingMountsSnapshot,
            out _,
            out var missingMountsError));
        Assert.Contains("invalid", missingMountsError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recovery_snapshot_rejects_more_panels_than_a_live_workspace_can_register()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Oversized workspace",
            "Bronze",
            []);
        for (var tabIndex = 0; tabIndex < 2; tabIndex++)
        {
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                $"Tab {tabIndex + 1}",
                "WORKSPACE TAB");
            var panelCount = tabIndex == 0
                ? WorkspaceInstance.MaximumPanelCount / 2
                : WorkspaceInstance.MaximumPanelCount / 2 + 1;
            for (var panelIndex = 0; panelIndex < panelCount; panelIndex++)
            {
                tab.AddPanel(new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    $"Panel {tabIndex + 1}-{panelIndex + 1}",
                    "BROWSER",
                    "Unavailable."));
            }

            workspace.Tabs.Add(tab);
        }

        workspace.ActiveTab = workspace.Tabs[0];

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RuntimeWorkspaceRecoveryCodec.Serialize(workspace));

        Assert.Contains(
            WorkspaceInstance.MaximumPanelCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryPolicyProvenanceIsVersionedAndWorkspaceLineageFailsClosed()
    {
        var workspaceDefinition = new DefinitionKey(
            DefinitionKind.Workspace,
            "policy-workspace");
        var provenance = RuntimeAgentPolicyProvenance.Unconfigured.WithOverride(
            new AgentPolicy(
                "Trusted provider",
                "trusted-model",
                AgentPolicy.Capabilities.ToImmutableDictionary(
                    capability => capability,
                    _ => AgentPermission.Ask))
            {
                CompactionModel = new AgentModelSelection("Trusted provider", "compact-model"),
                TitleModel = new AgentModelSelection("Trusted provider", "title-model"),
            },
            workspaceDefinition,
            revision: 11);
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Policy workspace",
            "Bronze",
            [],
            provenance);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Policy tab",
            "WORKSPACE TAB",
            historySource: new RuntimeHistorySource(
                workspaceDefinition,
                "Policy workspace"),
            agentPolicy: provenance);
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Browser,
            "Unavailable panel",
            "BROWSER",
            "The capability is unavailable."));
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        var snapshot = new RuntimeRecoverySnapshot(
            "interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            RuntimeWorkspaceRecoveryCodec.Serialize(workspace),
            DateTimeOffset.UtcNow);

        Assert.True(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            snapshot,
            out var current,
            out var currentError), currentError);
        var workspacePayload = current!.Workspace!;
        var tabPayload = Assert.Single(workspacePayload.Tabs);
        var tabPolicy = Assert.IsType<RuntimeAgentPolicyRecoveryPayload>(
            tabPayload.AgentPolicy);
        var workspacePolicy = Assert.IsType<RuntimeAgentPolicyRecoveryPayload>(
            workspacePayload.AgentPolicy);
        Assert.True(workspacePolicy.HasPolicyOverride);
        Assert.True(tabPolicy.HasPolicyOverride);
        Assert.Equal(
            11,
            Assert.Single(tabPolicy.Sources).Revision);

        var unsupportedPayload = new RuntimeWindowRecoveryPayload(
            workspacePayload with
            {
                AgentPolicy = null,
                Tabs =
                [
                    tabPayload with
                    {
                        AgentPolicy = null,
                    },
                ],
            });
        var unsupportedSchemaSnapshot = snapshot with
        {
            SchemaVersion = 2,
            PayloadJson = JsonSerializer.Serialize(
                unsupportedPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default
                    .RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            unsupportedSchemaSnapshot,
            out _,
            out var unsupportedSchemaError));
        Assert.Contains(
            "not supported",
            unsupportedSchemaError,
            StringComparison.OrdinalIgnoreCase);

        var mismatchedTabPolicy = tabPolicy with
        {
            Sources =
            [
                Assert.Single(tabPolicy.Sources) with
                {
                    Revision = 12,
                },
            ],
        };
        var mismatchedPayload = new RuntimeWindowRecoveryPayload(
            workspacePayload with
            {
                Tabs =
                [
                    tabPayload with
                    {
                        AgentPolicy = mismatchedTabPolicy,
                    },
                ],
            });
        var mismatchedSnapshot = snapshot with
        {
            PayloadJson = JsonSerializer.Serialize(
                mismatchedPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default
                    .RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            mismatchedSnapshot,
            out _,
            out var mismatchError));
        Assert.Contains(
            "lineage",
            Assert.IsType<string>(mismatchError),
            StringComparison.OrdinalIgnoreCase);

        var inheritedWorkspacePolicy = workspacePolicy with
        {
            Provider = AgentPolicy.Default.Provider,
            Model = AgentPolicy.Default.Model,
            Permissions = AgentPolicy.Default.Permissions.ToDictionary(
                item => item.Key,
                item => item.Value),
            CompactionModel = AgentPolicy.Default.CompactionModel,
            TitleModel = AgentPolicy.Default.TitleModel,
            SystemPrompt = AgentPolicy.Default.SystemPrompt,
            HasPolicyOverride = false,
        };
        var forgedOverrideMarkerPayload = new RuntimeWindowRecoveryPayload(
            workspacePayload with
            {
                AgentPolicy = inheritedWorkspacePolicy,
                Tabs =
                [
                    tabPayload with
                    {
                        AgentPolicy = inheritedWorkspacePolicy with
                        {
                            HasPolicyOverride = true,
                        },
                    },
                ],
            });
        var forgedOverrideMarkerSnapshot = snapshot with
        {
            PayloadJson = JsonSerializer.Serialize(
                forgedOverrideMarkerPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default
                    .RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            forgedOverrideMarkerSnapshot,
            out _,
            out var forgedOverrideMarkerError));
        Assert.Contains(
            "lineage",
            Assert.IsType<string>(forgedOverrideMarkerError),
            StringComparison.OrdinalIgnoreCase);

        var yoloPermissions = tabPolicy.Permissions.ToDictionary(
            item => item.Key,
            item => item.Value);
        yoloPermissions[AgentCapability.RunCommands] = AgentPermission.Yolo;
        var yoloPayload = new RuntimeWindowRecoveryPayload(
            workspacePayload with
            {
                AgentPolicy = workspacePolicy with
                {
                    Permissions = yoloPermissions,
                },
            });
        var yoloSnapshot = snapshot with
        {
            PayloadJson = JsonSerializer.Serialize(
                yoloPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default
                    .RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            yoloSnapshot,
            out _,
            out _));

        var missingPolicyPayload = new RuntimeWindowRecoveryPayload(
            workspacePayload with
            {
                Tabs =
                [
                    tabPayload with
                    {
                        AgentPolicy = null,
                    },
                ],
            });
        var missingPolicySnapshot = snapshot with
        {
            PayloadJson = JsonSerializer.Serialize(
                missingPolicyPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default
                    .RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            missingPolicySnapshot,
            out _,
            out _));
    }

    [Fact]
    public void NonCurrentScreenHistorySchemaIsRejected()
    {
        var source = new RuntimeHistorySource(
            new DefinitionKey(DefinitionKind.Screen, "unsupported-policy-screen"),
            "Unsupported policy screen");
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Unsupported policy workspace",
            "Bronze",
            []);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Unsupported policy tab",
            "SAVED SCREEN",
            historySource: source,
            agentPolicy: RuntimeAgentPolicyProvenance.Unconfigured);
        tab.AddPanel(new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKind.Browser,
            "Unavailable panel",
            "BROWSER",
            "The capability is unavailable."));
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        var currentSnapshot = new RuntimeRecoverySnapshot(
            "interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion,
            RuntimeWorkspaceRecoveryCodec.Serialize(workspace),
            DateTimeOffset.UtcNow);
        Assert.True(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            currentSnapshot,
            out var currentPayload,
            out var currentError), currentError);
        var currentWorkspace = currentPayload!.Workspace!;
        var unsupportedPayload = new RuntimeWindowRecoveryPayload(
            currentWorkspace with
            {
                AgentPolicy = null,
                Tabs = [.. currentWorkspace.Tabs.Select(currentTab => currentTab with { AgentPolicy = null })],
            });
        var unsupportedSnapshot = currentSnapshot with
        {
            SchemaVersion = 2,
            PayloadJson = JsonSerializer.Serialize(
                unsupportedPayload,
                RuntimeWorkspaceRecoveryJsonContext.Default
                    .RuntimeWindowRecoveryPayload),
        };
        Assert.False(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            unsupportedSnapshot,
            out _,
            out var unsupportedError));
        Assert.Contains(
            "not supported",
            unsupportedError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FrozenHistoricalEmptyWorkspaceSnapshotIsRejected()
    {
        const string frozenSnapshotKey = "desktop.main-window";
        const int frozenSchemaVersion = 1;
        const string frozenPayload = "{}";
        Assert.Equal(frozenSnapshotKey, RuntimeWorkspaceRecoveryCodec.SnapshotKey);
        Assert.True(RuntimeWorkspaceRecoveryCodec.SchemaVersion > frozenSchemaVersion);
        Assert.Equal(frozenPayload, RuntimeWorkspaceRecoveryCodec.Serialize(null));
        var snapshot = new RuntimeRecoverySnapshot(
            "interrupted-run",
            frozenSnapshotKey,
            frozenSchemaVersion,
            frozenPayload,
            DateTimeOffset.UtcNow);
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            new EmptyFileClients());

        Assert.False(await viewModel.RestoreRuntimeSnapshotsAsync([snapshot]));
        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.Equal(ShellRoute.Workspace, viewModel.Route);
        Assert.True(viewModel.HasOperationError);
        Assert.Contains("not supported", viewModel.OperationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedRecoverySchemaFailsVisiblyWithoutOpeningRuntimeState()
    {
        var snapshot = new RuntimeRecoverySnapshot(
            "interrupted-run",
            RuntimeWorkspaceRecoveryCodec.SnapshotKey,
            RuntimeWorkspaceRecoveryCodec.SchemaVersion + 1,
            "{}",
            DateTimeOffset.UtcNow);
        var files = new EmptyFileClients();
        using var viewModel = CreateViewModel(
            new DefinitionCatalogSnapshot([], [], [], [], [], [], [], [], []),
            files);

        Assert.False(await viewModel.RestoreRuntimeSnapshotsAsync([snapshot]));
        Assert.Null(viewModel.RuntimeWorkspace);
        Assert.True(viewModel.HasOperationError);
        Assert.Contains("not supported", viewModel.OperationError, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AwaitTerminalPanels(RuntimeWorkspaceViewModel workspace) =>
        await Task.WhenAll(workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.Initialization));

    private static async Task WaitForRecentSessionAsync(
        MainWindowViewModel viewModel,
        MemoryRecentSessionStore store,
        SessionId sessionId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);
            if (store.Snapshot.Any(item => item.SessionId == sessionId))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The delayed terminal was not recorded in recent-session history.");
    }

    private static async Task WaitForFileListRequestAsync(NullSessionClient host)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (host.FileListRequests.Count > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The deferred File Viewer did not bind after its provider appeared.");
    }

    private static async Task WaitForFileListCompletionAsync(
        NullSessionClient host,
        FileRuntimePanelViewModel panel)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (host.FileListRequests.Count > 0
                && panel.HostedClient?.IsInitialized == true
                && !panel.IsLoading)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The deferred File Viewer did not finish its first hosted listing.");
    }

    private static async Task WaitForFileEnsureRequestAsync(NullSessionClient host)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (host.EnsureFilePanelRequests.Count == 1)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The deferred File Viewer did not begin its first hosted bind.");
    }

    private static RuntimeIdentity Capture(RuntimeWorkspaceViewModel workspace)
    {
        var tabs = workspace.Tabs.ToArray();
        var panels = tabs.SelectMany(tab => tab.Panels).ToArray();
        var sessions = panels
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.SessionRequest!.SessionId)
            .ToHashSet();
        return new RuntimeIdentity(
            workspace.Id,
            tabs.Select(tab => tab.Id).ToHashSet(),
            panels.Select(panel => panel.Id).ToHashSet(),
            sessions);
    }

    private static StoredDefinition<T> Store<T>(T value)
        where T : IDurableDefinition =>
        new(value, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static ConnectionProfile LocalConnection(string id, string name) => new(
        new ConnectionId(id),
        ConnectionProfile.CurrentSchemaVersion,
        name,
        new ConnectionEndpoint.Local("/bin/sh"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private static ScreenPanelDefinition TerminalPanel(string id, ConnectionId connectionId) => new(
        new ScreenPanelId(id),
        new LayoutSlotId("main"),
        ScreenPanelKind.Terminal,
        id,
        connectionId,
        PanelStartupBehavior.None);

    [Fact]
    public async Task A_session_that_already_ended_is_not_reported_as_lost_metadata()
    {
        // The store refuses to overwrite a terminal outcome — rightly: a
        // session ends once. But a refusal means the end is already
        // recorded, not that anything was lost, and the exit report has to
        // stay a report of loss to mean anything. Seen live as
        // "Recent-session metadata could not be persisted safely" on quit.
        var connection = LocalConnection("connection-ended", "Ended connection");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [], [], [], [], [], [], [], []);
        var store = new AlreadyEndedRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await AwaitTerminalPanels(runtime);
        var panel = Assert.Single(Assert.Single(runtime.Tabs).Panels);

        Assert.True(await viewModel.RemovePanelAsync(panel.Id));
        var flush = await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.True(
            flush.IsSuccess,
            $"An already-recorded ending was reported as lost: {flush.Error?.Message}");
    }

    [Fact]
    public async Task A_session_is_completed_once_however_many_producers_reach_for_it()
    {
        var connection = LocalConnection("connection-once", "Once connection");
        var snapshot = new DefinitionCatalogSnapshot(
            [Store(connection)],
            [], [], [], [], [], [], [], []);
        var store = new CountingRecentSessionStore();
        using var viewModel = CreateViewModel(
            snapshot,
            new EmptyFileClients(),
            recentSessionHistory: new RecentSessionHistory(store));

        Assert.True(await viewModel.OpenConnectionAsync(connection.Id));
        var runtime = Assert.IsType<RuntimeWorkspaceViewModel>(viewModel.RuntimeWorkspace);
        await AwaitTerminalPanels(runtime);
        var panel = Assert.Single(Assert.Single(runtime.Tabs).Panels);

        // The panel's own close, and then the sweep every teardown performs.
        Assert.True(await viewModel.RemovePanelAsync(panel.Id));
        viewModel.Dispose();
        await viewModel.FlushRecentSessionHistoryAsync(CancellationToken.None);

        Assert.Equal(1, store.CompletionCount);
    }

    /// <summary>A store whose rows always already carry a terminal outcome.</summary>
    private sealed class AlreadyEndedRecentSessionStore : IRecentSessionStore
    {
        public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
            RecentSessionCompletion completion,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<Unit>.Failure(
                new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.Conflict,
                    "The recent session already has a different terminal outcome.")));

        public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
            RecentSessionRecord recentSession,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>>
            ListRecentAsync(RecentSessionQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success([]));

        public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

        public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
            DateTimeOffset through,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

        public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));
    }

    /// <summary>A store that counts how often each session is ended.</summary>
    private sealed class CountingRecentSessionStore : IRecentSessionStore
    {
        private int _completions;
        private int _initializations;
        private int _lists;

        public int CompletionCount => Volatile.Read(ref _completions);

        public int InitializationCount => Volatile.Read(ref _initializations);

        public int ListCount => Volatile.Read(ref _lists);

        public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
            RecentSessionCompletion completion,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _completions);
            return ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
            RecentSessionRecord recentSession,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>>
            ListRecentAsync(RecentSessionQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lists);
            return ValueTask.FromResult(
                RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success([]));
        }

        public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _initializations);
            return ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));
        }

        public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
            DateTimeOffset through,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

        public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));
    }

    private static MainWindowViewModel CreateViewModel(
        DefinitionCatalogSnapshot snapshot,
        EmptyFileClients files,
        RuntimeRecoveryWriter? recoveryWriter = null,
        RecentSessionHistory? recentSessionHistory = null,
        IConnectionRuntime? connectionRuntime = null,
        ISessionHostClient? sessionClient = null,
        OnboardingViewModel? onboarding = null,
        MainWindowRole role = MainWindowRole.Primary) =>
        new(
            sessionClient ?? DispatchProxy.Create<ISessionHostClient, NullSessionClient>(),
            new FixedDefinitionCatalog(snapshot),
            connectionRuntime ?? new SuccessfulConnectionRuntime(),
            new EmptySecretVault(),
            files,
            files,
            new TerminalStartupCommandDispatcher(new SuccessfulAuditStore(), TimeProvider.System),
            runtimeRecoveryWriter: recoveryWriter,
            recentSessionHistory: recentSessionHistory,
            onboarding: onboarding,
            role: role);

    private sealed class IncompleteOnboardingProgressStore : IOnboardingProgressStore
    {
        public int ReadCalls { get; private set; }

        public ValueTask<OnboardingProgressResult<OnboardingProgress>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return ValueTask.FromResult(
                OnboardingProgressResult<OnboardingProgress>.Success(
                    new OnboardingProgress(0, 1)));
        }

        public ValueTask<OnboardingProgressResult<OnboardingProgress>> CompleteAsync(
            int version,
            long expectedRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static ApplicationStartupState InitializeRun(string runId)
    {
        var startup = new ApplicationStartupState();
        startup.Initialize(new ApplicationRunStart(
            runId,
            RecoveryRequired: false,
            new ApplicationRunState(null, WasClean: true, null, null)));
        return startup;
    }

    private sealed record RuntimeIdentity(
        WorkspaceInstanceId WorkspaceId,
        IReadOnlySet<TabInstanceId> TabIds,
        IReadOnlySet<PanelInstanceId> PanelIds,
        IReadOnlySet<SessionId> SessionIds);

    public class NullSessionClient : DispatchProxy
    {
        private WorkspaceGraphSnapshot? _workspace;
        private int _watchStartCount;

        public TaskCompletionSource WatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WatchStartCount => Volatile.Read(ref _watchStartCount);

        public List<EnsureFilePanelSessionRequest> EnsureFilePanelRequests { get; } = [];

        public List<FilePanelListHostRequest> FileListRequests { get; } = [];

        public TaskCompletionSource<HostResult<SessionSnapshot>>?
            FileEnsureCompletion
        { get; set; }

        public TaskCompletionSource<HostResult<FilePanelResult<FilePanelPage>>>?
            FileListCompletion
        { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (string.Equals(targetMethod?.Name, nameof(ISessionHostClient.RegisterWorkspaceGraphAsync)
, StringComparison.Ordinal) && args is [RegisterWorkspaceGraphRequest request, ..])
            {
                var revision = _workspace?.Workspace.Id == request.Workspace.Id
                    ? _workspace.Revision + 1
                    : 1;
                _workspace = new WorkspaceGraphSnapshot(
                    request.WindowId,
                    request.Workspace,
                    revision,
                    revision);
                return new ValueTask<HostResult<WorkspaceGraphSnapshot>>(
                    HostResult<WorkspaceGraphSnapshot>.Succeed(_workspace, revision));
            }

            if (string.Equals(targetMethod?.Name, nameof(ISessionHostClient.UnregisterWorkspaceGraphAsync)
, StringComparison.Ordinal) && args is [UnregisterWorkspaceGraphRequest unregisterRequest, ..]
                && _workspace is { } workspace
                && workspace.WindowId == unregisterRequest.WindowId
                && workspace.Workspace.Id == unregisterRequest.WorkspaceId)
            {
                var revision = workspace.Revision + 1;
                _workspace = null;
                return new ValueTask<HostResult<Unit>>(
                    HostResult<Unit>.Succeed(Unit.Value, revision));
            }

            if (string.Equals(targetMethod?.Name, nameof(ISessionHostClient.WatchWorkspaceGraphAsync)
, StringComparison.Ordinal) && args is [WatchWorkspaceGraphRequest, .., CancellationToken cancellationToken])
            {
                Interlocked.Increment(ref _watchStartCount);
                WatchStarted.TrySetResult();
                return WatchUntilCancelledAsync(cancellationToken);
            }

            if (string.Equals(targetMethod?.Name, nameof(ISessionHostClient.EnsureFilePanelSessionAsync)
, StringComparison.Ordinal) && args is [EnsureFilePanelSessionRequest fileRequest, ..])
            {
                EnsureFilePanelRequests.Add(fileRequest);
                if (FileEnsureCompletion is { } ensureCompletion)
                {
                    return new ValueTask<HostResult<SessionSnapshot>>(
                        ensureCompletion.Task);
                }

                return new ValueTask<HostResult<SessionSnapshot>>(
                    SuccessfulFileEnsure(fileRequest));
            }

            if (string.Equals(targetMethod?.Name, nameof(ISessionHostClient.ListFilesAsync)
, StringComparison.Ordinal) && args is [FilePanelListHostRequest listRequest, ..])
            {
                FileListRequests.Add(listRequest);
                return FileListCompletion is { } completion
                    ? new ValueTask<HostResult<FilePanelResult<FilePanelPage>>>(
                        completion.Task)
                    : new ValueTask<HostResult<FilePanelResult<FilePanelPage>>>(
                        SuccessfulFileList());
            }

            throw new NotSupportedException(targetMethod?.Name);
        }

        public static HostResult<SessionSnapshot> SuccessfulFileEnsure(
            EnsureFilePanelSessionRequest fileRequest)
        {
            var revision = 1L;
            var metadata = new FileSessionMetadata(
                fileRequest.InitialLocation,
                FilePanelCapability.List,
                250,
                256 * 1024);
            var descriptor = new SessionDescriptor(
                fileRequest.SessionId,
                PanelKind.FileViewer,
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                fileRequest.Owner,
                CapabilitySet.Empty,
                revision,
                HasActiveWork: false,
                "Ready",
                FileMetadata: metadata);
            return HostResult<SessionSnapshot>.Succeed(
                new SessionSnapshot(descriptor, 1, [], null),
                revision);
        }

        public static HostResult<FilePanelResult<FilePanelPage>> SuccessfulFileList() =>
            HostResult<FilePanelResult<FilePanelPage>>.Succeed(
                FilePanelResult<FilePanelPage>.Success(
                    new FilePanelPage([], null)),
                resultingRevision: 1);

        private static async IAsyncEnumerable<WorkspaceGraphStreamItem> WatchUntilCancelledAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            yield break;
        }
    }

    private sealed class FixedDefinitionCatalog(DefinitionCatalogSnapshot snapshot)
        : IDefinitionCatalog
    {
        public DefinitionCatalogSnapshot Snapshot { get; } = snapshot;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> InitializeAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<DefinitionCatalogSnapshot>> ReloadAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
            ConnectionProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> SaveLayoutAsync(
            LayoutDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveScreenAsync(
            ScreenDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> SaveWorkspaceAsync(
            WorkspaceDefinition definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
            ThemePreference definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
            SaveTerminalProfileAsync(
                TerminalProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>> SaveKeymapAsync(
            KeymapProfile definition,
            long? expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>>
            SaveFileProviderProfileAsync(
                FileProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>>
            SaveAiProviderProfileAsync(
                AiProviderProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>>
            SaveMcpServerProfileAsync(
                McpServerProfile definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>>
            SaveQuickTerminalSettingsAsync(
                QuickTerminalSettings definition,
                long? expectedRevision,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
            DefinitionKey key,
            long expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SuccessfulConnectionRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(profile.Startup.Directory, "/bin/sh"),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DelayedConnectionRuntime(ConnectionId delayedConnectionId)
        : IConnectionRuntime
    {
        private readonly TaskCompletionSource<bool> _delayedPlanEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _completeDelayedPlan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayedPlanEntered => _delayedPlanEntered.Task;

        public void CompleteDelayedPlan() => _completeDelayedPlan.TrySetResult(true);

        public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.Id == delayedConnectionId)
            {
                _delayedPlanEntered.TrySetResult(true);
                await _completeDelayedPlan.Task.WaitAsync(cancellationToken);
            }

            return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(profile.Startup.Directory, "/bin/sh"),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EmptyFileClients :
        IFilePanelClient,
        IFileTransferQueueClient,
        IFileProviderProfileRuntime
    {
        public EmptyFileClients(IReadOnlyList<FileProviderProfileDescriptor>? profiles = null)
        {
            Profiles = profiles ?? [];
        }

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; private set; }

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; } = [];

        public IReadOnlyList<FileProviderRuntimeDiagnostic> Diagnostics => [];

        public event EventHandler? ProfilesChanged;

        public event EventHandler? TransfersChanged
        {
            add { }
            remove { }
        }

        public void AddProfile(FileProviderProfileDescriptor profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ReplaceProfiles([.. Profiles, profile]);
        }

        public void ReplaceProfiles(
            IReadOnlyList<FileProviderProfileDescriptor> profiles)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            Profiles = [.. profiles];
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask<FileProviderTestResult> TestAsync(
            FileProviderProfile profile,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileProviderTestResult(
                true,
                "test_profile_ready",
                "The test profile is available."));

        public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(
                new FilePanelPage([], null)));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class EmptySecretVault : ISecretVault
    {
        public SecretVaultAvailability Availability { get; } = new(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.MemoryOnly,
            SecretVaultCapabilities.ListMetadata,
            "test",
            "test_vault",
            "Test vault.");

        public void Dispose()
        {
        }

        public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
            ListSecretMetadataRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([]));

        public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
            CreateSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
            ReplaceSecretRequest request,
            SecretMaterial material,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
            RelabelSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
            GetSecretMetadataRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SuccessfulAuditStore : IAuditStore
    {
        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success([]));
    }

    private sealed class RecordingRecoveryStore : IRuntimeRecoveryStore
    {
        private readonly List<RuntimeRecoverySnapshot> _snapshots = [];

        public IReadOnlyList<RuntimeRecoverySnapshot> Snapshots
        {
            get
            {
                lock (_snapshots)
                {
                    return [.. _snapshots];
                }
            }
        }

        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_snapshots)
            {
                _snapshots.Add(snapshot);
            }

            return ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryRecentSessionStore :
        IRecentSessionStore,
        IRecentSessionRetentionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<SessionId, RecentSessionRecord> _records = [];
        private StoredRecentSessionRetentionPolicy _retention = new(
            RecentSessionRetentionPolicy.Default,
            1);

        public MemoryRecentSessionStore(params RecentSessionRecord[] records)
        {
            foreach (var record in records)
            {
                _records.Add(record.SessionId, record);
            }
        }

        public IReadOnlyList<RecentSessionRecord> Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records.Values.OrderBy(record => record.StartedAt)];
                }
            }
        }

        public bool FailReadsUntilCleared { get; set; }

        public bool FailCompletionWrites { get; set; }

        public RecentSessionStoreErrorCode ReadFailureCode { get; set; } =
            RecentSessionStoreErrorCode.InvalidHistoryData;

        public void ChangeRetentionExternally(RecentSessionRetentionPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            lock (_gate)
            {
                _retention = new StoredRecentSessionRetentionPolicy(
                    policy,
                    _retention.Revision + 1);
            }
        }

        public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
            RecentSessionRecord recentSession,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_retention.Policy.IsEnabled)
                {
                    _records.TryAdd(recentSession.SessionId, recentSession);
                }
            }

            return ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
            RecentSessionCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailCompletionWrites)
            {
                return ValueTask.FromResult(
                    RecentSessionStoreResult<Unit>.Failure(
                        new RecentSessionStoreError(
                            RecentSessionStoreErrorCode.StorageFailure,
                            "Completion persistence failed.")));
            }

            lock (_gate)
            {
                if (_records.TryGetValue(completion.SessionId, out var record))
                {
                    _records[completion.SessionId] = new RecentSessionRecord(
                        record.SessionId,
                        record.SourceDefinition,
                        record.Kind,
                        record.Title,
                        record.StartedAt,
                        completion.EndedAt,
                        completion.Outcome);
                }
            }

            return ValueTask.FromResult(RecentSessionStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>> ListRecentAsync(
            RecentSessionQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailReadsUntilCleared)
            {
                return ValueTask.FromResult(
                    RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Failure(
                        new RecentSessionStoreError(
                            ReadFailureCode,
                            "A retained history row is unreadable.")));
            }

            IReadOnlyList<RecentSessionRecord> records;
            lock (_gate)
            {
                records = [.. _records.Values
                    .Where(record => query.SourceKind is null
                        || record.SourceDefinition.Kind == query.SourceKind)
                    .OrderByDescending(record => record.LastUsedAt)
                    .Take(query.Limit)];
            }

            return ValueTask.FromResult(
                RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success(records));
        }

        public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interrupted = 0;
            lock (_gate)
            {
                foreach (var record in _records.Values
                    .Where(record => record.Outcome == RecentSessionOutcome.Active)
                    .ToArray())
                {
                    _records[record.SessionId] = new RecentSessionRecord(
                        record.SessionId,
                        record.SourceDefinition,
                        record.Kind,
                        record.Title,
                        record.StartedAt,
                        DateTimeOffset.UtcNow,
                        RecentSessionOutcome.Interrupted);
                    interrupted++;
                }
            }

            return ValueTask.FromResult(RecentSessionStoreResult<int>.Success(interrupted));
        }

        public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
            DateTimeOffset through,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = 0;
            lock (_gate)
            {
                foreach (var sessionId in _records.Values
                    .Where(record => record.LastUsedAt <= through)
                    .Select(record => record.SessionId)
                    .ToArray())
                {
                    removed += _records.Remove(sessionId) ? 1 : 0;
                }
            }

            return ValueTask.FromResult(RecentSessionStoreResult<int>.Success(removed));
        }

        public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int removed;
            lock (_gate)
            {
                removed = _records.Count;
                _records.Clear();
                FailReadsUntilCleared = false;
            }

            return ValueTask.FromResult(RecentSessionStoreResult<int>.Success(removed));
        }

        public ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>>
            GetRetentionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>.Success(
                        _retention));
            }
        }

        public ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
            UpdateRetentionAsync(
                RecentSessionRetentionPolicy policy,
                long expectedRevision,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_retention.Revision != expectedRevision)
                {
                    return ValueTask.FromResult(
                        RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Failure(
                            new RecentSessionStoreError(
                                RecentSessionStoreErrorCode.Conflict,
                                "Retention revision changed.")));
                }

                var before = _records.Count;
                if (!policy.IsEnabled)
                {
                    _records.Clear();
                }
                else
                {
                    foreach (var sessionId in _records.Values
                        .OrderByDescending(record => record.LastUsedAt)
                        .Skip(policy.MaximumEntries)
                        .Select(record => record.SessionId)
                        .ToArray())
                    {
                        _records.Remove(sessionId);
                    }
                }

                _retention = new StoredRecentSessionRetentionPolicy(
                    policy,
                    expectedRevision + 1);
                return ValueTask.FromResult(
                    RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Success(
                        new RecentSessionRetentionUpdateResult(
                            _retention,
                            before - _records.Count)));
            }
        }
    }
}
