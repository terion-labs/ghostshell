using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class ApplicationKeyControllerOwnershipTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Key_sequence_state_and_hint_lifetime_live_outside_main_window()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var owner = Read("ApplicationKeyController.cs");

        Assert.DoesNotContain("_applicationHintLifetime", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("SynchronizeApplicationKeymap", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpireApplicationKeySequenceAsync", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplayApplicationKeyStrokesAsync(", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ApplicationKeySequenceResolver _resolver", owner, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource? _hintLifetime", owner, StringComparison.Ordinal);
        Assert.Contains("Task ExpireAsync(", owner, StringComparison.Ordinal);
        Assert.Contains("Task ReplayAsync(", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_controller_is_framework_free_and_owns_an_explicit_lifetime()
    {
        var source = Read("ApplicationKeyController.cs");

        Assert.Contains(
            "class ApplicationKeyController : IDisposable",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);
        Assert.Contains(
            "CancellationTokenSource.CreateLinkedTokenSource(lifetime)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_lifetime.Cancel();", source, StringComparison.Ordinal);
        Assert.Contains("_lifetime.Dispose();", source, StringComparison.Ordinal);
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViews.RepositoryRoot,
        "src",
        "Asura.App",
        fileName));
}
