using System.Collections.ObjectModel;
using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class LauncherOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_launcher_owner_without_retaining_mutable_launcher_state()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.Launcher),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(LauncherViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(LauncherViewModel));
        Assert.DoesNotContain(fields, field => field.Name == "_launcherSearchQuery");
        Assert.DoesNotContain(fields, field => field.Name == "_selectedLauncherSearchResult");
        Assert.DoesNotContain(fields, field =>
            field.FieldType.IsGenericType
            && field.FieldType.GetGenericTypeDefinition() == typeof(ObservableCollection<>)
            && field.FieldType.GenericTypeArguments[0].Name.StartsWith(
                "Launcher",
                StringComparison.Ordinal));

        Assert.Null(typeof(MainWindowViewModel).GetMethod(
            "RefreshLauncherSearchResults",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(MainWindowViewModel).GetMethod(
            "PresentsSameResults",
            BindingFlags.Static | BindingFlags.NonPublic));
    }

    [Fact]
    public void Launcher_owner_contains_search_selection_and_candidate_lifetime_policy()
    {
        var fields = typeof(LauncherViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Contains(fields, field => field.Name == "_searchQuery");
        Assert.Contains(fields, field => field.Name == "_selectedSearchResult");
        Assert.Contains(fields, field => field.Name == "_candidateSource");
        Assert.Contains(typeof(IDisposable), typeof(LauncherViewModel).GetInterfaces());

        Assert.NotNull(typeof(LauncherViewModel).GetMethod(
            nameof(LauncherViewModel.RefreshSearchResults),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(LauncherViewModel).GetMethod(
            nameof(LauncherViewModel.SelectFirstAvailableSearchResult),
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(typeof(LauncherViewModel).GetMethod(
            nameof(LauncherViewModel.MoveSearchSelection),
            BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Launcher_presentation_has_no_background_or_context_escaping_search_path()
    {
        var source = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "LauncherViewModel.cs"));

        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("async ", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_window_keeps_only_candidate_projection_not_search_policy()
    {
        var source = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "MainWindowViewModel.cs"));

        Assert.Contains("BuildLauncherSearchCandidates", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LauncherSearchProjection.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindNextAvailableIndex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveAvailableSelection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmSelection", source, StringComparison.Ordinal);
    }
}
