using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;

namespace Asura.Architecture.Tests;

public sealed class DefinitionEditSessionOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_definition_edit_owner_without_draft_fields()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.DefinitionEdit),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(DefinitionEditSessionViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(DefinitionEditSessionViewModel));
        Assert.DoesNotContain(fields, field => field.Name == "_editingDefinition");
        Assert.DoesNotContain(fields, field => field.Name == "_editingRevision");
        Assert.DoesNotContain(fields, field => field.Name == "_editorName");
        Assert.DoesNotContain(fields, field => field.Name == "_editorDescription");
    }

    [Fact]
    public void Definition_edit_owner_contains_revision_and_save_policy()
    {
        var fields = typeof(DefinitionEditSessionViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Contains(fields, field => field.Name == "_definition");
        Assert.Contains(fields, field => field.Name == "_revision");
        Assert.Contains(fields, field => field.Name == "_name");
        Assert.Contains(fields, field => field.Name == "_description");

        Assert.NotNull(typeof(DefinitionEditSessionViewModel).GetMethod(
            nameof(DefinitionEditSessionViewModel.Begin),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(DefinitionEditSessionViewModel).GetMethod(
            nameof(DefinitionEditSessionViewModel.SaveAsync),
            BindingFlags.Instance | BindingFlags.Public));
    }
}
