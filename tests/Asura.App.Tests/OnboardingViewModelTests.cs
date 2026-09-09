using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task IncompleteProfileShowsTruthfulChecksAndPersistsCompletion()
    {
        var store = new RecordingProgressStore(new OnboardingProgress(0, 1));
        var connection = LocalConnection();
        using var viewModel = new OnboardingViewModel(
            store,
            () => connection,
            new RecordingConnectionRuntime(ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    connection.Id,
                    ConnectionKind.Local,
                    ConnectionTestVerification.RuntimeAvailable,
                    endpointReached: false))),
            PersistentVault());

        viewModel.Start();
        await viewModel.Initialization;

        Assert.True(viewModel.IsVisible);
        Assert.Equal("Welcome to Asura", viewModel.Title);
        Assert.Equal(
            "Asura checked the basics you need to get started.",
            viewModel.Introduction);
        Assert.Equal("Ready", viewModel.LocalTerminalState);
        Assert.Equal("Your default shell is available.", viewModel.LocalTerminalDetail);
        Assert.Equal("Ready", viewModel.CredentialVaultState);
        Assert.Equal(
            "Passwords can be stored securely by your operating system.",
            viewModel.CredentialVaultDetail);
        Assert.Equal(
            "No terminal was opened and no passwords were accessed during these checks.",
            viewModel.StatusMessage);
        Assert.True(viewModel.CanFinish);

        await viewModel.CompleteAsync(CancellationToken.None);

        Assert.False(viewModel.IsVisible);
        Assert.Equal(1, store.Progress.CompletedVersion);
        Assert.Equal(2, store.Progress.Revision);
        Assert.Equal(1, store.CompleteCalls);
    }

    [Fact]
    public async Task CompletedProfileStaysHiddenButCanBeReviewedWithoutResettingProgress()
    {
        var store = new RecordingProgressStore(new OnboardingProgress(1, 4));
        var connection = LocalConnection();
        using var viewModel = new OnboardingViewModel(
            store,
            () => connection,
            ReadyRuntime(connection),
            PersistentVault());

        viewModel.Start();
        await viewModel.Initialization;

        Assert.False(viewModel.IsVisible);
        viewModel.ShowReview();
        Assert.True(viewModel.IsVisible);
        Assert.Equal("Check your setup", viewModel.Title);
        Assert.Equal(
            "Make sure your terminal and secure password storage are ready to use.",
            viewModel.Introduction);

        await viewModel.CompleteAsync(CancellationToken.None);

        Assert.False(viewModel.IsVisible);
        Assert.Equal(1, store.Progress.CompletedVersion);
        Assert.Equal(4, store.Progress.Revision);
    }

    [Fact]
    public async Task MissingShellAndUnavailableVaultAreNeverPresentedAsReady()
    {
        using var viewModel = new OnboardingViewModel(
            new RecordingProgressStore(new OnboardingProgress(0, 1)),
            () => null,
            new RecordingConnectionRuntime(
                ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                    ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.RuntimeMissing))),
            new SecretVaultAvailability(
                SecretVaultAvailabilityState.Unavailable,
                SecretVaultPersistenceKind.None,
                SecretVaultCapabilities.None,
                "unavailable",
                "unavailable",
                "Unavailable"));

        viewModel.Start();
        await viewModel.Initialization;

        Assert.True(viewModel.IsVisible);
        Assert.Equal("Needs attention", viewModel.LocalTerminalState);
        Assert.Equal("Unavailable", viewModel.CredentialVaultState);
        Assert.True(viewModel.HasFailure);
        Assert.True(viewModel.CanRetry);
    }

    [Fact]
    public async Task ProgressFailureKeepsSetupVisibleAndRetryable()
    {
        var store = new RecordingProgressStore(new OnboardingProgress(0, 1))
        {
            ReadError = new OnboardingProgressError(
                OnboardingProgressErrorCode.StorageUnavailable,
                "Unavailable"),
        };
        var connection = LocalConnection();
        using var viewModel = new OnboardingViewModel(
            store,
            () => connection,
            ReadyRuntime(connection),
            PersistentVault());

        viewModel.Start();
        await viewModel.Initialization;

        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.HasFailure);
        Assert.False(viewModel.CanFinish);
        Assert.Equal("We couldn't load your setup status. Try again.", viewModel.StatusMessage);

        store.ReadError = null;
        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.True(viewModel.CanFinish);
        Assert.False(viewModel.HasFailure);
    }

    private static RecordingConnectionRuntime ReadyRuntime(ConnectionProfile connection) =>
        new(ConnectionRuntimeResult<ConnectionTestReport>.Succeed(new ConnectionTestReport(
            connection.Id,
            ConnectionKind.Local,
            ConnectionTestVerification.RuntimeAvailable,
            endpointReached: false)));

    private static SecretVaultAvailability PersistentVault() => new(
        SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.OsProtectedPersistent,
        SecretVaultCapabilities.All,
        "test-vault",
        "ready",
        "Ready");

    private static ConnectionProfile LocalConnection() => new(
        new ConnectionId("builtin.local"),
        ConnectionProfile.CurrentSchemaVersion,
        "Local terminal",
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);

    private sealed class RecordingProgressStore(OnboardingProgress progress)
        : IOnboardingProgressStore
    {
        public OnboardingProgress Progress { get; private set; } = progress;

        public OnboardingProgressError? ReadError { get; set; }

        public int CompleteCalls { get; private set; }

        public ValueTask<OnboardingProgressResult<OnboardingProgress>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadError is null
                ? OnboardingProgressResult<OnboardingProgress>.Success(Progress)
                : OnboardingProgressResult<OnboardingProgress>.Failure(ReadError));
        }

        public ValueTask<OnboardingProgressResult<OnboardingProgress>> CompleteAsync(
            int version,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCalls++;
            if (Progress.CompletedVersion >= version)
            {
                return ValueTask.FromResult(
                    OnboardingProgressResult<OnboardingProgress>.Success(Progress));
            }

            if (Progress.Revision != expectedRevision)
            {
                return ValueTask.FromResult(
                    OnboardingProgressResult<OnboardingProgress>.Failure(
                        new OnboardingProgressError(
                            OnboardingProgressErrorCode.Conflict,
                            "Conflict")));
            }

            Progress = new OnboardingProgress(version, Progress.Revision + 1);
            return ValueTask.FromResult(
                OnboardingProgressResult<OnboardingProgress>.Success(Progress));
        }
    }

    private sealed class RecordingConnectionRuntime(
        ConnectionRuntimeResult<ConnectionTestReport> testResult)
        : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(testResult);
        }
    }
}
