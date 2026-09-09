using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class RuntimeTabDragControllerOwnershipTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Drag_state_drop_policy_and_reorder_execution_live_outside_main_window()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var controller = Read("Views", "RuntimeTabDragController.cs");

        Assert.DoesNotContain("RuntimeTabDragCandidate", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTabActiveDrag", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTabDropTarget", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("DataFormat.CreateInProcessFormat", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("WouldMoveRuntimeTab", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDragCandidate? _candidate", controller, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabActiveDrag? _activeDrag", controller, StringComparison.Ordinal);
        Assert.Contains("DataFormat.CreateInProcessFormat", controller, StringComparison.Ordinal);
        Assert.Contains("private static bool WouldMove(", controller, StringComparison.Ordinal);
        Assert.Contains("await viewModel.MoveTabAsync(", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_drag_handlers_only_translate_and_forward_events()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");

        Assert.Contains("RuntimeTabDrag.PointerPressed(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.PointerMoved(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.PointerReleasedAsync(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.PointerCaptureLost(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.DragEnter(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.DragOver(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.DragLeave(sender, e);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RuntimeTabDrag.DropAsync(sender, e);", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Drag_cleanup_releases_capture_and_observes_the_shell_lifetime()
    {
        var controller = Read("Views", "RuntimeTabDragController.cs");

        Assert.Contains("active.Pointer.Capture(null);", controller, StringComparison.Ordinal);
        Assert.Contains("pointer.Capture(null);", controller, StringComparison.Ordinal);
        Assert.Contains("presentation.HideGhost();", controller, StringComparison.Ordinal);
        Assert.Contains("when (lifetime.IsCancellationRequested)", controller, StringComparison.Ordinal);
        Assert.Contains("if (lifetime.IsCancellationRequested)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", controller, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        Path.Combine(path)));
}
