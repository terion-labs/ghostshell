using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class DefaultAgentPolicySettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_default_agent_policy_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.DefaultAgentPolicySettings),
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(
            typeof(DefaultAgentPolicySettingsViewModel),
            property.PropertyType);
        Assert.Null(property.SetMethod);
        Assert.Single(
            typeof(MainWindowViewModel).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType
                == typeof(DefaultAgentPolicySettingsViewModel));
    }

    [Fact]
    public void Policy_edit_persistence_and_subscription_live_in_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("DefaultAgentPolicySettingsViewModel.cs");

        Assert.DoesNotContain("_agentPolicyCoordinator", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_defaultAgentPolicySaveGate", root, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistDefaultAgentPoliciesAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("new SavedScreenAgentPolicyEditorViewModel", root, StringComparison.Ordinal);
        Assert.Contains("_coordinator.SaveAsync", owner, StringComparison.Ordinal);
        Assert.Contains("PersistQueuedPoliciesAsync", owner, StringComparison.Ordinal);
        Assert.Contains("new(policy, providers)", owner, StringComparison.Ordinal);
        Assert.Contains("activeCoordinator.Changed +=", owner, StringComparison.Ordinal);
        Assert.Contains("activeCoordinator.Changed -=", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_owner_has_explicit_quiesce_and_disposal()
    {
        var owner = Read("DefaultAgentPolicySettingsViewModel.cs");

        Assert.Contains("public async Task QuiesceAsync", owner, StringComparison.Ordinal);
        Assert.Contains("public void Seal", owner, StringComparison.Ordinal);
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(DefaultAgentPolicySettingsViewModel)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
