using System.Collections.Immutable;

namespace Asura.Core.Tests;

public sealed class AgentPolicyResolverTests
{
    [Fact]
    public void Selecting_primary_model_never_changes_maintenance_routes()
    {
        var policy = AgentPolicy.Default with
        {
            CompactionModel = new AgentModelSelection("summary", "compact"),
            TitleModel = new AgentModelSelection("summary", "title"),
        };

        var selected = policy.SelectPrimaryModel("openai-profile", "gpt-5.6-sol");

        var expected = new AgentModelSelection("openai-profile", "gpt-5.6-sol");
        Assert.Equal(expected.Provider, selected.Provider);
        Assert.Equal(expected.Model, selected.Model);
        Assert.Equal(policy.CompactionModel, selected.CompactionModel);
        Assert.Equal(policy.TitleModel, selected.TitleModel);
    }

    [Fact]
    public void DefaultPolicyDefinesEveryExecutionCapability()
    {
        Assert.True(AgentPolicy.Default.IsStructurallyValid());
        Assert.True(AgentPolicy.Default.IsValidForDurableStorage());
        Assert.Equal(
            AgentPolicy.Capabilities.Order(),
            AgentPolicy.Default.Permissions.Keys.Order());
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.SecretUse));
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.BrowserInteraction));
        Assert.Equal(
            AgentPermission.Ask,
            AgentPolicy.Default.GetPermission(AgentCapability.WorkspaceLayout));
        Assert.Equal(
            AgentPermission.Off,
            AgentPolicy.Default.GetPermission(AgentCapability.GitData));
        Assert.All(
            PanelToolsetCapabilities,
            capability => Assert.Equal(
                AgentPermission.Off,
                AgentPolicy.Default.GetPermission(capability)));
    }

    [Theory]
    [InlineData(AgentPermission.Off)]
    [InlineData(AgentPermission.Ask)]
    [InlineData(AgentPermission.Auto)]
    public void DurablePoliciesAcceptOrdinaryPermissionModes(AgentPermission permission)
    {
        var policy = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                permission),
        };

        Assert.True(policy.IsValidForDurableStorage());
    }

    [Fact]
    public void DurablePoliciesRejectYoloForEveryCapability()
    {
        foreach (var capability in AgentPolicy.Capabilities)
        {
            var policy = AgentPolicy.Default with
            {
                Permissions = AgentPolicy.Default.Permissions.SetItem(
                    capability,
                    AgentPermission.Yolo),
            };

            Assert.False(
                policy.IsValidForDurableStorage(),
                $"Capability '{capability}' accepted a durable YOLO permission.");
        }
    }

    [Fact]
    public void PolicyProviderAndModelIdentifiersAreBoundedAndControlFree()
    {
        var valid = AgentPolicy.Default with
        {
            Provider = new string('p', AgentPolicy.MaximumProviderLength),
            Model = new string('m', AgentPolicy.MaximumModelLength),
        };
        var invalid = new[]
        {
            valid with { Provider = string.Empty },
            valid with
            {
                Provider = new string('p', AgentPolicy.MaximumProviderLength + 1),
            },
            valid with { Provider = "provider\ninjected" },
            valid with { Model = " " },
            valid with
            {
                Model = new string('m', AgentPolicy.MaximumModelLength + 1),
            },
            valid with { Model = "model\0injected" },
        };

        Assert.True(valid.IsStructurallyValid());
        Assert.True(valid.IsValidForDurableStorage());
        Assert.All(
            invalid,
            policy =>
            {
                Assert.False(policy.IsStructurallyValid());
                Assert.False(policy.IsValidForDurableStorage());
            });
    }

    [Fact]
    public void IncompleteCapabilityMapsAreRejected()
    {
        var incomplete = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.Remove(
                AgentCapability.BrowserInteraction),
        };

        Assert.False(incomplete.IsStructurallyValid());
        Assert.False(incomplete.IsValidForDurableStorage());
        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.Resolve(incomplete));
    }

    [Fact]
    public void MostSpecificPolicyOwnsEveryExplicitRouteAndPermission()
    {
        var global = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.McpTools,
                AgentPermission.Ask),
        };
        var workspace = FullPolicy(
            "workspace-provider",
            "workspace-model",
            AgentPermission.Auto) with
        {
            CompactionModel = new AgentModelSelection("compact", "workspace-compact"),
            TitleModel = new AgentModelSelection("title", "workspace-title"),
        };

        var resolved = AgentPolicyResolver.Resolve(global, workspace);

        Assert.Equal(
            AgentPermission.Auto,
            resolved.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Auto,
            resolved.GetPermission(AgentCapability.McpTools));
        Assert.Equal(workspace.Provider, resolved.Provider);
        Assert.Equal(workspace.Model, resolved.Model);
        Assert.Equal(workspace.CompactionModel, resolved.CompactionModel);
        Assert.Equal(workspace.TitleModel, resolved.TitleModel);
    }

    [Fact]
    public void ModelRoutesNeverComeFromAnotherPolicyLayer()
    {
        var global = AgentPolicy.Default with
        {
            Provider = "global-provider",
            Model = "global-model",
            TitleModel = new AgentModelSelection("title-provider", "title-model"),
        };
        var workspace = AgentPolicy.Default with
        {
            Provider = "workspace-provider",
            Model = "workspace-model",
            CompactionModel = new AgentModelSelection(
                "compact-provider",
                "compact-model"),
            TitleModel = new AgentModelSelection(
                "workspace-title-provider",
                "workspace-title-model"),
        };

        var resolved = AgentPolicyResolver.Resolve(global, workspace);

        Assert.Equal(
            new AgentModelSelection("compact-provider", "compact-model"),
            resolved.CompactionModel);
        Assert.Equal(
            new AgentModelSelection(
                "workspace-title-provider",
                "workspace-title-model"),
            resolved.TitleModel);
    }

    [Fact]
    public void SystemPromptUsesMostSpecificConfiguredLayer()
    {
        var global = AgentPolicy.Default with
        {
            SystemPrompt = "Prefer concise answers.",
        };
        var workspace = AgentPolicy.Default with
        {
            Provider = "workspace-provider",
            Model = "workspace-model",
            SystemPrompt = "Follow this workspace's conventions.",
        };
        var inherited = workspace with { SystemPrompt = null };

        Assert.Equal(
            "Follow this workspace's conventions.",
            AgentPolicyResolver.Resolve(global, workspace).SystemPrompt);
        Assert.Equal(
            "Prefer concise answers.",
            AgentPolicyResolver.Resolve(global, inherited).SystemPrompt);
    }

    [Fact]
    public void LeastPrivilegeAggregationUsesTheMostRestrictivePermissionPerCapability()
    {
        var allAuto = FullPolicy("provider", "model", AgentPermission.Auto);
        var first = allAuto with
        {
            Permissions = allAuto.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Off)
                .SetItem(AgentCapability.EditFiles, AgentPermission.Auto)
                .SetItem(AgentCapability.Search, AgentPermission.Ask),
        };
        var second = allAuto with
        {
            Permissions = allAuto.Permissions
                .SetItem(AgentCapability.RunCommands, AgentPermission.Ask)
                .SetItem(AgentCapability.EditFiles, AgentPermission.Ask)
                .SetItem(AgentCapability.Search, AgentPermission.Auto),
        };

        var resolved = AgentPolicyResolver.ResolveLeastPrivilege([first, second]);

        Assert.Equal("provider", resolved.Provider);
        Assert.Equal("model", resolved.Model);
        Assert.Equal(
            AgentPermission.Off,
            resolved.GetPermission(AgentCapability.RunCommands));
        Assert.Equal(
            AgentPermission.Ask,
            resolved.GetPermission(AgentCapability.EditFiles));
        Assert.Equal(
            AgentPermission.Ask,
            resolved.GetPermission(AgentCapability.Search));
        Assert.Equal(
            AgentPermission.Auto,
            resolved.GetPermission(AgentCapability.TerminalRead));
    }

    [Theory]
    [InlineData("other-provider", "model")]
    [InlineData("provider", "other-model")]
    public void LeastPrivilegeAggregationRejectsProviderOrModelMismatch(
        string provider,
        string model)
    {
        var first = FullPolicy("provider", "model", AgentPermission.Ask);
        var second = FullPolicy(provider, model, AgentPermission.Ask);

        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.ResolveLeastPrivilege([first, second]));
    }

    [Fact]
    public void LeastPrivilegeAggregationRejectsEmptyScope()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.ResolveLeastPrivilege([]));
    }

    [Fact]
    public void LeastPrivilegeAggregationRejectsNonDurablePolicy()
    {
        var yolo = AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.RunCommands,
                AgentPermission.Yolo),
        };

        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.ResolveLeastPrivilege([yolo]));
    }

    [Theory]
    [InlineData(AgentPermission.Off, AgentActionRisk.Observation, AgentPolicyDecision.Denied)]
    [InlineData(AgentPermission.Ask, AgentActionRisk.Observation, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Observation, AgentPolicyDecision.AuthorizedByAuto)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Routine, AgentPolicyDecision.AuthorizedByAuto)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Mutation, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Destructive, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Auto, AgentActionRisk.Privileged, AgentPolicyDecision.RequiresApproval)]
    [InlineData(AgentPermission.Yolo, AgentActionRisk.Privileged, AgentPolicyDecision.AuthorizedByYolo)]
    public void RiskClassificationCannotBeSuppliedByTheModelToBypassPolicy(
        AgentPermission permission,
        AgentActionRisk risk,
        AgentPolicyDecision expected)
    {
        Assert.Equal(expected, AgentPolicyResolver.Evaluate(permission, risk));
    }

    [Fact]
    public void MalformedLayerIsRejectedInsteadOfPartiallyApplied()
    {
        var malformed = AgentPolicy.Default with
        {
            Permissions = [],
        };

        Assert.Throws<ArgumentException>(() =>
            AgentPolicyResolver.Resolve(AgentPolicy.Default, malformed));
    }

    private static AgentPolicy FullPolicy(
        string provider,
        string model,
        AgentPermission permission) => new(
        provider,
        model,
        AgentPolicy.Capabilities.ToImmutableDictionary(
            capability => capability,
            _ => permission))
        {
            CompactionModel = new AgentModelSelection(provider, model),
            TitleModel = new AgentModelSelection(provider, model),
        };

    private static AgentCapability[] PanelToolsetCapabilities =>
    [
        AgentCapability.BrowserScripting,
        AgentCapability.BrowserDiagnostics,
        AgentCapability.DatabaseRead,
        AgentCapability.DatabaseWrite,
        AgentCapability.DockerData,
        AgentCapability.SystemData,
        AgentCapability.ProcessData,
        AgentCapability.ArtifactTransfer,
    ];

}
