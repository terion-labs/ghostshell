using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class TerminalContinuitySettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_terminal_continuity_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.TerminalContinuity),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(
            typeof(TerminalContinuitySettingsViewModel),
            property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType
                == typeof(TerminalContinuitySettingsViewModel));
    }

    [Fact]
    public void Preference_managed_session_mutation_and_subscription_live_in_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("TerminalContinuitySettingsViewModel.cs");

        Assert.DoesNotContain(
            "_terminalMultiplexerCoordinator.ReadPreferenceAsync",
            root,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_terminalMultiplexerCoordinator.WritePreferenceAsync",
            root,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_terminalMultiplexerCoordinator.ForgetAsync",
            root,
            StringComparison.Ordinal);
        Assert.DoesNotContain("LeasesChanged +=", root, StringComparison.Ordinal);
        Assert.DoesNotContain("LeasesChanged -=", root, StringComparison.Ordinal);
        Assert.Contains(".ReadPreferenceAsync", owner, StringComparison.Ordinal);
        Assert.Contains(".WritePreferenceAsync", owner, StringComparison.Ordinal);
        Assert.Contains(".ForgetAsync", owner, StringComparison.Ordinal);
        Assert.Contains("LeasesChanged +=", owner, StringComparison.Ordinal);
        Assert.Contains("LeasesChanged -=", owner, StringComparison.Ordinal);
        Assert.Contains("private void Project", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_continuity_owner_has_an_explicit_lifetime()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(TerminalContinuitySettingsViewModel)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
