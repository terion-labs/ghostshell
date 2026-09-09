using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class AppearanceSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_appearance_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.AppearanceSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(AppearanceSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(AppearanceSettingsViewModel));
    }

    [Fact]
    public void Appearance_owner_depends_only_on_the_definition_catalog()
    {
        var constructor = Assert.Single(
            typeof(AppearanceSettingsViewModel).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public));
        var parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(IDefinitionCatalog), parameter.ParameterType);

        var fields = typeof(AppearanceSettingsViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(IDefinitionCatalog));
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(MainWindowViewModel));
    }

    [Fact]
    public void Appearance_owner_contains_projection_and_revision_save_policy()
    {
        var root = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var owner = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "AppearanceSettingsViewModel.cs"));

        Assert.DoesNotContain("_catalog.Snapshot.Themes", root, StringComparison.Ordinal);
        Assert.DoesNotContain("new ThemePreference(", root, StringComparison.Ordinal);
        Assert.Contains("_catalog.Snapshot.Themes", owner, StringComparison.Ordinal);
        Assert.Contains("new ThemePreference(", owner, StringComparison.Ordinal);
        Assert.Contains("stored?.Revision", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_side_effects_stay_in_main_window()
    {
        var repositoryRoot = ApplicationViewCatalog.Load().RepositoryRoot;
        var root = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var owner = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "AppearanceSettingsViewModel.cs"));

        Assert.Contains("SetActiveWorkspaceAccent", root, StringComparison.Ordinal);
        Assert.Contains("nameof(HasWorkspaceAttention)", root, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspace", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("HasWorkspaceAttention", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", owner, StringComparison.Ordinal);
    }
}
