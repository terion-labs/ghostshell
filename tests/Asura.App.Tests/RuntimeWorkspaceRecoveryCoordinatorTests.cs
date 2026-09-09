using Asura.App;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class RuntimeWorkspaceRecoveryCoordinatorTests
{
    [Fact]
    public async Task Queue_snapshot_serializes_the_current_exact_runtime_graph()
    {
        var store = new RecordingStore();
        var writer = new RuntimeRecoveryWriter(
            store,
            InitializeRun("recovery-owner"),
            TimeProvider.System);
        var workspace = CreateWorkspace();
        var autoSaves = 0;
        using var recovery = new RuntimeWorkspaceRecoveryCoordinator(
            writer,
            () => workspace,
            () => null,
            () => false,
            () => autoSaves++,
            _ => { },
            ImmediateDispatcher.Instance);

        recovery.QueueSnapshot();
        var flushed = await writer.FlushAsync(CancellationToken.None);

        Assert.True(flushed.IsSuccess);
        Assert.Equal(1, autoSaves);
        var snapshot = Assert.Single(store.Snapshots);
        Assert.Equal(RuntimeWorkspaceRecoveryCodec.SnapshotKey, snapshot.Key);
        Assert.True(RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            snapshot,
            out var payload,
            out var error),
            error);
        Assert.Equal(workspace.Name, payload!.Workspace!.Name);
        Assert.Equal(workspace.ActiveTab!.Id.Value, payload.Workspace.ActiveTabKey);
    }

    [Fact]
    public void Missing_writer_still_schedules_durable_workspace_auto_save()
    {
        var autoSaves = 0;
        using var recovery = new RuntimeWorkspaceRecoveryCoordinator(
            null,
            () => null,
            () => null,
            () => false,
            () => autoSaves++,
            _ => { },
            ImmediateDispatcher.Instance);

        recovery.QueueSnapshot();

        Assert.Equal(1, autoSaves);
    }

    [Fact]
    public void Disposed_recovery_owner_rejects_new_tracking_and_snapshots()
    {
        var recovery = new RuntimeWorkspaceRecoveryCoordinator(
            null,
            () => null,
            () => null,
            () => false,
            () => { },
            _ => { },
            ImmediateDispatcher.Instance);
        recovery.Dispose();

        Assert.Throws<ObjectDisposedException>(() => recovery.Track(CreateWorkspace()));
        Assert.Throws<ObjectDisposedException>(recovery.QueueSnapshot);
    }

    private static RuntimeWorkspaceViewModel CreateWorkspace()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Recovery",
            "#123456",
            []);
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "Tab", "test");
        tab.AddPanel(new PanelPlaceholderViewModel(PanelInstanceId.New()));
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        return workspace;
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

    private sealed class RecordingStore : IRuntimeRecoveryStore
    {
        public List<RuntimeRecoverySnapshot> Snapshots { get; } = [];

        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public static ImmediateDispatcher Instance { get; } = new();

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
