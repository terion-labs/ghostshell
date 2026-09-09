using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class AgentWorkspaceScopeOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_agent_workspace_scope_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.AgentWorkspaceScope),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(AgentWorkspaceScopeViewModel), property.PropertyType);
        Assert.Null(property.SetMethod);
        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(AgentWorkspaceScopeViewModel));
    }

    [Fact]
    public void Live_terminal_selection_and_target_construction_live_in_the_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("AgentWorkspaceScopeViewModel.cs");
        var runtime = Read("RuntimeWorkspaceViewModels.cs");

        Assert.DoesNotContain("_agentSelectionTracked", root, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCreateSelectedPanelsTarget", root, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgentTerminalSelectionItemViewModel", root, StringComparison.Ordinal);
        Assert.DoesNotContain("class AgentTerminalSelectionItemViewModel", runtime, StringComparison.Ordinal);
        Assert.Contains("TryCreateSelectedPanelsTarget", owner, StringComparison.Ordinal);
        Assert.Contains("Tabs.CollectionChanged += OnTabsChanged", owner, StringComparison.Ordinal);
        Assert.Contains("terminal.PropertyChanged += OnTerminalPropertyChanged", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_owner_has_no_prompt_governance_secret_audit_or_recovery_effects()
    {
        var source = Read("AgentWorkspaceScopeViewModel.cs");
        Assert.DoesNotContain("IGovernedAgentRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecretVault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IAudit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Approval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentChatViewModel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeRecovery", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Governance_and_dispatch_remain_shell_composition_concerns()
    {
        var root = Read("MainWindowViewModel.cs");
        Assert.Contains("TryResolveAgentPolicy", root, StringComparison.Ordinal);
        Assert.Contains("agentChat.SendAsync", root, StringComparison.Ordinal);
        Assert.Contains("DefaultAgentPolicy", root, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_owner_declares_an_explicit_lifetime()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(AgentWorkspaceScopeViewModel)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
