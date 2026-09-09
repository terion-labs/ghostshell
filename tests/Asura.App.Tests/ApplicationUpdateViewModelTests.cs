using Asura.App;
using Asura.App.ViewModels;
using Asura.Application.ApplicationUpdates;

namespace Asura.App.Tests;

public sealed class ApplicationUpdateViewModelTests
{
    [Fact]
    public void Direct_update_moves_from_available_to_ready()
    {
        var service = new FakeApplicationUpdates();
        using var viewModel = new ApplicationUpdateViewModel(
            service,
            new ImmediateDispatcher());

        service.Set(ApplicationUpdateStage.Available, "1.4.0");

        Assert.True(viewModel.CanDownload);
        Assert.Contains("1.4.0", viewModel.Status, StringComparison.Ordinal);

        service.Set(
            ApplicationUpdateStage.ReadyToRestart,
            "1.4.0",
            downloadProgress: 100);

        Assert.True(viewModel.CanRestartToApply);
        Assert.False(viewModel.CanDownload);
        Assert.Contains("Restart", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_distribution_has_no_in_app_actions()
    {
        var distribution = new DistributionIdentity(
            DistributionSource.AppleAppStore,
            ApplicationUpdateStrategy.PlatformManaged,
            "osx-arm64-stable");
        using var viewModel = new ApplicationUpdateViewModel(
            new PassiveApplicationUpdateService(distribution),
            new ImmediateDispatcher());

        Assert.Equal("Apple App Store", viewModel.Channel);
        Assert.False(viewModel.CanCheck);
        Assert.False(viewModel.CanDownload);
        Assert.False(viewModel.CanRestartToApply);
        Assert.Contains("install source", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void System_wide_install_does_not_offer_privileged_apply()
    {
        var service = new FakeApplicationUpdates();
        using var viewModel = new ApplicationUpdateViewModel(
            service,
            new ImmediateDispatcher());

        service.Set(
            ApplicationUpdateStage.ReadyToRestart,
            "1.4.0",
            downloadProgress: 100,
            applyAllowed: false);

        Assert.False(viewModel.CanRestartToApply);
        Assert.Contains("signed installer", viewModel.Status, StringComparison.Ordinal);
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApplicationUpdates : IApplicationUpdateService
    {
        private static readonly DistributionIdentity DirectDistribution = new(
            DistributionSource.GitHubRelease,
            ApplicationUpdateStrategy.Velopack,
            "osx-arm64-stable");

        public event EventHandler<ApplicationUpdateSnapshot>? Changed;

        public ApplicationUpdateSnapshot Snapshot { get; private set; } = new(
            DirectDistribution,
            ApplicationUpdateStage.Idle);

        public Task CheckAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DownloadAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void RestartToApply()
        {
        }

        public void Set(
            ApplicationUpdateStage stage,
            string version,
            int? downloadProgress = null,
            bool applyAllowed = true)
        {
            Snapshot = new(
                DirectDistribution,
                stage,
                version,
                downloadProgress,
                ApplyAllowed: applyAllowed);
            Changed?.Invoke(this, Snapshot);
        }
    }
}
