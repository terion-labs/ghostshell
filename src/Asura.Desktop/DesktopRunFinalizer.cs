using Asura.Application;

namespace Asura.Desktop;

internal sealed class DesktopRunFinalizer
{
    private readonly IApplicationRunStore _runStore;
    private readonly RuntimeRecoveryWriter _runtimeRecoveryWriter;

    public DesktopRunFinalizer(
        RuntimeRecoveryWriter runtimeRecoveryWriter,
        IApplicationRunStore runStore)
    {
        _runtimeRecoveryWriter = runtimeRecoveryWriter
            ?? throw new ArgumentNullException(nameof(runtimeRecoveryWriter));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    }

    public async ValueTask<ApplicationRunResult<Unit>> FinalizeAsync(
        Func<CancellationToken, Task> quiescePresentation,
        Func<CancellationToken, Task<ApplicationRunResult<Unit>>> flushRecentSessionHistory,
        Func<CancellationToken, ValueTask> stopSessionHost,
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quiescePresentation);
        ArgumentNullException.ThrowIfNull(flushRecentSessionHistory);
        ArgumentNullException.ThrowIfNull(stopSessionHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        try
        {
            await quiescePresentation(cancellationToken).ConfigureAwait(false);
            var historyFlush = await flushRecentSessionHistory(cancellationToken)
                .ConfigureAwait(false);
            if (!historyFlush.IsSuccess)
            {
                return historyFlush;
            }

            await stopSessionHost(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                ApplicationRunErrorCode.Cancelled,
                "Application shutdown was cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(
                ApplicationRunErrorCode.StorageFailure,
                "Application runtime shutdown could not be completed.");
        }

        ApplicationRunResult<Unit> recoveryFlush;
        try
        {
            recoveryFlush = await _runtimeRecoveryWriter.SealAndFlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(
                ApplicationRunErrorCode.StorageFailure,
                "Runtime recovery shutdown could not be completed.");
        }

        if (!recoveryFlush.IsSuccess)
        {
            return recoveryFlush;
        }

        try
        {
            return await _runStore.CompleteRunAsync(runId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                ApplicationRunErrorCode.Cancelled,
                "Application shutdown finalization was cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(
                ApplicationRunErrorCode.StorageFailure,
                "The application run marker could not be finalized.");
        }
    }

    private static ApplicationRunResult<Unit> Failure(
        ApplicationRunErrorCode code,
        string message) =>
        ApplicationRunResult<Unit>.Failure(new ApplicationRunError(code, message));
}
