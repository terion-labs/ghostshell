using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class RuntimeWorkspaceGraphCoordinatorOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_runtime_graph_coordinator()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.RuntimeGraph),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(RuntimeWorkspaceGraphCoordinator), property.PropertyType);
        Assert.Null(property.SetMethod);
        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(RuntimeWorkspaceGraphCoordinator));
    }

    [Fact]
    public void Revision_application_receipt_validation_and_watch_lifetime_live_in_coordinator()
    {
        var root = ReadMainWindow();
        var owner = Read("RuntimeWorkspaceGraphCoordinator.cs");

        Assert.DoesNotContain("ApplyHostProjection(", root, StringComparison.Ordinal);
        Assert.DoesNotContain("WatchRuntimeWorkspaceGraphAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_runtimeGraphWatchTasks", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_runtimeGraphWatchCancellation", root, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterWorkspaceGraphAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("UnregisterWorkspaceGraphAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("GetWorkspaceGraphAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionClient.ActivateWorkspaceTabAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionClient.ActivateWorkspacePanelAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionClient.CloseAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionClient.EnsureBrowserSessionAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionClient.EnsureTerminalSessionAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionClient.WatchAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceGraphEventKind", root, StringComparison.Ordinal);
        Assert.Contains("ApplyHostProjection(", owner, StringComparison.Ordinal);
        Assert.Contains("WatchWorkspaceGraphAsync", owner, StringComparison.Ordinal);
        Assert.Contains("RegisterWorkspaceGraphAsync", owner, StringComparison.Ordinal);
        Assert.Contains("UnregisterWorkspaceGraphAsync", owner, StringComparison.Ordinal);
        Assert.Contains("GetWorkspaceGraphAsync", owner, StringComparison.Ordinal);
        Assert.Contains("ActivateWorkspaceTabAsync", owner, StringComparison.Ordinal);
        Assert.Contains("ActivateWorkspacePanelAsync", owner, StringComparison.Ordinal);
        Assert.Contains("RequireSessionClient().CloseAsync", owner, StringComparison.Ordinal);
        Assert.Contains("ObserveWorkspaceAsync", owner, StringComparison.Ordinal);
        Assert.Contains("EnsureBrowserSessionAsync", owner, StringComparison.Ordinal);
        Assert.Contains("EnsureTerminalSessionAsync", owner, StringComparison.Ordinal);
        Assert.Contains("RequireSessionClient().WatchAsync", owner, StringComparison.Ordinal);
        Assert.Contains("IsExpectedReceipt", owner, StringComparison.Ordinal);
        Assert.Contains("IsExpectedReconciledReceipt", owner, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim _gate", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_graph_identity_rules_live_in_projection_boundary()
    {
        var projection = Read("RuntimeWorkspaceGraphProjection.cs");
        Assert.Contains("expectedPanel.Id != actualPanel.Id", projection, StringComparison.Ordinal);
        Assert.Contains("expectedPanel.Kind != actualPanel.Kind", projection, StringComparison.Ordinal);
        Assert.Contains("StringComparison.Ordinal", projection, StringComparison.Ordinal);
        Assert.Contains(
            "PanelKind.Placeholder",
            Read("RuntimeWorkspaceViewModels.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_graph_owner_has_no_feature_runtime_or_persistence_dependencies()
    {
        var source = Read("RuntimeWorkspaceGraphCoordinator.cs");
        Assert.DoesNotContain("IConnectionRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IBrowser", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IFilePanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAiProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecretVault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDefinitionCatalog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeRecoveryWriter", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_graph_owner_declares_an_explicit_lifetime()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(RuntimeWorkspaceGraphCoordinator)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));

    private static string ReadMainWindow()
    {
        var directory = Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels");
        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(directory, "MainWindowViewModel*.cs")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }
}
