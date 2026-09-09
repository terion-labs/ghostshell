using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class TerminalContinuitySettingsViewModelTests
{
    [Fact]
    public async Task Coordinator_free_surface_keeps_an_in_memory_preference()
    {
        using var viewModel = CreateViewModel();

        Assert.True(await viewModel.LoadAsync());
        Assert.True(await viewModel.SetUseForSshTerminalsAsync(true));

        Assert.True(viewModel.UseForSshTerminals);
        Assert.Equal(TerminalMultiplexingMode.Automatic, viewModel.Mode);
        Assert.True(viewModel.CanChange);
    }

    [Fact]
    public async Task Dispose_rejects_future_preference_work()
    {
        var viewModel = CreateViewModel();
        viewModel.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await viewModel.LoadAsync());
    }

    private static TerminalContinuitySettingsViewModel CreateViewModel() => new(
        null,
        _ => null,
        _ => { },
        new ImmediateDispatcher());

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
