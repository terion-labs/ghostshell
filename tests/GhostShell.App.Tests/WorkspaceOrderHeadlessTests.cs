using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class WorkspaceOrderHeadlessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Hover_save_is_visible_only_when_needed_and_does_not_open_close_or_drag(bool expandsLeft, bool canClose) => RunAsync(async () =>
    {
        using var model = CreateModel(out var catalog);
        var view = new WorkspaceView { DataContext = model };
        var saved = 0;
        var opened = 0;
        var closed = 0;
        view.SaveWorkspaceLayoutRequested += (_, _) => saved++;
        view.OpenWorkspaceRequested += (_, _) => opened++;
        view.CloseWorkspaceRequested += (_, _) => closed++;
        var window = new Window { Width = 1000, Height = 700, Content = view, DataContext = model };
        // Headless startup does not attach the desktop appearance publisher.
        window.Resources["ShellTileSizeMd"] = 32d;
        window.Resources["ShellWorkspaceRailTileExpandedWidth"] = 64d;
        window.Resources["ShellWorkspaceRailTileSaveWidth"] = 96d;
        _ = new WorkspaceRailDragController(window);
        window.Show();
        window.UpdateLayout();
        try
        {
            var tile = Tiles(view)[0];
            tile.ExpandsLeft = expandsLeft;
            tile.CanClose = canClose;
            var save = tile.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_Save");
            Assert.False(save.IsVisible);
            model.Workspaces[0].CanSaveLayout = true;
            var surface = tile.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_Surface" && ReferenceEquals(border.TemplatedParent, tile));
            surface.Transitions = null;
            window.UpdateLayout();
            var open = tile.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_Open");
            Assert.True(Math.Abs(open.TranslatePoint(new Point(0, 0), tile)!.Value.X) <= 1,
                $"tile={tile.Bounds} surface={surface.Bounds} surfaceWidth={surface.Width} open={open.Bounds} save={save.Bounds} grid={open.GetVisualParent()?.Bounds}");
            window.MouseMove(Position(tile, window, 0.5));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.True(save.IsVisible);
            Assert.True(save.IsHitTestVisible);
            Assert.Equal(expandsLeft ? 1 : 3, Grid.GetColumn(save));
            Assert.InRange(open.TranslatePoint(new Point(0, 0), tile)!.Value.X, -1, 1);
            Assert.True(Math.Abs(tile.Bounds.Width * (canClose ? 3 : 2) - surface.Bounds.Width) < 1,
                $"hover={tile.IsPointerOver} surfaceWidth={surface.Width} surface={surface.Bounds} tile={tile.Bounds} grid={open.GetVisualParent()?.Bounds}");
            // Use the routed click after checking hover state; animation timing
            // must not make a native-input test depend on wall-clock sleeps.
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, saved);
            Assert.Equal(0, opened);
            Assert.Equal(0, closed);
            Assert.Equal(0, catalog.Writes);
            model.Workspaces[0].CanSaveLayout = false;
            Assert.False(save.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Drag_saves_order_without_opening_or_closing_and_shows_a_drop_marker() => RunAsync(async () =>
    {
        using var model = CreateModel(out var catalog);
        var view = new WorkspaceView { DataContext = model };
        var opened = 0;
        var closed = 0;
        view.OpenWorkspaceRequested += (_, _) => opened++;
        view.CloseWorkspaceRequested += (_, _) => closed++;
        var window = new Window { Width = 1000, Height = 700, Content = view, DataContext = model };
        _ = new WorkspaceRailDragController(window);
        window.Show();
        window.UpdateLayout();
        try
        {
            var tiles = Tiles(view);
            Assert.Equal(3, tiles.Length);
            var start = Position(tiles[0], window, 0.5);
            var end = Position(tiles[2], window, 0.8);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Assert.Contains("sortAfter", tiles[2].Classes, StringComparer.Ordinal);
            Assert.True(tiles[2].GetVisualDescendants().OfType<Border>()
                .Single(border => string.Equals(border.Name, "PART_SortAfter", StringComparison.Ordinal)).IsVisible);
            Assert.Equal(0.55, tiles[0].Opacity);
            window.MouseUp(end, MouseButton.Left);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal(["Beta", "Gamma", "Alpha"], model.Workspaces.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(1, catalog.Writes);
            Assert.Equal(0, opened);
            Assert.Equal(0, closed);
            Assert.Equal(1, tiles[0].Opacity);
            Assert.DoesNotContain("sortAfter", tiles[2].Classes, StringComparer.Ordinal);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Escape_or_drop_outside_cancels_without_writing(bool escape) => RunAsync(() =>
    {
        using var model = CreateModel(out var catalog);
        var view = new WorkspaceView { DataContext = model };
        var window = new Window { Width = 1000, Height = 700, Content = view, DataContext = model };
        _ = new WorkspaceRailDragController(window);
        window.Show();
        window.UpdateLayout();
        try
        {
            var tiles = Tiles(view);
            window.MouseDown(Position(tiles[0], window, 0.5), MouseButton.Left);
            var end = Position(tiles[2], window, 0.8);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            if (escape) { window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); }
            else { end = new Point(500, 600); window.MouseMove(end, RawInputModifiers.LeftMouseButton); }
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(0, catalog.Writes);
            Assert.Equal(1, tiles[0].Opacity);
            Assert.Equal(["Alpha", "Beta", "Gamma"], model.Workspaces.Select(item => item.Name), StringComparer.Ordinal);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Click_still_opens_and_keyboard_can_move_the_same_workspace_repeatedly() => RunAsync(async () =>
    {
        using var model = CreateModel(out var catalog);
        var view = new WorkspaceView { DataContext = model };
        var opened = 0;
        view.OpenWorkspaceRequested += (_, _) => opened++;
        var window = new Window { Width = 1000, Height = 700, Content = view, DataContext = model };
        _ = new WorkspaceRailDragController(window);
        window.Show();
        window.UpdateLayout();
        try
        {
            var start = Position(Tiles(view)[0], window, 0.5);
            window.MouseDown(start, MouseButton.Left);
            window.MouseUp(start, MouseButton.Left);
            Assert.Equal(1, opened);
            Assert.Equal(0, catalog.Writes);
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(["Beta", "Alpha", "Gamma"], model.Workspaces.Select(item => item.Name), StringComparer.Ordinal);
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(["Beta", "Gamma", "Alpha"], model.Workspaces.Select(item => item.Name), StringComparer.Ordinal);
            Assert.Equal(2, catalog.Writes);
            Assert.Equal(1, opened);
        }
        finally { window.Close(); }
    });

    private static WorkspaceRailTile[] Tiles(WorkspaceView view) =>
        [.. view.GetVisualDescendants().OfType<WorkspaceRailTile>()];

    private static Point Position(WorkspaceRailTile tile, Window window, double fraction) =>
        tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height * fraction), window)!.Value;

    private static MainWindowViewModel CreateModel(out OrderCatalog catalog)
    {
        var dependency = DispatchProxy.Create<IDefinitionCatalog, OrderCatalog>();
        catalog = (OrderCatalog)(object)dependency;
        return new(
            DispatchProxy.Create<ISessionHostClient, MainWindowTabReorderTests.EmptyDependency>(), dependency,
            DispatchProxy.Create<IConnectionRuntime, MainWindowTabReorderTests.EmptyDependency>(),
            DispatchProxy.Create<ISecretVault, MainWindowTabReorderTests.EmptyDependency>(),
            DispatchProxy.Create<IFilePanelClient, MainWindowTabReorderTests.EmptyDependency>(),
            DispatchProxy.Create<IFileTransferQueueClient, MainWindowTabReorderTests.EmptyDependency>(),
            new TerminalStartupCommandDispatcher(
                DispatchProxy.Create<IAuditStore, MainWindowTabReorderTests.EmptyDependency>(), TimeProvider.System));
    }

    public class OrderCatalog : DispatchProxy
    {
        private DefinitionCatalogSnapshot _snapshot = DefinitionCatalogSnapshot.Empty with
        {
            Workspaces = [.. new[] { "Gamma", "Alpha", "Beta" }.Select(name => new StoredDefinition<WorkspaceDefinition>(
                new(new WorkspaceId(name), WorkspaceDefinition.CurrentSchemaVersion, name, null, null, []),
                1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))],
        };

        public int Writes { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Snapshot": return _snapshot;
                case "add_Changed": case "remove_Changed": return null;
                case nameof(IDefinitionCatalog.ReorderWorkspacesAsync):
                    var ids = Assert.IsAssignableFrom<IReadOnlyList<WorkspaceId>>(args![0]);
                    Writes++;
                    _snapshot = _snapshot with
                    {
                        Workspaces = [.. ids.Select((id, index) =>
                    {
                        var stored = _snapshot.Workspaces.Single(item => item.Value.Id == id);
                        return stored with { Value = stored.Value with { SortOrder = index }, Revision = stored.Revision + 1 };
                    })]
                    };
                    return ValueTask.FromResult<DefinitionStoreError?>(null);
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private static async Task RunAsync(Func<Task> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = HeadlessUnitTestSession.StartNew(typeof(SqlEditorHeadlessApplication));
        try
        {
            Assert.True(await session.Dispatch(async () => { await action(); return true; }, timeout.Token));
        }
        finally { await session.DisposeAsync(); }
    }
}
