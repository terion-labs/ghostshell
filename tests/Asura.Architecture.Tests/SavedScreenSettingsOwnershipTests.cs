using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class SavedScreenSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_saved_screen_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.SavedScreenSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(SavedScreenSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(SavedScreenSettingsViewModel));
        Assert.DoesNotContain(
            fields,
            field => field.FieldType == typeof(SavedScreenDeleteUndoViewModel));
    }

    [Fact]
    public void Saved_screen_owner_uses_catalog_and_narrow_ai_projection_only()
    {
        var constructor = Assert.Single(
            typeof(SavedScreenSettingsViewModel).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public));
        var parameters = constructor.GetParameters();
        Assert.Collection(
            parameters,
            parameter => Assert.Equal(typeof(IDefinitionCatalog), parameter.ParameterType),
            parameter => Assert.Equal(
                typeof(Func<IReadOnlyList<AiProviderProfileDescriptor>>),
                parameter.ParameterType));

        var source = OwnerSource();
        Assert.DoesNotContain("IAiProviderProfileRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_aiProviderRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellOverlay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Saved_screen_authoring_and_delete_policy_no_longer_live_in_main_window()
    {
        var root = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        var owner = OwnerSource();

        Assert.DoesNotContain("new SavedScreenEditorViewModel(", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SavedScreenEditorViewModel.CreateNew(", root, StringComparison.Ordinal);
        Assert.DoesNotContain("private LayoutDefinition[] SelectableLayouts", root, StringComparison.Ordinal);
        Assert.DoesNotContain("SavedScreenDeleteUndo.Publish", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.SaveScreenAsync(", root, StringComparison.Ordinal);
        Assert.Contains("new SavedScreenEditorViewModel(", owner, StringComparison.Ordinal);
        Assert.Contains("SavedScreenEditorViewModel.CreateNew(", owner, StringComparison.Ordinal);
        Assert.Contains("DeleteUndo.Publish", owner, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveScreenAsync(", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Saved_screen_owner_has_explicit_lifetime_and_typed_operations()
    {
        Assert.Contains(typeof(IDisposable), typeof(SavedScreenSettingsViewModel).GetInterfaces());
        Assert.NotNull(typeof(SavedScreenSettingsViewModel).GetMethod(
            nameof(SavedScreenSettingsViewModel.CreateEditor)));
        Assert.NotNull(typeof(SavedScreenSettingsViewModel).GetMethod(
            nameof(SavedScreenSettingsViewModel.CreateNewEditor)));
        Assert.NotNull(typeof(SavedScreenSettingsViewModel).GetMethod(
            nameof(SavedScreenSettingsViewModel.SaveAsync)));
        Assert.NotNull(typeof(SavedScreenSettingsViewModel).GetMethod(
            nameof(SavedScreenSettingsViewModel.DeleteAsync)));
        Assert.NotNull(typeof(SavedScreenSettingsViewModel).GetMethod(
            nameof(SavedScreenSettingsViewModel.UndoDeleteAsync)));
    }

    private static string OwnerSource() => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        "SavedScreenSettingsViewModel.cs"));
}
