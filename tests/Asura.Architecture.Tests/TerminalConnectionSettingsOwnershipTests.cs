using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class TerminalConnectionSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_terminal_connection_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.TerminalConnectionSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(TerminalConnectionSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(TerminalConnectionSettingsViewModel));
    }

    [Fact]
    public void Connection_definition_authoring_and_persistence_live_in_the_owner()
    {
        Assert.Contains(
            typeof(IDisposable),
            typeof(TerminalConnectionSettingsViewModel).GetInterfaces());
        Assert.NotNull(typeof(TerminalConnectionSettingsViewModel).GetMethod(
            nameof(TerminalConnectionSettingsViewModel.CreateEditor)));
        Assert.NotNull(typeof(TerminalConnectionSettingsViewModel).GetMethod(
            nameof(TerminalConnectionSettingsViewModel.SaveAsync)));

        var root = ReadViewModel("MainWindowViewModel.cs");
        var owner = ReadViewModel("TerminalConnectionSettingsViewModel.cs");
        Assert.DoesNotContain("new ConnectionEditorViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.SaveConnectionAsync", root, StringComparison.Ordinal);
        Assert.Contains("new ConnectionEditorViewModel", owner, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveConnectionAsync", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_settings_owner_does_not_own_runtime_workspace_or_governance()
    {
        var source = ReadViewModel("TerminalConnectionSettingsViewModel.cs");

        Assert.DoesNotContain("RuntimeWorkspace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeTab", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimePanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecretVault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAuditStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_opening_and_unified_editor_composition_remain_root_concerns()
    {
        var root = ReadViewModel("MainWindowViewModel.cs");
        var owner = ReadViewModel("TerminalConnectionSettingsViewModel.cs");

        Assert.Contains("CreateUnifiedConnectionEditor", root, StringComparison.Ordinal);
        Assert.Contains("OpenConnectionAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateUnifiedConnectionEditor", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenConnectionAsync", owner, StringComparison.Ordinal);
    }

    private static string ReadViewModel(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
