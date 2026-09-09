using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SecondaryLaunchActivatesThePrimaryInstance()
    {
        await using var profile = TemporaryProfile.Create();
        await using var primary = await StartPrimaryAsync(profile.DirectoryPath);
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.RegisterActivationHandler(activated.SetResult);

        var secondary = await SingleInstanceCoordinator.StartAsync(
            profile.DirectoryPath,
            TestTimeout,
            CancellationToken.None);

        Assert.IsType<SingleInstanceStartResult.ExistingInstanceActivated>(secondary);
        await activated.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ActivationIsQueuedUntilThePrimaryRegistersItsHandler()
    {
        await using var profile = TemporaryProfile.Create();
        await using var primary = await StartPrimaryAsync(profile.DirectoryPath);

        var secondaryTask = SingleInstanceCoordinator.StartAsync(
                profile.DirectoryPath,
                TestTimeout,
                CancellationToken.None)
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(secondaryTask.IsCompleted);

        var activationCount = 0;
        primary.RegisterActivationHandler(() => Interlocked.Increment(ref activationCount));

        var secondary = await secondaryTask.WaitAsync(TestTimeout);
        Assert.IsType<SingleInstanceStartResult.ExistingInstanceActivated>(secondary);
        Assert.Equal(1, Volatile.Read(ref activationCount));
    }

    [Fact]
    public async Task PublicStartupWindowAllowsHandlerRegistrationAfterTwoSeconds()
    {
        await using var profile = TemporaryProfile.Create();
        var primaryStart = await SingleInstanceCoordinator.StartAsync(
            profile.DirectoryPath,
            CancellationToken.None);
        await using var primary =
            Assert.IsType<SingleInstanceStartResult.Primary>(primaryStart).Coordinator;
        var secondaryTask = SingleInstanceCoordinator.StartAsync(
                profile.DirectoryPath,
                CancellationToken.None)
            .AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(2100));
        var activated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.RegisterActivationHandler(activated.SetResult);

        var secondary = await secondaryTask.WaitAsync(TestTimeout);
        Assert.IsType<SingleInstanceStartResult.ExistingInstanceActivated>(secondary);
        await activated.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task ThirdLaunchCanActivateTheSamePrimaryAfterTheSecondLaunchExits()
    {
        await using var profile = TemporaryProfile.Create();
        await using var primary = await StartPrimaryAsync(profile.DirectoryPath);
        var activationCount = 0;
        var twiceActivated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.RegisterActivationHandler(() =>
        {
            if (Interlocked.Increment(ref activationCount) == 2)
            {
                twiceActivated.SetResult();
            }
        });

        var second = await SingleInstanceCoordinator.StartAsync(
            profile.DirectoryPath,
            TestTimeout,
            CancellationToken.None);
        var third = await SingleInstanceCoordinator.StartAsync(
            profile.DirectoryPath,
            TestTimeout,
            CancellationToken.None);

        Assert.IsType<SingleInstanceStartResult.ExistingInstanceActivated>(second);
        Assert.IsType<SingleInstanceStartResult.ExistingInstanceActivated>(third);
        await twiceActivated.Task.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref activationCount));
    }

    [Fact]
    public async Task StaleLockFileIsRecoverableButHeldLockWithoutServerFailsSafely()
    {
        await using var profile = TemporaryProfile.Create();
        var lockPath = Path.Combine(
            profile.DirectoryPath,
            SingleInstanceCoordinator.LockFileName);
        await File.WriteAllTextAsync(lockPath, string.Empty);

        await using (var recovered = await StartPrimaryAsync(profile.DirectoryPath))
        {
        }

        await using var abandonedOwner = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await SingleInstanceCoordinator.StartAsync(
            profile.DirectoryPath,
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        var failure = Assert.IsType<SingleInstanceStartResult.Failure>(result);
        Assert.Equal(SingleInstanceErrorCode.ActivationUnavailable, failure.Error.Code);
        Assert.Equal("single_instance_activation_unavailable", failure.Error.StableCode);
        Assert.DoesNotContain(profile.DirectoryPath, failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposalIsIdempotentAndReleasesTheProfileForANewPrimary()
    {
        await using var profile = TemporaryProfile.Create();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var primary = await StartPrimaryAsync(profile.DirectoryPath);
            await Task.Yield();

            await primary.DisposeAsync();
            await primary.DisposeAsync();
        }

        await using var replacement = await StartPrimaryAsync(profile.DirectoryPath);
    }

    [Fact]
    public async Task ThrowingActivationHandlerDoesNotReportFalseSuccess()
    {
        await using var profile = TemporaryProfile.Create();
        await using var primary = await StartPrimaryAsync(profile.DirectoryPath);
        primary.RegisterActivationHandler(() =>
            throw new InvalidOperationException("Simulated UI dispatch failure."));

        var result = await SingleInstanceCoordinator.StartAsync(
            profile.DirectoryPath,
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        var failure = Assert.IsType<SingleInstanceStartResult.Failure>(result);
        Assert.Equal(SingleInstanceErrorCode.ActivationUnavailable, failure.Error.Code);
    }

    [Fact]
    public async Task QueuedActivationWaitsForAHandlerAndDoesNotAcknowledgeItsFailure()
    {
        await using var profile = TemporaryProfile.Create();
        await using var primary = await StartPrimaryAsync(profile.DirectoryPath);
        var secondaryTask = SingleInstanceCoordinator.StartAsync(
                profile.DirectoryPath,
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None)
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(secondaryTask.IsCompleted);

        primary.RegisterActivationHandler(() =>
            throw new InvalidOperationException("Simulated queued dispatch failure."));

        var result = await secondaryTask.WaitAsync(TestTimeout);
        var failure = Assert.IsType<SingleInstanceStartResult.Failure>(result);
        Assert.Equal(SingleInstanceErrorCode.ActivationUnavailable, failure.Error.Code);
    }

    [Fact]
    public async Task LaunchDuringPrimaryShutdownBecomesTheNextPrimaryInsteadOfReportingActivation()
    {
        await using var profile = TemporaryProfile.Create();
        var primary = await StartPrimaryAsync(profile.DirectoryPath);
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHandler = new ManualResetEventSlim();
        primary.RegisterActivationHandler(() =>
        {
            handlerEntered.SetResult();
            releaseHandler.Wait(TestTimeout);
        });

        var contenderTask = SingleInstanceCoordinator.StartAsync(
                profile.DirectoryPath,
                TestTimeout,
                CancellationToken.None)
            .AsTask();
        await handlerEntered.Task.WaitAsync(TestTimeout);
        var disposalTask = primary.DisposeAsync().AsTask();
        releaseHandler.Set();

        var contender = await contenderTask.WaitAsync(TestTimeout);
        await disposalTask.WaitAsync(TestTimeout);

        var replacement = Assert.IsType<SingleInstanceStartResult.Primary>(contender);
        await replacement.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task StoppedPrimaryRejectsActivationUntilItsProfileLockIsReleased()
    {
        await using var profile = TemporaryProfile.Create();
        var primary = await StartPrimaryAsync(profile.DirectoryPath);
        var activationCount = 0;
        primary.RegisterActivationHandler(() => Interlocked.Increment(ref activationCount));
        primary.StopAcceptingActivations();

        var contenderTask = SingleInstanceCoordinator.StartAsync(
                profile.DirectoryPath,
                TestTimeout,
                CancellationToken.None)
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(contenderTask.IsCompleted);

        await primary.DisposeAsync();
        var contender = await contenderTask.WaitAsync(TestTimeout);

        Assert.Equal(0, Volatile.Read(ref activationCount));
        var replacement = Assert.IsType<SingleInstanceStartResult.Primary>(contender);
        await replacement.Coordinator.DisposeAsync();
    }

    [Fact]
    public async Task StopWaitsForAnInFlightActivationHandler()
    {
        await using var profile = TemporaryProfile.Create();
        await using var primary = await StartPrimaryAsync(profile.DirectoryPath);
        var handlerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHandler = new ManualResetEventSlim();
        primary.RegisterActivationHandler(() =>
        {
            handlerEntered.SetResult();
            releaseHandler.Wait(TestTimeout);
        });
        var secondaryTask = SingleInstanceCoordinator.StartAsync(
                profile.DirectoryPath,
                TestTimeout,
                CancellationToken.None)
            .AsTask();
        await handlerEntered.Task.WaitAsync(TestTimeout);

        var stopTask = Task.Run(primary.StopAcceptingActivations);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(stopTask.IsCompleted);

        releaseHandler.Set();
        await stopTask.WaitAsync(TestTimeout);
        _ = await secondaryTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task SymlinkAliasActivatesTheOwnerOfTheSameProfileDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var profile = TemporaryProfile.Create();
        var aliasPath = $"{profile.DirectoryPath}-alias";
        Directory.CreateSymbolicLink(aliasPath, profile.DirectoryPath);
        try
        {
            await using var primary = await StartPrimaryAsync(profile.DirectoryPath);
            var activated = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            primary.RegisterActivationHandler(activated.SetResult);

            var secondary = await SingleInstanceCoordinator.StartAsync(
                aliasPath,
                TestTimeout,
                CancellationToken.None);

            Assert.IsType<SingleInstanceStartResult.ExistingInstanceActivated>(secondary);
            await activated.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            Directory.Delete(aliasPath);
        }
    }

    private static async ValueTask<SingleInstanceCoordinator> StartPrimaryAsync(
        string profileDirectory)
    {
        var result = await SingleInstanceCoordinator.StartAsync(
            profileDirectory,
            TestTimeout,
            CancellationToken.None);
        return Assert.IsType<SingleInstanceStartResult.Primary>(result).Coordinator;
    }

    private sealed class TemporaryProfile : IAsyncDisposable
    {
        private TemporaryProfile(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static TemporaryProfile Create()
        {
            var directoryPath = Path.Combine(
                Path.GetTempPath(),
                "asura-single-instance-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TemporaryProfile(directoryPath);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
