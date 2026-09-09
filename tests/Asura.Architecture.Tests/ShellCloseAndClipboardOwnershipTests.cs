using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class ShellCloseAndClipboardOwnershipTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Window_close_state_and_preflight_live_in_the_close_coordinator()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var coordinator = Read("Views", "ShellCloseCoordinator.cs");

        Assert.DoesNotContain("_closeApproved", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_closeInProgress", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindowCloseFlow.RunAsync(", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirmations.CloseScope(", mainWindow, StringComparison.Ordinal);
        Assert.Contains("_windowCloseApproved", coordinator, StringComparison.Ordinal);
        Assert.Contains("_windowCloseInProgress", coordinator, StringComparison.Ordinal);
        Assert.Contains("MainWindowCloseFlow.RunAsync(", coordinator, StringComparison.Ordinal);
        Assert.Contains("ConfirmDiscardDatabaseChangesAsync(", coordinator, StringComparison.Ordinal);
        Assert.Contains("CloseWindowCoreAsync()", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_policy_is_framework_free_and_borrows_the_shell_lifetime()
    {
        var coordinator = Read("Views", "ShellCloseCoordinator.cs");
        var presentation = Read("Views", "ShellClosePresentation.cs");

        Assert.DoesNotContain("Avalonia", coordinator, StringComparison.Ordinal);
        Assert.Contains("CancellationToken lifetime", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", coordinator, StringComparison.Ordinal);
        Assert.Contains("Window owner", presentation, StringComparison.Ordinal);
        Assert.Contains("ShowDialog<bool>(owner)", presentation, StringComparison.Ordinal);
    }

    [Fact]
    public void Clipboard_failure_and_lifetime_policy_live_outside_main_window()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var clipboard = Read("ShellClipboard.cs");

        Assert.DoesNotContain("Clipboard is not", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("await clipboard.SetTextAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("lifetime.IsCancellationRequested", clipboard, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", clipboard, StringComparison.Ordinal);
        Assert.Contains("ObjectDisposedException", clipboard, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", clipboard, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        Path.Combine(path)));
}
