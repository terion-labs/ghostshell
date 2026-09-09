using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class MainWindowTabReorderTests
{
    [Fact]
    public async Task AcceptedMoveCommitsExactOrderAndPreservesRuntimeIdentities()
    {
        var fixture = CreateFixture(activeTabIndex: 1);
        using var viewModel = fixture.ViewModel;
        var runtime = fixture.Runtime;
        var source = runtime.Tabs[0];
        var anchor = runtime.Tabs[2];
        var originalTabs = runtime.Tabs.ToDictionary(tab => tab.Id);
        var originalPanels = runtime.Tabs
            .SelectMany(tab => tab.Panels)
            .ToDictionary(panel => panel.Id);
        var originalSessions = fixture.Session.Current!.Workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .ToDictionary(panel => panel.Id, panel => panel.SessionId);
        var activeTab = runtime.ActiveTab;
        var activePanel = runtime.ActiveTab!.ActivePanel;
        viewModel.ShowWorkspace();
        viewModel.ShowOverlay(ShellOverlay.CommandPalette);

        Assert.True(await viewModel.MoveTabAsync(
            source.Id,
            anchor.Id,
            RuntimeTabPlacement.After));

        Assert.Equal(["Beta", "Gamma", "Alpha"], runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(2, runtime.HostRevision);
        var call = Assert.Single(fixture.Session.Registrations);
        Assert.Equal(1, call.Context.ExpectedRevision);
        Assert.Equal(
            ["Beta", "Gamma", "Alpha"],
            call.Request.Workspace.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(runtime.ActiveTab!.Id, call.Request.Workspace.ActiveTabId);
        Assert.Equal(
            ["Beta", "Gamma", "Alpha"],
            fixture.Session.Current!.Workspace.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.All(runtime.Tabs, tab => Assert.Same(originalTabs[tab.Id], tab));
        Assert.All(
            runtime.Tabs.SelectMany(tab => tab.Panels),
            panel => Assert.Same(originalPanels[panel.Id], panel));
        Assert.Same(activeTab, runtime.ActiveTab);
        Assert.Same(activePanel, runtime.ActiveTab.ActivePanel);
        Assert.Equal(
            originalSessions,
            fixture.Session.Current.Workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .ToDictionary(panel => panel.Id, panel => panel.SessionId));
        Assert.Equal("Moved tab “Alpha” to position 3 of 3.", viewModel.TabReorderStatus);
        Assert.False(CommandResult(viewModel, BuiltInCommands.MoveTabLeft).IsAvailable);
        Assert.True(CommandResult(viewModel, BuiltInCommands.MoveTabRight).IsAvailable);
    }

    [Fact]
    public async Task DelayedHostReceiptDoesNotOptimisticallyMutateOrderOrRevision()
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        fixture.Session.DelayNextRegistration = true;
        var move = viewModel.MoveTabAsync(
            fixture.Runtime.Tabs[2].Id,
            fixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before);
        await fixture.Session.RegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, fixture.Runtime.HostRevision);
        Assert.Equal(string.Empty, viewModel.TabReorderStatus);

        fixture.Session.AllowRegistration.TrySetResult();

        Assert.True(await move);
        Assert.Equal(["Gamma", "Alpha", "Beta"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(2, fixture.Runtime.HostRevision);
    }

    [Fact]
    public async Task RejectionAndInvalidReceiptLeaveOrderAndRevisionUnchanged()
    {
        var rejectedFixture = CreateFixture();
        using var rejectedViewModel = rejectedFixture.ViewModel;
        rejectedFixture.Session.RejectNextRegistration = true;

        Assert.False(await rejectedViewModel.MoveTabAsync(
            rejectedFixture.Runtime.Tabs[2].Id,
            rejectedFixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before));

        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            rejectedFixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, rejectedFixture.Runtime.HostRevision);
        Assert.Equal(string.Empty, rejectedViewModel.TabReorderStatus);
        Assert.Contains(
            "revision_conflict",
            rejectedViewModel.OperationError,
            StringComparison.Ordinal);

        var invalidFixture = CreateFixture();
        using var invalidViewModel = invalidFixture.ViewModel;
        invalidFixture.Session.ReturnInvalidReceipt = true;

        Assert.False(await invalidViewModel.MoveTabAsync(
            invalidFixture.Runtime.Tabs[2].Id,
            invalidFixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before));

        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            invalidFixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, invalidFixture.Runtime.HostRevision);
        Assert.Equal(string.Empty, invalidViewModel.TabReorderStatus);
        Assert.Contains(
            "invalid tab reorder receipt",
            invalidViewModel.OperationError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationLeavesOrderAndRevisionUnchanged()
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        fixture.Session.DelayNextRegistration = true;
        using var cancellation = new CancellationTokenSource();
        var move = viewModel.MoveTabAsync(
            fixture.Runtime.Tabs[2].Id,
            fixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before,
            cancellation.Token);
        await fixture.Session.RegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, fixture.Runtime.HostRevision);
        Assert.Equal(string.Empty, viewModel.TabReorderStatus);
    }

    [Fact]
    public async Task NewerRevisionConflictRejectsStaleMoveWithoutRefreshingTheView()
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        fixture.Session.RejectWithNewerRevision = true;

        Assert.False(await viewModel.MoveTabAsync(
            fixture.Runtime.Tabs[2].Id,
            fixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before));

        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, fixture.Runtime.HostRevision);
        Assert.Single(fixture.Session.Registrations);
        Assert.Equal(string.Empty, viewModel.TabReorderStatus);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task SameOrLowerRevisionReceiptCannotCommitAnExactTabOrder(long receiptRevision)
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        fixture.Session.NextReceiptRevision = receiptRevision;

        Assert.False(await viewModel.MoveTabAsync(
            fixture.Runtime.Tabs[2].Id,
            fixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before));

        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, fixture.Runtime.HostRevision);
        Assert.Equal(1, fixture.Runtime.HostSequence);
        Assert.Equal(string.Empty, viewModel.TabReorderStatus);
        Assert.Contains(
            "invalid tab reorder receipt",
            viewModel.OperationError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewerRevisionReceiptCannotRegressTheGraphSequence()
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        fixture.Session.NextReceiptSequence = 0;

        Assert.False(await viewModel.MoveTabAsync(
            fixture.Runtime.Tabs[2].Id,
            fixture.Runtime.Tabs[0].Id,
            RuntimeTabPlacement.Before));

        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, fixture.Runtime.HostRevision);
        Assert.Equal(1, fixture.Runtime.HostSequence);
        Assert.Equal(string.Empty, viewModel.TabReorderStatus);
        Assert.Contains(
            "invalid tab reorder receipt",
            viewModel.OperationError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOpAndBoundaryMovesDoNotCallTheHost()
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        var alpha = fixture.Runtime.Tabs[0];
        var beta = fixture.Runtime.Tabs[1];

        Assert.False(await viewModel.MoveTabAsync(
            alpha.Id,
            beta.Id,
            RuntimeTabPlacement.Before));
        Assert.False(await viewModel.MoveTabAsync(
            alpha.Id,
            alpha.Id,
            RuntimeTabPlacement.After));
        Assert.False(await viewModel.MoveTabAsync(
            new TabInstanceId("missing-tab"),
            beta.Id,
            RuntimeTabPlacement.After));
        Assert.False(await viewModel.MoveActiveTabAsync(-1));

        Assert.Empty(fixture.Session.Registrations);
        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(1, fixture.Runtime.HostRevision);
        Assert.Equal(string.Empty, viewModel.TabReorderStatus);
    }

    [Fact]
    public async Task ActiveTabMoveUsesOnePositionWithoutWrapping()
    {
        var fixture = CreateFixture(activeTabIndex: 1);
        using var viewModel = fixture.ViewModel;
        var activeTab = fixture.Runtime.ActiveTab;

        Assert.True(await viewModel.MoveActiveTabAsync(-1));

        Assert.Equal(["Beta", "Alpha", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Same(activeTab, fixture.Runtime.ActiveTab);
        Assert.False(await viewModel.MoveActiveTabAsync(-1));
        Assert.Single(fixture.Session.Registrations);
    }

    [Fact]
    public async Task QueuedActiveMoveResolvesItsAdjacentAnchorAfterDelayedDragCommits()
    {
        var fixture = CreateFixture();
        using var viewModel = fixture.ViewModel;
        fixture.Session.DelayNextRegistration = true;
        var beta = fixture.Runtime.Tabs[1];
        var gamma = fixture.Runtime.Tabs[2];
        var delayedDrag = viewModel.MoveTabAsync(
            beta.Id,
            gamma.Id,
            RuntimeTabPlacement.After);
        await fixture.Session.RegistrationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedKeyboardMove = viewModel.MoveActiveTabAsync(1);

        Assert.Equal(["Alpha", "Beta", "Gamma"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Single(fixture.Session.Registrations);
        fixture.Session.AllowRegistration.TrySetResult();

        Assert.True(await delayedDrag);
        Assert.True(await queuedKeyboardMove);
        Assert.Equal(["Gamma", "Alpha", "Beta"], fixture.Runtime.Tabs.Select(tab => tab.Title), StringComparer.Ordinal);
        Assert.Equal(
            [
                ["Alpha", "Gamma", "Beta"],
                ["Gamma", "Alpha", "Beta"],
            ],
            fixture.Session.Registrations.Select(call =>
                call.Request.Workspace.Tabs.Select(tab => tab.Title).ToArray()));
        Assert.Equal(
            [1L, 2L],
            fixture.Session.Registrations.Select(call => call.Context.ExpectedRevision));
        Assert.Equal(3, fixture.Runtime.HostRevision);
    }

    private static LauncherSearchResultViewModel CommandResult(
        MainWindowViewModel viewModel,
        CommandId commandId) =>
        Assert.Single(
            viewModel.LauncherSearchResults,
            result => result.Target is LauncherSearchTarget.Command command
                && command.Id == commandId);

    private static TabReorderFixture CreateFixture(int activeTabIndex = 0)
    {
        var client = DispatchProxy.Create<ISessionHostClient, TabReorderSessionClient>();
        var session = (TabReorderSessionClient)(object)client;
        var viewModel = new MainWindowViewModel(
            client,
            DispatchProxy.Create<IDefinitionCatalog, EmptyCatalog>(),
            DispatchProxy.Create<IConnectionRuntime, EmptyDependency>(),
            DispatchProxy.Create<ISecretVault, EmptyDependency>(),
            DispatchProxy.Create<IFilePanelClient, EmptyDependency>(),
            DispatchProxy.Create<IFileTransferQueueClient, EmptyDependency>(),
            new TerminalStartupCommandDispatcher(
                DispatchProxy.Create<IAuditStore, EmptyDependency>(),
                TimeProvider.System));
        var runtime = new RuntimeWorkspaceViewModel(
            new WorkspaceInstanceId("tab-reorder-workspace"),
            "Tab reorder workspace",
            "#BB7A55",
            []);
        runtime.Tabs.Add(CreateTab("alpha", "Alpha"));
        runtime.Tabs.Add(CreateTab("beta", "Beta"));
        runtime.Tabs.Add(CreateTab("gamma", "Gamma"));
        runtime.ActiveTab = runtime.Tabs[activeTabIndex];
        var graph = CaptureGraph(runtime);
        runtime.ApplyHostProjection(graph, revision: 1, sequence: 1);
        session.Initialize(viewModel.WindowId, graph);

        var setter = typeof(MainWindowViewModel)
            .GetProperty(nameof(MainWindowViewModel.RuntimeWorkspace))!
            .GetSetMethod(nonPublic: true)!;
        setter.Invoke(viewModel, [runtime]);
        return new TabReorderFixture(viewModel, runtime, session);
    }

    private static RuntimeTabViewModel CreateTab(string id, string title)
    {
        var tab = new RuntimeTabViewModel(
            new TabInstanceId($"{id}-tab"),
            title,
            "TEST");
        tab.AddPanel(new TestRuntimePanel(
            new PanelInstanceId($"{id}-panel"),
            $"{title} panel",
            new SessionId($"{id}-session")));
        return tab;
    }

    private static WorkspaceInstance CaptureGraph(RuntimeWorkspaceViewModel runtime) =>
        new(
            runtime.Id,
            runtime.Name,
            runtime.Tabs.Select(tab => new TabInstance(
                tab.Id,
                tab.Title,
                tab.Panels.Select(panel => new PanelInstance(
                    panel.Id,
                    panel.Kind,
                    panel.Title,
                    ((TestRuntimePanel)panel).SessionId)),
                tab.ActivePanelId!.Value)),
            runtime.ActiveTab!.Id);

    private sealed record TabReorderFixture(
        MainWindowViewModel ViewModel,
        RuntimeWorkspaceViewModel Runtime,
        TabReorderSessionClient Session);

    private sealed class TestRuntimePanel(
        PanelInstanceId id,
        string title,
        SessionId sessionId)
        : RuntimePanelViewModel(id, PanelKind.Terminal, title, "TEST")
    {
        public SessionId SessionId { get; } = sessionId;
    }

    public sealed record WorkspaceRegistration(
        RegisterWorkspaceGraphRequest Request,
        OperationContext Context);

    public class TabReorderSessionClient : DispatchProxy
    {
        private readonly List<WorkspaceRegistration> _registrations = [];

        public bool DelayNextRegistration { get; set; }

        public bool RejectNextRegistration { get; set; }

        public bool RejectWithNewerRevision { get; set; }

        public bool ReturnInvalidReceipt { get; set; }

        public long? NextReceiptRevision { get; set; }

        public long? NextReceiptSequence { get; set; }

        public TaskCompletionSource RegistrationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowRegistration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<WorkspaceRegistration> Registrations => _registrations;

        public WorkspaceGraphSnapshot? Current { get; private set; }

        public void Initialize(WindowInstanceId windowId, WorkspaceInstance workspace)
        {
            Current = new WorkspaceGraphSnapshot(
                windowId,
                workspace,
                revision: 1,
                lastSequence: 1);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(ISessionHostClient.RegisterWorkspaceGraphAsync)
                    when args is
                    [
                        RegisterWorkspaceGraphRequest request,
                        OperationContext context,
                        CancellationToken cancellationToken,
                    ] =>
                    RegisterAsync(request, context, cancellationToken),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private async ValueTask<HostResult<WorkspaceGraphSnapshot>> RegisterAsync(
            RegisterWorkspaceGraphRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            _registrations.Add(new WorkspaceRegistration(request, context));
            if (DelayNextRegistration)
            {
                DelayNextRegistration = false;
                RegistrationEntered.TrySetResult();
                await AllowRegistration.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var current = Current
                ?? throw new InvalidOperationException("The test workspace was not initialized.");
            if (RejectWithNewerRevision)
            {
                RejectWithNewerRevision = false;
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.RevisionConflict,
                        "The test host has a newer workspace graph."),
                    current.Revision + 1);
            }

            if (RejectNextRegistration || context.ExpectedRevision != current.Revision)
            {
                RejectNextRegistration = false;
                return HostResult<WorkspaceGraphSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.RevisionConflict,
                        "The test host rejected the stale tab order."),
                    current.Revision);
            }

            var revision = current.Revision + 1;
            if (ReturnInvalidReceipt)
            {
                ReturnInvalidReceipt = false;
                return HostResult<WorkspaceGraphSnapshot>.Succeed(
                    new WorkspaceGraphSnapshot(
                        current.WindowId,
                        current.Workspace,
                        revision,
                        revision),
                    revision);
            }

            var sessions = current.Workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .ToDictionary(panel => panel.Id, panel => panel.SessionId);
            var reconciled = new WorkspaceInstance(
                request.Workspace.Id,
                request.Workspace.Title,
                request.Workspace.Tabs.Select(tab => new TabInstance(
                    tab.Id,
                    tab.Title,
                    tab.Panels.Select(panel => new PanelInstance(
                        panel.Id,
                        panel.Kind,
                        panel.Title,
                        sessions[panel.Id])),
                    tab.ActivePanelId)),
                request.Workspace.ActiveTabId);
            if (NextReceiptRevision is not null || NextReceiptSequence is not null)
            {
                var receiptRevision = NextReceiptRevision ?? revision;
                var receiptSequence = NextReceiptSequence ?? revision;
                NextReceiptRevision = null;
                NextReceiptSequence = null;
                var invalidReceipt = new WorkspaceGraphSnapshot(
                    request.WindowId,
                    reconciled,
                    receiptRevision,
                    receiptSequence);
                return HostResult<WorkspaceGraphSnapshot>.Succeed(
                    invalidReceipt,
                    receiptRevision);
            }

            Current = new WorkspaceGraphSnapshot(
                request.WindowId,
                reconciled,
                revision,
                revision);
            return HostResult<WorkspaceGraphSnapshot>.Succeed(Current, revision);
        }
    }

    public class EmptyCatalog : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            return targetMethod?.Name switch
            {
                "get_Snapshot" => DefinitionCatalogSnapshot.Empty,
                "add_Changed" or "remove_Changed" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }

    public class EmptyDependency : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            return targetMethod?.Name switch
            {
                "get_Availability" => new SecretVaultAvailability(
                    SecretVaultAvailabilityState.Available,
                    SecretVaultPersistenceKind.MemoryOnly,
                    SecretVaultCapabilities.ListMetadata,
                    "test",
                    "test_vault",
                    "Test vault"),
                "ListMetadataAsync" => ValueTask.FromResult(
                    SecretVaultResult<IReadOnlyList<SecretMetadata>>.Succeed([])),
                "get_Profiles" => Array.Empty<FileProviderProfileDescriptor>(),
                "get_Transfers" => Array.Empty<FilePanelTransferSnapshot>(),
                "add_TransfersChanged" or "remove_TransfersChanged" or "Dispose" => null,
                _ => throw new NotSupportedException(targetMethod?.Name),
            };
        }
    }
}
