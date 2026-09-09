using System.Reflection;
using Asura.App;
using Asura.App.ViewModels;
using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class RuntimeWorkspaceRecoveryCoordinatorOwnershipTests
{
    [Fact]
    public void Main_window_exposes_one_runtime_recovery_owner()
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.RuntimeRecovery),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.Equal(typeof(RuntimeWorkspaceRecoveryCoordinator), property.PropertyType);
        Assert.Null(property.SetMethod);
        var fields = typeof(MainWindowViewModel).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(
            fields,
            field => field.FieldType == typeof(RuntimeWorkspaceRecoveryCoordinator));
    }

    [Fact]
    public void Subscriptions_serialization_and_writer_failures_live_in_recovery_owner()
    {
        var root = Read("MainWindowViewModel.cs");
        var owner = Read("RuntimeWorkspaceRecoveryCoordinator.cs");

        Assert.DoesNotContain("_runtimeRecoveryWriter", root, StringComparison.Ordinal);
        Assert.DoesNotContain("OnRecoveryRelevantPanelPropertyChanged", root, StringComparison.Ordinal);
        Assert.DoesNotContain("OnRuntimeRecoveryWriteFailed", root, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeWorkspaceRecoveryCodec.Serialize", root, StringComparison.Ordinal);
        Assert.Contains("RuntimeWorkspaceRecoveryCodec.Serialize", owner, StringComparison.Ordinal);
        Assert.Contains("panel.PropertyChanged += OnPanelPropertyChanged", owner, StringComparison.Ordinal);
        Assert.Contains("_writer?.WriteFailed += OnWriteFailed", owner, StringComparison.Ordinal);
        Assert.Contains("_writer?.WriteFailed -= OnWriteFailed", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_owner_has_no_session_host_governance_or_definition_mutation_dependencies()
    {
        var source = Read("RuntimeWorkspaceRecoveryCoordinator.cs");
        Assert.DoesNotContain("ISessionHostClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IGovernedAgentRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPolicy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISecretVault", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDefinitionCatalog", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_owner_declares_an_explicit_lifetime()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            typeof(RuntimeWorkspaceRecoveryCoordinator)));
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Combine(
        ApplicationViewCatalog.Load().RepositoryRoot,
        "src",
        "Asura.App",
        "ViewModels",
        fileName));
}
