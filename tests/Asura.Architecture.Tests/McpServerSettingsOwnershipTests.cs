using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class McpServerSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_mcp_server_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.McpServerSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(McpServerSettingsViewModel), property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(McpServerSettingsViewModel));
    }

    [Fact]
    public void Editor_construction_trust_gate_and_persistence_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("McpServerSettingsViewModel.cs");

        Assert.DoesNotContain("new McpServerProfileEditorViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.SaveMcpServerProfileAsync", root, StringComparison.Ordinal);
        Assert.Contains("new McpServerProfileEditorViewModel", owner, StringComparison.Ordinal);
        Assert.Contains("IsAuthorizedForSave", owner, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveMcpServerProfileAsync", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_owner_has_no_diagnostics_secret_mutation_or_runtime_effects()
    {
        var owner = Read("McpServerSettingsViewModel.cs");
        Assert.DoesNotContain("IMcpServerDiagnostics", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecretVault", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("IMcpCredentialSessionInvalidator", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspace", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_owner_declares_an_explicit_lifetime()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(McpServerSettingsViewModel)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
