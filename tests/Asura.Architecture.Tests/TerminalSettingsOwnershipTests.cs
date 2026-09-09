using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class TerminalSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_terminal_settings_owner_without_editor_fields()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.TerminalSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(TerminalSettingsViewModel), property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.Null(property.SetMethod);

        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(TerminalSettingsViewModel));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(TerminalProfileEditorViewModel));
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(QuickTerminalSettingsEditorViewModel));
    }

    [Fact]
    public void Terminal_settings_owner_contains_projection_persistence_and_lifetime_policy()
    {
        Assert.Contains(typeof(IDisposable), typeof(TerminalSettingsViewModel).GetInterfaces());
        Assert.NotNull(typeof(TerminalSettingsViewModel).GetMethod(
            nameof(TerminalSettingsViewModel.ApplyCatalog)));
        Assert.NotNull(typeof(TerminalSettingsViewModel).GetMethod(
            nameof(TerminalSettingsViewModel.SaveTerminalProfileAsync)));
        Assert.NotNull(typeof(TerminalSettingsViewModel).GetMethod(
            nameof(TerminalSettingsViewModel.SaveQuickTerminalSettingsAsync)));
        Assert.NotNull(typeof(TerminalSettingsViewModel).GetMethod(
            nameof(TerminalSettingsViewModel.ApplyQuickTerminalRegistration)));

        var root = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "MainWindowViewModel.cs"));
        Assert.DoesNotContain("_terminalSettingsEditor", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_quickTerminalSettingsEditor", root, StringComparison.Ordinal);
        Assert.DoesNotContain("new TerminalProfileEditorViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new QuickTerminalSettingsEditorViewModel",
            root,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_settings_owner_does_not_own_runtime_or_operating_system_effects()
    {
        var source = File.ReadAllText(Path.Combine(
            ApplicationViewCatalog.Load().RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "TerminalSettingsViewModel.cs"));

        Assert.DoesNotContain("RuntimeWorkspace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalMultiplex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IGlobalHotkeyService", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Register(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait(false)", source, StringComparison.Ordinal);
    }
}
