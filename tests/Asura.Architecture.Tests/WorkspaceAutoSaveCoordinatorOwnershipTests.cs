using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceAutoSaveCoordinatorOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_workspace_auto_save_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.WorkspaceAutoSave),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(WorkspaceAutoSaveCoordinator), property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(WorkspaceAutoSaveCoordinator));
    }

    [Fact]
    public void Capture_persistence_cleanup_and_debounce_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("WorkspaceAutoSaveCoordinator.cs");

        Assert.DoesNotContain("CaptureWorkspaceAutoSave", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorkspaceWithLayoutsAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("CleanUpOrphanedAutoSaveLayoutsAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceAutoSaveDebounceMilliseconds", root, StringComparison.Ordinal);
        Assert.Contains("CaptureWorkspaceAutoSave", owner, StringComparison.Ordinal);
        Assert.Contains("SaveWorkspaceWithLayoutsAsync", owner, StringComparison.Ordinal);
        Assert.Contains("CleanUpOrphanedAutoSaveLayoutsAsync", owner, StringComparison.Ordinal);
        Assert.Contains("WorkspaceAutoSaveDebounceMilliseconds", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_save_owner_has_no_runtime_adapter_or_graph_protocol_dependencies()
    {
        var owner = Read("WorkspaceAutoSaveCoordinator.cs");
        Assert.DoesNotContain("ISessionHostClient", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("IConnectionRuntime", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("IBrowser", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("IFilePanel", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgent", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspaceGraph", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_save_owner_declares_an_explicit_lifetime()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(WorkspaceAutoSaveCoordinator)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
