using System.Reflection;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class RuntimeWorkspaceGraphCoordinatorTests
{
    [Fact]
    public async Task Serialization_lease_blocks_following_mutations_until_release()
    {
        var runtime = CreateWorkspace();
        using var coordinator = CreateCoordinator(
            WindowInstanceId.New(),
            () => runtime);
        var first = await coordinator.EnterAsync();

        var pending = coordinator.EnterAsync().AsTask();
        Assert.False(pending.IsCompleted);

        first.Dispose();
        using var second = await pending;
        Assert.False(coordinator.IsStopping);
    }

    [Fact]
    public async Task Quiesce_cancels_waiting_mutations_and_closes_the_lifetime()
    {
        var runtime = CreateWorkspace();
        using var coordinator = CreateCoordinator(
            WindowInstanceId.New(),
            () => runtime);
        using var first = await coordinator.EnterAsync();
        var pending = coordinator.EnterAsync().AsTask();

        await coordinator.QuiesceAsync();

        Assert.True(coordinator.IsStopping);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Receipt_validation_requires_exact_window_topology_focus_and_advanced_cursors()
    {
        var runtime = CreateWorkspace();
        var windowId = WindowInstanceId.New();
        var graph = RuntimeWorkspaceGraphProjection.Capture(runtime);
        var coordinator = CreateCoordinator(windowId, () => runtime);
        var accepted = Success(windowId, graph, revision: 1, sequence: 1);
        var wrongWindow = Success(WindowInstanceId.New(), graph, revision: 1, sequence: 1);
        var changedFocus = new WorkspaceInstance(
            graph.Id,
            graph.Title,
            graph.Tabs,
            graph.Tabs[1].Id);
        var focusReceipt = Success(windowId, changedFocus, revision: 1, sequence: 1);

        Assert.True(coordinator.IsExpectedReceipt(accepted, graph, 0, 0));
        Assert.False(coordinator.IsExpectedReceipt(wrongWindow, graph, 0, 0));
        Assert.False(coordinator.IsExpectedReceipt(accepted, graph, 1, 0));
        Assert.False(coordinator.IsExpectedReceipt(focusReceipt, graph, 0, 0));
        Assert.True(coordinator.IsExpectedReconciledReceipt(
            focusReceipt,
            graph,
            0,
            0));
    }

    [Fact]
    public void Applying_authoritative_projection_advances_cursor_and_notifies_once()
    {
        var runtime = CreateWorkspace();
        var graph = RuntimeWorkspaceGraphProjection.Capture(runtime);
        var windowId = WindowInstanceId.New();
        var applied = 0;
        var errors = new List<string>();
        var coordinator = new RuntimeWorkspaceGraphCoordinator(
            windowId,
            () => runtime,
            errors.Add,
            () => applied++);

        Assert.True(coordinator.TryApplyProjection(
            runtime,
            windowId,
            graph,
            revision: 4,
            sequence: 7,
            "workspace event"));

        Assert.Equal(4, runtime.HostRevision);
        Assert.Equal(7, runtime.HostSequence);
        Assert.Equal(1, applied);
        Assert.Empty(errors);
    }

    [Fact]
    public void Applying_projection_rejects_a_foreign_window_or_changed_identity_graph()
    {
        var runtime = CreateWorkspace();
        var graph = RuntimeWorkspaceGraphProjection.Capture(runtime);
        var windowId = WindowInstanceId.New();
        var errors = new List<string>();
        var coordinator = new RuntimeWorkspaceGraphCoordinator(
            windowId,
            () => runtime,
            errors.Add,
            () => throw new InvalidOperationException("Must not notify."));
        var panel = graph.Tabs[0].Panels[0];
        var changedPanel = new PanelInstance(
            PanelInstanceId.New(),
            panel.Kind,
            panel.Title);
        var changedGraph = ReplaceFirstPanel(graph, changedPanel);

        Assert.False(coordinator.TryApplyProjection(
            runtime,
            WindowInstanceId.New(),
            graph,
            1,
            1,
            "workspace event"));
        Assert.False(coordinator.TryApplyProjection(
            runtime,
            windowId,
            changedGraph,
            1,
            1,
            "workspace event"));
        Assert.Equal(0, runtime.HostRevision);
        Assert.Equal(0, runtime.HostSequence);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Validated_older_receipt_does_not_regress_a_newer_matching_projection()
    {
        var runtime = CreateWorkspace();
        var graph = RuntimeWorkspaceGraphProjection.Capture(runtime);
        var windowId = WindowInstanceId.New();
        runtime.ApplyHostProjection(graph, revision: 3, sequence: 5);
        var coordinator = CreateCoordinator(windowId, () => runtime);

        Assert.True(coordinator.TryApplyValidatedReceipt(
            runtime,
            Success(windowId, graph, revision: 2, sequence: 4),
            "panel addition"));

        Assert.Equal(3, runtime.HostRevision);
        Assert.Equal(5, runtime.HostSequence);
    }

    [Fact]
    public async Task Accepted_tab_transfer_commits_both_runtime_graphs_once()
    {
        var source = CreateWorkspace();
        var destination = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Destination",
            "#654321",
            []);
        destination.Tabs.Add(CreateTab("Destination tab"));
        destination.ActiveTab = destination.Tabs[0];
        var sourceBefore = RuntimeWorkspaceGraphProjection.Capture(source);
        var destinationBefore = RuntimeWorkspaceGraphProjection.Capture(destination);
        source.ApplyHostProjection(sourceBefore, 1, 1);
        destination.ApplyHostProjection(destinationBefore, 1, 1);
        var moved = source.Tabs[0];
        var movedGraph = sourceBefore.Tabs[0];
        var sourceAfter = new WorkspaceInstance(
            source.Id,
            source.Name,
            sourceBefore.Tabs.Skip(1),
            sourceBefore.Tabs[1].Id);
        var destinationAfter = new WorkspaceInstance(
            destination.Id,
            destination.Name,
            [.. destinationBefore.Tabs, movedGraph],
            movedGraph.Id);
        var windowId = WindowInstanceId.New();
        var client = TransferClientProxy.Create(request =>
            HostResult<WorkspaceGraphTransferReceipt>.Succeed(
                new WorkspaceGraphTransferReceipt(
                    Guid.NewGuid(),
                    new WorkspaceGraphSnapshot(
                        windowId,
                        request.Source,
                        2,
                        2),
                    new WorkspaceGraphSnapshot(
                        windowId,
                        request.Destination,
                        2,
                        2),
                    request.TabId,
                    null,
                    []),
                2));
        var projections = 0;
        using var coordinator = new RuntimeWorkspaceGraphCoordinator(
            client,
            ClientId.New(),
            windowId,
            new ImmediateDispatcher(),
            TimeProvider.System,
            () => source,
            _ => { },
            _ => { },
            () => projections++,
            _ => { });

        var accepted = await coordinator.TransferTabUnderGateAsync(
            source,
            destination,
            new TransferWorkspaceTabRequest(
                windowId,
                sourceAfter,
                1,
                windowId,
                destinationAfter,
                1,
                moved.Id),
            () =>
            {
                source.Tabs.Remove(moved);
                source.ActiveTab = source.Tabs[0];
                destination.Tabs.Add(moved);
                destination.ActiveTab = moved;
            },
            CancellationToken.None);

        Assert.True(accepted);
        Assert.DoesNotContain(moved, source.Tabs);
        Assert.Same(moved, destination.Tabs[1]);
        Assert.Equal(2, source.HostRevision);
        Assert.Equal(2, destination.HostRevision);
        Assert.Equal(1, projections);
    }

    [Fact]
    public async Task Rejected_tab_transfer_leaves_both_runtime_graphs_unchanged()
    {
        var source = CreateWorkspace();
        var destination = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Destination",
            "#654321",
            []);
        destination.Tabs.Add(CreateTab("Destination tab"));
        destination.ActiveTab = destination.Tabs[0];
        var sourceBefore = RuntimeWorkspaceGraphProjection.Capture(source);
        var destinationBefore = RuntimeWorkspaceGraphProjection.Capture(destination);
        source.ApplyHostProjection(sourceBefore, 1, 1);
        destination.ApplyHostProjection(destinationBefore, 1, 1);
        var moved = source.Tabs[0];
        var sourceAfter = new WorkspaceInstance(
            source.Id,
            source.Name,
            sourceBefore.Tabs.Skip(1),
            sourceBefore.Tabs[1].Id);
        var destinationAfter = new WorkspaceInstance(
            destination.Id,
            destination.Name,
            [.. destinationBefore.Tabs, sourceBefore.Tabs[0]],
            sourceBefore.Tabs[0].Id);
        var errors = new List<string>();
        var windowId = WindowInstanceId.New();
        var client = TransferClientProxy.Create(_ =>
            HostResult<WorkspaceGraphTransferReceipt>.Fail(
                HostError.Create(
                    HostErrorCode.RevisionConflict,
                    "The destination changed."),
                2));
        using var coordinator = new RuntimeWorkspaceGraphCoordinator(
            client,
            ClientId.New(),
            windowId,
            new ImmediateDispatcher(),
            TimeProvider.System,
            () => source,
            _ => { },
            errors.Add,
            () => throw new InvalidOperationException("Must not project."),
            _ => { });
        var committed = false;

        var accepted = await coordinator.TransferTabUnderGateAsync(
            source,
            destination,
            new TransferWorkspaceTabRequest(
                windowId,
                sourceAfter,
                1,
                windowId,
                destinationAfter,
                1,
                moved.Id),
            () => committed = true,
            CancellationToken.None);

        Assert.False(accepted);
        Assert.False(committed);
        Assert.Contains(moved, source.Tabs);
        Assert.DoesNotContain(moved, destination.Tabs);
        Assert.Equal(1, source.HostRevision);
        Assert.Equal(1, destination.HostRevision);
        Assert.Contains("revision_conflict", Assert.Single(errors), StringComparison.Ordinal);
    }

    private static RuntimeWorkspaceGraphCoordinator CreateCoordinator(
        WindowInstanceId windowId,
        Func<RuntimeWorkspaceViewModel?> currentWorkspace) =>
        new(windowId, currentWorkspace, _ => { }, () => { });

    private static HostResult<WorkspaceGraphSnapshot>.Success Success(
        WindowInstanceId windowId,
        WorkspaceInstance graph,
        long revision,
        long sequence) =>
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(
            HostResult<WorkspaceGraphSnapshot>.Succeed(
                new WorkspaceGraphSnapshot(windowId, graph, revision, sequence),
                revision));

    private static RuntimeWorkspaceViewModel CreateWorkspace()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Runtime",
            "#123456",
            []);
        var first = CreateTab("First");
        var second = CreateTab("Second");
        workspace.Tabs.Add(first);
        workspace.Tabs.Add(second);
        workspace.ActiveTab = first;
        return workspace;
    }

    private static RuntimeTabViewModel CreateTab(string title)
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), title, "test");
        tab.AddPanel(new PanelPlaceholderViewModel(PanelInstanceId.New()));
        return tab;
    }

    private static WorkspaceInstance ReplaceFirstPanel(
        WorkspaceInstance workspace,
        PanelInstance panel)
    {
        var tab = workspace.Tabs[0];
        var replacement = new TabInstance(tab.Id, tab.Title, [panel], panel.Id);
        return new WorkspaceInstance(
            workspace.Id,
            workspace.Title,
            [replacement, .. workspace.Tabs.Skip(1)],
            workspace.ActiveTabId);
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    public class TransferClientProxy : DispatchProxy
    {
        private Func<
            TransferWorkspaceTabRequest,
            HostResult<WorkspaceGraphTransferReceipt>>? _transfer;

        public static ISessionHostClient Create(
            Func<
                TransferWorkspaceTabRequest,
                HostResult<WorkspaceGraphTransferReceipt>> transfer)
        {
            var client = DispatchProxy.Create<ISessionHostClient, TransferClientProxy>();
            ((TransferClientProxy)(object)client)._transfer = transfer;
            return client;
        }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISessionHostClient.TransferWorkspaceTabAsync)
                && args?[0] is TransferWorkspaceTabRequest request)
            {
                return ValueTask.FromResult(_transfer!(request));
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
