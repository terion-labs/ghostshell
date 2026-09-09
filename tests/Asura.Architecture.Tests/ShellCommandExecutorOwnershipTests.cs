using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class ShellCommandExecutorOwnershipTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void Command_routing_and_execution_live_outside_main_window_partials()
    {
        var mainWindow = ApplicationViews.FindPartialClassSources("MainWindow");
        var executor = Read("ShellCommandExecutor.cs");

        Assert.DoesNotContain(
            "ApplicationCommandRouter.Route(",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "case ApplicationCommandActionKind.",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplicationCommandRouter.Route(",
            executor,
            StringComparison.Ordinal);
        Assert.Contains(
            "case ApplicationCommandActionKind.ClosePanel:",
            executor,
            StringComparison.Ordinal);
        Assert.Contains(
            "case ApplicationCommandActionKind.SelectWorkspace:",
            executor,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_native_menu_handler_invokes_the_shared_executor()
    {
        var nativeMenus = Read("Views", "MainWindow.NativeMenus.cs");
        var invocations = nativeMenus.Split(
            "ShellCommands.ExecuteNativeAsync(",
            StringSplitOptions.None);

        Assert.Equal(14, invocations.Length);
        Assert.DoesNotContain(
            "await RequestNewTerminalAsync();",
            nativeMenus,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await RequestClosePanelAsync();",
            nativeMenus,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "await SelectRelativeTabAsync(",
            nativeMenus,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_command_executor_borrows_one_explicit_lifetime()
    {
        var executor = Read("ShellCommandExecutor.cs");

        Assert.Contains("CancellationToken lifetime", executor, StringComparison.Ordinal);
        Assert.Contains(
            "when (lifetime.IsCancellationRequested)",
            executor,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("bool ", executor, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        Path.Combine(path)));
}
