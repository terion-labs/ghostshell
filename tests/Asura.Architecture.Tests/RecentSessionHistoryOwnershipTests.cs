using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class RecentSessionHistoryOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_history_owner_without_retaining_its_operation_state()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.History),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(RecentSessionHistoryViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(RecentSessionHistoryViewModel));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(RecentSessionHistory));
        Assert.DoesNotContain(fields, field => field.Name == "_historyOperations");
        Assert.DoesNotContain(fields, field => field.Name == "_historyLifetime");
        Assert.DoesNotContain(fields, field => field.Name == "_historyDrainError");
        Assert.DoesNotContain(fields, field => field.Name == "_storedHistoryRetention");

        Assert.Null(typeof(MainWindowViewModel).GetMethod(
            "QueueHistoryOperation",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(MainWindowViewModel).GetMethod(
            "RefreshRecentSessionsCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Extracted_history_owner_is_directly_constructible_and_explicitly_disposable()
    {
        var constructor = Assert.Single(
            typeof(RecentSessionHistoryViewModel).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public));
        Assert.Equal(typeof(RecentSessionHistory), constructor.GetParameters()[0].ParameterType);
        Assert.Contains(
            typeof(IDisposable),
            typeof(RecentSessionHistoryViewModel).GetInterfaces());

        var fields = typeof(RecentSessionHistoryViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Contains(fields, field => field.Name == "_operations");
        Assert.Contains(fields, field => field.Name == "_lifetime");
        Assert.Contains(fields, field => field.Name == "_drainError");
        Assert.Contains(fields, field => field.Name == "_storedRetention");
    }

    [Fact]
    public void History_presentation_does_not_escape_the_ui_synchronization_context()
    {
        var source = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "RecentSessionHistoryViewModel.cs"));

        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
    }
}
