using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class DatabaseConnectionSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_database_connection_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.DatabaseConnectionSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(DatabaseConnectionSettingsCoordinator), property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(DatabaseConnectionSettingsCoordinator));
    }

    [Fact]
    public void Profile_persistence_and_credential_lifecycle_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("DatabaseConnectionSettingsCoordinator.cs");

        Assert.DoesNotContain("_catalog.SaveDatabaseConnectionAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildInlineTunnelAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteUnusedDatabasePasswordAsync", root, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveDatabaseConnectionAsync", owner, StringComparison.Ordinal);
        Assert.Contains("BuildInlineTunnelAsync", owner, StringComparison.Ordinal);
        Assert.Contains("DeleteUnusedDatabasePasswordAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_secretVault.CreateAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_secretVault.ResolveAsync", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_owner_has_no_runtime_panel_graph_or_session_dependencies()
    {
        var owner = Read("DatabaseConnectionSettingsCoordinator.cs");
        Assert.DoesNotContain("RuntimePanelViewModel", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspace", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("ISessionHostClient", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceGraph", owner, StringComparison.Ordinal);
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
