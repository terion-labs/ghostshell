using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class AiProviderSettingsOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_ai_provider_settings_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.AiProviderSettings),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(AiProviderSettingsViewModel), property.PropertyType);
        Assert.Null(property.SetMethod);
        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(AiProviderSettingsViewModel));
    }

    [Fact]
    public void Projection_editor_persistence_and_subscription_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("AiProviderSettingsViewModel.cs");
        Assert.DoesNotContain("new AiProviderProfileEditorViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain("_catalog.SaveAiProviderProfileAsync", root, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAiProviderProfilesChanged", root, StringComparison.Ordinal);
        Assert.Contains("new AiProviderProfileEditorViewModel", owner, StringComparison.Ordinal);
        Assert.Contains("_catalog.SaveAiProviderProfileAsync", owner, StringComparison.Ordinal);
        Assert.Contains("ProfilesChanged += OnProfilesChanged", owner, StringComparison.Ordinal);
        Assert.Contains("ProfilesChanged -= OnProfilesChanged", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_settings_owner_has_no_governance_secret_or_runtime_workspace_effects()
    {
        var source = Read("AiProviderSettingsViewModel.cs");
        Assert.DoesNotContain("ISecretVault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IGovernedAgentRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgentApproval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAudit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPolicy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_policy_and_secret_mutation_remain_root_concerns()
    {
        var root = Read("MainWindowViewModel.cs");
        Assert.Contains("CreateAiProviderSecretAsync", root, StringComparison.Ordinal);
        Assert.Contains("RefreshDefaultAgentPolicyOptions", root, StringComparison.Ordinal);
        Assert.Contains("DefaultAgentPolicy", root, StringComparison.Ordinal);
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
