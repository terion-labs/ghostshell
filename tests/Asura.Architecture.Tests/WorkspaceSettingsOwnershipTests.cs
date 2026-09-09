using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_workspace_settings_owner_without_editor_state()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.WorkspaceSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(WorkspaceSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(WorkspaceSettingsViewModel));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(WorkspaceEditorViewModel));
        Assert.DoesNotContain(fields, field => field.Name == "_workspaceEditor");
    }

    [Fact]
    public void Workspace_settings_owner_contains_editor_projection_save_and_lifetime_policy()
    {
        Assert.Contains(typeof(IDisposable), typeof(WorkspaceSettingsViewModel).GetInterfaces());
        Assert.NotNull(typeof(WorkspaceSettingsViewModel).GetMethod(
            nameof(WorkspaceSettingsViewModel.TryBeginEdit)));
        Assert.NotNull(typeof(WorkspaceSettingsViewModel).GetMethod(
            nameof(WorkspaceSettingsViewModel.TryBeginCreate)));
        Assert.Equal(2, typeof(WorkspaceSettingsViewModel).GetMethods()
            .Count(method => method.Name == nameof(WorkspaceSettingsViewModel.SaveAsync)));
        Assert.NotNull(typeof(WorkspaceSettingsViewModel).GetMethod(
            nameof(WorkspaceSettingsViewModel.CreateAsync)));
        Assert.NotNull(typeof(WorkspaceSettingsViewModel).GetMethod(
            nameof(WorkspaceSettingsViewModel.SetAgentPanelPinnedAsync)));

        var root = ReadSource("MainWindowViewModel.cs");
        var owner = ReadSource("WorkspaceSettingsViewModel.cs");
        Assert.DoesNotContain("new WorkspaceEditorViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceEditor = new", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_workspaceEditor", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.SaveWorkspaceAsync", root, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveWorkspaceAsync", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_and_overlay_side_effects_remain_at_the_root_boundary()
    {
        var owner = ReadSource("WorkspaceSettingsViewModel.cs");
        var root = ReadSource("MainWindowViewModel.cs");

        Assert.DoesNotContain("RuntimeWorkspace", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellOverlay", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", owner, StringComparison.Ordinal);
        Assert.Contains(
            "ApplyTerminalMultiplexingOverrideToOpenWorkspaces",
            root,
            StringComparison.Ordinal);
        Assert.Contains("DismissWorkspaceEditor();", root, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName) =>
        File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            fileName));
}
