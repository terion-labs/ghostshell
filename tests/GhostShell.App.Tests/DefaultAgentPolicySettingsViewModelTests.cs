using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class DefaultAgentPolicySettingsViewModelTests
{
    [Fact]
    public async Task Unreadable_policy_is_visible_and_never_replaced_by_background_settings_refresh()
    {
        var store = new UnreadableStore();
        var coordinator = new AgentPolicyCoordinator(store);
        await coordinator.InitializeAsync(CancellationToken.None);
        var errors = new List<string>();
        using var viewModel = new DefaultAgentPolicySettingsViewModel(
            coordinator, null, new ImmediateDispatcher(), errors.Add, () => errors.Clear());

        viewModel.RefreshProviders(null);
        viewModel.QueuePersistence(onlyWhenMissing: true);
        viewModel.QueuePersistence(onlyWhenMissing: false);
        await viewModel.QuiesceAsync();

        Assert.Contains("disabled until you review", Assert.Single(errors), StringComparison.Ordinal);
        Assert.Equal(0, store.WriteCount);
        Assert.All(coordinator.Policy!.Permissions.Values, permission => Assert.Equal(AgentPermission.Off, permission));
    }

    [Fact]
    public async Task Missing_policy_store_is_presented_by_the_settings_owner()
    {
        var errors = new List<string>();
        using var viewModel = new DefaultAgentPolicySettingsViewModel(
            null,
            null,
            new ImmediateDispatcher(),
            errors.Add,
            () => { });

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.False(viewModel.CanSave);
        Assert.Equal(
            "Default AI configuration storage is unavailable.",
            errors.Single());
    }

    [Fact]
    public void Dispose_rejects_provider_refresh()
    {
        var viewModel = new DefaultAgentPolicySettingsViewModel(
            null,
            null,
            new ImmediateDispatcher(),
            _ => { },
            () => { });
        viewModel.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            viewModel.RefreshProviders([]));
    }

    private sealed class UnreadableStore : IAgentPolicyPreferenceStore
    {
        public int WriteCount { get; private set; }

        public ValueTask<ApplicationRunResult<AgentPolicy?>> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApplicationRunResult<AgentPolicy?>.Failure(
                new(ApplicationRunErrorCode.StorageFailure, "Unreadable policy.")));

        public ValueTask<ApplicationRunResult<Unit>> WriteAsync(AgentPolicy policy, CancellationToken cancellationToken)
        {
            WriteCount++;
            return ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
        }
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(
            Action action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
