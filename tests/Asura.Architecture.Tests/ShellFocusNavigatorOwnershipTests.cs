using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class ShellFocusNavigatorOwnershipTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Focus_policy_and_cancelled_close_restoration_live_outside_main_window()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var navigator = Read("Views", "ShellFocusNavigator.cs");

        Assert.DoesNotContain(
            "_restoreRouteFocusWhenActivated",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private const int PanelFocusAttempts",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetVisualDescendants()\n                    .OfType<BrowserPresentationHost>()",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int PanelFocusAttempts = 6;",
            navigator,
            StringComparison.Ordinal);
        Assert.Contains(
            "public void RestoreAfterCancelledClose()",
            navigator,
            StringComparison.Ordinal);
        Assert.Contains(
            "public void NotifyWindowActivated()",
            navigator,
            StringComparison.Ordinal);
        Assert.Contains(
            "OfType<BrowserPresentationHost>()",
            navigator,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Posted_focus_work_observes_the_borrowed_window_lifetime()
    {
        var navigator = Read("Views", "ShellFocusNavigator.cs");

        Assert.Contains(
            "CancellationToken lifetime",
            navigator,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!lifetime.IsCancellationRequested)",
            navigator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CancellationTokenSource",
            navigator,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "interface IShellFocus",
            navigator,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        Path.Combine(path)));
}
