using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class SecretSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_secret_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.SecretSettings),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(typeof(SecretSettingsViewModel), property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SecretSettingsViewModel));
    }

    [Fact]
    public void Vault_mutation_and_projection_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("SecretSettingsViewModel.cs");

        Assert.DoesNotContain("_secretVault.CreateAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_secretVault.ReplaceAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_secretVault.RelabelAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_secretVault.DeleteAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_secretVault.ListMetadataAsync", root, StringComparison.Ordinal);
        Assert.Contains("_vault.CreateAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_vault.ReplaceAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_vault.RelabelAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_vault.DeleteAsync", owner, StringComparison.Ordinal);
        Assert.Contains("_vault.ListMetadataAsync", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_dependency_rules_are_centralized_and_owner_is_disposable()
    {
        var root = Read("MainWindowViewModel.cs");
        var references = Read("SecretDefinitionReferences.cs");

        Assert.DoesNotContain("private static bool UsesSecret", root, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private static IEnumerable<McpServerCredentialBindingDescriptor>",
            root,
            StringComparison.Ordinal);
        Assert.Contains("internal static class SecretDefinitionReferences", references, StringComparison.Ordinal);
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(SecretSettingsViewModel)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
