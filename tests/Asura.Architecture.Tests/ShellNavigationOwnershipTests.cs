using System.Reflection;
using Asura.App.ViewModels;

namespace Asura.Architecture.Tests;

public sealed class ShellNavigationOwnershipTests
{
    [Fact]
    public void Main_window_has_one_extracted_navigation_state_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.Navigation),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(ShellNavigationViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        var navigation = Assert.Single(
            fields,
            field => field.FieldType == typeof(ShellNavigationViewModel));

        Assert.Equal("_navigation", navigation.Name);
        Assert.DoesNotContain(fields, field => field.Name == "_route");
        Assert.DoesNotContain(fields, field => field.Name == "_settingsPage");
        Assert.DoesNotContain(fields, field => field.Name == "_overlay");
        Assert.DoesNotContain(fields, field => field.Name == "_overlayRevision");
    }
}
