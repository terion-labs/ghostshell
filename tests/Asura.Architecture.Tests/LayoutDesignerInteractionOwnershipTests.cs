using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class LayoutDesignerInteractionOwnershipTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Pointer_and_keyboard_layout_mutations_share_one_controller()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var controller = Read("Views", "LayoutDesignerInteractionController.cs");

        Assert.DoesNotContain("LayoutDesignerEditor?.SelectSlot", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutDesignerEditor?.SplitSlot", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutDesignerEditor?.RemoveSlot", mainWindow, StringComparison.Ordinal);
        Assert.Contains("LayoutDesignerInteractions.SelectSlot(sender);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("LayoutDesignerInteractions.SplitSlotRight(sender);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("LayoutDesignerInteractions.SplitSlotDown(sender);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("LayoutDesignerInteractions.RemoveSlot(sender);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RemoveSelectedSlot()", controller, StringComparison.Ordinal);
        Assert.Contains("LayoutDesignerSplitDirection direction", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_controller_is_concrete_and_has_no_lifetime_or_mode_flags()
    {
        var controller = Read("Views", "LayoutDesignerInteractionController.cs");

        Assert.DoesNotContain("interface ILayoutDesigner", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("bool ", controller, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        Path.Combine(path)));
}
