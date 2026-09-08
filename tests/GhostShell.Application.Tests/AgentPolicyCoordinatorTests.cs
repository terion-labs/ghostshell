using System.Collections.Immutable;
using GhostShell.Core;

namespace GhostShell.Application.Tests;

public sealed class AgentPolicyCoordinatorTests
{
    [Fact]
    public async Task SavedDefaultBecomesTheActiveGlobalPolicy()
    {
        var store = new MemoryStore();
        var coordinator = new AgentPolicyCoordinator(store);
        var changed = 0;
        coordinator.Changed += (_, _) => changed++;
        var policy = new AgentPolicy(
            "provider-openai",
            "gpt-5.6-terra",
            AgentPolicy.Capabilities.ToImmutableDictionary(
                capability => capability,
                _ => AgentPermission.Ask))
        {
            CompactionModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-terra"),
            TitleModel = new AgentModelSelection(
                "provider-openai",
                "gpt-5.6-terra"),
            SystemPrompt = "  Follow the repository conventions.  ",
        };

        var result = await coordinator.SaveAsync(policy, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(policy.Provider, store.Policy?.Provider);
        Assert.Equal(policy.Model, store.Policy?.Model);
        Assert.Equal(policy.Provider, coordinator.Policy?.Provider);
        Assert.Equal(policy.Model, coordinator.Policy?.Model);
        Assert.Equal(policy.CompactionModel, store.Policy?.CompactionModel);
        Assert.Equal(policy.TitleModel, store.Policy?.TitleModel);
        Assert.Equal(policy.CompactionModel, coordinator.Policy?.CompactionModel);
        Assert.Equal(policy.TitleModel, coordinator.Policy?.TitleModel);
        Assert.Equal("Follow the repository conventions.", store.Policy?.SystemPrompt);
        Assert.Equal("Follow the repository conventions.", coordinator.Policy?.SystemPrompt);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task InitializeLoadsTheDurableDefaultWithoutPublishingAChange()
    {
        var store = new MemoryStore { Policy = AgentPolicy.Default };
        var coordinator = new AgentPolicyCoordinator(store);
        var changed = 0;
        coordinator.Changed += (_, _) => changed++;

        await coordinator.InitializeAsync(CancellationToken.None);

        Assert.Equal(AgentPolicy.Default.Provider, coordinator.Policy?.Provider);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task UnreadablePolicyDisablesCapabilitiesWithoutOverwritingUntilExplicitSave()
    {
        var store = new MemoryStore { ReadError = new(ApplicationRunErrorCode.StorageFailure, "Unreadable policy.") };
        var coordinator = new AgentPolicyCoordinator(store);

        await coordinator.InitializeAsync(CancellationToken.None);

        Assert.NotNull(coordinator.InitializationError);
        Assert.NotNull(coordinator.Policy);
        Assert.All(coordinator.Policy.Permissions.Values, permission => Assert.Equal(AgentPermission.Off, permission));
        Assert.Equal(0, store.WriteCount);

        Assert.True((await coordinator.SaveAsync(AgentPolicy.Default, CancellationToken.None)).IsSuccess);
        Assert.Null(coordinator.InitializationError);
        Assert.Equal(1, store.WriteCount);
    }

    private sealed class MemoryStore : IAgentPolicyPreferenceStore
    {
        public AgentPolicy? Policy { get; set; }

        public ApplicationRunError? ReadError { get; init; }

        public int WriteCount { get; private set; }

        public ValueTask<ApplicationRunResult<AgentPolicy?>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadError is { } error
                ? ApplicationRunResult<AgentPolicy?>.Failure(error)
                : ApplicationRunResult<AgentPolicy?>.Success(Policy));
        }

        public ValueTask<ApplicationRunResult<Unit>> WriteAsync(
            AgentPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            Policy = policy;
            return ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
        }
    }
}
